#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class NativeMeshRendererResolutionTests
{
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    private Scene scene;
    private Transform avatar;
    private SkinnedMeshRenderer clothing;
    private SkinnedMeshRenderer body;
    private readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();

    [SetUp]
    public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        avatar = new GameObject("Avatar").transform;
        SceneManager.MoveGameObjectToScene(avatar.gameObject, scene);
        var hips = new GameObject("Hips").transform;
        hips.SetParent(avatar, false);
        // Clothing comes first in the hierarchy: a first-name match would pick it.
        clothing = AddRenderer("Clothing/Body", Mesh("Shirt"), hips);
        body = AddRenderer("Model/Body", Mesh("Body"), hips);
    }

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var obj in created) UnityEngine.Object.DestroyImmediate(obj);
        created.Clear();
    }

    private Mesh Mesh(string name)
    {
        var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.bindposes = new[] { Matrix4x4.identity };
        created.Add(mesh);
        return mesh;
    }

    private SkinnedMeshRenderer AddRenderer(string path, Mesh mesh, Transform bone)
    {
        Transform parent = avatar;
        foreach (string part in path.Split('/'))
        {
            var child = new GameObject(part).transform;
            child.SetParent(parent, false);
            parent = child;
        }

        var renderer = parent.gameObject.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.bones = new[] { bone };
        return renderer;
    }

    // The session recorded the body at "Body"; the creator then moved it under "Model".
    private NativeMeshPayloadRenderer MovedBodyRecord(string avatarPath = "Body") => new NativeMeshPayloadRenderer
    {
        avatarPath = avatarPath, fbxMeshPath = "Body", meshName = "Body", rendererName = "Body",
        mesh = Mesh("Body_edited"), bonePaths = new List<string> { "Hips" }
    };

    private static SkinnedMeshRenderer Resolve(Transform root, NativeMeshPayloadRenderer record) =>
        (SkinnedMeshRenderer)typeof(NativeMeshPayloadService).GetMethod("ResolveAvatarRenderer", Private).Invoke(null, new object[] { root, record });

    private void Apply(params NativeMeshPayloadRenderer[] records)
    {
        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        created.Add(payload);
        payload.renderers.AddRange(records);
        try { typeof(NativeMeshPayloadService).GetMethod("ApplyPayloadToAvatar", Private).Invoke(null, new object[] { avatar, payload }); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    [Test]
    public void MovedRendererRequiresAnExplicitPathInsteadOfGuessingByMeshName()
    {
        Assert.IsNull(Resolve(avatar, MovedBodyRecord()));
        Assert.AreSame(body, Resolve(avatar, MovedBodyRecord("Model/Body")));
    }

    [Test]
    public void AmbiguousMovedRendererFailsClosedWithoutChangingAnyMesh()
    {
        var clothingMesh = Mesh("Body");
        clothing.sharedMesh = clothingMesh;
        var bodyMesh = body.sharedMesh;

        Assert.IsNull(Resolve(avatar, MovedBodyRecord()));
        var failure = Assert.Throws<InvalidOperationException>(() => Apply(MovedBodyRecord()));
        StringAssert.Contains("Put it back at 'Body'", failure.Message);
        Assert.AreSame(clothingMesh, clothing.sharedMesh);
        Assert.AreSame(bodyMesh, body.sharedMesh);
    }

    [Test]
    public void AUniqueClothingMeshNameDoesNotReplaceAMissingBody()
    {
        clothing.sharedMesh = Mesh("Body");
        body.sharedMesh = Mesh("Body from another preview");
        var clothingMesh = clothing.sharedMesh;
        Assert.IsNull(Resolve(avatar, MovedBodyRecord()));
        Assert.Throws<InvalidOperationException>(() => Apply(MovedBodyRecord()));
        Assert.AreSame(clothingMesh, clothing.sharedMesh);
    }

    [Test]
    public void EmptyRecordedPathResolvesTheRootRenderer()
    {
        var renderer = avatar.gameObject.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = Mesh("Root");
        Assert.AreSame(renderer, Resolve(avatar, MovedBodyRecord("")));
        Assert.IsNull(Resolve(avatar, MovedBodyRecord(null)));
    }

    [Test]
    public void TwoRecordsResolvingToOneRendererFailBeforeAnyMeshChanges()
    {
        var bodyMesh = body.sharedMesh;

        var failure = Assert.Throws<InvalidOperationException>(() => Apply(MovedBodyRecord("Model/Body"), MovedBodyRecord("Model/Body")));
        StringAssert.Contains("both resolve", failure.Message);
        Assert.AreSame(bodyMesh, body.sharedMesh);
    }
}
#endif
