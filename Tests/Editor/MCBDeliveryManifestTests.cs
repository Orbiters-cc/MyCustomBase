#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

public class MCBDeliveryManifestTests
{
    [Test] public void ManifestChangesOnlyAuthorizedCodecFieldsAndRejectsTamperingAtomically()
    {
        var selected = new MCBPayloadVariant { codec = "LZ4", path = "body.lz4.bin", hash = new string('a', 64),
            outputHash = new string('b', 64), bytes = 42, decodedBytes = 100 };
        var paths = new List<ModelFileSmrPathData> { new ModelFileSmrPathData { avatarPath = "Body", fbxMeshPath = "Armature/Body" } };
        var patch = new ModelFileData { path = "body.bin", sourceModelFileId = 123, hash = "old", outputHash = "old",
            transform = NativeMeshPayloadService.TransformName, smrPaths = paths, compression = "ZSTD",
            metadata = new Dictionary<string, object> { { "sourcePath", "base.fbx" }, { "payloadCompression", "ZSTD" },
                { "deliveryVariants", new[] { selected } } } };
        var version = new CustomBaseVersion { versionFiles = new[] { patch } };
        string manifest = "{\"schema\":1,\"codec\":\"LZ4\",\"files\":[{\"codec\":\"LZ4\",\"path\":\"body.bin\",\"hash\":\""
            + selected.hash + "\",\"outputHash\":\"" + selected.outputHash + "\",\"bytes\":42,\"decodedBytes\":100}]}";
        Assert.Throws<InvalidDataException>(() => MCBVersionDelivery.ApplyManifest(version, manifest.Replace(selected.hash, new string('c', 64))));
        Assert.That(patch.hash, Is.EqualTo("old")); Assert.That(patch.compression, Is.EqualTo("ZSTD"));
        MCBVersionDelivery.ApplyManifest(version, manifest);
        Assert.That(patch.hash, Is.EqualTo(selected.hash)); Assert.That(patch.compression, Is.EqualTo("LZ4"));
        Assert.That(patch.path, Is.EqualTo("body.bin")); Assert.That(patch.sourceModelFileId, Is.EqualTo(123));
        Assert.That(patch.smrPaths, Is.SameAs(paths)); Assert.That(patch.metadata["sourcePath"], Is.EqualTo("base.fbx"));
        MCBVersionDelivery.ApplyManifest(version, manifest);
        Assert.That(patch.smrPaths, Is.SameAs(paths));
    }
}
#endif
