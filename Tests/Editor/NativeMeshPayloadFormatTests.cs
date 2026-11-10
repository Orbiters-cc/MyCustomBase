using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The native mesh payload's bytes and what is read back from them, on small synthetic meshes.
/// </summary>
public sealed class NativeMeshPayloadFormatTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
    readonly List<UnityEngine.Object> created = new List<UnityEngine.Object>();

    [TearDown] public void TearDown()
    {
        foreach (var obj in created) if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
        created.Clear();
    }

    // Four vertices, two triangles, every vertex stream, and blendshapes whose frames touch different vertices (so a
    // scratch array that is not cleared between frames shows up). "DynamicNormals" in the name keeps the normal and
    // tangent deltas of the "flex" shape.
    internal static Mesh SyntheticMesh(string name = "Body DynamicNormals", int vertexCount = 4)
    {
        var mesh = new Mesh { name = name };
        var vertices = new Vector3[vertexCount];
        var random = new System.Random(vertexCount);
        float Next() => (float)(random.NextDouble() * 2 - 1);
        for (int i = 0; i < vertexCount; i++) vertices[i] = new Vector3(Next(), Next(), Next());
        mesh.vertices = vertices;
        mesh.normals = vertices.Select(v => v.normalized).ToArray();
        mesh.tangents = vertices.Select(v => new Vector4(v.y, -v.x, v.z, 1)).ToArray();
        mesh.colors32 = vertices.Select((v, i) => new Color32((byte)i, (byte)(i * 3), (byte)(i * 7), 255)).ToArray();
        mesh.SetUVs(0, vertices.Select(v => new Vector2(v.x, v.y)).ToList());
        mesh.SetUVs(3, vertices.Select(v => new Vector4(v.x, v.y, v.z, 1)).ToList());
        mesh.boneWeights = vertices.Select((v, i) => new BoneWeight { boneIndex0 = i % 2, weight0 = .75f, boneIndex1 = 1 - i % 2, weight1 = .25f }).ToArray();
        mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.TRS(Vector3.one, Quaternion.Euler(10, 20, 30), Vector3.one * 2) };
        var triangles = new List<int>();
        for (int i = 0; i + 2 < vertexCount; i += 2) triangles.AddRange(new[] { i, i + 1, i + 2 });
        mesh.subMeshCount = 2;
        mesh.SetIndices(triangles.ToArray(), MeshTopology.Triangles, 0, true);
        mesh.SetIndices(new[] { 0, vertexCount - 1 }, MeshTopology.Lines, 1, true);

        var deltas = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var tangents = new Vector3[vertexCount];
        void Frame(string shape, float weight, int first, int step, bool withNormals)
        {
            Array.Clear(deltas, 0, vertexCount); Array.Clear(normals, 0, vertexCount); Array.Clear(tangents, 0, vertexCount);
            for (int i = first; i < vertexCount; i += step)
            {
                deltas[i] = new Vector3(Next(), Next(), Next());
                normals[i] = new Vector3(Next(), Next(), .5f);
                tangents[i] = new Vector3(.5f, Next(), Next());
            }
            mesh.AddBlendShapeFrame(shape, weight, deltas, withNormals ? normals : null, withNormals ? tangents : null);
        }
        Frame("Smile", 50, 0, 2, false);
        Frame("Smile", 100, 1, 2, false);
        Frame("arm flex", 100, 0, 3, true);
        Frame("Blink", 100, vertexCount - 1, 1, false);
        return mesh;
    }

    internal static byte[] Payload(Action<BinaryWriter> writeMesh)
    {
        using (var memory = new MemoryStream())
        {
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write("MCB_NATIVE_MESH_PAYLOAD"); writer.Write(1); writer.Write("source.fbx"); writer.Write("source-hash");
                writer.Write(1);
                foreach (string name in new[] { "Body", "Body", "Body", "Body" }) writer.Write(name);
                foreach (float value in new[] { 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f }) writer.Write(value);
                writer.Write("Hips");
                writer.Write(0);
                writeMesh(writer);
                writer.Write(0);
                writer.Write(0);
            }
            return memory.ToArray();
        }
    }

    internal static byte[] WriteWithService(Mesh mesh)
    {
        var method = typeof(NativeMeshPayloadService).GetMethod("WriteMeshCoroutine", Private);
        return Payload(writer => MCBWork.Drain((IEnumerator)method.Invoke(null, new object[] { writer, mesh, mesh.name, null, null })));
    }

    internal static NativeMeshPayloadService.PreparedPayloadAssetData Parse(byte[] payload)
    {
        string hash = MCBHashing.HashBytes(payload);
        try
        {
            return (NativeMeshPayloadService.PreparedPayloadAssetData)typeof(NativeMeshPayloadService).GetMethod("ReadPreparedPayloadAsset", Private)
                .Invoke(null, new object[] { payload, "Test", hash, NativeMeshPayloadService.PayloadCompressionNone,
                    new NativeMeshPayloadService.NativeMeshPayloadPreparationStatus(), 0f, 1f });
        }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    static void AssertSameBlendShapes(Mesh expected, Mesh actual)
    {
        Assert.AreEqual(expected.blendShapeCount, actual.blendShapeCount);
        int n = expected.vertexCount;
        var ev = new Vector3[n]; var en = new Vector3[n]; var et = new Vector3[n];
        var av = new Vector3[n]; var an = new Vector3[n]; var at = new Vector3[n];
        for (int shape = 0; shape < expected.blendShapeCount; shape++)
        {
            Assert.AreEqual(expected.GetBlendShapeName(shape), actual.GetBlendShapeName(shape));
            Assert.AreEqual(expected.GetBlendShapeFrameCount(shape), actual.GetBlendShapeFrameCount(shape));
            bool withNormals = expected.GetBlendShapeName(shape).Contains("flex");
            for (int frame = 0; frame < expected.GetBlendShapeFrameCount(shape); frame++)
            {
                Assert.AreEqual(expected.GetBlendShapeFrameWeight(shape, frame), actual.GetBlendShapeFrameWeight(shape, frame));
                expected.GetBlendShapeFrameVertices(shape, frame, ev, en, et);
                actual.GetBlendShapeFrameVertices(shape, frame, av, an, at);
                CollectionAssert.AreEqual(ev, av, $"positions of {expected.GetBlendShapeName(shape)} frame {frame}");
                if (withNormals)
                {
                    CollectionAssert.AreEqual(en, an, $"normals of {expected.GetBlendShapeName(shape)} frame {frame}");
                    CollectionAssert.AreEqual(et, at, $"tangents of {expected.GetBlendShapeName(shape)} frame {frame}");
                }
                else
                {
                    Assert.IsTrue(an.All(v => v == Vector3.zero) && at.All(v => v == Vector3.zero), "Positions-only frames carry no normals.");
                }
            }
        }
    }

    [Test] public void SparseFramesCreateTheSameBlendShapes()
    {
        var source = SyntheticMesh();
        created.Add(source);
        var prepared = Parse(WriteWithService(source));
        var data = prepared.renderers.Single().mesh;
        var frame = data.blendShapes[0].frames[0];
        Assert.AreEqual(source.vertexCount, frame.vertices.length);
        Assert.AreEqual(2, frame.vertices.entries.Length, "Only the moved vertices are kept.");
        Assert.IsNull(frame.normals);
        Assert.IsNotNull(data.blendShapes[1].frames[0].normals);

        var mesh = NativeMeshPayloadService.CreateMeshFromPreparedData(data);
        created.Add(mesh);
        AssertSameBlendShapes(source, mesh);
        CollectionAssert.AreEqual(source.vertices, mesh.vertices);
        CollectionAssert.AreEqual(source.GetIndices(0), mesh.GetIndices(0));
        Assert.AreEqual(MeshTopology.Lines, mesh.GetTopology(1));
    }

    [Test] public void DenseFrameCopiesMatchTheSparseDeltas()
    {
        var source = SyntheticMesh();
        created.Add(source);
        var frame = Parse(WriteWithService(source)).renderers.Single().mesh.blendShapes[0].frames[1];
        var expected = new Vector3[source.vertexCount];
        source.GetBlendShapeFrameVertices(0, 1, expected, null, null);
        CollectionAssert.AreEqual(expected, frame.deltaVertices);
        Assert.IsNull(frame.deltaNormals);
    }

    // ---- Block writes and reads (MCBS-28): the bytes are the ones the per-value writer produced.

    [TestCase("Body DynamicNormals", 4)]
    [TestCase("Body DynamicNormals", 1001)]
    [TestCase("Clothing", 257)]
    public void WriterMatchesTheLegacyWriterByteForByte(string name, int vertexCount)
    {
        var source = SyntheticMesh(name, vertexCount);
        created.Add(source);
        byte[] legacy = Payload(writer => NativeMeshPayloadLegacyFormat.WriteMesh(writer, source, source.name));
        CollectionAssert.AreEqual(legacy, WriteWithService(source));
    }

    [Test] public void LegacyBytesParseIntoTheSameMesh()
    {
        var source = SyntheticMesh("Body DynamicNormals", 513);
        created.Add(source);
        var data = Parse(Payload(writer => NativeMeshPayloadLegacyFormat.WriteMesh(writer, source, source.name))).renderers.Single().mesh;
        CollectionAssert.AreEqual(source.vertices, data.vertices);
        CollectionAssert.AreEqual(source.normals, data.normals);
        CollectionAssert.AreEqual(source.tangents, data.tangents);
        CollectionAssert.AreEqual(source.colors32, data.colors);
        CollectionAssert.AreEqual(source.boneWeights, data.boneWeights);
        CollectionAssert.AreEqual(source.bindposes, data.bindposes);
        var uvs = new List<Vector4>();
        source.GetUVs(3, uvs);
        CollectionAssert.AreEqual(uvs, data.uvs[3]);
        Assert.AreEqual(0, data.uvs[5].Count);
        var mesh = NativeMeshPayloadService.CreateMeshFromPreparedData(data);
        created.Add(mesh);
        AssertSameBlendShapes(source, mesh);
    }

    // ---- XOR (MCBS-30, MCBS-34): in place, streamed and per file, against the byte loop

    static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes);
        return bytes;
    }

    [TestCase(1, 0, 100)]
    [TestCase(7, 3, 1000)]
    [TestCase(8, 9, 64)]
    [TestCase(13, 0, 5 * 1024 * 1024 + 3)]
    [TestCase(4096, 4095, 20000)]
    public void XorMatchesTheByteLoop(int keyLength, int offset, int dataLength)
    {
        byte[] key = RandomBytes(keyLength, keyLength), data = RandomBytes(dataLength, dataLength);
        byte[] expected = NativeMeshPayloadLegacyFormat.Xor(key, data, offset);
        var inPlace = (byte[])data.Clone();
        MCBXor.Apply(key, offset, inPlace);
        CollectionAssert.AreEqual(expected, inPlace, "Apply");
        if (offset == 0) CollectionAssert.AreEqual(expected, MCBXor.Transform(key, data), "Transform");

        // Read in uneven pieces: the key position must follow the data across reads.
        if (offset == 0)
        {
            using (var xor = new MCBXorReadStream(new MemoryStream(data), new MemoryStream(key), leaveOpen: false))
            {
                var read = new byte[dataLength];
                int done = 0, piece = 1;
                while (done < dataLength)
                {
                    int n = xor.Read(read, done, Math.Min(piece, dataLength - done));
                    Assert.Greater(n, 0);
                    done += n;
                    piece = piece * 3 % 70001 + 1;
                }
                CollectionAssert.AreEqual(expected, read, "MCBXorReadStream");
            }

            var writeStream = typeof(NativeMeshPayloadService).GetNestedType("XorWriteStream", BindingFlags.NonPublic);
            using (var output = new MemoryStream())
            {
                using (var xor = (Stream)Activator.CreateInstance(writeStream, output, key, true))
                {
                    int done = 0, piece = 5;
                    while (done < dataLength)
                    {
                        int n = Math.Min(piece, dataLength - done);
                        xor.Write(data, done, n);
                        done += n;
                        piece = piece * 7 % 300007 + 1;
                    }
                }
                CollectionAssert.AreEqual(expected, output.ToArray(), "XorWriteStream");
            }
        }
    }

    [Test] public void TransformFileWritesTheXorAndItsHash()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-xor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] key = RandomBytes(MCBXor.StreamBlockBytes + 17, 1), data = RandomBytes(3 * MCBXor.StreamBlockBytes + 5, 2);
            string keyPath = Path.Combine(folder, "key.fbx"), inputPath = Path.Combine(folder, "custom.fbx"), outputPath = Path.Combine(folder, "out.bin");
            File.WriteAllBytes(keyPath, key);
            File.WriteAllBytes(inputPath, data);
            File.WriteAllBytes(outputPath, new byte[7]);
            string hash = MCBXor.TransformFile(keyPath, inputPath, outputPath);
            byte[] written = File.ReadAllBytes(outputPath);
            CollectionAssert.AreEqual(NativeMeshPayloadLegacyFormat.Xor(key, data), written);
            Assert.AreEqual(MCBHashing.HashBytes(written), hash);
        }
        finally { Directory.Delete(folder, true); }
    }

    // ---- Codec container (MCBS-30): streamed encode and decode keep the block layout

    static byte[] CompressibleBytes(int length)
    {
        var bytes = RandomBytes(length, length);
        for (int i = 0; i < length; i += 3) bytes[i] = 0;
        return bytes;
    }

    [TestCase(MCBCompression.Zstd)]
    [TestCase(MCBCompression.Lz4)]
    public void ContainerRoundTripsAndKeepsItsLayout(string codec)
    {
        if (!MCBCompression.IsSupported(codec)) Assert.Ignore(codec + " is not available on this platform.");
        byte[] data = CompressibleBytes(2 * MCBCompression.BlockBytes + 12345);
        byte[] encoded = MCBCompression.Encode(data, codec);
        using (var reader = new BinaryReader(new MemoryStream(encoded)))
        {
            Assert.AreEqual(0x3143434d, reader.ReadInt32());
            Assert.AreEqual(data.Length, reader.ReadInt32());
            Assert.AreEqual(MCBCompression.BlockBytes, reader.ReadInt32());
            int blocks = 0;
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                int size = reader.ReadInt32();
                Assert.Greater(size, 0);
                reader.BaseStream.Position += size;
                blocks++;
            }
            Assert.AreEqual(3, blocks);
            Assert.AreEqual(reader.BaseStream.Length, reader.BaseStream.Position);
        }
        using (var stream = new MemoryStream())
        {
            Assert.AreEqual(encoded.Length, MCBCompression.Encode(data, data.Length, codec, stream));
            CollectionAssert.AreEqual(encoded, stream.ToArray(), "Streamed encode");
        }
        CollectionAssert.AreEqual(data, MCBCompression.Decode(encoded, codec));
        CollectionAssert.AreEqual(data, MCBCompression.Decode(new MemoryStream(encoded), encoded.Length, codec), "Streamed decode");

        Assert.Throws<InvalidDataException>(() => MCBCompression.Decode(encoded.Take(encoded.Length - 1).ToArray(), codec), "truncated");
        Assert.Throws<InvalidDataException>(() => MCBCompression.Decode(encoded.Concat(new byte[1]).ToArray(), codec), "trailing");
        Assert.Throws<InvalidDataException>(() => MCBCompression.Decode(encoded.Take(11).ToArray(), codec), "header");
    }

    // ---- Streamed preparation (MCBS-30): files read, XORed, hashed and decoded in blocks

    static NativeMeshPayloadService.PreparedPayloadAssetData Prepare(string binPath, string keyPath, string hash, string codec)
    {
        try
        {
            return (NativeMeshPayloadService.PreparedPayloadAssetData)typeof(NativeMeshPayloadService).GetMethod("PrepareNativeMeshPayloadData", Private)
                .Invoke(null, new object[] { binPath, keyPath, null, null, "Test", hash, codec, new NativeMeshPayloadService.NativeMeshPayloadPreparationStatus() });
        }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    [TestCase(MCBCompression.Zstd, false)]
    [TestCase(MCBCompression.Lz4, true)]
    public void StreamedPreparationReadsWhatTheBuildWrote(string codec, bool rawIdentity)
    {
        if (!MCBCompression.IsSupported(codec)) Assert.Ignore(codec + " is not available on this platform.");
        var source = SyntheticMesh("Body DynamicNormals", 3001);
        created.Add(source);
        byte[] payload = WriteWithService(source), encoded = MCBCompression.Encode(payload, codec), key = RandomBytes(100003, 9);
        string hash = rawIdentity ? "raw:" + MCBHashing.HashBytes(payload) : MCBHashing.HashBytes(encoded);
        string folder = Path.Combine(Path.GetTempPath(), "mcb-prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string binPath = Path.Combine(folder, "mesh.bin"), keyPath = Path.Combine(folder, "original.fbx");
            File.WriteAllBytes(keyPath, key);
            File.WriteAllBytes(binPath, NativeMeshPayloadLegacyFormat.Xor(key, encoded));
            var data = Prepare(binPath, keyPath, hash, codec).renderers.Single().mesh;
            CollectionAssert.AreEqual(source.vertices, data.vertices);
            var mesh = NativeMeshPayloadService.CreateMeshFromPreparedData(data);
            created.Add(mesh);
            AssertSameBlendShapes(source, mesh);

            // One flipped byte fails verification (or decoding, for a raw identity), never parses.
            var damaged = File.ReadAllBytes(binPath);
            damaged[damaged.Length / 2] ^= 0x40;
            File.WriteAllBytes(binPath, damaged);
            var ex = Assert.Throws<InvalidDataException>(() => Prepare(binPath, keyPath, hash, codec));
            if (!rawIdentity) StringAssert.Contains("verification failed", ex.Message);

            // A plain payload (no key) reads the codec bytes as they are.
            File.WriteAllBytes(binPath, encoded);
            Assert.AreEqual(3001, Prepare(binPath, null, hash, codec).renderers.Single().mesh.vertices.Length);
        }
        finally { Directory.Delete(folder, true); }
    }
}
