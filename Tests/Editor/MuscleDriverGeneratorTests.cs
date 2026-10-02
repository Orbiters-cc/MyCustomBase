#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// XMuscles driving: contacts placed on the joint read how bent it is, and the 1D blend trees put each corrective at the
// reading its bake angle gives. Checked against the real geometry of a rotated arm, at two avatar scales.
public class MuscleDriverGeneratorTests
{
    private string folder;
    private GameObject avatar;
    private Transform upper, lower, hand;
    private SkinnedMeshRenderer body;
    private Mesh mesh;

    [SetUp]
    public void SetUp()
    {
        folder = "Assets/MCB_XMuscleTest_" + Guid.NewGuid().ToString("N");
        avatar = new GameObject("XMuscle Test Avatar");
        var armature = new GameObject("Armature").transform;
        armature.SetParent(avatar.transform, false);
        upper = Bone("UpperArm.L", armature, new Vector3(0.2f, 1.4f, 0f));
        lower = Bone("LowerArm.L", upper, new Vector3(0.45f, 1.4f, 0f));
        hand = Bone("Hand.L", lower, new Vector3(0.7f, 1.4f, 0f));
        var bodyObject = new GameObject("Body");
        bodyObject.transform.SetParent(avatar.transform, false);
        body = bodyObject.AddComponent<SkinnedMeshRenderer>();
        mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        foreach (string shape in new[] { "elbow_45", "elbow_90", "elbow_135" })
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

    private static Transform Bone(string name, Transform parent, Vector3 worldPosition)
    {
        var bone = new GameObject(name).transform;
        bone.SetParent(parent, false);
        bone.position = worldPosition;
        return bone;
    }

    private static MuscleCorrectiveSet Elbow() => new MuscleCorrectiveSet
    {
        apiVersion = 1,
        muscles =
        {
            new MuscleCorrective
            {
                name = "elbow.L", bone = "LowerArm.L", axis = "Z",
                samples =
                {
                    new MuscleCorrectiveSample { shapeKey = "elbow_45", mesh = "Body", angleDeg = 45f },
                    new MuscleCorrectiveSample { shapeKey = "elbow_90", mesh = "Body", angleDeg = 90f },
                    new MuscleCorrectiveSample { shapeKey = "elbow_135", mesh = "Body", angleDeg = 135f },
                }
            }
        }
    };

    private MuscleDriverGenerator.Result Generate() => MuscleDriverGenerator.Generate(avatar, Elbow(),
        new Dictionary<string, SkinnedMeshRenderer> { { "Body", body } }, folder + "/XMuscles.controller");

    // The tests' assembly does not reference VRChat's contact components: they are read by name.
    private static Component[] Contacts(Component root, string type) =>
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

    // What VRChat's proximity receiver would read: 1 at its centre, 0 at its edge (its radius scales with the transform).
    private static float Reading(Component receiver, Component sender)
    {
        float radius = Field<float>(receiver, "radius") * receiver.transform.lossyScale.x;
        return Mathf.Clamp01(1f - Vector3.Distance(receiver.transform.position, sender.transform.position) / radius);
    }

    [Test]
    public void TheReadingFollowsTheBendAtAnyScaleAndEachCorrectiveSitsAtItsAngle()
    {
        var result = Generate();
        Assert.That(result.Warnings, Is.Empty, string.Join("\n", result.Warnings));
        Assert.That(result.Muscles, Is.EqualTo(1));
        Assert.That(result.Contacts, Is.EqualTo(2));
        var sender = Contacts(lower, "VRCContactSender").Single();
        var receiver = Contacts(upper, "VRCContactReceiver").Single();
        Assert.That(sender.transform.parent, Is.EqualTo(lower), "The sender rides the bending bone.");
        Assert.That(receiver.transform.parent, Is.EqualTo(upper));
        Assert.That(Field<object>(receiver, "receiverType").ToString(), Is.EqualTo("Proximity"));
        Assert.That(Field<string>(receiver, "parameter"), Is.EqualTo("MCB/XM/elbow.L"));
        Assert.That(Field<bool>(receiver, "allowSelf"), Is.True);
        Assert.That(Field<bool>(receiver, "allowOthers"), Is.False);
        Assert.That(Field<List<string>>(receiver, "collisionTags"), Is.EqualTo(Field<List<string>>(sender, "collisionTags")));

        Assert.That(MuscleDriverGenerator.TryJoint(lower, out var joint), Is.True);
        foreach (float scale in new[] { 1f, 2.5f })
        {
            avatar.transform.localScale = Vector3.one * scale;
            foreach (float angle in new[] { 0f, 45f, 90f, 135f })
            {
                lower.localRotation = Quaternion.Euler(0f, 0f, angle);
                Assert.That(Reading(receiver, sender), Is.EqualTo(joint.Proximity(angle)).Within(0.01f), "Bent " + angle + "° at scale " + scale);
            }
        }

        var tree = (BlendTree)((BlendTree)result.Controller.layers[0].stateMachine.defaultState.motion).children.Single().motion;
        Assert.That(tree.blendParameter, Is.EqualTo("MCB/XM/elbow.L"));
        var thresholds = tree.children.Select(child => child.threshold).ToArray();
        Assert.That(thresholds, Is.Ordered.Ascending);
        Assert.That(thresholds.Length, Is.EqualTo(4), "Rest and the three correctives.");
        Assert.That(thresholds[2], Is.EqualTo(joint.Proximity(90f)).Within(1e-4f));

        // At 90°, the 90° corrective is fully on and the others are off.
        var clip = (AnimationClip)tree.children[2].motion;
        var curves = AnimationUtility.GetCurveBindings(clip).ToDictionary(b => b.propertyName, b => AnimationUtility.GetEditorCurve(clip, b).Evaluate(0f));
        Assert.That(curves["blendShape.elbow_90"], Is.EqualTo(100f));
        Assert.That(curves["blendShape.elbow_45"], Is.EqualTo(0f));
        Assert.That(AnimationUtility.GetCurveBindings(clip).All(b => b.path == "Body"), Is.True);
    }

    [Test]
    public void AnExportReplacesOnlyTheMusclesOfTheMeshesItExported()
    {
        MuscleCorrective Muscle(string name, string mesh) => new MuscleCorrective
        {
            name = name, bone = "B", samples = { new MuscleCorrectiveSample { shapeKey = name + "_s", mesh = mesh, angleDeg = 30f } }
        };
        var existing = new MuscleCorrectiveSet { apiVersion = 1, muscles = { Muscle("elbow", "Body"), Muscle("jaw", "Head") } };
        var incoming = new MuscleCorrectiveSet { apiVersion = 1, muscles = { Muscle("knee", "Body") } };
        var merged = MuscleCorrectiveStore.Merge(existing, incoming, new[] { "Body" });
        Assert.That(merged.muscles.Select(m => m.name), Is.EquivalentTo(new[] { "jaw", "knee" }), "Body was exported again: its old elbow is gone.");
        Assert.That(MuscleCorrectiveStore.ContactCount(merged), Is.EqualTo(4));
        Assert.That(MuscleCorrectiveStore.ShapeCount(merged), Is.EqualTo(2));
        Assert.That(MuscleCorrectiveStore.Merge(merged, new MuscleCorrectiveSet(), new[] { "Head" }).muscles.Select(m => m.name),
            Is.EqualTo(new[] { "knee" }), "An export of Head without muscles removes Head's.");
    }

    [Test]
    public void GeneratingAgainReplacesTheRigAndMissingPiecesAreReported()
    {
        Generate();
        Generate();
        Assert.That(Contacts(avatar.transform, "VRCContactReceiver").Length, Is.EqualTo(1));
        Assert.That(avatar.transform.Cast<Transform>().Count(child => child.name == MuscleDriverGenerator.RootName), Is.EqualTo(1));

        var set = Elbow();
        set.muscles[0].samples.Add(new MuscleCorrectiveSample { shapeKey = "missing_shape", mesh = "Body", angleDeg = 20f });
        set.muscles.Add(new MuscleCorrective { name = "knee", bone = "NoSuchBone", samples = { new MuscleCorrectiveSample { shapeKey = "k", mesh = "Body", angleDeg = 30f } } });
        var result = MuscleDriverGenerator.Generate(avatar, set, new Dictionary<string, SkinnedMeshRenderer> { { "Body", body } }, folder + "/XMuscles.controller");
        Assert.That(result.Muscles, Is.EqualTo(1));
        Assert.That(result.Warnings.Any(w => w.Contains("missing_shape")), Is.True);
        Assert.That(result.Warnings.Any(w => w.Contains("NoSuchBone")), Is.True);

        MuscleDriverGenerator.Remove(avatar);
        Assert.That(Contacts(avatar.transform, "VRCContactReceiver"), Is.Empty);
        Assert.That(avatar.transform.Find(MuscleDriverGenerator.RootName), Is.Null);
    }
}
#endif
