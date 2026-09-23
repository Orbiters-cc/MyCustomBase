#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class BlenderSyncPreviewTests
{
    private string folder;
    private string sourcePath;
    private string originalFileContents;
    private GameObject source;
    private GameObject avatar;
    private Mesh sourceMesh;
    private Mesh originalMesh;

    [SetUp]
    public void SetUp()
    {
        folder = "Assets/MCB_BlenderPreviewTest_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        sourcePath = folder + "/Source.prefab";
        source = BuildRig("Export", 2f, out sourceMesh);
        avatar = BuildRig("Avatar", 1f, out originalMesh);
        PrefabUtility.SaveAsPrefabAsset(avatar, sourcePath);
        originalFileContents = File.ReadAllText(sourcePath);
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(source);
        UnityEngine.Object.DestroyImmediate(avatar);
        UnityEngine.Object.DestroyImmediate(sourceMesh);
        UnityEngine.Object.DestroyImmediate(originalMesh);
        AssetDatabase.DeleteAsset(folder);
    }

    private static GameObject BuildRig(string name, float height, out Mesh mesh)
    {
        var root = new GameObject(name);
        var bone = new GameObject("Hips");
        bone.transform.SetParent(root.transform, false);
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        mesh = new Mesh { name = "Body" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up * height };
        mesh.triangles = new[] { 0, 1, 2 };
        mesh.boneWeights = new[] {
            new BoneWeight { boneIndex0 = 0, weight0 = 1 },
            new BoneWeight { boneIndex0 = 0, weight0 = 1 },
            new BoneWeight { boneIndex0 = 0, weight0 = 1 }
        };
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.bones = new[] { bone.transform };
        renderer.rootBone = bone.transform;
        return root;
    }

    private List<ModelFileSmrPathData> Mappings(string path = "Body") => new List<ModelFileSmrPathData> {
        new ModelFileSmrPathData { avatarPath = path, fbxMeshPath = "Body", meshName = "Body", rendererName = "Body" }
    };

    [Test]
    public void PreviewAppliesEditedGeometryAndSurvivesDeletingImportedModel()
    {
        var payload = NativeMeshPayloadService.ApplyModelPreview(avatar.transform, source, sourcePath, Mappings(), folder);
        var body = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
        Assert.AreEqual(2f, body.sharedMesh.vertices[2].y);
        Assert.AreSame(avatar.transform.Find("Hips"), body.bones[0]);
        Assert.IsTrue(AssetDatabase.Contains(body.sharedMesh));
        Assert.AreNotSame(sourceMesh, body.sharedMesh);
        UnityEngine.Object.DestroyImmediate(source);
        source = null;
        UnityEngine.Object.DestroyImmediate(sourceMesh);
        sourceMesh = null;
        Assert.AreEqual(2f, body.sharedMesh.vertices[2].y);
        Assert.AreSame(payload.renderers[0].mesh, body.sharedMesh);
        Assert.AreEqual(originalFileContents, File.ReadAllText(sourcePath));
    }

    [Test]
    public void RepeatedPreviewReusesUnchangedPayloadAndRetainsEarlierMeshAfterAnotherEdit()
    {
        var first = NativeMeshPayloadService.ApplyModelPreview(avatar.transform, source, sourcePath, Mappings(), folder);
        var repeated = NativeMeshPayloadService.ApplyModelPreview(avatar.transform, source, sourcePath, Mappings(), folder);
        Assert.AreSame(first, repeated);
        var vertices = sourceMesh.vertices;
        vertices[2].y = 3f;
        sourceMesh.vertices = vertices;
        sourceMesh.RecalculateBounds();
        var next = NativeMeshPayloadService.ApplyModelPreview(avatar.transform, source, sourcePath, Mappings(), folder);
        Assert.AreNotSame(first, next);
        Assert.AreEqual(2f, first.renderers[0].mesh.vertices[2].y);
        Assert.AreEqual(3f, avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh.vertices[2].y);
    }

    [Test]
    public void MissingBoneFailsWithoutChangingAvatarMesh()
    {
        UnityEngine.Object.DestroyImmediate(avatar.transform.Find("Hips").gameObject);
        Assert.Throws<InvalidOperationException>(() => NativeMeshPayloadService.ApplyModelPreview(
            avatar.transform, source, sourcePath, Mappings(), folder));
        Assert.AreSame(originalMesh, avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh);
    }

    [Test]
    public void HeartbeatTransitionsFromWaitingToConnectedToDisconnected()
    {
        var type = typeof(BlenderSyncService).GetNestedType("ActiveSession", BindingFlags.NonPublic);
        var session = Activator.CreateInstance(type, true);
        string heartbeat = sourcePath + ".heartbeat";
        type.GetField("heartbeatPath").SetValue(session, heartbeat);
        var update = typeof(BlenderSyncService).GetMethod("UpdateConnectionState", BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            update.Invoke(null, new[] { session });
            Assert.AreEqual("waiting for Blender", type.GetField("connectionState").GetValue(session));
            File.WriteAllText(heartbeat, "{}");
            update.Invoke(null, new[] { session });
            Assert.AreEqual("connected", type.GetField("connectionState").GetValue(session));
            File.SetLastWriteTimeUtc(heartbeat, DateTime.UtcNow.AddMinutes(-1));
            update.Invoke(null, new[] { session });
            Assert.AreEqual("disconnected", type.GetField("connectionState").GetValue(session));
        }
        finally { File.Delete(heartbeat); }
    }
}
#endif
