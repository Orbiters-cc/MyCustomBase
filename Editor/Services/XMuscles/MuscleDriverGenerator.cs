#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;

/// <summary>
/// XMuscles in VRChat: the corrective blendshapes of each baked muscle follow its joint through contacts. A contact sender a
/// little way down the moving bone and a proximity receiver as far up its parent bone tell how bent the joint is (about 0
/// straight, 1 folded); a 1D blend tree per muscle, inside one Direct blend tree, turns that into the correctives; VRCFury
/// merges the controller into the FX layer. Contacts scale with the avatar, so the reading is the same at any avatar scale.
/// Generated again from scratch each time (the previous rig is removed first).
/// </summary>
internal static class MuscleDriverGenerator
{
    public const string RootName = "MCB XMuscles";
    public const string ContactPrefix = "MCB XM ";
    public const string ParameterPrefix = "MCB/XM/";
    public const string OneParameter = "MCB/XM/One";
    // The receiver reads 0 at its edge and 1 at its centre; the sender is a point.
    private const float SenderRadius = 0.001f;

    internal sealed class Result
    {
        public GameObject Root;
        public AnimatorController Controller;
        public int Muscles;
        public int Contacts;
        public readonly HashSet<string> Blendshapes = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>One muscle's joint as the contacts measure it: where both contacts go and how far apart they are at a bend.</summary>
    internal readonly struct Joint
    {
        public readonly Transform Bone, Parent;
        public readonly Vector3 BoneDirection, ParentDirection;
        public readonly float Distance;
        public readonly float RestAngle;

        public Joint(Transform bone, Transform parent, Vector3 boneDirection, Vector3 parentDirection, float distance)
        {
            Bone = bone;
            Parent = parent;
            BoneDirection = boneDirection;
            ParentDirection = parentDirection;
            Distance = distance;
            RestAngle = Vector3.Angle(parentDirection, boneDirection);
        }

        /// <summary>
        /// What the receiver reads with the joint bent by <paramref name="degrees"/> from its rest pose: the contacts are
        /// <see cref="Distance"/> from the joint on each bone, so they are 2 d sin(θ/2) apart for an opening θ, and the
        /// receiver's radius is 2 d.
        /// </summary>
        public float Proximity(float degrees)
        {
            float opening = Mathf.Clamp(RestAngle - Mathf.Abs(degrees), 0f, 180f);
            return Mathf.Clamp01(1f - Mathf.Sin(opening * 0.5f * Mathf.Deg2Rad));
        }
    }

