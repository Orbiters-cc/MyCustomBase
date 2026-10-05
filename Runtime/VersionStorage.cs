#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Orbiters.Toolkit.Storage;

/// <summary>Containment, complete-cache validation and transactional directory replacement of version folders.</summary>
public static class VersionStorage
{
    // Containment rules are Orbiters Toolkit's (SafePaths), shared with My Avatar's gallery downloads.
    public static void ValidateLabel(string value, string parameter) => SafePaths.ValidateLabel(value, parameter);

    public static string ContainedPath(string root, string relative) => SafePaths.ContainedPath(root, relative);

    public static void RejectLinks(string root, string path) => SafePaths.RejectLinks(root, path);

    public static bool IsComplete(string folder, CustomBaseVersion expected)
    {
        try
        {
            if (expected == null || string.IsNullOrEmpty(folder)) return false;
            var metadata = ReadJson<CustomBaseVersion>(ContainedPath(folder, "version.json"));
            var manifest = ReadJson<VersionManifest>(ContainedPath(folder, VersionManifest.FileName));
            bool matches = metadata != null && metadata.Equals(expected);
            if (!matches && metadata != null && expected.localArtifactSourceVersionKey != null &&
                metadata.assetId == expected.assetId && metadata.version == expected.version &&
                metadata.defaultAviVersion == expected.defaultAviVersion &&
                (metadata.sourceVersionKey ?? "") == expected.localArtifactSourceVersionKey)
            {
                var source = metadata.originalBaseVersions?.SingleOrDefault(v => v.key == expected.sourceVersionKey);
                matches = source != null && source.versionFiles != null && expected.versionFiles != null &&
                    source.versionFiles.Select(f => f.path + ":" + f.hash).OrderBy(p => p)
                        .SequenceEqual(expected.versionFiles.Select(f => f.path + ":" + f.hash).OrderBy(p => p));
            }
            if (!matches || manifest == null ||
                manifest.schema != VersionManifest.CurrentSchema || manifest.assetId != expected.assetId ||
                manifest.version != expected.version || manifest.defaultAviVersion != expected.defaultAviVersion ||
                manifest.outputs == null || manifest.outputs.Count == 0) return false;
            foreach (var file in manifest.outputs)
            {
                if (file == null || file.bytes < 0 || string.IsNullOrWhiteSpace(file.hash)) return false;
                var info = new FileInfo(ContainedPath(folder, file.path));
                if (!info.Exists || info.Length != file.bytes) return false;
            }
            // Required model files must be declared by the committed manifest as well.
            return (metadata.versionFiles ?? Array.Empty<ModelFileData>()).Concat(expected.versionFiles ?? Array.Empty<ModelFileData>()).All(file => file != null &&
                manifest.outputs.Any(output => string.Equals(output.path, file.path, StringComparison.Ordinal)));
        }
        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            return false;
        }
    }

    private static T ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Version metadata is too large.");
        return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
    }

    /// <summary>Stage must be a sibling on the same volume. Keep the previous directory until the rename succeeds.</summary>
    public static void ReplaceDirectory(string staging, string destination) => SafePaths.ReplaceDirectory(staging, destination);
}
#endif
