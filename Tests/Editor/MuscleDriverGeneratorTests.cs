#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools.Utils;

// XMuscles driving: each sensor the bake measured becomes a receiver and a sender on the avatar's bones, and the blend trees
// put each corrective at the reading its baked distances give. The bake is played here by the test arm itself: its
// distances are measured in the poses of the samples, as XMuscle Orbit Helper measures them in Blender, then the arm is
// posed again and the contacts must read what the blend trees expect, at two avatar scales.
public class MuscleDriverGeneratorTests
{
    private string folder;
    private GameObject avatar;
    private Transform upper, lower, hand;
    private SkinnedMeshRenderer body;
    private Mesh mesh;
    private Quaternion lowerRest;

    // The bake's poses: elbow bends around the forearm's rest X, twists around its own Y (hinge, then twist).
    private static readonly float[] Bends = { 0f, 45f, 90f, 135f };
    private static readonly float[] Twists = { -60f, 0f, 60f };

    // The sensors as Blender describes them: offsets along each bone's rest axes, Blender bone axes (X mirrored in Unity).
    private static MuscleSensor Stretch() => new MuscleSensor
    {
        receiver = new MuscleSensorPoint { bone = "UpperArm.L", position = new[] { -0.01f, 0.05f, 0.04f } },
        sender = new MuscleSensorPoint { bone = "LowerArm.L", position = new[] { 0.002f, 0.08f, 0.02f } },
    };

    private static MuscleSensor Twist() => new MuscleSensor
    {
        // A quarter turn ahead of the sender on the circle around the forearm (as XMuscle Orbit Helper places it).
        receiver = new MuscleSensorPoint { bone = "UpperArm.L", position = new[] { 0.12f, 0f, 0f } },
        sender = new MuscleSensorPoint { bone = "LowerArm.L", position = new[] { 0f, 0f, 0.12f } },
        aim = new MuscleSensorAim { bone = "LowerArm.L" },
    };

    [SetUp]
    public void SetUp()
    {
        folder = "Assets/MCB_XMuscleTest_" + Guid.NewGuid().ToString("N");
        avatar = new GameObject("XMuscle Test Avatar");
        var armature = new GameObject("Armature").transform;
        armature.SetParent(avatar.transform, false);
        // Rolled bones, as an FBX has them: the sensors must follow each bone's own axes.
        upper = Bone("UpperArm.L", armature, new Vector3(0.2f, 1.4f, 0f), Quaternion.Euler(10f, -5f, 95f));
        lower = Bone("LowerArm.L", upper, new Vector3(0.45f, 1.39f, 0.01f), Quaternion.Euler(-8f, 20f, 92f));
        hand = Bone("Hand.L", lower, new Vector3(0.7f, 1.39f, 0.02f), Quaternion.Euler(0f, 0f, 90f));
        lowerRest = lower.localRotation;
        var bodyObject = new GameObject("Body");
        bodyObject.transform.SetParent(avatar.transform, false);
        body = bodyObject.AddComponent<SkinnedMeshRenderer>();
        mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        foreach (string shape in Grid().Select(pose => pose.shape).Concat(Bends.Select(bend => "bend_" + bend)))
            mesh.AddBlendShapeFrame(shape, 100f, new[] { Vector3.up * 0.01f, Vector3.zero, Vector3.zero }, null, null);
        body.sharedMesh = mesh;
        body.bones = new[] { upper, lower, hand };
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(avatar);
        UnityEngine.Object.DestroyImmediate(mesh);
        if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
    }

    private static Transform Bone(string name, Transform parent, Vector3 worldPosition, Quaternion worldRotation)
    {
        var bone = new GameObject(name).transform;
        bone.SetParent(parent, false);
        bone.SetPositionAndRotation(worldPosition, worldRotation);
        return bone;
    }

    private static IEnumerable<(float bend, float twist, string shape)> Grid() =>
        Twists.SelectMany(twist => Bends.Select(bend => (bend, twist, "grid_" + bend + "_" + twist)));

    private void Pose(float bend, float twist) =>
        lower.localRotation = lowerRest * Quaternion.AngleAxis(bend, Vector3.right) * Quaternion.AngleAxis(twist, Vector3.up);

