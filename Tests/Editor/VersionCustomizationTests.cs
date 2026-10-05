using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

public class VersionCustomizationTests
{
    [Test] public void InvalidCustomizationCannotReachSceneMutation()
    {
        var config = new VersionCustomization { modes = null };
        Assert.Throws<ArgumentException>(() => config.Validate());
        config = new VersionCustomization();
        config.twistBones.Add(new TwistBoneConfiguration { bone = "Arm/Elbow", aim = "Arm/Elbow/Wrist", up = "Arm", upDirection = new TwistDirection { x = float.NaN } });
        Assert.Throws<ArgumentException>(() => config.Validate());
        config.twistBones.Clear();
        config.dynamicNormalBlendshapes.Add(new MeshBlendshapeSelection { mesh = "Body" });
        config.dynamicNormalBlendshapes.Add(new MeshBlendshapeSelection { mesh = "Body" });
        Assert.Throws<ArgumentException>(() => config.Validate());
        config = Configuration(); config.modes.options[1].enabledByDefault = true;
        Assert.Throws<ArgumentException>(() => config.Validate(), "An exclusive category needs exactly one default.");
        config = Configuration(); config.modes.options[0].category = "missing";
        Assert.Throws<ArgumentException>(() => config.Validate(), "A mode needs one of the version's categories.");
    }

