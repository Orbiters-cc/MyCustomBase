#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;

public static partial class NativeMeshPipelineBenchmark
{
    // Explicit native-library paths keep this experiment independent of package installation.
    public static string StartTransportSupplement(string asset, string bin, string key,
        string binarySample, string lz4Library, string zstdLibrary, int repetitions = 3)
    {
        if (UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsEditor)
            throw new PlatformNotSupportedException("Windows-only experiment.");
        foreach (string path in new[] { binarySample, lz4Library, zstdLibrary })
            if (!File.Exists(path)) throw new FileNotFoundException("Missing experiment input", path);
        string result = Start(asset, bin, key, repetitions);
        routine = RunTransportSupplement(binarySample, lz4Library, zstdLibrary, Math.Max(1, Math.Min(5, repetitions)));
        return result;
    }

    static IEnumerator RunTransportSupplement(string binarySample, string lz4Library, string zstdLibrary, int repetitions)
    {
        source = AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(report.sourceAsset);
        if (source == null) throw new InvalidDataException("Missing fixture.");
        report.rendererCount = source.renderers.Count;
        EnsureFolder(scratch);
        string expectedHash = source.payloadHash;
        Status = "Comparing native codecs inside Unity";
        activeWorker = Task.Run(() => {
            byte[] key = File.ReadAllBytes(report.sourceKey);
            byte[] plain = XorWords(File.ReadAllBytes(report.sourceBin), key, 4);
            using (var hash = new BenchmarkWindowsSha256())
                if (Hex(hash.ComputeHash(plain)) != expectedHash) throw new InvalidDataException("Wrong payload key.");
            using (var lz4 = new BenchmarkNativeCodec(lz4Library, false))
            using (var zstd = new BenchmarkNativeCodec(zstdLibrary, true)) {
                report.notes.Add("Native Unity codec versions: LZ4=" + lz4.Version + "; Zstd=" + zstd.Version + ". DLL paths explicitly supplied by developer; no production integration.");
                foreach (string kind in new[] { "payload", "binary_cache" }) {
                    byte[] input = kind == "payload" ? plain : File.ReadAllBytes(binarySample);
                    string digest;
                    using (var sha = new BenchmarkWindowsSha256()) digest = Hex(sha.ComputeHash(input));
                    for (int i = 0; i <= repetitions; i++) {
                        // Alternate order to reduce systematic warm-order bias.
                        foreach (int level in i % 2 == 0 ? new[] { 0, 3, 9 } : new[] { 9, 3, 0 }) {
                            var codec = level == 0 ? lz4 : zstd;
                            string label = kind + "_" + (level == 0 ? "lz4" : "zstd_" + level);
                            byte[] encoded = Measure("unity_compress_" + label, i, () => codec.Compress(input, level), x => x.Length);
                            byte[] decoded = Measure("unity_decode_" + label, i, () => codec.Decompress(encoded, input.Length), x => x.Length);
                            using (var sha = new BenchmarkWindowsSha256())
                                if (Hex(sha.ComputeHash(decoded)) != digest) throw new InvalidDataException("Codec changed bytes.");
                            lock (report.measurements) {
                                report.measurements[report.measurements.Count - 1].verified = true;
                                report.measurements[report.measurements.Count - 2].verified = true;
                            }
                            Checkpoint();
                        }
                    }
                }
            }
        });
        while (!activeWorker.IsCompleted) yield return null;
        activeWorker.GetAwaiter().GetResult();
        report.notes.Add("Codec timings include managed output allocation and native calls. Compression includes trimming the result. Hash verification is outside timers. Decoders use identical pinned-array binding and allocation policy.");

        string expectedFingerprint = Fingerprint(source);
        for (int i = 0; i <= repetitions; i++) {
            Status = "Importing binary cache into a fresh asset path " + i;
            string path = scratch + "/fresh-binary-" + i + ".asset";
            NativeMeshPayloadAsset loaded = null;
            try {
                loaded = Measure("binary_cache_copy_import_load_fresh_path", i, () => {
                    File.Copy(binarySample, path);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    return AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(path);
                }, _ => new FileInfo(path).Length);
                if (loaded == null) throw new InvalidDataException("Fresh binary import failed.");
                ValidateMeshCounts(loaded.renderers.Select(r => r.mesh).ToArray());
                if (Fingerprint(loaded) != expectedFingerprint) throw new InvalidDataException("Fresh binary import changed MCB mesh representation.");
                VerifiedLast();
            }
            finally {
                if (loaded != null) UnloadOwned(loaded);
                AssetDatabase.DeleteAsset(path);
            }
            yield return null;
        }
        report.notes.Add("Fresh-path import includes file copy, synchronous AssetDatabase import and load, with a warm Windows file cache. It does not establish cross-project, cross-Unity-version or build-target portability.");
    }
}

