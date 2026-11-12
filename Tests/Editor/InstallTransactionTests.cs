using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

// Custom body migration inside the install: only when needed, atomic, and going back writes only what was recorded.
public class InstallTransactionTests
{
    const string Folder = "Assets/__McbInstallTransactionTests";
    GameObject avatar, original;
    Mesh mesh, source;

    void Create()
    {
        Assert.That(AssetDatabase.IsValidFolder(Folder), Is.False);
        AssetDatabase.CreateFolder("Assets", "__McbInstallTransactionTests");
        mesh = new Mesh { name = "Personal body", vertices = new[] { Vector3.zero }, subMeshCount = 1 };
        source = new Mesh { name = "Original body", vertices = new[] { Vector3.one }, subMeshCount = 1 };
        AssetDatabase.CreateAsset(mesh, Folder + "/custom.asset");
        AssetDatabase.CreateAsset(source, Folder + "/original.asset");
        avatar = Rig("Avatar", mesh); original = Rig("Original", source);
        avatar.AddComponent<MyCustomBase>();
        avatar.AddComponent<VRCAvatarDescriptor>();
        avatar.AddComponent<Animator>();
    }

    static GameObject Rig(string name, Mesh mesh)
    {
        var root = new GameObject(name);
        var bone = new GameObject("Bone"); bone.transform.SetParent(root.transform);
        var body = new GameObject("Body"); body.transform.SetParent(root.transform);
        var r = body.AddComponent<SkinnedMeshRenderer>(); r.sharedMesh = mesh;
        r.bones = new[] { bone.transform }; r.rootBone = bone.transform;
        return root;
    }

    [TearDown] public void Dispose()
    {
        if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
        if (original != null) UnityEngine.Object.DestroyImmediate(original);
        if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
    }

    // 1. A body whose model file is named like an original is stock: no server answer, no hashing needed.
    [Test] public void StockBodyIsKnownWithoutTheServerOrHashing()
    {
        Create();
        var owner = avatar.GetComponent<MyCustomBase>();
        owner.baseFbxFiles = new System.Collections.Generic.List<GameObject> { original };
        var byName = new[] { new ModelFileData { path = "Assets/Somewhere/custom.asset", hash = new string('0', 64) } };
        Assert.That(LegacyMigrationService.HasCustomBaseRenderers(owner, avatar.transform, byName), Is.False);
        var other = new[] { new ModelFileData { path = "Assets/Somewhere/RexouiumOriginal.fbx", hash = new string('0', 64) } };
        Assert.That(LegacyMigrationService.HasCustomBaseRenderers(owner, avatar.transform, other), Is.True);
    }

    // 3. A failure after the migration started puts the body, its record and the files back.
    [Test] public void FailureAfterMigrationPutsTheBodyBack()
    {
        Create();
        var renderer = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
        var sceneBody = new Mesh { name = "Scene body", vertices = new[] { Vector3.up }, subMeshCount = 1 };
        renderer.sharedMesh = sceneBody;
        renderer.sharedMaterials = new Material[] { null };
        var matches = new System.Collections.Generic.List<LegacyMigrationService.Match> { new LegacyMigrationService.Match { assetId = 7, legacyModelPath = "",
            legacyRelease = new LegacyMigrationService.Release { label = "Your custom body" } } };
        bool hadFolder = AssetDatabase.IsValidFolder(LegacyMigrationService.MigratedFolder);
        int filesBefore = hadFolder ? Directory.GetFiles(LegacyMigrationService.MigratedFolder, "*", SearchOption.AllDirectories).Length : 0;
        var transaction = AvatarTransaction.Begin(avatar.transform, "Install test");
        try
        {
            LegacyMigrationService.Migrate(avatar.transform, matches, original);
            Assert.That(renderer.sharedMesh, Is.SameAs(source));
            Assert.That(avatar.GetComponent<MyCustomBase>().legacyOriginal.renderers[0].savedMesh, Is.Not.Null, "a scene-only body is saved");
            transaction.Fail();
        }
        finally { transaction.End(); }
        Assert.That(renderer.sharedMesh, Is.SameAs(sceneBody));
        Assert.That(renderer.sharedMaterials, Is.EqualTo(new Material[] { null }));
        Assert.That(avatar.GetComponent<MyCustomBase>().legacyOriginal?.migrated ?? false, Is.False);
        int filesAfter = AssetDatabase.IsValidFolder(LegacyMigrationService.MigratedFolder)
            ? Directory.GetFiles(LegacyMigrationService.MigratedFolder, "*", SearchOption.AllDirectories).Length : 0;
        Assert.That(filesAfter, Is.EqualTo(filesBefore));
        UnityEngine.Object.DestroyImmediate(sceneBody);
    }

