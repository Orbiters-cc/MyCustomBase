#if UNITY_EDITOR
using System;
using System.Buffers;
using System.Buffers.Binary;
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
        using (var output = new MemoryStream())
        {
            Encode(input, input.Length, codec, output, cancellation);
            return output.ToArray();
        }
    }

    /// <summary>
    /// Writes the first <paramref name="count"/> bytes of <paramref name="input"/> as a container to <paramref name="output"/>,
    /// one block at a time (the same bytes <see cref="Encode(byte[], string, CancellationToken)"/> returns), and returns how
    /// many bytes it wrote.
    /// </summary>
    public static long Encode(byte[] input, int count, string codec, Stream output, CancellationToken cancellation = default)
    {
        if (input == null || count < 0 || count > input.Length || count > MaxDecodedBytes) throw new InvalidDataException("Mesh payload exceeds the supported size.");
        ValidateCodec(codec);
        int largestBound = Bound(codec, Math.Min(BlockBytes, Math.Max(1, count)));
        int blockCount = (count + BlockBytes - 1) / BlockBytes;
        // Blocks are independent: a few are compressed at once, then written in order (the same bytes as one at a time).
        int workers = Math.Max(1, Math.Min(Math.Min(Workers, blockCount), 8));
        var blocks = new byte[workers][];
        var sizes = new int[workers];
        try
        {
            for (int i = 0; i < workers; i++) blocks[i] = ArrayPool<byte>.Shared.Rent(largestBound + 4);
            long written = 12;
            WriteInt(output, blocks[0], Magic); WriteInt(output, blocks[0], count); WriteInt(output, blocks[0], BlockBytes);
            for (int first = 0; first < blockCount; first += workers) {
                cancellation.ThrowIfCancellationRequested();
                int batch = Math.Min(workers, blockCount - first);
                Run(batch, i => {
                    int offset = (first + i) * BlockBytes, length = Math.Min(BlockBytes, count - offset), bound = Bound(codec, length);
                    // The block goes after its four-byte size, so both leave in one write.
                    sizes[i] = Pin(input, offset, blocks[i], 4, (src, dst) => codec == Lz4
                        ? LZ4_compress_default(src, dst, length, bound)
                        : CheckedSize(ZSTD_compress(dst, (UIntPtr)(uint)bound, src, (UIntPtr)(uint)length, 9)));
                });
                for (int i = 0; i < batch; i++) {
                    if (sizes[i] <= 0) throw new InvalidDataException("Mesh compression failed.");
                    BinaryPrimitives.WriteInt32LittleEndian(blocks[i], sizes[i]);
                    output.Write(blocks[i], 0, sizes[i] + 4);
                    written += sizes[i] + 4;
                }
            }
            return written;
        }
        finally { foreach (var block in blocks) if (block != null) ArrayPool<byte>.Shared.Return(block); }
    }

    static int Workers => Math.Max(1, Environment.ProcessorCount - 1);

    static int Bound(string codec, int length) =>
        codec == Lz4 ? LZ4_compressBound(length) : checked((int)ZSTD_compressBound((UIntPtr)(uint)length).ToUInt64());

    // Runs the batch on worker threads (inline for one), rethrowing the first failure as itself.
    static void Run(int count, Action<int> body)
    {
        if (count == 1) { body(0); return; }
        try { System.Threading.Tasks.Parallel.For(0, count, body); }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
    }

    static void WriteInt(Stream output, byte[] scratch, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(scratch, value);
        output.Write(scratch, 0, 4);
    }

    public static byte[] Decode(byte[] input, string codec, CancellationToken cancellation = default)
    {
        ValidateCodec(codec);
        if (input == null || input.Length < 12) throw new InvalidDataException("Incomplete compressed mesh payload.");
        using (var stream = new MemoryStream(input, false))
            return Decode(stream, input.Length, codec, cancellation);
    }

    /// <summary>
    /// Decodes a container read forward from <paramref name="input"/>, which holds exactly <paramref name="inputLength"/>
    /// bytes, a few blocks at a time: only the decoded payload and those encoded blocks are in memory. Accepts and rejects the
    /// same containers as <see cref="Decode(byte[], string, CancellationToken)"/>.
    /// </summary>
    public static byte[] Decode(Stream input, long inputLength, string codec, CancellationToken cancellation = default)
    {
        ValidateCodec(codec);
        if (input == null || inputLength < 12) throw new InvalidDataException("Incomplete compressed mesh payload.");
        byte[] header = new byte[12];
        ReadExactly(input, header, 12);
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != Magic) throw new InvalidDataException("Invalid compressed mesh header.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)), blockSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (length < 0 || length > MaxDecodedBytes || blockSize != BlockBytes) throw new InvalidDataException("Invalid decompressed mesh size.");
        var output = new byte[length];
        long consumed = 12;
        int blockCount = (length + blockSize - 1) / blockSize;
        // Read in order (each block checked before the next is read), then a few decoded at once.
        int workers = Math.Max(1, Math.Min(Math.Min(Workers, blockCount), 8));
        var blocks = new byte[workers][];
        var encodedSizes = new int[workers];
        try
        {
            for (int first = 0; first < blockCount; first += workers) {
                cancellation.ThrowIfCancellationRequested();
                int batch = Math.Min(workers, blockCount - first);
                for (int i = 0; i < batch; i++) {
                    int offset = (first + i) * blockSize;
                    if (inputLength - consumed < 4) throw new InvalidDataException("Truncated mesh block.");
                    ReadExactly(input, header, 4);
                    consumed += 4;
                    int encodedSize = BinaryPrimitives.ReadInt32LittleEndian(header), expected = Math.Min(blockSize, length - offset);
                    int bound = Bound(codec, expected);
                    if (encodedSize <= 0 || encodedSize > bound || encodedSize > inputLength - consumed) throw new InvalidDataException("Invalid mesh block size.");
                    if (blocks[i] == null || blocks[i].Length < encodedSize)
                    {
                        if (blocks[i] != null) ArrayPool<byte>.Shared.Return(blocks[i]);
                        blocks[i] = ArrayPool<byte>.Shared.Rent(Math.Max(encodedSize, bound));
                    }
                    ReadExactly(input, blocks[i], encodedSize);
                    consumed += encodedSize;
                    encodedSizes[i] = encodedSize;
                }
                Run(batch, i => {
                    int offset = (first + i) * blockSize, expected = Math.Min(blockSize, length - offset), encodedSize = encodedSizes[i];
                    var src = GCHandle.Alloc(blocks[i], GCHandleType.Pinned);
                    try {
                        var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
                        try {
                            IntPtr inputPtr = src.AddrOfPinnedObject(), outputPtr = IntPtr.Add(dst.AddrOfPinnedObject(), offset);
                            int decoded = codec == Lz4 ? LZ4_decompress_safe(inputPtr, outputPtr, encodedSize, expected)
                                : CheckedSize(ZSTD_decompress(outputPtr, (UIntPtr)(uint)expected, inputPtr, (UIntPtr)(uint)encodedSize));
                            if (decoded != expected) throw new InvalidDataException("Mesh block length does not match its header.");
                        } finally { dst.Free(); }
                    } finally { src.Free(); }
                });
            }
        }
        finally { foreach (var block in blocks) if (block != null) ArrayPool<byte>.Shared.Return(block); }
        if (consumed != inputLength || input.ReadByte() >= 0) throw new InvalidDataException("Unexpected trailing mesh data.");
        return output;
    }

    // The caller checked the stream's length: running out early means the data is shorter than it said.
    static void ReadExactly(Stream input, byte[] buffer, int count)
    {
        for (int done = 0; done < count;)
        {
            int read = input.Read(buffer, done, count - done);
            if (read <= 0) throw new InvalidDataException("Truncated mesh block.");
            done += read;
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
    static int Pin(byte[] input, int offset, byte[] output, int outputOffset, Func<IntPtr, IntPtr, int> action)
    {
        var src = GCHandle.Alloc(input, GCHandleType.Pinned);
        try {
            var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
            try { return action(IntPtr.Add(src.AddrOfPinnedObject(), offset), IntPtr.Add(dst.AddrOfPinnedObject(), outputOffset)); }
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
