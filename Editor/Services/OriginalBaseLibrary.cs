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
        if (!File.Exists(destination)) File.Copy(path, destination, false);
        if (MCBUtils.CalculateFileHash(destination) != hash) throw new InvalidDataException("The saved original key failed verification.");
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
    public static string ActiveKey(AvatarDiscoveredAsset asset) => asset?.sourceFiles?.Length > 0 ? Key(asset.sourceFiles) : null;
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
