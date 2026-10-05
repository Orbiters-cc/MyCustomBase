#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

// Existing applied scenes also need the payload's humanoid definition on the upload/play copy.
public sealed class NativeMeshAvatarBuildHook : IVRCSDKPreprocessAvatarCallback
{
    public int callbackOrder => -10200;
    public bool OnPreprocessAvatar(GameObject avatarRoot)
    {
        // Material edits saved lazily (custom veins) are on disk before anything reads the project.
        MaterialService.FlushPendingSaves(true);
        if (avatarRoot.GetComponent<MyCustomBase>() == null) return true;
        // Read the payload that owns the assigned mesh, including after reload. A version's
        // download metadata is not required to build the already-applied avatar.
        var paths = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Select(r => AssetDatabase.GetAssetPath(r.sharedMesh)).Where(p => !string.IsNullOrEmpty(p)).Distinct();
        foreach (var path in paths)
        {
            var payload = AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(path);
            if (payload != null) AvatarDefinitionGenerationService.ApplyNativeMeshAvatar(avatarRoot.transform, payload, recordUndo: false);
        }
        return true;
    }
}
#endif
