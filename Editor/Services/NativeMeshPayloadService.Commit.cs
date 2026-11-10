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

// Unity meshes and the payload asset made from prepared payload data.
public static partial class NativeMeshPayloadService
{
    private static IEnumerator CommitPreparedPayloadAssetCoroutine(
        string assetPath,
        PreparedPayloadAssetData prepared,
        string payloadHash,
        string payloadCompression,
        Action<float, string> reportProgress,
        Action<NativeMeshPayloadAsset> completed,
        bool yieldToEditor = true)
    {
        string normalizedPath = MCBUtils.ToUnityPath(assetPath);
        EnsureAssetFolder(Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/'));

        var existing = LoadPayloadAsset(normalizedPath);
        if (CachedPayloadMatches(existing, payloadHash, payloadCompression))
        {
            reportProgress?.Invoke(1f, "Loaded cached advanced mesh...");
            completed?.Invoke(existing);
            yield break;
        }

        DeleteAssetFile(normalizedPath);

        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        payload.name = prepared.assetName;
        payload.payloadVersion = prepared.payloadVersion;
        payload.sourceFbxPath = prepared.sourceFbxPath;
        payload.sourceFbxHash = prepared.sourceFbxHash;
        payload.payloadHash = prepared.payloadHash;
        payload.payloadCompression = NormalizePayloadCompression(payloadCompression);
        payload.bones.AddRange(prepared.bones);
        payload.authoringPoseBones.AddRange(prepared.authoringPoseBones);

        int rendererCount = prepared.renderers.Count;
        for (int i = 0; i < rendererCount; i++)
        {
            var preparedRenderer = prepared.renderers[i];
            var renderer = new NativeMeshPayloadRenderer
            {
                avatarPath = preparedRenderer.avatarPath,
                fbxMeshPath = preparedRenderer.fbxMeshPath,
                meshName = preparedRenderer.meshName,
                rendererName = preparedRenderer.rendererName,
                localPosition = preparedRenderer.localPosition,
                localRotation = preparedRenderer.localRotation,
                localScale = preparedRenderer.localScale,
                rootBonePath = preparedRenderer.rootBonePath,
                bonePaths = preparedRenderer.bonePaths ?? new List<string>()
            };
            var createMesh = CreateMeshFromPreparedDataCoroutine(preparedRenderer.mesh,
                mesh => renderer.mesh = mesh,
                progress => reportProgress?.Invoke((i + progress) / Math.Max(1, rendererCount) * 0.72f,
                    $"Creating Unity mesh {i + 1}/{rendererCount}..."), yieldToEditor);
            while (createMesh.MoveNext()) yield return createMesh.Current;
            payload.renderers.Add(renderer);
            reportProgress?.Invoke(rendererCount > 0 ? (i + 1f) / rendererCount * 0.72f : 0.72f, $"Creating Unity mesh {i + 1}/{rendererCount}...");
            yield return null;
        }

        reportProgress?.Invoke(0.78f, "Registering advanced mesh cache...");
        bool assetEditingStarted = false;
        try
        {
            AssetDatabase.StartAssetEditing();
            assetEditingStarted = true;
            AssetDatabase.CreateAsset(payload, normalizedPath);
            for (int i = 0; i < payload.renderers.Count; i++)
            {
                var renderer = payload.renderers[i];
                if (renderer?.mesh != null)
                {
                    AssetDatabase.AddObjectToAsset(renderer.mesh, payload);
                }
            }

            AssetDatabase.SetMainObject(payload, normalizedPath);
            EditorUtility.SetDirty(payload);
        }
        finally
        {
            if (assetEditingStarted)
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        reportProgress?.Invoke(0.94f, "Saving advanced mesh cache...");
        AssetDatabase.SaveAssetIfDirty(payload);
        reportProgress?.Invoke(1f, "Advanced mesh cache ready...");
        completed?.Invoke(payload);
    }

    internal static Mesh CreateMeshFromPreparedData(PreparedMeshData data)
    {
        Mesh result = null;
        var create = CreateMeshFromPreparedDataCoroutine(data, mesh => result = mesh, null, false);
        while (create.MoveNext()) { }
        return result;
    }

    private static IEnumerator CreateMeshFromPreparedDataCoroutine(PreparedMeshData data,
        Action<Mesh> completed, Action<float> progress, bool yieldToEditor = true)
    {
        var mesh = new Mesh();
        mesh.name = data.name;
        mesh.indexFormat = data.indexFormat;
        mesh.vertices = data.vertices ?? Array.Empty<Vector3>();

        int vertexCount = mesh.vertexCount;
        if (data.normals != null && data.normals.Length == vertexCount) mesh.normals = data.normals;
        if (data.tangents != null && data.tangents.Length == vertexCount) mesh.tangents = data.tangents;
        if (data.colors != null && data.colors.Length == vertexCount) mesh.colors32 = data.colors;

        for (int channel = 0; channel < 8; channel++)
        {
            var uvs = data.uvs != null && channel < data.uvs.Length ? data.uvs[channel] : null;
            if (uvs != null && uvs.Count == vertexCount)
            {
                mesh.SetUVs(channel, uvs);
            }
        }

        if (data.boneWeights != null && data.boneWeights.Length == vertexCount) mesh.boneWeights = data.boneWeights;
        if (data.bindposes != null && data.bindposes.Length > 0) mesh.bindposes = data.bindposes;

        mesh.subMeshCount = data.subMeshes?.Count ?? 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            var subMesh = data.subMeshes[i];
            mesh.SetIndices(subMesh.indices ?? Array.Empty<int>(), subMesh.topology, i, false);
        }

        mesh.bounds = data.bounds;
        int frameCount = data.blendShapes?.Sum(shape => shape.frames?.Count ?? 0) ?? 0;
        int framesAdded = 0;
        // Frames stay sparse until here: each is spread into the same three vertex-sized arrays for Unity to copy, which
        // are then cleared at the indices it wrote. Peak memory is the sparse deltas plus three arrays per mesh.
        Vector3[] scratchVertices = null, scratchNormals = null, scratchTangents = null;
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var call = new System.Diagnostics.Stopwatch();
        double maximumCallMs = 0;
        bool finished = false;
        try
        {
            foreach (var blendShape in data.blendShapes ?? new List<PreparedBlendShapeData>())
            {
                foreach (var frame in blendShape.frames ?? new List<PreparedBlendShapeFrameData>())
                {
                    Vector3[] deltaVertices = frame.vertices.Spread(ref scratchVertices, vertexCount);
                    Vector3[] deltaNormals = frame.normals.Spread(ref scratchNormals, vertexCount);
                    Vector3[] deltaTangents = frame.tangents.Spread(ref scratchTangents, vertexCount);
                    call.Restart();
                    mesh.AddBlendShapeFrame(blendShape.name, frame.weight, deltaVertices, deltaNormals, deltaTangents);
                    maximumCallMs = Math.Max(maximumCallMs, call.Elapsed.TotalMilliseconds);
                    frame.vertices.Unspread(deltaVertices);
                    frame.normals.Unspread(deltaNormals);
                    frame.tangents.Unspread(deltaTangents);
                    framesAdded++;
                    if (yieldToEditor && budget.Elapsed.TotalMilliseconds >= 8)
                    {
                        progress?.Invoke(framesAdded / (float)Math.Max(1, frameCount));
                        yield return null;
                        budget.Restart();
                    }
                }
            }
            progress?.Invoke(1f);
            MCBLogger.Log($"[NativeMeshPayloadProfile] Created mesh={data.name} frames={framesAdded} maxBlendShapeCallMs={maximumCallMs:F1} frameBudgeted={yieldToEditor}");
            completed?.Invoke(mesh);
            finished = true;
        }
        finally
        {
            if (!finished) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    // The dense array AddBlendShapeFrame takes: the mesh's scratch array filled at the written indices (Unity copies it), or
    // a new array when the stream's length is not the mesh's, passed on as the payload declares it.
    private static Vector3[] Spread(this SparseDeltas deltas, ref Vector3[] scratch, int vertexCount)
    {
        if (deltas == null) return null;
        if (deltas.length != vertexCount) return deltas.ToDense();
        if (scratch == null) scratch = new Vector3[vertexCount];
        foreach (var entry in deltas.entries) scratch[entry.index] = entry.value;
        return scratch;
    }

    // Zeroes what Spread wrote, so the scratch array is all zeros again for the next frame.
    private static void Unspread(this SparseDeltas deltas, Vector3[] spread)
    {
        if (deltas == null || spread == null) return;
        foreach (var entry in deltas.entries) spread[entry.index] = default;
    }

    private static float Lerp(float from, float to, float value)
    {
        return from + (to - from) * Math.Max(0f, Math.Min(1f, value));
    }
}
#endif
