using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

public class PhysicServiceTests
{
    private static readonly PhysicService.Kind Physic = PhysicService.Kind.Physic, Squishy = PhysicService.Kind.Squishy;
    private GameObject root;
    private Transform hips, leg, knee, assBase, assTip, quads, calf, squishBase, squishTip;
    private SkinnedMeshRenderer body;

    // A leg with two physic chains under the thigh (one of two bones), one under the knee, and a squishy chain of two
    // bones under the thigh. The body's bones array leaves the thigh out: stripping must add the bone the weights move to.
    [SetUp] public void Rig()
    {
        root = new GameObject("Avatar");
        var armature = Add("Armature", root.transform, Vector3.zero);
        hips = Add("Hips", armature, new Vector3(0, 1, 0));
        leg = Add("Left leg", hips, new Vector3(.1f, -.05f, 0));
        leg.localRotation = Quaternion.Euler(0, 0, 8);
        knee = Add("Left knee", leg, new Vector3(0, -.45f, 0));
        assBase = Add("Left ass physic base", leg, new Vector3(0, .02f, -.1f));
        assBase.localRotation = Quaternion.Euler(20, 0, 0);
        assTip = Add("Left ass physic tip", assBase, new Vector3(0, .08f, 0));
        quads = Add("Left quads physic base", leg, new Vector3(0, -.15f, .06f));
        calf = Add("Left calf physic", knee, new Vector3(0, -.1f, -.05f));
        squishBase = Add("Left thigh interaction base", leg, new Vector3(.05f, -.2f, 0));
        squishTip = Add("Left thigh interaction tip", squishBase, new Vector3(.04f, 0, 0));
        body = Add("Body", root.transform, Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
        var bones = new[] { hips, knee, assBase, assTip, quads, calf, squishBase, squishTip };
        var mesh = new Mesh { name = "Body" };
        mesh.vertices = new[] { assBase.position + Vector3.back * .02f, assTip.position, quads.position + Vector3.forward * .03f, calf.position,
            hips.position, knee.position + Vector3.down * .1f, squishBase.position + Vector3.right * .01f, squishTip.position };
        mesh.triangles = new[] { 0, 1, 2, 3, 4, 5, 5, 6, 7 };
        mesh.boneWeights = new[]
        {
            new BoneWeight { boneIndex0 = 2, weight0 = .6f, boneIndex1 = 0, weight1 = .4f },
            new BoneWeight { boneIndex0 = 3, weight0 = 1 },
            new BoneWeight { boneIndex0 = 4, weight0 = .5f, boneIndex1 = 2, weight1 = .5f },
            new BoneWeight { boneIndex0 = 5, weight0 = .7f, boneIndex1 = 1, weight1 = .3f },
            new BoneWeight { boneIndex0 = 0, weight0 = 1 },
            new BoneWeight { boneIndex0 = 1, weight0 = 1 },
            new BoneWeight { boneIndex0 = 6, weight0 = .8f, boneIndex1 = 0, weight1 = .2f },
            new BoneWeight { boneIndex0 = 7, weight0 = 1 },
        };
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * body.transform.localToWorldMatrix).ToArray();
        body.sharedMesh = mesh;
        body.bones = bones;
        body.rootBone = hips;
    }

    [TearDown] public void Clean()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    [Test] public void ChainsSharingAParentShareOnePhysBoneThatLeavesTheLimbAlone()
    {
        var groups = PhysicService.Groups(root.transform, Physic);
        Assert.AreEqual(2, groups.Count);
        var thigh = groups.Single(g => g.Root == leg);
        CollectionAssert.AreEquivalent(new[] { assBase, quads }, thigh.Chains);
        CollectionAssert.AreEquivalent(new[] { knee, squishBase }, thigh.Ignored, "the limb and the squishy chain are not physic");
        Assert.AreEqual(4, thigh.Transforms, "the still parent and the three chain bones");
        var single = groups.Single(g => g.Root == calf);
        Assert.AreEqual(1, single.Transforms);
        CollectionAssert.AreEquivalent(new[] { assBase, assTip, quads, calf }, PhysicService.Bones(root.transform, Physic));
    }

