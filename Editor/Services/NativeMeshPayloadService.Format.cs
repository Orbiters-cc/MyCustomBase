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

// The payload's binary format: the writer, the reader over untrusted bytes, the plain data it reads into, and
// their array helpers.
public static partial class NativeMeshPayloadService
{
    // An unbound payload names no source model, so applying it uses the custom skeleton pose it carries.
    private static System.Collections.IEnumerator WriteBinaryPayloadContentsCoroutine(
        string sourceFbxPath,
        GameObject customFbx,
        List<PayloadRendererSource> rendererSources,
        Stream payloadOutput,
        NativeMeshPayloadBuildMetrics metrics,
        Transform sourcePoseRoot,
        bool bindSource = true)
    {
        Transform customRoot = customFbx.transform;
        string sourceFbxHash = ContentHash.Proof(sourceFbxPath);
        var rendererRecordsForBones = new List<NativeMeshPayloadRenderer>();
        var usedMeshNames = new HashSet<string>(StringComparer.Ordinal);

        using (var writer = new BinaryWriter(payloadOutput, Encoding.UTF8, true))
        {
            writer.Write(BinaryPayloadMagic);
            writer.Write(PayloadVersion);
            writer.Write(bindSource ? StripOriginalSuffix(MCBUtils.ToUnityPath(sourceFbxPath)) ?? "" : "");
            writer.Write(bindSource ? sourceFbxHash ?? "" : "");
            writer.Write(rendererSources.Count);

            foreach (var source in rendererSources)
            {
                var record = RendererRecord(customRoot, source);
                Mesh mesh = source.renderer.sharedMesh;
                string meshName = string.IsNullOrWhiteSpace(source.entry.meshName)
                    ? mesh.name
                    : source.entry.meshName;
                string uniqueMeshName = BuildUniqueSubAssetName(meshName, usedMeshNames);

                writer.Write(record.avatarPath);
                writer.Write(record.fbxMeshPath);
                writer.Write(record.meshName);
                writer.Write(record.rendererName);
                WriteVector3(writer, record.localPosition);
                WriteQuaternion(writer, record.localRotation);
                WriteVector3(writer, record.localScale);
                writer.Write(record.rootBonePath);
                WriteStringList(writer, record.bonePaths);
                yield return WriteMeshCoroutine(writer, mesh, uniqueMeshName, metrics, source.normalFrames);

                rendererRecordsForBones.Add(record);
            }

            var authoringPoseBones = BuildAuthoringPoseDeltas(sourceFbxPath, customRoot, sourcePoseRoot);
            var bones = PayloadBones(customRoot, rendererRecordsForBones, authoringPoseBones, bindSource);

            writer.Write(bones.Count);
            foreach (var bone in bones)
            {
                writer.Write(bone.path ?? "");
                WriteVector3(writer, bone.localPosition);
                WriteQuaternion(writer, bone.localRotation);
                WriteVector3(writer, bone.localScale);
            }

            writer.Write(authoringPoseBones.Count);
            foreach (var bone in authoringPoseBones)
            {
                writer.Write(bone.path ?? "");
                WriteVector3(writer, bone.localPositionOffset);
                WriteQuaternion(writer, bone.localRotationDelta);
                WriteVector3(writer, bone.localScaleMultiplier);
            }
            MCBLogger.Log($"[NativeMeshPayload] Captured authoring pose deltas: bones={authoringPoseBones.Count}");

            writer.Flush();
        }
    }

