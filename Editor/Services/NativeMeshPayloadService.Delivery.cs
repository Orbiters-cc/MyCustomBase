#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static partial class NativeMeshPayloadService
{
    static void VerifyPayloadHash(byte[] payload, string expected)
    {
        if (payload == null || string.IsNullOrWhiteSpace(expected)) throw new InvalidDataException("Advanced mesh payload hash is missing.");
        using (var sha = MCBHashing.CreateSha256())
            if (!string.Equals(BytesToHex(sha.ComputeHash(payload)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Advanced mesh payload verification failed. Download this version again before applying it.");
    }

    static NativeMeshPayloadBuildResult WriteDeliveryVariants(string sourcePath, GameObject source,
        List<PayloadRendererSource> renderers, byte[] key, string outputPath,
        NativeMeshPayloadBuildMetrics metrics, Transform sourcePoseRoot)
    {
        if (renderers.Count > 1)
        {
            var parts = new List<NativeMeshPayloadBuildResult>();
            for (int i = 0; i < renderers.Count; i++)
            {
                string partPath = i == 0 ? outputPath : Path.Combine(Path.GetDirectoryName(outputPath),
                    Path.GetFileNameWithoutExtension(outputPath) + "_" + i + ".bin");
                parts.Add(WriteDeliveryVariants(sourcePath, source, new List<PayloadRendererSource> { renderers[i] },
                    key, partPath, new NativeMeshPayloadBuildMetrics(), sourcePoseRoot));
            }
            var first = parts[0];
            return new NativeMeshPayloadBuildResult { parts = parts, variants = first.variants,
                contentHash = first.contentHash, payloadHash = first.payloadHash, binHash = first.binHash,
                payloadCompression = first.payloadCompression, rendererCount = parts.Sum(p => p.rendererCount),
                payloadBytes = parts.Sum(p => p.payloadBytes), blendShapeVertexBytes = parts.Sum(p => p.blendShapeVertexBytes),
                blendShapeNormalBytes = parts.Sum(p => p.blendShapeNormalBytes), blendShapeTangentBytes = parts.Sum(p => p.blendShapeTangentBytes),
                skippedBlendShapeNormalBytes = parts.Sum(p => p.skippedBlendShapeNormalBytes),
                skippedBlendShapeTangentBytes = parts.Sum(p => p.skippedBlendShapeTangentBytes) };
        }
        byte[] plain;
        using (var stream = new MemoryStream()) {
            using (var buffered = new BufferedStream(stream, 256 * 1024)) {
                WriteBinaryPayloadContents(sourcePath, source, renderers, buffered, metrics, sourcePoseRoot);
                buffered.Flush();
                plain = stream.ToArray();
            }
        }
        var variants = new List<MCBPayloadVariant>();
        var writtenPaths = new List<string>();
        try {
            foreach (string codec in new[] { MCBCompression.Zstd, MCBCompression.Lz4 }) {
                if (!MCBCompression.IsSupported(codec)) continue;
                byte[] encoded = MCBCompression.Encode(plain, codec);
                string path = variants.Count == 0 ? outputPath : Path.ChangeExtension(outputPath, codec.ToLowerInvariant() + ".bin");
                string outputHash;
                using (var sha = MCBHashing.CreateSha256()) outputHash = BytesToHex(sha.ComputeHash(encoded));
                writtenPaths.Add(path);
                string binHash;
                using (var sha = MCBHashing.CreateSha256())
                using (var output = File.Create(path))
                using (var hashing = new HashingWriteStream(output, sha, true))
                using (var xor = new XorWriteStream(hashing, key, true)) {
                    for (int offset = 0; offset < encoded.Length; offset += 256 * 1024)
                        xor.Write(encoded, offset, Math.Min(256 * 1024, encoded.Length - offset));
                    xor.Flush(); hashing.CompleteHash();
                    binHash = BytesToHex(sha.Hash);
                }
                variants.Add(new MCBPayloadVariant {
                    codec = codec, path = Path.GetFileName(path), hash = binHash,
                    outputHash = outputHash, bytes = encoded.Length, decodedBytes = plain.Length
                });
                MCBMeshDelivery.StoreVerifiedBlob(variants[variants.Count - 1], path);
            }
            if (variants.Count == 0) throw new PlatformNotSupportedException("No MCB mesh codec is available for this Editor platform.");
            var primary = variants[0];
            return new NativeMeshPayloadBuildResult {
                contentHash = HashBytes(plain),
                variants = variants, payloadHash = primary.outputHash, binHash = primary.hash,
                payloadCompression = primary.codec, rendererCount = renderers.Count, payloadBytes = primary.bytes,
                blendShapeVertexBytes = metrics.blendShapeVertexBytes,
                blendShapeNormalBytes = metrics.blendShapeNormalBytes,
                blendShapeTangentBytes = metrics.blendShapeTangentBytes,
                skippedBlendShapeNormalBytes = metrics.skippedBlendShapeNormalBytes,
                skippedBlendShapeTangentBytes = metrics.skippedBlendShapeTangentBytes
            };
        } catch {
            foreach (string path in writtenPaths) if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
}
#endif
