#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// What a version build takes from the avatar (<see cref="VersionAvatarMeshes"/>): a base whose renderer shows a mesh Mixer
/// committed gets a custom FBX holding that mesh with the model's other meshes, objects, blendshapes and slot names, and its
/// entry names it, which is what the build reads; the same meshes give the same file; armature edits, scaled imports and
/// meshes still in a Mixer session are refused; a publish hears of meshes the entries do not hold. The model is written by
/// Toolkit's FbxExport into a unique Assets/ folder, the avatar lives in a preview scene and the committed mesh in a unique
/// folder under Mixer's.
/// </summary>
public class VersionAvatarMeshesTests
{
    private readonly List<Object> owned = new List<Object>();
    // Folders this test made, in the order made: removed newest first.
    private readonly List<string> folders = new List<string>();
    private Scene scene;
    private TestUndoSandbox undo;
    private Func<Renderer, bool> previousSession;
    private string folder;

    [SetUp]
    public void SetUp()
    {
        undo = TestUndoSandbox.Begin();
        scene = EditorSceneManager.NewPreviewScene();
        // Mixer's own sessions stay out of it: each test says which renderer is in one.
        previousSession = VersionAvatarMeshes.InMixerSession;
        VersionAvatarMeshes.InMixerSession = null;
        folder = Folder("Assets/MCB_AvatarMeshesTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        SmrPathService.InvalidateCache();
    }

    [TearDown]
    public void TearDown()
    {
        VersionAvatarMeshes.InMixerSession = previousSession;
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
        owned.Clear();
        for (int i = folders.Count - 1; i >= 0; i--) AssetDatabase.DeleteAsset(folders[i]);
        folders.Clear();
        SmrPathService.InvalidateCache();
        undo.End();
    }

    [Test]
    public void ABaseShowingACommittedMeshGetsACustomFbxHoldingIt()
    {
        var model = MakeModel("Base");
        var (customBase, body) = MakeAvatar(model);
        var committed = Commit(body, new Vector3(0f, 0.05f, 0f));
        Assert.That(VersionAvatarMeshes.IsCommitted(body), Is.True);
        var found = VersionAvatarMeshes.Find(customBase, null);
        Assert.That(found.InSession, Is.Empty);
        Assert.That(found.Committed.Select(s => s.Renderer), Is.EqualTo(new[] { body }), "Only the body shows a mesh the FBX does not hold.");
        Assert.That(found.Committed[0].Target, Is.EqualTo(AssetDatabase.GetAssetPath(model)));

        var taken = VersionAvatarMeshes.TakeIntoEntries(customBase, new SerializedObject(customBase), found.Committed, folder);
        Assert.That(taken.Select(t => t.Renderer), Is.EqualTo(new[] { "Body" }));
        var custom = customBase.modelFileBuildEntries[0].customFbx;
        Assert.That(custom, Is.Not.Null, "The entry names the new custom FBX: the build reads it, whichever way it delivers the version.");
        string path = AssetDatabase.GetAssetPath(custom);
        Assert.That(path, Is.EqualTo(taken[0].CustomFbx));
        StringAssert.StartsWith(folder + "/01_Base-", path);
        Assert.That(customBase.modelFileBuildEntries[0].externalCustomFbxPath, Is.Null.Or.Empty);
        StringAssert.Contains("Body", VersionAvatarMeshes.TakenMessage(taken));

        // The committed mesh in place of the model's body, bound to the model's bones.
        var written = custom.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
        AssertSameExtents(written.sharedMesh, committed, "The body is the committed one");
        Assert.That(written.sharedMesh.GetBlendShapeIndex("Smile"), Is.GreaterThanOrEqualTo(0), "Its blendshape");
        Assert.That(written.bones.Select(b => b.name), Is.EquivalentTo(new[] { "Hips", "Spine" }), "Its bones");
        // The rest as the model has it.
        var hair = custom.transform.Find("Hair").GetComponent<SkinnedMeshRenderer>();
        AssertSameExtents(hair.sharedMesh, model.transform.Find("Hair").GetComponent<SkinnedMeshRenderer>().sharedMesh, "The hair is the model's");
        Assert.That(custom.transform.Find("Armature/Hips/Spine/Spine_end"), Is.Not.Null, "An object no mesh weights stays in the file.");
        Assert.That(MaterialSlotNames.Of(written), Is.EqualTo(new[] { "Skin" }), "Slots keep the model's names.");
        Assert.That(MaterialSlotNames.Of(hair), Is.EqualTo(new[] { "HairMat" }));

        // Taken again (a build that stopped after it, a temporary version): the entry holds these meshes already.
        var again = VersionAvatarMeshes.TakeIntoEntries(customBase, new SerializedObject(customBase), VersionAvatarMeshes.Find(customBase, null).Committed, folder);
        Assert.That(again.Single().CustomFbx, Is.EqualTo(path));
        Assert.That(Directory.GetFiles(Path.GetFullPath(folder), "*.fbx").Length, Is.EqualTo(2), "The model and one file written from the avatar.");
    }

    [Test]
    public void APublishIsToldOfMeshesTheEntriesDoNotHold()
    {
        var model = MakeModel("Base");
        var (customBase, body) = MakeAvatar(model);
        Commit(body, new Vector3(0f, 0.05f, 0f));
        StringAssert.Contains("Body", VersionAvatarMeshes.NotInBuildMessage(customBase, null), "Not taken yet (a build without them).");
        VersionAvatarMeshes.TakeIntoEntries(customBase, new SerializedObject(customBase), VersionAvatarMeshes.Find(customBase, null).Committed, folder);
        Assert.That(VersionAvatarMeshes.NotInBuildMessage(customBase, null), Is.Null, "The entry holds what the avatar shows.");
        Commit(body, new Vector3(0f, 0.1f, 0f));
        StringAssert.Contains("committed after the build", VersionAvatarMeshes.NotInBuildMessage(customBase, null));
    }

    [Test]
    public void AnUnweightedBoneTheModelsMeshLacksDoesNotStopIt()
    {
        var model = MakeModel("Base");
        var (customBase, body) = MakeAvatar(model);
        // Mixer gave the body a leaf bone that weights nothing: the model has the object, its body mesh not the bone.
        var end = body.transform.parent.Find("Armature/Hips/Spine/Spine_end");
        var mesh = Object.Instantiate(body.sharedMesh);
        mesh.bindposes = mesh.bindposes.Append(end.worldToLocalMatrix * body.transform.localToWorldMatrix).ToArray();
        body.bones = body.bones.Append(end).ToArray();
        body.sharedMesh = MeshAssets.Save(mesh, MixerFolder(), "Body");

        var taken = VersionAvatarMeshes.TakeIntoEntries(customBase, new SerializedObject(customBase), VersionAvatarMeshes.Find(customBase, null).Committed, folder);
        Assert.That(taken.Count, Is.EqualTo(1));
        var written = customBase.modelFileBuildEntries[0].customFbx.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
        Assert.That(written.bones.Select(b => b.name), Is.EquivalentTo(new[] { "Hips", "Spine" }), "The bone that weights nothing is no skin bone.");
    }

    [Test]
    public void AnArmatureEditIsLeftToBlender()
    {
        var model = MakeModel("Base");
        var (customBase, body) = MakeAvatar(model);
        // A rest-pose edit: the body binds its bones 10 cm higher than the model does.
        Commit(body, Vector3.zero, poses => poses.Select(pose => Matrix4x4.Translate(new Vector3(0f, 0.1f, 0f)) * pose).ToArray());
        var found = VersionAvatarMeshes.Find(customBase, null);
        Assert.That(found.Committed.Count, Is.EqualTo(1));
        var error = Assert.Throws<InvalidOperationException>(() =>
            VersionAvatarMeshes.TakeIntoEntries(customBase, new SerializedObject(customBase), found.Committed, folder));
        StringAssert.Contains("Export for MCB", error.Message);
        Assert.That(customBase.modelFileBuildEntries[0].customFbx, Is.Null, "The entry is as it was.");
        Assert.That(Directory.GetFiles(Path.GetFullPath(folder), "*.fbx").Length, Is.EqualTo(1), "Nothing was written.");
    }

    [Test]
    public void AModelImportedAtAnotherScaleIsRefused()
    {
        var model = MakeModel("Base");
        string path = AssetDatabase.GetAssetPath(model);
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        importer.globalScale = 2f;
        importer.SaveAndReimport();
        model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var (customBase, body) = MakeAvatar(model);
        Commit(body, new Vector3(0f, 0.05f, 0f));
        var error = Assert.Throws<InvalidOperationException>(() =>
            VersionAvatarMeshes.TakeIntoEntries(customBase, new SerializedObject(customBase), VersionAvatarMeshes.Find(customBase, null).Committed, folder));
        StringAssert.Contains("Scale Factor 2", error.Message);
        Assert.That(customBase.modelFileBuildEntries[0].customFbx, Is.Null, "The entry is as it was.");
    }

    [Test]
    public void AMeshInAMixerSessionIsNotTakenAndStopsTheBuild()
    {
        var model = MakeModel("Base");
        var (customBase, body) = MakeAvatar(model);
        Commit(body, new Vector3(0f, 0.05f, 0f));
        VersionAvatarMeshes.InMixerSession = renderer => renderer == body;
        var found = VersionAvatarMeshes.Find(customBase, null);
        Assert.That(found.Committed, Is.Empty, "A Mixer session's working copy is never taken.");
        Assert.That(found.InSession.Select(s => s.Renderer), Is.EqualTo(new[] { body }));
        string message = VersionAvatarMeshes.InSessionMessage(found.InSession.Select(s => s.Renderer));
        StringAssert.Contains("Body", message);
        StringAssert.Contains("commit or revert", message);
        Assert.That(VersionAvatarMeshes.InSessionMessage(new Renderer[0]), Is.Null);
        Assert.That(VersionAvatarMeshes.CommittedMessage(new Renderer[0]), Is.Null);
        StringAssert.Contains("custom FBX", VersionAvatarMeshes.CommittedMessage(new Renderer[] { body }));
    }

    [Test]
    public void AnAvatarShowingItsModelsMeshesHasNothingToTake()
    {
        var model = MakeModel("Base");
        var (customBase, _) = MakeAvatar(model);
        var found = VersionAvatarMeshes.Find(customBase, null);
        Assert.That(found.Committed, Is.Empty);
        Assert.That(found.InSession, Is.Empty);
        Assert.That(VersionAvatarMeshes.TakeIntoEntries(customBase, new SerializedObject(customBase), found.Committed, folder), Is.Empty);
        Assert.That(customBase.modelFileBuildEntries[0].customFbx, Is.Null);
    }

    // ---- Fixtures ----------------------------------------------------------------------------------------------------

    // A model like a base's FBX: an armature with an end bone no mesh weights, a body with a blendshape weighted to two
    // bones, and hair, each with its own material slot. Written by FbxExport and imported as a model.
    private GameObject MakeModel(string name)
    {
        var root = new GameObject(name);
        SceneManager.MoveGameObjectToScene(root, scene);
        var armature = Child(root.transform, "Armature", Vector3.zero);
        var hips = Child(armature, "Hips", new Vector3(0f, 0.9f, 0f));
        var spine = Child(hips, "Spine", new Vector3(0f, 0.2f, 0f));
        var end = Child(spine, "Spine_end", new Vector3(0f, 0.2f, 0f));
        var shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
        var body = Skinned(root.transform, "Body", Box(new Vector3(0f, 1f, 0f), new Vector3(0.15f, 0.3f, 0.1f), "Smile"), new[] { hips, spine }, 2, new Material(shader) { name = "Skin" });
        // The end bone rides along unweighted: the file has it, no mesh binds to it.
        var hair = Skinned(root.transform, "Hair", Box(new Vector3(0f, 1.4f, 0f), new Vector3(0.1f, 0.05f, 0.1f), null), new[] { spine, end }, 1, new Material(shader) { name = "HairMat" });
        string path = folder + "/" + name + ".fbx";
        var result = FbxExport.Write(Path.GetFullPath(path), new[] { FbxExportMesh.From(body), FbxExportMesh.From(hair) }, root.transform);
        Assert.That(result.Error, Is.Null, "The model was written.");
        BlenderExportImport.WriteModelMeta(Path.GetFullPath(path));
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Assert.That(model, Is.Not.Null, "The model was imported.");
        Object.DestroyImmediate(root);
        return model;
    }

    private (MyCustomBase customBase, SkinnedMeshRenderer body) MakeAvatar(GameObject model)
    {
        var avatar = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
        var customBase = avatar.AddComponent<MyCustomBase>();
        customBase.baseFbxFiles.Add(model);
        customBase.modelFileBuildEntries.Add(new CreatorModelFileBuildEntry());
        return (customBase, avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>());
    }

    // An edit as Mixer commits it: the renderer's mesh with its vertices moved (and its bind poses changed), saved under
    // Mixer's folder and shown on the renderer.
    private Mesh Commit(SkinnedMeshRenderer renderer, Vector3 offset, Func<Matrix4x4[], Matrix4x4[]> bindposes = null)
    {
        var mesh = Object.Instantiate(renderer.sharedMesh);
        mesh.vertices = mesh.vertices.Select(v => v + offset).ToArray();
        if (bindposes != null) mesh.bindposes = bindposes(mesh.bindposes);
        mesh.RecalculateBounds();
        var saved = MeshAssets.Save(mesh, MixerFolder(), "Body");
        renderer.sharedMesh = saved;
        return saved;
    }

    private string MixerFolder()
    {
        string owner = "MCBAvatarMeshesTest-" + Path.GetFileName(folder).Substring("MCB_AvatarMeshesTest_".Length);
        return Folder(MeshAssets.Folder("Mixer", owner));
    }

    // Makes the folder and its missing parents, each remembered for the teardown.
    private string Folder(string path)
    {
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent) && parent != "Assets") Folder(parent);
        if (!AssetDatabase.IsValidFolder(path))
        {
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
            folders.Add(path);
        }
        return path;
    }

    private static Transform Child(Transform parent, string name, Vector3 position)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        child.localPosition = position;
        return child;
    }

    // Every vertex weighted to one bone: with two weighted bones, those above the mesh's middle to the second, the others to
    // the first; the bones past the weighted ones weight nothing.
    private SkinnedMeshRenderer Skinned(Transform parent, string name, Mesh mesh, Transform[] bones, int weightedBones, Material material)
    {
        var renderer = Child(parent, name, Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
        float middle = mesh.bounds.center.y;
        mesh.boneWeights = mesh.vertices.Select(v => new BoneWeight { boneIndex0 = weightedBones > 1 && v.y > middle ? 1 : 0, weight0 = 1f }).ToArray();
        mesh.bindposes = bones.Select(bone => bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix).ToArray();
        renderer.sharedMesh = mesh;
        renderer.bones = bones;
        renderer.rootBone = bones[0];
        renderer.sharedMaterials = new[] { material };
        owned.Add(material);
        return renderer;
    }

    private Mesh Box(Vector3 center, Vector3 half, string shape)
    {
        var vertices = new List<Vector3>();
        for (int i = 0; i < 8; i++)
            vertices.Add(center + Vector3.Scale(half, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f)));
        var mesh = new Mesh { name = "Box" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, vertices.Select(v => new Vector2(v.x, v.y)).ToList());
        mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 }, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        if (shape != null) mesh.AddBlendShapeFrame(shape, 100f, vertices.Select(v => v.y > center.y ? new Vector3(0.02f, 0f, 0f) : Vector3.zero).ToArray(), null, null);
        owned.Add(mesh);
        return mesh;
    }

    // Where the vertices are (an importer may order or split them its own way).
    private static void AssertSameExtents(Mesh actual, Mesh expected, string what)
    {
        Vector3 Min(Mesh mesh) => mesh.vertices.Aggregate(Vector3.Min);
        Vector3 Max(Mesh mesh) => mesh.vertices.Aggregate(Vector3.Max);
        Assert.That(Vector3.Distance(Min(actual), Min(expected)), Is.LessThan(1e-4f), what + ": lowest corner");
        Assert.That(Vector3.Distance(Max(actual), Max(expected)), Is.LessThan(1e-4f), what + ": highest corner");
    }
}
#endif
