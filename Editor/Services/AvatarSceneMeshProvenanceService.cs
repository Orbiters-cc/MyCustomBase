#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class AvatarSceneMeshProvenanceService
{
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
