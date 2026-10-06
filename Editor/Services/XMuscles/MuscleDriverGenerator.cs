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
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;

/// <summary>
/// XMuscles in VRChat: the corrective blendshapes of each baked muscle follow the pose through contacts. Each sensor the
/// Blender bake measured becomes a proximity receiver and a point sender on the avatar's bones, where the bake measured
/// them; the receiver reads 1 - distance / radius. The stretch sensor goes from the muscle's origin to its insertion, as the
/// X-Muscle stretches. A twist sensor's receiver rides on a pivot that a VRC Aim constraint keeps along the twisting bone,
/// with that bone's rest X held as up by its parent, so it reads the twist alone (exactly, for a hinge that twists).
/// A blend tree per muscle (1D, or 2D for a muscle that also follows a twist), inside one Direct blend tree, puts each
/// corrective fully on at the readings it was baked at; VRCFury merges the controller into the FX layer. Contacts scale
/// with the avatar, so the readings are the same at any avatar scale. Contacts run on every client, so everyone sees the
/// muscles (a VRC Raycast only hits a player's own colliders on that player's client).
/// Generated again from scratch each time (the previous rig is removed first).
/// </summary>
internal static class MuscleDriverGenerator
{
    public const string RootName = "MCB XMuscles";
    public const string ContactPrefix = "MCB XM ";
    public const string ParameterPrefix = "MCB/XM/";
    public const string OneParameter = "MCB/XM/One";
    // The receiver's radius leaves room past the farthest baked distance, so a pose beyond the bake still reads above 0.
    internal const float RadiusMargin = 1.25f;
    // The sender is a point, in Blender world units (it scales with the avatar like the distances).
    internal const float SenderRadius = 0.001f;
    // The aim target's distance down the twisting bone (only its direction counts).
    private const float AimLength = 0.1f;

    internal sealed class Result
    {
        public GameObject Root;
        public AnimatorController Controller;
        public int Muscles;
        public int Contacts;
        public readonly HashSet<string> Blendshapes = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>A Blender bone-space offset in the Unity bone's space: a Blender FBX keeps each bone's Y and Z and mirrors its X.</summary>
    internal static Vector3 FromBlenderBone(float[] offset) =>
        offset == null || offset.Length < 3 ? Vector3.zero : new Vector3(-offset[0], offset[1], offset[2]);

    /// <summary>The receiver radius for a sensor whose baked distances reach <paramref name="farthest"/> (Blender world units).</summary>
    internal static float Radius(float farthest) => Mathf.Max(farthest, SenderRadius * 10f) * RadiusMargin;

    /// <summary>What a receiver of <paramref name="radius"/> reads with its point sender <paramref name="distance"/> away (same units).</summary>
    internal static float Reading(float distance, float radius) => Mathf.Clamp01(1f - Mathf.Max(0f, distance - SenderRadius) / radius);

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
        // Blender world units on this avatar: the FBX comes in at the avatar root's scale.
        float scale = Mathf.Abs(avatar.transform.lossyScale.x);

