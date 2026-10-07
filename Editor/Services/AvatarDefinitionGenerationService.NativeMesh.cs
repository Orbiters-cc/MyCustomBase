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
    /// <summary>Builds humanoid proportions from the same absolute skeleton as the native mesh, at the pose its meshes
    /// are bound in. No scene pose, clothing bones or additive authoring deltas enter the definition.</summary>
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
            PoseAtBind(root.transform, transforms, payload.renderers);
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

    // A model exported from a posed armature (Ultirex 5.1, legs spread 26° in a star pose) keeps that pose in its node
    // transforms: the meshes are still bound at rest, and applying the version puts the avatar's limbs back there. Built
    // from the node pose, the humanoid's rest would have the legs spread and VRChat's standing animation would fold them in.
    private static void PoseAtBind(Transform root, Dictionary<string, Transform> transforms, IEnumerable<NativeMeshPayloadRenderer> renderers)
    {
        var bound = new Dictionary<Transform, Matrix4x4>();
        foreach (var renderer in renderers ?? Enumerable.Empty<NativeMeshPayloadRenderer>())
        {
            var bindposes = renderer?.mesh != null ? renderer.mesh.bindposes : null;
            if (bindposes == null || renderer.bonePaths == null) continue;
            string avatarPath = renderer.avatarPath ?? "";
            int slash = avatarPath.LastIndexOf('/');
            var parent = slash > 0 && transforms.TryGetValue(avatarPath.Substring(0, slash), out var owner) ? owner : root;
            var meshToRoot = root.worldToLocalMatrix * parent.localToWorldMatrix *
                Matrix4x4.TRS(renderer.localPosition, renderer.localRotation, renderer.localScale);
            // A bone the mesh carries no weight on has no real bind pose: exporters fill in its posed node.
            var weighted = WeightedBones(renderer.mesh);
            for (int i = 0; i < renderer.bonePaths.Count && i < bindposes.Length; i++)
                if (weighted.Contains(i) && transforms.TryGetValue(renderer.bonePaths[i] ?? "", out var bone) && bone != root && !bound.ContainsKey(bone))
                    bound[bone] = meshToRoot * bindposes[i].inverse;
        }
        // Parents first; a bone no mesh is bound to keeps its place relative to its parent.
        foreach (var pair in transforms.Where(t => t.Value != root).OrderBy(t => t.Key.Count(c => c == '/')))
            if (bound.TryGetValue(pair.Value, out var rest))
                pair.Value.SetPositionAndRotation(root.TransformPoint(rest.GetColumn(3)), root.rotation * rest.rotation);
    }

    private static HashSet<int> WeightedBones(Mesh mesh)
    {
        var weighted = new HashSet<int>();
        foreach (var weight in mesh.GetAllBoneWeights())
            if (weight.weight > 0f) weighted.Add(weight.boneIndex);
        return weighted;
    }

    // Partial payloads (Ultirex's feathers) carry the whole skeleton but weigh on a few bones: their limbs only have the
    // exported pose. Of the payloads on this avatar, the one whose meshes weigh on the most humanoid bones defines it,
    // whichever payload is being applied, so every application and build gives the avatar the same definition.
    private static NativeMeshPayloadAsset DefiningPayload(Transform root, NativeMeshPayloadAsset payload, HumanDescription description)
    {
        var humanBones = new HashSet<string>(description.human.Select(h => h.boneName), StringComparer.Ordinal);
        int Score(NativeMeshPayloadAsset candidate) => candidate.renderers
            .Where(r => r.mesh != null && r.bonePaths != null)
            .SelectMany(r => WeightedBones(r.mesh).Where(i => i < r.bonePaths.Count).Select(i => Path.GetFileName(r.bonePaths[i])))
            .Where(humanBones.Contains).Distinct().Count();
        return root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Select(r => r.sharedMesh != null ? AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(AssetDatabase.GetAssetPath(r.sharedMesh)) : null)
            .Where(p => p != null).Append(payload).Distinct()
            .OrderByDescending(Score).First();
    }

    public static bool ApplyNativeMeshAvatar(Transform root, NativeMeshPayloadAsset payload, bool recordUndo = true)
    {
        var animator = root != null ? root.GetComponent<Animator>() : null;
        var mapping = animator != null ? animator.avatar : null;
        if (mapping == null || !mapping.isHuman || payload == null) return false;
        payload = DefiningPayload(root, payload, mapping.humanDescription);
        // Mapping and muscle settings matter, but the old skeleton does not. This also makes a
        // second application reuse the same definition instead of applying a delta again.
        var description = mapping.humanDescription;
        description.skeleton = Array.Empty<SkeletonBone>();
        string key = Hash128.Compute("native-humanoid-2|" + JsonUtility.ToJson(description)).ToString();
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
        bool changed = animator.avatar != avatar;
        // Also when unchanged: the payload may just have reparented bones the Animator had cached.
        AssignAvatarKeepingPose(animator, avatar);
        return changed;
    }
}
#endif
