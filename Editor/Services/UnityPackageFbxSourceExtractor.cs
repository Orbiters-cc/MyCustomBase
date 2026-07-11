#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;

public static class UnityPackageFbxSourceExtractor
{
    private const int TarBlockSize = 512;
    private const int MaxPathnameBytes = 64 * 1024;
    private const long MaxSingleFbxBytes = 1024L * 1024L * 1024L;
    private const long MaxExtractedFbxBytes = 2L * 1024L * 1024L * 1024L;
    private const long MaxTarUncompressedBytes = 8L * 1024L * 1024L * 1024L;
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
                    tempPath = pair.Value,
                    hash = MCBUtils.CalculateFileHash(pair.Value),
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

    private static Dictionary<string, string> ReadFbxPathnames(string packageFullPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var objectIdByPublishedPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ProcessTar(packageFullPath, (name, size, stream) =>
        {
            if (!TrySplitObjectEntry(name, out string objectId, out string leafName) ||
                !string.Equals(leafName, "pathname", StringComparison.OrdinalIgnoreCase))
            {
                SkipExactly(stream, size);
                return;
            }

            if (size > MaxPathnameBytes)
            {
                throw new InvalidDataException($"unitypackage pathname is too large for object {objectId}.");
            }

            byte[] bytes = new byte[(int)size];
            ReadExactlyOrThrow(stream, bytes, 0, bytes.Length, $"pathname for {objectId}");
            string publishedPath = Encoding.UTF8.GetString(bytes).Trim('\0', '\r', '\n', ' ');
            if (!TryNormalizePublishedFbxPath(publishedPath, out string normalizedPath)) return;

            if (objectIdByPublishedPath.TryGetValue(normalizedPath, out string existingObjectId) &&
                !string.Equals(existingObjectId, objectId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"unitypackage contains duplicate FBX pathname '{normalizedPath}'.");
            }
            if (result.ContainsKey(objectId))
            {
                throw new InvalidDataException($"unitypackage contains more than one pathname for object '{objectId}'.");
            }

            result[objectId] = normalizedPath;
            objectIdByPublishedPath[normalizedPath] = objectId;
        });
        return result;
    }

    private static Dictionary<string, string> ExtractSelectedAssets(
        string packageFullPath,
        string extractionRoot,
        IReadOnlyDictionary<string, string> fbxPathsByObjectId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        ProcessTar(packageFullPath, (name, size, stream) =>
        {
            if (!TrySplitObjectEntry(name, out string objectId, out string leafName) ||
                !string.Equals(leafName, "asset", StringComparison.OrdinalIgnoreCase) ||
                !fbxPathsByObjectId.ContainsKey(objectId))
            {
                SkipExactly(stream, size);
                return;
            }

            if (size <= 0 || size > MaxSingleFbxBytes || totalBytes + size > MaxExtractedFbxBytes)
            {
                throw new InvalidDataException($"unitypackage FBX extraction limit exceeded for '{fbxPathsByObjectId[objectId]}'.");
            }
            if (result.ContainsKey(objectId))
            {
                throw new InvalidDataException($"unitypackage contains more than one asset entry for '{fbxPathsByObjectId[objectId]}'.");
            }

            string outputPath = Path.Combine(extractionRoot, objectId + ".fbx");
            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyExactly(stream, output, size);
            }

            totalBytes += size;
            result[objectId] = outputPath;
        });
        return result;
    }

    private static void ProcessTar(string packageFullPath, Action<string, long, Stream> handleEntry)
    {
        using (var file = File.OpenRead(packageFullPath))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        {
            byte[] header = new byte[TarBlockSize];
            int entryCount = 0;
            long totalUncompressedBytes = 0;
            while (true)
            {
                int read = ReadExactly(gzip, header, 0, header.Length);
                if (read == 0 || IsZeroBlock(header)) return;
                if (read != header.Length) throw new InvalidDataException("Unexpected end of unitypackage TAR header.");
                if (++entryCount > MaxTarEntries) throw new InvalidDataException("unitypackage contains too many TAR entries.");

                string name = ReadNullTerminatedString(header, 0, 100);
                string prefix = ReadNullTerminatedString(header, 345, 155);
                if (!string.IsNullOrWhiteSpace(prefix)) name = prefix.TrimEnd('/') + "/" + name.TrimStart('/');
                long size = ReadOctal(header, 124, 12);
                if (size < 0) throw new InvalidDataException($"Invalid TAR size for '{name}'.");
                if (size > MaxTarUncompressedBytes - totalUncompressedBytes)
                {
                    throw new InvalidDataException("unitypackage expanded data exceeds the extraction limit.");
                }
                totalUncompressedBytes += size;

                handleEntry(name, size, gzip);
                long padding = (TarBlockSize - (size % TarBlockSize)) % TarBlockSize;
                if (padding > 0) SkipExactly(gzip, padding);
            }
        }
    }

    private static bool TrySplitObjectEntry(string name, out string objectId, out string leafName)
    {
        objectId = null;
        leafName = null;
        string normalized = (name ?? string.Empty).Replace('\\', '/').Trim('/');
        int slash = normalized.IndexOf('/');
        if (slash <= 0 || normalized.IndexOf('/', slash + 1) >= 0) return false;

        string candidateId = normalized.Substring(0, slash);
        if (candidateId.Any(character => !char.IsLetterOrDigit(character) && character != '-' && character != '_')) return false;

        objectId = candidateId;
        leafName = normalized.Substring(slash + 1);
        return true;
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

    private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static void ReadExactlyOrThrow(Stream stream, byte[] buffer, int offset, int count, string label)
    {
        if (ReadExactly(stream, buffer, offset, count) != count)
        {
            throw new InvalidDataException($"Unexpected end of unitypackage TAR data for {label}.");
        }
    }

    private static void CopyExactly(Stream source, Stream destination, long byteCount)
    {
        byte[] buffer = new byte[1024 * 1024];
        long remaining = byteCount;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new InvalidDataException("Unexpected end of unitypackage FBX data.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void SkipExactly(Stream stream, long byteCount)
    {
        byte[] buffer = new byte[8192];
        long remaining = byteCount;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new InvalidDataException("Unexpected end of unitypackage TAR data.");
            remaining -= read;
        }
    }

    private static bool IsZeroBlock(byte[] block)
    {
        return block.All(value => value == 0);
    }

    private static string ReadNullTerminatedString(byte[] buffer, int offset, int length)
    {
        int end = offset;
        int max = offset + length;
        while (end < max && buffer[end] != 0) end++;
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static long ReadOctal(byte[] buffer, int offset, int length)
    {
        string text = ReadNullTerminatedString(buffer, offset, length).Trim();
        return string.IsNullOrWhiteSpace(text) ? 0 : Convert.ToInt64(text, 8);
    }
}
#endif
