using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.Dynamics;

/// <summary>
/// Secondary-motion chains a creator marks in bone names (any case). <see cref="Kind.Physic"/> ("physic") reacts to the
/// avatar's movement; <see cref="Kind.Squishy"/> ("interaction") squashes when players touch it. Each kind is a version
/// option users turn on (off by default). On, the build gives the chains PhysBones; off, it removes those bones and moves
/// their weights to the nearest remaining parent, so the avatar pays nothing for a motion it does not show. The PhysBones
/// are plain, always-enabled components: every player simulates them, nothing depends on synced parameters.
/// </summary>
public static class PhysicService
{
    // Length of the virtual segment a chain's last bone swings with, in meters.
    private const float EndpointLength = .1f;
    // Collision radius of squishy bones, in meters: players' hands push them from this distance.
    private const float SquishRadius = .05f;

    /// <summary>A kind of chain, the PhysBone its chains get, and where versions and users turn it on.</summary>
    public sealed class Kind
    {
        public readonly string Marker;
        // Holds the generated PhysBones on the build copy. Its name contains no marker.
        public readonly string HostName;
        internal readonly Action<VRCPhysBone, Group> Configure;
        private readonly Func<VersionCustomization, bool> supported;
        private readonly Action<VersionCustomization, bool> support;
        private readonly Func<MyCustomBase, bool> enabled;
        private readonly Action<MyCustomBase, bool> enable;

        private Kind(string marker, string hostName, Action<VRCPhysBone, Group> configure,
            Func<VersionCustomization, bool> supported, Action<VersionCustomization, bool> support,
            Func<MyCustomBase, bool> enabled, Action<MyCustomBase, bool> enable)
        {
            Marker = marker; HostName = hostName; Configure = configure;
            this.supported = supported; this.support = support; this.enabled = enabled; this.enable = enable;
        }

        public static readonly Kind Physic = new Kind("physic", "MCB PhysBones", ConfigurePhysic,
            c => c.physic, (c, value) => c.physic = value, o => o.physicEnabled, (o, value) => o.physicEnabled = value);
        public static readonly Kind Squishy = new Kind("interaction", "MCB Squishy PhysBones", ConfigureSquishy,
            c => c.squishy, (c, value) => c.squishy = value, o => o.squishyEnabled, (o, value) => o.squishyEnabled = value);
        public static readonly Kind[] All = { Physic, Squishy };

        /// <summary>A bone of this kind. A bone named with both markers is squishy.</summary>
        public bool Owns(Transform bone) => bone != null && Named(bone, Marker) && (this == Squishy || !Named(bone, Squishy.Marker));
        public bool SupportedBy(VersionCustomization customization) => customization != null && supported(customization);
        public void SetSupported(VersionCustomization customization, bool value) => support(customization, value);
        public bool EnabledOn(MyCustomBase owner) => owner != null && enabled(owner);
        public void SetEnabled(MyCustomBase owner, bool value) => enable(owner, value);

        private static bool Named(Transform bone, string marker) => bone.name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>The PhysBone of chains that share a parent bone: one component for all of them keeps the count low.</summary>
    public sealed class Group
    {
        public Transform Root;
        public readonly List<Transform> Chains = new List<Transform>();
        public readonly List<Transform> Ignored = new List<Transform>();
        // Transforms the PhysBone affects, its root included, as VRChat counts them.
        public int Transforms;
    }

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

    /// <summary>Chain roots: bones of the kind whose parent is not one.</summary>
    public static List<Transform> Chains(Transform root, Kind kind)
    {
        var armature = Armature(root);
        if (armature == null) return new List<Transform>();
        return armature.GetComponentsInChildren<Transform>(true).Where(t => kind.Owns(t) && !kind.Owns(t.parent)).ToList();
    }

    /// <summary>Every bone of the chains. Other bones attached to a chain are not part of it.</summary>
    public static List<Transform> Bones(Transform root, Kind kind) =>
        Chains(root, kind).SelectMany(c => c.GetComponentsInChildren<Transform>(true)).Where(kind.Owns).Distinct().ToList();

