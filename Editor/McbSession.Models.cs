#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;

// Which model files make up the avatar, and the base models the custom base applies versions to.
public sealed partial class McbSession
{
    // The detected model paths and the key they were found for. The key holds every input the detection reads besides the
    // scene (cleared on each hierarchy and project change): the avatar root and the component's base model paths.
    private string detectedAvatarFbxKey;
    private List<string> detectedAvatarFbxPaths;

    /// <summary>The first base model's project path (the one versions apply to), read from the component itself.</summary>
    public string GetCurrentFBXPath()
    {
        if (customBaseTarget == null || customBaseTarget.baseFbxFiles == null || customBaseTarget.baseFbxFiles.Count == 0) return null;
        var fbx = customBaseTarget.baseFbxFiles[0];
        return fbx != null ? AssetDatabase.GetAssetPath(fbx) : null;
    }

    public List<string> GetDetectedAvatarFbxPaths()
    {
        if (customBaseTarget == null)
        {
            return new List<string>();
        }

        // The avatar (its descriptor), not the scene root it may share with other avatars.
        Transform root = AvatarPaths.Root(customBaseTarget);
        string key = BuildDetectedAvatarFbxCacheKey(root != null ? root.GetInstanceID() : 0);
        if (detectedAvatarFbxPaths != null && key != null && string.Equals(key, detectedAvatarFbxKey, StringComparison.Ordinal))
        {
            return new List<string>(detectedAvatarFbxPaths);
        }

        var uniquePaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Each model file's skinned meshes in the scene: those skinned to the avatar's armature tell its base model apart.
        var skinned = new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.OrdinalIgnoreCase);

