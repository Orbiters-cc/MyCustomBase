#if UNITY_EDITOR
using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Shared SHA-256 provider; hash identity is independent of the platform. The one streaming file hash and the one hex
/// format (lowercase, no separators) every MCB hash uses. Editor code reads file hashes through <c>ContentHash</c>, which
/// caches them; this class never caches.
/// </summary>
public static class MCBHashing
{
    /// <summary>Read size of <see cref="HashStream"/>: large enough that a native hash call per block costs nothing.</summary>
    public const int StreamBufferBytes = 1024 * 1024;

    public static HashAlgorithm CreateSha256()
    {
#if UNITY_EDITOR_WIN
        return new MCBWindowsSha256();
#else
        return SHA256.Create();
#endif
    }

    /// <summary>SHA-256 hex of a whole file. Throws when it cannot be read.</summary>
    /// <param name="progress">Called with the share hashed so far (0..1), from the hashing thread.</param>
    public static string HashFile(string path, Action<float> progress = null)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            return Hex(HashStream(stream, progress));
    }

    /// <summary>SHA-256 of the rest of <paramref name="stream"/>, read in <see cref="StreamBufferBytes"/> blocks.</summary>
    public static byte[] HashStream(Stream stream, Action<float> progress = null)
    {
        long total = progress != null && stream.CanSeek ? Math.Max(1, stream.Length - stream.Position) : 0, done = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StreamBufferBytes);
        try
        {
            using (var sha = CreateSha256())
            {
                for (int read; (read = stream.Read(buffer, 0, StreamBufferBytes)) > 0;)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    if (total > 0) progress(Math.Min(1f, (done += read) / (float)total));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                progress?.Invoke(1f);
                return sha.Hash;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static string HashBytes(byte[] bytes) => HashBytes(bytes, 0, bytes.Length);

    public static string HashBytes(byte[] bytes, int offset, int count)
    {
        using (var sha = CreateSha256()) return Hex(sha.ComputeHash(bytes, offset, count));
    }

    /// <summary>SHA-256 hex of the UTF-8 bytes of <paramref name="value"/> (null hashes as empty).</summary>
    public static string HashString(string value) => HashBytes(Encoding.UTF8.GetBytes(value ?? string.Empty));

    /// <summary>Lowercase hex without separators.</summary>
    public static string Hex(byte[] digest)
    {
        if (digest == null) return null;
        var chars = new char[digest.Length * 2];
        for (int i = 0; i < digest.Length; i++)
        {
            int b = digest[i];
            chars[2 * i] = HexDigit(b >> 4);
            chars[2 * i + 1] = HexDigit(b & 15);
        }
        return new string(chars);
    }

    static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);

    /// <summary>The bytes of a hex string of even length (either case); null when it is not one.</summary>
    public static byte[] FromHex(string hex)
    {
        if (hex == null || (hex.Length & 1) != 0) return null;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int high = HexValue(hex[2 * i]), low = HexValue(hex[2 * i + 1]);
            if (high < 0 || low < 0) return null;
            bytes[i] = (byte)(high << 4 | low);
        }
        return bytes;
    }

    static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    /// <summary>True for a lowercase or uppercase SHA-256 hex digest (64 hex digits).</summary>
    public static bool IsSha256Hex(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value) if (HexValue(c) < 0) return false;
        return true;
    }
}
#endif
