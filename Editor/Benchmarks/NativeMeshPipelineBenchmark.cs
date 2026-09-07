#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Explicit, local experiments against a real payload. Never applies a version to a
/// scene, changes project serialization settings, uploads, or edits source assets.
/// Results are checkpointed under Library/MCB/Benchmarks. Only owned scratch assets
/// under Assets/MCB/generated/pipelineBenchmarks are created and removed.
/// </summary>
public static partial class NativeMeshPipelineBenchmark
{
    const string ScratchRoot = "Assets/MCB/generated/pipelineBenchmarks";
    static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static IEnumerator routine;
    static Report report;
    static string outputDirectory, scratch;
    static NativeMeshPayloadAsset source;
    static Task activeWorker;
    static double lastTick;
    static bool allocationCounterAvailable;
    public static string Status { get; private set; } = "Idle";
    public static string ResultPath { get; private set; }

    [Serializable] public sealed class Measurement
    {
        public string stage;
        public int iteration;
        public double milliseconds;
        public long? allocatedBytesCurrentThread;
        public long bytes;
        public bool verified;
        public string note;
    }
    [Serializable] public sealed class Report
    {
        public string startedUtc, finishedUtc, unity, cpu, gpu, sourceAsset, sourceBin, sourceKey;
        public int logicalCores, memoryMB, rendererCount, vertices, blendshapeFrames;
        public string serializationMode, payloadSha256, error;
        public bool sceneDirtyBefore, sceneDirtyAfter;
        public long sourceAssetBytes, payloadBytes, keyBytes;
        public long? processPeakWorkingSetBytes;
        public double longestEditorTickGapMs;
        public List<Measurement> measurements = new List<Measurement>();
        public List<string> notes = new List<string>();
    }

    public static string Start(string payloadAssetPath, string binPath, string originalKeyPath, int repetitions = 3)
    {
        if (routine != null) throw new InvalidOperationException("A benchmark is already running.");
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run in edit mode.");
        foreach (var path in new[] { payloadAssetPath, binPath, originalKeyPath })
            if (!File.Exists(path)) throw new FileNotFoundException("Benchmark input missing", path);
        repetitions = Math.Max(1, Math.Min(5, repetitions));
        long probeStart = GC.GetAllocatedBytesForCurrentThread();
        var allocationProbe = new byte[4096];
        allocationCounterAvailable = GC.GetAllocatedBytesForCurrentThread() > probeStart;
        GC.KeepAlive(allocationProbe);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        outputDirectory = Path.GetFullPath("Library/MCB/Benchmarks/" + id);
        Directory.CreateDirectory(outputDirectory);
        scratch = ScratchRoot + "/" + id;
        report = new Report {
            startedUtc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion,
            cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName,
            logicalCores = Environment.ProcessorCount, memoryMB = SystemInfo.systemMemorySize,
            sourceAsset = payloadAssetPath, sourceBin = binPath, sourceKey = originalKeyPath,
            sourceAssetBytes = new FileInfo(payloadAssetPath).Length,
            serializationMode = EditorSettings.serializationMode.ToString(),
            sceneDirtyBefore = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty
        };
        report.notes.Add("Iteration 0 is warmup; measured iterations are >= 1. File reads use the normal Windows cache; no cold-disk claim.");
        report.notes.Add("Allocated bytes cover the executing thread only, excluding parallel worker allocations and Unity native allocations. Process peak is whole-editor lifetime, not benchmark-exclusive.");
        if (!allocationCounterAvailable) report.notes.Add("The current runtime allocation counter is unavailable; allocation fields are null, not zero allocations.");
        report.notes.Add("No scene version switch or real network transfer is performed. UI intro/completion timing is preserved and excluded.");
        ResultPath = Path.Combine(outputDirectory, "results.json");
        routine = Run(repetitions);
        Status = "Starting";
        lastTick = EditorApplication.timeSinceStartup;
        EditorApplication.update += Tick;
        Checkpoint();
        return ResultPath;
    }