    [Test] public void InteractionBonesAreSquishyAndNeverPhysic()
    {
        CollectionAssert.AreEquivalent(new[] { squishBase, squishTip }, PhysicService.Bones(root.transform, Squishy));
        var both = Add("Left physic interaction", leg, Vector3.zero);
        Assert.True(Squishy.Owns(both) && !Physic.Owns(both), "a bone named with both words is squishy");
        Assert.False(Squishy.Owns(assBase) || Physic.Owns(squishBase));
    }

    [Test] public void AddsAlwaysOnPhysBonesThatEveryPlayerSimulates()
    {
        Assert.AreEqual(2, PhysicService.AddPhysBones(root, Physic));
        var bones = root.transform.Find(Physic.HostName).GetComponentsInChildren<VRCPhysBone>(true);
        Assert.AreEqual(2, bones.Length);
        var thigh = bones.Single(b => b.rootTransform == leg);
        Assert.AreEqual(VRCPhysBoneBase.MultiChildType.Ignore, thigh.multiChildType);
        CollectionAssert.AreEquivalent(new[] { knee, squishBase }, thigh.ignoreTransforms);
        var single = bones.Single(b => b.rootTransform == calf);
        Assert.AreEqual(VRCPhysBoneBase.MultiChildType.Average, single.multiChildType);
        Assert.Greater(single.endpointPosition.y, 0, "a one-bone chain swings with an end point");
        Assert.True(bones.All(b => b.enabled && b.gameObject.activeInHierarchy && string.IsNullOrEmpty(b.parameter)),
            "nothing toggles or drives them: remote players simulate the same PhysBones");
        Assert.True(bones.All(b => b.allowCollision == VRCPhysBoneBase.AdvancedBool.False));
        Assert.False(PhysicService.Kind.All.Any(k => k.Owns(root.transform.Find(Physic.HostName))), "a generated object is never mistaken for a chain");
    }

    [Test] public void SquishyChainsCollideWithHandsAndSquash()
    {
        Assert.AreEqual(1, PhysicService.AddPhysBones(root, Squishy));
        var bone = root.transform.Find(Squishy.HostName).GetComponentInChildren<VRCPhysBone>(true);
        Assert.AreSame(squishBase, bone.rootTransform);
        Assert.AreEqual(VRCPhysBoneBase.AdvancedBool.True, bone.allowCollision, "players' hands touch it");
        Assert.Greater(bone.radius, 0);
        Assert.Greater(bone.maxSquish, 0);
        Assert.AreEqual(VRCPhysBoneBase.AdvancedBool.False, bone.allowGrabbing);
        Assert.AreEqual(Vector3.zero, bone.endpointPosition, "a two-bone chain squashes along its own tip");
    }

    [Test] public void StrippingMovesWeightsToTheKeptParentWithoutChangingTheRestShape()
    {
        var before = Bake();
        // The version created these bones: MCB's record of them is not a use.
        var owner = root.AddComponent<MyCustomBase>();
        owner.nativeMeshGeneratedBones.AddRange(new[] { assBase.gameObject, assTip.gameObject, quads.gameObject, calf.gameObject });
        var stray = Add("Clothing bone", assTip, new Vector3(0, .01f, 0));
        var strayPosition = stray.position;
        var kept = new List<Mesh>();
        Assert.AreEqual(6, PhysicService.Strip(root, PhysicService.Kind.All, kept.Add));

        Assert.AreEqual(1, kept.Count);
        Assert.AreSame(kept[0], body.sharedMesh);
        Assert.False(body.bones.Any(b => Physic.Owns(b) || Squishy.Owns(b)));
        Assert.Contains(leg, body.bones, "the thigh the chains hung from now carries their weights");
        Assert.True(assBase == null && assTip == null && quads == null && calf == null && squishBase == null && squishTip == null);
        Assert.AreSame(leg, stray.parent, "other bones under a chain stay, in place");
        Assert.Less(Vector3.Distance(strayPosition, stray.position), 1e-5f);
        var after = Bake();
        for (int i = 0; i < before.Length; i++) Assert.Less(Vector3.Distance(before[i], after[i]), 1e-5f, "vertex " + i);

        var counts = body.sharedMesh.GetBonesPerVertex();
        var weights = body.sharedMesh.GetAllBoneWeights();
        for (int v = 0, k = 0; v < counts.Length; k += counts[v], v++)
        {
            float sum = 0;
            for (int n = 0; n < counts[v]; n++)
            {
                sum += weights[k + n].weight;
                if (n > 0) Assert.LessOrEqual(weights[k + n].weight, weights[k + n - 1].weight, "heaviest first");
            }
            Assert.AreEqual(1, sum, 1e-5f);
        }
        // Both stripped influences of vertex 2 merged into one.
        Assert.AreEqual(1, counts[2]);
        Assert.AreSame(leg, body.bones[weights[counts.Take(2).Sum(c => c)].boneIndex]);

        // The tip's vertex now follows the thigh rigidly.
        var onThigh = leg.InverseTransformPoint(after[1]);
        leg.localRotation *= Quaternion.Euler(30, 0, 0);
        Assert.Less(Vector3.Distance(Bake()[1], leg.TransformPoint(onThigh)), 1e-5f);
    }

