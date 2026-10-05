using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.Dynamics;

/// <summary>
/// Physic: secondary-motion bone chains a creator marks by putting "physic" in their bone names (any case). With physic
/// on, the build gives the chains PhysBones; off (the default), the build removes those bones and moves their weights to
/// the nearest remaining parent, so the avatar pays nothing for a motion it does not show. The PhysBones are plain,
/// always-enabled components: every player simulates them, nothing depends on synced parameters.
/// </summary>
public static class PhysicService
{
    public const string Marker = "physic";
    // Holds the generated PhysBones on the build copy. Its name must not contain the marker.
    public const string HostName = "MCB PhysBones";
    // Length of the virtual segment a chain's last bone swings with, in meters.
    private const float EndpointLength = .1f;

    /// <summary>The PhysBone of chains that share a parent bone: one component for all of them keeps the count low.</summary>
    public sealed class Group
    {
        public Transform Root;
        public readonly List<Transform> Chains = new List<Transform>();
        public readonly List<Transform> Ignored = new List<Transform>();
        // Transforms the PhysBone affects, its root included, as VRChat counts them.
        public int Transforms;
    }

    public static bool IsPhysic(Transform bone) => bone != null && bone.name.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The armature: the humanoid hips' parent, else the top of the largest skinned mesh's skeleton.</summary>
    public static Transform Armature(Transform root)
    {
        if (root == null) return null;
        var animator = root.GetComponent<Animator>();
        var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (hips != null) return hips.parent != null && hips.parent != root ? hips.parent : hips;
        var body = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null)
            .OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
        var top = body == null ? null : body.rootBone != null ? body.rootBone : body.bones.FirstOrDefault(b => b != null);
        if (top == null || !top.IsChildOf(root)) return null;
        while (top.parent != null && top.parent != root) top = top.parent;
        return top;
    }

    /// <summary>Chain roots: physic bones of the armature whose parent is not one.</summary>
    public static List<Transform> Chains(Transform root)
    {
        var armature = Armature(root);
        if (armature == null) return new List<Transform>();
        return armature.GetComponentsInChildren<Transform>(true).Where(t => IsPhysic(t) && !IsPhysic(t.parent)).ToList();
    }

    /// <summary>Every bone of the chains. Other bones attached to a chain are not part of it.</summary>
    public static List<Transform> Bones(Transform root) =>
        Chains(root).SelectMany(c => c.GetComponentsInChildren<Transform>(true)).Where(IsPhysic).Distinct().ToList();

    public static List<Group> Groups(Transform root)
    {
        var groups = new List<Group>();
        foreach (var siblings in Chains(root).GroupBy(c => c.parent))
        {
            var group = new Group();
            group.Chains.AddRange(siblings);
            if (group.Chains.Count == 1) group.Root = group.Chains[0];
            else
            {
                // Rooted at the shared parent, which stays still; its other children (the limb itself) are left alone.
                group.Root = siblings.Key;
                group.Transforms = 1;
                foreach (Transform child in siblings.Key)
                    if (!group.Chains.Contains(child)) group.Ignored.Add(child);
            }
            foreach (var chain in group.Chains)
                foreach (var t in chain.GetComponentsInChildren<Transform>(true))
                {
                    if (IsPhysic(t)) group.Transforms++;
                    else if (IsPhysic(t.parent)) group.Ignored.Add(t);
                }
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>What the build does for this avatar: the PhysBones it adds with physic on, or the bones it removes with physic off.</summary>
    public static (int physBones, int transforms, int removedBones) Plan(MyCustomBase owner)
    {
        if (owner == null || !(owner.appliedCustomization?.physic ?? false)) return (0, 0, 0);
        var root = AvatarPaths.Root(owner);
        if (owner.physicEnabled)
        {
            var groups = Groups(root);
            return (groups.Count, groups.Sum(g => g.Transforms), 0);
        }
        var skinned = new HashSet<Transform>(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b != null));
        return (0, 0, Bones(root).Count(skinned.Contains));
    }

    /// <summary>Adds one PhysBone per group under a new object of the build copy. Returns how many.</summary>
    public static int AddPhysBones(GameObject avatar)
    {
        var groups = Groups(avatar.transform);
        if (groups.Count == 0) return 0;
        var host = new GameObject(HostName).transform;
        host.SetParent(avatar.transform, false);
        foreach (var group in groups)
        {
            var holder = new GameObject(group.Root.name);
            holder.transform.SetParent(host, false);
            Configure(holder.AddComponent<VRCPhysBone>(), group);
        }
        return groups.Count;
    }

    // Tuned on Ultirex's muscle physics: follows the body's own movement (still in world space), firm, not grabbable.
    private static void Configure(VRCPhysBone bone, Group group)
    {
        bone.version = VRCPhysBoneBase.Version.Version_1_1;
        bone.rootTransform = group.Root;
        bone.ignoreTransforms = group.Ignored.ToList();
        // A shared parent stays still; a chain's own root swings with its children.
        bone.multiChildType = group.Chains.Count > 1 ? VRCPhysBoneBase.MultiChildType.Ignore : VRCPhysBoneBase.MultiChildType.Average;
        bone.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;
        bone.pull = .585f;
        bone.spring = .752f;
        bone.stiffness = .2f;
        bone.gravity = .327f;
        bone.gravityFalloff = .483f;
        bone.immobileType = VRCPhysBoneBase.ImmobileType.World;
        bone.immobile = 1f;
        bone.limitType = VRCPhysBoneBase.LimitType.Polar;
        bone.maxAngleX = 50.4f;
        bone.maxAngleZ = 45f;
        bone.radius = 0;
        bone.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
        bone.allowGrabbing = VRCPhysBoneBase.AdvancedBool.False;
        bone.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
        bone.resetWhenDisabled = true;
        // Bones point along their local Y axis, as Blender exports them.
        bone.endpointPosition = new Vector3(0, EndpointLength / Mathf.Max(1e-4f, group.Chains[0].lossyScale.y), 0);
    }

    /// <summary>
    /// Removes the chains from the build copy. Their weights go to the nearest remaining parent; bones another component
    /// uses (a contact, a constraint, a PhysBone) stay. Each mesh this creates is passed to <paramref name="keep"/>, which
    /// must make it an asset for the upload. Returns how many bones were removed.
    /// </summary>
    public static int Strip(GameObject avatar, Action<Mesh> keep)
    {
        var bones = new HashSet<Transform>(Bones(avatar.transform));
        bones.ExceptWith(Used(avatar.transform, bones));
        if (bones.Count == 0) return 0;
        Transform Kept(Transform t) { while (t != null && bones.Contains(t)) t = t.parent; return t; }
        foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.rootBone != null && bones.Contains(renderer.rootBone)) renderer.rootBone = Kept(renderer.rootBone);
            var mesh = Rebind(renderer, bones, Kept);
            if (mesh != null) keep?.Invoke(mesh);
        }
        // Deepest first. Anything else under a removed bone moves to the kept parent, in place.
        foreach (var bone in bones.OrderByDescending(Depth).ToList())
        {
            foreach (var child in bone.Cast<Transform>().ToArray()) child.SetParent(Kept(bone), true);
            UnityEngine.Object.DestroyImmediate(bone.gameObject);
        }
        return bones.Count;
    }

    private static int Depth(Transform t)
    {
        int depth = 0;
        for (; t != null; t = t.parent) depth++;
        return depth;
    }

    // Bones with components of their own, or that a component other than a skinned mesh points at. MCB's own record
    // of the bones a version created is bookkeeping, not a use.
    private static HashSet<Transform> Used(Transform root, HashSet<Transform> bones)
    {
        var used = new HashSet<Transform>();
        foreach (var component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null || component is Transform || component is SkinnedMeshRenderer || component is MyCustomBase) continue;
            if (bones.Contains(component.transform)) used.Add(component.transform);
            var property = new SerializedObject(component).GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                var value = property.objectReferenceValue;
                var target = value is GameObject go ? go.transform : value as Transform;
                if (target != null && bones.Contains(target)) used.Add(target);
            }
        }
        return used;
    }

    // A copy of the renderer's mesh without the removed bones: their weights merge into the kept parent's.
    private static Mesh Rebind(SkinnedMeshRenderer renderer, HashSet<Transform> bones, Func<Transform, Transform> kept)
    {
        var mesh = renderer.sharedMesh;
        var old = renderer.bones;
        if (mesh == null || !old.Any(b => b != null && bones.Contains(b))) return null;
        var bindposes = mesh.bindposes;
        if (bindposes.Length != old.Length)
            throw new InvalidOperationException($"{renderer.name} has {bindposes.Length} bind poses for {old.Length} bones.");
        var result = new List<Transform>();
        var poses = new List<Matrix4x4>();
        var index = new Dictionary<Transform, int>();
        var map = new int[old.Length];
        for (int i = 0; i < old.Length; i++)
        {
            if (old[i] != null && bones.Contains(old[i])) continue;
            map[i] = result.Count;
            if (old[i] != null && !index.ContainsKey(old[i])) index[old[i]] = result.Count;
            result.Add(old[i]);
            poses.Add(bindposes[i]);
        }
        for (int i = 0; i < old.Length; i++)
        {
            if (old[i] == null || !bones.Contains(old[i])) continue;
            var target = kept(old[i]);
            if (!index.TryGetValue(target, out int j))
            {
                // The parent's bind pose follows from the removed bone's through their rest offset.
                j = result.Count;
                index[target] = j;
                result.Add(target);
                poses.Add(target.worldToLocalMatrix * old[i].localToWorldMatrix * bindposes[i]);
            }
            map[i] = j;
        }

        var counts = mesh.GetBonesPerVertex();
        var weights = mesh.GetAllBoneWeights();
        var newCounts = new byte[counts.Length];
        var newWeights = new List<BoneWeight1>(weights.Length);
        var merged = new List<BoneWeight1>(8);
        for (int v = 0, k = 0; v < counts.Length; v++)
        {
            merged.Clear();
            for (int n = 0; n < counts[v]; n++, k++)
            {
                int bone = map[weights[k].boneIndex];
                int at = merged.FindIndex(m => m.boneIndex == bone);
                if (at >= 0) merged[at] = new BoneWeight1 { boneIndex = bone, weight = merged[at].weight + weights[k].weight };
                else merged.Add(new BoneWeight1 { boneIndex = bone, weight = weights[k].weight });
            }
            // Unity expects each vertex's weights heaviest first.
            merged.Sort((a, b) => b.weight.CompareTo(a.weight));
            newCounts[v] = (byte)merged.Count;
            newWeights.AddRange(merged);
        }
        var copy = UnityEngine.Object.Instantiate(mesh);
        copy.name = mesh.name;
        using (var perVertex = new NativeArray<byte>(newCounts, Allocator.Temp))
        using (var all = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp))
            copy.SetBoneWeights(perVertex, all);
        copy.bindposes = poses.ToArray();
        renderer.sharedMesh = copy;
        renderer.bones = result.ToArray();
        return copy;
    }
}