    static void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        report.longestEditorTickGapMs = Math.Max(report.longestEditorTickGapMs, (now - lastTick) * 1000);
        lastTick = now;
        try
        {
            if (routine.MoveNext()) return;
            Finish(null);
        }
        catch (Exception ex) { Finish(ex); }
    }

    static void Finish(Exception error)
    {
        EditorApplication.update -= Tick;
        (routine as IDisposable)?.Dispose();
        routine = null;
        report.error = error?.ToString();
        report.finishedUtc = DateTime.UtcNow.ToString("O");
        report.sceneDirtyAfter = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty;
        long processPeak = Process.GetCurrentProcess().PeakWorkingSet64;
        report.processPeakWorkingSetBytes = processPeak > 0 ? processPeak : (long?)null;
        Status = error == null ? "Completed" : "Failed: " + error.GetBaseException().Message;
        // Do not clean a folder while a failed worker could still be writing to it.
        if (activeWorker == null || activeWorker.IsCompleted) CleanupScratch();
        source = null;
        Checkpoint();
        UnityEngine.Debug.Log("[NativeMeshPipelineBenchmark] " + Status + ": " + ResultPath);
    }

    static void Checkpoint()
    {
        lock (report.measurements)
            File.WriteAllText(ResultPath, JsonConvert.SerializeObject(report, Formatting.Indented));
    }

    static T Measure<T>(string stage, int iteration, Func<T> action, Func<T, long> bytes = null, string note = null)
    {
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        T value = action();
        timer.Stop();
        var row = new Measurement {
            stage = stage, iteration = iteration, milliseconds = timer.Elapsed.TotalMilliseconds,
            allocatedBytesCurrentThread = allocationCounterAvailable ? GC.GetAllocatedBytesForCurrentThread() - allocation : (long?)null,
            bytes = bytes == null ? 0 : bytes(value), verified = false, note = note
        };
        lock (report.measurements) report.measurements.Add(row);
        Checkpoint();
        return value;
    }

    static void VerifiedLast()
    {
        lock (report.measurements) report.measurements[report.measurements.Count - 1].verified = true;
        Checkpoint();
    }

    static IEnumerator Run(int repeats)
    {
        Status = "Loading real mesh fixture";
        source = AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(report.sourceAsset);
        if (source == null) throw new InvalidDataException("Input is not a native mesh payload asset.");
        if (source.payloadCompression != NativeMeshPayloadService.PayloadCompressionNone)
            throw new InvalidDataException("This codec-comparison fixture must use NONE payload compression.");
        report.rendererCount = source.renderers.Count;
        foreach (var record in source.renderers)
        {
            report.vertices += record.mesh.vertexCount;
            for (int i = 0; i < record.mesh.blendShapeCount; i++)
                report.blendshapeFrames += record.mesh.GetBlendShapeFrameCount(i);
        }
        EnsureFolder(scratch);
        Checkpoint();
        yield return null;

        byte[] bin = null, key = null, plain = null;
        Status = "CPU decode and source verification experiments";
        activeWorker = Task.Run(() => {
            key = File.ReadAllBytes(report.sourceKey);
            bin = File.ReadAllBytes(report.sourceBin);
            report.keyBytes = key.Length;
            if (Hash(key) != source.sourceFbxHash) throw new InvalidDataException("Original key hash mismatch.");
            for (int i = 0; i <= repeats; i++)
            {
                var hashed = Measure("original_key_sha256", i, () => Hash(key), _ => key.Length);
                if (hashed != source.sourceFbxHash) throw new InvalidDataException("Key hash changed.");
                VerifiedLast();
                byte[] read = Measure("payload_file_read_warm_os_cache", i, () => File.ReadAllBytes(report.sourceBin), x => x.Length);
                if (!read.SequenceEqual(bin)) throw new InvalidDataException("Read mismatch.");
                VerifiedLast();
                var status = new NativeMeshPayloadService.NativeMeshPayloadPreparationStatus();
                var baseline = Measure("xor_current_parallel_modulo", i, () => (byte[])Call("XorTransformWithProgress", key, bin, status, 0f, 1f), x => x.Length);
                if (Hash(baseline) != source.payloadHash) throw new InvalidDataException("Payload hash mismatch.");
                VerifiedLast();
                foreach (int workers in new[] { 1, 4, 8 })
                {
                    var decoded = Measure("xor_contiguous_words_workers_" + workers, i, () => XorWords(bin, key, workers), x => x.Length);
                    if (!decoded.SequenceEqual(baseline)) throw new InvalidDataException("Optimized XOR differs.");
                    VerifiedLast();
                }
                plain = baseline;
            }
            report.payloadSha256 = Hash(plain);
            report.payloadBytes = plain.Length;
            File.WriteAllBytes(Path.Combine(outputDirectory, "payload-for-codec-benchmark.bin"), plain);
        });
        while (!activeWorker.IsCompleted) yield return null;
        activeWorker.GetAwaiter().GetResult();

        // Exercise the real writer, rather than assuming a representative small-write pattern.
        for (int iteration = 0; iteration <= repeats; iteration++)
        {
            foreach (bool buffered in iteration % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                Status = "Packaging real meshes: " + (buffered ? "buffered" : "current") + " " + iteration;
                string destination = Path.Combine(outputDirectory, "packaging-" + buffered + ".bin");
                var hash = Measure("mesh_packaging_" + (buffered ? "buffered_256KiB" : "current"), iteration,
                    () => PackageMeshes(destination, key, buffered), _ => new FileInfo(destination).Length);
                string other = Path.Combine(outputDirectory, "packaging-" + !buffered + ".bin");
                if (File.Exists(other) && HashFile(other) != hash) throw new InvalidDataException("Buffered writer changed output bytes.");
                VerifiedLast();
                yield return null;
            }
        }

        NativeMeshPayloadService.PreparedPayloadAssetData prepared = null;
        Status = "Parsing payload using production parser";
        activeWorker = Task.Run(() => {
            for (int i = 0; i <= repeats; i++)
            {
                prepared = Measure("parse_current_dense_preparation", i,
                    () => (NativeMeshPayloadService.PreparedPayloadAssetData)Call("ReadPreparedPayloadAsset", plain, "BenchmarkPayload", source.payloadHash, source.payloadCompression,
                        new NativeMeshPayloadService.NativeMeshPayloadPreparationStatus(), 0f, 1f));
                if (prepared.renderers.Count != report.rendererCount) throw new InvalidDataException("Parser renderer mismatch.");
                VerifiedLast();
            }
            AnalyzeSparse(prepared);
        });
        while (!activeWorker.IsCompleted) yield return null;
        activeWorker.GetAwaiter().GetResult();
        plain = null; bin = null; key = null;

        for (int iteration = 0; iteration <= repeats; iteration++)
        {
            Status = "Creating Unity meshes " + iteration;
            var meshes = Measure("create_meshes_current", iteration,
                () => prepared.renderers.Select(r => (Mesh)Call("CreateMeshFromPreparedData", r.mesh)).ToArray());
            ValidateMeshCounts(meshes);
            VerifiedLast();
            foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
            yield return null;
        }
        prepared = null;

        for (int iteration = 0; iteration <= repeats; iteration++)
        {
            foreach (bool binary in iteration % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                Status = "Saving " + (binary ? "binary" : "text") + " cache " + iteration;
                string path = scratch + "/" + (binary ? "binary" : "text") + ".asset";
                NativeMeshPayloadAsset clone = CreateClone(binary);
                Measure("cache_register_save_" + (binary ? "binary" : "text"), iteration, () => {
                    AssetDatabase.StartAssetEditing();
                    try {
                        AssetDatabase.CreateAsset(clone, path);
                        foreach (var r in clone.renderers) AssetDatabase.AddObjectToAsset(r.mesh, clone);
                        AssetDatabase.SetMainObject(clone, path);
                        EditorUtility.SetDirty(clone);
                    }
                    finally { AssetDatabase.StopAssetEditing(); }
                    AssetDatabase.SaveAssetIfDirty(clone);
                    return new FileInfo(path).Length;
                }, x => x);
                using (var stream = File.OpenRead(path)) {
                    bool isText = stream.ReadByte() == '%';
                    if (isText == binary) throw new InvalidDataException("Requested serialization mode was not honored.");
                }
                VerifiedLast();
                yield return null;
                UnloadOwned(clone); clone = null;
                var loaded = Measure("cache_reload_" + (binary ? "binary" : "text") + "_warm_os_cache", iteration,
                    () => AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(path));
                if (loaded == null) throw new InvalidDataException("Saved cache did not reload.");
                ValidateMeshCounts(loaded.renderers.Select(r => r.mesh).ToArray());
                VerifiedLast();
                // Full MCB serialization fingerprint includes vertex streams, skinning,
                // submesh indices and the blendshape channels preserved by the payload format.
                if (Fingerprint(loaded) != Fingerprint(source)) throw new InvalidDataException("Serialized mesh roundtrip mismatch.");
                var warm = Measure("cache_already_loaded_lookup_" + (binary ? "binary" : "text"), iteration,
                    () => AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(path));
                if (warm != loaded) throw new InvalidDataException("Warm lookup did not reuse object.");
                VerifiedLast();
                UnloadOwned(loaded);
                AssetDatabase.DeleteAsset(path);
                yield return null;
            }
        }
        // Scratch CPU outputs are reproducible, regenerable, and intentionally not retained.
        foreach (string name in new[] { "packaging-False.bin", "packaging-True.bin" })
            File.Delete(Path.Combine(outputDirectory, name));
    }

    static object Call(string method, params object[] arguments)
    {
        var member = typeof(NativeMeshPayloadService).GetMethod(method, PrivateStatic);
        if (member == null) throw new MissingMethodException(method);
        try { return member.Invoke(null, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    static string PackageMeshes(string destination, byte[] key, bool buffered, bool nativeHash = false)
    {
        using (var output = File.Create(destination))
        using (HashAlgorithm sha = nativeHash ? (HashAlgorithm)new BenchmarkWindowsSha256() : SHA256.Create())
        using (HashAlgorithm payloadSha = nativeHash ? (HashAlgorithm)new BenchmarkWindowsSha256() : SHA256.Create())
        {
            var type = typeof(NativeMeshPayloadService).GetNestedType("XorWriteStream", BindingFlags.NonPublic);
            var hashType = typeof(NativeMeshPayloadService).GetNestedType("HashingWriteStream", BindingFlags.NonPublic);
            using (var hashing = (Stream)Activator.CreateInstance(hashType, new object[] { output, sha, true }))
            using (var xor = (Stream)Activator.CreateInstance(type, new object[] { hashing, key, true }))
            using (var payloadHashing = (Stream)Activator.CreateInstance(hashType, new object[] { xor, payloadSha, true }))
            {
                Stream target = buffered ? new BufferedStream(payloadHashing, 256 * 1024) : payloadHashing;
                using (var writer = new BinaryWriter(target, Encoding.UTF8, true))
                {
                    foreach (var r in source.renderers) Call("WriteMesh", writer, r.mesh, r.mesh.name, null);
                    writer.Flush(); target.Flush();
                }
                hashType.GetMethod("CompleteHash").Invoke(payloadHashing, null);
                hashType.GetMethod("CompleteHash").Invoke(hashing, null);
                if (buffered) target.Dispose();
            }
            return Hex(sha.Hash);
        }
    }

    static byte[] XorWords(byte[] input, byte[] key, int workers)
    {
        var output = new byte[input.Length];
        const int chunkSize = 1024 * 1024;
        Action<int> process = chunk => {
            int position = chunk * chunkSize, end = Math.Min(input.Length, position + chunkSize);
            int keyPosition = position % key.Length;
            var words = new ulong[8192];
            var keyWords = new ulong[8192];
            while (position < end)
            {
                int count = Math.Min(end - position, key.Length - keyPosition);
                count = Math.Min(count, words.Length * 8);
                int aligned = count & ~7;
                Buffer.BlockCopy(input, position, words, 0, aligned);
                Buffer.BlockCopy(key, keyPosition, keyWords, 0, aligned);
                for (int i = 0; i < aligned / 8; i++) words[i] ^= keyWords[i];
                Buffer.BlockCopy(words, 0, output, position, aligned);
                for (int i = aligned; i < count; i++) output[position + i] = (byte)(input[position + i] ^ key[keyPosition + i]);
                position += count; keyPosition += count;
                if (keyPosition == key.Length) keyPosition = 0;
            }
        };
        int chunks = (input.Length + chunkSize - 1) / chunkSize;
        if (workers == 1) for (int i = 0; i < chunks; i++) process(i);
        else Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, process);
        return output;
    }

    static void AnalyzeSparse(NativeMeshPayloadService.PreparedPayloadAssetData data)
    {
        long dense = 0, sparse = 0, adaptive = 0;
        int arrays = 0, denseWinners = 0;
        foreach (var mesh in data.renderers.Select(r => r.mesh))
        foreach (var frame in mesh.blendShapes.SelectMany(s => s.frames))
        foreach (var values in new[] { frame.deltaVertices, frame.deltaNormals, frame.deltaTangents })
        {
            if (values == null) continue;
            int nonzero = 0;
            foreach (var v in values) if (Math.Abs(v.x) > 0.00001f || Math.Abs(v.y) > 0.00001f || Math.Abs(v.z) > 0.00001f) nonzero++;
            long d = 4L + values.Length * 12L, s = 8L + nonzero * 16L;
            dense += d; sparse += s; adaptive += 1 + Math.Min(d, s); arrays++;
            if (d < s) denseWinners++;
        }
        report.notes.Add($"Blendshape arrays={arrays}; dense winners={denseWinners}; current sparse encoded bytes={sparse}; dense working bytes approx={dense}; adaptive encoded bytes incl one-byte tags={adaptive}. Sizes are computed from real arrays, not codec throughput measurements.");
        Checkpoint();
    }

    static NativeMeshPayloadAsset CreateClone(bool binary)
    {
        NativeMeshPayloadAsset clone = binary ? ScriptableObject.CreateInstance<BinaryBenchmarkPayload>() : ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        clone.name = "BenchmarkPayload";
        clone.payloadVersion = source.payloadVersion; clone.payloadHash = source.payloadHash;
        clone.sourceFbxPath = source.sourceFbxPath; clone.sourceFbxHash = source.sourceFbxHash;
        clone.payloadCompression = source.payloadCompression;
        clone.bones.AddRange(source.bones); clone.authoringPoseBones.AddRange(source.authoringPoseBones);
        foreach (var r in source.renderers) clone.renderers.Add(new NativeMeshPayloadRenderer {
            avatarPath = r.avatarPath, fbxMeshPath = r.fbxMeshPath, meshName = r.meshName,
            rendererName = r.rendererName, mesh = Object.Instantiate(r.mesh),
            localPosition = r.localPosition, localRotation = r.localRotation, localScale = r.localScale,
            rootBonePath = r.rootBonePath, bonePaths = new List<string>(r.bonePaths)
        });
        for (int i = 0; i < clone.renderers.Count; i++) clone.renderers[i].mesh.name = source.renderers[i].mesh.name;
        return clone;
    }

    static void ValidateMeshCounts(Mesh[] meshes)
    {
        if (meshes.Length != report.rendererCount) throw new InvalidDataException("Mesh count mismatch.");
        for (int i = 0; i < meshes.Length; i++) {
            var expected = source.renderers[i].mesh;
            if (meshes[i].vertexCount != expected.vertexCount || meshes[i].blendShapeCount != expected.blendShapeCount || meshes[i].subMeshCount != expected.subMeshCount)
                throw new InvalidDataException("Mesh structure mismatch.");
        }
    }

    static string Fingerprint(NativeMeshPayloadAsset payload)
    {
        using (var sha = SHA256.Create())
        using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var buffered = new BufferedStream(stream, 256 * 1024))
        {
            using (var writer = new BinaryWriter(buffered, Encoding.UTF8, true)) {
                foreach (var r in payload.renderers) Call("WriteMesh", writer, r.mesh, r.mesh.name, null);
                writer.Flush(); buffered.Flush();
            }
            stream.FlushFinalBlock();
            return Hex(sha.Hash);
        }
    }
    static void UnloadOwned(NativeMeshPayloadAsset asset)
    {
        if (asset == null) return;
        string path = AssetDatabase.GetAssetPath(asset);
        if (!path.StartsWith(scratch + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to unload input asset.");
        foreach (var mesh in asset.renderers.Select(r => r.mesh).ToArray()) if (mesh != null) Resources.UnloadAsset(mesh);
        Resources.UnloadAsset(asset);
    }
    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
    static void CleanupScratch()
    {
        if (string.IsNullOrEmpty(scratch) || !scratch.StartsWith(ScratchRoot + "/", StringComparison.Ordinal)) return;
        if (AssetDatabase.IsValidFolder(scratch)) AssetDatabase.DeleteAsset(scratch);
    }
    static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
    static string HashFile(string path) { using (var sha = SHA256.Create()) using (var s = File.OpenRead(path)) return Hex(sha.ComputeHash(s)); }
    static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
#endif
