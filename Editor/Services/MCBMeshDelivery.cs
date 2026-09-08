#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

/// <summary>Authorized manifest delivery with independently verified, reusable renderer blobs.</summary>
[UnityEditor.InitializeOnLoad]
public static class MCBMeshDelivery
{
    internal sealed class Manifest
    {
        public int schema;
        public long commonBytes;
        public string commonHash;
        public Part[] files;
    }
    internal sealed class Part { public string path; public string contentHash; public MCBPayloadVariant[] variants; }
    public sealed class Metrics
    {
        public int reusedMeshes, downloadedMeshes;
        public long downloadedBytes, reusedBytes;
        public string codec;
    }
    public static Metrics LastDownload { get; private set; }
    static readonly string CacheRoot = Path.Combine(MCBUtils.GetMCBDataFolder(), "mesh-blobs-v1");
    static readonly object CacheLock = new object();
    static MCBMeshDelivery() { }
    static bool Hash(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{64}$");
    static string BlobPath(MCBPayloadVariant v) => Hash(v.hash) ? Path.Combine(CacheRoot, v.hash + ".bin") : throw new InvalidDataException("Invalid mesh blob hash.");

    internal static void Validate(Manifest manifest, CustomBaseVersion version)
    {
        var patches = (version.versionFiles ?? Array.Empty<ModelFileData>()).Where(p => p?.transform == NativeMeshPayloadService.TransformName).ToArray();
        if (manifest?.schema != 1 || manifest.files == null || manifest.files.Length != patches.Length || !Hash(manifest.commonHash)
            || manifest.commonBytes <= 0 || manifest.commonBytes > 1024L * 1024 * 1024) throw new InvalidDataException("Invalid mesh delivery manifest.");
        foreach (var patch in patches)
        {
            var matches = manifest.files.Where(p => p?.path == patch.path).ToArray();
            if (matches.Length != 1 || !Hash(matches[0].contentHash) || NativeMeshPayloadService.GetPayloadIdentity(patch) != "raw:" + matches[0].contentHash)
                throw new InvalidDataException("Mesh manifest does not match the authorized version.");
            var expected = MCBVersionDelivery.GetVariants(patch);
            var actual = matches[0].variants;
            if (actual == null || actual.Length == 0 || actual.Length != expected.Length || actual.Select(v => v?.codec).Distinct().Count() != actual.Length)
                throw new InvalidDataException("Invalid mesh codec list.");
            foreach (var v in actual)
                if (v == null || !Hash(v.hash) || !Hash(v.outputHash) || v.bytes <= 0 || v.bytes > 1024L * 1024 * 1024
                    || !expected.Any(e => e.codec == v.codec && e.hash == v.hash && e.outputHash == v.outputHash && e.bytes == v.bytes && e.decodedBytes == v.decodedBytes))
                    throw new InvalidDataException("Mesh variant differs from its authorized descriptor.");
        }
    }

    static bool HasVerifiedBlob(MCBPayloadVariant v)
    {
        string path = BlobPath(v);
        return File.Exists(path) && new FileInfo(path).Length == v.bytes && MCBUtils.CalculateFileHash(path) == v.hash;
    }

    internal static void StoreVerifiedBlob(MCBPayloadVariant variant, string source)
    {
        if (new FileInfo(source).Length != variant.bytes || MCBUtils.CalculateFileHash(source) != variant.hash)
            throw new InvalidDataException("Mesh blob failed integrity verification.");
        lock (CacheLock) {
            if (HasVerifiedBlob(variant)) return;
            Directory.CreateDirectory(CacheRoot);
            string path = BlobPath(variant), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.Copy(source, temp);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public static async Task<(bool success, string error)> DownloadAsync(NetworkService network, string url,
        CustomBaseVersion version, string destination, Action<float> progress, Action<ulong> transferred, bool recordMeasurements = true)
    {
        string staging = Path.Combine(Path.GetTempPath(), "mcb-mesh-delivery-" + Guid.NewGuid().ToString("N"));
        MCBDeliveryDecision choice = null;
        long received = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(staging);
            var reply = await network.DownloadBytesAsync(url + "&meshManifest=1");
            if (!reply.success) throw new IOException(reply.error);
            if (reply.data.Length > 128 * 1024) throw new InvalidDataException("Mesh manifest is too large.");
            var manifest = JsonConvert.DeserializeObject<Manifest>(System.Text.Encoding.UTF8.GetString(reply.data));
            Validate(manifest, version);
            var cached = await Task.Run(() => manifest.files.Select(p => p.variants.FirstOrDefault(v => MCBCompression.IsSupported(v.codec) && HasVerifiedBlob(v))).ToArray());
            var options = new[] { MCBCompression.Zstd, MCBCompression.Lz4 }.Where(c => manifest.files.All(p => p.variants.Any(v => v.codec == c)))
                .Select(c => new MCBDeliveryVariant { codec = c, packageBytes = manifest.commonBytes + manifest.files.Select((p, i) => cached[i] == null ? p.variants.Single(v => v.codec == c).bytes : 0).Sum(),
                    decodedBytes = manifest.files.Select((p, i) => cached[i] == null ? p.variants.Single(v => v.codec == c).decodedBytes : 0).Sum() }).ToArray();
            choice = MCBPerformance.Choose(options);
            var selected = manifest.files.Select((p, i) => cached[i] ?? p.variants.Single(v => v.codec == choice.codec)).ToArray();
            var metrics = new Metrics { codec = choice.codec, reusedMeshes = cached.Count(v => v != null), downloadedMeshes = cached.Count(v => v == null),
                reusedBytes = cached.Where(v => v != null).Sum(v => v.bytes) };
            LastDownload = metrics;
            long total = manifest.commonBytes + selected.Where((v, i) => cached[i] == null).Sum(v => v.bytes);
            Action<long> report = bytes => { long current = Interlocked.Add(ref received, bytes); transferred?.Invoke((ulong)current); progress?.Invoke((float)current / Math.Max(1, total)); };
            string commonPath = Path.Combine(staging, "common.zip");
            var common = await network.DownloadFileAsync(url + "&meshCommon=1", commonPath);
            if (!common.success) throw new IOException(common.error);
            if (new FileInfo(commonPath).Length != manifest.commonBytes || MCBUtils.CalculateFileHash(commonPath) != manifest.commonHash)
                throw new InvalidDataException("Common version package failed integrity verification.");
            report(manifest.commonBytes);
            Directory.CreateDirectory(CacheRoot);
            using (var limit = new SemaphoreSlim(3))
                await Task.WhenAll(selected.Select(async (v, i) => {
                    if (cached[i] != null) return;
                    await limit.WaitAsync();
                    try {
                        string temp = Path.Combine(staging, i + ".bin");
                        var blob = await network.DownloadFileAsync(url + "&meshBlob=" + v.hash, temp);
                        if (!blob.success) throw new IOException(blob.error);
                        await Task.Run(() => StoreVerifiedBlob(v, temp));
                        report(v.bytes);
                    } finally { limit.Release(); }
                }));
            metrics.downloadedBytes = received;
            await Task.Run(() => {
                using (var stream = File.Create(destination)) {
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
                        using (var commonZip = ZipFile.OpenRead(commonPath))
                            foreach (var entry in commonZip.Entries) {
                                if (entry.FullName == MCBVersionDelivery.ManifestName || manifest.files.Any(p => p.path == entry.FullName))
                                    throw new InvalidDataException("Repeated mesh path in common package.");
                                using (var output = zip.CreateEntry(entry.FullName).Open())
                                using (var input = entry.Open()) input.CopyTo(output);
                            }
                        for (int i = 0; i < selected.Length; i++) {
                            string name = manifest.files[i].path;
                            if (Path.GetFileName(name) != name || name.Contains(":")) throw new InvalidDataException("Unsafe mesh path.");
                            using (var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open())
                            using (var input = File.OpenRead(BlobPath(selected[i]))) input.CopyTo(output);
                        }
                        var files = selected.Select((v, i) => new MCBPayloadVariant { path = manifest.files[i].path, codec = v.codec,
                            hash = v.hash, outputHash = v.outputHash, bytes = v.bytes, decodedBytes = v.decodedBytes });
                        using (var writer = new StreamWriter(zip.CreateEntry(MCBVersionDelivery.ManifestName).Open()))
                            writer.Write(JsonConvert.SerializeObject(new { schema = 2, codec = choice.codec, files }));
                    }
                }
            });
            if (recordMeasurements && metrics.downloadedMeshes > 0) MCBPerformance.RecordDownload(choice, received, started.Elapsed.TotalMilliseconds, true);
            MCBLogger.Log($"[MCBMeshReuse] Downloaded {metrics.downloadedMeshes} meshes, reused {metrics.reusedMeshes}; received {metrics.downloadedBytes} bytes, avoided {metrics.reusedBytes} bytes.");
            return (true, null);
        }
        catch (Exception ex) {
            if (recordMeasurements && choice?.candidates?.Any(c => c.decodedBytes > 0) == true)
                MCBPerformance.RecordDownload(choice, received, started.Elapsed.TotalMilliseconds, false);
            if (File.Exists(destination)) File.Delete(destination);
            return (false, ex.GetBaseException().Message);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
#endif
