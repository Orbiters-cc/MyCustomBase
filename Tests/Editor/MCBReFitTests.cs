using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>MCB's custom base as the Orbiters refit tools see it: its body, blendshapes and original base.</summary>
public sealed class MCBReFitTests
{
    private GameObject root;
    private SkinnedMeshRenderer body, accessory;
    private Mesh bodyMesh, accessoryMesh;
    private MyCustomBase mcb;
    private GameObject basePrefab;
    private UnityEngine.SceneManagement.Scene scene;
    private string folder;

    [SetUp]
    public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("ReFit test avatar");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        bodyMesh = MakeMesh("Body", "orbit muscles", "biceps flex right", "FLEX left", "Smile");
        accessoryMesh = MakeMesh("Accessory", "Smile");
        body = AddRenderer("Body", bodyMesh);
        accessory = AddRenderer("Accessory", accessoryMesh);
        folder = "Assets/MCB-ReFitTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring(7));
        AssetDatabase.CreateAsset(bodyMesh, folder + "/body.asset");
        basePrefab = PrefabUtility.SaveAsPrefabAsset(body.gameObject, folder + "/base.prefab");
        // Match an imported FBX: its base mesh is a sub-asset, not just an external prefab reference.
        var importedMesh = Object.Instantiate(bodyMesh);
        importedMesh.name = bodyMesh.name;
        AssetDatabase.AddObjectToAsset(importedMesh, basePrefab);
        mcb = root.AddComponent<MyCustomBase>();
        mcb.baseFbxFiles.Add(basePrefab);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(root);
        AssetDatabase.DeleteAsset(folder);
        Object.DestroyImmediate(accessoryMesh);
        EditorSceneManager.ClosePreviewScene(scene);
    }

    [Test]
    public void SelectionAddsMeshOnlyFlexShapesWithoutDuplicatingExposedShapes()
    {
        mcb.appliedCustomBaseVersion = new CustomBaseVersion { customBlendshapes = new[]
        {
            new CustomBlendshapeEntry { name = "orbit muscles" }, null,
            new CustomBlendshapeEntry { name = "FLEX left" },
            new CustomBlendshapeEntry { name = "orbit muscles" }
        } };
        Assert.That(MCBReFitIntegration.CollectRefitBlendShapeNames(mcb, bodyMesh), Is.EqualTo(new[] { "orbit muscles", "FLEX left", "biceps flex right" }));
        // After a reload the version object is gone: its serialized blendshape cache still names the shapes.
        mcb.appliedCustomBaseVersion = null;
        mcb.appliedVersionBlendshapeLinksCache = new List<CreatorBlendshapeEntry> { new CreatorBlendshapeEntry { name = "orbit muscles" } };
        Assert.That(MCBReFitIntegration.CollectRefitBlendShapeNames(mcb, bodyMesh), Is.EqualTo(new[] { "orbit muscles", "biceps flex right", "FLEX left" }));
        Assert.That(MCBReFitIntegration.CollectRefitBlendShapeNames(null, null), Is.Empty);
    }

    [Test]
    public void TheAppliedCustomBaseIsDescribedForTheOtherTools()
    {
        Assert.That(MCBReFitIntegration.Describe(mcb), Is.Null, "The original base is not a custom base.");
        Assert.That(CustomBases.Describe(root.transform), Is.Null);

        mcb.appliedCustomBaseAssetId = 14;
        mcb.appliedCustomBaseName = "Muscle Orbit";
        mcb.appliedCustomBaseVersionString = "0.5.0";
        mcb.appliedCustomBaseDefaultAviVersion = "1.0.0";
        mcb.appliedVersionBlendshapeLinksCache = new List<CreatorBlendshapeEntry> { new CreatorBlendshapeEntry { name = "orbit muscles" } };
        var info = CustomBases.Describe(root.transform);
        Assert.That(info, Is.Not.Null);
        Assert.That(info.Source, Is.EqualTo("MCB"));
        Assert.That(info.Body, Is.SameAs(body));
        Assert.That(info.Name, Is.EqualTo("Muscle Orbit 0.5.0"));
        Assert.That(info.Key, Is.EqualTo("mcb:14:0.5.0|1.0.0"));
        Assert.That(info.Shapes, Is.EqualTo(new[] { "orbit muscles", "biceps flex right", "FLEX left" }));
        Assert.That(info.CanFit, Is.True);

        using (var original = info.ResolveOriginal())
        {
            Assert.That(original.Avatar, Is.SameAs(basePrefab), "Without an .fbx.originalbase backup the base file is the original.");
            Assert.That(original.Body.sharedMesh, Is.SameAs(bodyMesh));
        }
    }

    [Test]
    public void CandidatesAreTheMeshesThatAreNotTheBaseBody()
    {
        var overlay = new GameObject("__XRayGizmos_WeightPaint");
        overlay.transform.SetParent(root.transform, false);
        overlay.AddComponent<SkinnedMeshRenderer>().sharedMesh = accessoryMesh;
        Assert.That(MCBReFitIntegration.GetRefitCandidates(mcb), Is.EqualTo(new[] { accessory }));
    }

    [Test]
    public void FactorDrivenLinksStillUseTheirExistingWrapperPath()
    {
        AssetDatabase.CreateFolder(folder, "com.vrcfury.temp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/com.vrcfury.temp/fx.controller");
        root.AddComponent<Animator>().runtimeAnimatorController = controller;
        var clip = new AnimationClip { name = "Corrective source" };
        AssetDatabase.CreateAsset(clip, folder + "/source.anim");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer),
            "blendShape.biceps flex right"), AnimationCurve.Linear(0, 0, 1, 100));
        var state = controller.layers[0].stateMachine.AddState("Corrective");
        state.motion = clip;
        Assert.That(BlendShapeLinkService.Instance.UpsertFactorLinkConfig(root, body,
            CorrectiveActivationType.Blendshape, "biceps flex right", CorrectiveActivationType.Blendshape,
            "FLEX left", "ExistingFactor").success, Is.True);
        Assert.That(BlendShapeLinkService.Instance.ApplyConfiguredFactorLinks(root).Success, Is.True);
        Assert.That(state.motion, Is.TypeOf<BlendTree>());
        Assert.That(((BlendTree)state.motion).blendParameter, Is.EqualTo("ExistingFactor"));
        Assert.That(controller.parameters.Select(p => p.name), Does.Contain("ExistingFactor"));
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
