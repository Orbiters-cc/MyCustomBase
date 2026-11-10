#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCBEditorUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Meshes;

// Where payload assets are cached, how a cached one is recognised, and their cleanup.
public static partial class NativeMeshPayloadService
{
    /// <summary>
    /// A patch's payload identity, codec and cache asset path, after the checks every entry point makes before it reads
    /// a payload.
    /// </summary>
    private sealed class PayloadRequest
    {
        public string payloadHash;
        public string payloadCompression;
        public string assetPath;

        /// <param name="binInMemory">The .bin arrives in memory: no file to check.</param>
        /// <param name="keyInMemory">The original model arrives in memory: no key file to check.</param>
        public static PayloadRequest Resolve(CustomBaseVersion version, ModelFileData patchFile, string binPath, string originalFbxPath,
            bool binInMemory = false, bool keyInMemory = false)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            if (patchFile == null)
            {
                throw new ArgumentNullException(nameof(patchFile));
            }

            if (!IsAdvancedMeshPatchTransform(patchFile.transform))
            {
                throw new InvalidDataException($"Native mesh payload has unsupported transform '{patchFile.transform}'.");
            }

            if (string.IsNullOrWhiteSpace(patchFile.outputHash))
            {
                throw new InvalidDataException("Native mesh payload metadata is missing outputHash.");
            }

            if (!binInMemory && (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath)))
            {
                throw new FileNotFoundException("Native mesh .bin file not found.", binPath);
            }

