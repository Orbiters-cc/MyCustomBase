using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// A plain (unencrypted) version carries its model's own rest pose, e.g. Ultirex's star pose. Applied on a Rexouium in
// T-pose, the limbs must follow the original base's pose, on the bones the meshes are skinned to (MCB's logic proxies
// carry the same names), while bone lengths and the hips stay the version's.
public class KeepBasePoseTests
{
    private string Folder;
    private readonly List<Object> owned = new List<Object>();

    // A folder of this test's own: only what the test created is deleted.
    private void CreateFolder()
    {
        string name = "__MCBKeepBasePoseTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", name);
        Folder = "Assets/" + name;
    }

    [TearDown]
    public void Clean()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
        if (Folder != null && AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        Folder = null;
    }

    [Test]
    public void APlainVersionKeepsTheOriginalBasePose()
    {
        CreateFolder();
        var (baseRoot, baseBones) = Humanoid("Base", 0f, 1f);
        var baseAvatar = Build(baseRoot);
        AssetDatabase.CreateAsset(baseAvatar, Folder + "/Base.asset");
        baseRoot.AddComponent<Animator>().avatar = baseAvatar;
        string basePath = Folder + "/Base.prefab";
        PrefabUtility.SaveAsPrefabAsset(baseRoot, basePath);

        // The version: longer shins, legs spread 25° outward, an unweighted proxy bone listed first.
        var (version, bones) = Humanoid("Version", 25f, 1.1f);
        var animator = version.AddComponent<Animator>();
        animator.avatar = Build(version);
        var proxy = new GameObject("mcb logic").transform; proxy.SetParent(version.transform, false); proxy.SetAsFirstSibling();
        new GameObject("UpperLegL").transform.SetParent(proxy, false);
        var skin = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
        skin.transform.SetParent(version.transform, false);
        skin.bones = bones.Values.ToArray();
        float shin = Vector3.Distance(bones["LowerLegL"].position, bones["FootL"].position);
        var hips = bones["Hips"].position;

        Assert.That(NativeMeshPayloadService.KeepBasePose(version.transform, basePath), Is.True);

        foreach (var (from, to) in new[] { ("UpperLegL", "LowerLegL"), ("LowerLegL", "FootL"), ("UpperLegR", "LowerLegR"), ("UpperArmL", "LowerArmL") })
        {
            var wanted = (baseBones[to].position - baseBones[from].position).normalized;
            var actual = (bones[to].position - bones[from].position).normalized;
            Assert.That(Vector3.Angle(wanted, actual), Is.LessThan(.1f), from + " does not point like the base.");
        }
        Assert.That(Vector3.Distance(bones["LowerLegL"].position, bones["FootL"].position), Is.EqualTo(shin).Within(1e-4f), "Bone lengths are the version's.");
        Assert.That(Vector3.Distance(bones["Hips"].position, hips), Is.LessThan(1e-5f), "The hips stay where they are.");
    }

    // A Digitigrade Rexouium 1.7: its humanoid legs are a Planti_* chain beside the legs the body hangs on. The version's
    // legs (skinned by its payload meshes) must follow the base model's legs of the same names, not the Planti chain.
    [Test]
    public void ADigitigradeAvatarAimsTheLegsTheBodyHangsOn()
    {
        CreateFolder();
        var (baseRoot, baseBones) = Humanoid("Base", 0f, 1f);
        AddPlanti(baseRoot.transform, baseBones);
        var baseAvatar = Build(baseRoot, "Planti");
        AssetDatabase.CreateAsset(baseAvatar, Folder + "/Base.asset");
        baseRoot.AddComponent<Animator>().avatar = baseAvatar;
        string basePath = Folder + "/Base.prefab";
        PrefabUtility.SaveAsPrefabAsset(baseRoot, basePath);

        var (version, bones) = Humanoid("Version", 25f, 1f);
        AddPlanti(version.transform, bones);
        // The body's own feet point their toes elsewhere than the base's; the Planti chain's toes elsewhere again.
        bones["FootL"].localRotation = Quaternion.Euler(30, 0, 0);
        bones["PlantiToesL"].localPosition = new Vector3(.2f, -.05f, -.1f);
        var footBefore = bones["FootL"].localRotation;
        // The version's own humanoid names no toes (as Ultirex V5's default avatar); the avatar's maps them to the Planti chain.
        var versionHumanoid = Build(version);
        version.AddComponent<Animator>().avatar = Build(version, "Planti", toes: true);
        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        AssetDatabase.CreateAsset(payload, Folder + "/payload.asset");
        // Real weights on the deform bones; a Planti bone sits in the bone list with no weight (a zero-weight slot).
        var deform = bones.Where(b => !b.Key.StartsWith("Planti")).Select(b => b.Value).ToList();
        var slots = deform.Append(bones["PlantiUpperLegL"]).ToArray();
        var skin = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
        skin.transform.SetParent(version.transform, false);
        var mesh = new Mesh { name = "Body", vertices = deform.Select(b => b.position).ToArray() };
        mesh.boneWeights = Enumerable.Range(0, deform.Count).Select(i => new BoneWeight { boneIndex0 = i, weight0 = 1f }).ToArray();
        mesh.bindposes = slots.Select(b => b.worldToLocalMatrix * skin.transform.localToWorldMatrix).ToArray();
        AssetDatabase.AddObjectToAsset(mesh, payload);
        skin.sharedMesh = mesh;
        skin.bones = slots;

        NativeMeshPayloadService.KeepBasePose(version.transform, basePath);
        float before = Vector3.Angle(baseBones["LowerLegL"].position - baseBones["UpperLegL"].position, bones["LowerLegL"].position - bones["UpperLegL"].position);
        Assert.That(before, Is.GreaterThan(10f), "Without the version's humanoid, the Planti chain is aimed and the body's legs stay spread.");

        Assert.That(NativeMeshPayloadService.KeepBasePose(version.transform, basePath, versionHumanoid), Is.True);
        foreach (var (from, to) in new[] { ("UpperLegL", "LowerLegL"), ("LowerLegL", "FootL"), ("UpperLegR", "LowerLegR"), ("UpperArmL", "LowerArmL") })
            Assert.That(Vector3.Angle(baseBones[to].position - baseBones[from].position, bones[to].position - bones[from].position), Is.LessThan(.1f), from + " does not point like the base.");
        Assert.That(Quaternion.Angle(bones["FootL"].localRotation, footBefore), Is.LessThan(1e-3f), "The body's foot is never aimed at the Planti chain's toe.");
    }

    private static void AddPlanti(Transform root, Dictionary<string, Transform> bones)
    {
        foreach (var side in new[] { "L", "R" })
        {
            float x = side == "L" ? -1 : 1;
            Transform Add(string name, Transform parent, Vector3 position) { var t = new GameObject(name).transform; t.SetParent(parent, false); t.localPosition = position; bones[name] = t; return t; }
            var leg = Add("PlantiUpperLeg" + side, bones["Hips"], new Vector3(.1f * x, -.05f, 0));
            var knee = Add("PlantiLowerLeg" + side, leg, new Vector3(0, -.45f, 0));
            var foot = Add("PlantiFoot" + side, knee, new Vector3(0, -.45f, 0));
            Add("PlantiToes" + side, foot, new Vector3(0, -.05f, .1f));
        }
    }

    private (GameObject root, Dictionary<string, Transform> bones) Humanoid(string name, float legSpread, float shinScale)
    {
        var root = new GameObject(name); owned.Add(root);
        var bones = new Dictionary<string, Transform>();
        void Add(string bone, string parent, Vector3 position)
        {
            var t = new GameObject(bone).transform;
            t.SetParent(parent == null ? root.transform : bones[parent], false);
            t.localPosition = position;
            bones[bone] = t;
        }
        Add("Hips", null, new Vector3(0, 1, 0)); Add("Spine", "Hips", new Vector3(0, .1f, 0)); Add("Chest", "Spine", new Vector3(0, .15f, 0));
        Add("Neck", "Chest", new Vector3(0, .24f, 0)); Add("Head", "Neck", new Vector3(0, .08f, 0));
        foreach (var side in new[] { "L", "R" })
        {
            float x = side == "L" ? -1 : 1;
            Add("UpperArm" + side, "Chest", new Vector3(.15f * x, .15f, 0)); Add("LowerArm" + side, "UpperArm" + side, new Vector3(.25f * x, 0, 0));
            Add("Hand" + side, "LowerArm" + side, new Vector3(.25f * x, 0, 0));
            Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
            Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f * shinScale, 0));
            Add("Toes" + side, "Foot" + side, new Vector3(0, -.05f, .1f));
            bones["UpperLeg" + side].localRotation = Quaternion.Euler(0, 0, legSpread * -x);
        }
        return (root, bones);
    }

    private static Avatar Build(GameObject root, string legPrefix = "", bool toes = false)
    {
        var map = new Dictionary<string, string>
        {
            ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
            ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
            ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
            ["LeftUpperLeg"] = "UpperLegL", ["LeftLowerLeg"] = "LowerLegL", ["LeftFoot"] = "FootL",
            ["RightUpperLeg"] = "UpperLegR", ["RightLowerLeg"] = "LowerLegR", ["RightFoot"] = "FootR",
        };
        if (toes) { map["LeftToes"] = "ToesL"; map["RightToes"] = "ToesR"; }
        foreach (var leg in map.Keys.Where(k => k.Contains("Leg") || k.Contains("Foot") || k.Contains("Toes")).ToList()) map[leg] = legPrefix + map[leg];
        var skeleton = root.GetComponentsInChildren<Transform>(true);
        var description = new HumanDescription
        {
            human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
            skeleton = skeleton.Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
            upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
        };
        var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
        Assert.That(avatar.isHuman, Is.True);
        return avatar;
    }
}
