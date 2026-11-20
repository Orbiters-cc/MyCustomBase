using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// A body on another base than the one a version was made on (Rexouium 1.7 under a version made on Rexouium 1.6): a version
// that brings its own skeleton installs on it as it is. A personal edit of an original (the original is in the project and
// the avatar has its bones) is migrated as before, and so are versions that need the original file and bodies from a
// recognised older release.
public class UnknownBodyInstallTests
{
    const string OriginalName = "RexouiumRig1.6_V265_Fthrs_ReparentChestUp.fbx";
    const string OriginalPath = "Assets/Avatars/Rexouium/FBX_Rigs/Rig1.6 (HeadJntMovedUp)/" + OriginalName;
    string folder;
    GameObject avatar, model;
    Mesh mesh;
    McbSession session;

    void Create(string token = "test-token", bool hideOriginal = true)
    {
        folder = "Assets/__McbUnknownBodyTests_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        mesh = new Mesh { name = "Rexouium 1.7 body", vertices = new[] { Vector3.zero }, subMeshCount = 1 };
        AssetDatabase.CreateAsset(mesh, folder + "/RexouiumRig1.7_V510_HD body.asset");
        avatar = Rig("Avatar"); model = Rig("Model");
        Own(avatar, model);
        session.authToken = token;
        // As in the buyer's project before they imported Rexouium 1.6: no original model file can be found.
        if (hideOriginal) LegacyMigrationService.HiddenFromOriginalLookup = path => true;
    }

    void Own(GameObject body, GameObject baseModel)
    {
        var owner = body.AddComponent<MyCustomBase>();
        if (body.GetComponent<VRCAvatarDescriptor>() == null) body.AddComponent<VRCAvatarDescriptor>();
        if (body.GetComponent<Animator>() == null) body.AddComponent<Animator>();
        owner.baseFbxFiles = new List<GameObject> { baseModel };
        session = McbSession.Detached(owner);
    }

    GameObject Rig(string name)
    {
        var root = new GameObject(name);
        var hips = new GameObject("Hips"); hips.transform.SetParent(root.transform);
        var body = new GameObject("Body"); body.transform.SetParent(root.transform);
        var r = body.AddComponent<SkinnedMeshRenderer>(); r.sharedMesh = mesh;
        r.bones = new[] { hips.transform }; r.rootBone = hips.transform;
        return root;
    }

    static CustomBaseVersion VersionOn(string originalHash, params string[] transforms) => new CustomBaseVersion
    {
        assetId = 7, version = "5.0.0",
        sourceFiles = new[] { new ModelFileData { path = OriginalPath, hash = originalHash, role = "SOURCE", type = "FBX" } },
        versionFiles = transforms.Select((t, i) => new ModelFileData { role = "PATCH", type = "BIN", transform = t, path = "body_" + i + ".bin" }).ToArray()
    };

    static CustomBaseVersion Version(params string[] transforms) => VersionOn(new string('a', 64), transforms);

    LegacyMigrationService.MigrationPlan Plan(CustomBaseVersion version, Task<List<LegacyMigrationService.Match>> identified)
    {
        LegacyMigrationService.IdentifyForTests = () => identified;
        LegacyMigrationService.MigrationPlan plan = null;
        var work = LegacyMigrationService.PlanApply(session, 7, planned => plan = planned, version);
        while (work.MoveNext()) { }
        return plan;
    }

    LegacyMigrationService.MigrationPlan Plan(CustomBaseVersion version, params LegacyMigrationService.Match[] identified) =>
        Plan(version, Task.FromResult(identified.ToList()));

