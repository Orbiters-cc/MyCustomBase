#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Blender export imports: what a manifest may name, where the copies land and how they are named, which target a model
/// goes to, which renderer stands for which target, what a version would leave out, and the cleanup of the copies nothing
/// uses. Fixtures live in preview scenes, temporary folders and unique Assets/ folders.
/// </summary>
public class BlenderExportImportTests
{
    private readonly List<string> tempFolders = new List<string>();
    private readonly List<string> assetFolders = new List<string>();
    private Scene scene;

    [SetUp]
    public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (string folder in tempFolders)
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        foreach (string folder in assetFolders) AssetDatabase.DeleteAsset(folder);
        tempFolders.Clear();
        assetFolders.Clear();
    }

    private string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "MCB_BlenderImport_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        tempFolders.Add(folder);
        return folder;
    }

    private string AssetFolder()
    {
        string name = "MCB_BlenderImportTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", name);
        assetFolders.Add("Assets/" + name);
        return "Assets/" + name;
    }

    private GameObject Make(string name, Transform parent = null)
    {
        var gameObject = new GameObject(name);
        if (parent != null) gameObject.transform.SetParent(parent, false);
        else SceneManager.MoveGameObjectToScene(gameObject, scene);
        return gameObject;
    }

    // ---- What a manifest may name -----------------------------------------------------------------------------------

    [Test]
    public void ManifestModelPathsStayInsideTheExportFolder()
    {
        string export = TempFolder();
        Directory.CreateDirectory(Path.Combine(export, "models"));
        File.WriteAllText(Path.Combine(export, "models", "01_Body.fbx"), "fbx");
        File.WriteAllText(Path.Combine(export, "notes.txt"), "text");
        string elsewhere = TempFolder();
        string secret = Path.Combine(elsewhere, "secret.fbx");
        File.WriteAllText(secret, "not the export's");

        Assert.AreEqual(Path.GetFullPath(Path.Combine(export, "models", "01_Body.fbx")), BlenderExportImport.ExportFile(export, "models/01_Body.fbx"));
        Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ExportFile(export, secret), "an absolute path");
        Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ExportFile(export, "../" + Path.GetFileName(elsewhere) + "/secret.fbx"), "a way out");
        Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ExportFile(export, "models/../../secret.fbx"), "a way out through a folder");
        Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ExportFile(export, "CON.fbx"), "a device name");
        Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ExportFile(export, "notes.txt"), "not an FBX");
        Assert.Throws<FileNotFoundException>(() => BlenderExportImport.ExportFile(export, "models/02_Missing.fbx"));
    }

    // Mixer's Export for MCB names the target's meshes Unity kept out of Blender: an FBX without them would remove them
    // from the base (fbxReplace), so that model is refused, naming them.
    [Test]
    public void AModelLeavingOutMeshesUnityKeptOutOfBlenderIsRefusedNamingThem()
    {
        string manifestPath = Path.Combine(TempFolder(), "manifest.json");
        File.WriteAllText(manifestPath, @"{
  ""kind"": ""orbiters.mcb.blenderExport"", ""protocolVersion"": 2,
  ""models"": [
    { ""role"": ""CUSTOM_BASE"", ""path"": ""models/01_Avatar.fbx"", ""targetFbxPath"": ""Assets/Avatar/Avatar.fbx"",
      ""meshNames"": [""Body""], ""missingUnityTargetMeshNames"": [""Hair"", ""Shirt""] },
    { ""role"": ""CUSTOM_BASE"", ""path"": ""models/02_Head.fbx"", ""targetFbxPath"": ""Assets/Avatar/Head.fbx"",
      ""meshNames"": [""Head""], ""missingUnityTargetMeshNames"": [] }
  ]
}");
        var models = BlenderExportImport.ReadExportManifest(manifestPath).models;

        var error = Assert.Throws<InvalidOperationException>(() => BlenderExportImport.RequireEveryTargetMesh(models[0], "Assets/Avatar/Avatar.fbx"));
        StringAssert.Contains("Avatar.fbx", error.Message);
        StringAssert.Contains("Hair, Shirt", error.Message);
        StringAssert.Contains("Send to Blender", error.Message);
        Assert.DoesNotThrow(() => BlenderExportImport.RequireEveryTargetMesh(models[1], "Assets/Avatar/Head.fbx"));
        Assert.DoesNotThrow(() => BlenderExportImport.RequireEveryTargetMesh(new BlenderExportImport.ModelInfo { path = "03.fbx" }, "Assets/Avatar/Hair.fbx"),
            "An export from before the field names nothing missing.");
    }

    // ---- Where the copies land ----------------------------------------------------------------------------------------

    [Test]
    public void ImportNamesAreShortPlainAndKeyedByContent()
    {
        string stem = BlenderExportImport.ImportFileStem(0, "Assets/Avatars/My Very Long Avatar Base Name With Spaces v1.5.fbx", "0123456789abcdef");
        StringAssert.StartsWith("01_", stem);
        StringAssert.EndsWith("-0123456789abcdef", stem);
        StringAssert.DoesNotContain(" ", stem);
        Assert.LessOrEqual(stem.Length, "01_".Length + 24 + "-0123456789abcdef".Length);
        Assert.AreEqual("02__CON-k", BlenderExportImport.ImportFileStem(1, "Assets/CON.fbx", "k"), "never a device name");
        Assert.AreEqual("mcb", BlenderExportImport.SanitizeFileName(" . "), "never empty or a trailing dot");

        // The cleanup only ever takes MCB's own names.
        Assert.IsTrue(BlenderExportImport.IsImportFile(stem + ".fbx"));
        Assert.IsTrue(BlenderExportImport.IsImportFile(stem + "Avatar.asset"));
        Assert.IsFalse(BlenderExportImport.IsImportFile("Body.fbx"));
        Assert.IsTrue(BlenderExportImport.IsAdvancedExportCopy(stem + ".fbx"));
        Assert.IsTrue(BlenderExportImport.IsPreviewFile(new string('a', 32) + ".asset"));
        Assert.IsTrue(BlenderExportImport.IsPreviewFile(new string('a', 32) + ".humanoid-" + new string('b', 16) + ".asset"));
        Assert.IsFalse(BlenderExportImport.IsPreviewFile("Body.asset"));
    }

    [Test]
    public void TwoBasesOfTheSameNameGetFoldersOfTheirOwn()
    {
        var first = Make("Avatar").AddComponent<MyCustomBase>();
        var second = Make("Avatar").AddComponent<MyCustomBase>();

        string key = BlenderExportImport.BaseFolderKey(first);

        Assert.AreNotEqual(key, BlenderExportImport.BaseFolderKey(second));
        Assert.AreEqual(MCBUtils.PathHashLength, key.Length);
        Assert.AreEqual(key, BlenderExportImport.BaseFolderKey(first), "A base keeps its folder.");
    }

    // ---- Which target a model goes to ---------------------------------------------------------------------------------

    [Test]
    public void AModelWithoutTargetGoesToTheCreatorsFirstSourceAndSeveralAreRefused()
    {
        string folder = AssetFolder();
        var model = Make("source");
        var body = PrefabUtility.SaveAsPrefabAsset(model, folder + "/Body.prefab");
        var hair = PrefabUtility.SaveAsPrefabAsset(model, folder + "/Hair.prefab");
        var customBase = Make("creator").AddComponent<MyCustomBase>();
        customBase.baseFbxFiles.AddRange(new[] { body, hair });
        var targets = new List<BlenderExportImport.ExportTarget>
        {
            // The asset's SOURCE file the creator's source files do not have: never a default.
            new BlenderExportImport.ExportTarget { UnityPath = folder + "/Unresolved.prefab" },
            new BlenderExportImport.ExportTarget { UnityPath = folder + "/Hair.prefab" },
            new BlenderExportImport.ExportTarget { UnityPath = folder + "/Body.prefab" },
        };
        var manifest = new BlenderExportImport.BlenderExportManifest();
        var unnamed = new BlenderExportImport.ModelInfo { path = "01.fbx" };
        var named = new BlenderExportImport.ModelInfo { path = "02.fbx", targetFbxPath = folder + "/Body.prefab" };

        Assert.AreEqual(folder + "/Hair.prefab", BlenderExportImport.ResolveTarget(customBase, targets, manifest, unnamed, 1).UnityPath);
        Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ResolveTarget(customBase, targets, manifest, unnamed, 2),
            "Two models without a target would land on the same one.");
        Assert.AreEqual(folder + "/Body.prefab", BlenderExportImport.ResolveTarget(customBase, targets, manifest, named, 2).UnityPath);
        Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ResolveTarget(customBase, targets, manifest,
            new BlenderExportImport.ModelInfo { path = "03.fbx", targetFbxPath = folder + "/Other.prefab" }, 0));
    }

    // ---- Which renderer stands for which target -------------------------------------------------------------------------

    [Test]
    public void ARendererFindsTheCustomBaseOnAChildOfItsAvatar()
    {
        var avatar = Make("Avatar");
        var body = Make("Body", avatar.transform);
        body.AddComponent<SkinnedMeshRenderer>();
        var customBase = Make("MCB", avatar.transform).AddComponent<MyCustomBase>();

        Assert.AreSame(customBase, BlenderExportImport.CustomBaseOf(body.transform));
    }

    [Test]
    public void OnlyOneRendererPerFbxMeshStandsForTheTargetActiveOnesFirst()
    {
        string folder = AssetFolder();
        string basePath = folder + "/Base.prefab";
        PrefabUtility.SaveAsPrefabAsset(Make("Base"), basePath);
        // The base's FBX meshes (a prefab holding a mesh stands for the FBX).
        AssetDatabase.AddObjectToAsset(new Mesh { name = "Body" }, basePath);
        var avatar = Make("Avatar");
        var customBase = avatar.AddComponent<MyCustomBase>();
        customBase.baseFbxFiles.Add(AssetDatabase.LoadAssetAtPath<GameObject>(basePath));
        // An applied version's meshes: not the FBX's own, so they are matched by name.
        var versionMesh = new Mesh { name = "Body" };
        try
        {
            var outfit = Make("Outfit", avatar.transform);
            var copy = Make("Body", outfit.transform).AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = versionMesh;
            outfit.SetActive(false);
            var body = Make("Body", avatar.transform).AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = versionMesh;

            Assert.AreEqual(basePath, BlenderExportImport.TargetFbxOf(body));
            Assert.IsNull(BlenderExportImport.TargetFbxOf(copy), "The inactive outfit copy of Body is not exported into the base's FBX.");
        }
        finally
        {
            Object.DestroyImmediate(versionMesh);
            BlenderExportImport.ForgetTargets();
        }
    }

    // ---- What a version would leave out ---------------------------------------------------------------------------------

    [Test]
    public void RenderersInMixerSessionsAreNamedAsNotInTheVersion()
    {
        var edited = Make("Body").AddComponent<SkinnedMeshRenderer>();
        var untouched = Make("Hair").AddComponent<SkinnedMeshRenderer>();
        var previous = BlenderExportImport.InMixerSession;
        BlenderExportImport.InMixerSession = renderer => renderer == edited;
        try
        {
            Assert.IsTrue(BlenderExportImport.ShowsMeshOutsideVersions(edited));
            Assert.IsFalse(BlenderExportImport.ShowsMeshOutsideVersions(untouched));
            string warning = BlenderExportImport.EditsOutsideVersionsWarning(new Renderer[] { edited });
            StringAssert.Contains("Body", warning);
            StringAssert.Contains("Export for MCB", warning);
            Assert.IsNull(BlenderExportImport.EditsOutsideVersionsWarning(new Renderer[0]));
        }
        finally
        {
            BlenderExportImport.InMixerSession = previous;
        }
    }

    // ---- The cleanup of copies nothing uses ---------------------------------------------------------------------------

    [Test]
    public void AdvancedCopiesASavedSceneOrAnOpenBaseNamesStayTheOthersGo()
    {
        string folder = TempFolder();
        string own = Path.Combine(folder, "0123456789abcdef");
        string emptied = Path.Combine(folder, "fedcba9876543210");
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(emptied);
        string saved = Path.Combine(own, "01_Body-1111111111111111.fbx");
        string folded = Path.Combine(own, "01_My Body_20261017_181621.fbx");
        string open = Path.Combine(own, "01_Body-2222222222222222.fbx");
        string unused = Path.Combine(own, "01_Body-3333333333333333.fbx");
        string usersOwn = Path.Combine(own, "MyBody.fbx");
        string alone = Path.Combine(emptied, "02_Hair-4444444444444444.fbx");
        foreach (string file in new[] { saved, folded, open, unused, usersOwn, alone }) File.WriteAllText(file, "fbx");
        // Unity folds a long path over several lines at its spaces.
        string sceneFile = Path.Combine(TempFolder(), "Avatar.unity");
        File.WriteAllText(sceneFile, "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  modelFileBuildEntries:\n" +
                                     "  - externalCustomFbxPath: H:\\some project\n      folder\\01_Body-1111111111111111.fbx\n" +
                                     "  - externalCustomFbxPath: H:\\x\\01_My\n      Body_20261017_181621.fbx\n");

        var removed = BlenderExportImport.DiscardUnusedAdvancedExports(folder, new[] { sceneFile }, new HashSet<string> { open }, permanent: true);

        Assert.IsTrue(File.Exists(saved), "A saved scene names it.");
        Assert.IsTrue(File.Exists(folded), "A saved scene names it, folded at a space.");
        Assert.IsTrue(File.Exists(open), "An open base names it.");
        Assert.IsTrue(File.Exists(usersOwn), "Not one of MCB's names.");
        Assert.IsFalse(File.Exists(unused));
        Assert.IsFalse(File.Exists(alone));
        Assert.IsFalse(Directory.Exists(emptied), "Emptied folders go too.");
        Assert.AreEqual(2, removed.files);
    }

    [Test]
    public void ASceneThatIsNotTextKeepsEveryAdvancedCopy()
    {
        string folder = TempFolder();
        string copy = Path.Combine(folder, "01_Body-1111111111111111.fbx");
        File.WriteAllText(copy, "fbx");
        string binary = Path.Combine(TempFolder(), "Binary.unity");
        File.WriteAllBytes(binary, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });

        var removed = BlenderExportImport.DiscardUnusedAdvancedExports(folder, new[] { binary }, null, permanent: true);

        Assert.AreEqual(0, removed.files);
        Assert.IsTrue(File.Exists(copy));
    }

    [Test]
    public void UnusedPreviewsGoAndThoseASavedPrefabOrAnOpenObjectUsesStay()
    {
        string folder = AssetFolder();
        string Payload(char c) => folder + "/" + new string(c, 32) + ".asset";
        string saved = Payload('a'), open = Payload('c'), unused = Payload('b'), usersOwn = folder + "/MyPreview.asset";
        foreach (string path in new[] { saved, open, unused, usersOwn })
        {
            var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
            AssetDatabase.CreateAsset(payload, path);
            AssetDatabase.AddObjectToAsset(new Mesh { name = "Body" }, payload);
            AssetDatabase.SaveAssetIfDirty(payload);
        }
        var prop = Make("Prop");
        prop.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAllAssetsAtPath(saved).OfType<Mesh>().Single();
        // Saved files in the folder being cleaned are not read: the prefab lives elsewhere, as scenes and prefabs do.
        string prefab = AssetFolder() + "/Prop.prefab";
        PrefabUtility.SaveAsPrefabAsset(prop, prefab);

        using (ProjectReferences.UseCacheFile(Path.Combine(TempFolder(), "references.json")))
            NativeMeshPayloadService.DeleteUnusedAssets(folder, BlenderExportImport.IsPreviewFile, new[] { "t:NativeMeshPayloadAsset", "t:Avatar" },
                () => new[] { open }, new[] { prefab });

        Assert.IsTrue(File.Exists(saved), "A saved prefab uses it.");
        Assert.IsTrue(File.Exists(open), "An open object uses it.");
        Assert.IsTrue(File.Exists(usersOwn), "Not one of MCB's names.");
        Assert.IsFalse(File.Exists(unused));
    }

    [Test]
    public void AnImportsAvatarAnOpenAnimatorUsesStays()
    {
        string folder = AssetFolder();
        var avatar = Make("Avatar");
        string used = folder + "/01_Body-1111111111111111Avatar.asset", unused = folder + "/01_Body-2222222222222222Avatar.asset";
        AssetDatabase.CreateAsset(AvatarBuilder.BuildGenericAvatar(avatar, ""), used);
        AssetDatabase.CreateAsset(AvatarBuilder.BuildGenericAvatar(avatar, ""), unused);
        avatar.AddComponent<Animator>().avatar = AssetDatabase.LoadAssetAtPath<Avatar>(used);

        using (ProjectReferences.UseCacheFile(Path.Combine(TempFolder(), "references.json")))
            NativeMeshPayloadService.DeleteUnusedAssets(folder, BlenderExportImport.IsImportFile, new[] { "t:Model", "t:Avatar" }, null, Array.Empty<string>());

        Assert.IsTrue(File.Exists(used));
        Assert.IsFalse(File.Exists(unused));
    }

    [Test]
    public void WhatABaseUsesIsKeptForTheSession()
    {
        string folder = AssetFolder();
        var source = Make("source");
        var custom = PrefabUtility.SaveAsPrefabAsset(source, folder + "/01_Body-1111111111111111.prefab");
        var customBase = Make("Avatar").AddComponent<MyCustomBase>();
        customBase.modelFileBuildEntries.Add(new CreatorModelFileBuildEntry { customFbx = custom });
        customBase.modelFileBuildEntries.Add(new CreatorModelFileBuildEntry { externalCustomFbxPath = "Assets/MCB/generated/blenderAdvancedExports~/k/01_Body-2.fbx" });

        var used = BlenderExportImport.FilesUsedBy(customBase);

        Assert.That(used, Does.Contain(folder + "/01_Body-1111111111111111.prefab"));
        Assert.That(used, Does.Contain(Path.GetFullPath("Assets/MCB/generated/blenderAdvancedExports~/k/01_Body-2.fbx")));
    }
}
#endif
