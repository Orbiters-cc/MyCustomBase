#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;

/// <summary>Project-local original keys, addressed by their verified content hash. Never uploaded.</summary>
public static class OriginalBaseLibrary
{
    static string Root => Path.GetFullPath("Assets/MCB/original-base-keys");
    public static string Key(ModelFileData[] files)
    {
        string canonical = string.Join("|", files.Select(f => f.path.ToLowerInvariant() + ":" + f.hash).OrderBy(s => s, StringComparer.Ordinal));
        using (var sha = MCBHashing.CreateSha256()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
    }
    public static string Cache(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("Choose a non-empty original FBX file.");
        string hash = MCBUtils.CalculateFileHash(path);
        if (string.IsNullOrEmpty(hash)) throw new FileNotFoundException("Original FBX is missing.", path);
        Directory.CreateDirectory(Root);
        string destination = Path.Combine(Root, hash + ".originalbase");
        // A damaged key (truncated copy, edited file) is replaced by a verified one instead of failing forever.
        if (!File.Exists(destination) || MCBUtils.CalculateFileHash(destination) != hash)
            FileManagerService.WriteVerifiedCopy(path, destination, hash, replaceExisting: true);
        return hash;
    }
    public static string Resolve(ModelFileData file)
    {
        if (file == null || !System.Text.RegularExpressions.Regex.IsMatch(file.hash ?? "", "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid original FBX hash.");
        string cached = Path.Combine(Root, file.hash + ".originalbase");
        if (File.Exists(cached) && MCBUtils.CalculateFileHash(cached) == file.hash) return cached;
        if (MCBUtils.TryResolveProjectAssetPath(file.path, out _, out string fullPath))
        {
            foreach (string candidate in new[] { FileManagerService.GetOriginalBasePath(fullPath), fullPath + ".old", fullPath })
                if (File.Exists(candidate) && MCBUtils.CalculateFileHash(candidate) == file.hash) { Cache(candidate); return cached; }
        }
        // A renamed project FBX is valid only when its exact original hash matches.
        foreach (string guid in AssetDatabase.FindAssets("t:Model"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            foreach (string candidate in new[] { FileManagerService.GetOriginalBasePath(path), path + ".old", path })
                if (File.Exists(candidate) && MCBUtils.CalculateFileHash(candidate) == file.hash) { Cache(candidate); return cached; }
        }
        throw new FileNotFoundException("Import the original FBX for " + file.path + " (" + file.hash.Substring(0, 12) + ") in Support new version.");
    }
    private static readonly Dictionary<string, string> ProjectModels = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>An imported project model with exactly the original's bytes, or null. Plain versions read its skeleton.</summary>
    public static string FindProjectModel(ModelFileData file)
    {
        if (file?.hash == null) return null;
        if (ProjectModels.TryGetValue(file.hash, out string known) && File.Exists(known) && MCBUtils.CalculateFileHash(known) == file.hash) return known;
        var candidates = new[] { file.path }.Concat(AssetDatabase.FindAssets("t:Model").Select(AssetDatabase.GUIDToAssetPath))
            .Where(p => !string.IsNullOrEmpty(p) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).Distinct();
        foreach (string path in candidates)
            if (MCBUtils.CalculateFileHash(path) == file.hash) { ProjectModels[file.hash] = path; return path; }
        return null;
    }

    /// <summary>
    /// Which of <paramref name="bones"/> every given original has; null when none of them is imported in this project.
    /// An original holding fewer than half of them is an accessory model (e.g. head feathers only) and is ignored.
    /// </summary>
    public static HashSet<string> SharedSkeleton(IEnumerable<OriginalBaseVersionData> versions, ICollection<string> bones)
    {
        HashSet<string> shared = null;
        foreach (var source in (versions ?? Enumerable.Empty<OriginalBaseVersionData>()).SelectMany(v => v.sourceFiles ?? Array.Empty<ModelFileData>()))
        {
            string path = FindProjectModel(source);
            var model = path != null ? AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(path) : null;
            if (model == null) continue;
            var names = new HashSet<string>(model.GetComponentsInChildren<UnityEngine.Transform>(true).Select(t => t.name), StringComparer.Ordinal);
            if (bones.Count(names.Contains) * 2 < bones.Count) continue;
            if (shared == null) shared = new HashSet<string>(bones.Where(names.Contains), StringComparer.Ordinal); else shared.IntersectWith(names);
        }
        return shared;
    }

    public static string ActiveKey(AvatarDiscoveredAsset asset)
    {
        if (asset?.sourceFiles?.Length > 0)
        {
            string key = Key(asset.sourceFiles);
            // Nonmatching discovery returns every supported original, not one active set.
            if (asset.sourceVersions?.Length > 0 && !asset.sourceVersions.Any(v => v.key == key)) return null;
            return key;
        }
        return null;
    }
    public static OriginalBaseVersionData[] Versions(AvatarDiscoveredAsset asset)
    {
        if (asset?.sourceVersions?.Length > 0) return asset.sourceVersions;
        if (asset?.sourceFiles?.Length > 0) return new[] { new OriginalBaseVersionData { key = Key(asset.sourceFiles), label = "Original base", sourceFiles = asset.sourceFiles } };
        return Array.Empty<OriginalBaseVersionData>();
    }
    static string SelectionPath(int assetId) => Path.Combine(Root, assetId + "-selection.json");
    public static string[] Selection(AvatarDiscoveredAsset asset)
    {
        var versions = Versions(asset);
        string path = SelectionPath(asset.id);
        var saved = File.Exists(path) ? JsonConvert.DeserializeObject<string[]>(File.ReadAllText(path)) : null;
        return saved != null ? saved.Where(key => versions.Any(v => v.key == key)).ToArray() : versions.Select(v => v.key).ToArray();
    }
    public static void SaveSelection(int assetId, IEnumerable<string> keys)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(SelectionPath(assetId), JsonConvert.SerializeObject(keys.ToArray()));
    }
}
#endif
