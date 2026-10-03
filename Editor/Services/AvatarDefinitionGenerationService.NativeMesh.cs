#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static partial class AvatarDefinitionGenerationService
{
    /// <summary>Builds humanoid proportions from the same absolute skeleton as the native mesh.
    /// No scene pose, clothing bones or additive authoring deltas enter the definition.</summary>
    public static Avatar BuildNativeMeshAvatar(NativeMeshPayloadAsset payload, Avatar mapping)
    {
        if (payload == null || mapping == null || !mapping.isHuman) return null;
        var description = mapping.humanDescription;
        var names = new HashSet<string>(payload.bones.Select(b => Path.GetFileName(b.path)));
        if (description.human.Any(b => !names.Contains(b.boneName))) return null; // A partial prop payload.
        var root = new GameObject("MCB Native Humanoid") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var transforms = new Dictionary<string, Transform> { [""] = root.transform };
            foreach (var bone in payload.bones.OrderBy(b => b.path.Count(c => c == '/')))
            {
                var path = bone.path;
                if (string.IsNullOrEmpty(path)) continue;
                int slash = path.LastIndexOf('/');
                string parent = slash < 0 ? "" : path.Substring(0, slash);
                if (!transforms.TryGetValue(parent, out var parentTransform))
                    throw new InvalidDataException("Native humanoid is missing parent: " + parent);
                var transform = new GameObject(path.Substring(slash + 1)).transform;
                transform.SetParent(parentTransform, false);
                transform.localPosition = bone.localPosition;
                transform.localRotation = bone.localRotation;
                transform.localScale = bone.localScale;
                transforms.Add(path, transform);
            }
            if (!TryBuildPoseCorrectedSkeleton(root, description.human, out var skeleton, out var reason))
                throw new InvalidDataException("Cannot create the native mesh humanoid pose: " + reason);
            description.skeleton = skeleton;
            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                if (avatar != null) Object.DestroyImmediate(avatar);
                throw new InvalidDataException("Native mesh skeleton did not produce a valid humanoid Avatar.");
            }
            avatar.name = "MCB Native Humanoid";
            return avatar;
        }
        finally { Object.DestroyImmediate(root); }
    }

    public static bool ApplyNativeMeshAvatar(Transform root, NativeMeshPayloadAsset payload, bool recordUndo = true)
    {
        var animator = root != null ? root.GetComponent<Animator>() : null;
        var mapping = animator != null ? animator.avatar : null;
        if (mapping == null || !mapping.isHuman || payload == null) return false;
        // Mapping and muscle settings matter, but the old skeleton does not. This also makes a
        // second application reuse the same definition instead of applying a delta again.
        var description = mapping.humanDescription;
        description.skeleton = Array.Empty<SkeletonBone>();
        string key = Hash128.Compute("native-humanoid-1|" + JsonUtility.ToJson(description)).ToString();
        string payloadPath = AssetDatabase.GetAssetPath(payload);
        string path = string.IsNullOrEmpty(payloadPath) ? null :
            Path.ChangeExtension(payloadPath, null) + ".humanoid-" + key + ".asset";
        var avatar = path == null ? null : AssetDatabase.LoadAssetAtPath<Avatar>(path);
        if (avatar == null)
        {
            avatar = BuildNativeMeshAvatar(payload, mapping);
            if (avatar == null) return false;
            if (path != null) AssetDatabase.CreateAsset(avatar, path);
        }
        if (recordUndo) return SetRootAnimatorAvatar(root, avatar);
        if (animator.avatar == avatar) return false;
        animator.avatar = avatar;
        return true;
    }
}
#endif
