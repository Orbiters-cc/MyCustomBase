#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

/// <summary>Partitions immutable outputs by original source; shared dependencies accompany each package.</summary>
public static class VersionPublishPlan
{
    public static VersionArtifact[] Create(VersionArtifact artifact)
    {
        if (artifact?.Manifest?.outputs == null) throw new InvalidDataException("The local build manifest is missing. Rebuild this version.");
        var originals = artifact.Metadata.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>();
        if (originals.Length == 0) { CheckSize(artifact); return new[] { artifact }; }
        var allPayloads = new HashSet<string>(originals.SelectMany(v => Payloads(v.versionFiles))
            .Concat(Payloads(artifact.Metadata.versionFiles)), StringComparer.Ordinal);
        var result = new List<VersionArtifact>();
        foreach (var source in originals.OrderByDescending(v => v.key == artifact.Metadata.sourceVersionKey))
        {
            var wanted = new HashSet<string>(Payloads(source.versionFiles), StringComparer.Ordinal);
            var metadata = JsonConvert.DeserializeObject<CustomBaseVersion>(JsonConvert.SerializeObject(artifact.Metadata));
            var snapshot = metadata.originalBaseVersions.Single(v => v.key == source.key);
            metadata.sourceVersionKey = snapshot.key;
            metadata.sourceFiles = snapshot.sourceFiles;
            metadata.versionFiles = snapshot.versionFiles;
            metadata.baseFbxHash = snapshot.sourceFiles[0].hash;
            metadata.defaultAviHash = snapshot.sourceFiles.Select(f => f.hash).ToArray();
            metadata.originalBaseVersions = new[] { snapshot };
            metadata.deliveryVariants = snapshot.deliveryVariants;
            metadata.meshDelivery = snapshot.meshDelivery;
            var manifest = JsonConvert.DeserializeObject<VersionManifest>(JsonConvert.SerializeObject(artifact.Manifest));
            manifest.outputs = manifest.outputs.Where(f => {
                var path = f.path.EndsWith(".meta", StringComparison.Ordinal) ? f.path.Substring(0, f.path.Length - 5) : f.path;
                return !allPayloads.Contains(path) || wanted.Contains(path);
            }).ToList();
            if (wanted.Any(path => !manifest.outputs.Any(f => f.path == path)))
                throw new InvalidDataException("A source variant references a file absent from the build manifest: " + source.label);
            var part = new VersionArtifact(artifact.FolderUnityPath, metadata, manifest);
            CheckSize(part); result.Add(part);
        }
        return result.ToArray();
    }

    private static IEnumerable<string> Payloads(ModelFileData[] files)
    {
        foreach (var file in files ?? Array.Empty<ModelFileData>())
        {
            if (!string.IsNullOrEmpty(file.path)) yield return file.path;
            foreach (var codec in MCBVersionDelivery.GetVariants(file))
                if (!string.IsNullOrEmpty(codec.path)) yield return codec.path;
        }
    }

    private static void CheckSize(VersionArtifact artifact)
    {
        // Native payloads are already compressed and ZIP stores them without recompression.
        long storedBytes = artifact.Manifest.outputs.Where(f => f.path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).Sum(f => f.bytes);
        if (storedBytes > VersionPublisher.MaxVersionPackageUploadBytes)
            throw new InvalidOperationException("Version package is too large to upload for source " + artifact.Metadata.sourceVersionKey +
                ". Its compressed mesh payloads exceed 600 MB. Rebuild with smaller meshes or fewer payload variants.");
    }
}
#endif
