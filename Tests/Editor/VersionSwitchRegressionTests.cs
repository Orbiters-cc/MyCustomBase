#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class VersionSwitchRegressionTests
{
    private readonly List<string> tempPaths = new List<string>();
    private TestUndoSandbox sandbox;

    [SetUp]
    public void SetUp() => sandbox = TestUndoSandbox.Begin();

    [TearDown]
    public void TearDown()
    {
        try
        {
            foreach (string path in tempPaths)
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
        }
        finally
        {
            tempPaths.Clear();
            sandbox.End();
        }
    }

    // ---- MCB-01: code in downloaded versions

    [Test]
    public void DownloadedCodeListsArchiveScriptsAndImportedPackageScriptsOnly()
    {
        byte[] package = UnityPackage(
            (Guid(), "Assets/Creator/Tool.cs", "class Tool {}"),
            (Guid(), "Assets/Creator/flex.anim", "anim"),
            (Guid(), "Packages/com.vrcfury.vrcfury/Runtime/VF/Model/VRCFury.cs", "class VRCFury {}"));
        byte[] zip = Zip(("model.bin", new byte[] { 1, 2 }), ("Editor/AuditMarker.cs", new byte[] { 3 }),
            ("mcb logic.prefab", new byte[] { 4 }), ("mcb logic.unitypackage", package));

        using (var stream = new MemoryStream(zip))
            CollectionAssert.AreEquivalent(new[] { "Editor/AuditMarker.cs", "mcb logic.unitypackage/Assets/Creator/Tool.cs" },
                VersionContentTrust.ListCode(stream));
    }

    [Test]
    public void ModelAndDataFilesAreNotCode()
    {
        byte[] zip = Zip(("model.fbx.bin", new byte[] { 1 }), ("Body avatar.asset", new byte[] { 2 }), ("veins normal.png", new byte[] { 3 }),
            ("mcb logic.prefab", new byte[] { 4 }), ("mcb logic.unitypackage", UnityPackage((Guid(), "Assets/Creator/flex.anim", "anim"))));
        using (var stream = new MemoryStream(zip))
            Assert.IsEmpty(VersionContentTrust.ListCode(stream));
    }

    [Test]
    public void OnlyFreshMatchingCreatorTrustCanSkipCodeConsent()
    {
        var version = new CustomBaseVersion { assetId = 14, id = 22, version = "1", sourceVersionKey = "base-a",
            creatorTrusted = true, uploaderId = 7 };
        var current = new VersionContentTrust.CreatorTrustSnapshot { assetId = 14, versionId = 22, version = "1",
            sourceVersionKey = "base-a", creatorId = 7, creatorTrusted = true };
        Assert.IsFalse(VersionContentTrust.IsCreatorTrusted(version, null, 7), "Cached flags and uploader identity do not bypass a failed fresh check.");
        Assert.IsTrue(VersionContentTrust.IsCreatorTrusted(version, current, 0));
        current.creatorTrusted = false;
        Assert.IsFalse(VersionContentTrust.IsCreatorTrusted(version, current, 0), "Revocation overrides cached trusted=true.");
        Assert.IsTrue(VersionContentTrust.IsCreatorTrusted(version, current, 7), "Fresh own-upload identity is trusted.");
        current.creatorId = 8;
        Assert.IsFalse(VersionContentTrust.IsCreatorTrusted(version, current, 7));
        current.creatorTrusted = true;
        current.sourceVersionKey = "base-b";
        Assert.IsFalse(VersionContentTrust.IsCreatorTrusted(version, current, 7));
        current.sourceVersionKey = "base-a";
        current.versionId = 23;
        Assert.IsFalse(VersionContentTrust.IsCreatorTrusted(version, current, 7));
        current.versionId = 22;
        current.assetId = 15;
        Assert.IsFalse(VersionContentTrust.IsCreatorTrusted(version, current, 7));
        current.assetId = 14;
        current.creatorTrusted = null;
        Assert.IsFalse(VersionContentTrust.IsCreatorTrusted(version, current, 7));
    }

    [Test]
    public void MissingTrustFieldReadsAsUnknown()
    {
        string folder = TempFolder();
        File.WriteAllText(Path.Combine(folder, "version.json"), "{\"version\":\"1.0\"}");
        Assert.IsNull(VersionRepository.LoadVersionJson(folder).creatorTrusted);
        File.WriteAllText(Path.Combine(folder, "version.json"), "{\"version\":\"1.0\",\"creatorTrusted\":true,\"creatorName\":\"Orbit\"}");
        var version = VersionRepository.LoadVersionJson(folder);
        Assert.IsTrue(version.creatorTrusted.Value);
        Assert.AreEqual("Orbit", version.creatorName);
    }

    // Ctrl+Z of a committed switch puts back the files it changed, Ctrl+Y the switched ones.
    [Test]
    public void UndoAndRedoOfASwitchRestoreItsFiles()
    {
        string folder = "Assets/MCB_UndoTest_" + Guid();
        string path = folder + "/body.txt";
        var type = typeof(VersionActions).GetNestedType("VersionTransitionRollbackSnapshot", BindingFlags.NonPublic);
        Undo.IncrementCurrentGroup();
        int setupGroup = Undo.GetCurrentGroup();
        try
        {
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            File.WriteAllText(path, "version A");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            string meta = File.ReadAllText(path + ".meta");
            var snapshot = type.GetMethod("Capture").Invoke(null, new object[] { new[] { path }, null, null });
            File.WriteAllText(path, "version B");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            type.GetMethod("Commit").Invoke(snapshot, null);

            sandbox.PerformUndo();
            Assert.AreEqual("version A", File.ReadAllText(path));
            Assert.AreEqual(meta, File.ReadAllText(path + ".meta"));
            Undo.PerformRedo();
            Assert.AreEqual("version B", File.ReadAllText(path));
        }
        finally
        {
            Undo.RevertAllDownToGroup(setupGroup);
            VersionSwitchFileUndo.Forget(folder);
            AssetDatabase.DeleteAsset(folder);
        }
    }

    // Only the last switches keep their files. Undoing an older one moves the scene back and leaves its files (reported).
    [Test]
    public void UndoBeyondTheKeptSwitchesLeavesTheirFilesAsTheyAre()
    {
        string folder = "Assets/MCB_UndoTest_" + Guid();
        string path = folder + "/body.txt";
        var type = typeof(VersionActions).GetNestedType("VersionTransitionRollbackSnapshot", BindingFlags.NonPublic);
        Undo.IncrementCurrentGroup();
        int setupGroup = Undo.GetCurrentGroup();
        bool ignoreFailingMessages = LogAssert.ignoreFailingMessages;
        try
        {
            LogAssert.ignoreFailingMessages = true;
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            File.WriteAllText(path, "version 0");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            for (int i = 1; i <= 6; i++)
            {
                var snapshot = type.GetMethod("Capture").Invoke(null, new object[] { new[] { path }, null, null });
                File.WriteAllText(path, "version " + i);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                type.GetMethod("Commit").Invoke(snapshot, null);
            }

            // Each switch is one Undo step of this test: the sandbox fails rather than undo past them into the user's.
            for (int i = 0; i < 5; i++) sandbox.PerformUndo();
            Assert.AreEqual("version 1", File.ReadAllText(path));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("only keeps the files of the last 5 switches"));
            sandbox.PerformUndo();
            Assert.AreEqual("version 1", File.ReadAllText(path), "The oldest switch kept no copies: its files stay.");
            Undo.PerformRedo();
            Undo.PerformRedo();
            Assert.AreEqual("version 2", File.ReadAllText(path), "Redo of a kept switch still restores its files.");
        }
        finally
        {
            LogAssert.ignoreFailingMessages = ignoreFailingMessages;
            Undo.RevertAllDownToGroup(setupGroup);
            VersionSwitchFileUndo.Forget(folder);
            AssetDatabase.DeleteAsset(folder);
        }
    }

    // Reset puts back the import settings kept before a version changed them; a model nothing kept is left alone.
    [Test]
    public void ResetRestoresTheImportSettingsKeptBeforeTheVersion()
    {
        string folder = "Assets/MCB_ImportTest_" + Guid();
        string path = folder + "/model.txt";
        try
        {
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            File.WriteAllText(path, "model");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            AvatarDefinitionGenerationService.BackupOriginalImportSettings(path);
            var importer = AssetImporter.GetAtPath(path);
            importer.userData = "changed by a version";
            importer.SaveAndReimport();
            AvatarDefinitionGenerationService.BackupOriginalImportSettings(path);

            Assert.That(AvatarDefinitionGenerationService.RestoreOriginalImportSettings(path), Is.True);
            Assert.That(AssetImporter.GetAtPath(path).userData, Is.Empty, "The first kept settings are the original ones.");
            Assert.That(File.Exists(AvatarDefinitionGenerationService.OriginalImportSettingsPath(path)), Is.False);
            Assert.That(AvatarDefinitionGenerationService.RestoreOriginalImportSettings(path), Is.False);
        }
        finally
        {
            File.Delete(AvatarDefinitionGenerationService.OriginalImportSettingsPath(path));
            AssetDatabase.DeleteAsset(folder);
        }
    }

    // Removing MCB's veins switches off only what MCB switched on; a detail normal map MCB did not set stays.
    [Test]
    public void VeinsRemovalKeepsTheMaterialsOwnNormalMaps()
    {
        string folder = MCBUtils.ASSETS_BASE_FOLDER + "/VeinsTest_" + Guid();
        string veinsPath = folder + "/veins normal.png";
        var avatar = new GameObject("Avatar");
        var material = new Material(Shader.Find("Standard"));
        var normal = new Texture2D(2, 2);
        var userDetail = new Texture2D(2, 2);
        try
        {
            CreateAssetFolder(folder);
            File.WriteAllBytes(veinsPath, normal.EncodeToPNG());
            AssetDatabase.ImportAsset(veinsPath, ImportAssetOptions.ForceSynchronousImport);
            material.SetTexture("_BumpMap", normal);
            material.EnableKeyword("_NORMALMAP");
            var renderer = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
            renderer.transform.SetParent(avatar.transform);
            renderer.sharedMaterial = material;
            var service = new MaterialService(avatar.transform);

            material.SetTexture("_DetailNormalMap", userDetail);
            Assert.That(service.RemoveDetailNormalMap(renderer, false), Is.False);
            Assert.That(material.GetTexture("_DetailNormalMap"), Is.SameAs(userDetail));

            Assert.That(service.SetDetailNormalMap(renderer, veinsPath, false), Is.True);
            Assert.That(material.IsKeywordEnabled("_DETAIL_MULX2"), Is.True);
            Assert.That(service.RemoveDetailNormalMap(renderer, false), Is.True);
            Assert.That(material.GetTexture("_DetailNormalMap"), Is.Null);
            Assert.That(material.IsKeywordEnabled("_DETAIL_MULX2"), Is.False);
            Assert.That(material.IsKeywordEnabled("_NORMALMAP"), Is.True, "The material's own normal map stays on.");
        }
        finally
        {
            Object.DestroyImmediate(avatar);
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(normal);
            Object.DestroyImmediate(userDetail);
            AssetDatabase.DeleteAsset(folder);
        }
    }

    // Pose deltas are read from the imported source model: a version's model imported in its place stops the build.
    [Test]
    public void PayloadBuildRefusesWhileTheSourceModelIsReplaced()
    {
        string folder = "Assets/MCB_PoseTest_" + Guid();
        tempPaths.Add(Path.GetFullPath(folder));
        Directory.CreateDirectory(Path.GetFullPath(folder));
        string model = folder + "/model.bytes";
        File.WriteAllText(model, "original");
        File.WriteAllText(FileManagerService.GetOriginalBasePath(model), "original");
        Assert.DoesNotThrow(() => NativeMeshPayloadService.RequireOriginalModelImported(FileManagerService.GetOriginalBasePath(model)));
        File.WriteAllText(model, "version");
        Assert.Throws<InvalidOperationException>(() => NativeMeshPayloadService.RequireOriginalModelImported(model));
    }

    // Deleting a version's generated meshes keeps what an avatar still uses and removes the rest, humanoid Avatars too.
    [Test]
    public void DeletingVersionPayloadsKeepsWhatAnAvatarUses()
    {
        var version = new CustomBaseVersion { assetId = 999990, version = "cleanup-test" };
        string assetFolder = "Assets/MCB/generated/advancedMeshPayloads/999990";
        string folder = assetFolder + "/cleanup-test";
        var avatar = new GameObject("Avatar");
        try
        {
            CreateAssetFolder(folder);
            var used = AvatarBuilder.BuildGenericAvatar(avatar, "");
            var unused = AvatarBuilder.BuildGenericAvatar(avatar, "");
            AssetDatabase.CreateAsset(used, folder + "/payload.humanoid-used.asset");
            AssetDatabase.CreateAsset(unused, folder + "/payload.humanoid-unused.asset");
            avatar.AddComponent<Animator>().avatar = used;

            NativeMeshPayloadService.DeleteGeneratedPayloadsForVersion(version);
            Assert.That(AssetDatabase.LoadAssetAtPath<Avatar>(folder + "/payload.humanoid-used.asset"), Is.Not.Null);
            Assert.That(File.Exists(folder + "/payload.humanoid-unused.asset"), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(avatar);
            AssetDatabase.DeleteAsset(assetFolder);
        }
    }

    [Test]
    public void NestedPackagesAreScannedWithinTheArchiveBudget()
    {
        byte[] zip = Zip(("mcb logic.unitypackage", UnityPackage((Guid(), "Assets/Creator/Big.anim", new string('x', 64 * 1024)))));
        Assert.Less(zip.Length, 16 * 1024);
        using (var stream = new MemoryStream(zip))
            Assert.Throws<InvalidDataException>(() => VersionContentTrust.ListCode(stream, new VersionArchiveBudget(maxTotalBytes: 32 * 1024)));
        using (var stream = new MemoryStream(zip))
            Assert.Throws<InvalidDataException>(() => VersionContentTrust.ListCode(stream, new VersionArchiveBudget(maxEntries: 2)));
        using (var stream = new MemoryStream(zip))
            Assert.IsEmpty(VersionContentTrust.ListCode(stream));
    }

    [Test]
    public void TrustedOrCodeFreeDownloadsNeedNoConfirmation()
    {
        Assert.IsTrue(VersionContentTrust.ConfirmDownloadedCode(new CustomBaseVersion { assetId = 14, version = "1" }, null,
            new[] { "Editor/Setup.cs" },
            new VersionContentTrust.CreatorTrustSnapshot { assetId = 14, version = "1", creatorTrusted = true }));
        Assert.IsTrue(VersionContentTrust.ConfirmDownloadedCode(new CustomBaseVersion { version = "1" }, null,
            VersionContentTrust.ListCode(new MemoryStream(Zip(("model.bin", new byte[] { 1 }))))));
    }

    // ---- MCB-03: logic package dependencies

    [Test]
    public void LogicPackageImportKeepsOnlyMissingCreatorAssets()
    {
        string clip = Guid(), fury = Guid();
        string package = TempFile(".unitypackage", UnityPackage((clip, "Assets/Creator/flex.anim", "anim"),
            (fury, "Packages/com.vrcfury.vrcfury/Runtime/VF/Model/VRCFury.cs", "class VRCFury {}")));

        string import = FileManagerService.PrepareLogicPackageImport(package);
        tempPaths.Add(import);
        Assert.AreNotEqual(package, import);
        var entries = UnityPackageIndex.Read(import).Entries;
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(clip, entries[0].Guid);
        Assert.AreEqual("Assets/Creator/flex.anim", entries[0].Path);
    }

    [Test]
    public void LogicPackageIsImportedWhenAPrefabDependencyIsMissing()
    {
        string package = TempFile(".unitypackage", UnityPackage((Guid(), "Assets/Creator/flex.anim", "anim")));
        Assert.AreEqual(package, FileManagerService.PrepareLogicPackageImport(package));
    }

    [Test]
    public void LogicPackageIsSkippedWhenEveryCreatorAssetIsInstalled()
    {
        string installed = AssetDatabase.AssetPathToGUID("Packages/orbiters.mcb/package.json");
        Assert.IsNotEmpty(installed);
        string package = TempFile(".unitypackage", UnityPackage((installed, "Assets/Creator/package.json", "{}"),
            (Guid(), "Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/VRC.SDK3.Dynamics.Contact.dll", "dll")));
        Assert.IsNull(FileManagerService.PrepareLogicPackageImport(package));
    }

    [Test]
    public void MissingDependencyDoesNotReimportAnAlreadyInstalledCreatorAsset()
    {
        string installed = AssetDatabase.AssetPathToGUID("Packages/orbiters.mcb/package.json");
        Assert.IsNotEmpty(installed);
        string missing = Guid();
        string package = TempFile(".unitypackage", UnityPackage(
            (installed, "Assets/Creator/AlreadyEdited.json", "creator's old copy"),
            (missing, "Assets/Creator/NewClip.anim", "new clip")));

        string import = FileManagerService.PrepareLogicPackageImport(package);
        tempPaths.Add(import);
        Assert.AreNotEqual(package, import);
        var entries = UnityPackageIndex.Read(import).Entries;
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(missing, entries[0].Guid);
    }

    // A new version's dependency with an existing GUID is updated only over the copy a stored version installed.
    [Test]
    public void ChangedDependencyReplacesOnlyTheCopyAStoredVersionInstalled()
    {
        string folder = "Assets/MCB_DependencyTest_" + Guid();
        string path = folder + "/flex.anim.txt";
        try
        {
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            File.WriteAllText(path, "shipped by 1.0");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.IsNotEmpty(guid);
            string stored = TempFile(".unitypackage", UnityPackage((guid, "Assets/Creator/flex.anim.txt", "shipped by 1.0")));
            string same = TempFile(".unitypackage", UnityPackage((guid, "Assets/Creator/flex.anim.txt", "shipped by 1.0")));
            string updated = TempFile(".unitypackage", UnityPackage((guid, "Assets/Creator/flex.anim.txt", "shipped by 1.1")));

            Assert.IsNull(FileManagerService.PrepareLogicPackageImport(same, new[] { stored }), "Identical content is not imported again.");
            Assert.AreEqual(updated, FileManagerService.PrepareLogicPackageImport(updated, new[] { stored }),
                "The installed copy is the one 1.0 shipped: 1.1 updates it.");
            Assert.IsNull(FileManagerService.PrepareLogicPackageImport(updated, Array.Empty<string>()),
                "A copy no stored version shipped is not MCB's to replace.");

            File.WriteAllText(path, "edited by the user");
            Assert.IsNull(FileManagerService.PrepareLogicPackageImport(updated, new[] { stored }), "The user's edits are kept.");
        }
        finally
        {
            AssetDatabase.DeleteAsset(folder);
        }
    }

    // ---- Version responses reach only the inspector whose selection asked for them

    [Test]
    public void VersionResponsesMatchOnlyTheirOwnRequest()
    {
        var request = new VersionFetchRequest("Assets/Base/Body.fbx", "token-a", 14, "source-a");
        Assert.IsTrue(request.Matches(Path.GetFullPath("Assets/Base/Body.fbx"), "token-a", 14, "source-a"));
        Assert.IsFalse(request.Matches("Assets/Base/Body.fbx", "token-a", 15, "source-a"), "Another asset.");
        Assert.IsFalse(request.Matches("Assets/Other/Body.fbx", "token-a", 14, "source-a"), "Another source model.");
        Assert.IsFalse(request.Matches("Assets/Base/Body.fbx", "token-b", 14, "source-a"), "Another account.");
        Assert.IsFalse(request.Matches("Assets/Base/Body.fbx", "token-a", 14, "source-b"), "Another original base.");
        Assert.IsFalse(request.Matches(null, "token-a", 14, "source-a"));
        Assert.IsTrue(new VersionFetchRequest("Assets/Base/Body.fbx", "token-a", 14, null).Matches("Assets/Base/Body.fbx", "token-a", 14, ""));
    }

    // ---- Stopped editor coroutines run their cleanup

    [Test]
    public void StoppingAnEditorCoroutineDisposesEverySuspendedLevel()
    {
        var cleaned = new List<string>();
        IEnumerator Inner()
        {
            try { yield return null; yield return null; }
            finally { cleaned.Add("inner"); }
        }
        IEnumerator Outer()
        {
            try { yield return Inner(); }
            finally { cleaned.Add("outer"); }
        }

        var runners = (IList)typeof(EditorCoroutineUtility).GetField("ActiveRunners", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        EditorCoroutineUtility.StartCoroutineOwnerless(Outer());
        object runner = runners[runners.Count - 1];
        var update = runner.GetType().GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
        update.Invoke(runner, null); // Outer starts Inner.
        update.Invoke(runner, null); // Inner is suspended inside its try.
        Assert.IsEmpty(cleaned);
        runner.GetType().GetMethod("Stop").Invoke(runner, null);
        CollectionAssert.AreEqual(new[] { "inner", "outer" }, cleaned);
        Assert.IsFalse(runners.Contains(runner));
    }

    // ---- MCB-04: pending build identity

    [Test]
    public void PendingBuildKeyKeepsTheOriginalBaseIdentity()
    {
        var built = new CustomBaseVersion { assetId = 14, version = "0.5.0", defaultAviVersion = "1.0.0", sourceVersionKey = new string('a', 64) };
        var recovered = CreatorModeModule.ParsePendingBuildKey(CreatorModeModule.FormatPendingBuildKey(built));
        Assert.IsTrue(recovered.isUnsubmitted);
        Assert.AreEqual(MCBUtils.GetVersionDataPath(built), MCBUtils.GetVersionDataPath(recovered));

        built.sourceVersionKey = null;
        recovered = CreatorModeModule.ParsePendingBuildKey(CreatorModeModule.FormatPendingBuildKey(built));
        Assert.IsNull(recovered.sourceVersionKey);
        Assert.AreEqual(MCBUtils.GetVersionDataPath(built), MCBUtils.GetVersionDataPath(recovered));
        Assert.IsNull(CreatorModeModule.ParsePendingBuildKey("14|0.5.0|1.0.0"));
    }

    // ---- MCB-05: original-base variants of Avatar-only patches

    [Test]
    public void VariantOfAvatarOnlyPatchPointsAtTheRenamedAvatar()
    {
        string folder = TempFolder();
        File.WriteAllBytes(Path.Combine(folder, "Body avatar.asset"), new byte[] { 1, 2, 3, 4 });
        string key = TempFile(".fbx", new byte[] { 9, 9, 9 });
        var version = new CustomBaseVersion
        {
            sourceFiles = new[] { new ModelFileData { id = 1, path = "Assets/Base/Body.fbx", hash = "old" } },
            versionFiles = new[]
            {
                new ModelFileData
                {
                    path = "Body avatar.asset", hash = MCBUtils.CalculateFileHash(Path.Combine(folder, "Body avatar.asset")),
                    type = "PREFAB", role = "PATCH", transform = ModelFileTransforms.DirectAsset, sourceModelFileId = 1,
                    metadata = new Dictionary<string, object> { { "sourcePath", "Assets/Base/Body.fbx" }, { "customAvatarPath", "Body avatar.asset" } }
                }
            }
        };
        var target = new OriginalBaseVersionData { sourceFiles = new[] { new ModelFileData { id = 2, path = "Assets/Base/Body.fbx", hash = "new" } } };

        var patch = OriginalBaseVariantBuilder.Build(folder, version, target, _ => key).versionFiles.Single();

        Assert.AreNotEqual("Body avatar.asset", patch.path);
        Assert.AreEqual(patch.path, patch.metadata["customAvatarPath"]);
        Assert.IsTrue(File.Exists(Path.Combine(folder, patch.path)));
    }

    // ---- MCB-09: reset plus saved-custom copy are one transaction

    [TestCase(false)]
    [TestCase(true)]
    public void FailedCustomInstallRestoresTheReleaseAfterAnInnerResetCommit(bool failAfterCopy)
    {
        string folder = "Assets/MCB_TransactionTest_" + Guid();
        string assetPath = folder + "/Model.asset";
        var scene = EditorSceneManager.NewPreviewScene();
        var avatar = new GameObject("Avatar");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar, scene);
        var target = avatar.AddComponent<MyCustomBase>();
        var material = new Material(Shader.Find("Standard"));
        Undo.IncrementCurrentGroup();
        int setupGroup = Undo.GetCurrentGroup();
        object snapshot = null;
        var type = typeof(VersionActions).GetNestedType("VersionTransitionRollbackSnapshot", BindingFlags.NonPublic);
        try
        {
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            AssetDatabase.CreateAsset(material, assetPath);
            byte[] original = File.ReadAllBytes(assetPath);
            snapshot = type.GetMethod("Capture").Invoke(null, new object[] { new[] { assetPath }, target, null });
            // The reset commits its own scope while the custom install still owns the encompassing snapshot.
            var reset = new VersionSwitchUndoScope(avatar.transform, "Reset release");
            Undo.RecordObject(avatar.transform, "Reset pose");
            avatar.transform.localPosition = Vector3.up;
            Undo.FlushUndoRecordObjects();
            reset.Commit();
            if (failAfterCopy)
            {
                File.WriteAllText(assetPath, "replacement bytes before an import failure");
                // Failure at the import boundary must restore bytes and scene state, including the committed reset.
            }
            else
            {
                Assert.Throws<FileNotFoundException>(() => File.Copy(assetPath + ".missing", assetPath, true));
            }
            type.GetMethod("Rollback").Invoke(snapshot, null);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(assetPath));
            Assert.AreEqual(Vector3.zero, avatar.transform.localPosition);
            // Idempotence prevents an outer error handler from reverting unrelated Undo groups a second time.
            Undo.RecordObject(avatar.transform, "Later edit");
            avatar.transform.localPosition = Vector3.right;
            type.GetMethod("Rollback").Invoke(snapshot, null);
            Assert.AreEqual(Vector3.right, avatar.transform.localPosition);
        }
        finally
        {
            if (snapshot != null) type.GetMethod("Commit").Invoke(snapshot, null);
            Undo.RevertAllDownToGroup(setupGroup);
            AssetDatabase.DeleteAsset(folder);
            if (material != null) Object.DestroyImmediate(material);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    // ---- MCB-10: applied-version detection

    [Test]
    public void SecondaryModelHashIdentifiesTheAppliedRelease()
    {
        var a = Release("A", "hat-a");
        var b = Release("B", "hat-b");
        var hashes = new Dictionary<string, string> { { "Assets/Body.fbx", "body-x" }, { "Assets/Hat.fbx", "hat-b" } };

        Assert.AreSame(b, Select(new[] { a, b }, hashes, _ => false));
        hashes["Assets/Hat.fbx"] = "unrelated";
        Assert.IsNull(Select(new[] { a, b }, hashes, _ => false));
    }

    [Test]
    public void UnknownSecondaryHashesDoNotSelectAnArbitraryRelease()
    {
        var a = Release("A", "hat-a");
        var b = Release("B", "hat-b");
        var hashes = new Dictionary<string, string> { { "Assets/Body.fbx", "body-x" } };

        Assert.IsNull(Select(new[] { a, b }, hashes, _ => false));
        Assert.AreSame(b, Select(new[] { a, b }, hashes, v => v == b));
    }

    [Test]
    public void PersistedIdentityPicksAmongReleasesWithIdenticalMeshes()
    {
        var a = Release("A", "hat-x");
        var b = Release("B", "hat-x");
        var hashes = new Dictionary<string, string> { { "Assets/Body.fbx", "body-x" }, { "Assets/Hat.fbx", "hat-x" } };

        Assert.AreSame(b, Select(new[] { a, b }, hashes, v => v == b));
    }

    // ---- MCB-11: reset needs every original

    [Test]
    public void ResetStopsWhenAnyReplacedModelLacksItsBackup()
    {
        var replaced = new[] { "Assets/Base/Body.fbx", "Assets/Base/Hat.fbx" };
        var error = Assert.Throws<FileNotFoundException>(() => VersionActions.EnsureOriginalBackups(replaced, path => path.EndsWith("Body.fbx")));
        StringAssert.Contains("Hat.fbx", error.Message);
        StringAssert.DoesNotContain("Body.fbx", error.Message);
        Assert.DoesNotThrow(() => VersionActions.EnsureOriginalBackups(replaced, _ => true));
    }

    // ---- MCB-12: undo isolation

    [Test]
    public void FailedSynchronousSwitchKeepsPreparationTimeCreationDeletionAndMaterialEdits()
    {
        var avatarScene = EditorSceneManager.NewPreviewScene();
        var unrelatedScene = EditorSceneManager.NewPreviewScene();
        Undo.IncrementCurrentGroup();
        int setupGroup = Undo.GetCurrentGroup();
        var avatar = new GameObject("Avatar");
        var other = new GameObject("Unrelated");
        var deleted = new GameObject("Deleted during preparation");
        var material = new Material(Shader.Find("Standard"));
        GameObject created = null;
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar, avatarScene);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(other, unrelatedScene);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(deleted, unrelatedScene);
        try
        {
            IEnumerator Prepare() { yield return null; }
            var transition = VersionActions.RunPreparedMutation(Prepare(), () =>
            {
                var scope = new VersionSwitchUndoScope(avatar.transform, "Switch MCB Version");
                Undo.RecordObject(avatar.transform, "switch");
                avatar.transform.localPosition = Vector3.up;
                scope.Rollback();
            });
            Assert.IsTrue(transition.MoveNext(), "Preparation yields before a rollback range exists.");
            // These user edits happen while network/import/mesh preparation is suspended, before MCB captures Undo.
            Undo.RecordObject(other.transform, "User transform edit");
            other.transform.localPosition = Vector3.right * 5;
            Undo.RecordObject(material, "User material edit");
            material.color = Color.magenta;
            created = new GameObject("Created during preparation");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(created, unrelatedScene);
            Undo.RegisterCreatedObjectUndo(created, "User creation");
            Undo.DestroyObjectImmediate(deleted);
            Undo.FlushUndoRecordObjects();

            Assert.IsFalse(transition.MoveNext(), "The whole mutation/rollback finishes in this callback.");

            Assert.AreEqual(Vector3.zero, avatar.transform.localPosition);
            Assert.AreEqual(Vector3.right * 5, other.transform.localPosition);
            Assert.AreEqual(Color.magenta, material.color);
            Assert.IsTrue(created != null);
            Assert.IsTrue(deleted == null);
            Assert.IsFalse(unrelatedScene.GetRootGameObjects().Any(go => go.name == "Deleted during preparation"));
        }
        finally
        {
            Undo.RevertAllDownToGroup(setupGroup);
            // Reverting the deletion brings "Deleted during preparation" back outside its preview scene, into the user's.
            sandbox.DestroyNewRoots();
            Object.DestroyImmediate(avatar);
            Object.DestroyImmediate(other);
            Object.DestroyImmediate(material);
            EditorSceneManager.ClosePreviewScene(avatarScene);
            EditorSceneManager.ClosePreviewScene(unrelatedScene);
        }
    }

    [Test]
    public void SwitchWithoutOtherEditsRevertsItsWholeRange()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        Undo.IncrementCurrentGroup();
        int setupGroup = Undo.GetCurrentGroup();
        var avatar = new GameObject("Avatar");
        var child = new GameObject("Body");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar, scene);
        child.transform.SetParent(avatar.transform, false);
        try
        {
            var scope = new VersionSwitchUndoScope(avatar.transform, "Switch MCB Version");
            Undo.RecordObject(child.transform, "switch");
            child.transform.localScale = Vector3.one * 2f;
            Undo.IncrementCurrentGroup();
            var edit = new SerializedObject(avatar.transform);
            edit.FindProperty("m_LocalPosition").vector3Value = Vector3.forward;
            edit.ApplyModifiedProperties();

            scope.Rollback();

            Assert.AreEqual(Vector3.one, child.transform.localScale);
            Assert.AreEqual(Vector3.zero, avatar.transform.localPosition);
        }
        finally
        {
            Undo.RevertAllDownToGroup(setupGroup);
            Object.DestroyImmediate(avatar);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    [Test]
    public void SharedLogoutCallbackClearsAnOpenInspectorWithoutReadingOrChangingCredentials()
    {
        // Avoid OnEnable: real inspector initialization starts background services and reads the user's account.
        var inspector = (MCBEditor)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(MCBEditor));
        inspector.authToken = "fixture-stale-token";
        inspector.isAuthenticated = true;
        inspector.accessDeniedAssetId = "fixture-denied";
        inspector.fetchError = "fixture error";
        inspector.SyncAuthentication((AuthenticationService.AuthData)null);
        Assert.IsNull(inspector.authToken);
        Assert.IsFalse(inspector.isAuthenticated);
        Assert.IsNull(inspector.accessDeniedAssetId);
        Assert.IsNull(inspector.fetchError);
        Assert.DoesNotThrow(() => inspector.SyncAuthentication((AuthenticationService.AuthData)null));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CopiedLocalFbxRollbackRestoresFileImporterAndPreviewCloneAfterReset(bool failAfterImport)
    {
        string source = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) &&
                path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .OrderBy(path => new FileInfo(path).Length).FirstOrDefault();
        if (source == null) Assert.Ignore("This project has no local FBX available for the isolated import fixture.");
        string folder = "Assets/MCB_FbxRollbackTest_" + Guid();
        string copy = folder + "/Model.fbx";
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject clone = null;
        object snapshot = null;
        var type = typeof(VersionActions).GetNestedType("VersionTransitionRollbackSnapshot", BindingFlags.NonPublic);
        Undo.IncrementCurrentGroup();
        int setupGroup = Undo.GetCurrentGroup();
        try
        {
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            File.Copy(source, copy);
            AssetDatabase.ImportAsset(copy, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(copy);
            Assert.IsNotNull(model);
            clone = Object.Instantiate(model);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
            var target = clone.AddComponent<MyCustomBase>();
            target.appliedCustomBaseVersionString = "fixture-release";
            Vector3 originalPosition = clone.transform.localPosition;
            byte[] originalBytes = File.ReadAllBytes(copy), originalMeta = File.ReadAllBytes(copy + ".meta");
            snapshot = type.GetMethod("Capture").Invoke(null, new object[] { new[] { copy }, target, null });
            var reset = new VersionSwitchUndoScope(clone.transform, "Reset release");
            Undo.RecordObject(clone.transform, "Reset pose");
            Undo.RecordObject(target, "Reset marker");
            clone.transform.localPosition = Vector3.up * 42;
            target.appliedCustomBaseVersionString = "";
            var importer = (ModelImporter)AssetImporter.GetAtPath(copy);
            importer.isReadable = !importer.isReadable;
            importer.SaveAndReimport();
            reset.Commit();
            if (failAfterImport)
            {
                File.Copy(source, copy, true);
                AssetDatabase.ImportAsset(copy, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                // Exercise recovery at the importer failure boundary without installing a failing global postprocessor.
            }
            else Assert.Throws<FileNotFoundException>(() => File.Copy(copy + ".missing", copy, true));
            type.GetMethod("Rollback").Invoke(snapshot, null);
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(copy));
            CollectionAssert.AreEqual(originalMeta, File.ReadAllBytes(copy + ".meta"));
            Assert.AreEqual(originalPosition, clone.transform.localPosition);
            Assert.AreEqual("fixture-release", target.appliedCustomBaseVersionString);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(copy));
        }
        finally
        {
            if (snapshot != null) type.GetMethod("Commit").Invoke(snapshot, null);
            Undo.RevertAllDownToGroup(setupGroup);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.DeleteAsset(folder);
        }
    }

    // ---- helpers

    private static CustomBaseVersion Select(CustomBaseVersion[] versions, Dictionary<string, string> hashes, Func<CustomBaseVersion, bool> persisted) =>
        VersionActions.SelectAppliedVersionByHashes(versions, hashes["Assets/Body.fbx"], hashes, source => source.path, persisted);

    private static CustomBaseVersion Release(string name, string hatOutput) => new CustomBaseVersion
    {
        version = name, defaultAviVersion = "1", appliedCustomAviHash = "body-x",
        sourceFiles = new[]
        {
            new ModelFileData { id = 1, path = "Assets/Body.fbx", hash = "body-original" },
            new ModelFileData { id = 2, path = "Assets/Hat.fbx", hash = "hat-original" }
        },
        versionFiles = new[]
        {
            new ModelFileData { role = "PATCH", transform = ModelFileTransforms.XorBinToFbx, sourceModelFileId = 1, outputHash = "body-x" },
            new ModelFileData { role = "PATCH", transform = ModelFileTransforms.XorBinToFbx, sourceModelFileId = 2, outputHash = hatOutput }
        }
    };

    private static string Guid() => System.Guid.NewGuid().ToString("N");

    private static void CreateAssetFolder(string folder)
    {
        string parent = "Assets";
        foreach (string segment in folder.Substring("Assets/".Length).Split('/'))
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + segment)) AssetDatabase.CreateFolder(parent, segment);
            parent += "/" + segment;
        }
    }

    private string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-switch-test-" + Guid());
        Directory.CreateDirectory(folder);
        tempPaths.Add(folder);
        return folder;
    }

    private string TempFile(string extension, byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "mcb-switch-test-" + Guid() + extension);
        File.WriteAllBytes(path, bytes);
        tempPaths.Add(path);
        return path;
    }

    private static byte[] Zip(params (string name, byte[] bytes)[] entries)
    {
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var entry in entries)
                    using (var stream = archive.CreateEntry(entry.name).Open())
                        stream.Write(entry.bytes, 0, entry.bytes.Length);
            return memory.ToArray();
        }
    }

    private static byte[] UnityPackage(params (string guid, string pathname, string content)[] entries)
    {
        using (var memory = new MemoryStream())
        {
            using (var gzip = new GZipStream(memory, CompressionMode.Compress, true))
            {
                foreach (var entry in entries)
                {
                    TarEntry(gzip, entry.guid + "/pathname", Encoding.UTF8.GetBytes(entry.pathname));
                    TarEntry(gzip, entry.guid + "/asset", Encoding.UTF8.GetBytes(entry.content));
                    TarEntry(gzip, entry.guid + "/asset.meta", Encoding.UTF8.GetBytes("fileFormatVersion: 2\nguid: " + entry.guid + "\n"));
                }
                gzip.Write(new byte[1024], 0, 1024);
            }
            return memory.ToArray();
        }
    }

    private static void TarEntry(Stream output, string name, byte[] bytes)
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(Convert.ToString(bytes.Length, 8).PadLeft(11, '0')).CopyTo(header, 124);
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);
        for (int i = 148; i < 156; i++) header[i] = (byte)' ';
        int checksum = header.Sum(value => (int)value);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        output.Write(header, 0, 512);
        output.Write(bytes, 0, bytes.Length);
        output.Write(new byte[(512 - bytes.Length % 512) % 512], 0, (512 - bytes.Length % 512) % 512);
    }
}
#endif