    public static List<Group> Groups(Transform root, Kind kind)
    {
        var groups = new List<Group>();
        foreach (var siblings in Chains(root, kind).GroupBy(c => c.parent))
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
                    if (kind.Owns(t)) group.Transforms++;
                    else if (kind.Owns(t.parent)) group.Ignored.Add(t);
                }
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>What the build does for this avatar: the PhysBones it adds for the kinds turned on, the bones it removes for the others.</summary>
    public static (int physBones, int transforms, int removedBones) Plan(MyCustomBase owner)
    {
        int physBones = 0, transforms = 0, removed = 0;
        if (owner == null) return (0, 0, 0);
        var root = AvatarPaths.Root(owner);
        HashSet<Transform> skinned = null;
        foreach (var kind in Kind.All)
        {
            if (!kind.SupportedBy(owner.appliedCustomization)) continue;
            if (kind.EnabledOn(owner))
            {
                var groups = Groups(root, kind);
                physBones += groups.Count;
                transforms += groups.Sum(g => g.Transforms);
                continue;
            }
            skinned ??= new HashSet<Transform>(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b != null));
            removed += Bones(root, kind).Count(skinned.Contains);
        }
        return (physBones, transforms, removed);
    }

    /// <summary>Adds one PhysBone per group of the kind under a new object of the build copy. Returns how many.</summary>
    public static int AddPhysBones(GameObject avatar, Kind kind)
    {
        var groups = Groups(avatar.transform, kind);
        if (groups.Count == 0) return 0;
        var host = new GameObject(kind.HostName).transform;
        host.SetParent(avatar.transform, false);
        foreach (var group in groups)
        {
            var holder = new GameObject(group.Root.name);
            holder.transform.SetParent(host, false);
            var bone = holder.AddComponent<VRCPhysBone>();
            bone.version = VRCPhysBoneBase.Version.Version_1_1;
            bone.rootTransform = group.Root;
            bone.ignoreTransforms = group.Ignored.ToList();
            // A shared parent stays still; a chain's own root swings with its children.
            bone.multiChildType = group.Chains.Count > 1 ? VRCPhysBoneBase.MultiChildType.Ignore : VRCPhysBoneBase.MultiChildType.Average;
            bone.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;
            bone.allowGrabbing = VRCPhysBoneBase.AdvancedBool.False;
            bone.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
            bone.resetWhenDisabled = true;
            kind.Configure(bone, group);
        }
        return groups.Count;
    }

    // Tuned on Ultirex's muscle physics: follows the body's own movement (still in world space), firm, no collision.
    private static void ConfigurePhysic(VRCPhysBone bone, Group group)
    {
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
        // Bones point along their local Y axis, as Blender exports them.
        bone.endpointPosition = new Vector3(0, EndpointLength / Scale(group), 0);
    }

    // Tuned on Ultirex's squishy parts: players' hands collide with the chain, which squashes and springs back.
    private static void ConfigureSquishy(VRCPhysBone bone, Group group)
    {
        bone.pull = .585f;
        bone.spring = .915f;
        bone.stiffness = .2f;
        bone.gravity = .16f;
        bone.gravityFalloff = 0f;
        bone.immobileType = VRCPhysBoneBase.ImmobileType.AllMotion;
        bone.immobile = .292f;
        bone.limitType = VRCPhysBoneBase.LimitType.Angle;
        bone.maxAngleX = 34f;
        bone.radius = SquishRadius / Scale(group);
        bone.allowCollision = VRCPhysBoneBase.AdvancedBool.True;
        bone.maxSquish = .5f;
        bone.maxStretch = 0f;
        // A two-bone chain squashes along its own tip; a lone bone needs a short virtual one.
        bool tipped = group.Chains.All(chain => chain.Cast<Transform>().Any(Kind.Squishy.Owns));
        bone.endpointPosition = tipped ? Vector3.zero : new Vector3(0, SquishRadius / Scale(group), 0);
    }

    private static float Scale(Group group) => Mathf.Max(1e-4f, group.Chains[0].lossyScale.y);

    /// <summary>
    /// Removes the chains of these kinds from the build copy. Their weights go to the nearest remaining parent; bones
    /// another component uses (a contact, a constraint, a PhysBone) stay. Each mesh this creates is passed to
    /// <paramref name="keep"/>, which must make it an asset for the upload. Returns how many bones were removed.
    /// </summary>
    public static int Strip(GameObject avatar, IEnumerable<Kind> kinds, Action<Mesh> keep)
    {
        var bones = new HashSet<Transform>(kinds.SelectMany(kind => Bones(avatar.transform, kind)));
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
