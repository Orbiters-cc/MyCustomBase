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
        Assert.AreEqual(2, frame.vertices.indices.Length, "Only the moved vertices are kept.");
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
}