    [Test] public void StrippingOneKindKeepsTheOther()
    {
        Assert.AreEqual(2, PhysicService.Strip(root, new[] { Squishy }, null));
        Assert.True(squishBase == null && squishTip == null);
        Assert.True(assBase != null && calf != null, "physic chains stay");
    }

    [Test] public void BonesOtherComponentsUseAreKept()
    {
        var physBone = Add("Ass PhysBone", root.transform, Vector3.zero).gameObject.AddComponent<VRCPhysBone>();
        physBone.rootTransform = assBase;
        Assert.AreEqual(3, PhysicService.Strip(root, new[] { Physic }, null));
        Assert.True(assBase != null, "a PhysBone of the avatar points at it");
        Assert.True(assTip == null && quads == null && calf == null);
        Assert.Contains(assBase, body.bones);
    }

    [Test] public void PlanCountsWhatTheBuildDoesForTheUsersChoices()
    {
        var owner = root.AddComponent<MyCustomBase>();
        Assert.AreEqual((0, 0, 0), PhysicService.Plan(owner), "versions without physic or squishy change nothing");
        owner.appliedCustomization.physic = true;
        owner.appliedCustomization.squishy = true;
        Assert.AreEqual((0, 0, 6), PhysicService.Plan(owner), "off by default: the chains' bones are removed");
        owner.physicEnabled = true;
        Assert.AreEqual((2, 5, 2), PhysicService.Plan(owner));
        owner.squishyEnabled = true;
        Assert.AreEqual((3, 7, 0), PhysicService.Plan(owner));
    }

    [Test] public void PhysicAndSquishyAreVersionFlags()
    {
        var entries = new List<object> { "customVeins" };
        new VersionCustomization { physic = true, squishy = true }.Write(entries);
        Assert.True(ExtraCustomizationUtils.HasFlag(entries, VersionCustomization.PhysicKey));
        Assert.True(ExtraCustomizationUtils.HasFlag(entries, VersionCustomization.SquishyKey));
        var read = VersionCustomization.Read(entries);
        Assert.True(read.physic && read.squishy);
        new VersionCustomization().Write(entries);
        read = VersionCustomization.Read(entries);
        Assert.False(read.physic || read.squishy);
        Assert.True(ExtraCustomizationUtils.HasFlag(entries, "customVeins"));
    }

    // A version moved Neck from Chest to ChestUp: the avatar's own animations still use the Chest path.
    [Test] public void MovedBonesKeepTheirFormerPathForTheBuild()
    {
        var owner = root.AddComponent<MyCustomBase>();
        var spine = Add("Spine", hips, Vector3.up * .1f);
        var chest = Add("Chest", spine, Vector3.up * .15f);
        var chestUp = Add("ChestUp", chest, Vector3.up * .05f);
        var neck = Add("Neck", chestUp, Vector3.up * .2f);
        owner.nativeMeshOriginalParents.Add(new NativeMeshOriginalParent { target = neck, parent = chest });
        var moved = VersionCustomizationBuild.MovedBones(owner, root.transform);
        Assert.AreEqual(1, moved.Count);
        Assert.AreSame(neck, moved[0].target);
        Assert.AreEqual("Armature/Hips/Spine/Chest/Neck", moved[0].formerPath);
    }

    private Vector3[] Bake()
    {
        var mesh = new Mesh();
        body.BakeMesh(mesh);
        var vertices = mesh.vertices.Select(v => body.transform.TransformPoint(v)).ToArray();
        Object.DestroyImmediate(mesh);
        return vertices;
    }

    private static Transform Add(string name, Transform parent, Vector3 position)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = position;
        return t;
    }
}
