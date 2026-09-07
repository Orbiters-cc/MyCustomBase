#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using MCBEditorUtils;

public class MCBDynamicNormalPayloadTests
{
    [Test]
    public void DirectNormalFramesMatchTemporaryMeshBakeByteForByte()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-normal-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var mesh = new Mesh { name = "Body" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        mesh.triangles = new[] { 0, 1, 2 }; mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
        mesh.RecalculateNormals(); mesh.RecalculateTangents();
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { weight0 = 1f }, 3).ToArray();
        mesh.AddBlendShapeFrame("muscle", 50, new[] { Vector3.up * 0.0000003f, Vector3.zero, Vector3.forward * .1f }, null, null);
        mesh.AddBlendShapeFrame("muscle", 100, new[] { Vector3.up * 0.0000006f, Vector3.zero, Vector3.forward * .2f }, null, null);
        var source = Rig(mesh); var slow = Rig(mesh); var fast = Rig(mesh);
        Mesh baked = null;
        try
        {
            string key = Path.Combine(folder, "source.fbx"); File.WriteAllBytes(key, new byte[] { 1, 5, 9 });
            var renderer = slow.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            DynamicNormals.ForRoot(slow.transform).limitToMeshes(new[] { renderer })
                .applyToBlendshapes(new[] { "muscle" }).enable(true).saveAsAsset(false).Apply();
            baked = renderer.sharedMesh;
            var paths = new[] { new ModelFileSmrPathData { avatarPath = "Body", fbxMeshPath = "Body", meshName = "Body", rendererName = "Body" } };
            var manager = new FileManagerService();
            var expected = NativeMeshPayloadService.WriteEncryptedPayload(key, slow, paths, manager,
                Path.Combine(folder, "slow.bin"), sourcePoseRoot: source.transform);
            var actual = NativeMeshPayloadService.WriteEncryptedPayload(key, fast, paths, manager,
                Path.Combine(folder, "fast.bin"), bakeDynamicNormals: true, sourcePoseRoot: source.transform);
            Assert.That(actual.payloadHash, Is.EqualTo(expected.payloadHash));
            Assert.That(File.ReadAllBytes(Path.Combine(folder, "fast.bin")), Is.EqualTo(File.ReadAllBytes(Path.Combine(folder, "slow.bin"))));
            Assert.That(fast.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(mesh));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(slow); UnityEngine.Object.DestroyImmediate(fast);
            if (baked != null && baked != mesh) UnityEngine.Object.DestroyImmediate(baked);
            UnityEngine.Object.DestroyImmediate(mesh); Directory.Delete(folder, true);
        }
    }

    private static GameObject Rig(Mesh mesh)
    {
        var root = new GameObject("Rig");
        var bone = new GameObject("Bone"); bone.transform.SetParent(root.transform, false);
        var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh; renderer.bones = new[] { bone.transform }; renderer.rootBone = bone.transform;
        return root;
    }
}
#endif
