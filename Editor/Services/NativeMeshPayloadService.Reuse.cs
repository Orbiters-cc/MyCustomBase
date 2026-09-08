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
        if (string.IsNullOrEmpty(path) || !path.StartsWith(GeneratedFolder + "/", StringComparison.Ordinal)
            || !path.Contains("/shared-")) return false;
        return version == null || (version.versionFiles ?? Array.Empty<ModelFileData>()).Any(p =>
            p?.transform == TransformName && GetGeneratedPayloadPath(version, p, GetPayloadIdentity(p)) == path);
    }

    internal static CustomBaseVersion ResolveAppliedMeshVersion(Transform root,
        IEnumerable<CustomBaseVersion> candidates, int assetId, string version, string defaultVersion)
    {
        var paths = ResolveAppliedGeneratedMeshRenderers(root)
            .Select(r => MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(r.sharedMesh)));
        return ResolveAppliedMeshVersionFromPaths(paths, candidates, assetId, version, defaultVersion);
    }

    internal static CustomBaseVersion ResolveAppliedMeshVersionFromPaths(IEnumerable<string> meshPaths,
        IEnumerable<CustomBaseVersion> candidates, int assetId, string version, string defaultVersion)
    {
        var paths = meshPaths.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return null;
        var matches = (candidates ?? Enumerable.Empty<CustomBaseVersion>())
            .Where(v => v != null && !string.IsNullOrWhiteSpace(v.version))
            .GroupBy(v => new { v.assetId, v.version, v.defaultAviVersion })
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

    static GeneratedPayloadStorageInfo DeleteUnreferencedGeneratedPayloads()
    {
        if (!AssetDatabase.IsValidFolder(GeneratedFolder)) return new GeneratedPayloadStorageInfo(0, 0, 0);
        // Saved assets and unsaved/open avatars both own references. Never remove their meshes.
        var owners = AssetDatabase.GetAllAssetPaths().Where(p =>
            p.StartsWith("Assets/", StringComparison.Ordinal) && !p.StartsWith(GeneratedFolder + "/", StringComparison.Ordinal)
            && (p.EndsWith(".unity") || p.EndsWith(".prefab") || p.EndsWith(".asset"))).ToArray();
        var retained = new HashSet<string>(AssetDatabase.GetDependencies(owners, true), StringComparer.Ordinal);
        foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
            if (renderer.sharedMesh != null) retained.Add(AssetDatabase.GetAssetPath(renderer.sharedMesh));
        long bytes = 0; int assets = 0, files = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:NativeMeshPayloadAsset", new[] { GeneratedFolder })) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (retained.Contains(path)) continue;
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            bool hasMeta = File.Exists(path + ".meta");
            if (hasMeta) size += new FileInfo(path + ".meta").Length;
            if (AssetDatabase.DeleteAsset(path)) { bytes += size; assets++; files += hasMeta ? 2 : 1; }
        }
        return new GeneratedPayloadStorageInfo(bytes, assets, files);
    }
}
#endif
