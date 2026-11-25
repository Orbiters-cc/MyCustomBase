using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public class PoseLedgerTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void Dispose()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
        string folder = VersionPoseLedgerService.ReboundFolder + "/PoseLedgerTestAvatar";
        if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
    }

    private GameObject Own(GameObject go) { owned.Add(go); return go; }

    private static Transform Child(Transform parent, string name, Vector3 position)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); t.localPosition = position; return t;
    }

    // Model: Armature/Hips/{Leg, Tail}. Avatar: the same, posed by the person (Tail turned, Hips raised).
    private (GameObject model, GameObject avatar, MyCustomBase owner) Rig()
    {
        var model = Own(new GameObject("Model"));
        var mh = Child(Child(model.transform, "Armature", Vector3.zero), "Hips", Vector3.up);
        Child(mh, "Leg", Vector3.left); Child(mh, "Tail", Vector3.back);
        var avatar = Own(new GameObject("Avatar"));
        var owner = avatar.AddComponent<MyCustomBase>();
        var hips = Child(Child(avatar.transform, "Armature", Vector3.zero), "Hips", Vector3.up + new Vector3(0, .03f, 0));
        Child(hips, "Leg", Vector3.left);
        Child(hips, "Tail", Vector3.back).localRotation = Quaternion.Euler(10, 0, 0);
        return (model, avatar, owner);
    }

    [Test] public void InstallKeepsThePersonsPoseAndGoingBackIsExact()
    {
        var (model, avatar, owner) = Rig();
        var hips = avatar.transform.Find("Armature/Hips"); var tail = hips.Find("Tail"); var leg = hips.Find("Leg");
        var before = avatar.GetComponentsInChildren<Transform>().ToDictionary(t => t, t => (t.parent, t.GetSiblingIndex(), t.localPosition, t.localRotation, t.localScale));
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        // The install: every bone to the model rest, then the version moves the leg under a new bone and lengthens it.
        hips.localPosition = Vector3.up; tail.localRotation = Quaternion.identity;
        var top = new GameObject("Top").transform; top.SetParent(hips, false); leg.SetParent(top, false); leg.localPosition = Vector3.left * 2;
        VersionPoseLedgerService.CarryPose(snapshot);
        Assert.That(tail.localRotation, Is.EqualTo(Quaternion.Euler(10, 0, 0)), "a bone the version left at rest ends at the person's pose");
        Assert.That(hips.localPosition, Is.EqualTo(Vector3.up + new Vector3(0, .03f, 0)));
        Assert.That(leg.localPosition, Is.EqualTo(Vector3.left * 2), "an unposed bone the version changed keeps the version's pose");
        VersionPoseLedgerService.Record(owner, snapshot);
        Assert.That(owner.poseLedger.recorded, Is.True);
        // The person turns the tail further while the version is on.
        tail.localRotation = tail.localRotation * Quaternion.Euler(0, 5, 0);
        var pending = VersionPoseLedgerService.BeginRestore(owner, avatar.transform);
        // The reset machinery: the version's bone goes, every bone to the model rest.
        leg.SetParent(hips, false); Object.DestroyImmediate(top.gameObject); leg.localPosition = Vector3.left; hips.localPosition = Vector3.up; tail.localRotation = Quaternion.identity;
        VersionPoseLedgerService.FinishRestore(owner, pending);
        foreach (var pair in before.Where(p => p.Key != tail))
        {
            Assert.That(pair.Key.parent, Is.SameAs(pair.Value.Item1), pair.Key.name);
            Assert.That(pair.Key.GetSiblingIndex(), Is.EqualTo(pair.Value.Item2), pair.Key.name);
            Assert.That(pair.Key.localPosition, Is.EqualTo(pair.Value.Item3), pair.Key.name);
            Assert.That(pair.Key.localRotation, Is.EqualTo(pair.Value.Item4), pair.Key.name);
        }
        Assert.That(Quaternion.Angle(tail.localRotation, Quaternion.Euler(10, 0, 0) * Quaternion.Euler(0, 5, 0)), Is.LessThan(0.01f), "the edit made while installed is kept");
        Assert.That(owner.poseLedger.recorded, Is.False);
    }

    // A body on another base installs without the migration that used to record its meshes: going back puts the base
    // model's meshes on the version's renderers, and the person's own meshes (a sculpted mesh asset, a mesh that only
    // lives in the scene) must come back from the ledger.
    [Test] public void PersonalMeshesComeBackAfterGoingBack()
    {
        var baseMesh = AssetDatabase.LoadAllAssetsAtPath("Assets/Avatars/Rexouium/FBX_Rigs/Rig1.6 (HeadJntMovedUp)/RexouiumRig1.6_V265_Fthrs_ReparentChestUp.fbx").OfType<Mesh>().FirstOrDefault();
        Assume.That(baseMesh, Is.Not.Null, "Rexouium 1.6 is not in this project");
        var (model, avatar, owner) = Rig();
        string folder = "Assets/__PoseLedgerTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        try
        {
            var hips = avatar.transform.Find("Armature/Hips");
            var sculpted = new Mesh { name = "Sculpted body", vertices = new[] { Vector3.zero } };
            AssetDatabase.CreateAsset(sculpted, folder + "/sculpted.asset");
            var sceneOnly = new Mesh { name = "Scene-only piece", vertices = new[] { Vector3.one } }; owned.Add(sceneOnly);
            SkinnedMeshRenderer Piece(string name, Mesh mesh)
            {
                var r = Child(avatar.transform, name, Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
                r.sharedMesh = mesh; r.bones = new[] { hips }; r.rootBone = hips; return r;
            }
            var body = Piece("Body", sculpted); var piece = Piece("Piece", sceneOnly);
            var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
            // The install: the version's meshes on both.
            var version = new Mesh { name = "Version body" }; owned.Add(version);
            body.sharedMesh = version; piece.sharedMesh = version;
            VersionPoseLedgerService.Record(owner, snapshot);
            var pending = VersionPoseLedgerService.BeginRestore(owner, avatar.transform);
            // The reset machinery: the base model's mesh on the renderers the version replaced.
            body.sharedMesh = baseMesh; piece.sharedMesh = baseMesh;
            VersionPoseLedgerService.FinishRestore(owner, pending);
            Assert.That(body.sharedMesh, Is.SameAs(sculpted));
            Assert.That(piece.sharedMesh, Is.SameAs(sceneOnly));
            Assert.That(body.bones, Is.EqualTo(new[] { hips }));
        }
        finally { AssetDatabase.DeleteAsset(folder); }
    }

    // "Migrate avatar" on its own, then an install: the ledger recorded the model the body was migrated onto. Going back
    // gives the release from before MCB again first, and that mesh (a model file's too) must stay.
    [Test] public void ABodyGivenItsOldReleaseAgainKeepsThatMesh()
    {
        var meshes = AssetDatabase.LoadAllAssetsAtPath("Assets/Avatars/Rexouium/FBX_Rigs/Rig1.6 (HeadJntMovedUp)/RexouiumRig1.6_V265_Fthrs_ReparentChestUp.fbx").OfType<Mesh>().Take(2).ToArray();
        Assume.That(meshes.Length, Is.EqualTo(2), "Rexouium 1.6 is not in this project");
        Mesh migratedOnto = meshes[0], oldRelease = meshes[1];
        var (model, avatar, owner) = Rig();
        var hips = avatar.transform.Find("Armature/Hips");
        SkinnedMeshRenderer Piece(string name)
        {
            var r = Child(avatar.transform, name, Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = migratedOnto; r.bones = new[] { hips }; r.rootBone = hips; return r;
        }
        var body = Piece("Body"); var other = Piece("Piece");
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        var version = new Mesh { name = "Version body" }; owned.Add(version);
        body.sharedMesh = version; other.sharedMesh = version;
        VersionPoseLedgerService.Record(owner, snapshot);
        var pending = VersionPoseLedgerService.BeginRestore(owner, avatar.transform);
        // Going back: the old release's mesh on the body it was restored on; the other piece left on another model mesh.
        body.sharedMesh = oldRelease; other.sharedMesh = oldRelease;
        VersionPoseLedgerService.FinishRestore(owner, pending, null, new[] { body });
        Assert.That(body.sharedMesh, Is.SameAs(oldRelease), "The release from before MCB stays on the renderer that got it back.");
        Assert.That(other.sharedMesh, Is.SameAs(migratedOnto), "A renderer nothing restored gets its recorded mesh.");
    }

    [Test] public void SnapshotRestoresWhatAFailedSwitchLeft()
    {
        var (model, avatar, owner) = Rig();
        var hips = avatar.transform.Find("Armature/Hips");
        var start = VersionPoseLedgerService.CaptureState(avatar.transform);
        var p = hips.localPosition;
        hips.localPosition = Vector3.zero; hips.SetSiblingIndex(0);
        VersionPoseLedgerService.RestoreSnapshot(start);
        Assert.That(hips.localPosition, Is.EqualTo(p));
    }

    // A version installed before the pose ledger (UltiPaw 1.0.7 under MCB 1.13) raised the hips and turned the leg; the
    // person turned the tail. Installing the next version with the same pose must not count the old version's pose as the
    // person's (it was added a second time: hips 8 cm too high, ears, jaw and tail doubled), and going back is the original.
    [Test] public void AVersionInstalledBeforeTheLedgerIsNotThePersonsPose()
    {
        var (model, avatar, owner) = Rig();
        var hips = avatar.transform.Find("Armature/Hips"); var leg = hips.Find("Leg"); var tail = hips.Find("Tail");
        var poseBones = new Dictionary<Transform, NativeMeshPayloadAuthoringPoseBone>
        {
            [hips] = new NativeMeshPayloadAuthoringPoseBone { path = "Armature/Hips", localPositionOffset = new Vector3(0, .08f, 0) },
            [leg] = new NativeMeshPayloadAuthoringPoseBone { path = "Armature/Hips/Leg", localRotationDelta = Quaternion.Euler(0, 0, 20) },
            [tail] = new NativeMeshPayloadAuthoringPoseBone { path = "Armature/Hips/Tail" }
        };
        var modelHips = model.transform.Find("Armature/Hips");
        Transform Source(Transform t) => t == hips ? modelHips : modelHips.Find(t.name);
        void InstallVersion()
        {
            foreach (var pair in poseBones)
            {
                var s = Source(pair.Key);
                pair.Key.localPosition = s.localPosition + pair.Value.localPositionOffset;
                pair.Key.localRotation = s.localRotation * pair.Value.localRotationDelta;
            }
        }
        // The old install, then the person's own turn of the tail.
        InstallVersion();
        tail.localRotation = Quaternion.Euler(10, 0, 0);
        var undone = poseBones.ToDictionary(p => p.Key, p => NativeMeshPayloadService.AuthoringPoseUndone(Source(p.Key).localPosition,
            Source(p.Key).localRotation, Source(p.Key).localScale, p.Value, p.Key.localPosition, p.Key.localRotation, p.Key.localScale));
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model }, owner, undone);
        Assert.That(snapshot.reference, Is.Not.Empty, string.Join("; ", snapshot.referenceNotes));
        InstallVersion();
        VersionPoseLedgerService.CarryPose(snapshot);
        Assert.That(Vector3.Distance(hips.localPosition, Vector3.up * 1.08f), Is.LessThan(1e-5f), "the hips at the version's height, once");
        Assert.That(Quaternion.Angle(leg.localRotation, Quaternion.Euler(0, 0, 20)), Is.LessThan(0.01f), "the leg turned by the version, once");
        Assert.That(Quaternion.Angle(tail.localRotation, Quaternion.Euler(10, 0, 0)), Is.LessThan(0.01f), "the person's tail");
        VersionPoseLedgerService.Record(owner, snapshot);
        var pending = VersionPoseLedgerService.BeginRestore(owner, avatar.transform);
        foreach (var pair in poseBones) { var s = Source(pair.Key); pair.Key.localPosition = s.localPosition; pair.Key.localRotation = s.localRotation; }
        VersionPoseLedgerService.FinishRestore(owner, pending);
        Assert.That(Vector3.Distance(hips.localPosition, Vector3.up), Is.LessThan(1e-5f), "going back: the original hips");
        Assert.That(Quaternion.Angle(leg.localRotation, Quaternion.identity), Is.LessThan(0.01f), "going back: the original leg");
        Assert.That(Quaternion.Angle(tail.localRotation, Quaternion.Euler(10, 0, 0)), Is.LessThan(0.01f), "going back: the person's tail");
    }

    // Root-space pose of a transform relative to the avatar root.
    private static Matrix4x4 Space(Transform root, Transform t) => root.worldToLocalMatrix * t.localToWorldMatrix;

    private static void AssertSame(Matrix4x4 a, Matrix4x4 b, float tolerance, string what)
    {
        for (int i = 0; i < 16; i++) Assert.That(Mathf.Abs(a[i] - b[i]), Is.LessThan(tolerance), what + " [" + i + "]");
    }

    // Model and avatar: Armature/Hips/{ArmL, ArmR, LegL, LegR, Tail}, each with an end bone.
    private (GameObject model, GameObject avatar) Limbs()
    {
        var model = Own(new GameObject("Model"));
        var mh = Child(Child(model.transform, "Armature", Vector3.zero), "Hips", Vector3.up);
        foreach (var (n, p) in new[] { ("ArmL", new Vector3(-.2f, .5f, 0)), ("ArmR", new Vector3(.2f, .5f, 0)), ("LegL", new Vector3(-.1f, 0, 0)), ("LegR", new Vector3(.1f, 0, 0)), ("Tail", Vector3.back * .1f) })
            Child(Child(mh, n, p), n + "End", Vector3.down * .3f);
        var avatar = Own(Object.Instantiate(model)); avatar.name = "PoseLedgerTestAvatar"; avatar.AddComponent<MyCustomBase>();
        return (model, avatar);
    }

    [Test] public void AModelThatDoesNotReproduceTheSkeletonCarriesNothing()
    {
        var (model, avatar) = Limbs();
        // The model file's rest has an arm down, the legs splayed and the tail turned: not where the avatar's pose comes from.
        var mh = model.transform.Find("Armature/Hips");
        mh.Find("ArmR").localRotation = Quaternion.Euler(0, 0, -80);
        mh.Find("LegL").localRotation = Quaternion.Euler(0, 0, 25); mh.Find("LegR").localRotation = Quaternion.Euler(0, 0, -25);
        mh.Find("Tail").localRotation = Quaternion.Euler(30, 0, 0); mh.Find("ArmL").localRotation = Quaternion.Euler(0, 0, 5);
        mh.localPosition = Vector3.up * 1.1f;
        foreach (var end in mh.GetComponentsInChildren<Transform>().Where(t => t.name.EndsWith("End"))) end.localPosition = Vector3.down * .35f;
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        Assert.That(snapshot.reference, Is.Empty, string.Join("; ", snapshot.referenceNotes));
        // The version: its own rest pose.
        var hips = avatar.transform.Find("Armature/Hips"); hips.localPosition = Vector3.up * 1.04f;
        var before = avatar.GetComponentsInChildren<Transform>().ToDictionary(t => t, t => (t.localPosition, t.localRotation, t.localScale));
        Assert.That(VersionPoseLedgerService.CarryPose(snapshot), Is.Zero);
        foreach (var pair in before) Assert.That((pair.Key.localPosition, pair.Key.localRotation, pair.Key.localScale), Is.EqualTo(pair.Value), pair.Key.name);
    }

    [Test] public void SmallAdjustmentsAreKeptInRootSpace()
    {
        var (model, avatar) = Limbs();
        var hips = avatar.transform.Find("Armature/Hips"); var leg = hips.Find("LegL");
        // 0.01 degrees: Quaternion.Angle calls it zero.
        leg.localRotation = Quaternion.Euler(0.01f, 0, 0);
        Assert.That(Quaternion.Angle(leg.localRotation, Quaternion.identity), Is.Zero);
        var person = Space(avatar.transform, leg);
        var rest = Space(model.transform, model.transform.Find("Armature/Hips/LegL"));
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        // The version lengthens the leg and turns its frame (another bone roll).
        leg.localRotation = Quaternion.Euler(0, 90, 0); leg.localPosition = new Vector3(-.12f, -.05f, 0);
        var installed = Space(avatar.transform, leg);
        VersionPoseLedgerService.CarryPose(snapshot);
        AssertSame(Space(avatar.transform, leg), person * rest.inverse * installed, 1e-6f, "the person's adjustment on the version's leg");
        var turn = Quaternion.Inverse(Quaternion.Euler(0, 90, 0)) * leg.localRotation;
        Assert.That(Mathf.Max(Mathf.Abs(turn.x), Mathf.Abs(turn.y), Mathf.Abs(turn.z)), Is.GreaterThan(5e-5f), "the 0.01 degree turn is there");
    }

    [Test] public void AnUnposedAvatarIsLeftAtTheVersionsPoseWhateverItsBoneRoll()
    {
        var (model, avatar) = Limbs();
        var hips = avatar.transform.Find("Armature/Hips"); var arm = hips.Find("ArmR");
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        // The version: a different frame for the arm (rolled 90 degrees about itself) and another hip height.
        arm.localRotation = Quaternion.Euler(0, 90, 0); hips.localPosition = Vector3.up * 1.05f;
        var before = avatar.GetComponentsInChildren<Transform>().ToDictionary(t => t, t => (t.localPosition, t.localRotation, t.localScale));
        Assert.That(VersionPoseLedgerService.CarryPose(snapshot), Is.Zero, "no adjustment, nothing written");
        foreach (var pair in before) Assert.That((pair.Key.localPosition, pair.Key.localRotation, pair.Key.localScale), Is.EqualTo(pair.Value), pair.Key.name);
    }

    [Test] public void ObjectsOnABoneTheVersionMovesKeepTheirPlaceAndGoingBackKeepsWhereThePersonPutThem()
    {
        var (model, avatar) = Limbs();
        var owner = avatar.GetComponent<MyCustomBase>();
        var hips = avatar.transform.Find("Armature/Hips"); var tail = hips.Find("Tail");
        var prop = new GameObject("Prop").transform; prop.SetParent(tail, false); prop.localPosition = new Vector3(0, .1f, .05f); prop.localRotation = Quaternion.Euler(10, 20, 30);
        var place = Space(avatar.transform, prop);
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        // The version moves the tail's pivot and turns it.
        tail.localPosition += new Vector3(0, .04f, 0); tail.localRotation = Quaternion.Euler(0, 0, 15);
        VersionPoseLedgerService.CarryPose(snapshot);
        Assert.That(VersionPoseLedgerService.KeepPlaces(snapshot), Is.EqualTo(1));
        AssertSame(Space(avatar.transform, prop), place, 1e-6f, "the prop stays on the body");
        VersionPoseLedgerService.Record(owner, snapshot);
        // The person moves the prop 2 cm while the version is on.
        prop.position += new Vector3(.02f, 0, 0);
        var moved = Space(avatar.transform, prop);
        var pending = VersionPoseLedgerService.BeginRestore(owner, avatar.transform);
        tail.localPosition -= new Vector3(0, .04f, 0); tail.localRotation = Quaternion.identity;
        VersionPoseLedgerService.FinishRestore(owner, pending);
        AssertSame(Space(avatar.transform, prop), moved, 1e-6f, "where the person put it");
        Assert.That(tail.localRotation, Is.EqualTo(Quaternion.identity));
    }

    [Test] public void ClothingOnAMovedBoneKeepsItsShapeWithOneReusedCopy()
    {
        var (model, avatar) = Limbs();
        var hips = avatar.transform.Find("Armature/Hips"); var tail = hips.Find("Tail");
        var cloth = new GameObject("Cloth"); cloth.transform.SetParent(avatar.transform, false);
        var renderer = cloth.AddComponent<SkinnedMeshRenderer>();
        var mesh = new Mesh { name = "ClothMesh", vertices = new[] { Vector3.zero, Vector3.right * .1f, Vector3.up * .1f }, triangles = new[] { 0, 1, 2 } };
        owned.Add(mesh);
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
        renderer.bones = new[] { tail };
        mesh.bindposes = new[] { tail.worldToLocalMatrix * cloth.transform.localToWorldMatrix };
        renderer.sharedMesh = mesh;
        var baked = new Mesh(); owned.Add(baked); renderer.BakeMesh(baked);
        var before = baked.vertices.Select(cloth.transform.TransformPoint).ToArray();
        for (int round = 0; round < 2; round++)
        {
            var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
            tail.localPosition += new Vector3(0, .05f, 0); tail.localRotation = Quaternion.Euler(0, 0, 20);
            VersionPoseLedgerService.CarryPose(snapshot);
            VersionPoseLedgerService.KeepPlaces(snapshot);
            Assert.That(VersionPoseLedgerService.RebindClothing(snapshot), Is.EqualTo(1));
            renderer.BakeMesh(baked);
            var after = baked.vertices.Select(cloth.transform.TransformPoint).ToArray();
            for (int i = 0; i < before.Length; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(1e-5f), "vertex " + i);
            Assert.That(AssetDatabase.GetAssetPath(renderer.sharedMesh), Does.StartWith(VersionPoseLedgerService.ReboundFolder + "/PoseLedgerTestAvatar/"));
            // Going back: the original mesh and pose.
            renderer.sharedMesh = mesh; tail.localPosition -= new Vector3(0, .05f, 0); tail.localRotation = Quaternion.identity;
        }
        Assert.That(System.IO.Directory.GetFiles(VersionPoseLedgerService.ReboundFolder + "/PoseLedgerTestAvatar", "*.asset").Length, Is.EqualTo(1), "one copy, reused");
    }

    [Test] public void AnAnimatorWithoutAvatarGetsNoneBack()
    {
        var (model, avatar) = Limbs();
        var owner = avatar.GetComponent<MyCustomBase>();
        var animator = avatar.AddComponent<Animator>();
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        var generic = AvatarBuilder.BuildGenericAvatar(avatar, ""); owned.Add(generic);
        animator.avatar = generic;
        VersionPoseLedgerService.Record(owner, snapshot);
        var pending = VersionPoseLedgerService.BeginRestore(owner, avatar.transform);
        VersionPoseLedgerService.FinishRestore(owner, pending);
        Assert.That(animator.avatar, Is.Null);
    }
}
