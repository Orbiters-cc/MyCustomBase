using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

/// <summary>Build-copy twist generation. Weight distribution adapted from Haï's MIT Prefabulous Universal.</summary>
public static class TwistBoneService
{
    public sealed class Target
    {
        public Transform Bone, Aim, Up;
        public TwistBoneConfiguration Configuration;
    }

    public static Target Resolve(Transform root, TwistBoneConfiguration config)
    {
        var target = new Target { Bone = ResolveBone(root, config.bone), Aim = ResolveBone(root, config.aim),
            Up = ResolveBone(root, config.up), Configuration = config };
        if ((target.Aim.position - target.Bone.position).sqrMagnitude < 1e-10f)
            throw new InvalidOperationException("Twist aim must be separated from its bone: " + config.bone);
        return target;
    }

    // Supported originals can reparent the same bone (e.g. Rexouium Chest/ChestUp).
    // Match the authored path first, then only an unambiguous bone used by a skin.
    private static Transform ResolveBone(Transform root, string path)
    {
        VersionCustomization.ValidatePath(path);
        try { return AvatarPaths.Resolve(root, path); }
        catch (InvalidOperationException)
        {
            string name = path.Split('/').Last();
            var matches = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones)
                .Where(b => b != null && b.name == name).Distinct().Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
            throw new InvalidOperationException("Twist bone must match its path or one unique skinned bone: " + path);
        }
    }

    /// <summary>Generates every twist, one mesh copy per renderer for all of them. Returns the renderers changed.</summary>
    public static int Generate(GameObject avatar, IEnumerable<Target> targets)
    {
        var copies = new Dictionary<SkinnedMeshRenderer, Mesh>();
        try { foreach (var target in targets) Generate(avatar, target, copies); }
        catch { foreach (var copy in copies.Values) UnityEngine.Object.DestroyImmediate(copy); throw; }
        return copies.Count;
    }

    private static void Generate(GameObject avatar, Target target, Dictionary<SkinnedMeshRenderer, Mesh> copies)
    {
        if (target.Bone == null || target.Aim == null || target.Up == null)
            throw new InvalidOperationException("A configured twist target was removed during avatar preprocessing.");
        // Only meshes weighted to the bone: a rig's other renderers often list every bone (Rexouium OneMesh).
        var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh != null && Weighted(r, target.Bone)).ToArray();
        if (renderers.Length == 0) throw new InvalidOperationException("No mesh is weighted to twist bone: " + target.Configuration.bone);
        string name = "ZMCB_" + target.Bone.name + "_Twist";
        if (target.Bone.Find(name) != null) throw new InvalidOperationException("Twist generation was requested twice for " + target.Bone.name);
        var twist = new GameObject(name).transform;
        twist.SetParent(target.Bone, false);
        var helper = new GameObject(name + "Up").transform;
        helper.SetParent(target.Up, false);
        Vector3 direction = avatar.transform.TransformDirection(target.Configuration.upDirection.ToVector()).normalized;
        helper.position = target.Up.position + direction * Mathf.Max(Vector3.Distance(target.Up.position, target.Bone.position), .001f) * .2f;
        var constraint = twist.gameObject.AddComponent<VRCAimConstraint>();
        constraint.AimAxis = twist.InverseTransformDirection(target.Aim.position - target.Bone.position).normalized;
        constraint.UpAxis = twist.InverseTransformDirection(direction).normalized;
        constraint.WorldUp = VRCConstraintBase.WorldUpType.ObjectUp;
        constraint.WorldUpTransform = helper;
        constraint.Sources.Add(new VRCConstraintSource(target.Aim, 1));
        constraint.GlobalWeight = 1;
        constraint.AffectsRotationX = constraint.AffectsRotationY = constraint.AffectsRotationZ = true;
        constraint.Locked = constraint.IsActive = true;
        foreach (var renderer in renderers) SplitWeights(renderer, target, twist, copies);
    }

    private static bool Weighted(SkinnedMeshRenderer renderer, Transform bone)
    {
        int index = Array.IndexOf(renderer.bones, bone);
        if (index < 0) return false;
        // The mesh's own data: a view, not owned here.
        foreach (var weight in renderer.sharedMesh.GetAllBoneWeights())
            if (weight.boneIndex == index && weight.weight > 0) return true;
        return false;
    }

    // Per renderer: two renderers can share a mesh yet use different bone arrays. Never share their generated weights.
    // A renderer's copy takes its later twists in place.
    private static void SplitWeights(SkinnedMeshRenderer renderer, Target target, Transform twist, Dictionary<SkinnedMeshRenderer, Mesh> copies)
    {
        Mesh source = renderer.sharedMesh;
        var bones = renderer.bones;
        var poses = source.bindposes;
        int lower = Array.IndexOf(bones, target.Bone), tip = Array.IndexOf(bones, target.Aim);
        if (poses.Length != bones.Length || lower < 0) throw new InvalidOperationException("Invalid bindposes on " + renderer.name);
        Matrix4x4 lowerToMesh = poses[lower].inverse;
        Vector3 origin = lowerToMesh.MultiplyPoint3x4(Vector3.zero);
        Vector3 end = tip >= 0 ? poses[tip].inverse.MultiplyPoint3x4(Vector3.zero)
            : lowerToMesh.MultiplyPoint3x4(target.Bone.InverseTransformPoint(target.Aim.position));
        Vector3 segment = end - origin;
        if (segment.sqrMagnitude < 1e-12f) throw new InvalidOperationException("Degenerate twist bindpose on " + renderer.name);
        var vertices = source.vertices;
        // Copied out: a renderer's copy is rewritten in place below.
        var counts = source.GetBonesPerVertex().ToArray();
        var weights = source.GetAllBoneWeights().ToArray();
        var output = new List<BoneWeight1>(weights.Length + vertices.Length);
        var outputCounts = new byte[vertices.Length];
        var curve = target.Configuration.curve.ToCurve();
        int cursor = 0;
        for (int vertex = 0; vertex < vertices.Length; vertex++)
        {
            var vertexWeights = new List<BoneWeight1>(counts[vertex] + 1);
            float t = Mathf.Clamp01(Vector3.Dot(vertices[vertex] - origin, segment) / segment.sqrMagnitude);
            float keep = Mathf.Clamp01(curve.Evaluate(t));
            for (int j = 0; j < counts[vertex]; j++)
            {
                var weight = weights[cursor++];
                if (weight.boneIndex != lower) { vertexWeights.Add(weight); continue; }
                float twistWeight = weight.weight * (1 - keep);
                weight.weight *= keep;
                if (weight.weight > 0) vertexWeights.Add(weight);
                if (twistWeight > 0) vertexWeights.Add(new BoneWeight1 { boneIndex = bones.Length, weight = twistWeight });
            }
            if (vertexWeights.Count > byte.MaxValue) throw new InvalidOperationException("Twist would exceed 255 influences on " + renderer.name);
            vertexWeights.Sort((a, b) => b.weight.CompareTo(a.weight));
            outputCounts[vertex] = (byte)vertexWeights.Count;
            output.AddRange(vertexWeights);
        }
        if (!copies.TryGetValue(renderer, out var copy))
        {
            copy = UnityEngine.Object.Instantiate(source);
            copy.name = source.name + " (MCB Twist)";
            copies.Add(renderer, copy);
        }
        copy.bindposes = poses.Concat(new[] { poses[lower] }).ToArray();
        using var nativeCounts = new NativeArray<byte>(outputCounts, Allocator.Temp);
        using var nativeWeights = new NativeArray<BoneWeight1>(output.ToArray(), Allocator.Temp);
        copy.SetBoneWeights(nativeCounts, nativeWeights);
        renderer.sharedMesh = copy;
        renderer.bones = bones.Concat(new[] { twist }).ToArray();
    }
}