    // Turns Read/Write on in the model's import settings (the payload reads the vertices) and imports it again: the mesh
    // object stays the same one. Only a mesh that isn't an imported model's stays unreadable.
    private static void MakeReadable(Mesh mesh)
    {
        string path = AssetDatabase.GetAssetPath(mesh);
        if (AssetImporter.GetAtPath(path) is ModelImporter importer && !importer.isReadable)
        {
            MCBLogger.Log($"[NativeMeshPayload] Enabling Read/Write on {path} to read {mesh.name}.");
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
        if (!mesh.isReadable) throw new InvalidOperationException("The mesh " + mesh.name + " can't be read" + (string.IsNullOrEmpty(path) ? "" : " (" + path + ")") + ": enable Read/Write on its model.");
    }

    private static System.Collections.IEnumerator WriteMeshCoroutine(BinaryWriter writer, Mesh mesh, string meshName, NativeMeshPayloadBuildMetrics metrics,
        Dictionary<(string shape, int frame), DynamicNormals.NormalFrame> normalFrames = null)
    {
        if (mesh == null)
        {
            throw new InvalidOperationException("Native mesh payload cannot serialize a null mesh.");
        }

        if (!mesh.isReadable) MakeReadable(mesh);
        {
            var vertices = mesh.vertices;
            writer.Write(string.IsNullOrWhiteSpace(meshName) ? mesh.name : meshName);
            writer.Write((int)mesh.indexFormat);
            WriteBlock(writer, vertices);
            WriteBlock(writer, mesh.normals);
            WriteBlock(writer, mesh.tangents);
            WriteBlock(writer, mesh.colors32);

            var uvs = new List<Vector4>(vertices.Length);
            Vector4[] uvScratch = null;
            for (int channel = 0; channel < 8; channel++)
            {
                uvs.Clear();
                mesh.GetUVs(channel, uvs);
                WriteVector4List(writer, uvs, ref uvScratch);
            }

            WriteBoneWeightArray(writer, mesh.boneWeights);
            WriteBlock(writer, mesh.bindposes);

            writer.Write(mesh.subMeshCount);
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                writer.Write((int)mesh.GetTopology(i));
                WriteBlock(writer, mesh.GetIndices(i, true));
            }

            WriteBounds(writer, mesh.bounds);

            int vertexCount = vertices.Length;
            writer.Write(mesh.blendShapeCount);
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];
            // Whether deltaNormals and deltaTangents hold a previous frame's deltas: they are written as zeros when a baked
            // frame lacks its own (the arrays used to be cleared before every frame).
            bool normalsHeld = false;
            SparseDelta[] sparse = null;
            var slice = System.Diagnostics.Stopwatch.StartNew();
            yield return null;
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                string shapeName = mesh.GetBlendShapeName(shape) ?? "";
                bool includeNormalTangents = ShouldSerializeBlendShapeNormalTangents(mesh, shapeName)
                    || (normalFrames != null && (shapeName.IndexOf("muscle", StringComparison.OrdinalIgnoreCase) >= 0
                        || shapeName.IndexOf("flex", StringComparison.OrdinalIgnoreCase) >= 0));
                writer.Write(shapeName);
                int frameCount = mesh.GetBlendShapeFrameCount(shape);
                writer.Write(frameCount);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    DynamicNormals.NormalFrame bakedFrame = null;
                    normalFrames?.TryGetValue((shapeName, frame), out bakedFrame);
                    bool writeNormalTangents = includeNormalTangents || bakedFrame != null;
                    Array.Clear(deltaVertices, 0, deltaVertices.Length);
                    if (includeNormalTangents)
                    {
                        Array.Clear(deltaNormals, 0, deltaNormals.Length);
                        Array.Clear(deltaTangents, 0, deltaTangents.Length);
                        normalsHeld = true;
                    }
                    else if (normalsHeld && writeNormalTangents && (bakedFrame.normals == null || bakedFrame.tangents == null))
                    {
                        Array.Clear(deltaNormals, 0, deltaNormals.Length);
                        Array.Clear(deltaTangents, 0, deltaTangents.Length);
                        normalsHeld = false;
                    }
                    mesh.GetBlendShapeFrameVertices(
                        shape,
                        frame,
                        deltaVertices,
                        includeNormalTangents ? deltaNormals : null,
                        includeNormalTangents ? deltaTangents : null);
                    writer.Write(mesh.GetBlendShapeFrameWeight(shape, frame));
                    long vertexBytes = WriteSparseVector3Array(writer, deltaVertices, ref sparse, exact: true);
                    writer.Write(writeNormalTangents);
                    long normalBytes = writeNormalTangents ? WriteSparseVector3Array(writer, bakedFrame?.normals ?? deltaNormals, ref sparse) : 0L;
                    writer.Write(writeNormalTangents);
                    long tangentBytes = writeNormalTangents ? WriteSparseVector3Array(writer, bakedFrame?.tangents ?? deltaTangents, ref sparse) : 0L;
                    if (metrics != null)
                    {
                        metrics.blendShapeVertexBytes += vertexBytes;
                        metrics.skippedBlendShapeNormalBytes += writeNormalTangents ? 0L : EstimateSparseVector3ArrayWorstCaseBytes(vertexCount);
                        metrics.skippedBlendShapeTangentBytes += writeNormalTangents ? 0L : EstimateSparseVector3ArrayWorstCaseBytes(vertexCount);
                        metrics.blendShapeNormalBytes += normalBytes;
                        metrics.blendShapeTangentBytes += tangentBytes;
                    }
                    // Time-sliced: a frame takes far less than a tick, so yielding after each one stretched the build.
                    if (slice.ElapsedMilliseconds >= 8)
                    {
                        yield return null;
                        slice.Restart();
                    }
                }
            }
        }
    }

    private static PreparedPayloadAssetData PrepareNativeMeshPayloadData(
        string binPath,
        string originalFbxPath,
        byte[] preloadedBinData,
        byte[] preloadedOriginalFbxData,
        string assetName,
        string payloadHash,
        string payloadCompression,
        NativeMeshPayloadPreparationStatus status)
    {
        // The .bin and its key (the original model; a plain payload has none) are read forward in blocks, XORed and hashed
        // on the way, and decoded block by block: only the decoded payload is ever held whole.
        bool inMemory = preloadedBinData != null && preloadedBinData.Length > 0;
        status.Report(inMemory ? 0.24f : 0.12f, inMemory ? "Using in-memory advanced mesh payload..." : "Reading advanced mesh payload...");
        using (Stream bin = inMemory ? new MemoryStream(preloadedBinData, false) : (Stream)MCBXor.OpenRead(binPath))
        using (Stream key = preloadedOriginalFbxData != null && preloadedOriginalFbxData.Length > 0
                   ? new MemoryStream(preloadedOriginalFbxData, false)
                   : !string.IsNullOrEmpty(originalFbxPath) ? MCBXor.OpenRead(originalFbxPath) : null)
        {
            long length = bin.Length;
            var xor = key != null ? new MCBXorReadStream(bin, key, leaveOpen: true) : null;
            try
            {
                string label = xor != null ? "Decrypting advanced mesh payload..." : "Reading advanced mesh payload...";
                var progress = new ProgressReadStream(xor ?? bin, length, share => status.Report(Lerp(0.24f, 0.48f, share), label));
                var payload = ReadPreparedPayload(progress, length, assetName, payloadHash, payloadCompression, status, 0.50f, 1f);
                status.Report(1f, "Advanced mesh payload prepared...");
                return payload;
            }
            finally { xor?.Dispose(); }
        }
    }

    private static PreparedPayloadAssetData ReadPreparedPayloadAsset(
        byte[] payloadBytes,
        string assetName,
        string payloadHash,
        string payloadCompression,
        NativeMeshPayloadPreparationStatus status,
        float startProgress,
        float endProgress)
    {
        using (var stream = new MemoryStream(payloadBytes ?? Array.Empty<byte>(), false))
            return ReadPreparedPayload(stream, payloadBytes?.LongLength ?? 0, assetName, payloadHash, payloadCompression, status, startProgress, endProgress);
    }

    /// <summary>
    /// Verifies, decodes and parses a payload read forward from <paramref name="source"/> (<paramref name="length"/> bytes,
    /// already XORed). A codec payload's hash covers its encoded bytes ("raw:" identities cover the decoded payload): it is
    /// computed while the blocks are decoded, and a payload that fails to decode is reported as failing verification when
    /// its hash does not match, as when the hash was checked first.
    /// </summary>
    private static PreparedPayloadAssetData ReadPreparedPayload(
        Stream source,
        long length,
        string assetName,
        string payloadHash,
        string payloadCompression,
        NativeMeshPayloadPreparationStatus status,
        float startProgress,
        float endProgress)
    {
        if (string.IsNullOrWhiteSpace(payloadHash)) throw new InvalidDataException("Advanced mesh payload hash is missing.");
        bool raw = payloadHash.StartsWith("raw:", StringComparison.Ordinal);
        byte[] payloadBytes;
        if (payloadCompression == MCBCompression.Lz4 || payloadCompression == MCBCompression.Zstd)
        {
            if (raw)
            {
                payloadBytes = MCBCompression.Decode(source, length, payloadCompression);
                VerifyPayloadHash(payloadBytes, payloadHash.Substring(4));
            }
            else
            {
                using (var hashing = new MCBHashingReadStream(source))
                {
                    try { payloadBytes = MCBCompression.Decode(hashing, length, payloadCompression); }
                    catch (InvalidDataException)
                    {
                        RequireHash(hashing.Finish(), payloadHash);
                        throw;
                    }
                    RequireHash(hashing.Finish(), payloadHash);
                }
            }
        }
        else
        {
            payloadBytes = ReadAll(source, length);
            VerifyPayloadHash(payloadBytes, raw ? payloadHash.Substring(4) : payloadHash);
        }

        if (payloadBytes == null || payloadBytes.Length == 0)
        {
            throw new InvalidDataException("Native mesh payload is empty.");
        }

        try
        {
            using (var reader = PayloadReader.Open(payloadBytes, PayloadUsesGZip(payloadCompression)))
            {
                return ReadPreparedPayloadAssetContents(reader, assetName, payloadHash, status, startProgress, endProgress);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Native mesh payload could not be read with the expected MCB payload format.", ex);
        }
    }

    private static byte[] ReadAll(Stream source, long length)
    {
        if (length > int.MaxValue) throw new InvalidDataException("Native mesh payload exceeds the supported size.");
        var bytes = new byte[length];
        for (int done = 0; done < bytes.Length;)
        {
            int read = source.Read(bytes, done, bytes.Length - done);
            if (read <= 0) throw new EndOfStreamException("The advanced mesh payload is shorter than its file.");
            done += read;
        }
        return bytes;
    }

    private static PreparedPayloadAssetData ReadPreparedPayloadAssetContents(
        PayloadReader reader,
        string assetName,
        string payloadHash,
        NativeMeshPayloadPreparationStatus status,
        float startProgress,
        float endProgress)
    {
        string magic = reader.ReadString();
        if (!string.Equals(magic, BinaryPayloadMagic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native mesh payload is not an MCB binary payload.");
        }

        int version = reader.ReadInt32();
        if (version != PayloadVersion)
        {
            throw new InvalidDataException($"Unsupported native mesh payload version: {version}");
        }

        var payload = new PreparedPayloadAssetData
        {
            assetName = assetName,
            payloadVersion = version,
            sourceFbxPath = reader.ReadString(),
            sourceFbxHash = reader.ReadString(),
            payloadHash = payloadHash
        };

        int rendererCount = reader.ReadCount(PayloadReader.MinRendererBytes, "renderers");
        status.Report(startProgress, $"Parsing advanced mesh header ({rendererCount} renderer{(rendererCount == 1 ? "" : "s")})...");
        var usedMeshNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rendererCount; i++)
        {
            var record = new PreparedPayloadRendererData
            {
                avatarPath = reader.ReadString(),
                fbxMeshPath = reader.ReadString(),
                meshName = reader.ReadString(),
                rendererName = reader.ReadString(),
                localPosition = ReadVector3(reader),
                localRotation = ReadQuaternion(reader),
                localScale = ReadVector3(reader),
                rootBonePath = reader.ReadString(),
                bonePaths = ReadStringList(reader)
            };

            record.mesh = ReadPreparedMesh(reader);
            record.mesh.name = BuildUniqueSubAssetName($"{record.mesh.name}_{ShortHash(payloadHash)}", usedMeshNames);
            payload.renderers.Add(record);
            float local = rendererCount > 0 ? (i + 1f) / rendererCount : 1f;
            status.Report(Lerp(startProgress, Lerp(startProgress, endProgress, 0.82f), local), $"Parsing advanced mesh {i + 1}/{rendererCount}...");
        }

        int boneCount = reader.ReadCount(PayloadReader.MinBoneBytes, "bones");
        for (int i = 0; i < boneCount; i++)
        {
            payload.bones.Add(new NativeMeshPayloadBone
            {
                path = reader.ReadString(),
                localPosition = ReadVector3(reader),
                localRotation = ReadQuaternion(reader),
                localScale = ReadVector3(reader)
            });
        }

        status.Report(Lerp(startProgress, endProgress, 0.88f), $"Parsing advanced mesh bone table ({boneCount})...");
        int authoringPoseBoneCount = reader.ReadCount(PayloadReader.MinBoneBytes, "authoring pose bones");
        for (int i = 0; i < authoringPoseBoneCount; i++)
        {
            payload.authoringPoseBones.Add(new NativeMeshPayloadAuthoringPoseBone
            {
                path = reader.ReadString(),
                localPositionOffset = ReadVector3(reader),
                localRotationDelta = ReadQuaternion(reader),
                localScaleMultiplier = ReadVector3(reader)
            });
        }

        status.Report(endProgress, $"Parsed advanced mesh authoring pose ({authoringPoseBoneCount})...");
        return payload;
    }

    private static PreparedMeshData ReadPreparedMesh(PayloadReader reader)
    {
        var mesh = new PreparedMeshData
        {
            name = reader.ReadString(),
            indexFormat = (IndexFormat)reader.ReadInt32(),
            vertices = reader.ReadBlock<Vector3>(12, "vectors"),
            normals = reader.ReadBlock<Vector3>(12, "vectors"),
            tangents = reader.ReadBlock<Vector4>(16, "vectors"),
            colors = reader.ReadBlock<Color32>(4, "colors"),
            uvs = new List<Vector4>[8]
        };

        for (int channel = 0; channel < 8; channel++)
        {
            mesh.uvs[channel] = new List<Vector4>(reader.ReadBlock<Vector4>(16, "vectors"));
        }

        mesh.boneWeights = ReadBoneWeightArray(reader);
        mesh.bindposes = reader.ReadBlock<Matrix4x4>(64, "bind poses");

        int subMeshCount = reader.ReadCount(PayloadReader.MinSubMeshBytes, "submeshes");
        for (int i = 0; i < subMeshCount; i++)
        {
            mesh.subMeshes.Add(new PreparedSubMeshData
            {
                topology = (MeshTopology)reader.ReadInt32(),
                indices = reader.ReadBlock<int>(4, "indices")
            });
        }

        mesh.bounds = ReadBounds(reader);
        int vertexCount = mesh.vertices.Length;
        int blendShapeCount = reader.ReadCount(PayloadReader.MinBlendShapeBytes, "blendshapes");
        for (int shape = 0; shape < blendShapeCount; shape++)
        {
            var blendShape = new PreparedBlendShapeData
            {
                name = reader.ReadString()
            };
            int frameCount = reader.ReadCount(PayloadReader.MinBlendShapeFrameBytes, "blendshape frames");
            for (int frame = 0; frame < frameCount; frame++)
            {
                blendShape.frames.Add(new PreparedBlendShapeFrameData
                {
                    weight = reader.ReadSingle(),
                    vertices = ReadSparseDeltas(reader, vertexCount),
                    normals = reader.ReadBoolean() ? ReadSparseDeltas(reader, vertexCount) : null,
                    tangents = reader.ReadBoolean() ? ReadSparseDeltas(reader, vertexCount) : null
                });
            }

            mesh.blendShapes.Add(blendShape);
        }

        return mesh;
    }

    internal sealed class NativeMeshPayloadPreparationStatus
    {
        private readonly object gate = new object();
        private float progress;
        private string label = "Preparing advanced mesh payload...";

        public void Report(float progress, string label)
        {
            lock (gate)
            {
                this.progress = Math.Max(0f, Math.Min(1f, progress));
                if (!string.IsNullOrWhiteSpace(label))
                {
                    this.label = label;
                }
            }
        }

        public void Snapshot(out float progress, out string label)
        {
            lock (gate)
            {
                progress = this.progress;
                label = this.label;
            }
        }
    }

    internal sealed class PreparedPayloadAssetData
    {
        public string assetName;
        public int payloadVersion;
        public string sourceFbxPath;
        public string sourceFbxHash;
        public string payloadHash;
        public readonly List<PreparedPayloadRendererData> renderers = new List<PreparedPayloadRendererData>();
        public readonly List<NativeMeshPayloadBone> bones = new List<NativeMeshPayloadBone>();
        public readonly List<NativeMeshPayloadAuthoringPoseBone> authoringPoseBones = new List<NativeMeshPayloadAuthoringPoseBone>();
    }

    internal sealed class PreparedPayloadRendererData
    {
        public string avatarPath;
        public string fbxMeshPath;
        public string meshName;
        public string rendererName;
        public PreparedMeshData mesh;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
        public string rootBonePath;
        public List<string> bonePaths = new List<string>();
    }

    internal sealed class PreparedMeshData
    {
        public string name;
        public IndexFormat indexFormat;
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector4[] tangents;
        public Color32[] colors;
        public List<Vector4>[] uvs;
        public BoneWeight[] boneWeights;
        public Matrix4x4[] bindposes;
        public readonly List<PreparedSubMeshData> subMeshes = new List<PreparedSubMeshData>();
        public Bounds bounds;
        public readonly List<PreparedBlendShapeData> blendShapes = new List<PreparedBlendShapeData>();
    }

    internal sealed class PreparedSubMeshData
    {
        public MeshTopology topology;
        public int[] indices;
    }

    internal sealed class PreparedBlendShapeData
    {
        public string name;
        public readonly List<PreparedBlendShapeFrameData> frames = new List<PreparedBlendShapeFrameData>();
    }

    internal sealed class PreparedBlendShapeFrameData
    {
        public float weight;
        public SparseDeltas vertices;
        /// <summary>Null when the payload stores no normal deltas for this frame (likewise <see cref="tangents"/>).</summary>
        public SparseDeltas normals;
        public SparseDeltas tangents;

        // Dense copies for previews that read single frames.
        public Vector3[] deltaVertices => vertices?.ToDense();
        public Vector3[] deltaNormals => normals?.ToDense();
        public Vector3[] deltaTangents => tangents?.ToDense();
    }

    /// <summary>
    /// One blendshape delta stream as the payload stores it: the length of the vertex array it describes and the written
    /// (index, delta) pairs, in file order (a repeated index keeps its last delta, as when the array was filled in place).
    /// </summary>
    internal sealed class SparseDeltas
    {
        public int length;
        public SparseDelta[] entries;

        public Vector3[] ToDense()
        {
            var dense = new Vector3[length];
            foreach (var entry in entries) dense[entry.index] = entry.value;
            return dense;
        }
    }

    private static void WriteStringList(BinaryWriter writer, List<string> values)
    {
        writer.Write(values?.Count ?? 0);
        foreach (string value in values ?? new List<string>())
        {
            writer.Write(value ?? "");
        }
    }

    private static List<string> ReadStringList(PayloadReader reader)
    {
        int count = reader.ReadCount(1, "strings");
        var values = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(reader.ReadString());
        }

        return values;
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteVector4(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
        writer.Write(value.w);
    }

    private static Vector4 ReadVector4(BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
        writer.Write(value.w);
    }

    private static Quaternion ReadQuaternion(BinaryReader reader)
    {
        return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    // Arrays of fixed-size records are written and read as one block of bytes: the payload is little-endian, as
    // BinaryWriter writes it, and so is every platform the editor runs on.
    private static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Native mesh payloads need a little-endian editor.");
    }

    // A count, then the records' bytes.
    private static void WriteBlock<T>(BinaryWriter writer, T[] values, int count = -1) where T : struct
    {
        if (count < 0) count = values?.Length ?? 0;
        writer.Write(count);
        if (count == 0) return;
        RequireLittleEndian();
        writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(values, 0, count)));
    }

    private static void WriteVector4List(BinaryWriter writer, List<Vector4> values, ref Vector4[] scratch)
    {
        int count = values?.Count ?? 0;
        if (count > 0)
        {
            if (scratch == null || scratch.Length < count) scratch = new Vector4[count];
            values.CopyTo(scratch, 0);
        }
        WriteBlock(writer, scratch, count);
    }

    // The payload's bone weight: four indices, then four weights.
    [StructLayout(LayoutKind.Sequential)]
    private struct PackedBoneWeight
    {
        public int index0, index1, index2, index3;
        public float weight0, weight1, weight2, weight3;
    }

    private static void WriteBoneWeightArray(BinaryWriter writer, BoneWeight[] values)
    {
        var packed = new PackedBoneWeight[values?.Length ?? 0];
        for (int i = 0; i < packed.Length; i++)
        {
            var value = values[i];
            packed[i] = new PackedBoneWeight
            {
                index0 = value.boneIndex0, index1 = value.boneIndex1, index2 = value.boneIndex2, index3 = value.boneIndex3,
                weight0 = value.weight0, weight1 = value.weight1, weight2 = value.weight2, weight3 = value.weight3
            };
        }
        WriteBlock(writer, packed);
    }

    private static BoneWeight[] ReadBoneWeightArray(PayloadReader reader)
    {
        var packed = reader.ReadBlock<PackedBoneWeight>(32, "bone weights");
        var values = new BoneWeight[packed.Length];
        for (int i = 0; i < packed.Length; i++)
        {
            var value = packed[i];
            values[i] = new BoneWeight
            {
                boneIndex0 = value.index0, boneIndex1 = value.index1, boneIndex2 = value.index2, boneIndex3 = value.index3,
                weight0 = value.weight0, weight1 = value.weight1, weight2 = value.weight2, weight3 = value.weight3
            };
        }

        return values;
    }

    // A sparse delta as the payload stores it: the vertex index, then the delta.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct SparseDelta
    {
        public int index;
        public Vector3 value;
    }

    /// <param name="scratch">Reused between calls; grown to the array's length when needed.</param>
    private static long WriteSparseVector3Array(BinaryWriter writer, Vector3[] values, ref SparseDelta[] scratch, bool exact = false)
    {
        int length = values?.Length ?? 0;
        writer.Write(length);
        if (length == 0)
        {
            writer.Write(0);
            return 8;
        }

        if (scratch == null || scratch.Length < length) scratch = new SparseDelta[length];
        int count = 0;
        for (int i = 0; i < length; i++)
        {
            Vector3 value = values[i];
            if (exact ? value.x == 0f && value.y == 0f && value.z == 0f :
                Mathf.Abs(value.x) <= SparseVectorEpsilon &&
                Mathf.Abs(value.y) <= SparseVectorEpsilon &&
                Mathf.Abs(value.z) <= SparseVectorEpsilon)
            {
                continue;
            }

            scratch[count++] = new SparseDelta { index = i, value = value };
        }

        WriteBlock(writer, scratch, count);
        return EstimateSparseVector3ArrayBytes(count);
    }

    private static long EstimateSparseVector3ArrayBytes(int nonZeroCount)
    {
        return 8L + (long)Mathf.Max(0, nonZeroCount) * 16L;
    }

    private static long EstimateSparseVector3ArrayWorstCaseBytes(int vertexCount)
    {
        return EstimateSparseVector3ArrayBytes(vertexCount);
    }

    private static bool ShouldSerializeBlendShapeNormalTangents(Mesh mesh, string shapeName)
    {
        if (mesh == null || string.IsNullOrWhiteSpace(shapeName))
        {
            return false;
        }

        if (mesh.name?.IndexOf("DynamicNormals", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        string lower = shapeName.ToLowerInvariant();
        return lower.Contains("muscle") || lower.Contains("flex");
    }

    // A sparse array stands for a whole vertex array: its length is bounded by its mesh's vertices (which were read from the
    // payload's own bytes) and the vertex array it stands for is charged to the payload's budget, as when it was expanded.
    private static SparseDeltas ReadSparseDeltas(PayloadReader reader, int vertexCount)
    {
        int length = reader.ReadInt32();
        int nonZeroCount = reader.ReadCount(16, "blendshape deltas");
        if (length < 0 || length > vertexCount || nonZeroCount > length)
        {
            throw new InvalidDataException($"Native mesh payload has a blendshape delta array of {length} for a mesh of {vertexCount} vertices.");
        }

        reader.Expand(length * 12L, "blendshape deltas");
        var deltas = new SparseDeltas { length = length, entries = new SparseDelta[nonZeroCount] };
        reader.ReadBlock(deltas.entries);
        foreach (var entry in deltas.entries)
        {
            if (entry.index < 0 || entry.index >= length)
            {
                throw new InvalidDataException("Native mesh payload has a blendshape delta outside its mesh.");
            }
        }

        return deltas;
    }

    private static void WriteBounds(BinaryWriter writer, Bounds bounds)
    {
        WriteVector3(writer, bounds.center);
        WriteVector3(writer, bounds.size);
    }

    private static Bounds ReadBounds(BinaryReader reader)
    {
        return new Bounds(ReadVector3(reader), ReadVector3(reader));
    }

    /// <summary>
    /// Reads a decoded payload whose counts are untrusted: each count is checked against the bytes left in the payload
    /// before anything is allocated from it, strings against their length prefix, and sparse arrays that expand from a few
    /// bytes against their mesh and <see cref="MaxExpandedBytes"/>. A corrupt or crafted payload fails with
    /// <see cref="InvalidDataException"/> instead of exhausting memory.
    /// </summary>
    private sealed class PayloadReader : BinaryReader
    {
        public const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
        public const int MaxStringBytes = 64 * 1024;
        // The smallest encoding of each record: strings take at least their one-byte length prefix.
        public const int MinBoneBytes = 1 + 12 + 16 + 12;
        public const int MinRendererBytes = 4 + 12 + 16 + 12 + 1 + 4;
        public const int MinSubMeshBytes = 4 + 4;
        public const int MinBlendShapeBytes = 1 + 4;
        public const int MinBlendShapeFrameBytes = 4 + 8 + 1 + 1;

        private long expandedBytes;

        private PayloadReader(MemoryStream stream) : base(stream, Encoding.UTF8) { }

        /// <summary>A reader over the payload's bytes; GZIP payloads are decoded first, within the supported payload size.</summary>
        public static PayloadReader Open(byte[] payloadBytes, bool gzip)
        {
            if (!gzip) return new PayloadReader(new MemoryStream(payloadBytes, 0, payloadBytes.Length, false, true));
            var decoded = new MemoryStream();
            using (var input = new GZipStream(new MemoryStream(payloadBytes, false), System.IO.Compression.CompressionMode.Decompress))
            {
                var buffer = new byte[81920];
                for (int read; (read = input.Read(buffer, 0, buffer.Length)) > 0;)
                {
                    if (decoded.Length + read > MCBCompression.MaxDecodedBytes)
                        throw new InvalidDataException("Native mesh payload expands beyond the supported size.");
                    decoded.Write(buffer, 0, read);
                }
            }

            decoded.Position = 0;
            return new PayloadReader(decoded);
        }

        private long Remaining => BaseStream.Length - BaseStream.Position;

        /// <summary>Reads a count of records taking at least <paramref name="minimumBytes"/> each.</summary>
        public int ReadCount(int minimumBytes, string what)
        {
            int count = ReadInt32();
            if (count < 0 || (long)count * minimumBytes > Remaining)
                throw new InvalidDataException($"Native mesh payload declares {count} {what}, more than it contains.");
            return count;
        }

        /// <summary>Reads a count of records of <paramref name="recordBytes"/> each, then the records as one block.</summary>
        public T[] ReadBlock<T>(int recordBytes, string what) where T : struct
        {
            var values = new T[ReadCount(recordBytes, what)];
            ReadBlock(values);
            return values;
        }

        /// <summary>Fills <paramref name="values"/> with the next bytes of the payload, copied straight from its buffer.</summary>
        public void ReadBlock<T>(T[] values) where T : struct
        {
            Span<byte> target = MemoryMarshal.AsBytes(values.AsSpan());
            if (target.Length == 0) return;
            RequireLittleEndian();
            if (target.Length > Remaining) throw new InvalidDataException("Native mesh payload ends inside an array.");
            var stream = (MemoryStream)BaseStream;
            if (!stream.TryGetBuffer(out ArraySegment<byte> buffer)) throw new InvalidOperationException("The payload buffer is not readable.");
            new ReadOnlySpan<byte>(buffer.Array, buffer.Offset + checked((int)stream.Position), target.Length).CopyTo(target);
            stream.Position += target.Length;
        }

        public void Expand(long bytes, string what)
        {
            expandedBytes += bytes;
            if (expandedBytes > MaxExpandedBytes)
                throw new InvalidDataException($"Native mesh payload expands beyond {MaxExpandedBytes / (1024 * 1024 * 1024)} GB ({what}).");
        }

        public override string ReadString()
        {
            int length;
            try { length = Read7BitEncodedInt(); }
            catch (FormatException ex) { throw new InvalidDataException("Native mesh payload has an invalid string length.", ex); }
            if (length < 0 || length > MaxStringBytes || length > Remaining)
                throw new InvalidDataException($"Native mesh payload has a string of {length} bytes.");
            return length == 0 ? string.Empty : Encoding.UTF8.GetString(ReadBytes(length));
        }
    }

    private sealed class PayloadRendererSource
    {
        public Dictionary<(string shape, int frame), DynamicNormals.NormalFrame> normalFrames;
        public ModelFileSmrPathData entry;
        public SkinnedMeshRenderer renderer;
    }

    private sealed class NativeMeshPayloadBuildMetrics
    {
        public long blendShapeVertexBytes;
        public long blendShapeNormalBytes;
        public long blendShapeTangentBytes;
        public long skippedBlendShapeNormalBytes;
        public long skippedBlendShapeTangentBytes;
    }
}
#endif
