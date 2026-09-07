#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;

/// <summary>Exact repeating-key XOR, using bounded workers and reusable word buffers.</summary>
public static class MCBXor
{
    const int ChunkBytes = 1024 * 1024;
    sealed class Scratch { public readonly ulong[] data = new ulong[8192], key = new ulong[8192]; }

    public static byte[] Transform(byte[] key, byte[] input, Action<float> progress = null)
    {
        if (key == null || key.Length == 0) throw new InvalidDataException("Original FBX key data is empty.");
        if (input == null) throw new ArgumentNullException(nameof(input));
        var output = new byte[input.Length];
        int chunks = (input.Length + ChunkBytes - 1) / ChunkBytes, completed = 0;
        var progressLock = new object();
        Action<int, Scratch> process = (chunk, scratch) => {
            int position = chunk * ChunkBytes, end = Math.Min(input.Length, position + ChunkBytes);
            int keyPosition = position % key.Length;
            while (position < end) {
                int count = Math.Min(end - position, key.Length - keyPosition);
                count = Math.Min(count, scratch.data.Length * 8);
                int aligned = count & ~7;
                if (aligned > 0) {
                    Buffer.BlockCopy(input, position, scratch.data, 0, aligned);
                    Buffer.BlockCopy(key, keyPosition, scratch.key, 0, aligned);
                    for (int i = 0; i < aligned / 8; i++) scratch.data[i] ^= scratch.key[i];
                    Buffer.BlockCopy(scratch.data, 0, output, position, aligned);
                }
                for (int i = aligned; i < count; i++) output[position + i] = (byte)(input[position + i] ^ key[keyPosition + i]);
                position += count; keyPosition += count;
                if (keyPosition == key.Length) keyPosition = 0;
            }
            if (progress != null) lock (progressLock) progress(++completed / (float)chunks);
        };
        if (input.Length >= 16 * ChunkBytes && Environment.ProcessorCount > 1) {
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) },
                () => new Scratch(), (chunk, _, scratch) => { process(chunk, scratch); return scratch; }, _ => { });
        } else {
            var scratch = new Scratch();
            for (int i = 0; i < chunks; i++) process(i, scratch);
        }
        return output;
    }
}
#endif
