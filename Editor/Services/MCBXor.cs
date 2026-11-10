#if UNITY_EDITOR
using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

/// <summary>
/// Exact repeating-key XOR: byte <c>i</c> of the data is XORed with byte <c>i % key.Length</c> of the key (the original
/// model). <see cref="Apply"/> works in place over spans; the streams and <see cref="TransformFile"/> keep memory bounded
/// by reading the key and the data in blocks.
/// </summary>
public static class MCBXor
{
    const int ChunkBytes = 1024 * 1024;
    /// <summary>Block size of the streaming transforms.</summary>
    public const int StreamBlockBytes = 4 * 1024 * 1024;

    /// <summary>XORs <paramref name="data"/> in place with the key repeated from position <paramref name="keyOffset"/>.</summary>
    public static void Apply(ReadOnlySpan<byte> key, long keyOffset, Span<byte> data)
    {
        if (key.Length == 0) throw new InvalidDataException("Original FBX key data is empty.");
        int position = (int)(keyOffset % key.Length);
        while (data.Length > 0)
        {
            int count = Math.Min(data.Length, key.Length - position);
            XorRun(key.Slice(position, count), data.Slice(0, count));
            data = data.Slice(count);
            position = 0;
        }
    }

    // Equal lengths: eight bytes at a time, then the tail.
    static void XorRun(ReadOnlySpan<byte> key, Span<byte> data)
    {
        int aligned = data.Length & ~7;
        var words = MemoryMarshal.Cast<byte, ulong>(data.Slice(0, aligned));
        var keyWords = MemoryMarshal.Cast<byte, ulong>(key.Slice(0, aligned));
        for (int i = 0; i < words.Length; i++) words[i] ^= keyWords[i];
        for (int i = aligned; i < data.Length; i++) data[i] ^= key[i];
    }

    /// <summary>A new array: <paramref name="input"/> XORed with <paramref name="key"/>.</summary>
    public static byte[] Transform(byte[] key, byte[] input, Action<float> progress = null)
    {
        if (key == null || key.Length == 0) throw new InvalidDataException("Original FBX key data is empty.");
        if (input == null) throw new ArgumentNullException(nameof(input));
        var output = (byte[])input.Clone();
        int chunks = (output.Length + ChunkBytes - 1) / ChunkBytes, completed = 0;
        var progressLock = new object();
        Action<int> process = chunk =>
        {
            int position = chunk * ChunkBytes;
            Apply(key, position, new Span<byte>(output, position, Math.Min(ChunkBytes, output.Length - position)));
            if (progress != null) lock (progressLock) progress(++completed / (float)chunks);
        };
        if (output.Length >= 16 * ChunkBytes && Environment.ProcessorCount > 1)
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, process);
        else
            for (int i = 0; i < chunks; i++) process(i);
        return output;
    }

    /// <summary>
    /// Writes <paramref name="inputPath"/> XORed with the file at <paramref name="keyPath"/> to <paramref name="outputPath"/>
    /// (replaced), reading both in blocks, and returns the SHA-256 of the bytes it wrote, hashed as they were written
    /// (an advisory hash for <see cref="ContentHash"/>, and a proof inside <paramref name="operation"/>).
    /// </summary>
    public static string TransformFile(string keyPath, string inputPath, string outputPath, ContentHash.Operation operation = null)
    {
        string hash;
        using (var key = OpenRead(keyPath))
        using (var input = OpenRead(inputPath))
        using (var xor = new MCBXorReadStream(input, key, leaveOpen: true))
        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
        using (var sha = MCBHashing.CreateSha256())
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamBlockBytes);
            try
            {
                for (int read; (read = xor.Read(buffer, 0, StreamBlockBytes)) > 0;)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    output.Write(buffer, 0, read);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            hash = MCBHashing.Hex(sha.Hash);
        }
        ContentHash.Remember(outputPath, hash);
        operation?.Record(outputPath, hash);
        return hash;
    }

    internal static FileStream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
}

/// <summary>
/// Reads <c>inner</c> XORed with a key read from a seekable stream, both forward in blocks: the key is never loaded whole.
/// Byte <c>i</c> read is <c>inner[i] ^ key[i % key.Length]</c>.
/// </summary>
internal sealed class MCBXorReadStream : Stream
{
    readonly Stream inner, key;
    readonly long keyLength;
    readonly bool leaveOpen;
    byte[] keyBuffer;
    long position;

    public MCBXorReadStream(Stream inner, Stream key, bool leaveOpen)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.key = key ?? throw new ArgumentNullException(nameof(key));
        if (!key.CanSeek) throw new ArgumentException("The XOR key must be seekable.", nameof(key));
        keyLength = key.Length;
        if (keyLength == 0) throw new InvalidDataException("Original FBX key data is empty.");
        key.Position = 0;
        this.leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        if (read <= 0) return read;
        if (keyBuffer == null) keyBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(MCBXor.StreamBlockBytes, (int)Math.Min(int.MaxValue, keyLength)));
        int done = 0;
        while (done < read)
        {
            // The key is read in step with the data: its position is always position % keyLength.
            long keyPosition = position % keyLength;
            if (key.Position != keyPosition) key.Position = keyPosition;
            int run = (int)Math.Min(Math.Min(read - done, keyBuffer.Length), keyLength - keyPosition);
            int got = 0;
            while (got < run)
            {
                int n = key.Read(keyBuffer, got, run - got);
                if (n <= 0) throw new IOException("The original FBX key changed while it was read.");
                got += n;
            }
            MCBXor.Apply(new ReadOnlySpan<byte>(keyBuffer, 0, run), 0, new Span<byte>(buffer, offset + done, run));
            done += run;
            position += run;
        }
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (keyBuffer != null) { ArrayPool<byte>.Shared.Return(keyBuffer); keyBuffer = null; }
        if (disposing && !leaveOpen) { inner.Dispose(); key.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Hashes the bytes read through it with SHA-256.</summary>
internal sealed class MCBHashingReadStream : Stream
{
    readonly Stream inner;
    readonly System.Security.Cryptography.HashAlgorithm sha = MCBHashing.CreateSha256();
    string hash;

    public MCBHashingReadStream(Stream inner) { this.inner = inner ?? throw new ArgumentNullException(nameof(inner)); }

    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (hash != null) throw new InvalidOperationException("The hash is already complete.");
        int read = inner.Read(buffer, offset, count);
        if (read > 0) { sha.TransformBlock(buffer, offset, read, null, 0); BytesRead += read; }
        return read;
    }

    /// <summary>Reads what is left, then returns the hash of everything read.</summary>
    public string Finish()
    {
        if (hash != null) return hash;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MCBHashing.StreamBufferBytes);
        try { while (Read(buffer, 0, MCBHashing.StreamBufferBytes) > 0) { } }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return hash = MCBHashing.Hex(sha.Hash);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) sha.Dispose();
        base.Dispose(disposing);
    }
}
#endif
