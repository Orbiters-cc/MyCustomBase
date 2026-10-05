#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class AvatarSceneMeshProvenanceService
{
    public static IEnumerable<string> GetAdditionalDiscoverySources(MyCustomBase owner, bool hasMeshSources)
    {
        if (owner == null) yield break;
        if (owner.specifyCustomBaseFbx)
        {
            foreach (var model in owner.baseFbxFiles)
                if (model != null) yield return AssetDatabase.GetAssetPath(model);
            yield break;
        }
        // Applied native meshes no longer reference their FBX. Use the models recorded when the version was applied,
        // else the applied version's source bindings, never the accumulated auto-detection list from another avatar.
        if (owner.appliedCustomBaseAssetId > 0 && owner.versionOriginalModels.Count > 0)
        {
            foreach (var model in owner.versionOriginalModels)
                if (model != null) yield return AssetDatabase.GetAssetPath(model);
            yield break;
        }
        var sources = owner.appliedCustomBaseVersion?.sourceFiles;
        if (owner.appliedCustomBaseAssetId > 0 && sources != null && sources.Length > 0)
        {
            foreach (var source in sources)
                yield return AvatarPathOverrideService.ResolveLocalTargetPath(owner, source, false);
            yield break;
        }
        if (!hasMeshSources)
        {
            var animator = AvatarPaths.Root(owner).GetComponent<Animator>();
            string path = animator != null ? AssetDatabase.GetAssetPath(animator.avatar) : null;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    public sealed class ReplacedRenderer
    {
        public string rendererPath;
        public string rendererName;
        public string expectedSourcePath;
        public string currentMeshAssetPath;
    }

    public static List<ReplacedRenderer> FindReplacedBaseRenderers(
        Transform avatarRoot,
        IEnumerable<string> baseFbxPaths)
    {
        return FindReplacedRenderers(avatarRoot, BuildExpectedSourcePaths(baseFbxPaths));
    }

    public static List<ReplacedRenderer> FindReplacedRenderers(
        Transform avatarRoot,
        IReadOnlyDictionary<string, string> expectedSourcePathByRendererPath)
    {
        var result = new List<ReplacedRenderer>();
        if (avatarRoot == null || expectedSourcePathByRendererPath == null || expectedSourcePathByRendererPath.Count == 0)
        {
            return result;
        }

        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.sharedMesh == null) continue;
            string rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, avatarRoot);
            if (!expectedSourcePathByRendererPath.TryGetValue(rendererPath, out string expectedSourcePath)) continue;

            string currentMeshAssetPath = AvatarPathOverrideService.NormalizeUnityPath(
                AssetDatabase.GetAssetPath(renderer.sharedMesh));
            if (string.Equals(currentMeshAssetPath, expectedSourcePath, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new ReplacedRenderer
            {
                rendererPath = rendererPath,
                rendererName = renderer.name,
                expectedSourcePath = expectedSourcePath,
                currentMeshAssetPath = currentMeshAssetPath
            });
        }

        return result;
    }

    private static Dictionary<string, string> BuildExpectedSourcePaths(IEnumerable<string> baseFbxPaths)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string suppliedPath in baseFbxPaths ?? Enumerable.Empty<string>())
        {
            string baseFbxPath = AvatarPathOverrideService.NormalizeUnityPath(suppliedPath);
            var baseFbx = AssetDatabase.LoadAssetAtPath<GameObject>(baseFbxPath);
            if (baseFbx == null) continue;

            foreach (var renderer in baseFbx.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.sharedMesh == null) continue;
                string rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, baseFbx.transform);
                if (!expected.ContainsKey(rendererPath))
                {
                    expected[rendererPath] = baseFbxPath;
                }
            }
        }
        return expected;
    }
}
#endif
