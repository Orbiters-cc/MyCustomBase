#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static partial class NativeMeshPayloadService
{
    // Custom geometry can introduce weighted bones absent from the original FBX.
    // Record only newly created objects so reset and the transition Undo transaction own them.
    private static void EnsurePayloadBones(Transform root, NativeMeshPayloadAsset payload)
    {
        var owner = root.GetComponentInChildren<MyCustomBase>(true);
        var definitions = (payload.bones ?? new List<NativeMeshPayloadBone>())
            .Where(b => b != null).ToDictionary(b => b.path, StringComparer.Ordinal);
        var needed = CollectPayloadBonePaths(payload.renderers);
        foreach (string path in needed.OrderBy(p => p.Count(c => c == '/')))
        {
            VersionCustomization.ValidatePath(path);
            var existing = ResolveAvatarTransform(root, path);
            int slash = path.LastIndexOf('/');
            var parent = slash < 0 ? root : ResolveAvatarTransform(root, path.Substring(0, slash));
            if (parent == null) throw new InvalidOperationException("Your avatar has no bone where the version adds '" + path + "' (its parent is missing or renamed). Nothing was changed. Undo the bone change and try again.");
            if (existing != null)
            {
                // A top-level object of the version (the armature) stays where the avatar has it (root/Model/Armature).
                if (existing.parent != parent && slash >= 0)
                {
                    if (owner == null || parent.IsChildOf(existing)) throw new InvalidOperationException("The version would put '" + path + "' under one of its own children on your avatar. Nothing was changed. Move that bone back where the base has it and try again.");
                    // Unity silently refuses reparenting model-prefab children. Unpack only when
                    // this payload actually changes hierarchy, within the version Undo transaction.
                    var instance = PrefabUtility.GetOutermostPrefabInstanceRoot(existing.gameObject);
                    if (instance != null && instance.transform != root && !instance.transform.IsChildOf(root))
                        throw new InvalidOperationException("Your avatar is inside another prefab, so MCB cannot move its bones. Nothing was changed. Unpack that prefab (right-click > Prefab > Unpack) and try again.");
                    VersionPoseLedgerService.UnpackFor(existing);
                    Undo.RecordObject(owner, "Track custom skeleton parent");
                    if (!owner.nativeMeshOriginalParents.Any(p => p.target == existing))
                        owner.nativeMeshOriginalParents.Add(new NativeMeshOriginalParent { target = existing, parent = existing.parent });
                    Undo.SetTransformParent(existing, parent, "Apply custom skeleton hierarchy");
                    if (existing.parent != parent) throw new InvalidOperationException("Unity refused to move '" + path + "' for the version. Nothing was changed. Unpack the avatar's prefab and try again.");
                    EditorUtility.SetDirty(owner);
                }
                continue;
            }
            // A bone the version adds is always MCB's own, even when clothing has a bone of that name.
            string name = GetLastPathSegment(path);
            if (owner == null || !definitions.TryGetValue(path, out var definition))
                throw new InvalidOperationException("This version's download is incomplete (no definition for the bone '" + path + "'). Nothing was changed. Delete the version from the list and download it again.");
            var bone = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(bone, "Create custom mesh bone");
            bone.transform.SetParent(parent, false);
            bone.transform.localPosition = definition.localPosition;
            bone.transform.localRotation = definition.localRotation;
            bone.transform.localScale = definition.localScale;
            // An object of the person's with that name already there (clothing's own TopFut_L): MCB's bone comes first,
            // so every path lookup (animations, Transform.Find) finds the version's bone. Removing it on going back puts
            // the order back.
            var sameNamed = Enumerable.Range(0, parent.childCount).Select(parent.GetChild).Where(t => t != bone.transform && t.name == name).ToList();
            if (sameNamed.Count > 0)
            {
                bone.transform.SetSiblingIndex(sameNamed.Min(t => t.GetSiblingIndex()));
                MCBLogger.Log($"[MCB] Your avatar has its own '{AnimationUtility.CalculateTransformPath(sameNamed[0], root)}' where the version adds that bone: "
                    + "MCB adds its own bone first and leaves yours as it is.");
            }
            Undo.RecordObject(owner, "Track custom mesh bone");
            owner.nativeMeshGeneratedBones.Add(bone);
            EditorUtility.SetDirty(owner);
        }
    }

    public static void RestoreOriginalBoneParents(MyCustomBase owner)
    {
        Undo.RecordObject(owner, "Restore original skeleton hierarchy");
        foreach (var original in owner.nativeMeshOriginalParents.AsEnumerable().Reverse())
            if (original.target != null && original.parent != null)
                Undo.SetTransformParent(original.target, original.parent, "Restore original skeleton hierarchy");
        owner.nativeMeshOriginalParents.Clear();
        EditorUtility.SetDirty(owner);
    }

    /// <summary>
    /// Refuses, before this payload writes a bone, an avatar whose armature is at another scale than the version's skeleton
    /// (a model exported in centimetres: armature at scale 100, bones at a hundredth of their positions). The version
    /// writes its skeleton at its own scale, which would change the size of every collider, PhysBone, contact and bone of
    /// the avatar's own that hangs from it. A body MCB migrated from a release made before MCB is left to install as it
    /// always did.
    /// </summary>
    internal static void RefuseOtherArmatureScale(Transform avatarRoot, NativeMeshPayloadAsset payload)
    {
        if (avatarRoot.GetComponentsInChildren<MyCustomBase>(true).Any(o => o.legacyOriginal != null && o.legacyOriginal.migrated)) return;
        var top = (payload?.bones ?? new List<NativeMeshPayloadBone>()).FirstOrDefault(b => b != null && !string.IsNullOrWhiteSpace(b.path) && b.path.IndexOf('/') < 0
            && payload.bones.Any(c => c != null && c.path != null && c.path.StartsWith(b.path + "/", StringComparison.Ordinal)));
        var armature = top == null ? null : ResolveAvatarTransform(avatarRoot, top.path);
        if (armature == null || armature == avatarRoot) return;
        var own = avatarRoot.lossyScale; var have = armature.lossyScale;
        for (int axis = 0; axis < 3; axis++)
        {
            float relative = Mathf.Abs(own[axis]) < 1e-9f ? 0f : have[axis] / own[axis];
            float wanted = top.localScale[axis];
            if (Mathf.Abs(wanted) > 1e-9f && Mathf.Abs(relative / wanted - 1f) <= 0.01f) continue;
            throw new InvalidOperationException($"Your avatar's armature '{AnimationUtility.CalculateTransformPath(armature, avatarRoot)}' has a scale of "
                + $"{relative:0.###} and this version's skeleton a scale of {wanted:0.###}: your avatar's model was exported in another unit (centimetres, usually). "
                + "Installing would change the size of everything on its bones, so nothing was changed. Use a model of your base exported at the "
                + "version's scale, or ask the version's creator which file to use.");
        }
    }

    public static void RemoveGeneratedBones(MyCustomBase owner)
    {
        Undo.RecordObject(owner, "Restore original mesh skeleton");
        foreach (var bone in owner.nativeMeshGeneratedBones.AsEnumerable().Reverse())
            if (bone != null) Undo.DestroyObjectImmediate(bone);
        owner.nativeMeshGeneratedBones.Clear();
        EditorUtility.SetDirty(owner);
    }
}
#endif
