#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class MCBDeliveryHealthCheck
{
    public static void RunOrThrow()
    {
        string id = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Path.GetTempPath(), "mcb-delivery-health-" + id);
        Directory.CreateDirectory(folder);
        string key = Path.Combine(folder, "source.fbx.originalbase");
        File.WriteAllBytes(key, Enumerable.Range(0, 513).Select(i => (byte)(i * 17)).ToArray());
        var original = MeshData(1f); var custom = MeshData(1.35f); var targetMesh = Object.Instantiate(original);
        var source = Rig("MCB_Delivery_Source", original);
        var author = Rig("MCB_Delivery_Author", custom);
        var target = Rig("MCB_Delivery_Target", targetMesh);
        var cacheFolders = new List<string>();
        try {
            var paths = new[] { new ModelFileSmrPathData { avatarPath = "Body", fbxMeshPath = "Body", meshName = "Body", rendererName = "Body" } };
            var manager = new FileManagerService();
            var build = NativeMeshPayloadService.WriteEncryptedPayload(key, author, paths, manager,
                Path.Combine(folder, "mesh.bin"), sourcePoseRoot: source.transform, createDeliveryVariants: true);
            if (build.variants == null || build.variants.Count == 0) throw new InvalidOperationException("Delivery variants were not built.");
            foreach (var variant in build.variants) {
                var patch = new ModelFileData { path = variant.path, hash = variant.hash, outputHash = variant.outputHash,
                    type = "BIN", role = "PATCH", transform = NativeMeshPayloadService.TransformName, compression = variant.codec,
                    sourceModelFileId = 1, smrPaths = paths.ToList(), metadata = new Dictionary<string, object> {
                        { "sourcePath", key }, { NativeMeshPayloadService.PayloadCompressionMetadataKey, variant.codec }
                    } };
                var version = new CustomBaseVersion { assetId = 999998, version = "delivery-" + id + "-" + variant.codec,
                    defaultAviVersion = "1", extraCustomization = new object[] { NativeMeshPayloadService.ExtraCustomizationKey },
                    versionFiles = new[] { patch } };
                string cacheFolder = "Assets/MCB/generated/advancedMeshPayloads/999998/" + version.version;
                cacheFolders.Add(cacheFolder);
                string validBin = Path.Combine(folder, variant.path), corruptBin = Path.Combine(folder, "corrupt.bin");
                var corrupt = File.ReadAllBytes(validBin); corrupt[corrupt.Length - 1] ^= 1; File.WriteAllBytes(corruptBin, corrupt);
                var rendererBefore = target.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                var meshBefore = rendererBefore.sharedMesh;
                bool rejected = false;
                try { NativeMeshPayloadService.ApplyEncryptedPayload(target.transform, version, patch, corruptBin, key, manager); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || rendererBefore.sharedMesh != meshBefore) throw new InvalidOperationException("Corrupt delivery was not rejected before changing the avatar.");
                var payload = NativeMeshPayloadService.ApplyEncryptedPayload(target.transform, version, patch,
                    validBin, key, manager);
                var renderer = target.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
                AssertMesh(custom, renderer.sharedMesh);
                var expectedBones = new[] { target.transform.Find("Armature/Hips"), target.transform.Find("Armature/Hips/Tip") };
                if (!renderer.bones.SequenceEqual(expectedBones) || renderer.rootBone != expectedBones[0]) throw new InvalidOperationException("Bone palette association changed.");
                if (!NativeMeshPayloadService.TryGetAppliedGeneratedMeshVersion(target.transform, out int assetId, out string versionName)
                    || assetId != version.assetId || versionName != version.version) throw new InvalidOperationException("Applied asset/version association changed.");
                using (var saved = File.OpenRead(AssetDatabase.GetAssetPath(payload)))
                    if (saved.ReadByte() == '%') throw new InvalidOperationException("Generated mesh cache was saved as text.");
                var cached = NativeMeshPayloadService.ApplyEncryptedPayload(target.transform, version, patch,
                    Path.Combine(folder, variant.path), key, manager);
                if (cached != payload) throw new InvalidOperationException("Warm application rebuilt a valid mesh cache.");
                AssertMesh(custom, renderer.sharedMesh);
                SmrPathService.RestoreTargetStateFromFbxRoot(target.transform, source.transform, paths);
                AssertMesh(original, renderer.sharedMesh);
                if (!renderer.bones.SequenceEqual(expectedBones)) throw new InvalidOperationException("Reset changed bone association.");

                // Exercise the RAM preloading and coroutine commit path used by Download & Apply.
                AssetDatabase.DeleteAsset(cacheFolder);
                var preload = NativeMeshPayloadService.StartEncryptedPayloadPreparation(version, patch, validBin, key,
                    Task.FromResult(File.ReadAllBytes(validBin)), Task.FromResult(File.ReadAllBytes(key)));
                preload.task.GetAwaiter().GetResult();
                var routine = NativeMeshPayloadService.ApplyEncryptedPayloadCoroutine(target.transform, version, patch,
                    validBin, key, manager, null, preload);
                try { while (routine.MoveNext()) { } }
                finally { (routine as IDisposable)?.Dispose(); }
                AssertMesh(custom, renderer.sharedMesh);
                if (!renderer.bones.SequenceEqual(expectedBones) || renderer.rootBone != expectedBones[0])
                    throw new InvalidOperationException("Prepared delivery changed bone association.");
                SmrPathService.RestoreTargetStateFromFbxRoot(target.transform, source.transform, paths);
                AssertMesh(original, renderer.sharedMesh);
            }
        } finally {
            Object.DestroyImmediate(source); Object.DestroyImmediate(author); Object.DestroyImmediate(target);
            foreach (var mesh in new[] { original, custom, targetMesh }) if (mesh != null) Object.DestroyImmediate(mesh);
            foreach (string cache in cacheFolders) if (AssetDatabase.IsValidFolder(cache)) AssetDatabase.DeleteAsset(cache);
            Directory.Delete(folder, true);
        }
    }

    static GameObject Rig(string name, Mesh mesh)
    {
        var root = new GameObject(name);
        var armature = new GameObject("Armature"); armature.transform.SetParent(root.transform, false);
        var hips = new GameObject("Hips"); hips.transform.SetParent(armature.transform, false);
        var tip = new GameObject("Tip"); tip.transform.SetParent(hips.transform, false); tip.transform.localPosition = Vector3.up;
        var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh; renderer.bones = new[] { hips.transform, tip.transform }; renderer.rootBone = hips.transform;
        return root;
    }
    static Mesh MeshData(float height)
    {
        var mesh = new Mesh { name = "Body" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(0, height, 0) };
        mesh.triangles = new[] { 0, 1, 2 }; mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
        mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.down) };
        mesh.boneWeights = new[] {
            new BoneWeight { boneIndex0 = 0, boneIndex1 = 1, weight0 = .25f, weight1 = .75f },
            new BoneWeight { boneIndex0 = 1, boneIndex1 = 0, weight0 = .6f, weight1 = .4f },
            new BoneWeight { boneIndex0 = 1, weight0 = 1f }
        };
        // Tiny authored movement must survive sparse encoding as exactly as large movements.
        mesh.AddBlendShapeFrame("Shape", 50, new[] { Vector3.up * 0.0000003f, Vector3.zero, Vector3.right * .1f }, null, null);
        mesh.AddBlendShapeFrame("Shape", 100, new[] { Vector3.zero, Vector3.zero, Vector3.right * .2f }, null, null);
        mesh.RecalculateBounds(); return mesh;
    }
    static void AssertMesh(Mesh expected, Mesh actual)
    {
        if (actual == null || !expected.vertices.SequenceEqual(actual.vertices) || !expected.triangles.SequenceEqual(actual.triangles)
            || !expected.normals.SequenceEqual(actual.normals) || !expected.uv.SequenceEqual(actual.uv)
            || !expected.bindposes.SequenceEqual(actual.bindposes) || !expected.boneWeights.SequenceEqual(actual.boneWeights)
            || expected.blendShapeCount != actual.blendShapeCount) throw new InvalidOperationException("Mesh geometry, bindposes or skin weights changed.");
        for (int shape = 0; shape < expected.blendShapeCount; shape++) {
            if (expected.GetBlendShapeName(shape) != actual.GetBlendShapeName(shape)
                || expected.GetBlendShapeFrameCount(shape) != actual.GetBlendShapeFrameCount(shape)) throw new InvalidOperationException("Blendshape identity changed.");
            for (int frame = 0; frame < expected.GetBlendShapeFrameCount(shape); frame++) {
                var left = new Vector3[expected.vertexCount]; var right = new Vector3[actual.vertexCount];
                expected.GetBlendShapeFrameVertices(shape, frame, left, null, null); actual.GetBlendShapeFrameVertices(shape, frame, right, null, null);
                if (!left.SequenceEqual(right) || expected.GetBlendShapeFrameWeight(shape, frame) != actual.GetBlendShapeFrameWeight(shape, frame))
                    throw new InvalidOperationException("Blendshape frame data changed.");
            }
        }
    }
}
#endif
