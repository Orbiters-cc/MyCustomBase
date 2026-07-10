#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

public static class UnityPackageFbxSourceExtractor
{
    public sealed class ExtractedFbx
    {
        public string publishedSourcePath;
        public string tempPath;
        public string hash;
        public string packagePath;
    }

    private sealed class PackageObject
    {
        public string pathname;
        public byte[] assetBytes;
    }

    public static List<ExtractedFbx> ExtractFbxEntries(string unityPackagePath)
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

        var objects = ReadUnityPackageObjects(packageFullPath);
        var result = new List<ExtractedFbx>();
        foreach (var entry in objects.Values)
        {
            string publishedPath = NormalizePackagePath(entry.pathname);
            if (string.IsNullOrWhiteSpace(publishedPath) ||
                !publishedPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                entry.assetBytes == null ||
                entry.assetBytes.Length == 0)
            {
                continue;
            }

            string outputPath = Path.Combine(extractionRoot, SanitizePathForFileName(publishedPath));
            File.WriteAllBytes(outputPath, entry.assetBytes);
            result.Add(new ExtractedFbx
            {
                publishedSourcePath = publishedPath,
                tempPath = outputPath,
                hash = MCBUtils.CalculateFileHash(outputPath),
                packagePath = unityPackagePath
            });
        }

        return result
            .Where(entry => !string.IsNullOrWhiteSpace(entry.hash))
            .OrderBy(entry => entry.publishedSourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, PackageObject> ReadUnityPackageObjects(string packageFullPath)
    {
        var result = new Dictionary<string, PackageObject>(StringComparer.OrdinalIgnoreCase);
        using (var file = File.OpenRead(packageFullPath))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        {
            foreach (var entry in ReadTarEntries(gzip))
            {
                string normalizedName = entry.Name.Replace('\\', '/').Trim('/');
                int slash = normalizedName.IndexOf('/');
                if (slash <= 0)
                {
                    continue;
                }

                string objectId = normalizedName.Substring(0, slash);
                string leafName = normalizedName.Substring(slash + 1);
                if (!result.TryGetValue(objectId, out var packageObject))
                {
                    packageObject = new PackageObject();
                    result[objectId] = packageObject;
                }

                if (string.Equals(leafName, "pathname", StringComparison.OrdinalIgnoreCase))
                {
                    packageObject.pathname = Encoding.UTF8.GetString(entry.Bytes).Trim('\0', '\r', '\n', ' ');
                }
                else if (string.Equals(leafName, "asset", StringComparison.OrdinalIgnoreCase))
                {
                    packageObject.assetBytes = entry.Bytes;
                }
            }
        }

        return result;
    }

    private static IEnumerable<TarEntry> ReadTarEntries(Stream stream)
    {
        byte[] header = new byte[512];
        while (true)
        {
            int read = ReadExactly(stream, header, 0, header.Length);
            if (read == 0 || IsZeroBlock(header))
            {
                yield break;
            }

            if (read != header.Length)
            {
                throw new InvalidDataException("Unexpected end of unitypackage TAR header.");
            }

            string name = ReadNullTerminatedString(header, 0, 100);
            long size = ReadOctal(header, 124, 12);
            if (size > int.MaxValue)
            {
                throw new InvalidDataException($"unitypackage TAR entry is too large to read in memory: {name}");
            }

            byte[] bytes = new byte[(int)size];
            if (size > 0)
            {
                int dataRead = ReadExactly(stream, bytes, 0, bytes.Length);
                if (dataRead != bytes.Length)
                {
                    throw new InvalidDataException($"Unexpected end of unitypackage TAR data for '{name}'.");
                }
            }

            long padding = (512 - (size % 512)) % 512;
            if (padding > 0)
            {
                SkipExactly(stream, padding);
            }

            yield return new TarEntry { Name = name, Bytes = bytes };
        }
    }

    private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void SkipExactly(Stream stream, long byteCount)
    {
        byte[] buffer = new byte[Math.Min(8192, (int)Math.Max(1, byteCount))];
        long remaining = byteCount;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new InvalidDataException("Unexpected end of unitypackage TAR padding.");
            }

            remaining -= read;
        }
    }

    private static bool IsZeroBlock(byte[] block)
    {
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] != 0) return false;
        }

        return true;
    }

    private static string ReadNullTerminatedString(byte[] buffer, int offset, int length)
    {
        int end = offset;
        int max = offset + length;
        while (end < max && buffer[end] != 0)
        {
            end++;
        }

        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static long ReadOctal(byte[] buffer, int offset, int length)
    {
        string text = ReadNullTerminatedString(buffer, offset, length).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Convert.ToInt64(text, 8);
    }

    private static string NormalizePackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string SanitizePathForFileName(string path)
    {
        string value = NormalizePackagePath(path) ?? "source.fbx";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value.Replace('/', '_');
    }

    private sealed class TarEntry
    {
        public string Name;
        public byte[] Bytes;
    }
}
#endif