    [TearDown] public void Dispose()
    {
        LegacyMigrationService.IdentifyForTests = null;
        LegacyMigrationService.HiddenFromOriginalLookup = null;
        if (avatar != null) UnityEngine.Object.DestroyImmediate(avatar);
        if (model != null && !AssetDatabase.Contains(model)) UnityEngine.Object.DestroyImmediate(model);
        // Only the folder this test created.
        if (folder != null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
    }

    [Test] public void BodyIsCustomForMcb()
    {
        Create();
        Assert.That(LegacyMigrationService.HasCustomBaseRenderers(session.customBaseTarget, avatar.transform, Version().sourceFiles), Is.True);
    }

    // The buyer's first message ("MCB needs the base's original model in this project") came from moving the body onto the
    // original before a version that never reads that file.
    [Test] public void VersionWithItsOwnSkeletonInstallsOnTheBodyAsItIsWithoutTheOriginal()
    {
        Create();
        Assume.That(session.HasServerAccess, "MCB considers itself offline in this editor");
        var r = avatar.GetComponentInChildren<SkinnedMeshRenderer>();
        Assert.That(Plan(Version(NativeMeshPayloadService.PlainTransformName, NativeMeshPayloadService.PlainTransformName)), Is.Null);
        Assert.That(r.sharedMesh, Is.SameAs(mesh));
        Assert.That(session.customBaseTarget.legacyOriginal?.migrated ?? false, Is.False);
    }

    // A personal edit of the original (the original in the project, every bone its body is skinned to on the avatar) is
    // migrated as before; the same avatar without one of those bones (another base) installs directly.
    [Test] public void PersonalEditOfTheOriginalIsMigratedAnotherBaseIsNot()
    {
        var original = AssetDatabase.LoadAssetAtPath<GameObject>(OriginalPath);
        Assume.That(original, Is.Not.Null, "Rexouium 1.6 is not in this project");
        folder = "Assets/__McbUnknownBodyTests_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        mesh = new Mesh { name = "Sculpted body", vertices = new[] { Vector3.zero }, subMeshCount = 1 };
        AssetDatabase.CreateAsset(mesh, folder + "/sculpted body.asset");
        avatar = UnityEngine.Object.Instantiate(original);
        avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        model = original;
        Own(avatar, original);
        session.authToken = "test-token";
        Assume.That(session.HasServerAccess, "MCB considers itself offline in this editor");
        string hash = ContentHash.Proof(OriginalPath);

        var plan = Plan(VersionOn(hash, NativeMeshPayloadService.PlainTransformName));
        Assert.That(plan, Is.Not.Null, "a personal edit of the original takes the migration, as in MCB 1.14");
        Assert.That(plan.OriginalPath, Is.EqualTo(OriginalPath));

        UnityEngine.Object.DestroyImmediate(avatar.transform.Find("Armature/Hips/Belly").gameObject);
        Assert.That(Plan(VersionOn(hash, NativeMeshPayloadService.PlainTransformName)), Is.Null, "without Belly it is another base: direct install");
        Assert.That(session.customBaseTarget.legacyOriginal?.migrated ?? false, Is.False);
    }

    [Test] public void VersionKeyedOrPatchedOnTheOriginalStillNeedsIt()
    {
        Create();
        Assume.That(session.HasServerAccess, "MCB considers itself offline in this editor");
        foreach (var version in new[] { Version(NativeMeshPayloadService.TransformName), Version(NativeMeshPayloadService.PlainTransformName, NativeMeshPayloadService.TransformName),
                     Version(NativeMeshPayloadService.PlainTransformName, ModelFileTransforms.HdiffXorBinToFbx) })
        {
            var error = Assert.Throws<InvalidOperationException>(() => Plan(version));
            Assert.That(error.Message, Does.Contain(OriginalName));
        }
    }

    // A recognised older release keeps its migration (its own layers and objects are cleaned up on the way).
    [Test] public void RecognisedReleaseIsStillMigrated()
    {
        Create();
        Assume.That(session.HasServerAccess, "MCB considers itself offline in this editor");
        var release = new LegacyMigrationService.Match { assetId = 7, kind = "legacy", legacyModelPath = AssetDatabase.GetAssetPath(mesh),
            legacyRelease = new LegacyMigrationService.Release { label = "Ultirex V4" }, originals = Version().sourceFiles.ToList() };
        var error = Assert.Throws<InvalidOperationException>(() => Plan(Version(NativeMeshPayloadService.PlainTransformName), release));
        Assert.That(error.Message, Does.Contain(OriginalName));
    }

    // A failed or incomplete check never reads as "nothing recognised".
    [Test] public void FailedCheckRefusesInsteadOfInstallingDirectly()
    {
        Create();
        Assume.That(session.HasServerAccess, "MCB considers itself offline in this editor");
        var failed = Task.FromException<List<LegacyMigrationService.Match>>(new System.IO.InvalidDataException("the server's answer was incomplete"));
        var error = Assert.Throws<InvalidOperationException>(() => Plan(Version(NativeMeshPayloadService.PlainTransformName), failed));
        Assert.That(error.Message, Does.Contain("could not check").And.Contain("Nothing was changed"));
    }

    // Offline MCB cannot tell another base from an older release that needs cleaning up: it asks to sign in, and never for a
    // model file the version does not need.
    [Test] public void OfflineAsksToSignInRatherThanForTheOriginal()
    {
        Create(token: null);
        var error = Assert.Throws<InvalidOperationException>(() => Plan(Version(NativeMeshPayloadService.PlainTransformName)));
        Assert.That(error.Message, Does.Contain("Sign in").And.Not.Contain(OriginalName));
    }

    [Test] public void OnlyPlainAdvancedMeshesInstallOnAnyModel()
    {
        Assert.That(NativeMeshPayloadService.InstallsOnAnyModel(Version(NativeMeshPayloadService.PlainTransformName)), Is.True);
        Assert.That(NativeMeshPayloadService.InstallsOnAnyModel(Version(NativeMeshPayloadService.TransformName)), Is.False);
        Assert.That(NativeMeshPayloadService.InstallsOnAnyModel(Version()), Is.False, "a version without meshes");
        Assert.That(NativeMeshPayloadService.InstallsOnAnyModel(null), Is.False);
    }

    // A model exported in centimetres (armature at scale 100) is refused before the version writes its skeleton.
    [Test] public void ArmatureAtAnotherScaleIsRefused()
    {
        avatar = new GameObject("Centimetres");
        var armature = new GameObject("Armature").transform; armature.SetParent(avatar.transform, false);
        new GameObject("Hips").transform.SetParent(armature, false);
        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        try
        {
            payload.bones.Add(new NativeMeshPayloadBone { path = "Armature", localRotation = Quaternion.identity, localScale = Vector3.one });
            payload.bones.Add(new NativeMeshPayloadBone { path = "Armature/Hips", localRotation = Quaternion.identity, localScale = Vector3.one });
            Assert.DoesNotThrow(() => NativeMeshPayloadService.RefuseOtherArmatureScale(avatar.transform, payload));
            armature.localScale = Vector3.one * 100f;
            var error = Assert.Throws<InvalidOperationException>(() => NativeMeshPayloadService.RefuseOtherArmatureScale(avatar.transform, payload));
            Assert.That(error.Message, Does.Contain("'Armature'").And.Contain("100").And.Contain("nothing was changed"));
            Assert.That(armature.localScale, Is.EqualTo(Vector3.one * 100f));
        }
        finally { UnityEngine.Object.DestroyImmediate(payload); }
    }
}

// The humanoid MCB builds for a version's skeleton is looked up by what it is made of: a Digitigrade Rexouium 1.7 (legs
// on its Planti_* chain) must never get the plantigrade humanoid another avatar built for the same payload.
public class NativeHumanoidKeyTests
{
    static HumanDescription Mapping(string upperLeg, float stretch = 0.05f)
    {
        return new HumanDescription
        {
            human = new[]
            {
                new HumanBone { humanName = "Hips", boneName = "Hips", limit = new HumanLimit { useDefaultValues = true } },
                new HumanBone { humanName = "LeftUpperLeg", boneName = upperLeg, limit = new HumanLimit { useDefaultValues = true } },
            },
            armStretch = stretch, legStretch = 0.05f, upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f, feetSpacing = 0f,
        };
    }

    [Test] public void KeyFollowsTheBoneMappingAndSettings()
    {
        Assert.That(AvatarDefinitionGenerationService.MappingKey(Mapping("Left leg")), Is.EqualTo(AvatarDefinitionGenerationService.MappingKey(Mapping("Left leg"))));
        Assert.That(AvatarDefinitionGenerationService.MappingKey(Mapping("Planti_Left_Leg")), Is.Not.EqualTo(AvatarDefinitionGenerationService.MappingKey(Mapping("Left leg"))));
        Assert.That(AvatarDefinitionGenerationService.MappingKey(Mapping("Left leg", 0.2f)), Is.Not.EqualTo(AvatarDefinitionGenerationService.MappingKey(Mapping("Left leg"))));
    }
}
