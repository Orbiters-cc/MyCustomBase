#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class SmrPathService
{
    private class FbxRendererLookup
    {
        public Transform root;
        public Dictionary<string, List<SkinnedMeshRenderer>> renderersByMeshName =
            new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
    }

    private class SmrPathCacheEntry
    {
        public int revision;
        public Dictionary<string, List<ModelFileSmrPathData>> pathsByFbx;
    }

    private static readonly Dictionary<string, SmrPathCacheEntry> SmrPathCache =
        new Dictionary<string, SmrPathCacheEntry>(StringComparer.Ordinal);

    private static int cacheRevision = 1;
    private static bool invalidationHooked;

    public static Dictionary<string, List<ModelFileSmrPathData>> CollectSmrPathsByFbx(Transform avatarRoot, IEnumerable<string> fbxPaths)
    {
        var result = new Dictionary<string, List<ModelFileSmrPathData>>(StringComparer.OrdinalIgnoreCase);
        if (avatarRoot == null) return result;

        EnsureInvalidationHooked();

        var targetPaths = new HashSet<string>(
            (fbxPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(MCBUtils.ToUnityPath),
            StringComparer.OrdinalIgnoreCase);

        if (targetPaths.Count == 0)
        {
            return result;
        }

        string cacheKey = BuildCacheKey(avatarRoot, targetPaths);
        if (SmrPathCache.TryGetValue(cacheKey, out var cached) && cached.revision == cacheRevision)
        {
            return CloneMap(cached.pathsByFbx);
        }

        foreach (string path in targetPaths)
        {
            result[path] = new List<ModelFileSmrPathData>();
        }

        var fbxLookups = new Dictionary<string, FbxRendererLookup>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in targetPaths)
        {
            fbxLookups[path] = BuildFbxRendererLookup(path);
        }

        foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr?.sharedMesh == null) continue;

            string meshAssetPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(smr.sharedMesh));
            if (string.IsNullOrWhiteSpace(meshAssetPath) || !targetPaths.Contains(meshAssetPath)) continue;

            if (!result.TryGetValue(meshAssetPath, out var entries))
            {
                entries = new List<ModelFileSmrPathData>();
                result[meshAssetPath] = entries;
            }

            fbxLookups.TryGetValue(meshAssetPath, out var lookup);
            var fbxRenderer = FindBestFbxRenderer(lookup, smr.sharedMesh.name, smr.transform.name);
            entries.Add(new ModelFileSmrPathData
            {
                avatarPath = GetRelativeTransformPath(avatarRoot, smr.transform),
                fbxMeshPath = fbxRenderer != null ? GetRelativeTransformPath(lookup?.root, fbxRenderer.transform) : "",
                meshName = smr.sharedMesh.name,
                rendererName = smr.transform.name
            });
        }

        foreach (string path in result.Keys.ToList())
        {
            result[path] = result[path]
                .Where(entry => entry != null && entry.avatarPath != null)
                .GroupBy(entry => entry.avatarPath, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(entry => entry.avatarPath, StringComparer.Ordinal)
                .ToList();
        }

        SmrPathCache[cacheKey] = new SmrPathCacheEntry
        {
            revision = cacheRevision,
            pathsByFbx = CloneMap(result)
        };

        return result;
    }

    public static void InvalidateCache()
    {
        cacheRevision++;
        SmrPathCache.Clear();
    }

    public static List<ModelFileSmrPathData> CollectSmrPathsForFbx(Transform avatarRoot, string fbxPath)
    {
        string unityPath = MCBUtils.ToUnityPath(fbxPath);
        var map = CollectSmrPathsByFbx(avatarRoot, new[] { unityPath });
        return map.TryGetValue(unityPath, out var entries) ? entries : new List<ModelFileSmrPathData>();
    }

    public static void RefreshTargetMeshesFromFbx(
        Transform avatarRoot,
        string fbxPath,
        IEnumerable<ModelFileSmrPathData> smrPaths,
        bool allowNameFallback = true)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(fbxPath)) return;

        string unityFbxPath = MCBUtils.ToUnityPath(fbxPath);
        var fbxRoot = GetFbxRoot(unityFbxPath);
        if (fbxRoot == null) return;

        var entries = (smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>())
            .Where(entry => entry != null && entry.avatarPath != null)
            .ToList();

        if (entries.Count > 0)
        {
            foreach (var entry in entries)
            {
                var targetTransform = FindTransformByRelativePath(avatarRoot, entry.avatarPath);
                var targetSmr = targetTransform != null ? targetTransform.GetComponent<SkinnedMeshRenderer>() : null;
                if (targetSmr == null) continue;

                Mesh replacementMesh = ResolveFbxMesh(fbxRoot.transform, entry);
                if (replacementMesh == null) continue;

                Undo.RecordObject(targetSmr, "Refresh Mesh from FBX");
                targetSmr.sharedMesh = replacementMesh;
                EditorUtility.SetDirty(targetSmr);
            }

            return;
        }

        if (!allowNameFallback) return;
        RefreshTargetMeshesByCurrentMeshName(avatarRoot, unityFbxPath, fbxRoot);
    }

    public static List<ModelFileSmrPathData> ResolveSmrPathsForSource(CustomBaseVersion version, string sourceFbxPath)
    {
        if (version?.sourceFiles == null || string.IsNullOrWhiteSpace(sourceFbxPath))
        {
            return new List<ModelFileSmrPathData>();
        }

        string normalized = MCBUtils.ToUnityPath(sourceFbxPath);
        var source = version.sourceFiles.FirstOrDefault(file =>
            file != null &&
            string.Equals(MCBUtils.ToUnityPath(file.path), normalized, StringComparison.OrdinalIgnoreCase));

        return source?.smrPaths ?? new List<ModelFileSmrPathData>();
    }

    public static string GetRelativeTransformPath(Transform root, Transform target)
    {
        if (root == null || target == null) return string.Empty;
        if (target == root) return string.Empty;

        var segments = new List<string>();
        var current = target;
        while (current != null && current != root)
        {
            segments.Add(current.name);
            current = current.parent;
        }

        if (current != root) return string.Empty;
        segments.Reverse();
        return string.Join("/", segments);
    }

    private static Transform FindTransformByRelativePath(Transform root, string relativePath)
    {
        if (root == null) return null;
        if (string.IsNullOrWhiteSpace(relativePath)) return root;

        Transform current = root;
        foreach (string rawSegment in relativePath.Split('/'))
        {
            string segment = rawSegment.Trim();
            if (string.IsNullOrEmpty(segment)) continue;
            current = current.Find(segment);
            if (current == null) return null;
        }

        return current;
    }

    private static GameObject GetFbxRoot(string fbxPath)
    {
        return string.IsNullOrWhiteSpace(fbxPath)
            ? null
            : AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(fbxPath));
    }

    private static SkinnedMeshRenderer FindBestFbxRenderer(FbxRendererLookup lookup, string meshName, string rendererName)
    {
        if (lookup == null || lookup.root == null || string.IsNullOrWhiteSpace(meshName)) return null;
        if (!lookup.renderersByMeshName.TryGetValue(meshName, out var candidates) || candidates == null || candidates.Count == 0) return null;

        return candidates.FirstOrDefault(smr => string.Equals(smr.transform.name, rendererName, StringComparison.Ordinal))
               ?? candidates.FirstOrDefault();
    }

    private static FbxRendererLookup BuildFbxRendererLookup(string fbxPath)
    {
        var lookup = new FbxRendererLookup();
        var fbxRoot = GetFbxRoot(fbxPath);
        if (fbxRoot == null)
        {
            return lookup;
        }

        lookup.root = fbxRoot.transform;
        foreach (var smr in fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr?.sharedMesh == null || string.IsNullOrWhiteSpace(smr.sharedMesh.name)) continue;
            if (!lookup.renderersByMeshName.TryGetValue(smr.sharedMesh.name, out var renderers))
            {
                renderers = new List<SkinnedMeshRenderer>();
                lookup.renderersByMeshName[smr.sharedMesh.name] = renderers;
            }

            renderers.Add(smr);
        }

        return lookup;
    }

    private static void EnsureInvalidationHooked()
    {
        if (invalidationHooked) return;
        invalidationHooked = true;
        EditorApplication.hierarchyChanged += InvalidateCache;
        EditorApplication.projectChanged += InvalidateCache;
    }

    private static string BuildCacheKey(Transform avatarRoot, IEnumerable<string> targetPaths)
    {
        return avatarRoot.GetInstanceID() + "|" + string.Join("|",
            (targetPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(MCBUtils.ToUnityPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    private static Dictionary<string, List<ModelFileSmrPathData>> CloneMap(Dictionary<string, List<ModelFileSmrPathData>> source)
    {
        var clone = new Dictionary<string, List<ModelFileSmrPathData>>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return clone;
        }

        foreach (var pair in source)
        {
            clone[pair.Key] = pair.Value != null
                ? pair.Value
                    .Where(entry => entry != null)
                    .Select(entry => new ModelFileSmrPathData
                    {
                        avatarPath = entry.avatarPath,
                        fbxMeshPath = entry.fbxMeshPath,
                        meshName = entry.meshName,
                        rendererName = entry.rendererName
                    })
                    .ToList()
                : new List<ModelFileSmrPathData>();
        }

        return clone;
    }

    private static Mesh ResolveFbxMesh(Transform fbxRoot, ModelFileSmrPathData entry)
    {
        if (fbxRoot == null || entry == null) return null;

        if (!string.IsNullOrWhiteSpace(entry.fbxMeshPath))
        {
            var transform = FindTransformByRelativePath(fbxRoot, entry.fbxMeshPath);
            var smr = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (smr?.sharedMesh != null) return smr.sharedMesh;
        }

        if (!string.IsNullOrWhiteSpace(entry.meshName))
        {
            foreach (var smr in fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr?.sharedMesh == null) continue;
                if (string.Equals(smr.sharedMesh.name, entry.meshName, StringComparison.Ordinal))
                {
                    return smr.sharedMesh;
                }
            }
        }

        return null;
    }

    private static void RefreshTargetMeshesByCurrentMeshName(Transform avatarRoot, string fbxPath, GameObject fbxRoot)
    {
        var meshLookup = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (var smr in fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr?.sharedMesh == null || meshLookup.ContainsKey(smr.sharedMesh.name)) continue;
            meshLookup.Add(smr.sharedMesh.name, smr.sharedMesh);
        }

        foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr?.sharedMesh == null) continue;
            string currentAssetPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(smr.sharedMesh));
            if (!string.Equals(currentAssetPath, fbxPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!meshLookup.TryGetValue(smr.sharedMesh.name, out var replacementMesh)) continue;

            Undo.RecordObject(smr, "Refresh Mesh from FBX");
            smr.sharedMesh = replacementMesh;
            EditorUtility.SetDirty(smr);
        }
    }
}
#endif