    [Test] public void CustomMeshBonesAreTrackedAndUndoRestoresOriginalSkeletonAndRenderer()
    {
        var root = Own(new GameObject("Custom skeleton")); var owner = root.AddComponent<MyCustomBase>();
        var arm = Child(root.transform, "Arm");
        var renderer = Child(root.transform, "Body").gameObject.AddComponent<SkinnedMeshRenderer>();
        var original = MakeMesh(); original.bindposes = new[] { Matrix4x4.identity };
        renderer.sharedMesh = original; renderer.bones = new[] { arm };
        var replacement = MakeMesh(); replacement.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
        var payload = Own(ScriptableObject.CreateInstance<NativeMeshPayloadAsset>());
        payload.bones.Add(new NativeMeshPayloadBone { path = "Arm/Fluff", localPosition = Vector3.up, localRotation = Quaternion.identity, localScale = Vector3.one });
        payload.renderers.Add(new NativeMeshPayloadRenderer { avatarPath = "Body", mesh = replacement, rootBonePath = "Arm", localScale = Vector3.one, bonePaths = new List<string> { "Arm", "Arm/Fluff" } });
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        try
        {
            typeof(NativeMeshPayloadService).GetMethod("ApplyPayloadToAvatar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { root.transform, payload });
            Assert.That(owner.nativeMeshGeneratedBones.Count, Is.EqualTo(1));
            Assert.That(renderer.bones[1], Is.EqualTo(arm.Find("Fluff")));
            Assert.That(arm.Find("Fluff").localPosition, Is.EqualTo(Vector3.up));
            Undo.FlushUndoRecordObjects();
            Undo.RevertAllDownToGroup(group);
            Assert.That(arm.Find("Fluff"), Is.Null);
            Assert.That(renderer.sharedMesh, Is.SameAs(original));
            Assert.That(renderer.bones, Is.EqualTo(new[] { arm }));
            Assert.That(owner.nativeMeshGeneratedBones, Is.Empty);
        }
        finally { Undo.RevertAllDownToGroup(group); }
    }

    private readonly List<Object> owned = new List<Object>();

    [Test] public void CustomHierarchyUnpacksPrefabAndRestoresItsOriginalParent()
    {
        string path = "Assets/MCBSkeletonTest-" + Guid.NewGuid().ToString("N") + ".prefab";
        var source = Own(new GameObject("Skeleton")); source.AddComponent<MyCustomBase>();
        var chest = Child(source.transform, "Chest"); Child(chest, "ChestUp"); Child(chest, "Shoulder");
        var prefab = PrefabUtility.SaveAsPrefabAsset(source, path);
        var instance = Own((GameObject)PrefabUtility.InstantiatePrefab(prefab));
        var owner = instance.GetComponent<MyCustomBase>();
        var payload = Own(ScriptableObject.CreateInstance<NativeMeshPayloadAsset>());
        payload.renderers.Add(new NativeMeshPayloadRenderer { bonePaths = new List<string> { "Chest/ChestUp/Shoulder" } });
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        try
        {
            var ensure = typeof(NativeMeshPayloadService).GetMethod("EnsurePayloadBones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            ensure.Invoke(null, new object[] { instance.transform, payload });
            Assert.That(instance.transform.Find("Chest/ChestUp/Shoulder"), Is.Not.Null);
            Assert.That(owner.nativeMeshOriginalParents.Count, Is.EqualTo(1));
            NativeMeshPayloadService.RestoreOriginalBoneParents(owner);
            Assert.That(instance.transform.Find("Chest/Shoulder"), Is.Not.Null);
            Assert.That(owner.nativeMeshOriginalParents, Is.Empty);
            Undo.FlushUndoRecordObjects(); Undo.RevertAllDownToGroup(group);
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(instance), Is.True);
        }
        finally { Undo.RevertAllDownToGroup(group); AssetDatabase.DeleteAsset(path); }
    }
    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
    [TearDown] public void Cleanup() { foreach (var obj in owned.AsEnumerable().Reverse()) if (obj != null) Object.DestroyImmediate(obj); owned.Clear(); }

    [Test] public void TypedEntriesRoundTripWithoutLosingOtherKeysOrSharingMutableState()
    {
        var entries = new List<object> { "advancedMesh", JObject.Parse("{\"modes\":{\"categories\":[],\"options\":[]},\"unrelated\":{\"x\":[1,2]}}") };
        var config = Configuration();
        config.twistBones.Add(new TwistBoneConfiguration { bone = "Arm/Elbow", aim = "Arm/Elbow/Wrist", up = "Arm" });
        config.dynamicNormalBlendshapes.Add(new MeshBlendshapeSelection { mesh = "Body", names = new List<string> { "ulti female", "belly suck" } });
        config.Write(entries); config.modes.options[0].label = "Changed outside";
        var result = VersionCustomization.Read(JsonConvert.DeserializeObject<object[]>(JsonConvert.SerializeObject(entries)));
        result.Validate(); Assert.That(result.modes.options[0].label, Is.EqualTo("Male"));
        Assert.That(result.modes.options[2].animations, Is.EqualTo(new[] { "0123456789abcdef0123456789abcdef" }));
        Assert.That(ExtraCustomizationUtils.HasFlag(entries, "advancedMesh"));
        Assert.That(ExtraCustomizationUtils.GetObject<JObject>(entries, "unrelated")["x"].Count(), Is.EqualTo(2));
        result.twistBones[0].curve.keys[1].value = .7f;
        Assert.That(VersionCustomization.Read(entries).twistBones[0].curve.keys[1].value, Is.EqualTo(.3680387f));
    }

    [Test] public void FullCurveRoundTripPreservesEvaluationAndTangents()
    {
        var curve = TwistCurve.Ultirex().ToCurve(); var roundtrip = TwistCurve.FromCurve(curve);
        var copied = JsonConvert.DeserializeObject<TwistCurve>(JsonConvert.SerializeObject(roundtrip)).ToCurve();
        for (int i = 0; i <= 100; i++) Assert.That(copied.Evaluate(i / 100f), Is.EqualTo(curve.Evaluate(i / 100f)).Within(1e-6));
        Assert.That(copied.keys[1].outTangent, Is.EqualTo(.9305876f));
        Assert.That(copied.keys[1].outWeight, Is.EqualTo(.2487787f));
    }

    [TestCase("../Body")] [TestCase("/Body")] [TestCase("Armature//Elbow")] [TestCase("Armature\\Elbow")]
    public void PathsRejectAmbiguity(string path) => Assert.Throws<ArgumentException>(() => VersionCustomization.ValidatePath(path));

    [Test] public void ModesSwitchRestoreAndRememberChoicesPerAssetWithDefaultFallback()
    {
        var (root, owner, renderer, physics, ears) = ModeAvatar("Mode test");
        ModeService.Set(owner, "female", true); Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(100)); Assert.That(physics.activeSelf);
        ModeService.Set(owner, "male", true); Assert.That(renderer.GetBlendShapeWeight(0), Is.Zero); Assert.That(physics.activeSelf, Is.False);
        ModeService.Set(owner, "male", false);
        Assert.That(ModeService.EnabledIds(owner), Is.EqualTo(new[] { "male" }), "An exclusive category always keeps one mode on.");

        // Combinable modes add up: the animation poses a bone and sets a blendshape; turning it off restores the pose.
        ModeService.Set(owner, "female", true); ModeService.Set(owner, "dogears", true);
        Assert.That(ModeService.EnabledIds(owner), Is.EqualTo(new[] { "female", "dogears" }));
        Assert.That(ears.localPosition.y, Is.EqualTo(.3f).Within(1e-5)); Assert.That(renderer.GetBlendShapeWeight(1), Is.EqualTo(100));
        ModeService.Set(owner, "dogears", false);
        Assert.That(ears.localPosition.y, Is.EqualTo(.1f).Within(1e-5)); Assert.That(renderer.GetBlendShapeWeight(1), Is.Zero);
        ModeService.Set(owner, "dogears", true); ModeService.Restore(owner);
        Assert.That(renderer.GetBlendShapeWeight(0), Is.Zero); Assert.That(physics.activeSelf, Is.False); Assert.That(ears.localPosition.y, Is.EqualTo(.1f).Within(1e-5));

        string earsClip = owner.appliedCustomization.modes.options[2].animations[0];
        var data = new List<object>(); Configuration(earsClip).Write(data);
        ModeService.Install(owner, new CustomBaseVersion { extraCustomization = data.ToArray() });
        Assert.That(ModeService.EnabledIds(owner), Is.EqualTo(new[] { "female", "dogears" }), "Choices persist across versions.");
        var replacement = Configuration(earsClip); replacement.modes.options.RemoveAt(1); replacement.modes.options.Add(new ModeOption { id = "tail", label = "Tail", category = "body", enabledByDefault = true });
        data.Clear(); replacement.Write(data);
        ModeService.Restore(owner); ModeService.Install(owner, new CustomBaseVersion { extraCustomization = data.ToArray() });
        Assert.That(ModeService.EnabledIds(owner), Is.EqualTo(new[] { "male", "dogears", "tail" }), "A missing genre falls back to the default; new modes start with theirs.");
    }

