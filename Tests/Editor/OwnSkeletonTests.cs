using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// The avatar's own skeleton and renderers, never an outfit's copy of the base (OwnSkeleton).
public class OwnSkeletonTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void Dispose() { foreach (var o in owned) if (o != null) Object.DestroyImmediate(o); owned.Clear(); }

    private static Transform Child(Transform parent, string name)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); return t;
    }

    // Armature/Hips/{Spine/Chest/Left shoulder, Leg} with a Body skinned to the four bones.
    private GameObject Model()
    {
        var root = new GameObject("Model"); owned.Add(root);
        var hips = Child(Child(root.transform, "Armature"), "Hips");
        var chest = Child(Child(hips, "Spine"), "Chest");
        var shoulder = Child(chest, "Left shoulder");
        var leg = Child(hips, "Leg");
        var body = Child(root.transform, "Body").gameObject.AddComponent<SkinnedMeshRenderer>();
        var mesh = new Mesh { name = "Body" }; owned.Add(mesh);
        body.sharedMesh = mesh;
        body.bones = new[] { hips, chest, shoulder, leg };
        return root;
    }

    // An avatar made from the model wearing a full copy of it as an "Outfit".
    private (GameObject model, GameObject avatar, GameObject outfit) Dressed()
    {
        var model = Model();
        var avatar = Object.Instantiate(model); avatar.name = "Avatar"; owned.Add(avatar);
        var outfit = Object.Instantiate(model); outfit.name = "Outfit"; outfit.transform.SetParent(avatar.transform, false);
        return (model, avatar, outfit);
    }

    [Test] public void OutfitCopyIsNeverTheAvatarsSkeletonOrBody()
    {
        var (model, avatar, outfit) = Dressed();
        var skeleton = OwnSkeleton.Resolve(avatar.transform, model.transform);
        Assert.That(skeleton.Failure, Is.Null);
        Assert.That(skeleton.Armature, Is.SameAs(avatar.transform.Find("Armature")));
        Assert.That(skeleton[model.transform.Find("Armature/Hips/Spine/Chest")], Is.SameAs(avatar.transform.Find("Armature/Hips/Spine/Chest")));
        Assert.That(skeleton.Renderers[model.GetComponentInChildren<SkinnedMeshRenderer>()].transform, Is.SameAs(avatar.transform.Find("Body")));
        Assert.That(skeleton.IsOwnRenderer(outfit.GetComponentInChildren<SkinnedMeshRenderer>()), Is.False);
        Assert.That(OwnSkeleton.ForAvatar(avatar.transform).ResolveBone("Armature/Hips/Leg"), Is.SameAs(avatar.transform.Find("Armature/Hips/Leg")));
    }

    [Test] public void RenamedBodyIsFoundWhereItIsAndTheOutfitsBodyIsNot()
    {
        var (model, avatar, outfit) = Dressed();
        var body = avatar.transform.Find("Body"); body.name = "Body.001";
        var renderer = OwnSkeleton.ForAvatar(avatar.transform).ResolveRenderer("Body", out string failure);
        Assert.That(failure, Is.Null);
        Assert.That(renderer.transform, Is.SameAs(body));
        Assert.That(renderer.transform.parent, Is.SameAs(avatar.transform), "used where it is");
    }

    [Test] public void RenamedArmatureIsStillTheOwnOne()
    {
        var (model, avatar, outfit) = Dressed();
        var own = avatar.transform.Find("Armature"); own.name = "Armature.004";
        var skeleton = OwnSkeleton.Resolve(avatar.transform, model.transform);
        Assert.That(skeleton.Failure, Is.Null);
        Assert.That(skeleton[model.transform.Find("Armature/Hips")], Is.SameAs(own.Find("Hips")));
        Assert.That(OwnSkeleton.ForAvatar(avatar.transform).ResolveBone("Armature/Hips/Spine"), Is.SameAs(own.Find("Hips/Spine")));
    }

    [Test] public void OutfitArmatureInsideTheOwnOneIsSkippedWhenABoneMoved()
    {
        var (model, avatar, outfit) = Dressed();
        var hips = avatar.transform.Find("Armature/Hips");
        outfit.transform.Find("Armature").SetParent(hips, false);
        // The person moved the shoulder under Spine: only the own one (not the outfit's copy) is the moved bone.
        var shoulder = hips.Find("Spine/Chest/Left shoulder"); shoulder.SetParent(hips.Find("Spine"), true);
        var skeleton = OwnSkeleton.Resolve(avatar.transform, model.transform);
        Assert.That(skeleton.Failure, Is.Null);
        Assert.That(skeleton[model.transform.Find("Armature/Hips/Spine/Chest/Left shoulder")], Is.SameAs(shoulder));
        Assert.That(skeleton.InOutfitCopy(hips.Find("Armature/Hips/Leg")), Is.True);
        Assert.That(OwnSkeleton.ForAvatar(avatar.transform).ResolveBone("Armature/Hips/Leg"), Is.SameAs(hips.Find("Leg")));
    }

    [Test] public void SameNamedSiblingsThatNothingTellsApartAreARefusalNamingBoth()
    {
        var model = Model();
        var avatar = Object.Instantiate(model); avatar.name = "Avatar"; owned.Add(avatar);
        var hips = avatar.transform.Find("Armature/Hips");
        var extra = Child(hips, "Leg2"); extra.name = "Spine";
        // Neither Spine is skinned: nothing tells them apart.
        var skeleton = OwnSkeleton.Resolve(avatar.transform, model.transform);
        Assert.That(skeleton.Failure, Does.Contain("'Armature/Hips/Spine'").And.Contain("Rename"));
    }

    [Test] public void ABoneOnlyAnOutfitHasIsNeverTheAvatars()
    {
        var (model, avatar, outfit) = Dressed();
        var knee = outfit.transform.Find("Armature/Hips/Leg");
        Child(knee, "TopFut_R");
        Assert.That(OwnSkeleton.ForAvatar(avatar.transform).ResolveBone("Armature/Hips/Leg/TopFut_R"), Is.Null);
    }

    [Test] public void TwoOwnBodiesNamedAlikeAreARefusalNamingThem()
    {
        var (model, avatar, outfit) = Dressed();
        var body = avatar.transform.Find("Body"); body.name = "Body.001";
        var copy = Object.Instantiate(body.gameObject, avatar.transform); copy.name = "Body (1)";
        OwnSkeleton.ForAvatar(avatar.transform).ResolveRenderer("Body", out string failure);
        Assert.That(failure, Does.Contain("'Body.001'").And.Contain("'Body (1)'"));
    }

    [Test] public void AnArmatureOneLevelDeeperIsTheOwnOneAndStaysWhereItIs()
    {
        var model = Model();
        var avatar = Object.Instantiate(model); avatar.name = "Avatar"; owned.Add(avatar);
        var holder = Child(avatar.transform, "Model");
        var armature = avatar.transform.Find("Armature"); armature.SetParent(holder, false);
        avatar.transform.Find("Body").SetParent(holder, false);
        var skeleton = OwnSkeleton.Resolve(avatar.transform, model.transform);
        Assert.That(skeleton.Failure, Is.Null);
        Assert.That(skeleton.Armature, Is.SameAs(armature));
        Assert.That(skeleton[model.transform.Find("Armature")], Is.SameAs(armature));
        Assert.That(skeleton[model.transform.Find("Armature/Hips/Leg")], Is.SameAs(armature.Find("Hips/Leg")));
        SmrPathService.RestoreTargetTransformHierarchyFromFbxRoot(avatar.transform, model.transform);
        Assert.That(armature.parent, Is.SameAs(holder), "the armature is never moved");
        Assert.That(holder.Find("Body").parent, Is.SameAs(holder), "nor the renderers");
        Assert.That(armature.Find("Hips").parent, Is.SameAs(armature));
    }
}
