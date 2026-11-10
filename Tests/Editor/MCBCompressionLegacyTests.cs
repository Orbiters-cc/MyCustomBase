using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;

/// <summary>
/// The mesh codec container against an independent reference: MCB 1.12.24's encoder and decoder, kept here with their own
/// bindings to the native libraries, and hand-made containers written from the format (no encoder involved).
/// </summary>
public sealed class MCBCompressionLegacyTests
{
    // ---- MCB 1.12.24 (MCBCompression.Encode/Decode before streaming and parallel blocks). A reference for the bytes
    // only: the old validation (header, block size and bound checks, error messages) is left out, so it decodes only the
    // well-formed containers these tests give it; rejection is tested against MCBCompression itself.

    static class Legacy
    {
        const int Magic = 0x3143434d;
        const int BlockBytes = 4 * 1024 * 1024;

        public static byte[] Encode(byte[] input, string codec)
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(Magic); writer.Write(input.Length); writer.Write(BlockBytes);
                for (int offset = 0; offset < input.Length; offset += BlockBytes)
                {
                    int count = Math.Min(BlockBytes, input.Length - offset);
                    int bound = codec == MCBCompression.Lz4 ? LZ4_compressBound(count) : checked((int)ZSTD_compressBound((UIntPtr)(uint)count).ToUInt64());
                    var block = new byte[bound];
                    int size = Pin(input, offset, block, (src, dst) => codec == MCBCompression.Lz4
                        ? LZ4_compress_default(src, dst, count, bound)
                        : checked((int)ZSTD_compress(dst, (UIntPtr)(uint)bound, src, (UIntPtr)(uint)count, 9).ToUInt64()));
                    if (size <= 0) throw new InvalidDataException("Mesh compression failed.");
                    writer.Write(size); writer.Write(block, 0, size);
                }
                return output.ToArray();
            }
        }

        public static byte[] Decode(byte[] input, string codec)
        {
            using (var stream = new MemoryStream(input, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("Invalid compressed mesh header.");
                int length = reader.ReadInt32(), blockSize = reader.ReadInt32();
                var output = new byte[length];
                for (int offset = 0; offset < length; offset += blockSize)
                {
                    int encodedSize = reader.ReadInt32(), expected = Math.Min(blockSize, length - offset);
                    int inputOffset = checked((int)stream.Position);
                    var src = GCHandle.Alloc(input, GCHandleType.Pinned);
                    var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
                    try
                    {
                        IntPtr inputPtr = IntPtr.Add(src.AddrOfPinnedObject(), inputOffset), outputPtr = IntPtr.Add(dst.AddrOfPinnedObject(), offset);
                        int decoded = codec == MCBCompression.Lz4 ? LZ4_decompress_safe(inputPtr, outputPtr, encodedSize, expected)
                            : checked((int)ZSTD_decompress(outputPtr, (UIntPtr)(uint)expected, inputPtr, (UIntPtr)(uint)encodedSize).ToUInt64());
                        if (decoded != expected) throw new InvalidDataException("Mesh block length does not match its header.");
                    }
                    finally { dst.Free(); src.Free(); }
                    stream.Position += encodedSize;
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing mesh data.");
                return output;
            }
        }

        static int Pin(byte[] input, int offset, byte[] output, Func<IntPtr, IntPtr, int> action)
        {
            var src = GCHandle.Alloc(input, GCHandleType.Pinned);
            var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
            try { return action(IntPtr.Add(src.AddrOfPinnedObject(), offset), dst.AddrOfPinnedObject()); }
            finally { dst.Free(); src.Free(); }
        }

        [DllImport("mcb_lz4", CallingConvention = CallingConvention.Cdecl)] static extern int LZ4_compressBound(int count);
        [DllImport("mcb_lz4", CallingConvention = CallingConvention.Cdecl)] static extern int LZ4_compress_default(IntPtr src, IntPtr dst, int count, int capacity);
        [DllImport("mcb_lz4", CallingConvention = CallingConvention.Cdecl)] static extern int LZ4_decompress_safe(IntPtr src, IntPtr dst, int count, int capacity);
        [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr ZSTD_compressBound(UIntPtr count);
        [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr ZSTD_compress(IntPtr dst, UIntPtr capacity, IntPtr src, UIntPtr count, int level);
        [DllImport("mcb_zstd", CallingConvention = CallingConvention.Cdecl)] static extern UIntPtr ZSTD_decompress(IntPtr dst, UIntPtr capacity, IntPtr src, UIntPtr count);
    }

    static void RequireCodec(string codec)
    {
        if (!MCBCompression.IsSupported(codec)) Assert.Ignore(codec + " is not available on this platform.");
    }

    // Mostly zeros with some noise: compresses fast, so many blocks stay cheap to test.
    static byte[] Data(int length, int seed)
    {
        var bytes = new byte[length];
        var random = new Random(seed);
        for (int i = 0; i < length; i += 61) bytes[i] = (byte)random.Next(256);
        return bytes;
    }

    // 0, one partial block, three blocks, and seventeen blocks: more than one parallel batch on any machine (at most 8).
    [TestCase(MCBCompression.Zstd, 0)]
    [TestCase(MCBCompression.Zstd, 1000)]
    [TestCase(MCBCompression.Zstd, 3 * 4 * 1024 * 1024 - 7)]
    [TestCase(MCBCompression.Zstd, 17 * 4 * 1024 * 1024 + 5)]
    [TestCase(MCBCompression.Lz4, 0)]
    [TestCase(MCBCompression.Lz4, 1000)]
    [TestCase(MCBCompression.Lz4, 3 * 4 * 1024 * 1024 - 7)]
    [TestCase(MCBCompression.Lz4, 17 * 4 * 1024 * 1024 + 5)]
    public void EncoderAndDecoderMatchTheLegacyCodec(string codec, int length)
    {
        RequireCodec(codec);
        byte[] data = Data(length, length);
        byte[] legacy = Legacy.Encode(data, codec);
        byte[] current = MCBCompression.Encode(data, codec);
        Assert.AreEqual(legacy.Length, current.Length);
        Assert.IsTrue(legacy.SequenceEqual(current), "The container bytes changed.");
        Assert.IsTrue(Legacy.Decode(current, codec).SequenceEqual(data), "The legacy decoder reads the new container.");
        Assert.IsTrue(MCBCompression.Decode(legacy, codec).SequenceEqual(data), "The new decoder reads the legacy container.");
        Assert.IsTrue(MCBCompression.Decode(new MemoryStream(legacy), legacy.Length, codec).SequenceEqual(data));
    }

    // Containers written by hand from the format: MCC1 header (magic, decoded length, block size), then per block its size
    // and bytes. An LZ4 block of literals only, and a zstd frame with one raw block.
    static readonly byte[] Text = Encoding.ASCII.GetBytes("hello mesh!!!");

    static byte[] Container(byte[] block)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x3143434d); writer.Write(Text.Length); writer.Write(4 * 1024 * 1024);
            writer.Write(block.Length); writer.Write(block);
            return stream.ToArray();
        }
    }

    [Test] public void HandMadeLz4ContainerDecodes()
    {
        RequireCodec(MCBCompression.Lz4);
        var block = new[] { (byte)(Text.Length << 4) }.Concat(Text).ToArray(); // token: 13 literals, no match
        Assert.IsTrue(MCBCompression.Decode(Container(block), MCBCompression.Lz4).SequenceEqual(Text));
    }

    [Test] public void HandMadeZstdContainerDecodes()
    {
        RequireCodec(MCBCompression.Zstd);
        var frame = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, (byte)Text.Length } // magic, single segment, content size
            .Concat(new[] { (byte)((Text.Length << 3) | 1), (byte)0, (byte)0 }) // last raw block of 13 bytes
            .Concat(Text).ToArray();
        Assert.IsTrue(MCBCompression.Decode(Container(frame), MCBCompression.Zstd).SequenceEqual(Text));
    }

    // ---- Historical GZIP payloads (single file, no codec container): still read, hash over the GZIP bytes. The payload is
    // the 1.12.24 writer's (NativeMeshPayloadLegacyFormat), compressed as that release's GZIP path did.

    [Test] public void HistoricalGzipPayloadParsesLikeThePlainOne()
    {
        var source = NativeMeshPayloadFormatTests.SyntheticMesh("Body DynamicNormals", 200);
        try
        {
            byte[] plain = NativeMeshPayloadFormatTests.Payload(writer => NativeMeshPayloadLegacyFormat.WriteMesh(writer, source, source.name));
            byte[] gzip;
            using (var stream = new MemoryStream())
            {
                using (var zip = new GZipStream(stream, CompressionLevel.Optimal, true)) zip.Write(plain, 0, plain.Length);
                gzip = stream.ToArray();
            }
            var fromGzip = Parse(gzip, MCBHashing.HashBytes(gzip), NativeMeshPayloadService.PayloadCompressionGZip).renderers.Single().mesh;
            var fromPlain = NativeMeshPayloadFormatTests.Parse(plain).renderers.Single().mesh;
            CollectionAssert.AreEqual(fromPlain.vertices, fromGzip.vertices);
            Assert.AreEqual(fromPlain.blendShapes.Count, fromGzip.blendShapes.Count);
            CollectionAssert.AreEqual(fromPlain.blendShapes[0].frames[1].deltaVertices, fromGzip.blendShapes[0].frames[1].deltaVertices);
            Assert.Throws<InvalidDataException>(() => Parse(gzip, MCBHashing.HashBytes(plain), NativeMeshPayloadService.PayloadCompressionGZip),
                "The hash of a GZIP payload covers its compressed bytes.");
        }
        finally { UnityEngine.Object.DestroyImmediate(source); }
    }

    static NativeMeshPayloadService.PreparedPayloadAssetData Parse(byte[] payload, string hash, string compression)
    {
        try
        {
            return (NativeMeshPayloadService.PreparedPayloadAssetData)typeof(NativeMeshPayloadService)
                .GetMethod("ReadPreparedPayloadAsset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { payload, "Test", hash, compression, new NativeMeshPayloadService.NativeMeshPayloadPreparationStatus(), 0f, 1f });
        }
        catch (System.Reflection.TargetInvocationException ex) { throw ex.InnerException; }
    }
}
