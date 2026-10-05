#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static partial class MCBDeliveryHealthCheck
{
    /// <summary>
    /// An unprotected payload is stored without XOR, applies to an avatar built from another original without any key,
    /// and refuses a key; an XOR payload still refuses to apply without its original model.
    /// </summary>
    public static void RunPlainOrThrow()
    {
        string id = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Path.GetTempPath(), "mcb-plain-health-" + id);
        Directory.CreateDirectory(folder);
        // The authoring original exists for the build only; the user's avatar comes from a different model.
        string authoringOriginal = Path.Combine(folder, "authoring.fbx");
        File.WriteAllBytes(authoringOriginal, Enumerable.Range(0, 257).Select(i => (byte)(i * 31)).ToArray());
        var original = MeshData(1f); var custom = MeshData(1.4f); var userMesh = MeshData(0.8f);
        var source = Rig("MCB_Plain_Source", original);
        var author = Rig("MCB_Plain_Author", custom);
        var target = Rig("MCB_Plain_Target", userMesh);
        string cacheFolder = null;
        try
        {
            var paths = new[] { new ModelFileSmrPathData { avatarPath = "Body", fbxMeshPath = "Body", meshName = "Body", rendererName = "Body" } };
            var manager = new FileManagerService();
            var build = NativeMeshPayloadService.WriteEncryptedPayload(authoringOriginal, author, paths, manager,
                Path.Combine(folder, "mesh.bin"), sourcePoseRoot: source.transform, createDeliveryVariants: true, encrypt: false);
            var variant = build.variants?.FirstOrDefault() ?? throw new InvalidOperationException("Plain delivery variants were not built.");
            byte[] stored = File.ReadAllBytes(Path.Combine(folder, variant.path));
            // Stored bytes are the codec output itself: decoding them needs no key.
            MCBCompression.Decode(stored, variant.codec);
            var patch = new ModelFileData { path = variant.path, hash = variant.hash, outputHash = variant.outputHash, type = "BIN", role = "PATCH",
                transform = NativeMeshPayloadService.PlainTransformName, compression = variant.codec, smrPaths = paths.ToList(),
                metadata = new Dictionary<string, object> { { NativeMeshPayloadService.PayloadCompressionMetadataKey, variant.codec } } };
            var version = new CustomBaseVersion { assetId = 999997, version = "plain-" + id, defaultAviVersion = "1",
                protection = new VersionProtection { xor = false, discordRole = true },
                extraCustomization = new object[] { NativeMeshPayloadService.ExtraCustomizationKey }, versionFiles = new[] { patch } };
            cacheFolder = "Assets/MCB/generated/advancedMeshPayloads/999997/" + version.version;
            string bin = Path.Combine(folder, variant.path);
            bool keyRejected = false;
            try { NativeMeshPayloadService.ApplyEncryptedPayload(target.transform, version, patch, bin, authoringOriginal, manager); }
            catch (InvalidOperationException) { keyRejected = true; }
            if (!keyRejected) throw new InvalidOperationException("A plain payload accepted an original-model key.");

            NativeMeshPayloadService.ApplyEncryptedPayload(target.transform, version, patch, bin, null, manager);
            AssertMesh(custom, target.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh);

            var xorPatch = new ModelFileData { path = patch.path, hash = patch.hash, outputHash = patch.outputHash, type = "BIN", role = "PATCH",
                transform = NativeMeshPayloadService.TransformName, compression = patch.compression, metadata = patch.metadata };
            bool keyRequired = false;
            try { NativeMeshPayloadService.MaterializeEncryptedPayloadAsset(version, xorPatch, bin, null, manager); }
            catch (FileNotFoundException) { keyRequired = true; }
            if (!keyRequired) throw new InvalidOperationException("An XOR payload applied without its original model.");
        }
        finally
        {
            Object.DestroyImmediate(source); Object.DestroyImmediate(author); Object.DestroyImmediate(target);
            foreach (var mesh in new[] { original, custom, userMesh }) if (mesh != null) Object.DestroyImmediate(mesh);
            if (cacheFolder != null && AssetDatabase.IsValidFolder(cacheFolder)) AssetDatabase.DeleteAsset(cacheFolder);
            Directory.Delete(folder, true);
        }
    }
}
#endif
