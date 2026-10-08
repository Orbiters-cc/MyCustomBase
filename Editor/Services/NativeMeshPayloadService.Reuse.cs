#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEditor;

public static partial class NativeMeshPayloadService
{
    internal static string HashBytes(byte[] bytes)
    {
        using (var sha = MCBHashing.CreateSha256()) return BytesToHex(sha.ComputeHash(bytes));
    }

    internal static string GetPayloadIdentity(ModelFileData patch)
    {
        if (patch?.metadata != null && patch.metadata.TryGetValue("contentHash", out var value))
        {
            string hash = value?.ToString();
            if (hash == null || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid native mesh content identity.");
            return "raw:" + hash;
        }
        return patch?.outputHash;
    }

    internal static ModelFileData[] ExpandRendererParts(List<ModelFileData> files,
        IList<FileManagerService.ModelFilePackageEntry> entries)
    {
        var result = new List<ModelFileData>();
        foreach (var file in files)
        {
            var entry = entries.FirstOrDefault(e => Path.GetFileName(e.binUnityPath) == file.path);
            if (entry?.payloadParts == null) { result.Add(file); continue; }
            foreach (var part in entry.payloadParts)
            {
                var patch = JsonConvert.DeserializeObject<ModelFileData>(JsonConvert.SerializeObject(file));
                var primary = part.variants[0];
                patch.path = primary.path; patch.hash = primary.hash; patch.outputHash = primary.outputHash;
                patch.compression = primary.codec;
                patch.metadata["contentHash"] = part.contentHash;
                patch.metadata["deliveryVariants"] = part.variants;
                patch.metadata["advancedRendererCount"] = 1;
                patch.metadata[PayloadCompressionMetadataKey] = primary.codec;
                result.Add(patch);
            }
        }
        return result.ToArray();
    }

    internal static bool IsSharedMeshForVersion(string path, CustomBaseVersion version)
    {
        if (SharedPayloadKey(path) == null) return false;
        return version == null || (version.versionFiles ?? Array.Empty<ModelFileData>()).Any(p =>
            IsAdvancedMeshPatchTransform(p?.transform) && IsGeneratedPayloadPath(path, version, p));
    }

    // Shared payloads are cached per Unity version; a mesh applied under another editor version is still the same payload.
    internal static bool IsGeneratedPayloadPath(string meshPath, CustomBaseVersion version, ModelFileData patch)
    {
        if (string.IsNullOrEmpty(meshPath)) return false;
        string expected = GetGeneratedPayloadPath(version, patch, GetPayloadIdentity(patch));
        if (meshPath == expected) return true;
        string key = SharedPayloadKey(meshPath);
        return key != null && key == SharedPayloadKey(expected);
    }

    // "<generated>/<assetId>/shared-<unity version>/<content hash>.asset" without its Unity version, else null.
    static string SharedPayloadKey(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(GeneratedFolder + "/", StringComparison.Ordinal)) return null;
        string[] parts = path.Substring(GeneratedFolder.Length + 1).Split('/');
        if (parts.Length != 3 || !parts[1].StartsWith("shared-", StringComparison.Ordinal)) return null;
        return parts[0] + "/" + parts[2];
    }

    internal static CustomBaseVersion ResolveAppliedMeshVersionFromPaths(IEnumerable<string> meshPaths,
        IEnumerable<CustomBaseVersion> candidates, int assetId, string version, string defaultVersion)
    {
        var paths = meshPaths.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return null;
        var matches = (candidates ?? Enumerable.Empty<CustomBaseVersion>())
            .Where(v => v != null && !string.IsNullOrWhiteSpace(v.version))
            .GroupBy(v => new { v.assetId, v.version, v.defaultAviVersion, v.sourceVersionKey })
            .Select(g => g.First())
            .Where(v => paths.All(path => IsSharedMeshForVersion(path, v) ||
                (TryParseGeneratedMeshAssetPath(path, out int owner, out string revision) &&
                 owner == v.assetId && revision == v.version)))
            .ToArray();

        // Identical meshes can belong to different releases with different options.
        // Preserve the explicit installed identity; never choose the newest by mesh alone.
        var persisted = matches.FirstOrDefault(v => v.assetId == assetId && v.version == version &&
            (string.IsNullOrEmpty(defaultVersion) || v.defaultAviVersion == defaultVersion));
        return persisted ?? (matches.Length == 1 ? matches[0] : null);
    }

