using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Ultirex 5.1 was exported with its legs posed 26° outward: the payload's bone table carries that pose while its meshes
// are bound at rest, and the applied avatar stands at rest. The humanoid definition must rest the way the avatar does,
// or VRChat's standing animation folds the legs inward.
public class NativeHumanoidBindPoseTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown]
    public void Clean()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test]
    public void TheHumanoidRestsAtTheBindPoseNotTheExportedPose()
    {
        var (rest, restBones) = Humanoid("Rest", 0f);
        var (posed, posedBones) = Humanoid("Posed", 25f);
        var mapping = Build(rest);

        // One vertex weighted to each bone: only weighted bones have a real bind pose.
        var order = restBones.Values.ToArray();
        var mesh = new Mesh { vertices = order.Select(b => b.position).ToArray() };
        owned.Add(mesh);
        mesh.boneWeights = order.Select((b, i) => new BoneWeight { boneIndex0 = i, weight0 = 1 }).ToArray();
        mesh.bindposes = order.Select(b => b.worldToLocalMatrix * rest.transform.localToWorldMatrix).ToArray();

        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        owned.Add(payload);
        payload.renderers.Add(new NativeMeshPayloadRenderer
        {
            avatarPath = "Body", mesh = mesh,
            bonePaths = order.Select(b => AnimationUtility.CalculateTransformPath(b, rest.transform)).ToList()
        });
        foreach (var bone in posed.GetComponentsInChildren<Transform>(true).Where(t => t != posed.transform))
            payload.bones.Add(new NativeMeshPayloadBone
            {
                path = AnimationUtility.CalculateTransformPath(bone, posed.transform),
                localPosition = bone.localPosition, localRotation = bone.localRotation, localScale = bone.localScale
            });
        Assert.That(Quaternion.Angle(posedBones["UpperLegL"].localRotation, restBones["UpperLegL"].localRotation), Is.GreaterThan(20f));

        var avatar = AvatarDefinitionGenerationService.BuildNativeMeshAvatar(payload, mapping);
        owned.Add(avatar);

        Assert.That(avatar != null && avatar.isHuman, Is.True);
        foreach (string leg in new[] { "UpperLegL", "UpperLegR" })
        {
            var skeleton = avatar.humanDescription.skeleton.Single(b => b.name == leg);
            Assert.That(Quaternion.Angle(skeleton.rotation, restBones[leg].localRotation), Is.LessThan(.5f), leg + " rests as its mesh is bound.");
        }
    }

    private (GameObject root, Dictionary<string, Transform> bones) Humanoid(string name, float legSpread)
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
            Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
            bones["UpperLeg" + side].localRotation = Quaternion.Euler(0, 0, legSpread * -x);
        }
        return (root, bones);
    }

    private Avatar Build(GameObject root)
    {
        var map = new Dictionary<string, string>
        {
            ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
            ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
            ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
            ["LeftUpperLeg"] = "UpperLegL", ["LeftLowerLeg"] = "LowerLegL", ["LeftFoot"] = "FootL",
            ["RightUpperLeg"] = "UpperLegR", ["RightLowerLeg"] = "LowerLegR", ["RightFoot"] = "FootR",
        };
        var description = new HumanDescription
        {
            human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
            skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
            upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
        };
        var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
        owned.Add(avatar);
        Assert.That(avatar.isHuman, Is.True);
        return avatar;
    }
}
