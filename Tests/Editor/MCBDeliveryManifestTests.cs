#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

public class MCBDeliveryManifestTests
{
    [Test] public void ReusedMeshesCanKeepDifferentVerifiedCodecsInOneVersion()
    {
        var body = new MCBPayloadVariant { codec = "LZ4", path = "body.bin", hash = new string('a', 64), outputHash = new string('b', 64), bytes = 10, decodedBytes = 20 };
        var hair = new MCBPayloadVariant { codec = "ZSTD", path = "hair.bin", hash = new string('c', 64), outputHash = new string('d', 64), bytes = 5, decodedBytes = 20 };
        var version = new CustomBaseVersion { versionFiles = new[] { body, hair }.Select(v => new ModelFileData {
            path = v.path, transform = NativeMeshPayloadService.TransformName, metadata = new Dictionary<string, object> { { "deliveryVariants", new[] { v } } } }).ToArray() };
        var json = "{\"schema\":2,\"codec\":\"ZSTD\",\"files\":[" + string.Join(",", new[] { body, hair }.Select(v =>
            "{\"codec\":\"" + v.codec + "\",\"path\":\"" + v.path + "\",\"hash\":\"" + v.hash + "\",\"outputHash\":\"" + v.outputHash
            + "\",\"bytes\":" + v.bytes + ",\"decodedBytes\":" + v.decodedBytes + "}")) + "]}";
        MCBVersionDelivery.ApplyManifest(version, json);
        Assert.That(version.versionFiles[0].compression, Is.EqualTo("LZ4"));
        Assert.That(version.versionFiles[1].compression, Is.EqualTo("ZSTD"));
        Assert.Throws<InvalidDataException>(() => MCBVersionDelivery.ApplyManifest(version, json.Replace(body.hash, hair.hash)));
    }
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
