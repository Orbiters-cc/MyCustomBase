#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Blender's edits on one skinned mesh of the avatar, live: a temporary copy of the mesh (never saved) takes the streamed
/// positions, with normals recomputed and smoothed across Unity's seam splits; <see cref="Revert"/> puts the original
/// mesh back. Nothing is written to assets.
/// </summary>
internal sealed class BlenderLivePreview
{
    private readonly SkinnedMeshRenderer renderer;
    private readonly Mesh original;
    private readonly BlenderVertexMap map;
    private readonly Vector3[] positions;
    private readonly Vector3[] originalNormals;
    private readonly int[] triangles;
    // For each vertex, the vertices whose normal it shares: the same Blender vertex on the same side of any hard edge.
    private readonly int[][] smoothWith;
    private Mesh live;

    public BlenderLivePreview(SkinnedMeshRenderer renderer, BlenderVertexMap map)
    {
        this.renderer = renderer;
        this.map = map;
        // A live mesh left on the renderer (an older preview's) stands for its original.
        original = BlenderLiveMeshGuard.OriginalOf(renderer.sharedMesh) ?? renderer.sharedMesh;
        positions = original.vertices;
        originalNormals = original.normals;
        triangles = original.triangles;
        smoothWith = SmoothingGroups();
    }

    private int[][] SmoothingGroups()
    {
        var byBlender = new Dictionary<int, List<int>>();
        for (int i = 0; i < positions.Length; i++)
        {
            int key = map.BlenderIndex[i];
            if (!byBlender.TryGetValue(key, out var list)) byBlender[key] = list = new List<int>(2);
            list.Add(i);
        }
        bool hadNormals = originalNormals != null && originalNormals.Length == positions.Length;
        var result = new int[positions.Length][];
        foreach (var group in byBlender.Values)
        {
            foreach (int i in group)
            {
                var shared = new List<int>(group.Count);
                // Same side of a hard edge: the original normals agreed (within about 8 degrees).
                foreach (int j in group)
                    if (i == j || !hadNormals || Vector3.Dot(originalNormals[i], originalNormals[j]) > 0.99f) shared.Add(j);
                result[i] = shared.ToArray();
            }
        }
        return result;
    }

    public SkinnedMeshRenderer Renderer => renderer;
    public Mesh Original => original;
    public bool Active => live != null && renderer != null && renderer.sharedMesh == live;

    /// <summary>
    /// Shows Blender's current positions; false when they do not fit this mesh (another vertex count). Positions of a shape
    /// key (<paramref name="shapeKey"/>) are the base plus that key: the blendshape of that name comes off them, so it shows
    /// once, as Blender has it, at its full weight.
    /// </summary>
    public bool Apply(float[] blenderPositions, string shapeKey = null)
    {
        if (renderer == null || original == null || !map.Apply(blenderPositions, positions)) return false;
        var delta = ShapeDelta(shapeKey);
        if (delta != null)
            for (int i = 0; i < positions.Length; i++) positions[i] -= delta[i];
        if (live == null)
        {
            live = Object.Instantiate(original);
            live.name = original.name + " (Blender live)";
            live.hideFlags = HideFlags.HideAndDontSave;
            BlenderLiveMeshGuard.Shown(live, original);
        }
        live.vertices = positions;
        live.normals = SmoothNormals(positions);
        live.RecalculateBounds();
        if (renderer.sharedMesh != live) renderer.sharedMesh = live;
        return true;
    }

    // The full-weight offsets of the original mesh's blendshape of that name (read once per name), or null.
    private readonly Dictionary<string, Vector3[]> shapeDeltas = new Dictionary<string, Vector3[]>();

    private Vector3[] ShapeDelta(string shapeKey)
    {
        if (string.IsNullOrEmpty(shapeKey)) return null;
        if (shapeDeltas.TryGetValue(shapeKey, out var known)) return known;
        int index = original.GetBlendShapeIndex(shapeKey);
        Vector3[] delta = null;
        if (index >= 0)
        {
            int frame = original.GetBlendShapeFrameCount(index) - 1;
            delta = new Vector3[original.vertexCount];
            original.GetBlendShapeFrameVertices(index, frame, delta, new Vector3[original.vertexCount], new Vector3[original.vertexCount]);
        }
        shapeDeltas[shapeKey] = delta;
        return delta;
    }

    /// <summary>The original mesh again; the temporary copy is destroyed.</summary>
    public void Revert()
    {
        if (renderer != null && renderer.sharedMesh == live) renderer.sharedMesh = original;
        BlenderLiveMeshGuard.Released(live);
        if (live != null) Object.DestroyImmediate(live);
        live = null;
    }

    // Face normals summed per Blender vertex, so a seam where Unity split a vertex for its UVs stays smooth; a vertex that
    // had its own normal (a hard edge) keeps its side's normal.
    private Vector3[] SmoothNormals(Vector3[] vertices)
    {
        var perVertex = new Vector3[vertices.Length];
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            var face = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            perVertex[a] += face;
            perVertex[b] += face;
            perVertex[c] += face;
        }

        var normals = new Vector3[vertices.Length];
        bool hadNormals = originalNormals != null && originalNormals.Length == vertices.Length;
        for (int i = 0; i < vertices.Length; i++)
        {
            var sum = Vector3.zero;
            foreach (int j in smoothWith[i]) sum += perVertex[j];
            // By hand: small faces (millimetres in metres) sum below Vector3.normalized's threshold, which gives zero.
            normals[i] = sum.sqrMagnitude > 1e-20f ? sum / Mathf.Sqrt(sum.sqrMagnitude) : hadNormals ? originalNormals[i] : Vector3.up;
        }
        return normals;
    }
}
#endif