    /// <summary>
    /// Deletes the generated payloads and humanoid Avatars under <paramref name="folder"/> (those <paramref name="include"/>
    /// accepts) that nothing uses any more. Saved assets, unsaved/open avatars and the version switches Undo can still go
    /// back to all own references.
    /// </summary>
    static GeneratedPayloadStorageInfo DeleteUnreferencedGeneratedPayloads(string folder = GeneratedFolder, Func<string, bool> include = null)
    {
        if (!AssetDatabase.IsValidFolder(folder)) return new GeneratedPayloadStorageInfo(0, 0, 0);
        var loaded = CollectLoadedGeneratedAssets();
        var candidates = AssetDatabase.FindAssets("t:NativeMeshPayloadAsset", new[] { folder })
            .Concat(AssetDatabase.FindAssets("t:Avatar", new[] { folder }))
            .Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.Ordinal)
            .Where(path => !loaded.Contains(path) && (include == null || include(path))).ToList();
        long bytes = 0; int assets = 0, files = 0;
        // Searching every project asset for references is slow: only when something may be deleted.
        if (candidates.Count == 0) return new GeneratedPayloadStorageInfo(0, 0, 0);
        var owners = AssetDatabase.GetAllAssetPaths().Where(p =>
            p.StartsWith("Assets/", StringComparison.Ordinal) && !p.StartsWith(GeneratedFolder + "/", StringComparison.Ordinal)
            && (p.EndsWith(".unity") || p.EndsWith(".prefab") || p.EndsWith(".asset"))).ToArray();
        var retained = new HashSet<string>(AssetDatabase.GetDependencies(owners, true), StringComparer.Ordinal);
        foreach (string path in candidates) {
            if (retained.Contains(path)) continue;
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            bool hasMeta = File.Exists(path + ".meta");
            if (hasMeta) size += new FileInfo(path + ".meta").Length;
            if (AssetDatabase.DeleteAsset(path)) { bytes += size; assets++; files += hasMeta ? 2 : 1; }
        }
        return new GeneratedPayloadStorageInfo(bytes, assets, files);
    }

    // Generated assets open objects (unsaved avatars included) or a version switch Undo/Redo still use.
    static HashSet<string> CollectLoadedGeneratedAssets()
    {
        var loaded = new HashSet<string>(VersionSwitchFileUndo.RetainedAssets(), StringComparer.Ordinal);
        foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
            if (renderer.sharedMesh != null) loaded.Add(AssetDatabase.GetAssetPath(renderer.sharedMesh));
        foreach (var animator in Resources.FindObjectsOfTypeAll<Animator>())
            if (animator.avatar != null) loaded.Add(AssetDatabase.GetAssetPath(animator.avatar));
        return loaded;
    }

    /// <summary>Deletes the generated assets of <paramref name="version"/> nothing uses any more, its shared payloads included.</summary>
    internal static GeneratedPayloadStorageInfo DeleteUnreferencedPayloadsOfVersion(CustomBaseVersion version)
    {
        string versionFolder = GetGeneratedPayloadVersionFolder(version);
        if (versionFolder == null) return new GeneratedPayloadStorageInfo(0, 0, 0);
        string assetFolder = MCBUtils.CombineUnityPath(GeneratedFolder, version.assetId.ToString());
        var deleted = DeleteUnreferencedGeneratedPayloads(assetFolder, path =>
        {
            // A humanoid Avatar belongs to the payload it was generated from ("<payload>.humanoid-<key>.asset").
            int humanoid = path.IndexOf(".humanoid-", StringComparison.Ordinal);
            string payloadPath = humanoid >= 0 ? path.Substring(0, humanoid) + ".asset" : path;
            return payloadPath.StartsWith(versionFolder + "/", StringComparison.Ordinal) || IsSharedMeshForVersion(payloadPath, version);
        });
        string fullPath = GetGeneratedPayloadFullPath(versionFolder);
        if (Directory.Exists(fullPath) && !Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories).Any())
            DeleteGeneratedPayloadFolder(versionFolder);
        PruneEmptyGeneratedPayloadAssetFolder(version);
        return deleted;
    }
}
#endif
