using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class AnimatorAvatarPoseTests
{
    private GameObject root;

    [TearDown] public void Clean() { if (root != null) Object.DestroyImmediate(root); }

    // A version moves Neck under ChestUp (as Ultirex does on a Rexouium). The Animator had bound the original
    // skeleton; assigning the version's humanoid Avatar must not write the original Neck pose back (stretched neck).
    [Test]
    public void AssigningAnAvatarKeepsAReparentedSkeletonThroughLaterRebinds()
    {
        var bones = Humanoid();
        var animator = root.AddComponent<Animator>();
        animator.avatar = Build(bones);
        animator.Rebind();

        var neck = bones["Neck"];
        neck.SetParent(bones["ChestUp"], true);
        var pose = new Vector3(0, .19f, -.03f);
        neck.localPosition = pose;
        AvatarDefinitionGenerationService.AssignAvatarKeepingPose(animator, Build(bones));
        Assert.That(Vector3.Distance(neck.localPosition, pose), Is.LessThan(1e-5f));

        animator.Rebind();
        Assert.That(Vector3.Distance(neck.localPosition, pose), Is.LessThan(1e-5f), "A later rebind wrote the original pose back.");
    }

    private Dictionary<string, Transform> Humanoid()
    {
        root = new GameObject("Avatar");
        var bones = new Dictionary<string, Transform>();
        void Add(string name, string parent, Vector3 position)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent == null ? root.transform : bones[parent], false);
            bone.localPosition = position;
            bones[name] = bone;
        }
        Add("Hips", null, new Vector3(0, 1, 0)); Add("Spine", "Hips", new Vector3(0, .1f, 0)); Add("Chest", "Spine", new Vector3(0, .15f, 0));
        Add("ChestUp", "Chest", new Vector3(0, .05f, 0)); bones["ChestUp"].localRotation = Quaternion.Euler(10, 0, 0);
        Add("Neck", "Chest", new Vector3(0, .24f, 0)); Add("Head", "Neck", new Vector3(0, .08f, 0));
        foreach (var side in new[] { "L", "R" })
        {
            float x = side == "L" ? -1 : 1;
            Add("UpperArm" + side, "Chest", new Vector3(.15f * x, .15f, 0)); Add("LowerArm" + side, "UpperArm" + side, new Vector3(.25f * x, 0, 0));
            Add("Hand" + side, "LowerArm" + side, new Vector3(.25f * x, 0, 0));
            Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
            Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
        }
        return bones;
    }

    private Avatar Build(Dictionary<string, Transform> bones)
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
        Assert.That(avatar.isHuman, Is.True);
        return avatar;
    }
}
