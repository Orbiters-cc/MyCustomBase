#if UNITY_EDITOR
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>Bounded, lossless block container shared by packaging, preparation and calibration.</summary>
public static class MCBCompression
{
    public const string Lz4 = "LZ4";
    public const string Zstd = "ZSTD";
    public const int BlockBytes = 4 * 1024 * 1024;
    public const int MaxDecodedBytes = 1024 * 1024 * 1024;
    const int Magic = 0x3143434d; // MCC1

    public static bool IsSupported(string codec)
    {
        try { return codec == Lz4 ? LZ4_versionNumber() > 0 : codec == Zstd && ZSTD_versionNumber() > 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    public static byte[] Encode(byte[] input, string codec, CancellationToken cancellation = default)
    {
        if (input == null || input.Length > MaxDecodedBytes) throw new InvalidDataException("Mesh payload exceeds the supported size.");
        ValidateCodec(codec);
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output)) {
            writer.Write(Magic); writer.Write(input.Length); writer.Write(BlockBytes);
            for (int offset = 0; offset < input.Length; offset += BlockBytes) {
                cancellation.ThrowIfCancellationRequested();
                int count = Math.Min(BlockBytes, input.Length - offset);
                int bound = codec == Lz4 ? LZ4_compressBound(count) : checked((int)ZSTD_compressBound((UIntPtr)(uint)count).ToUInt64());
                var block = new byte[bound];
                int size = Pin(input, offset, block, (src, dst) => codec == Lz4
                    ? LZ4_compress_default(src, dst, count, bound)
                    : CheckedSize(ZSTD_compress(dst, (UIntPtr)(uint)bound, src, (UIntPtr)(uint)count, 9)));
                if (size <= 0) throw new InvalidDataException("Mesh compression failed.");
                writer.Write(size); writer.Write(block, 0, size);
            }
            return output.ToArray();
        }
    }

    public static byte[] Decode(byte[] input, string codec, CancellationToken cancellation = default)
    {
        ValidateCodec(codec);
        if (input == null || input.Length < 12) throw new InvalidDataException("Incomplete compressed mesh payload.");
        using (var stream = new MemoryStream(input, false))
        using (var reader = new BinaryReader(stream)) {
            if (reader.ReadInt32() != Magic) throw new InvalidDataException("Invalid compressed mesh header.");
            int length = reader.ReadInt32(), blockSize = reader.ReadInt32();
            if (length < 0 || length > MaxDecodedBytes || blockSize != BlockBytes) throw new InvalidDataException("Invalid decompressed mesh size.");
            var output = new byte[length];
            for (int offset = 0; offset < length; offset += blockSize) {
                cancellation.ThrowIfCancellationRequested();
                if (stream.Length - stream.Position < 4) throw new InvalidDataException("Truncated mesh block.");
                int encodedSize = reader.ReadInt32(), expected = Math.Min(blockSize, length - offset);
                int bound = codec == Lz4 ? LZ4_compressBound(expected) : checked((int)ZSTD_compressBound((UIntPtr)(uint)expected).ToUInt64());
                if (encodedSize <= 0 || encodedSize > bound || encodedSize > stream.Length - stream.Position) throw new InvalidDataException("Invalid mesh block size.");
                int inputOffset = checked((int)stream.Position);
                var src = GCHandle.Alloc(input, GCHandleType.Pinned);
                try {
                    var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
                    try {
                        IntPtr inputPtr = IntPtr.Add(src.AddrOfPinnedObject(), inputOffset), outputPtr = IntPtr.Add(dst.AddrOfPinnedObject(), offset);
                        int decoded = codec == Lz4 ? LZ4_decompress_safe(inputPtr, outputPtr, encodedSize, expected)
                            : CheckedSize(ZSTD_decompress(outputPtr, (UIntPtr)(uint)expected, inputPtr, (UIntPtr)(uint)encodedSize));
                        if (decoded != expected) throw new InvalidDataException("Mesh block length does not match its header.");
                    } finally { dst.Free(); }
                } finally { src.Free(); }
                stream.Position += encodedSize;
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing mesh data.");
            return output;
        }
    }

    static void ValidateCodec(string codec)
    {
        if (codec != Lz4 && codec != Zstd) throw new InvalidDataException("Unsupported mesh codec: " + codec);
    }
    static int CheckedSize(UIntPtr size)
    {
        if (ZSTD_isError(size) != 0) throw new InvalidDataException("Zstd rejected the mesh data.");
        return checked((int)size.ToUInt64());
    }
    static int Pin(byte[] input, int offset, byte[] output, Func<IntPtr, IntPtr, int> action)
    {
        var src = GCHandle.Alloc(input, GCHandleType.Pinned);
        try {
            var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
            try { return action(IntPtr.Add(src.AddrOfPinnedObject(), offset), dst.AddrOfPinnedObject()); }
            finally { dst.Free(); }
        } finally { src.Free(); }
    }
    [DllImport("mcb_lz4", CallingConvention = CallingConvention.Cdecl)] static extern int LZ4_versionNumber();
    [DllImport("mcb_lz4", CallingConvention = CallingConvention.Cdecl)] static extern int LZ4_compressBound(int count);
    [DllImport("mcb_lz4", CallingConvention = CallingConvention.Cdecl)] static extern int LZ4_compress_default(IntPtr src, IntPtr dst, int count, int capacity);
    [DllImport("mcb_lz4", CallingConvention = CallingConvention.Cdecl)] static extern int LZ4_decompress_safe(IntPtr src, IntPtr dst, int count, int capacity);
    [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern uint ZSTD_versionNumber();
    [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr ZSTD_compressBound(UIntPtr count);
    [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr ZSTD_compress(IntPtr dst, UIntPtr capacity, IntPtr src, UIntPtr count, int level);
    [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr ZSTD_decompress(IntPtr dst, UIntPtr capacity, IntPtr src, UIntPtr count);
    [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern uint ZSTD_isError(UIntPtr size);
}
#endif
