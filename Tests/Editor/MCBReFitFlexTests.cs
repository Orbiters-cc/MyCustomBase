using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class MCBReFitFlexTests
{
    private GameObject root;
    private SkinnedMeshRenderer body;
    private SkinnedMeshRenderer accessory;
    private Mesh bodyMesh;
    private Mesh accessoryMesh;
    private RefitAppliedMeshEntry entry;
    private UnityEngine.SceneManagement.Scene scene;
    private string folder;

    [SetUp]
    public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Flex test avatar");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        bodyMesh = MakeMesh("Body", "orbit muscles", "biceps flex right", "FLEX left", "Smile");
        accessoryMesh = MakeMesh("Accessory", "orbit muscles", "refit_biceps flex right", "FLEX left", "Native flex");
        body = AddRenderer("Body", bodyMesh);
        accessory = AddRenderer("Accessory", accessoryMesh);
        entry = new RefitAppliedMeshEntry
        {
            rendererPath = "Accessory", refitMesh = accessoryMesh,
            transferredBlendShapeSourceNames = new List<string> { "orbit muscles", "biceps flex right", "FLEX left" },
            transferredBlendShapeNames = new List<string> { "orbit muscles", "refit_biceps flex right", "FLEX left" }
        };
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(root);
        if (folder != null) AssetDatabase.DeleteAsset(folder);
        Object.DestroyImmediate(bodyMesh);
        Object.DestroyImmediate(accessoryMesh);
        EditorSceneManager.ClosePreviewScene(scene);
    }

    [Test]
    public void SelectionAddsMeshOnlyFlexShapesWithoutDuplicatingExposedShapes()
    {
        var version = new CustomBaseVersion { customBlendshapes = new[]
        {
            new CustomBlendshapeEntry { name = "orbit muscles" }, null,
            new CustomBlendshapeEntry { name = "FLEX left" },
            new CustomBlendshapeEntry { name = "orbit muscles" }
        } };
        Assert.That(MCBReFitIntegration.CollectRefitBlendShapeNames(version, bodyMesh),
            Is.EqualTo(new[] { "orbit muscles", "FLEX left", "biceps flex right" }));
        Assert.That(MCBReFitIntegration.CollectRefitBlendShapeNames(null, bodyMesh),
            Is.EqualTo(new[] { "biceps flex right", "FLEX left" }));
        Assert.That(MCBReFitIntegration.CollectRefitBlendShapeNames(null, null), Is.Empty);
    }

    [TestCase("flex biceps", true)]
    [TestCase("biceps FLEX right", true)]
    [TestCase("prefixflexsuffix", true)]
    [TestCase("orbit muscles", false)]
    [TestCase(null, false)]
    public void FlexSelectionIsCaseInsensitiveSubstring(string name, bool expected)
    {
        Assert.That(MCBReFitIntegration.IsFlexBlendShape(name), Is.EqualTo(expected));
    }

    [Test]
    public void LinksUseRecordedExactMappingsNotEveryAccessoryShape()
    {
        entry.transferredBlendShapeSourceNames.AddRange(new[] { "missing flex", "biceps flex right", "biceps flex right" });
        entry.transferredBlendShapeNames.AddRange(new[] { "Native flex", "missing generated", "refit_biceps flex right" });
        var mappings = MCBReFitIntegration.GetTransferredFlexMappings(entry, body, accessory);
        Assert.That(mappings.Select(p => p.Key), Is.EqualTo(new[] { "biceps flex right", "FLEX left" }));
        Assert.That(mappings.Select(p => p.Value), Is.EqualTo(new[] { "refit_biceps flex right", "FLEX left" }));
        accessory.gameObject.SetActive(false); // An outfit toggle can enable it at runtime.
        Assert.That(MCBReFitIntegration.GetTransferredFlexMappings(entry, body, accessory).Count, Is.EqualTo(2));
        accessory.sharedMesh = bodyMesh; // ReFit disabled/replaced by the user.
        Assert.That(MCBReFitIntegration.GetTransferredFlexMappings(entry, body, accessory), Is.Empty);
    }

    [Test]
    public void ConflictingSourcesAreRejected()
    {
        entry.transferredBlendShapeNames[2] = entry.transferredBlendShapeNames[1];
        Assert.Throws<InvalidOperationException>(() => MCBReFitIntegration.GetTransferredFlexMappings(entry, body, accessory));
    }

    [Test]
    public void DirectLinksCopyAnimatedCurvesThroughNestedTreesWithoutTouchingAuthoringClips()
    {
        ConfigureBuildTracking();
        var second = AddRenderer("Second accessory", accessoryMesh);
        root.GetComponent<MyCustomBase>().appliedRefits.Add(new RefitAppliedMeshEntry
        {
            rendererPath = second.name, refitMesh = accessoryMesh,
            transferredBlendShapeSourceNames = new List<string>(entry.transferredBlendShapeSourceNames),
            transferredBlendShapeNames = new List<string>(entry.transferredBlendShapeNames)
        });
        var controller = CreateController(true);
        var source = new AnimationClip { name = "Flex animation" };
        var curve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.4f, 85), new Keyframe(1, 20));
        curve.postWrapMode = WrapMode.PingPong;
        var sourceBinding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.biceps flex right");
        var destinationBinding = EditorCurveBinding.FloatCurve("Accessory", typeof(SkinnedMeshRenderer), "blendShape.refit_biceps flex right");
        AnimationUtility.SetEditorCurve(source, sourceBinding, curve);
        AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve("Unrelated", typeof(SkinnedMeshRenderer), "blendShape.FLEX left"), curve);
        AssetDatabase.CreateAsset(source, folder + "/source.anim");
        var tree = new BlendTree { name = "Nested", blendParameter = "Existing" };
        tree.children = new[] { new ChildMotion { motion = source, timeScale = 1 } };
        AssetDatabase.AddObjectToAsset(tree, controller);
        controller.AddParameter("Existing", AnimatorControllerParameterType.Float);
        var state = controller.layers[0].stateMachine.AddState("Flex");
        state.motion = tree;
        body.SetBlendShapeWeight(1, 63);
        var result = BlendShapeLinkService.Instance.ApplyReFitFlexLinks(root);
        Assert.That(result.success, Is.True, result.message);
        var output = (AnimationClip)tree.children[0].motion;
        Assert.That(output, Is.Not.SameAs(source));
        var copied = AnimationUtility.GetEditorCurve(output, destinationBinding);
        var original = AnimationUtility.GetEditorCurve(source, sourceBinding);
        Assert.That(copied, Is.Not.Null);
        Assert.That(copied.keys, Is.EqualTo(original.keys));
        Assert.That(copied.postWrapMode, Is.EqualTo(original.postWrapMode));
        foreach (float time in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            output.SampleAnimation(root, time);
            Assert.That(accessory.GetBlendShapeWeight(1), Is.EqualTo(body.GetBlendShapeWeight(1)).Within(0.0001f));
            Assert.That(second.GetBlendShapeWeight(1), Is.EqualTo(body.GetBlendShapeWeight(1)).Within(0.0001f));
        }
        Assert.That(AnimationUtility.GetEditorCurve(source, destinationBinding), Is.Null);
        Assert.That(AnimationUtility.GetEditorCurve(output, EditorCurveBinding.FloatCurve("Accessory", typeof(SkinnedMeshRenderer), "blendShape.FLEX left")), Is.Null);
        Assert.That(controller.parameters.Select(p => p.name), Is.EqualTo(new[] { "Existing" }));
        BlendShapeLinkService.Instance.ApplyReFitFlexLinks(root);
        Assert.That(tree.children[0].motion, Is.SameAs(output), "Repeated preprocess must not create another clip.");
    }

    [Test]
    public void AuthoringControllersAndSceneWeightsAreNeverModified()
    {
        ConfigureBuildTracking();
        var controller = CreateController(false);
        body.SetBlendShapeWeight(1, 64);
        Assert.That(BlendShapeLinkService.Instance.ApplyReFitFlexLinks(root).success, Is.False);
        Assert.That(controller.parameters, Is.Empty);
        Assert.That(accessory.GetBlendShapeWeight(1), Is.Zero);
    }

    [Test]
    public void CaptureSurvivesRendererReparentingAndMeshCloningOnBuildCopy()
    {
        ConfigureBuildTracking();
        GameObject clone = null;
        Mesh clonedMesh = null;
        try
        {
            clone = Object.Instantiate(root);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
            Assert.That(new MCBReFitLinkCaptureHook().callbackOrder, Is.LessThan(-10000));
            Assert.That(new MCBReFitLinkCaptureHook().OnPreprocessAvatar(clone), Is.True);
            var links = MCBReFitIntegration.GetBuildFlexLinks(clone);
            var copiedAccessory = clone.transform.Find("Accessory").GetComponent<SkinnedMeshRenderer>();
            clonedMesh = Object.Instantiate(accessoryMesh);
            copiedAccessory.sharedMesh = clonedMesh;
            copiedAccessory.name = "Moved accessory";
            copiedAccessory.transform.SetParent(clone.transform.Find("Body"), false);
            Assert.That(links.Count, Is.EqualTo(1));
            Assert.That(links[0].accessory, Is.SameAs(copiedAccessory));
            Assert.That(links[0].body, Is.SameAs(clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>()));
            Assert.That(root.GetComponents<Component>().Length, Is.EqualTo(2));
            var controller = CreateController(true, clone);
            var clip = new AnimationClip { name = "Source" };
            AssetDatabase.CreateAsset(clip, folder + "/source.anim");
            var binding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.biceps flex right");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 0, 1, 100));
            var state = controller.layers[0].stateMachine.AddState("Flex");
            state.motion = clip;
            Assert.That(BlendShapeLinkService.Instance.ApplyReFitFlexLinks(clone).success, Is.True);
            var output = (AnimationClip)state.motion;
            Assert.That(AnimationUtility.GetEditorCurve(output, EditorCurveBinding.FloatCurve(
                "Body/Moved accessory", typeof(SkinnedMeshRenderer), "blendShape.refit_biceps flex right")), Is.Not.Null);
            output.SampleAnimation(clone, 0.65f);
            Assert.That(copiedAccessory.GetBlendShapeWeight(1), Is.EqualTo(65).Within(0.001f));
            Assert.That(accessory.GetBlendShapeWeight(1), Is.Zero);
            accessory.sharedMesh = bodyMesh;
            Assert.That(MCBReFitIntegration.CollectBuildFlexLinks(root), Is.Empty);
        }
        finally
        {
            if (clone != null) Object.DestroyImmediate(clone);
            if (clonedMesh != null) Object.DestroyImmediate(clonedMesh);
        }
    }

    private void ConfigureBuildTracking()
    {
        folder = "Assets/MCB-FlexTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring(7));
        AssetDatabase.CreateAsset(bodyMesh, folder + "/body.asset");
        var prefab = PrefabUtility.SaveAsPrefabAsset(body.gameObject, folder + "/base.prefab");
        // Match an imported FBX: its base mesh is a sub-asset, not just an external prefab reference.
        var importedMesh = Object.Instantiate(bodyMesh);
        importedMesh.name = bodyMesh.name;
        AssetDatabase.AddObjectToAsset(importedMesh, prefab);
        var mcb = root.AddComponent<MyCustomBase>();
        mcb.baseFbxFiles.Add(prefab);
        mcb.appliedRefits.Add(entry);
    }

    [Test]
    public void FactorDrivenLinksStillUseTheirExistingWrapperPath()
    {
        ConfigureBuildTracking();
        var controller = CreateController(true);
        var clip = new AnimationClip { name = "Corrective source" };
        AssetDatabase.CreateAsset(clip, folder + "/source.anim");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer),
            "blendShape.biceps flex right"), AnimationCurve.Linear(0, 0, 1, 100));
        var state = controller.layers[0].stateMachine.AddState("Corrective");
        state.motion = clip;
        Assert.That(BlendShapeLinkService.Instance.UpsertFactorLinkConfig(root, body,
            CorrectiveActivationType.Blendshape, "biceps flex right", CorrectiveActivationType.Blendshape,
            "FLEX left", "ExistingFactor").success, Is.True);
        Assert.That(BlendShapeLinkService.Instance.ApplyConfiguredFactorLinks(root).success, Is.True);
        Assert.That(state.motion, Is.TypeOf<BlendTree>());
        Assert.That(((BlendTree)state.motion).blendParameter, Is.EqualTo("ExistingFactor"));
        Assert.That(controller.parameters.Select(p => p.name), Does.Contain("ExistingFactor"));
    }

    private AnimatorController CreateController(bool temporary, GameObject avatar = null)
    {
        string controllerFolder = folder;
        if (temporary)
        {
            AssetDatabase.CreateFolder(folder, "com.vrcfury.temp");
            controllerFolder += "/com.vrcfury.temp";
        }
        var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerFolder + "/fx.controller");
        (avatar ?? root).AddComponent<Animator>().runtimeAnimatorController = controller;
        return controller;
    }

    private SkinnedMeshRenderer AddRenderer(string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }

    private static Mesh MakeMesh(string name, params string[] shapes)
    {
        var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        foreach (string shape in shapes) mesh.AddBlendShapeFrame(shape, 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
        return mesh;
    }

}