// Benchmark-only dynamically loaded codecs. No production code calls this class.
internal sealed class BenchmarkNativeCodec : IDisposable
{
    IntPtr library;
    readonly bool zstd;
    readonly Bound32 bound32;
    readonly Codec32 compress32, decompress32;
    readonly Bound64 bound64;
    readonly Compress64 compress64;
    readonly Decompress64 decompress64;
    readonly IsError isError;
    public readonly uint Version;

    public BenchmarkNativeCodec(string path, bool useZstd)
    {
        zstd = useZstd;
        library = LoadLibraryW(Path.GetFullPath(path));
        if (library == IntPtr.Zero) throw new InvalidOperationException("Native codec load failed: " + Marshal.GetLastWin32Error());
        try {
            Version = Function<VersionFn>(zstd ? "ZSTD_versionNumber" : "LZ4_versionNumber")();
            if (zstd) {
                bound64 = Function<Bound64>("ZSTD_compressBound");
                compress64 = Function<Compress64>("ZSTD_compress");
                decompress64 = Function<Decompress64>("ZSTD_decompress");
                isError = Function<IsError>("ZSTD_isError");
            } else {
                bound32 = Function<Bound32>("LZ4_compressBound");
                compress32 = Function<Codec32>("LZ4_compress_default");
                decompress32 = Function<Codec32>("LZ4_decompress_safe");
            }
        } catch { Dispose(); throw; }
    }
    public byte[] Compress(byte[] input, int level)
    {
        int capacity = zstd ? checked((int)bound64((UIntPtr)(uint)input.Length).ToUInt64()) : bound32(input.Length);
        byte[] output = new byte[capacity];
        int length = WithPinned(input, output, (src, dst) => {
            if (!zstd) return compress32(src, dst, input.Length, output.Length);
            UIntPtr size = compress64(dst, (UIntPtr)(uint)output.Length, src, (UIntPtr)(uint)input.Length, level);
            if (isError(size) != 0) throw new InvalidDataException("Zstd compression failed.");
            return checked((int)size.ToUInt64());
        });
        if (length <= 0) throw new InvalidDataException("Compression failed.");
        Array.Resize(ref output, length);
        return output;
    }
    public byte[] Decompress(byte[] input, int outputLength)
    {
        byte[] output = new byte[outputLength];
        int length = WithPinned(input, output, (src, dst) => {
            if (!zstd) return decompress32(src, dst, input.Length, output.Length);
            UIntPtr size = decompress64(dst, (UIntPtr)(uint)output.Length, src, (UIntPtr)(uint)input.Length);
            if (isError(size) != 0) throw new InvalidDataException("Zstd decompression failed.");
            return checked((int)size.ToUInt64());
        });
        if (length != outputLength) throw new InvalidDataException("Decompressed length mismatch.");
        return output;
    }
    static int WithPinned(byte[] input, byte[] output, Func<IntPtr, IntPtr, int> action)
    {
        var src = GCHandle.Alloc(input, GCHandleType.Pinned);
        try {
            var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
            try { return action(src.AddrOfPinnedObject(), dst.AddrOfPinnedObject()); }
            finally { dst.Free(); }
        } finally { src.Free(); }
    }
    T Function<T>(string name) where T : Delegate
    {
        IntPtr symbol = GetProcAddress(library, name);
        if (symbol == IntPtr.Zero) throw new MissingMethodException(name);
        return Marshal.GetDelegateForFunctionPointer<T>(symbol);
    }
    public void Dispose() { if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; } }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint VersionFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Bound32(int input);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Codec32(IntPtr source, IntPtr destination, int sourceBytes, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate UIntPtr Bound64(UIntPtr input);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate UIntPtr Compress64(IntPtr destination, UIntPtr capacity, IntPtr source, UIntPtr sourceBytes, int level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate UIntPtr Decompress64(IntPtr destination, UIntPtr capacity, IntPtr source, UIntPtr sourceBytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint IsError(UIntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
}
#endif
