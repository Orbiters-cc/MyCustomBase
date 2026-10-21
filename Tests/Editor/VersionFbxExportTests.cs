using System;
using System.IO;
using NUnit.Framework;

/// <summary>Export FBX on a version's details: a replacement decodes to the very file its creator published.</summary>
public sealed class VersionFbxExportTests
{
    // An XOR replacement decodes from the original model to the published file (its hash checked); another original
    // model, or a version not downloaded, says why instead of writing a wrong file.
    [Test]
    public void AReplacementDecodesToThePublishedFileOrSaysWhy()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-fbx-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var random = new Random(7);
            var original = new byte[4096];
            var published = new byte[3000];
            random.NextBytes(original);
            random.NextBytes(published);
            string originalPath = Path.Combine(folder, "Body.fbx.originalbase");
            string binPath = Path.Combine(folder, "patch.bin");
            File.WriteAllBytes(originalPath, original);
            File.WriteAllBytes(binPath, MCBXor.Transform(original, published));
            var patch = new VersionActions.VersionModelPatch
            {
                Patch = new ModelFileData { role = "PATCH", outputHash = Hex(published) },
                TargetFbxPath = "Assets/Body.fbx",
                OriginalFbxPath = originalPath,
                BinPath = binPath,
            };
            Assert.That(VersionFbxExport.Decode(patch), Is.EqualTo(published), "the creator's file");

            original[0] ^= 0xFF;
            File.WriteAllBytes(originalPath, original);
            var wrong = Assert.Throws<InvalidDataException>(() => VersionFbxExport.Decode(patch));
            Assert.That(wrong.Message, Does.Contain("original model"), "another original model is named as the cause");

            File.Delete(binPath);
            var missing = Assert.Throws<FileNotFoundException>(() => VersionFbxExport.Decode(patch));
            Assert.That(missing.Message, Does.Contain("download"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // A version of advanced meshes exports the avatar's renderers with the version's meshes on them (decoded from its
    // payload, nothing applied): the FBX holds the version's shape, and the avatar keeps its own mesh.
    [Test]
    public void AnAdvancedVersionExportsTheAvatarWithItsMeshesWithoutApplyingThem()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var health = typeof(NativeMeshPayloadHealthCheck);
        object Call(string name, params object[] args) => health.GetMethod(name, flags).Invoke(null, args);
        string folder = Path.Combine(Path.GetTempPath(), "mcb-fbx-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string sourceKeyPath = Path.Combine(folder, "source.fbx"), binPath = Path.Combine(folder, "payload.bin"), output = Path.Combine(folder, "Avatar 0.0.1.fbx");
        UnityEngine.GameObject source = null, avatar = null, custom = null;
        try
        {
            File.WriteAllBytes(sourceKeyPath, (byte[])Call("BuildDeterministicSourceKey"));
            source = (UnityEngine.GameObject)Call("BuildRig", "MCB_FbxExport_Source", Call("CreateTriangleMesh", "BodyMesh", 1f), 0f);
            avatar = (UnityEngine.GameObject)Call("BuildRig", "MCB_FbxExport_Avatar", Call("CreateTriangleMesh", "BodyMesh", 1f), 0f);
            custom = (UnityEngine.GameObject)Call("BuildRig", "MCB_FbxExport_Custom", Call("CreateQuadMesh", "BodyMesh", 1.35f), 0.25f);
            var smrPaths = new System.Collections.Generic.List<ModelFileSmrPathData>
            {
                new ModelFileSmrPathData { avatarPath = "Body", fbxMeshPath = "Body", meshName = "BodyMesh", rendererName = "Body" }
            };
            var built = NativeMeshPayloadService.WriteEncryptedPayload(sourceKeyPath, custom, smrPaths, new FileManagerService(), binPath, sourcePoseRoot: source.transform);
            var patchFile = new ModelFileData
            {
                id = 1, path = "payload.bin", type = "BIN", role = "PATCH", transform = NativeMeshPayloadService.TransformName,
                compression = built.payloadCompression, outputHash = built.payloadHash, smrPaths = smrPaths,
                metadata = new System.Collections.Generic.Dictionary<string, object> { { NativeMeshPayloadService.PayloadCompressionMetadataKey, built.payloadCompression } },
            };
            var version = new CustomBaseVersion { assetId = 999990, version = "0.0.1", defaultAviVersion = "1.0.0", versionFiles = new[] { patchFile } };
            var patch = new VersionActions.VersionModelPatch { Patch = patchFile, AdvancedMesh = true, BinPath = binPath, OriginalFbxPath = sourceKeyPath };
            var avatarMesh = avatar.transform.Find("Body").GetComponent<UnityEngine.SkinnedMeshRenderer>().sharedMesh;

            var result = VersionFbxExport.WriteAdvanced(avatar.transform, version, new System.Collections.Generic.List<VersionActions.VersionModelPatch> { patch }, output);
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Meshes, Is.EqualTo(1));
            var meshes = Orbiters.Toolkit.Editor.Meshes.FbxReader.Read(File.ReadAllBytes(output));
            Assert.That(meshes.Count, Is.EqualTo(1));
            Assert.That(meshes[0].Points.Length, Is.EqualTo(4), "the version's quad, not the avatar's triangle");
            Assert.That(System.Linq.Enumerable.Max(meshes[0].Points, p => p.y), Is.EqualTo(1.35f).Within(1e-3f));
            Assert.That(avatar.transform.Find("Body").GetComponent<UnityEngine.SkinnedMeshRenderer>().sharedMesh, Is.SameAs(avatarMesh), "nothing was applied");
            Assert.That(avatarMesh.vertexCount, Is.EqualTo(3));
        }
        finally
        {
            foreach (var root in new[] { source, avatar, custom }) if (root != null) UnityEngine.Object.DestroyImmediate(root);
            Directory.Delete(folder, true);
        }
    }

    private static string Hex(byte[] bytes)
    {
        using (var sha = MCBHashing.CreateSha256())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
