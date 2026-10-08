using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
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

    // The clothing links copy body curves: the modes must be locked before they run, so clothing follows the locked values.
    [Test] public void ModeLocksRunAfterCorrectivesAndBeforeEveryClothingLink()
    {
        int Order(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("mcb") || a.GetName().Name.StartsWith("Orbiters"))
                .SelectMany(a => a.GetTypes()).Single(t => t.Name == name && t.GetProperty("callbackOrder") != null);
            return (int)type.GetProperty("callbackOrder").GetValue(Activator.CreateInstance(type, true));
        }
        int survey = Order("VersionCustomizationSurveyHook"), locks = Order("VersionCustomizationLockHook"), apply = Order("VersionCustomizationApplyHook");
        Assert.That(survey, Is.GreaterThan(-10000).And.LessThan(-9010), "After VRCFury, before face tracking renames its curves.");
        Assert.That(locks, Is.GreaterThan(Order("BlendShapeLinkPostVrcfuryHook")));
        foreach (string link in new[] { "RefitLinkHook", "SurfaceFollowHook", "AttachmentFinishHook" })
            Assert.That(locks, Is.LessThan(Order(link)), link + " copies body curves: it must see the locked ones.");
        Assert.That(apply, Is.GreaterThan(Order("AttachmentFinishHook")), "Pruning stays after every animation-adding step.");
    }

    [Test] public void ClothingLinksCopyTheLockedModeValuesAndOtherStepsAreReported()
    {
        var (root, owner, renderer, _, _) = ModeAvatar("Mode clothing test");
        var jacketMesh = Own(Object.Instantiate(renderer.sharedMesh));
        var jacket = Child(root.transform, "Jacket").gameObject.AddComponent<SkinnedMeshRenderer>(); jacket.sharedMesh = jacketMesh;
        var controller = Own(new AnimatorController()); controller.AddLayer("Gestures");
        var own = Own(new AnimationClip { name = "Own" });
        var female = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.ulti female");
        AnimationUtility.SetEditorCurve(own, female, AnimationCurve.Constant(0, 1, 0));
        controller.layers[0].stateMachine.AddState("Own").motion = own;
        root.AddComponent<Animator>().runtimeAnimatorController = controller;
        ModeService.Set(owner, "female", true); ModeService.Set(owner, "dogears", true);

        VersionCustomizationBuild.Capture(root);
        VersionCustomizationBuild.Survey(root);
        // The build works on its own copy of the avatar's controller.
        var built = (AnimatorController)root.GetComponent<Animator>().runtimeAnimatorController;
        Assert.That(built, Is.Not.SameAs(controller));
        // A later build step (face tracking) animates a locked shape the avatar's own animations did not.
        var added = Own(new AnimationClip { name = "Face tracking" });
        AnimationUtility.SetEditorCurve(added, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.dynamic dog ears"), AnimationCurve.Constant(0, 1, 30));
        built.AddLayer("Face tracking"); built.layers[built.layers.Length - 1].stateMachine.AddState("Track").motion = added;
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("dynamic dog ears.*overrides those animations"));
        VersionCustomizationBuild.Lock(root);
        // As ReFit's link hook does, after the lock.
        BlendShapeSync.Apply(root, new[] { new BlendShapeCopy { Source = renderer, SourceShape = "ulti female", Destination = jacket, DestinationShape = "ulti female" } }, "Test");

        Assert.That(jacket.GetBlendShapeWeight(0), Is.EqualTo(100), "Clothing takes the locked weight.");
        Assert.That(AnimationUtility.GetEditorCurve(own, female).Evaluate(.5f), Is.Zero, "The authored clip must never be mutated.");
        var locked = (AnimationClip)built.layers.Single(l => l.name == "MCB Modes").stateMachine.defaultState.motion;
        locked.SampleAnimation(root, .5f);
        Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(100));
        Assert.That(jacket.GetBlendShapeWeight(0), Is.EqualTo(100), "The locked value is copied onto the clothing shape.");
        var jacketFemale = EditorCurveBinding.FloatCurve("Jacket", typeof(SkinnedMeshRenderer), "blendShape.ulti female");
        Assert.That(built.animationClips.Where(c => c != locked).Any(c => AnimationUtility.GetEditorCurve(c, jacketFemale) != null), Is.False,
            "No unlocked body curve reaches the clothing.");
    }

    // Ultirex: 380 of the body's 488 blendshapes are never used and took 448 MB, over VRChat's upload limits.
    [Test] public void BuildRemovesTheCustomBaseUnusedBlendshapesAndBakesStaticOnes()
    {
        var (root, owner, renderer, _, _) = ModeAvatar("Blendshape pruning test");
        var source = renderer.sharedMesh;
        Vector3[] Offset(float y) => new[] { new Vector3(0, y, 0), Vector3.zero, Vector3.zero };
        source.AddBlendShapeFrame("old sculpt", 100, Offset(1), new Vector3[3], new Vector3[3]);
        source.AddBlendShapeFrame("jawline", 100, Offset(2), new Vector3[3], new Vector3[3]);
        renderer.SetBlendShapeWeight(3, 50);
        owner.nativeGeneratedRenderers.Add(renderer.gameObject);
        var jacketMesh = Own(Object.Instantiate(source));
        var jacket = Child(root.transform, "Jacket").gameObject.AddComponent<SkinnedMeshRenderer>(); jacket.sharedMesh = jacketMesh;
        var controller = Own(new AnimatorController()); controller.AddLayer("Base");
        root.AddComponent<Animator>().runtimeAnimatorController = controller;
        ModeService.Set(owner, "female", true); ModeService.Set(owner, "dogears", true);

        VersionCustomizationBuild.Capture(root); VersionCustomizationBuild.Apply(root);

        var built = Own(renderer.sharedMesh);
        Assert.That(built, Is.Not.SameAs(source));
        Assert.That(Enumerable.Range(0, built.blendShapeCount).Select(built.GetBlendShapeName), Is.EqualTo(new[] { "ulti female", "dynamic dog ears" }),
            "Shapes the modes lock stay; the unused and the static ones leave.");
        Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(100));
        Assert.That(built.vertices[0].y - source.vertices[0].y, Is.EqualTo(1).Within(1e-5), "The static shape is baked at its weight (50% of 2).");
        Assert.That(source.blendShapeCount, Is.EqualTo(4), "The source mesh is never changed.");
        Assert.That(jacket.sharedMesh, Is.SameAs(jacketMesh), "Clothing is not part of the custom base.");
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
        Assert.That(TwistBoneService.Generate(root, new[] { TwistBoneService.Resolve(root.transform, config) }), Is.EqualTo(2));
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

    // Rexouium OneMesh: every renderer lists every bone. Only meshes weighted to a twist are copied, once for all twists.
    [Test] public void TwistCopiesOnlyWeightedRenderersOnceForAllTwists()
    {
        var root = Own(new GameObject("Twist test"));
        var head = Child(root.transform, "Head"); head.localPosition = Vector3.up * 3;
        Transform Arm(string side, float x)
        {
            var arm = Child(root.transform, side + " arm"); arm.localPosition = new Vector3(x, 0, 0);
            var elbow = Child(arm, side + " elbow"); elbow.localPosition = Vector3.up;
            Child(elbow, side + " wrist").localPosition = Vector3.up;
            return elbow;
        }
        var left = Arm("Left", -1); var right = Arm("Right", 1);
        var bones = new[] { left, right, head };
        Mesh Skin(params BoneWeight[] weights)
        {
            var mesh = MakeMesh(); mesh.vertices = weights.Select(w => bones[w.boneIndex0].position + Vector3.up * .5f).ToArray();
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix).ToArray(); mesh.boneWeights = weights;
            return mesh;
        }
        var bodyMesh = Skin(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 1, weight0 = 1 }, new BoneWeight { boneIndex0 = 2, weight0 = 1 });
        var featherMesh = Skin(new BoneWeight { boneIndex0 = 2, weight0 = 1 }, new BoneWeight { boneIndex0 = 2, weight0 = 1 }, new BoneWeight { boneIndex0 = 2, weight0 = 1 });
        var body = Child(root.transform, "Body").gameObject.AddComponent<SkinnedMeshRenderer>(); body.sharedMesh = bodyMesh; body.bones = bones;
        var feathers = Child(root.transform, "Feathers").gameObject.AddComponent<SkinnedMeshRenderer>(); feathers.sharedMesh = featherMesh; feathers.bones = bones;
        var targets = new[] { "Left", "Right" }.Select(side => TwistBoneService.Resolve(root.transform, new TwistBoneConfiguration
            { bone = side + " arm/" + side + " elbow", aim = side + " arm/" + side + " elbow/" + side + " wrist", up = side + " arm" })).ToArray();

        Assert.That(TwistBoneService.Generate(root, targets), Is.EqualTo(1));
        Own(body.sharedMesh);
        Assert.That(feathers.sharedMesh, Is.SameAs(featherMesh)); Assert.That(feathers.bones.Length, Is.EqualTo(3));
        Assert.That(body.sharedMesh, Is.Not.SameAs(bodyMesh)); Assert.That(body.sharedMesh.name, Is.EqualTo(bodyMesh.name + " (MCB Twist)"));
        Assert.That(body.bones.Length, Is.EqualTo(5)); Assert.That(body.sharedMesh.bindposes.Length, Is.EqualTo(5));
        Assert.That(body.bones.Skip(3).Select(b => b.name), Is.EqualTo(new[] { "ZMCB_Left elbow_Twist", "ZMCB_Right elbow_Twist" }));
        var baked = Own(new Mesh()); body.BakeMesh(baked);
        for (int i = 0; i < 3; i++) Assert.That(Vector3.Distance(baked.vertices[i], bodyMesh.vertices[i]), Is.LessThan(.00001f), "Rest pose must not move.");
    }
    // Ultirex's elbows: a twist on a vertex with four influences gives it a fifth. Unity narrows the skin channels to
    // 16 bits then, and logged "Unsupported conversion of vertex data" converting the old float weights.
    [Test] public void TwistsPastFourInfluencesKeepTheRestOfTheMeshAndLogNothing()
    {
        var root = Own(new GameObject("Twist test"));
        Transform Arm(string side, float x)
        {
            var arm = Child(root.transform, side + " arm"); arm.localPosition = new Vector3(x, 0, 0);
            var elbow = Child(arm, side + " elbow"); elbow.localPosition = Vector3.up;
            Child(elbow, side + " wrist").localPosition = Vector3.up;
            return elbow;
        }
        var left = Arm("Left", -1); var right = Arm("Right", 1);
        var spine = Child(root.transform, "Spine"); var head = Child(spine, "Head"); head.localPosition = Vector3.up * 3;
        var bones = new[] { left, right, spine, head };
        var mesh = MakeMesh();
        mesh.vertices = new[] { left.position + Vector3.up * .5f, right.position + Vector3.up * .5f, head.position };
        mesh.uv2 = new[] { Vector2.one, Vector2.up, Vector2.right };
        mesh.colors = new[] { Color.red, Color.green, Color.blue };
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix).ToArray();
        mesh.boneWeights = new[]
        {
            new BoneWeight { boneIndex0 = 0, weight0 = .4f, boneIndex1 = 1, weight1 = .3f, boneIndex2 = 2, weight2 = .2f, boneIndex3 = 3, weight3 = .1f },
            new BoneWeight { boneIndex0 = 1, weight0 = .4f, boneIndex1 = 0, weight1 = .3f, boneIndex2 = 2, weight2 = .2f, boneIndex3 = 3, weight3 = .1f },
            new BoneWeight { boneIndex0 = 3, weight0 = .4f, boneIndex1 = 2, weight1 = .3f, boneIndex2 = 0, weight2 = .2f, boneIndex3 = 1, weight3 = .1f },
        };
        var smile = new[] { Vector3.up, Vector3.zero, Vector3.right };
        mesh.AddBlendShapeFrame("smile", 100, smile, new Vector3[3], new Vector3[3]);
        var body = Child(root.transform, "Body").gameObject.AddComponent<SkinnedMeshRenderer>(); body.sharedMesh = mesh; body.bones = bones;
        var targets = new[] { "Left", "Right" }.Select(side => TwistBoneService.Resolve(root.transform, new TwistBoneConfiguration
            { bone = side + " arm/" + side + " elbow", aim = side + " arm/" + side + " elbow/" + side + " wrist", up = side + " arm" })).ToArray();

        var conversions = new List<string>();
        void Watch(string condition, string stack, LogType type) { if (condition.Contains("vertex data")) conversions.Add(condition); }
        Application.logMessageReceived += Watch;
        try { Assert.That(TwistBoneService.Generate(root, targets), Is.EqualTo(1)); }
        finally { Application.logMessageReceived -= Watch; }
        var twisted = Own(body.sharedMesh);
        Assert.That(conversions, Is.Empty);
        Assert.That(twisted.GetBonesPerVertex().ToArray(), Is.EqualTo(new byte[] { 6, 6, 4 }), "Each elbow vertex takes both twists.");
        var weights = twisted.GetAllBoneWeights().ToArray();
        foreach (int twist in new[] { 4, 5 }) Assert.That(weights.Where(w => w.boneIndex == twist).Sum(w => w.weight), Is.GreaterThan(.1f));
        var top = twisted.boneWeights[0];
        Assert.That(new[] { top.boneIndex0, top.boneIndex1 }, Is.EqualTo(new[] { 4, 5 }), "Both twists are among the four skinned influences.");
        Assert.That(twisted.uv2, Is.EqualTo(mesh.uv2)); Assert.That(twisted.colors, Is.EqualTo(mesh.colors));
        Assert.That(twisted.triangles, Is.EqualTo(mesh.triangles)); Assert.That(twisted.bindposes.Length, Is.EqualTo(6));
        var deltas = new Vector3[3]; twisted.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
        Assert.That(twisted.blendShapeCount, Is.EqualTo(1)); Assert.That(deltas, Is.EqualTo(smile));
        var baked = Own(new Mesh()); body.BakeMesh(baked);
        for (int i = 0; i < 3; i++) Assert.That(Vector3.Distance(baked.vertices[i], mesh.vertices[i]), Is.LessThan(.0001f), "Rest pose must not move.");
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
