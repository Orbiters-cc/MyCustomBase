#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

/// <summary>Shared containment, complete-cache validation and transactional directory replacement.</summary>
public static class VersionStorage
{
    private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static void ValidateLabel(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.EndsWith(".", StringComparison.Ordinal) ||
            value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0))
            throw new ArgumentException("Version labels must be plain names without path separators or reserved filename characters.", parameter);
    }

    public static string ContainedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":"))
            throw new InvalidDataException("A relative path inside the version folder is required.");
        foreach (string segment in relative.Replace('\\', '/').Split('/'))
        {
            try { ValidateLabel(segment, nameof(relative)); }
            catch (ArgumentException ex) { throw new InvalidDataException("The version path contains an invalid segment.", ex); }
            string device = segment.Split('.')[0].ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9'))
                throw new InvalidDataException("Version paths cannot use reserved device names.");
        }
        string fullRoot = Path.GetFullPath(root).TrimEnd('/', '\\');
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('\\', '/')));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("The version path resolves outside its storage folder.");
        RejectLinks(fullRoot, full);
        return full;
    }

    public static void RejectLinks(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd('/', '\\');
        for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Version storage cannot traverse symbolic links or junctions.");
            if (string.Equals(current.TrimEnd('/', '\\'), fullRoot, PathComparison)) return;
        }
        throw new InvalidDataException("The version path resolves outside its storage folder.");
    }

    public static bool IsComplete(string folder, CustomBaseVersion expected)
    {
        try
        {
            if (expected == null || string.IsNullOrEmpty(folder)) return false;
            var metadata = ReadJson<CustomBaseVersion>(ContainedPath(folder, "version.json"));
            var manifest = ReadJson<VersionManifest>(ContainedPath(folder, VersionManifest.FileName));
            if (metadata == null || !metadata.Equals(expected) || manifest == null ||
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
    public static void ReplaceDirectory(string staging, string destination)
    {
        string staged = Path.GetFullPath(staging), final = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(final);
        if (!string.Equals(parent, Path.GetDirectoryName(staged), PathComparison) ||
            string.Equals(staged, final, PathComparison))
            throw new InvalidDataException("Replacement requires a distinct sibling staging folder.");
        RejectLinks(parent, staged);
        RejectLinks(parent, final);
        string previous = final + ".trash-" + Guid.NewGuid().ToString("N");
        bool moved = false;
        try
        {
            if (Directory.Exists(final)) { Directory.Move(final, previous); moved = true; }
            Directory.Move(staged, final);
        }
        catch
        {
            if (moved && !Directory.Exists(final)) Directory.Move(previous, final);
            throw;
        }
        // Cleanup failure must not turn a successfully committed replacement into a failed download.
        if (moved)
        {
            try { Directory.Delete(previous, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
