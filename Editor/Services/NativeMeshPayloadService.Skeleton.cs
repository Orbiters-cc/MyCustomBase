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
            if (parent == null) throw new InvalidOperationException("Missing custom bone parent: " + path);
            if (existing != null)
            {
                if (existing.parent != parent)
                {
                    if (owner == null || parent.IsChildOf(existing)) throw new InvalidOperationException("Invalid custom skeleton parent: " + path);
                    // Unity silently refuses reparenting model-prefab children. Unpack only when
                    // this payload actually changes hierarchy, within the version Undo transaction.
                    var instance = PrefabUtility.GetOutermostPrefabInstanceRoot(existing.gameObject);
                    if (instance != null)
                    {
                        if (instance.transform != root && !instance.transform.IsChildOf(root))
                            throw new InvalidOperationException("Unpack the containing prefab before changing this avatar's skeleton.");
                        PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.UserAction);
                    }
                    Undo.RecordObject(owner, "Track custom skeleton parent");
                    if (!owner.nativeMeshOriginalParents.Any(p => p.target == existing))
                        owner.nativeMeshOriginalParents.Add(new NativeMeshOriginalParent { target = existing, parent = existing.parent });
                    Undo.SetTransformParent(existing, parent, "Apply custom skeleton hierarchy");
                    if (existing.parent != parent) throw new InvalidOperationException("Could not change custom skeleton parent: " + path);
                    EditorUtility.SetDirty(owner);
                }
                continue;
            }
            string name = GetLastPathSegment(path);
            if (root.GetComponentsInChildren<Transform>(true).Any(t => t.name == name))
                throw new InvalidOperationException("Ambiguous custom bone: " + path);
            if (owner == null || !definitions.TryGetValue(path, out var definition))
                throw new InvalidOperationException("Missing custom bone definition: " + path);
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
