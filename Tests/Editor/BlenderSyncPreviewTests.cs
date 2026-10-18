#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// The Blender link's preview of a Blender export: the Toolkit's Blender fixture is the avatar's base model, and edited
// copies of it are the exports.
public class BlenderSyncPreviewTests
{
    private const string Fixture = "Packages/orbiters.toolkit/Tests/Editor/Fixtures/BlenderSkinned.fbx";
    private string folder;
    private string exportsFolder;
    private byte[] fixtureBytes;
    private GameObject avatar;
    private Mesh originalMesh;

    [SetUp]
    public void SetUp()
    {
        folder = "Assets/MCB_BlenderPreviewTest_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        exportsFolder = Path.Combine(Path.GetTempPath(), "MCB_BlenderPreviewTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportsFolder);
        fixtureBytes = File.ReadAllBytes(Path.GetFullPath(Fixture));
        avatar = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Fixture));
        avatar.hideFlags = HideFlags.HideAndDontSave;
        originalMesh = Body().sharedMesh;
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(avatar);
        AssetDatabase.DeleteAsset(folder);
        Directory.Delete(exportsFolder, true);
    }

    private SkinnedMeshRenderer Body() => avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();

    // The fixture with its first point raised in the file's space.
    private string Export(string name, float lift)
    {
        var document = Orbiters.Toolkit.Editor.Meshes.FbxDocument.Read(fixtureBytes);
        var geometry = document.Objects().Values.Single(o => o.Name == "Geometry" && o.Class == "Mesh" && o.ObjectName == "Body");
        ((double[])geometry.Child("Vertices").Properties[0])[1] += lift;
        string path = Path.Combine(exportsFolder, name + ".fbx");
        File.WriteAllBytes(path, document.Write());
        return path;
    }

    private static List<ModelFileSmrPathData> Mappings() => new List<ModelFileSmrPathData> {
        new ModelFileSmrPathData { avatarPath = "Body", fbxMeshPath = "Body", meshName = "Body", rendererName = "Body" }
    };

    private NativeMeshPayloadAsset Apply(string export, IEnumerable<MeshBlendshapeSelection> dynamicNormals = null) =>
        NativeMeshPayloadService.ApplyBlenderExportPreview(avatar.transform, export, Fixture, Mappings(), folder, dynamicNormals, null);

    private static Vector3[] ReadBody(string export)
    {
        var model = Orbiters.Toolkit.Editor.Meshes.FbxModel.Load(export);
        try { return model.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh.vertices; }
        finally { Orbiters.Toolkit.Editor.Meshes.FbxModel.Release(model); }
    }

    [Test]
    public void PreviewShowsTheExportOnTheAvatarsBones()
    {
        string export = Export("raised", 0.1f);
        var payload = Apply(export);
        var body = Body();
        Assert.That(body.sharedMesh.vertices, Is.EqualTo(ReadBody(export)), "The export's vertices, read straight from its file.");
        Assert.That(body.sharedMesh.vertices, Is.Not.EqualTo(ReadBody(Path.GetFullPath(Fixture))));
        Assert.IsTrue(AssetDatabase.Contains(body.sharedMesh));
        Assert.AreSame(payload.renderers[0].mesh, body.sharedMesh);
        Assert.AreSame(avatar.transform.Find("Armature/Hips"), body.rootBone);
        foreach (var bone in body.bones) Assert.That(bone.IsChildOf(avatar.transform), Is.True, bone.name);
        Assert.That(body.sharedMesh.blendShapeCount, Is.EqualTo(originalMesh.blendShapeCount));
        Assert.That(File.ReadAllBytes(Path.GetFullPath(Fixture)), Is.EqualTo(fixtureBytes), "The base model is untouched.");
    }

    [Test]
    public void RepeatedPreviewReusesUnchangedPayloadAndRetainsEarlierMeshAfterAnotherEdit()
    {
        string export = Export("raised", 0.1f);
        var first = Apply(export);
        var repeated = Apply(export);
        Assert.AreSame(first, repeated);
        var firstVertices = first.renderers[0].mesh.vertices;
        var next = Apply(Export("raised more", 0.2f));
        Assert.AreNotSame(first, next);
        Assert.That(first.renderers[0].mesh.vertices, Is.EqualTo(firstVertices), "The earlier preview stays as it was (Undo).");
        Assert.AreSame(next.renderers[0].mesh, Body().sharedMesh);
    }

    [Test]
    public void DynamicNormalBlendshapesGetTheirNormalsBakedAndTheOthersNone()
    {
        var payload = Apply(Export("raised", 0.1f), new[] { new MeshBlendshapeSelection { mesh = "Body", names = new List<string> { "Orbit muscles" } } });
        var mesh = payload.renderers[0].mesh;
        var deltas = new Vector3[mesh.vertexCount];
        var normals = new Vector3[mesh.vertexCount];
        mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("Orbit muscles"), 0, deltas, normals, null);
        Assert.That(normals.Any(n => n.sqrMagnitude > 1e-8f), Is.True, "Baked normals on the selected blendshape.");
        mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("Flex arm"), 0, deltas, normals, null);
        Assert.That(deltas.Any(d => d.sqrMagnitude > 1e-8f), Is.True);
        Assert.That(normals.All(n => n == Vector3.zero), Is.True, "Positions only on the others, as a version's payload.");
    }

    [Test]
    public void MissingBoneFailsWithoutChangingAvatarMesh()
    {
        UnityEngine.Object.DestroyImmediate(avatar.transform.Find("Armature/Hips/LeftUpperLeg").gameObject);
        Assert.Throws<InvalidOperationException>(() => Apply(Export("raised", 0.1f)));
        Assert.AreSame(originalMesh, Body().sharedMesh);
    }

    [Test]
    public void HeartbeatTransitionsFromWaitingToConnectedToDisconnected()
    {
        var type = typeof(BlenderSyncService).GetNestedType("ActiveSession", BindingFlags.NonPublic);
        var session = Activator.CreateInstance(type, true);
        string heartbeat = Path.Combine(exportsFolder, "heartbeat.json");
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