    // Where a Blender offset lands, with the avatar at rest (Blender world units are Unity's at scale 1).
    private Vector3 Point(Transform bone, float[] offset) => bone.position + bone.rotation * MuscleDriverGenerator.FromBlenderBone(offset);

    // The bake: each sensor's distance in each sample's pose, measured on the arm itself. The twist receiver rides on the
    // pivot, which follows the forearm's direction with the forearm's rest X carried by the upper arm as up.
    private float Distance(MuscleSensor sensor, float bend, float twist)
    {
        Pose(0f, 0f);
        Vector3 senderLocal = lower.InverseTransformPoint(Point(lower, sensor.sender.position));
        Vector3 receiverWorld = sensor.HasAim ? Point(lower, sensor.receiver.position) : Point(upper, sensor.receiver.position);
        Vector3 receiverLocal = sensor.HasAim ? lower.InverseTransformPoint(receiverWorld) : upper.InverseTransformPoint(receiverWorld);
        Pose(bend, twist);
        Vector3 sender = lower.TransformPoint(senderLocal);
        Vector3 receiver = sensor.HasAim
            ? lower.position + upper.rotation * lowerRest * Quaternion.AngleAxis(bend, Vector3.right) * receiverLocal
            : upper.TransformPoint(receiverLocal);
        Pose(0f, 0f);
        return Vector3.Distance(sender, receiver);
    }

    private MuscleCorrectiveSet Bake() => new MuscleCorrectiveSet
    {
        apiVersion = MuscleCorrectiveSet.ApiVersion,
        muscles =
        {
            new MuscleCorrective
            {
                name = "biceps.L", sensors = { Stretch(), Twist() },
                samples = Grid().Select(pose => new MuscleCorrectiveSample
                {
                    shapeKey = pose.shape, mesh = "Body",
                    distances = { Distance(Stretch(), pose.bend, pose.twist), Distance(Twist(), pose.bend, pose.twist) }
                }).ToList()
            },
            new MuscleCorrective
            {
                name = "triceps.L", sensors = { Stretch() },
                samples = Bends.Select(bend => new MuscleCorrectiveSample
                {
                    shapeKey = "bend_" + bend, mesh = "Body", distances = { Distance(Stretch(), bend, 0f) }
                }).ToList()
            }
        }
    };

    private MuscleDriverGenerator.Result Generate(MuscleCorrectiveSet set = null) => MuscleDriverGenerator.Generate(avatar, set ?? Bake(),
        new Dictionary<string, SkinnedMeshRenderer> { { "Body", body } }, folder + "/XMuscles.controller");

