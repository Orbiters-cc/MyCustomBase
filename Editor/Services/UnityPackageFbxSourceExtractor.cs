#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;

public static class UnityPackageFbxSourceExtractor
{
    private const long MaxSingleFbxBytes = 1024L * 1024L * 1024L;
    private const long MaxExtractedFbxBytes = 2L * 1024L * 1024L * 1024L;
    private const int MaxTarEntries = 100000;
    private static readonly HashSet<ExtractionResult> ActiveExtractions = new HashSet<ExtractionResult>();

    static UnityPackageFbxSourceExtractor()
    {
        CleanupStaleExtractionRoots();
        AssemblyReloadEvents.beforeAssemblyReload += DisposeActiveExtractions;
    }

    public sealed class ExtractedFbx
    {
        public string publishedSourcePath;
        public string tempPath;
        public string hash;
        public string packagePath;
    }

    public sealed class ExtractionResult : IDisposable
    {
        public readonly List<ExtractedFbx> entries;
        public readonly string extractionRoot;
        private bool disposed;

        internal ExtractionResult(string root, List<ExtractedFbx> extractedEntries)
        {
            extractionRoot = root;
            entries = extractedEntries;
            ActiveExtractions.Add(this);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ActiveExtractions.Remove(this);
            try
            {
                if (!string.IsNullOrWhiteSpace(extractionRoot) && Directory.Exists(extractionRoot))
                {
                    Directory.Delete(extractionRoot, true);
                }
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[UnityPackageFbxSourceExtractor] Could not remove temporary source files: {ex.Message}");
            }
        }
    }

    private static void DisposeActiveExtractions()
    {
        foreach (var extraction in ActiveExtractions.ToArray()) extraction.Dispose();
    }

    private static void CleanupStaleExtractionRoots()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcb_source_keys");
        if (!Directory.Exists(root)) return;
        foreach (string directory in Directory.GetDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch { }
        }
    }

    public static ExtractionResult ExtractFbxEntries(string unityPackagePath)
    {
        if (string.IsNullOrWhiteSpace(unityPackagePath))
        {
            throw new ArgumentNullException(nameof(unityPackagePath));
        }

        string packageFullPath = Path.GetFullPath(unityPackagePath);
        if (!File.Exists(packageFullPath))
        {
            throw new FileNotFoundException("Unity package file not found.", packageFullPath);
        }

        string extractionRoot = Path.Combine(Path.GetTempPath(), "mcb_source_keys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);

        try
        {
            Dictionary<string, string> fbxPathsByObjectId = ReadFbxPathnames(packageFullPath);
            var extractedByObjectId = ExtractSelectedAssets(packageFullPath, extractionRoot, fbxPathsByObjectId);
            var entries = extractedByObjectId
                .Select(pair => new ExtractedFbx
                {
                    publishedSourcePath = fbxPathsByObjectId[pair.Key],
                    tempPath = pair.Value.path,
                    hash = pair.Value.hash,
                    packagePath = unityPackagePath
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.hash))
                .OrderBy(entry => entry.publishedSourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ExtractionResult(extractionRoot, entries);
        }
        catch
        {
            try { Directory.Delete(extractionRoot, true); } catch { }
            throw;
        }
    }

    // Pass one: which records are FBX files, from the package's pathnames (Toolkit's reader checks the archive). Unity's
    // importer reads a pathname's first line, so does this: some exporters write more after it ("\n00").
    private static Dictionary<string, string> ReadFbxPathnames(string packageFullPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var objectIdByPublishedPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = UnityPackageIndex.Read(packageFullPath, maxEntries: MaxTarEntries, firstLinePathname: true);
        foreach (var entry in index.Entries)
        {
            if (!TryNormalizePublishedFbxPath(entry.Path, out string normalizedPath)) continue;
            if (objectIdByPublishedPath.TryGetValue(normalizedPath, out string existingObjectId) &&
                !string.Equals(existingObjectId, entry.Guid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"unitypackage contains duplicate FBX pathname '{normalizedPath}'.");
            }

            result[entry.Guid] = normalizedPath;
            objectIdByPublishedPath[normalizedPath] = entry.Guid;
        }
        return result;
    }

    // Pass two: the selected FBX files go to the extraction folder, hashed as they are written.
    private static Dictionary<string, (string path, string hash)> ExtractSelectedAssets(
        string packageFullPath,
        string extractionRoot,
        IReadOnlyDictionary<string, string> fbxPathsByObjectId)
    {
        var result = new Dictionary<string, (string path, string hash)>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        var buffer = new byte[1024 * 1024];
        var options = new UnityPackageReader.Options { MaxEntries = MaxTarEntries, FirstLinePathname = true };
        UnityPackageReader.Read(packageFullPath, options, record =>
        {
            if (record.Part != UnityPackageReader.Part.Asset || !fbxPathsByObjectId.TryGetValue(record.Guid, out string publishedPath)) return;
            if (record.Size <= 0 || record.Size > MaxSingleFbxBytes || totalBytes + record.Size > MaxExtractedFbxBytes)
            {
                throw new InvalidDataException($"unitypackage FBX extraction limit exceeded for '{publishedPath}'.");
            }

            string outputPath = Path.Combine(extractionRoot, record.Guid + ".fbx");
            using (var sha256 = MCBHashing.CreateSha256())
            {
                using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    for (int read; (read = record.Content.Read(buffer, 0, buffer.Length)) > 0;)
                    {
                        output.Write(buffer, 0, read);
                        sha256.TransformBlock(buffer, 0, read, null, 0);
                    }
                }
                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                result[record.Guid] = (outputPath, string.Concat(sha256.Hash.Select(b => b.ToString("x2"))));
            }

            totalBytes += record.Size;
        });
        return result;
    }

    private static bool TryNormalizePublishedFbxPath(string path, out string normalizedPath)
    {
        normalizedPath = null;
        string candidate = path?.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(candidate) || !candidate.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return false;
        if (!MCBUtils.TryResolveProjectAssetPath(candidate, out normalizedPath, out _))
        {
            throw new InvalidDataException($"unitypackage contains an unsafe FBX pathname: '{path}'.");
        }
        return true;
    }
}
#endif
