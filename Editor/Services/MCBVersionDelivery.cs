#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Reconcile downloaded codec metadata without changing source identity or rig mappings.</summary>
public static class MCBVersionDelivery
{
    public const string ManifestName = "mcb-delivery.json";
    sealed class Manifest { public int schema; public string codec; public MCBPayloadVariant[] files; }

    public static void ApplyLocalDelivery(CustomBaseVersion version)
    {
        if (version == null) return;
        string path = Path.Combine(Path.GetFullPath(MCBUtils.GetVersionDataPath(version)), ManifestName);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Delivery manifest is too large.");
        ApplyManifest(version, File.ReadAllText(path));
    }

    public static void ApplyManifest(CustomBaseVersion version, string json)
    {
        if (version == null || json == null || json.Length > 128 * 1024) throw new InvalidDataException("Invalid delivery manifest.");
        var manifest = JsonConvert.DeserializeObject<Manifest>(json);
        if (manifest == null || (manifest.schema != 1 && manifest.schema != 2) || manifest.files == null
            || (manifest.codec != MCBCompression.Lz4 && manifest.codec != MCBCompression.Zstd)) throw new InvalidDataException("Invalid delivery manifest.");
        var patches = (version.versionFiles ?? Array.Empty<ModelFileData>()).Where(p => p != null
            && p.transform == NativeMeshPayloadService.TransformName).ToArray();
        if (patches.Length != manifest.files.Length) throw new InvalidDataException("Delivery manifest does not match this version.");
        var changes = new List<(ModelFileData patch, MCBPayloadVariant variant)>();
        foreach (var patch in patches) {
            var matches = manifest.files.Where(v => v != null && v.path == patch.path).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Delivery manifest has a missing or repeated mesh.");
            var selected = matches[0];
            var expected = GetVariants(patch).SingleOrDefault(v => v.codec == (manifest.schema == 2 ? selected.codec : manifest.codec));
            if (expected == null || expected.hash != selected.hash || expected.outputHash != selected.outputHash
                || expected.bytes != selected.bytes || expected.decodedBytes != selected.decodedBytes || (manifest.schema == 1 && selected.codec != manifest.codec))
                throw new InvalidDataException("Delivery manifest does not match the authorized mesh variant.");
            changes.Add((patch, expected));
        }
        foreach (var change in changes) {
            change.patch.hash = change.variant.hash;
            change.patch.outputHash = change.variant.outputHash;
            change.patch.compression = change.variant.codec;
            change.patch.metadata[NativeMeshPayloadService.PayloadCompressionMetadataKey] = change.variant.codec;
        }
    }

    public static MCBPayloadVariant[] GetVariants(ModelFileData patch)
    {
        if (patch?.metadata == null || !patch.metadata.TryGetValue("deliveryVariants", out object value) || value == null)
            return Array.Empty<MCBPayloadVariant>();
        return JToken.FromObject(value).ToObject<MCBPayloadVariant[]>() ?? Array.Empty<MCBPayloadVariant>();
    }
}
#endif
