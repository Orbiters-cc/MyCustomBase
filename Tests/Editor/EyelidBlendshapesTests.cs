using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

// Ultirex's mesh orders its blendshapes differently from Rexouium's: the descriptor's eyelid indices must follow the names.
public class EyelidBlendshapesTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown]
    public void Clean()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test]
    public void EyelidShapesFollowTheirNamesOntoTheNewMesh()
    {
        var avatar = new GameObject("Avatar"); owned.Add(avatar);
        var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
        var body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
        body.transform.SetParent(avatar.transform, false);
        var original = Mesh("Smile", "Blink", "LookDown", "LookUp");
        var version = Mesh("Throat", "TailSkinny", "LookUp", "Blink", "Smile");
        body.sharedMesh = original;
        var eyes = descriptor.customEyeLookSettings;
        eyes.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
        eyes.eyelidsSkinnedMesh = body;
        eyes.eyelidsBlendshapes = new[] { 1, 3, 2 };
        descriptor.customEyeLookSettings = eyes;

        EyelidBlendshapes.Assign(body, version);

        Assert.AreSame(version, body.sharedMesh);
        Assert.AreEqual(new[] { 3, 2, -1 }, descriptor.customEyeLookSettings.eyelidsBlendshapes, "Blink and LookUp follow; LookDown is gone, so it is off");
    }

    private Mesh Mesh(params string[] shapes)
    {
        var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } };
        owned.Add(mesh);
        foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, new Vector3[3], null, null);
        return mesh;
    }
}
