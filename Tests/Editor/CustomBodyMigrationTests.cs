using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

public class CustomBodyMigrationTests
{
    const string Folder = "Assets/__McbCustomBodyTests";
    GameObject avatar, original;
    Mesh mesh, source;
    void Create()
    {
        Assert.That(AssetDatabase.IsValidFolder(Folder), Is.False);
        AssetDatabase.CreateFolder("Assets", "__McbCustomBodyTests");
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
    List<LegacyMigrationService.Match> Matches() => new List<LegacyMigrationService.Match> {
        new LegacyMigrationService.Match { assetId = 7, legacyModelPath = AssetDatabase.GetAssetPath(mesh),
            legacyRelease = new LegacyMigrationService.Release { label = "Your custom body" } }
    };
    [TearDown] public void Dispose()
    {
        if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
        if (original != null) UnityEngine.Object.DestroyImmediate(original);
        if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
    }
    [Test] public void NonOriginalModelAtBasePathIsCustomButKnownOriginalIsNot()
    {
        Create();
        var files = new[] { new ModelFileData { path = AssetDatabase.GetAssetPath(source), hash = ContentHash.Proof(AssetDatabase.GetAssetPath(source)) } };
        var paths = new[] { "Body" };
        Assert.That(LegacyMigrationService.UnregisteredBaseRenderers(avatar.transform, paths, new LegacyMigrationService.Cleanup(), Array.Empty<string>(), AssetDatabase.GetAssetPath(source), files).Count, Is.EqualTo(1));
        avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = source;
        Assert.That(LegacyMigrationService.UnregisteredBaseRenderers(avatar.transform, paths, new LegacyMigrationService.Cleanup(), Array.Empty<string>(), AssetDatabase.GetAssetPath(source), files), Is.Empty);
    }
    // The pose comes back from the version's pose ledger: restoring the original body leaves bones where they are.
    [Test] public void EmptyCleanupMigrationAndReturnRestorePersonalBodyAndLeaveThePose()
    {
        Create();
        var bone = avatar.transform.Find("Bone"); bone.localPosition = Vector3.up;
        var r = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
        LegacyMigrationService.Migrate(avatar.transform, Matches(), original);
        Assert.That(r.sharedMesh, Is.SameAs(source));
        Assert.That(avatar.GetComponent<MyCustomBase>().legacyOriginal.migrated, Is.True);
        bone.localPosition = Vector3.down;
        LegacyMigrationService.RestoreOriginal(avatar.transform);
        Assert.That(r.sharedMesh, Is.SameAs(mesh));
        Assert.That(bone.localPosition, Is.EqualTo(Vector3.down));
        Assert.That(avatar.GetComponent<MyCustomBase>().legacyOriginal.migrated, Is.False);
    }
    [Test] public void MissingBoneStopsBeforeChangingAnyRenderer()
    {
        Create(); avatar.transform.Find("Bone").name = "Other";
        Assert.Throws<InvalidOperationException>(() => LegacyMigrationService.Migrate(avatar.transform, Matches(), original));
        Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
        Assert.That(avatar.GetComponent<MyCustomBase>().legacyOriginal?.migrated ?? false, Is.False);
    }
    [Test] public void MissingOriginalStopsBeforeDirectApplyWithAcceptedFilename()
    {
        Create(); bool applied = false;
        var e = Assert.Throws<InvalidOperationException>(() => {
            LegacyMigrationService.PlanCustomBody(avatar.transform, 7, new[] { new ModelFileData { path = "Assets/RequiredOriginal.fbx", hash = new string('0', 64) } }, new LegacyMigrationService.Match[0]);
            applied = true;
        });
        Assert.That(e.Message, Does.Contain("RequiredOriginal.fbx")); Assert.That(applied, Is.False);
        Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
    }
    [Test] public void AppliedVersionSkipsPreparation()
    {
        Create(); var owner = avatar.GetComponent<MyCustomBase>(); owner.appliedCustomBaseAssetId = 7;
        Assert.That(LegacyMigrationService.PrepareApply(McbSession.Detached(owner), 7, () => Assert.Fail()).MoveNext(), Is.False);
    }

    [Test] public void UnsavedCustomMeshCannotReachDirectInstall()
    {
        Create(); var unsaved = new Mesh();
        try {
            avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh = unsaved;
            var error = Assert.Throws<InvalidOperationException>(() => LegacyMigrationService.UnregisteredBaseRenderers(avatar.transform,
                new[] { "Body" }, new LegacyMigrationService.Cleanup(), Array.Empty<string>(), "original.fbx", Array.Empty<ModelFileData>()));
            Assert.That(error.Message, Does.Contain("Save the custom body mesh"));
            Assert.That(avatar.GetComponentInChildren<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(unsaved));
        } finally { UnityEngine.Object.DestroyImmediate(unsaved); }
    }
}
