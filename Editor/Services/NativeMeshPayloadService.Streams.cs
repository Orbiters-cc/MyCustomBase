#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCBEditorUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Meshes;

// Write and read streams of the payload pipeline.
public static partial class NativeMeshPayloadService
{
    /// <summary>Reports the share of <c>length</c> bytes read through it.</summary>
    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream inner;
        private readonly long length;
        private readonly Action<float> report;
        private long read;

        public ProgressReadStream(Stream inner, long length, Action<float> report)
        {
            this.inner = inner;
            this.length = Math.Max(1, length);
            this.report = report;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            if (n > 0)
            {
                read += n;
                report(Math.Min(1f, read / (float)length));
            }
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly HashAlgorithm hashAlgorithm;
        private readonly bool leaveOpen;
        private bool hashCompleted;

        public HashingWriteStream(Stream inner, HashAlgorithm hashAlgorithm, bool leaveOpen)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.hashAlgorithm = hashAlgorithm ?? throw new ArgumentNullException(nameof(hashAlgorithm));
            this.leaveOpen = leaveOpen;
        }

        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (hashCompleted)
            {
                throw new InvalidOperationException("Cannot write after hash completion.");
            }

            if (count <= 0)
            {
                return;
            }

            hashAlgorithm.TransformBlock(buffer, offset, count, null, 0);
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public void CompleteHash()
        {
            if (hashCompleted)
            {
                return;
            }

            inner.Flush();
            hashAlgorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            hashCompleted = true;
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CompleteHash();
                if (!leaveOpen)
                {
                    inner.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class XorWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly byte[] key;
        private readonly bool leaveOpen;

        public XorWriteStream(Stream inner, byte[] key, bool leaveOpen)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.key = key ?? throw new ArgumentNullException(nameof(key));
            this.leaveOpen = leaveOpen;
            if (key.Length == 0)
            {
                throw new ArgumentException("XOR key cannot be empty.", nameof(key));
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get; set; }

        private byte[] scratch;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            // XORed in a reused block, a key run at a time (MCBXor.Apply): the caller's buffer is left as it is.
            if (scratch == null) scratch = new byte[Math.Min(Math.Max(count, 64 * 1024), MCBXor.StreamBlockBytes)];
            for (int done = 0; done < count;)
            {
                int run = Math.Min(count - done, scratch.Length);
                Buffer.BlockCopy(buffer, offset + done, scratch, 0, run);
                MCBXor.Apply(key, Position, new Span<byte>(scratch, 0, run));
                inner.Write(scratch, 0, run);
                Position += run;
                done += run;
            }
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
#endif
