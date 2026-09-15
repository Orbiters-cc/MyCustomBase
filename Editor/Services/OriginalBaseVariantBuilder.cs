#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Re-encrypt an immutable custom version against another mapped set of original FBX files.</summary>
public static class OriginalBaseVariantBuilder
{
    static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
    public static OriginalBaseVersionData Build(string folder, CustomBaseVersion version, OriginalBaseVersionData target, Func<ModelFileData, string> resolveKey = null)
    {
        if (target.sourceFiles == null || version.sourceFiles.Any(source => !target.sourceFiles.Any(targetSource => string.Equals(targetSource.path, source.path, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Each supported original needs a mapping for every target FBX.");
        resolveKey = resolveKey ?? OriginalBaseLibrary.Resolve;
        var result = Copy(target);
        result.key = OriginalBaseLibrary.Key(target.sourceFiles);
        var patches = new List<ModelFileData>();
        foreach (var originalPatch in version.versionFiles)
        {
            var oldSource = originalPatch.sourceModelFileId > 0
                ? version.sourceFiles.SingleOrDefault(f => f.id == originalPatch.sourceModelFileId)
                : version.sourceFiles.SingleOrDefault(f => f.path == Convert.ToString(originalPatch.metadata?["sourcePath"]) &&
                    (originalPatch.metadata == null || !originalPatch.metadata.ContainsKey("sourceHash") || f.hash == Convert.ToString(originalPatch.metadata["sourceHash"])));
            if (oldSource == null) throw new InvalidDataException("The custom version has an ambiguous original-source binding.");
            var newSource = target.sourceFiles.SingleOrDefault(f => string.Equals(f.path, oldSource.path, StringComparison.OrdinalIgnoreCase));
            if (newSource == null) throw new InvalidDataException("Map the original FBX for " + oldSource.path);
            string oldKeyPath = resolveKey(oldSource), newKeyPath = resolveKey(newSource);
            byte[] oldKey = File.ReadAllBytes(oldKeyPath), newKey = File.ReadAllBytes(newKeyPath);
            var patch = Copy(originalPatch);
            patch.id = 0; patch.sourceModelFileId = newSource.id > 0 ? newSource.id : (int?)null;
            patch.metadata = patch.metadata ?? new Dictionary<string, object>();
            patch.metadata["sourcePath"] = newSource.path; patch.metadata["sourceHash"] = newSource.hash;
            patch.metadata["sourceVersionKey"] = result.key;
            var codecs = MCBVersionDelivery.GetVariants(originalPatch);
            string prefix = "original-" + result.key + "-";
            if (codecs.Length > 0)
            {
                // Archive delivery stores the chosen codec under the primary payload path.
                // Mesh delivery and local builds retain each codec's own path.
                string CodecInput(MCBPayloadVariant codec) => new[] { codec.path, originalPatch.path }
                    .Select(path => Path.Combine(folder, path)).FirstOrDefault(path => File.Exists(path) && MCBUtils.CalculateFileHash(path) == codec.hash);
                codecs = codecs.Where(c => CodecInput(c) != null).ToArray();
                if (codecs.Length == 0) throw new InvalidDataException("No verified historical mesh codec is available.");
                foreach (var codec in codecs)
                {
                    string sourcePath = CodecInput(codec);
                    if (MCBUtils.CalculateFileHash(sourcePath) != codec.hash) throw new InvalidDataException("Historical mesh payload failed its hash check.");
                    byte[] encoded = MCBXor.Transform(oldKey, File.ReadAllBytes(sourcePath));
                    if (Hash(encoded) != codec.outputHash) throw new InvalidDataException("Original FBX cannot decrypt this mesh payload.");
                    codec.path = prefix + PayloadName(codec.path);
                    Write(folder, codec.path, MCBXor.Transform(newKey, encoded));
                    codec.hash = MCBUtils.CalculateFileHash(Path.Combine(folder, codec.path));
                }
                var primary = codecs.FirstOrDefault(v => v.codec == patch.compression) ?? codecs[0];
                patch.path = primary.path; patch.hash = primary.hash; patch.outputHash = primary.outputHash; patch.compression = primary.codec;
                patch.metadata[NativeMeshPayloadService.PayloadCompressionMetadataKey] = primary.codec;
                patch.metadata["deliveryVariants"] = JArray.FromObject(codecs);
            }
            else
            {
                string input = Path.Combine(folder, originalPatch.path);
                if (MCBUtils.CalculateFileHash(input) != originalPatch.hash) throw new InvalidDataException("Historical payload failed its hash check.");
                byte[] output;
                if (originalPatch.transform == ModelFileTransforms.DirectAsset) output = File.ReadAllBytes(input);
                else
                {
                    byte[] decoded;
                    if (originalPatch.transform == ModelFileTransforms.HdiffXorBinToFbx)
                    {
                        string temp = Path.Combine(Path.GetTempPath(), "mcb-rekey-" + Guid.NewGuid().ToString("N") + ".fbx");
                        try { HdiffService.ApplyXorEncryptedPatchToTempFbx(oldKeyPath, input, temp, new FileManagerService()); decoded = File.ReadAllBytes(temp); }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                        patch.transform = ModelFileTransforms.XorBinToFbx; patch.compression = null;
                    }
                    else decoded = MCBXor.Transform(oldKey, File.ReadAllBytes(input));
                    if (!string.IsNullOrEmpty(patch.outputHash) && Hash(decoded) != patch.outputHash) throw new InvalidDataException("Original FBX cannot reconstruct the historical custom version.");
                    output = MCBXor.Transform(newKey, decoded);
                }
                patch.path = prefix + PayloadName(originalPatch.path);
                Write(folder, patch.path, output); patch.hash = Hash(output);
            }
            patches.Add(patch);
        }
        result.versionFiles = patches.ToArray(); return result;
    }
    public static void AddToBuild(string folder, CustomBaseVersion metadata, OriginalBaseVersionData[] targets)
    {
        if (targets == null || targets.Length == 0) throw new InvalidOperationException("Select at least one supported original base version.");
        metadata.originalBaseVersions = targets.Select(target => Build(folder, metadata, target)).ToArray();
    }
    static string PayloadName(string path) => System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(path), "^(original-[a-f0-9]{64}-)+", "");
    static void Write(string folder, string relative, byte[] data)
    {
        string path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, data);
    }
    static string Hash(byte[] bytes) { using (var sha = MCBHashing.CreateSha256()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
}
#endif