    // The tests' assembly does not reference VRChat's components: they are read by name.
    private static Component[] Components(Component root, string type) =>
        root.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == type).ToArray();

    private static T Field<T>(Component component, string name)
    {
        for (var type = component.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            if (field != null) return (T)field.GetValue(component);
        }
        throw new MissingFieldException(component.GetType().Name, name);
    }

    // What VRChat's proximity receiver reads: 1 - distance to the sender's closest point / radius (radii scale with the transform).
    private static float Reading(Component receiver, Component sender)
    {
        float radius = Field<float>(receiver, "radius") * receiver.transform.lossyScale.x;
        float senderRadius = Field<float>(sender, "radius") * sender.transform.lossyScale.x;
        return Mathf.Clamp01(1f - Mathf.Max(0f, Vector3.Distance(receiver.transform.position, sender.transform.position) - senderRadius) / radius);
    }

    private static Component Contact(Component root, string type, string label) =>
        Components(root, type).Single(contact => contact.name.StartsWith(MuscleDriverGenerator.ContactPrefix + label + " (", StringComparison.Ordinal));

    // Runs VRChat's constraints once, as its player loop does (the SDK keeps this API internal; the test cannot run without it).
    private static void EvaluateConstraints(Component constraint)
    {
        var assembly = constraint.GetType().BaseType?.Assembly;
        var constraintBase = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.Dynamics.VRCConstraintBase")).FirstOrDefault(t => t != null);
        var manager = constraintBase?.Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
        var stage = constraintBase?.Assembly.GetType("VRC.Dynamics.VRCConstraintPlayerLoopStage");
        if (manager == null || stage == null) Assert.Inconclusive("VRChat's constraint manager was not found (SDK " + assembly + ").");
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var usage = constraintBase.BaseType.GetProperty("Usage");
        usage.SetValue(constraint, Enum.Parse(usage.PropertyType, "Avatar"));
        if (!(bool)manager.GetProperty("IsInitialized", any).GetValue(null)) manager.GetMethod("Initialize", any).Invoke(null, null);
        if (!(bool)manager.GetMethod("IsConstraintRegistered", any).Invoke(null, new object[] { constraint }))
            manager.GetMethod("RegisterConstraint", any).Invoke(null, new object[] { constraint });
        var array = Array.CreateInstance(constraintBase, 1);
        array.SetValue(constraint, 0);
        manager.GetMethod("Sdk_ManuallyRefreshGroups", any).Invoke(null, new object[] { array });
        manager.GetMethod("UpdateConstraints", any).Invoke(null, null);
        var handle = (Unity.Jobs.JobHandle)manager.GetMethod("ScheduleReadJob", any).Invoke(null, new object[] { default(Unity.Jobs.JobHandle) });
        foreach (var value in Enum.GetValues(stage))
            handle = (Unity.Jobs.JobHandle)manager.GetMethod("ScheduleExecutionJobs", any).Invoke(null, new[] { value, handle });
        handle.Complete();
        manager.GetMethod("PostUpdateConstraints", any).Invoke(null, null);
    }

    [Test]
    public void BlenderOffsetsKeepTheBoneYAndZAndMirrorItsX()
    {
        // Checked on an FBX exported by Blender: a bone's local X is mirrored, its Y (along the bone) and Z are kept.
        Assert.That(MuscleDriverGenerator.FromBlenderBone(new[] { 1f, 2f, 3f }), Is.EqualTo(new Vector3(-1f, 2f, 3f)));
        Assert.That(MuscleDriverGenerator.Reading(0f, 0.5f), Is.EqualTo(1f));
        Assert.That(MuscleDriverGenerator.Reading(0.5f + MuscleDriverGenerator.SenderRadius, 0.5f), Is.EqualTo(0f));
    }

    [Test]
    public void TheStretchReadingPutsEachCorrectiveAtItsBakedPoseAtAnyScale()
    {
        var result = Generate();
        Assert.That(result.Warnings, Is.Empty, string.Join("\n", result.Warnings));
        Assert.That(result.Muscles, Is.EqualTo(2));
        Assert.That(result.Contacts, Is.EqualTo(6));

        var sender = Contact(lower, "VRCContactSender", "triceps.L");
        var receiver = Contact(upper, "VRCContactReceiver", "triceps.L");
        Assert.That(sender.transform.parent, Is.EqualTo(lower), "The sender rides the bake's driver bone.");
        Assert.That(receiver.transform.parent, Is.EqualTo(upper), "The receiver stays at the muscle's origin.");
        Assert.That(receiver.transform.position, Is.EqualTo(Point(upper, Stretch().receiver.position)).Using(Vector3EqualityComparer.Instance));
        Assert.That(Field<object>(receiver, "receiverType").ToString(), Is.EqualTo("Proximity"));
        Assert.That(Field<string>(receiver, "parameter"), Is.EqualTo("MCB/XM/triceps.L"));
        Assert.That(Field<bool>(receiver, "allowSelf"), Is.True);
        Assert.That(Field<bool>(receiver, "allowOthers"), Is.False);
        Assert.That(Field<List<string>>(receiver, "collisionTags"), Is.EqualTo(Field<List<string>>(sender, "collisionTags")));

        var trees = ((BlendTree)result.Controller.layers[0].stateMachine.defaultState.motion).children.Select(child => (BlendTree)child.motion).ToList();
        var triceps = trees.Single(tree => tree.name == "triceps.L");
        Assert.That(triceps.blendType, Is.EqualTo(BlendTreeType.Simple1D));
        Assert.That(triceps.blendParameter, Is.EqualTo("MCB/XM/triceps.L"));
        Assert.That(triceps.children.Select(child => child.threshold), Is.Ordered.Ascending);
        Assert.That(triceps.children.Length, Is.EqualTo(Bends.Length), "One child per corrective: the first sample is the rest pose.");

        foreach (float scale in new[] { 1f, 2.5f })
        {
            avatar.transform.localScale = Vector3.one * scale;
            foreach (float bend in Bends)
            {
                Pose(bend, 0f);
                var child = triceps.children.Single(c => ((AnimationClip)c.motion).name.EndsWith("bend_" + bend, StringComparison.Ordinal));
                Assert.That(Reading(receiver, sender), Is.EqualTo(child.threshold).Within(1e-4f), "Bent " + bend + "° at scale " + scale);
            }
        }

        // The clip of a pose puts its corrective fully on and the muscle's others off.
        var clip = (AnimationClip)triceps.children.Single(c => ((AnimationClip)c.motion).name.EndsWith("bend_90", StringComparison.Ordinal)).motion;
        var curves = AnimationUtility.GetCurveBindings(clip).ToDictionary(b => b.propertyName, b => AnimationUtility.GetEditorCurve(clip, b).Evaluate(0f));
        Assert.That(curves["blendShape.bend_90"], Is.EqualTo(100f));
        Assert.That(curves["blendShape.bend_45"], Is.EqualTo(0f));
        Assert.That(AnimationUtility.GetCurveBindings(clip).All(b => b.path == "Body"), Is.True);
    }

    [Test]
    public void TheTwistReadingFollowsTheTwistAloneThroughTheAimPivot()
    {
        var result = Generate();
        var pivot = Components(upper, "VRCAimConstraint").Single();
        Assert.That(pivot.transform.parent, Is.EqualTo(upper));
        Assert.That(Field<Vector3>(pivot, "AimAxis"), Is.EqualTo(Vector3.up));
        Assert.That(Field<Vector3>(pivot, "UpAxis"), Is.EqualTo(Vector3.right));
        Assert.That(Field<object>(pivot, "WorldUp").ToString(), Is.EqualTo("ObjectRotationUp"));
        Assert.That(Field<Transform>(pivot, "WorldUpTransform"), Is.EqualTo(upper));
        var receiver = Contact(upper, "VRCContactReceiver", "biceps.L 2");
        var sender = Contact(lower, "VRCContactSender", "biceps.L 2");
        Assert.That(receiver.transform.parent, Is.EqualTo(pivot.transform));

        var biceps = ((BlendTree)result.Controller.layers[0].stateMachine.defaultState.motion).children
            .Select(child => (BlendTree)child.motion).Single(tree => tree.name == "biceps.L");
        Assert.That(biceps.blendType, Is.EqualTo(BlendTreeType.FreeformCartesian2D));
        Assert.That(biceps.blendParameter, Is.EqualTo("MCB/XM/biceps.L/1"));
        Assert.That(biceps.blendParameterY, Is.EqualTo("MCB/XM/biceps.L/2"));
        Assert.That(biceps.children.Length, Is.EqualTo(Bends.Length * Twists.Length));
        var stretchReceiver = Contact(upper, "VRCContactReceiver", "biceps.L 1");
        var stretchSender = Contact(lower, "VRCContactSender", "biceps.L 1");

        foreach (float scale in new[] { 1f, 2.5f })
        {
            avatar.transform.localScale = Vector3.one * scale;
            foreach (var pose in Grid())
            {
                Pose(pose.bend, pose.twist);
                EvaluateConstraints(pivot);
                var child = biceps.children.Single(c => ((AnimationClip)c.motion).name.EndsWith(pose.shape, StringComparison.Ordinal));
                Assert.That(Reading(stretchReceiver, stretchSender), Is.EqualTo(child.position.x).Within(1e-4f), pose.shape + " stretch at scale " + scale);
                Assert.That(Reading(receiver, sender), Is.EqualTo(child.position.y).Within(1e-3f), pose.shape + " twist at scale " + scale);
            }
            // The twist reading does not move with the bend.
            Pose(120f, 30f);
            EvaluateConstraints(pivot);
            float bent = Reading(receiver, sender);
            Pose(10f, 30f);
            EvaluateConstraints(pivot);
            Assert.That(Reading(receiver, sender), Is.EqualTo(bent).Within(1e-3f));
        }
        Pose(0f, 0f);
        EvaluateConstraints(pivot);
    }

    [Test]
    public void AnExportReplacesOnlyTheMusclesOfTheMeshesItExported()
    {
        MuscleCorrective Muscle(string name, string mesh, int sensors) => new MuscleCorrective
        {
            name = name,
            sensors = Enumerable.Range(0, sensors).Select(_ => Stretch()).ToList(),
            samples = { new MuscleCorrectiveSample { shapeKey = name + "_s", mesh = mesh, distances = Enumerable.Repeat(0.3f, sensors).ToList() } }
        };
        var existing = new MuscleCorrectiveSet { apiVersion = MuscleCorrectiveSet.ApiVersion, muscles = { Muscle("elbow", "Body", 1), Muscle("jaw", "Head", 1) } };
        var incoming = new MuscleCorrectiveSet { apiVersion = MuscleCorrectiveSet.ApiVersion, muscles = { Muscle("knee", "Body", 2) } };
        var merged = MuscleCorrectiveStore.Merge(existing, incoming, new[] { "Body" });
        Assert.That(merged.muscles.Select(m => m.name), Is.EquivalentTo(new[] { "jaw", "knee" }), "Body was exported again: its old elbow is gone.");
        Assert.That(MuscleCorrectiveStore.ContactCount(merged), Is.EqualTo(6), "Two contacts per sensor.");
        Assert.That(MuscleCorrectiveStore.ShapeCount(merged), Is.EqualTo(2));
        Assert.That(MuscleCorrectiveStore.Merge(merged, new MuscleCorrectiveSet(), new[] { "Head" }).muscles.Select(m => m.name),
            Is.EqualTo(new[] { "knee" }), "An export of Head without muscles removes Head's.");
    }

    [Test]
    public void GeneratingAgainReplacesTheRigAndMissingPiecesAreReported()
    {
        Generate();
        Generate();
        Assert.That(Components(avatar.transform, "VRCContactReceiver").Length, Is.EqualTo(3));
        Assert.That(Components(avatar.transform, "VRCAimConstraint").Length, Is.EqualTo(1));
        Assert.That(avatar.transform.Cast<Transform>().Count(child => child.name == MuscleDriverGenerator.RootName), Is.EqualTo(1));

        var set = Bake();
        set.muscles[1].samples.Add(new MuscleCorrectiveSample { shapeKey = "missing_shape", mesh = "Body", distances = { 0.05f } });
        set.muscles.Add(new MuscleCorrective
        {
            name = "knee", sensors = { new MuscleSensor { receiver = { bone = "NoSuchBone" }, sender = { bone = "LowerArm.L" } } },
            samples = { new MuscleCorrectiveSample { shapeKey = "bend_0", mesh = "Body", distances = { 0.3f } } }
        });
        set.muscles.Add(new MuscleCorrective { name = "old", samples = { new MuscleCorrectiveSample { shapeKey = "bend_0", mesh = "Body" } } });
        var result = Generate(set);
        Assert.That(result.Muscles, Is.EqualTo(2));
        Assert.That(result.Warnings.Any(w => w.Contains("missing_shape")), Is.True);
        Assert.That(result.Warnings.Any(w => w.Contains("NoSuchBone")), Is.True);
        Assert.That(result.Warnings.Any(w => w.Contains("‘old’") && w.Contains("bake it again")), Is.True);
        Assert.That(Components(avatar.transform, "VRCContactReceiver").Length, Is.EqualTo(3), "The knee left nothing behind.");

        MuscleDriverGenerator.Remove(avatar);
        Assert.That(Components(avatar.transform, "VRCContactReceiver"), Is.Empty);
        Assert.That(Components(avatar.transform, "VRCAimConstraint"), Is.Empty);
        Assert.That(avatar.transform.Find(MuscleDriverGenerator.RootName), Is.Null);
    }
}
#endif
