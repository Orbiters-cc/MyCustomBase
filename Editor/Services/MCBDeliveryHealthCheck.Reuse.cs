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
    static void RunReuseCheck()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-reuse-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string key = Path.Combine(folder, "base.fbx"); File.WriteAllBytes(key, new byte[] { 1, 3, 7, 17 });
        var body = MeshData(1); var hair = MeshData(2); var changed = MeshData(3);
        var author = Rig("ReuseAuthor", body); var target = Rig("ReuseTarget", body);
        var generated = new HashSet<string>();
        try {
            foreach (var root in new[] { author, target }) {
                var extra = Object.Instantiate(root.transform.Find("Body").gameObject, root.transform);
                extra.name = "Hair"; extra.GetComponent<SkinnedMeshRenderer>().sharedMesh = hair;
            }
            var paths = new[] { "Body", "Hair" }.Select(p => new ModelFileSmrPathData {
                avatarPath = p, fbxMeshPath = p, rendererName = p, meshName = p }).ToList();
            var manager = new FileManagerService();
            var a = NativeMeshPayloadService.WriteEncryptedPayload(key, author, paths, manager, Path.Combine(folder, "a.bin"), sourcePoseRoot: target.transform, createDeliveryVariants: true);
            author.transform.Find("Hair").GetComponent<SkinnedMeshRenderer>().sharedMesh = changed;
            var b = NativeMeshPayloadService.WriteEncryptedPayload(key, author, paths, manager, Path.Combine(folder, "b.bin"), sourcePoseRoot: target.transform, createDeliveryVariants: true);
            if (a.parts.Count != 2 || a.rendererCount != 2 || a.parts[0].contentHash != b.parts[0].contentHash
                || a.parts[1].contentHash == b.parts[1].contentHash) throw new Exception("Renderer identities do not isolate changed Hair from unchanged Body.");
            Mesh retainedBody = null;
            foreach (var build in new[] { a, b }) {
                foreach (string codec in new[] { MCBCompression.Zstd, MCBCompression.Lz4 }) {
                    if (!MCBCompression.IsSupported(codec)) continue;
                    var patches = build.parts.Select(part => {
                        var v = part.variants.Single(x => x.codec == codec);
                        return new ModelFileData { path = v.path, hash = v.hash, outputHash = v.outputHash,
                            transform = NativeMeshPayloadService.TransformName, role = "PATCH", compression = codec,
                            metadata = new Dictionary<string, object> { { "contentHash", part.contentHash },
                                { NativeMeshPayloadService.PayloadCompressionMetadataKey, codec } } };
                    }).ToArray();
                    var version = new CustomBaseVersion { assetId = 999997, version = build == a ? "reuse-a" : "reuse-b", versionFiles = patches };
                    foreach (var patch in patches) {
                        var payload = NativeMeshPayloadService.ApplyEncryptedPayload(target.transform, version, patch, Path.Combine(folder, patch.path), key, manager);
                        generated.Add(AssetDatabase.GetAssetPath(payload));
                    }
                    var current = target.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh;
                    if (retainedBody != null && current != retainedBody) throw new Exception("Identical Body was rebuilt across versions or codecs.");
                    retainedBody = current;
                    AssertMesh(body, current);
                    AssertMesh(build == a ? hair : changed, target.transform.Find("Hair").GetComponent<SkinnedMeshRenderer>().sharedMesh);
                    NativeMeshPayloadService.DeleteGeneratedPayloadsForVersion(version);
                    if (retainedBody == null || !File.Exists(AssetDatabase.GetAssetPath(retainedBody))) throw new Exception("Deleting a version removed a shared mesh.");
                }
            }
            if (generated.Count != 3) throw new Exception("Expected exactly three unique renderer cache assets.");
        } finally {
            Object.DestroyImmediate(author); Object.DestroyImmediate(target);
            foreach (string path in generated) AssetDatabase.DeleteAsset(path);
            foreach (var mesh in new[] { body, hair, changed }) Object.DestroyImmediate(mesh);
            Directory.Delete(folder, true);
        }
    }
}
#endif
