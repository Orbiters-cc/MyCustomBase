#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static partial class NativeMeshPipelineBenchmark
{
    public static string StartNativeSupplement(string asset, string bin, string key, int repetitions = 3)
    {
        if (Application.platform != RuntimePlatform.WindowsEditor) throw new PlatformNotSupportedException("Windows-only experiment.");
        string path = Start(asset, bin, key, repetitions);
        routine = RunNativeSupplement(Math.Max(1, Math.Min(5, repetitions)));
        return path;
    }

    static IEnumerator RunNativeSupplement(int repetitions)
    {
        source = AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(report.sourceAsset);
        if (source == null) throw new InvalidDataException("Missing fixture.");
        report.rendererCount = source.renderers.Count;
        EnsureFolder(scratch);
        byte[] key = null;
        activeWorker = Task.Run(() => {
            key = File.ReadAllBytes(report.sourceKey);
            string expected = Hash(key);
            using (var current = SHA256.Create()) report.notes.Add("Current provider: " + current.GetType().FullName);
            foreach (var sample in new[] { Array.Empty<byte>(), System.Text.Encoding.ASCII.GetBytes("abc"), key }) {
                using (var native = new BenchmarkWindowsSha256())
                    if (Hex(native.ComputeHash(sample)) != Hash(sample)) throw new InvalidDataException("Native SHA-256 mismatch.");
            }
            for (int i = 0; i <= repetitions; i++) {
                foreach (bool native in i % 2 == 0 ? new[] { false, true } : new[] { true, false }) {
                    string actual = Measure(native ? "original_key_sha256_windows_cng" : "original_key_sha256_managed", i, () => {
                        using (HashAlgorithm sha = native ? (HashAlgorithm)new BenchmarkWindowsSha256() : SHA256.Create()) return Hex(sha.ComputeHash(key));
                    }, _ => key.Length);
                    if (actual != expected) throw new InvalidDataException("Hash mismatch.");
                    VerifiedLast();
                }
            }
        });
        while (!activeWorker.IsCompleted) yield return null;
        activeWorker.GetAwaiter().GetResult();

        string reference = null;
        for (int i = 0; i <= repetitions; i++) {
            Status = "Buffered packaging with Windows SHA-256 " + i;
            string destination = Path.Combine(outputDirectory, "native-packaging.bin");
            string digest = Measure("mesh_packaging_buffered_windows_cng", i,
                () => PackageMeshes(destination, key, true, true), _ => new FileInfo(destination).Length);
            if (reference == null) {
                // Compare to the production algorithms on the same mesh serializer.
                reference = PackageMeshes(Path.Combine(outputDirectory, "managed-reference.bin"), key, true);
            }
            if (digest != reference || digest != HashFile(destination)) throw new InvalidDataException("Native packaging changed output bytes.");
            VerifiedLast();
            yield return null;
        }

        NativeMeshPayloadService.PreparedPayloadAssetData prepared = null;
        activeWorker = Task.Run(() => {
            byte[] bin = File.ReadAllBytes(report.sourceBin);
            byte[] plain = XorWords(bin, key, 4);
            prepared = (NativeMeshPayloadService.PreparedPayloadAssetData)Call("ReadPreparedPayloadAsset", plain, "BenchmarkPayload", source.payloadHash, source.payloadCompression,
                new NativeMeshPayloadService.NativeMeshPayloadPreparationStatus(), 0f, 1f);
        });
        while (!activeWorker.IsCompleted) yield return null;
        activeWorker.GetAwaiter().GetResult();
        for (int i = 0; i <= repetitions; i++) {
            foreach (var record in prepared.renderers) {
                Status = "Measuring blendshape submission " + record.rendererName;
                var frames = record.mesh.blendShapes.ToArray();
                record.mesh.blendShapes.Clear();
                Mesh mesh;
                try { mesh = Measure("create_geometry_" + record.rendererName, i, () => (Mesh)Call("CreateMeshFromPreparedData", record.mesh)); }
                finally { record.mesh.blendShapes.AddRange(frames); }
                VerifiedLast();
                try {
                    Measure("add_blendshapes_" + record.rendererName, i, () => {
                        foreach (var shape in frames) foreach (var frame in shape.frames)
                            mesh.AddBlendShapeFrame(shape.name, frame.weight, frame.deltaVertices, frame.deltaNormals, frame.deltaTangents);
                        return mesh.blendShapeCount;
                    });
                    if (mesh.blendShapeCount != frames.Length) throw new InvalidDataException("Blendshape count mismatch.");
                    VerifiedLast();
                }
                finally { Object.DestroyImmediate(mesh); }
                yield return null;
            }
        }
        prepared = null;
        // A local binary-cache sample lets the separate codec benchmark check
        // transport cost before considering any new wire representation.
        Status = "Saving a binary-cache sample for size comparison";
        var clone = CreateClone(true);
        string path = scratch + "/binary-wire-size-experiment.asset";
        AssetDatabase.StartAssetEditing();
        try {
            AssetDatabase.CreateAsset(clone, path);
            foreach (var r in clone.renderers) AssetDatabase.AddObjectToAsset(r.mesh, clone);
            AssetDatabase.SetMainObject(clone, path);
            EditorUtility.SetDirty(clone);
        }
        finally { AssetDatabase.StopAssetEditing(); }
        AssetDatabase.SaveAssetIfDirty(clone);
        File.Copy(path, Path.Combine(outputDirectory, "binary-cache-for-codec-benchmark.bin"));
        report.notes.Add("Binary-cache codec sample is a benchmark container with editor-local references; it is NOT a validated distribution format or an AssetBundle.");
        UnloadOwned(clone);
        AssetDatabase.DeleteAsset(path);
        foreach (var name in new[] { "native-packaging.bin", "managed-reference.bin" }) File.Delete(Path.Combine(outputDirectory, name));
    }
}

// Benchmark-only Windows CNG SHA-256. No production call sites use this class.
internal sealed class BenchmarkWindowsSha256 : HashAlgorithm
{
    IntPtr algorithm, hash;
    public BenchmarkWindowsSha256()
    {
        HashSizeValue = 256;
        Check(BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 0));
        try { Initialize(); }
        catch { BCryptCloseAlgorithmProvider(algorithm, 0); algorithm = IntPtr.Zero; throw; }
    }
    public override void Initialize()
    {
        if (hash != IntPtr.Zero) { BCryptDestroyHash(hash); hash = IntPtr.Zero; }
        Check(BCryptCreateHash(algorithm, out hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
    }
    protected override void HashCore(byte[] array, int offset, int count)
    {
        if (count == 0) return;
        var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
        try { Check(BCryptHashData(hash, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), count, 0)); }
        finally { pinned.Free(); }
    }
    protected override byte[] HashFinal()
    {
        var bytes = new byte[32];
        Check(BCryptFinishHash(hash, bytes, bytes.Length, 0));
        return bytes;
    }
    protected override void Dispose(bool disposing)
    {
        if (hash != IntPtr.Zero) { BCryptDestroyHash(hash); hash = IntPtr.Zero; }
        if (algorithm != IntPtr.Zero) { BCryptCloseAlgorithmProvider(algorithm, 0); algorithm = IntPtr.Zero; }
        base.Dispose(disposing);
    }
    static void Check(int status) { if (status != 0) throw new CryptographicException("Windows SHA-256 status 0x" + status.ToString("X8")); }
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr objectBuffer, int objectBytes, IntPtr secret, int secretBytes, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptHashData(IntPtr hash, IntPtr input, int bytes, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] output, int bytes, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDestroyHash(IntPtr hash);
}
#endif