            if (!keyInMemory) RequireOriginalKey(patchFile, originalFbxPath);
            return Of(version, patchFile);
        }

        /// <summary>Identity and cache path only, without checks (for cache lookups).</summary>
        public static PayloadRequest Of(CustomBaseVersion version, ModelFileData patchFile)
        {
            string payloadHash = GetPayloadIdentity(patchFile);
            return new PayloadRequest
            {
                payloadHash = payloadHash,
                payloadCompression = ResolvePayloadCompression(patchFile),
                assetPath = GetGeneratedPayloadPath(version, patchFile, payloadHash)
            };
        }

        /// <summary>The payload asset an earlier apply cached for this identity and codec, or null.</summary>
        public NativeMeshPayloadAsset FindCached()
        {
            var cached = LoadPayloadAsset(assetPath);
            return CachedPayloadMatches(cached, payloadHash, payloadCompression) ? cached : null;
        }
    }

    private static string GetGeneratedPayloadPath(CustomBaseVersion version, ModelFileData patchFile, string payloadHash)
    {
        if (payloadHash.StartsWith("raw:", StringComparison.Ordinal))
            return MCBUtils.CombineUnityPath(GeneratedFolder, version.assetId.ToString(),
                "shared-" + SanitizeFileName(Application.unityVersion), MCBUtils.PathHash(payloadHash.Substring(4)) + ".asset");
        string sourceName = "nativeMesh";
        if (patchFile?.metadata != null &&
            patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue) &&
            sourcePathValue != null)
        {
            sourceName = Path.GetFileNameWithoutExtension(sourcePathValue.ToString());
        }
        else if (!string.IsNullOrWhiteSpace(patchFile?.path))
        {
            sourceName = Path.GetFileNameWithoutExtension(patchFile.path);
        }

        string folder = MCBUtils.CombineUnityPath(
            GeneratedFolder,
            version.assetId.ToString(),
            SanitizeFileName(version.version));
        string file = $"{SanitizeFileName(sourceName)}_{MCBUtils.PathHash(payloadHash)}.asset";
        return MCBUtils.CombineUnityPath(folder, file);
    }

    private static string GetGeneratedPayloadVersionFolder(CustomBaseVersion version)
    {
        if (version == null || version.assetId <= 0 || string.IsNullOrWhiteSpace(version.version))
        {
            return null;
        }

        return MCBUtils.CombineUnityPath(
            GeneratedFolder,
            version.assetId.ToString(),
            SanitizeFileName(version.version));
    }

    private static GeneratedPayloadStorageInfo GetGeneratedPayloadStorageInfo(string unityFolder)
    {
        string normalizedFolder = NormalizeGeneratedPayloadFolder(unityFolder);
        if (string.IsNullOrWhiteSpace(normalizedFolder))
        {
            return new GeneratedPayloadStorageInfo(0L, 0, 0);
        }

        string fullPath = GetGeneratedPayloadFullPath(normalizedFolder);
        if (!Directory.Exists(fullPath))
        {
            return new GeneratedPayloadStorageInfo(0L, 0, 0);
        }

        long totalBytes = 0L;
        int assetCount = 0;
        int fileCount = 0;

        foreach (string file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fileInfo = new FileInfo(file);
                totalBytes += fileInfo.Length;
                fileCount++;
                if (string.Equals(fileInfo.Extension, ".asset", StringComparison.OrdinalIgnoreCase))
                {
                    assetCount++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new GeneratedPayloadStorageInfo(totalBytes, assetCount, fileCount);
    }

    private static void DeleteGeneratedPayloadFolder(string unityFolder)
    {
        string normalizedFolder = NormalizeGeneratedPayloadFolder(unityFolder);
        if (string.IsNullOrWhiteSpace(normalizedFolder))
        {
            return;
        }

        if (AssetDatabase.IsValidFolder(normalizedFolder))
        {
            if (!AssetDatabase.DeleteAsset(normalizedFolder))
            {
                throw new IOException($"Unity failed to delete generated advanced mesh folder: {normalizedFolder}");
            }

            return;
        }

        string fullPath = GetGeneratedPayloadFullPath(normalizedFolder);
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, true);
        }

        string metaPath = fullPath + ".meta";
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }

    private static void PruneEmptyGeneratedPayloadAssetFolder(CustomBaseVersion version)
    {
        if (version == null || version.assetId <= 0)
        {
            return;
        }

        string assetFolder = MCBUtils.CombineUnityPath(GeneratedFolder, version.assetId.ToString());
        string fullPath = GetGeneratedPayloadFullPath(assetFolder);
        if (Directory.Exists(fullPath) && !Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            DeleteGeneratedPayloadFolder(assetFolder);
        }
    }

    private static string NormalizeGeneratedPayloadFolder(string unityFolder)
    {
        string normalized = MCBUtils.ToUnityPath(unityFolder)?.TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (!IsGeneratedPayloadFolder(normalized))
        {
            throw new InvalidOperationException($"Refusing to delete non advanced mesh payload folder: {normalized}");
        }

        return normalized;
    }

    private static bool IsGeneratedPayloadFolder(string normalizedFolder)
    {
        return string.Equals(normalizedFolder, GeneratedFolder, StringComparison.OrdinalIgnoreCase) ||
               normalizedFolder.StartsWith(GeneratedFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetGeneratedPayloadFullPath(string normalizedFolder)
    {
        string fullPath = GetProjectRelativeFullPath(normalizedFolder);
        string generatedFullPath = GetProjectRelativeFullPath(GeneratedFolder);
        if (!IsSameOrChildPath(fullPath, generatedFullPath))
        {
            throw new InvalidOperationException($"Refusing to access generated advanced mesh path outside cache folder: {normalizedFolder}");
        }

        return fullPath;
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProjectRelativeFullPath(string unityPath)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string relativePath = MCBUtils.ToUnityPath(unityPath).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(projectRoot, relativePath));
    }

    private static void DeleteAssetFile(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        if (AssetDatabase.LoadMainAssetAtPath(normalizedPath) != null)
        {
            AssetDatabase.DeleteAsset(normalizedPath);
            return;
        }

        string fullPath = Path.GetFullPath(normalizedPath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        if (File.Exists(fullPath + ".meta")) File.Delete(fullPath + ".meta");
    }

    private static NativeMeshPayloadAsset LoadPayloadAsset(string assetPath)
    {
        string normalizedPath = MCBUtils.ToUnityPath(assetPath);
        return AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(normalizedPath);
    }

    private static bool CachedPayloadMatches(NativeMeshPayloadAsset payload, string payloadHash, string payloadCompression)
    {
        if (payload == null ||
            payload.payloadVersion != PayloadVersion ||
            !string.Equals(payload.payloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (payloadHash.StartsWith("raw:", StringComparison.Ordinal))
                return payload.renderers != null && payload.renderers.Count > 0 && payload.renderers.All(r => r?.mesh != null);
            return string.Equals(
                NormalizePayloadCompression(payload.payloadCompression),
                NormalizePayloadCompression(payloadCompression),
                StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void EnsureAssetFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(parent))
        {
            EnsureAssetFolder(parent);
        }

        string parentFolder = string.IsNullOrWhiteSpace(parent) ? "Assets" : parent;
        string folderName = Path.GetFileName(folderPath);
        if (!AssetDatabase.IsValidFolder(folderPath))
        {
            AssetDatabase.CreateFolder(parentFolder, folderName);
        }
    }
}
#endif
