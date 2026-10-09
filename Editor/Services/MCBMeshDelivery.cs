#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;

/// <summary>Authorized manifest delivery with independently verified, reusable renderer blobs.</summary>
[InitializeOnLoad]
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
    const int ManifestLimit = 128 * 1024;
    static MCBMeshDelivery() { EditorApplication.update += PruneWhenIdle; }
    static bool Hash(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{64}$");
    static string BlobPath(MCBPayloadVariant v) => Hash(v.hash) ? Path.Combine(CacheRoot, v.hash + ".bin") : throw new InvalidDataException("Invalid mesh blob hash.");

    internal static void Validate(Manifest manifest, CustomBaseVersion version)
    {
        var patches = (version.versionFiles ?? Array.Empty<ModelFileData>()).Where(p => NativeMeshPayloadService.IsAdvancedMeshPatchTransform(p?.transform)).ToArray();
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
        if (!File.Exists(path) || new FileInfo(path).Length != v.bytes || MCBUtils.CalculateFileHash(path) != v.hash) return false;
        Touch(path);
        return true;
    }

    // The write time is the blob's last use: pruning keeps recently used blobs.
    static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
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
                // Another editor may store or read the same blob meanwhile: its verified copy is as good as this one.
                try { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
                catch (IOException) when (HasVerifiedBlob(variant)) { }
                catch (UnauthorizedAccessException) when (HasVerifiedBlob(variant)) { }
                Touch(path);
            } finally {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }

    static bool TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
    }

    // Wall time while at least one transfer runs: the speed sample leaves out hashing, caching and the zip assembly.
    sealed class TransferClock
    {
        readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        int running;
        public double Milliseconds { get { lock (watch) return watch.Elapsed.TotalMilliseconds; } }
        public async Task<T> Time<T>(Func<Task<T>> transfer)
        {
            lock (watch) if (running++ == 0) watch.Start();
            try { return await transfer(); }
            finally { lock (watch) if (--running == 0) watch.Stop(); }
        }
    }

    public static async Task<(bool success, string error)> DownloadAsync(NetworkService network, string url,
        CustomBaseVersion version, string destination, Action<float> progress, Action<ulong> transferred, bool recordMeasurements = true,
        string authToken = null)
    {
        string staging = Path.Combine(Path.GetTempPath(), "mcb-mesh-delivery-" + Guid.NewGuid().ToString("N"));
        MCBDeliveryDecision choice = null;
        var clock = new TransferClock();
        long[] received = new long[1];
        bool succeeded = false;
        MCBDownloadTempFiles.Track(destination);
        try
        {
            Directory.CreateDirectory(staging);
            var reply = await network.DownloadBytesAsync(url + "&meshManifest=1", authToken: authToken, maxBytes: ManifestLimit);
            if (!reply.success) throw new IOException(reply.error);
            if (reply.data.Length > ManifestLimit) throw new InvalidDataException("Mesh manifest is too large.");
            var manifest = JsonConvert.DeserializeObject<Manifest>(System.Text.Encoding.UTF8.GetString(reply.data));
            Validate(manifest, version);
            var cached = await Task.Run(() => manifest.files.Select(p => p.variants.FirstOrDefault(v => MCBCompression.IsSupported(v.codec) && HasVerifiedBlob(v))).ToArray());
            var options = new[] { MCBCompression.Zstd, MCBCompression.Lz4 }.Where(c => manifest.files.All(p => p.variants.Any(v => v.codec == c)))
                .Select(c => new MCBDeliveryVariant { codec = c, packageBytes = manifest.commonBytes + manifest.files.Select((p, i) => cached[i] == null ? p.variants.Single(v => v.codec == c).bytes : 0).Sum(),
                    decodedBytes = manifest.files.Select((p, i) => cached[i] == null ? p.variants.Single(v => v.codec == c).decodedBytes : 0).Sum() }).ToArray();
            choice = MCBPerformance.Choose(options);
            if (choice?.codec == null)
                throw new NotSupportedException("This version's meshes need a codec this editor cannot load (" +
                    string.Join(", ", manifest.files.SelectMany(p => p.variants).Select(v => v.codec).Distinct()) +
                    "). Reinstall or update My Custom Base, or apply the version from another computer.");
            var selected = manifest.files.Select((p, i) => cached[i] ?? p.variants.Single(v => v.codec == choice.codec)).ToArray();
            var metrics = new Metrics { codec = choice.codec, reusedMeshes = cached.Count(v => v != null), downloadedMeshes = cached.Count(v => v == null),
                reusedBytes = cached.Where(v => v != null).Sum(v => v.bytes) };
            LastDownload = metrics;
            long total = manifest.commonBytes + selected.Where((v, i) => cached[i] == null).Sum(v => v.bytes);
            // Bytes per transfer (the common package, then each mesh): the bar moves while each one downloads.
            received = new long[selected.Length + 1];
            Action<int, ulong> report = (part, bytes) => {
                long current;
                lock (received) { received[part] = (long)bytes; current = received.Sum(); }
                transferred?.Invoke((ulong)current);
                progress?.Invoke((float)Math.Min(1d, (double)current / Math.Max(1, total)));
            };
            string commonPath = Path.Combine(staging, "common.zip");
            var common = await clock.Time(() => network.DownloadFileAsync(url + "&meshCommon=1", commonPath, onDownloadedBytes: bytes => report(0, bytes), authToken: authToken));
            if (!common.success) throw new IOException(common.error);
            bool commonValid = await Task.Run(() => new FileInfo(commonPath).Length == manifest.commonBytes && MCBUtils.CalculateFileHash(commonPath) == manifest.commonHash);
            if (!commonValid) throw new InvalidDataException("Common version package failed integrity verification.");
            report(0, (ulong)manifest.commonBytes);
            Directory.CreateDirectory(CacheRoot);
            using (var limit = new SemaphoreSlim(3))
                await Task.WhenAll(selected.Select(async (v, i) => {
                    if (cached[i] != null) return;
                    await limit.WaitAsync();
                    try {
                        string temp = Path.Combine(staging, i + ".bin");
                        var blob = await clock.Time(() => network.DownloadFileAsync(url + "&meshBlob=" + v.hash, temp, onDownloadedBytes: bytes => report(i + 1, bytes), authToken: authToken));
                        if (!blob.success) throw new IOException(blob.error);
                        await Task.Run(() => StoreVerifiedBlob(v, temp));
                        report(i + 1, (ulong)v.bytes);
                    } finally { limit.Release(); }
                }));
            lock (received) metrics.downloadedBytes = received.Sum();
            await Task.Run(() => {
                using (var stream = File.Create(destination)) {
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
                        var budget = new VersionArchiveBudget();
                        using (var commonZip = ZipFile.OpenRead(commonPath)) {
                            budget.AddEntries(commonZip.Entries.Count);
                            foreach (var entry in commonZip.Entries) {
                                if (entry.FullName == MCBVersionDelivery.ManifestName || manifest.files.Any(p => p.path == entry.FullName))
                                    throw new InvalidDataException("Repeated mesh path in common package.");
                                using (var output = zip.CreateEntry(entry.FullName).Open())
                                using (var input = entry.Open()) budget.Copy(input, output, entry.FullName);
                            }
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
            if (recordMeasurements && metrics.downloadedMeshes > 0) MCBPerformance.RecordDownload(choice, metrics.downloadedBytes, clock.Milliseconds, true);
            MCBLogger.Log($"[MCBMeshReuse] Downloaded {metrics.downloadedMeshes} meshes, reused {metrics.reusedMeshes}; received {metrics.downloadedBytes} bytes, avoided {metrics.reusedBytes} bytes.");
            succeeded = true;
            return (true, null);
        }
        catch (Exception ex) {
            if (recordMeasurements && choice?.candidates?.Any(c => c.decodedBytes > 0) == true) {
                long bytes;
                lock (received) bytes = received.Sum();
                MCBPerformance.RecordDownload(choice, bytes, clock.Milliseconds, false);
            }
            TryDeleteFile(destination);
            return (false, ex.GetBaseException().Message);
        }
        finally
        {
            // Cleanup never turns the result into a failure: what is still in use goes before the next reload, or with the sweep.
            if (!MCBDownloadTempFiles.TryDelete(staging)) MCBDownloadTempFiles.Track(staging);
            MCBDownloadTempFiles.Finish(destination, succeeded || File.Exists(destination));
        }
    }

    // --- Blob cache pruning. The cache is shared by every project of this user: blobs this project's local versions use
    // stay; the others go once unused for MaxBlobAge, and least recently used first while the cache is over its cap. A blob
    // used in the last BlobGrace stays whatever its size: another editor may be assembling a download from it. ---

    internal const long BlobCacheCapBytes = 4L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan MaxBlobAge = TimeSpan.FromDays(60), BlobGrace = TimeSpan.FromDays(1);
    const string PrunedSessionKey = "MCB.MeshBlobs.Pruned";

    internal struct CachedBlob
    {
        public string hash, path;
        public long bytes;
        public DateTime lastUsedUtc;
    }

    // Once per editor session, a minute after startup, while nothing is compiling, importing, building or entering play mode.
    static void PruneWhenIdle()
    {
        if (EditorApplication.timeSinceStartup < 60) return;
        if (UnityEngine.Application.isBatchMode || SessionState.GetBool(PrunedSessionKey, false)) { EditorApplication.update -= PruneWhenIdle; return; }
        if (EditorApplication.isPlayingOrWillChangePlaymode || MCBEditor.ShouldDeferBackgroundNetworkRefresh()) return;
        EditorApplication.update -= PruneWhenIdle;
        SessionState.SetBool(PrunedSessionKey, true);
        string versionsRoot = Path.GetFullPath(MCBUtils.ASSET_VERSIONS_FOLDER);
        Task.Run(() => {
            try {
                long freed = Prune(versionsRoot, DateTime.UtcNow);
                if (freed > 0) MCBLogger.Log($"[MCBMeshReuse] Pruned {freed} bytes of unused mesh blobs.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                MCBLogger.LogWarning($"[MCBMeshReuse] Could not prune the mesh blob cache: {ex.Message}");
            }
        });
    }

    /// <summary>Prunes the blob cache; returns the bytes freed.</summary>
    internal static long Prune(string versionsRoot, DateTime nowUtc)
    {
        if (!Directory.Exists(CacheRoot)) return 0;
        var files = new DirectoryInfo(CacheRoot).GetFiles();
        // Copies left by interrupted stores.
        foreach (var file in files.Where(f => f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && nowUtc - f.LastWriteTimeUtc > BlobGrace))
            TryDeleteFile(file.FullName);
        var blobs = files.Where(f => f.Extension == ".bin" && Hash(Path.GetFileNameWithoutExtension(f.Name)))
            .Select(f => new CachedBlob { hash = Path.GetFileNameWithoutExtension(f.Name), path = f.FullName, bytes = f.Length, lastUsedUtc = f.LastWriteTimeUtc });
        long freed = 0;
        foreach (var blob in SelectPrunable(blobs, UsedBlobHashes(versionsRoot), nowUtc, BlobCacheCapBytes, MaxBlobAge, BlobGrace))
            lock (CacheLock)
                if (TryDeleteFile(blob.path)) freed += blob.bytes;
        return freed;
    }

    /// <summary>The blobs to delete, least recently used first.</summary>
    internal static List<CachedBlob> SelectPrunable(IEnumerable<CachedBlob> blobs, ICollection<string> used, DateTime nowUtc,
        long capBytes, TimeSpan maxAge, TimeSpan grace)
    {
        var all = blobs.ToList();
        long total = all.Sum(b => b.bytes);
        var prune = new List<CachedBlob>();
        foreach (var blob in all.Where(b => !used.Contains(b.hash) && nowUtc - b.lastUsedUtc > grace).OrderBy(b => b.lastUsedUtc))
        {
            // Oldest first: once under the cap, only blobs past the age limit remain to delete.
            if (total <= capBytes && nowUtc - blob.lastUsedUtc <= maxAge) break;
            prune.Add(blob);
            total -= blob.bytes;
        }
        return prune;
    }

    /// <summary>Every mesh blob hash (each codec) the local versions under <paramref name="versionsRoot"/> describe.</summary>
    internal static HashSet<string> UsedBlobHashes(string versionsRoot)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(versionsRoot)) return used;
        foreach (string json in Directory.EnumerateFiles(versionsRoot, "version.json", SearchOption.AllDirectories))
        {
            try
            {
                var version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(json));
                var files = (version?.versionFiles ?? Array.Empty<ModelFileData>())
                    .Concat((version?.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>()).SelectMany(o => o?.versionFiles ?? Array.Empty<ModelFileData>()));
                foreach (var file in files.Where(f => f != null))
                {
                    if (Hash(file.hash)) used.Add(file.hash);
                    foreach (var variant in MCBVersionDelivery.GetVariants(file)) if (Hash(variant?.hash)) used.Add(variant.hash);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { }
        }
        return used;
    }
}
#endif
