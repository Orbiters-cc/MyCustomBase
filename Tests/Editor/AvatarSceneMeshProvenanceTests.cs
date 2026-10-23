#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class AvatarSceneMeshProvenanceTests
{
    private const string TestFolder = "Assets/__MCBSceneMeshProvenanceTests";
    private const string MeshPath = TestFolder + "/custom-mesh.asset";

    [Test]
    public void AutomaticDiscoveryDoesNotRecycleStoredSourcesWhenCurrentMeshesExist()
    {
        var root = new GameObject("Avatar");
        var stale = new GameObject("Stale source");
        try
        {
            var owner = root.AddComponent<MyCustomBase>();
            owner.baseFbxFiles.Add(stale);
            Assert.That(AvatarSceneMeshProvenanceService.GetAdditionalDiscoverySources(owner, true), Is.Empty);
            owner.specifyCustomBaseFbx = true;
            Assert.That(AvatarSceneMeshProvenanceService.GetAdditionalDiscoverySources(owner, true).Count(), Is.EqualTo(1));
        }
        finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(stale); }
    }

    // While Orbiters Mixer edits a mesh the renderer shows a working copy that is no asset: MCB found no base model for an
    // avatar whose meshes were all in a session, took an outfit's FBX for it and listed no custom base.
    [Test]
    public void AWorkingCopyStandsForTheModelFileOfItsOriginal()
    {
        const string model = "Packages/orbiters.toolkit/Tests/Editor/Fixtures/BlenderSkinned.fbx";
        if (!(AssetImporter.GetAtPath(model) is ModelImporter)) Assert.Ignore("Orbiters Toolkit's test model is not in this project.");
        if (!AssetDatabase.IsValidFolder(TestFolder))
        {
            AssetDatabase.CreateFolder("Assets", "__MCBSceneMeshProvenanceTests");
        }
        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        payload.sourceFbxPath = model;
        AssetDatabase.CreateAsset(payload, TestFolder + "/payload.asset");
        var applied = new Mesh { name = "Body" };
        AssetDatabase.AddObjectToAsset(applied, payload);
        var working = new Mesh { name = "Body (Mixer working)" };
        var previous = AvatarSceneMeshProvenanceService.WorkingMeshOriginal;
        try
        {
            AvatarSceneMeshProvenanceService.WorkingMeshOriginal = null;
            Assert.That(AvatarSceneMeshProvenanceService.ModelPathOf(working), Is.Null, "a working copy is no asset");
            AvatarSceneMeshProvenanceService.WorkingMeshOriginal = mesh => mesh == working ? applied : null;
            Assert.That(AvatarSceneMeshProvenanceService.ModelPathOf(working), Is.EqualTo(model), "through the working copy, the applied mesh's model");
            Assert.That(AvatarSceneMeshProvenanceService.ModelPathOf(applied), Is.EqualTo(model));
            var fbxMesh = AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().First();
            Assert.That(AvatarSceneMeshProvenanceService.ModelPathOf(fbxMesh), Is.EqualTo(model), "a model's own mesh");
        }
        finally
        {
            AvatarSceneMeshProvenanceService.WorkingMeshOriginal = previous;
            UnityEngine.Object.DestroyImmediate(working);
            AssetDatabase.DeleteAsset(TestFolder);
        }
    }

    [Test]
    public void BaseOwnedRendererWithMeshFromAnotherAssetIsReported()
    {
        if (!AssetDatabase.IsValidFolder(TestFolder))
        {
            AssetDatabase.CreateFolder("Assets", "__MCBSceneMeshProvenanceTests");
        }
        var mesh = new Mesh { name = "CustomBody" };
        AssetDatabase.CreateAsset(mesh, MeshPath);
        var avatar = new GameObject("Avatar");
        var body = new GameObject("Body");
        body.transform.SetParent(avatar.transform, false);
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        var clothing = new GameObject("Clothing");
        clothing.transform.SetParent(avatar.transform, false);
        clothing.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        try
        {
            var replaced = AvatarSceneMeshProvenanceService.FindReplacedRenderers(
                avatar.transform,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Body"] = "Assets/Base/original.fbx"
                });

            Assert.That(replaced, Has.Count.EqualTo(1));
            Assert.That(replaced[0].rendererPath, Is.EqualTo("Body"));
            Assert.That(replaced[0].currentMeshAssetPath, Is.EqualTo(MeshPath));

            Assert.That(AvatarSceneMeshProvenanceService.FindReplacedRenderers(
                avatar.transform,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Body"] = MeshPath }), Is.Empty);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(avatar);
            AssetDatabase.DeleteAsset(TestFolder);
        }
    }
}
#endif