    /// <param name="renderersByMesh">The avatar's renderer for each Blender mesh name the samples use.</param>
    /// <param name="controllerPath">Where the generated controller is saved (its clips and blend trees go inside it).</param>
    public static Result Generate(GameObject avatar, MuscleCorrectiveSet set, IReadOnlyDictionary<string, SkinnedMeshRenderer> renderersByMesh, string controllerPath)
    {
        if (avatar == null) throw new ArgumentNullException(nameof(avatar));
        var result = new Result();
        Remove(avatar);
        if (set?.muscles == null || set.muscles.Count == 0) return result;

        MCBUtils.EnsureAssetFolder(Path.GetDirectoryName(controllerPath));
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath) != null) AssetDatabase.DeleteAsset(controllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
        controller.AddParameter(new AnimatorControllerParameter { name = OneParameter, type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
        var layer = controller.layers[0];
        layer.name = "MCB XMuscles";
        var direct = new BlendTree { name = "XMuscles", blendType = BlendTreeType.Direct, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(direct, controller);
        var drive = layer.stateMachine.AddState("Drive");
        drive.motion = direct;
        // Direct blend trees need write defaults: a muscle's blendshapes are animated by its tree alone.
        drive.writeDefaultValues = true;
        controller.layers = new[] { layer };

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Generate XMuscles");
        root.transform.SetParent(avatar.transform, false);
        result.Root = root;
        result.Controller = controller;

        var children = new List<ChildMotion>();
        foreach (var muscle in set.muscles)
        {
            if (muscle?.samples == null || muscle.samples.Count == 0) continue;
            var bone = FindBone(avatar.transform, muscle.bone);
            if (bone == null || bone.parent == null)
            {
                result.Warnings.Add("Muscle ‘" + muscle.name + "’: no bone ‘" + muscle.bone + "’ with a parent on the avatar.");
                continue;
            }
            if (!TryJoint(bone, out var joint))
            {
                result.Warnings.Add("Muscle ‘" + muscle.name + "’: ‘" + muscle.bone + "’ has no length to place its contacts on.");
                continue;
            }

            var shapes = new List<(MuscleCorrectiveSample sample, SkinnedMeshRenderer renderer)>();
            foreach (var sample in muscle.samples)
            {
                if (sample == null || string.IsNullOrEmpty(sample.shapeKey)) continue;
                if (renderersByMesh == null || !renderersByMesh.TryGetValue(sample.mesh ?? string.Empty, out var renderer) || renderer == null)
                {
                    result.Warnings.Add("Muscle ‘" + muscle.name + "’: no renderer on the avatar for mesh ‘" + sample.mesh + "’.");
                    continue;
                }
                if (renderer.sharedMesh == null || renderer.sharedMesh.GetBlendShapeIndex(sample.shapeKey) < 0)
                    result.Warnings.Add("Muscle ‘" + muscle.name + "’: ‘" + renderer.name + "’ has no blendshape ‘" + sample.shapeKey + "’ yet.");
                shapes.Add((sample, renderer));
            }
            if (shapes.Count == 0) continue;

            if (muscle.samples.Any(sample => sample.angleDeg > 0f) && muscle.samples.Any(sample => sample.angleDeg < 0f))
                result.Warnings.Add("Muscle ‘" + muscle.name + "’ has samples on both sides of its rest pose: a contact reads only how far the " +
                                    "joint is bent, so the two sides share their correctives.");

            string parameter = ParameterPrefix + Sanitize(muscle.name);
            string tag = "MCB_XM_" + Sanitize(muscle.name) + "_" + Mathf.Abs((avatar.name + muscle.name).GetHashCode()).ToString("x");
            AddContacts(joint, muscle.name, tag, parameter);
            result.Contacts += 2;
            controller.AddParameter(new AnimatorControllerParameter { name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = 0f });

            var tree = new BlendTree
            {
                name = muscle.name,
                blendType = BlendTreeType.Simple1D,
                blendParameter = parameter,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            var thresholds = new List<(float threshold, Motion motion)> { (joint.Proximity(0f), Clip(controller, avatar, muscle.name + " rest", shapes, null)) };
            foreach (var shape in shapes)
                thresholds.Add((joint.Proximity(shape.sample.angleDeg), Clip(controller, avatar, muscle.name + " " + shape.sample.shapeKey, shapes, shape.sample)));
            // Equal readings (the same bend) cannot be told apart: the first one is kept.
            foreach (var group in thresholds.OrderBy(item => item.threshold).GroupBy(item => Mathf.Round(item.threshold * 10000f)))
                tree.AddChild(group.First().motion, group.First().threshold);
            children.Add(new ChildMotion { motion = tree, directBlendParameter = OneParameter, timeScale = 1f });
            foreach (var shape in shapes) result.Blendshapes.Add(shape.renderer.name + "/" + shape.sample.shapeKey);
            result.Muscles++;
        }

        direct.children = children.ToArray();
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        VRCFuryService.Instance.AddFullController(root, controller);
        return result;
    }

    /// <summary>Removes a rig made earlier: its root object and the contacts it put on the bones.</summary>
    public static void Remove(GameObject avatar)
    {
        if (avatar == null) return;
        foreach (var transform in avatar.GetComponentsInChildren<Transform>(true).ToList())
        {
            if (transform == null || transform == avatar.transform) continue;
            if (transform.name == RootName || transform.name.StartsWith(ContactPrefix, StringComparison.Ordinal))
                Undo.DestroyObjectImmediate(transform.gameObject);
        }
    }

    /// <summary>The joint at this bone: halfway down the shorter of the bone and its parent, toward the bone's child and the parent's head.</summary>
    internal static bool TryJoint(Transform bone, out Joint joint)
    {
        joint = default;
        var parent = bone.parent;
        var tail = Enumerable.Range(0, bone.childCount).Select(bone.GetChild)
            .Where(child => !child.name.StartsWith(ContactPrefix, StringComparison.Ordinal))
            .OrderByDescending(child => (child.position - bone.position).sqrMagnitude).FirstOrDefault();
        Vector3 toParent = parent.position - bone.position;
        Vector3 toChild = tail != null ? tail.position - bone.position : Vector3.zero;
        float parentLength = toParent.magnitude, childLength = toChild.magnitude;
        if (parentLength < 1e-5f) return false;
        if (childLength < 1e-5f)
        {
            // An end bone: along its own axis as far as the parent is long.
            toChild = bone.rotation * Vector3.up * parentLength;
            childLength = parentLength;
        }
        joint = new Joint(bone, parent, toChild / childLength, toParent / parentLength, 0.5f * Mathf.Min(parentLength, childLength));
        return true;
    }

    private static void AddContacts(Joint joint, string muscle, string tag, string parameter)
    {
        var sender = new GameObject(ContactPrefix + muscle + " (bend)");
        Undo.RegisterCreatedObjectUndo(sender, "Generate XMuscles");
        sender.transform.SetParent(joint.Bone, false);
        sender.transform.position = joint.Bone.position + joint.BoneDirection * joint.Distance;
        var send = sender.AddComponent<VRCContactSender>();
        send.shapeType = ContactBase.ShapeType.Sphere;
        send.radius = SenderRadius / Scale(sender.transform);
        send.collisionTags = new List<string> { tag };

        var receiver = new GameObject(ContactPrefix + muscle + " (reading)");
        Undo.RegisterCreatedObjectUndo(receiver, "Generate XMuscles");
        receiver.transform.SetParent(joint.Parent, false);
        receiver.transform.position = joint.Bone.position + joint.ParentDirection * joint.Distance;
        var receive = receiver.AddComponent<VRCContactReceiver>();
        receive.shapeType = ContactBase.ShapeType.Sphere;
        receive.radius = 2f * joint.Distance / Scale(receiver.transform);
        receive.collisionTags = new List<string> { tag };
        receive.receiverType = ContactReceiver.ReceiverType.Proximity;
        receive.parameter = parameter;
        receive.allowSelf = true;
        receive.allowOthers = false;
    }

    private static float Scale(Transform transform)
    {
        var scale = transform.lossyScale;
        return Mathf.Max(1e-6f, (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f);
    }

    // A clip holding every corrective of the muscle: this sample's at 100, the others at 0 (rest: all at 0).
    private static AnimationClip Clip(AnimatorController controller, GameObject avatar, string name,
        List<(MuscleCorrectiveSample sample, SkinnedMeshRenderer renderer)> shapes, MuscleCorrectiveSample on)
    {
        var clip = new AnimationClip { name = name, hideFlags = HideFlags.HideInHierarchy };
        foreach (var shape in shapes)
        {
            string path = AnimationUtility.CalculateTransformPath(shape.renderer.transform, avatar.transform);
            var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape.sample.shapeKey);
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, 0f, shape.sample == on ? 100f : 0f));
        }
        AssetDatabase.AddObjectToAsset(clip, controller);
        return clip;
    }

    private static Transform FindBone(Transform root, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var matches = root.GetComponentsInChildren<Transform>(true).Where(transform => transform.name == name).ToList();
        // Prefer the armature's bone over a same-named object elsewhere (bones are skinned by a renderer).
        var bones = new HashSet<Transform>(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(renderer => renderer.bones).Where(b => b != null));
        return matches.FirstOrDefault(bones.Contains) ?? matches.FirstOrDefault();
    }

    private static string Sanitize(string value) => Regex.Replace(value ?? "muscle", "[^A-Za-z0-9_.-]", "_");
}
#endif
