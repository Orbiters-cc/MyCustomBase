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
                if (existing.parent != parent)
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