        var children = new List<ChildMotion>();
        foreach (var muscle in set.muscles)
        {
            if (muscle?.samples == null || muscle.samples.Count == 0) continue;
            int sensorCount = muscle.sensors?.Count ?? 0;
            if (sensorCount < 1 || sensorCount > 2)
            {
                result.Warnings.Add("Muscle ‘" + muscle.name + "’: " + (sensorCount == 0 ? "no sensors, bake it again in Blender." : sensorCount + " sensors, at most 2 are blended."));
                continue;
            }
            var samples = muscle.samples.Where(sample => sample != null && !string.IsNullOrEmpty(sample.shapeKey)).ToList();
            if (samples.Any(sample => sample.distances == null || sample.distances.Count != sensorCount))
            {
                result.Warnings.Add("Muscle ‘" + muscle.name + "’: a sample has no distance for each sensor, bake it again in Blender.");
                continue;
            }

            var shapes = new List<(MuscleCorrectiveSample sample, SkinnedMeshRenderer renderer)>();
            foreach (var sample in samples)
            {
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

            var radii = Enumerable.Range(0, sensorCount).Select(i => Radius(shapes.Max(shape => shape.sample.distances[i]))).ToArray();
            var parameters = new string[sensorCount];
            string problem = null;
            var placed = new List<GameObject>();
            for (int i = 0; i < sensorCount && problem == null; i++)
            {
                string label = muscle.name + (sensorCount > 1 ? " " + (i + 1) : string.Empty);
                parameters[i] = ParameterPrefix + Sanitize(muscle.name) + (sensorCount > 1 ? "/" + (i + 1) : string.Empty);
                string tag = "MCB_XM_" + Sanitize(label) + "_" + Mathf.Abs((avatar.name + label).GetHashCode()).ToString("x");
                problem = AddSensor(avatar.transform, muscle.sensors[i], label, tag, parameters[i], radii[i], scale, placed);
            }
            if (problem != null)
            {
                foreach (var created in placed) Undo.DestroyObjectImmediate(created);
                result.Warnings.Add("Muscle ‘" + muscle.name + "’: " + problem);
                continue;
            }
            result.Contacts += 2 * sensorCount;
            foreach (string parameter in parameters)
                controller.AddParameter(new AnimatorControllerParameter { name = parameter, type = AnimatorControllerParameterType.Float, defaultFloat = 0f });

            var tree = new BlendTree
            {
                name = muscle.name,
                blendType = sensorCount == 1 ? BlendTreeType.Simple1D : BlendTreeType.FreeformCartesian2D,
                blendParameter = parameters[0],
                blendParameterY = sensorCount > 1 ? parameters[1] : parameters[0],
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            var readings = new List<(Vector2 reading, Motion motion)>();
            foreach (var shape in shapes)
            {
                var reading = new Vector2(Reading(shape.sample.distances[0], radii[0]), sensorCount > 1 ? Reading(shape.sample.distances[1], radii[1]) : 0f);
                // Equal readings (the same pose) cannot be told apart: the first one is kept.
                if (readings.Any(other => (other.reading - reading).sqrMagnitude < 1e-8f))
                {
                    result.Warnings.Add("Muscle ‘" + muscle.name + "’: ‘" + shape.sample.shapeKey + "’ was baked at the same reading as another sample and is left out.");
                    continue;
                }
                readings.Add((reading, Clip(controller, avatar, muscle.name + " " + shape.sample.shapeKey, shapes, shape.sample)));
            }
            if (sensorCount == 1)
                foreach (var item in readings.OrderBy(item => item.reading.x)) tree.AddChild(item.motion, item.reading.x);
            else
                foreach (var item in readings) tree.AddChild(item.motion, item.reading);
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

    /// <summary>Removes a rig made earlier: its root object and the contacts and pivots it put on the bones.</summary>
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

    // Places a sensor's receiver and sender (and the pivot of a twist sensor); returns what is missing, or null.
    private static string AddSensor(Transform avatar, MuscleSensor sensor, string label, string tag, string parameter, float radius, float scale, List<GameObject> placed)
    {
        var receiverBone = FindBone(avatar, sensor?.receiver?.bone);
        var senderBone = FindBone(avatar, sensor?.sender?.bone);
        if (receiverBone == null || senderBone == null)
            return "no bone ‘" + (receiverBone == null ? sensor?.receiver?.bone : sensor?.sender?.bone) + "’ on the avatar.";

        Transform receiverParent = receiverBone;
        Vector3 receiverOrigin = receiverBone.position;
        Quaternion receiverAxes = receiverBone.rotation;
        if (sensor.HasAim)
        {
            var aimBone = FindBone(avatar, sensor.aim.bone);
            if (aimBone == null) return "no bone ‘" + sensor.aim.bone + "’ on the avatar.";
            var pivot = Create(ContactPrefix + label + " (pivot)", receiverBone, aimBone.position, aimBone.rotation, placed);
            var target = Create(ContactPrefix + label + " (aim)", aimBone, aimBone.position + aimBone.rotation * Vector3.up * (AimLength * scale), aimBone.rotation, placed);
            var aim = pivot.AddComponent<VRCAimConstraint>();
            aim.AimAxis = Vector3.up;
            aim.UpAxis = Vector3.right;
            aim.WorldUp = VRCConstraintBase.WorldUpType.ObjectRotationUp;
            aim.WorldUpTransform = receiverBone;
            aim.WorldUpVector = Quaternion.Inverse(receiverBone.rotation) * aimBone.rotation * Vector3.right;
            aim.Sources.Add(new VRCConstraintSource(target.transform, 1f));
            aim.RotationAtRest = pivot.transform.localEulerAngles;
            aim.Locked = true;
            aim.IsActive = true;
            receiverParent = pivot.transform;
            receiverOrigin = aimBone.position;
            receiverAxes = aimBone.rotation;
        }

        var receiver = Create(ContactPrefix + label + " (reading)", receiverParent,
            receiverOrigin + receiverAxes * (FromBlenderBone(sensor.receiver.position) * scale), receiverAxes, placed);
        var receive = receiver.AddComponent<VRCContactReceiver>();
        receive.shapeType = ContactBase.ShapeType.Sphere;
        receive.radius = radius * scale / Scale(receiver.transform);
        receive.collisionTags = new List<string> { tag };
        receive.receiverType = ContactReceiver.ReceiverType.Proximity;
        receive.parameter = parameter;
        receive.allowSelf = true;
        receive.allowOthers = false;

        var sender = Create(ContactPrefix + label + " (sender)", senderBone,
            senderBone.position + senderBone.rotation * (FromBlenderBone(sensor.sender.position) * scale), senderBone.rotation, placed);
        var send = sender.AddComponent<VRCContactSender>();
        send.shapeType = ContactBase.ShapeType.Sphere;
        send.radius = SenderRadius * scale / Scale(sender.transform);
        send.collisionTags = new List<string> { tag };
        return null;
    }

    private static GameObject Create(string name, Transform parent, Vector3 position, Quaternion rotation, List<GameObject> placed)
    {
        var created = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(created, "Generate XMuscles");
        created.transform.SetParent(parent, false);
        created.transform.SetPositionAndRotation(position, rotation);
        placed.Add(created);
        return created;
    }

    private static float Scale(Transform transform)
    {
        var scale = transform.lossyScale;
        return Mathf.Max(1e-6f, (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f);
    }

    // A clip holding every corrective of the muscle: this sample's at 100, the others at 0.
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
