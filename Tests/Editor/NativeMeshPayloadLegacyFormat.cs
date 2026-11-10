using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// The payload mesh writer as it was before block writes (MCB 1.12.24), one BinaryWriter call per value, and the XOR
/// and codec container it was wrapped in: the reference the current writer must match byte for byte.
/// </summary>
internal static class NativeMeshPayloadLegacyFormat
{
    const float SparseVectorEpsilon = 0.00001f;

    public static void WriteMesh(BinaryWriter writer, Mesh mesh, string meshName)
    {
        var vertices = mesh.vertices;
        writer.Write(string.IsNullOrWhiteSpace(meshName) ? mesh.name : meshName);
        writer.Write((int)mesh.indexFormat);
        WriteVector3Array(writer, vertices);
        WriteVector3Array(writer, mesh.normals);
        WriteVector4Array(writer, mesh.tangents);
        WriteColor32Array(writer, mesh.colors32);
        for (int channel = 0; channel < 8; channel++)
        {
            var uvs = new List<Vector4>();
            mesh.GetUVs(channel, uvs);
            WriteVector4List(writer, uvs);
        }
        WriteBoneWeightArray(writer, mesh.boneWeights);
        WriteMatrixArray(writer, mesh.bindposes);
        writer.Write(mesh.subMeshCount);
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            writer.Write((int)mesh.GetTopology(i));
            WriteIntArray(writer, mesh.GetIndices(i, true));
        }
        WriteVector3(writer, mesh.bounds.center);
        WriteVector3(writer, mesh.bounds.size);

        int vertexCount = vertices.Length;
        writer.Write(mesh.blendShapeCount);
        var deltaVertices = new Vector3[vertexCount];
        var deltaNormals = new Vector3[vertexCount];
        var deltaTangents = new Vector3[vertexCount];
        for (int shape = 0; shape < mesh.blendShapeCount; shape++)
        {
            string shapeName = mesh.GetBlendShapeName(shape) ?? "";
            bool includeNormalTangents = ShouldSerializeBlendShapeNormalTangents(mesh, shapeName);
            writer.Write(shapeName);
            int frameCount = mesh.GetBlendShapeFrameCount(shape);
            writer.Write(frameCount);
            for (int frame = 0; frame < frameCount; frame++)
            {
                Array.Clear(deltaVertices, 0, deltaVertices.Length);
                Array.Clear(deltaNormals, 0, deltaNormals.Length);
                Array.Clear(deltaTangents, 0, deltaTangents.Length);
                mesh.GetBlendShapeFrameVertices(shape, frame, deltaVertices,
                    includeNormalTangents ? deltaNormals : null, includeNormalTangents ? deltaTangents : null);
                writer.Write(mesh.GetBlendShapeFrameWeight(shape, frame));
                WriteSparseVector3Array(writer, deltaVertices, exact: true);
                writer.Write(includeNormalTangents);
                if (includeNormalTangents) WriteSparseVector3Array(writer, deltaNormals);
                writer.Write(includeNormalTangents);
                if (includeNormalTangents) WriteSparseVector3Array(writer, deltaTangents);
            }
        }
    }

    static bool ShouldSerializeBlendShapeNormalTangents(Mesh mesh, string shapeName)
    {
        if (mesh == null || string.IsNullOrWhiteSpace(shapeName)) return false;
        if (mesh.name?.IndexOf("DynamicNormals", StringComparison.OrdinalIgnoreCase) < 0) return false;
        string lower = shapeName.ToLowerInvariant();
        return lower.Contains("muscle") || lower.Contains("flex");
    }

    static void WriteVector3(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }

    static void WriteVector4(BinaryWriter writer, Vector4 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }

    static void WriteVector3Array(BinaryWriter writer, Vector3[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<Vector3>()) WriteVector3(writer, value);
    }

    static void WriteVector4Array(BinaryWriter writer, Vector4[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<Vector4>()) WriteVector4(writer, value);
    }

    static void WriteVector4List(BinaryWriter writer, List<Vector4> values)
    {
        writer.Write(values?.Count ?? 0);
        foreach (var value in values ?? new List<Vector4>()) WriteVector4(writer, value);
    }

    static void WriteColor32Array(BinaryWriter writer, Color32[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<Color32>()) { writer.Write(value.r); writer.Write(value.g); writer.Write(value.b); writer.Write(value.a); }
    }

    static void WriteBoneWeightArray(BinaryWriter writer, BoneWeight[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<BoneWeight>())
        {
            writer.Write(value.boneIndex0); writer.Write(value.boneIndex1); writer.Write(value.boneIndex2); writer.Write(value.boneIndex3);
            writer.Write(value.weight0); writer.Write(value.weight1); writer.Write(value.weight2); writer.Write(value.weight3);
        }
    }

    static void WriteMatrixArray(BinaryWriter writer, Matrix4x4[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var matrix in values ?? Array.Empty<Matrix4x4>())
            for (int i = 0; i < 16; i++) writer.Write(matrix[i]);
    }

    static void WriteIntArray(BinaryWriter writer, int[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (int value in values ?? Array.Empty<int>()) writer.Write(value);
    }

    static void WriteSparseVector3Array(BinaryWriter writer, Vector3[] values, bool exact = false)
    {
        int length = values?.Length ?? 0;
        writer.Write(length);
        if (length == 0) { writer.Write(0); return; }
        var nonZeroValues = new List<KeyValuePair<int, Vector3>>();
        for (int i = 0; i < length; i++)
        {
            Vector3 value = values[i];
            if (exact ? value.x == 0f && value.y == 0f && value.z == 0f :
                Mathf.Abs(value.x) <= SparseVectorEpsilon && Mathf.Abs(value.y) <= SparseVectorEpsilon && Mathf.Abs(value.z) <= SparseVectorEpsilon)
                continue;
            nonZeroValues.Add(new KeyValuePair<int, Vector3>(i, value));
        }
        writer.Write(nonZeroValues.Count);
        foreach (var item in nonZeroValues) { writer.Write(item.Key); WriteVector3(writer, item.Value); }
    }

    /// <summary>The XOR of the old write stream: one modulo per byte.</summary>
    public static byte[] Xor(byte[] key, byte[] data, long position = 0)
    {
        var output = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) output[i] = (byte)(data[i] ^ key[(int)((position + i) % key.Length)]);
        return output;
    }
}
