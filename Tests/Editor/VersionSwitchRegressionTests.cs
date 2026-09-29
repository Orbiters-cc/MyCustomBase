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
using Object = UnityEngine.Object;

public sealed class VersionSwitchRegressionTests
{
    private readonly List<string> tempPaths = new List<string>();

    [TearDown]
    public void TearDown()
    {
        foreach (string path in tempPaths)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        tempPaths.Clear();
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