        void TryAddPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            path = path.Replace("\\", "/");
            if (seen.Add(path))
            {
                uniquePaths.Add(path);
            }
        }

        if (root != null)
        {
            foreach (var smr in MeshFinder.GetAllSkinnedMeshRenderers(root))
            {
                string path = AvatarSceneMeshProvenanceService.ModelPathOf(smr != null ? smr.sharedMesh : null);
                if (path == null) continue;
                TryAddPath(path);
                path = path.Replace("\\", "/");
                if (!skinned.TryGetValue(path, out var renderers)) skinned[path] = renderers = new List<SkinnedMeshRenderer>();
                renderers.Add(smr);
            }

            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                TryAddPath(AvatarSceneMeshProvenanceService.ModelPathOf(meshFilter != null ? meshFilter.sharedMesh : null));
            }
        }

        var baseModels = new AvatarBaseModels(root);
        GameObject Model(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);
        IEnumerable<SkinnedMeshRenderer> Skinned(string path) =>
            skinned.TryGetValue(path, out var renderers) ? renderers : Enumerable.Empty<SkinnedMeshRenderer>();
        // A prop's model file (a face tracking debug panel) does not stand for the base model's meshes.
        bool hasMeshSources = uniquePaths.Any(path => baseModels.Carries(Model(path), Skinned(path)));
        foreach (string path in AvatarSceneMeshProvenanceService.GetAdditionalDiscoverySources(customBaseTarget, hasMeshSources))
        {
            TryAddPath(path);
        }

        // Versions apply to the first base FBX: the model with the avatar's skeleton, never a prop found before it.
        uniquePaths = baseModels.BaseFirst(uniquePaths, Model, Skinned);

        detectedAvatarFbxKey = key;
        detectedAvatarFbxPaths = uniquePaths;
        return new List<string>(uniquePaths);
    }

    private string BuildDetectedAvatarFbxCacheKey(int rootInstanceId)
    {
        if (rootInstanceId == 0)
        {
            return null;
        }

        var basePaths = new List<string>();
        foreach (var fbx in customBaseTarget.baseFbxFiles ?? new List<GameObject>())
        {
            if (fbx == null)
            {
                continue;
            }

            string path = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(fbx));
            if (!string.IsNullOrWhiteSpace(path))
            {
                basePaths.Add(path);
            }
        }

        basePaths.Sort(StringComparer.OrdinalIgnoreCase);
        return rootInstanceId + "|" + string.Join("|", basePaths);
    }

    internal void InvalidateDetectedAvatarFbxCache()
    {
        detectedAvatarFbxKey = null;
        detectedAvatarFbxPaths = null;
    }

    // Compared with the component itself: the serialized view may not have read a change made without it yet.
    private bool AreBaseFbxReferencesEqual(List<GameObject> fbxAssets)
    {
        var current = customBaseTarget != null ? customBaseTarget.baseFbxFiles : null;
        if (current == null || fbxAssets == null || current.Count != fbxAssets.Count)
        {
            return false;
        }

        for (int i = 0; i < fbxAssets.Count; i++)
        {
            if (current[i] != fbxAssets[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The avatar's models changed (a legacy migration): detect its base models again, as when a view opens.</summary>
    internal void RedetectBaseFbx()
    {
        serializedObject.Update();
        InvalidateDetectedAvatarFbxCache();
        if (!SyncBaseFbxFilesFromSelectedAsset()) AutoDetectBaseFbxViaHierarchy();
        actions.UpdateCurrentBaseFbxHash();
    }

    private bool AutoDetectBaseFbxViaHierarchy()
    {
        if (customBaseTarget == null) return false;
        var detectedPaths = GetDetectedAvatarFbxPaths();
        if (detectedPaths.Count == 0)
        {
            return false;
        }

        var detectedAssets = new List<GameObject>();
        for (int i = 0; i < detectedPaths.Count; i++)
        {
            var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(detectedPaths[i]);
            if (fbxAsset == null)
            {
                continue;
            }

            detectedAssets.Add(fbxAsset);
        }

        if (detectedAssets.Count == 0)
        {
            return false;
        }

        if (AreBaseFbxReferencesEqual(detectedAssets))
        {
            return true;
        }

        serializedObject.ApplyModifiedProperties();
        FileManagerService.SetCreatorSourceFiles(customBaseTarget, detectedAssets);
        serializedObject.Update();
        InvalidateDetectedAvatarFbxCache();
        MCBLogger.Log($"[MCBEditor] Auto-detected {customBaseTarget.baseFbxFiles.Count} FBX file(s) for the avatar root.");
        Repaint();
        return true;
    }

    public bool SyncBaseFbxFilesFromSelectedAsset()
    {
        var selectedAsset = SelectedAsset;
        if (selectedAsset?.sourceFiles == null || selectedAsset.sourceFiles.Length == 0 || customBaseTarget == null)
        {
            return false;
        }
        // One unencrypted package serves every original: the asset's registered source is not this avatar's model.
        // Neither is it while a version is applied: the models recorded at that time are.
        if (selectedAsset.UsesPlainPackages() || (customBaseTarget.appliedCustomBaseAssetId > 0 && customBaseTarget.versionOriginalModels.Count > 0))
        {
            return false;
        }

        AvatarPathOverrideService.SyncOverridesForSelectedAsset(
            customBaseTarget,
            selectedAsset.sourceFiles,
            GetDetectedAvatarFbxPaths());

        var sourceFiles = selectedAsset.sourceFiles
            .Where(file => file != null
                           && string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(file.path))
            .ToList();
        var paths = sourceFiles
            .Select(file => AvatarPathOverrideService.ResolveLocalTargetPath(customBaseTarget, file))
            .ToList();

        if (sourceFiles.Count == 0 ||
            paths.Any(string.IsNullOrWhiteSpace) ||
            paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourceFiles.Count)
        {
            return false;
        }

        var fbxAssets = new List<GameObject>();
        foreach (string path in paths)
        {
            var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (fbxAsset == null || !(AssetImporter.GetAtPath(path) is ModelImporter))
            {
                MCBLogger.LogWarning($"[MCBEditor] Selected custom base source FBX is not present in this project: {path}");
                continue;
            }

            fbxAssets.Add(fbxAsset);
        }

        if (fbxAssets.Count != sourceFiles.Count)
        {
            return false;
        }

        if (AreBaseFbxReferencesEqual(fbxAssets))
        {
            return true;
        }

        serializedObject.ApplyModifiedProperties();
        FileManagerService.SetCreatorSourceFiles(customBaseTarget, fbxAssets);
        serializedObject.Update();
        InvalidateDetectedAvatarFbxCache();
        MCBLogger.Log($"[MCBEditor] Synced {customBaseTarget.baseFbxFiles.Count} target FBX file(s) from selected custom base source ModelFiles.");
        actions.UpdateCurrentBaseFbxHash();
        return true;
    }
}
#endif
