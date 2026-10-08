using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class FbxHierarchyRestoreTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void Clean() { foreach (var item in owned) if (item != null) Object.DestroyImmediate(item); owned.Clear(); }

    // An Ultirex-applied Rexouium: the shoulder moved under ChestUp (its original parent was not recorded) and the
    // installed logic prefab carries same-named "Target Bones". Reset must restore the FBX hierarchy and pose anyway.
    [Test]
    public void ResetRestoresMovedBonesUnderTheirFbxParentsDespiteSameNamedLogicObjects()
    {
        var fbx = Rig("Original");
        var avatar = Rig("Avatar");
        var shoulder = avatar.transform.Find("Armature/Hips/Chest/Left shoulder");
        shoulder.SetParent(avatar.transform.Find("Armature/Hips/Chest/ChestUp"), true);
        shoulder.localPosition = new Vector3(0, .3f, .1f);
        var logicArm = Child(Child(Child(avatar.transform, "mcb logic"), "Target Bones"), "Left arm");
        logicArm.localPosition = new Vector3(5, 5, 5);

        Assert.That(SmrPathService.RestoreTargetTransformHierarchyFromFbxRoot(avatar.transform, fbx.transform), Is.GreaterThan(0));
        Assert.That(shoulder.parent, Is.SameAs(avatar.transform.Find("Armature/Hips/Chest")));
        Assert.That(shoulder.localPosition, Is.EqualTo(new Vector3(.1f, .2f, 0)));
        Assert.That(shoulder.Find("Left arm").localPosition, Is.EqualTo(new Vector3(.2f, 0, 0)));
        Assert.That(logicArm.localPosition, Is.EqualTo(new Vector3(5, 5, 5)), "Logic objects are not bones.");
    }

    // A replaced renderer the user deleted: the others are still restored and the missing one is reported.
    [Test]
    public void RestoreSkipsReplacedRenderersTheAvatarNoLongerHas()
    {
        var fbx = Rig("Original");
        var avatar = Rig("Avatar");
        var bodyMesh = Own(new Mesh { name = "Body" });
        Renderer(fbx.transform, "Body", bodyMesh);
        Renderer(fbx.transform, "Hair", Own(new Mesh { name = "Hair" }));
        var body = Renderer(avatar.transform, "Body", Own(new Mesh { name = "Payload" }));
        var entries = new[] { "Body", "Hair" }.Select(path => new ModelFileSmrPathData
            { avatarPath = path, fbxMeshPath = path, meshName = path, rendererName = path }).ToList();

        Assert.That(SmrPathService.RestoreTargetStateFromFbxRoot(avatar.transform, fbx.transform, entries), Is.EqualTo(0));
        var missing = new List<string>();
        Assert.That(SmrPathService.RestoreTargetStateFromFbxRoot(avatar.transform, fbx.transform, entries,
            skipMissingRenderers: true, missingRenderers: missing), Is.EqualTo(1));
        Assert.That(body.sharedMesh, Is.SameAs(bodyMesh));
        Assert.That(missing, Is.EqualTo(new[] { "Hair" }));
    }

    private T Own<T>(T item) where T : Object { owned.Add(item); return item; }

    private static SkinnedMeshRenderer Renderer(Transform parent, string name, Mesh mesh)
    {
        var renderer = Child(parent, name).gameObject.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }

    private GameObject Rig(string name)
    {
        var root = new GameObject(name); owned.Add(root);
        var chest = Child(Child(Child(root.transform, "Armature"), "Hips"), "Chest");
        Child(chest, "ChestUp").localPosition = new Vector3(0, .05f, 0);
        var shoulder = Child(chest, "Left shoulder"); shoulder.localPosition = new Vector3(.1f, .2f, 0);
        Child(shoulder, "Left arm").localPosition = new Vector3(.2f, 0, 0);
        return root;
    }

    private static Transform Child(Transform parent, string name)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }
}