    // 4. A record without the descriptor's state leaves descriptor and Animator alone; empty material slots stay empty.
    [Test] public void RecordWithoutAvatarStateLeavesTheDescriptorAndKeepsEmptySlots()
    {
        Create();
        var renderer = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
        var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        descriptor.expressionsMenu = menu;
        var owner = avatar.GetComponent<MyCustomBase>();
        owner.legacyOriginal = new LegacyOriginal { migrated = true, label = "Old release", renderers = { new LegacyRendererState {
            renderer = renderer, name = "Body", mesh = mesh, bones = renderer.bones, rootBone = renderer.rootBone, materials = new Material[] { null } } } };
        renderer.sharedMesh = source;
        renderer.sharedMaterials = new[] { new Material(Shader.Find("Standard")) };
        try
        {
            Assert.That(LegacyMigrationService.RestoreOriginal(avatar.transform), Is.EqualTo("Old release"));
            Assert.That(descriptor.expressionsMenu, Is.SameAs(menu));
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(renderer.sharedMaterials, Is.EqualTo(new Material[] { null }));
        }
        finally { UnityEngine.Object.DestroyImmediate(menu); }
    }

    // 4. Only slots that still hold MCB's copies go back; a slot the person changed since stays.
    [Test] public void OnlySlotsStillHoldingMcbsCopiesGoBack()
    {
        Create();
        var originalMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>(); var copyMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
        var originalParameters = ScriptableObject.CreateInstance<VRCExpressionParameters>(); var copyParameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
        var chosen = ScriptableObject.CreateInstance<VRCExpressionParameters>();
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        descriptor.expressionsMenu = copyMenu; descriptor.expressionParameters = chosen;
        var owner = avatar.GetComponent<MyCustomBase>();
        owner.legacyOriginal = new LegacyOriginal { migrated = true, hasAvatarState = true, label = "Old release",
            assets = { new LegacyAssetSwap { original = originalMenu, copy = copyMenu }, new LegacyAssetSwap { original = originalParameters, copy = copyParameters } } };
        try
        {
            LegacyMigrationService.RestoreOriginal(avatar.transform);
            Assert.That(descriptor.expressionsMenu, Is.SameAs(originalMenu));
            Assert.That(descriptor.expressionParameters, Is.SameAs(chosen));
        }
        finally { foreach (var o in new UnityEngine.Object[] { originalMenu, copyMenu, originalParameters, copyParameters, chosen }) UnityEngine.Object.DestroyImmediate(o); }
    }

    // 2. With no original in the lookup, the answer is immediate and names the accepted file.
    [Test] public void MissingOriginalAnswersAtOnceNamingTheFile()
    {
        Create();
        LegacyMigrationService.HiddenFromOriginalLookup = path => true;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var error = Assert.Throws<InvalidOperationException>(() => LegacyMigrationService.PlanCustomBody(avatar.transform, 7,
                new[] { new ModelFileData { path = "Assets/Base/RexouiumOriginal.fbx", hash = new string('0', 64) } }, new LegacyMigrationService.Match[0]));
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(2000));
            Assert.That(error.Message, Does.Contain("RexouiumOriginal.fbx").And.Contain("Nothing was changed"));
        }
        finally { LegacyMigrationService.HiddenFromOriginalLookup = null; }
    }
}