    [Test] public void ModeLockRewritesPrivateControllerAndSurvivesPosingAnimation()
    {
        var (root, owner, renderer, physics, ears) = ModeAvatar("Mode build test");
        var controller = Own(new AnimatorController()); controller.AddLayer("PosingMode");
        var clip = Own(new AnimationClip()); var binding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.ulti female");
        var earsBinding = EditorCurveBinding.FloatCurve("Head/Ears", typeof(Transform), "m_LocalPosition.y");
        AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 0));
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Physics", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
        AnimationUtility.SetEditorCurve(clip, earsBinding, AnimationCurve.Constant(0, 1, .5f));
        controller.layers[0].stateMachine.AddState("Off").motion = clip; root.AddComponent<Animator>().runtimeAnimatorController = controller;
        ModeService.Set(owner, "female", true); ModeService.Set(owner, "dogears", true);
        VersionCustomizationBuild.Capture(root); VersionCustomizationBuild.Apply(root); VersionCustomizationBuild.Apply(root);
        var built = root.GetComponent<Animator>().runtimeAnimatorController.animationClips.Single(c => c.name == "MCB Modes"); built.SampleAnimation(root, .5f);
        Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(100)); Assert.That(renderer.GetBlendShapeWeight(1), Is.EqualTo(100)); Assert.That(physics.activeSelf);
        Assert.That(AnimationUtility.GetEditorCurve(built, earsBinding), Is.Null, "Bone poses stay in the scene; other ear animations keep working.");
        Assert.That(AnimationUtility.GetEditorCurve(clip, binding).Evaluate(.5f), Is.Zero, "The authored clip must never be mutated.");
    }

    private (GameObject root, MyCustomBase owner, SkinnedMeshRenderer renderer, GameObject physics, Transform ears) ModeAvatar(string name)
    {
        var root = Own(new GameObject(name)); var owner = root.AddComponent<MyCustomBase>();
        var mesh = MakeMesh();
        mesh.AddBlendShapeFrame("ulti female", 100, new Vector3[3], new Vector3[3], new Vector3[3]);
        mesh.AddBlendShapeFrame("dynamic dog ears", 100, new Vector3[3], new Vector3[3], new Vector3[3]);
        var body = new GameObject("Body"); body.transform.SetParent(root.transform); var renderer = body.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
        var physics = new GameObject("Physics"); physics.transform.SetParent(root.transform); physics.SetActive(false);
        var ears = Child(Child(root.transform, "Head"), "Ears"); ears.localPosition = new Vector3(0, .1f, 0);
        var clip = Own(new AnimationClip { name = "dog ears" });
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Head/Ears", typeof(Transform), "m_LocalPosition.y"), AnimationCurve.Constant(0, 1, .3f));
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.dynamic dog ears"), AnimationCurve.Constant(0, 1, 100));
        string path = "Assets/MCB Mode Test " + Guid.NewGuid().ToString("N") + ".anim";
        AssetDatabase.CreateAsset(Object.Instantiate(clip), path);
        clips.Add(path);
        owner.appliedCustomBaseAssetId = 42; owner.appliedCustomization = Configuration(AssetDatabase.AssetPathToGUID(path));
        return (root, owner, renderer, physics, ears);
    }
    private readonly List<string> clips = new List<string>();
    [TearDown] public void DeleteClips() { foreach (string path in clips) AssetDatabase.DeleteAsset(path); clips.Clear(); }

    [Test] public void TwistSplitsWeightsForBodyAndClothingWithDifferentBoneArraysAndKeepsSources()
    {
        var root = Own(new GameObject("Twist test")); var upper = Child(root.transform, "Arm"); var lower = Child(upper, "Elbow"); lower.localPosition = Vector3.up;
        var tip = Child(lower, "Wrist"); tip.localPosition = Vector3.up;
        var mesh = MakeMesh(); mesh.vertices = new[] { Vector3.up, Vector3.up * 1.5f, Vector3.up * 2 };
        mesh.bindposes = new[] { lower.worldToLocalMatrix, tip.worldToLocalMatrix };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
        var body = Child(root.transform, "Body").gameObject.AddComponent<SkinnedMeshRenderer>(); body.sharedMesh = mesh; body.bones = new[] { lower, tip };
        var clothingMesh = Own(Object.Instantiate(mesh)); clothingMesh.bindposes = new[] { tip.worldToLocalMatrix, lower.worldToLocalMatrix };
        clothingMesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 1, weight0 = 1 }, 3).ToArray();
        var clothing = Child(root.transform, "Jacket").gameObject.AddComponent<SkinnedMeshRenderer>(); clothing.sharedMesh = clothingMesh; clothing.bones = new[] { tip, lower };
        var config = new TwistBoneConfiguration { bone = "Arm/Elbow", aim = "Arm/Elbow/Wrist", up = "Arm" };
        Assert.That(TwistBoneService.Generate(root, TwistBoneService.Resolve(root.transform, config)), Is.EqualTo(2));
        Own(body.sharedMesh); Own(clothing.sharedMesh);
        foreach (var renderer in new[] { body, clothing })
        {
            Assert.That(renderer.bones.Length, Is.EqualTo(3)); Assert.That(renderer.sharedMesh.bindposes.Length, Is.EqualTo(3));
            var weights = renderer.sharedMesh.boneWeights;
            Assert.That(weights[0].boneIndex0, Is.EqualTo(2)); Assert.That(weights[0].weight0, Is.EqualTo(1));
            Assert.That(weights[1].weight0 + weights[1].weight1, Is.EqualTo(1).Within(1e-6));
            Assert.That(weights[2].boneIndex0, Is.EqualTo(Array.IndexOf(renderer.bones, lower)));
            var baked = Own(new Mesh()); renderer.BakeMesh(baked);
            for (int i = 0; i < 3; i++) Assert.That(Vector3.Distance(baked.vertices[i], mesh.vertices[i]), Is.LessThan(.00001f), "Rest pose must not move.");
        }
        Assert.That(mesh.bindposes.Length, Is.EqualTo(2)); Assert.That(mesh.boneWeights.All(w => w.weight0 == 1));
    }
    private Mesh MakeMesh() => Own(new Mesh { vertices = new Vector3[3], triangles = new[] { 0, 1, 2 } });
    private static Transform Child(Transform parent, string name) { var t = new GameObject(name).transform; t.SetParent(parent, false); return t; }
    private static VersionCustomization Configuration(string earsClip = "0123456789abcdef0123456789abcdef") => new VersionCustomization { modes = new ModeConfiguration
    {
        categories = new List<ModeCategory> { ModeCategory.Genre(), ModeCategory.Body() },
        options = new List<ModeOption>
        {
            new ModeOption { id = "male", label = "Male", category = "genre", enabledByDefault = true, gameObjects = new List<ModeGameObject> { new ModeGameObject { path = "Physics", active = false } } },
            new ModeOption { id = "female", label = "Female", category = "genre", blendshapes = new List<ModeBlendshape> { new ModeBlendshape { mesh = "Body", name = "ulti female", value = 100 } },
                gameObjects = new List<ModeGameObject> { new ModeGameObject { path = "Physics", active = true } } },
            new ModeOption { id = "dogears", label = "Dog ears", category = "body", animations = new List<string> { earsClip } },
        }
    } };
}
