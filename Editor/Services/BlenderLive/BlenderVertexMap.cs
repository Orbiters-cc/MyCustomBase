#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Which Blender vertex each vertex of a Unity mesh is. Unity splits a vertex wherever its UVs or normals differ, so
/// several Unity vertices point to one Blender vertex. Built once per mesh by matching rest positions (nearest within a
/// tolerance, through a spatial grid), trying the axis conventions FBX exports use, and kept per mesh content.
/// </summary>
internal sealed class BlenderVertexMap
{
    // Blender to Unity axis conventions: every signed axis permutation, the usual one first. Unity imports a Blender FBX
    // export (axis conversion on the nodes, not baked) as (-x, y, z) of Blender's local coordinates: X mirrored for
    // Unity's left-handed space. "Bake Axis Conversion" and other exporters end up with other permutations.
    private static readonly Matrix4x4[] Conventions = SignedPermutations();
    private static readonly float[] Scales = { 1f, 0.01f, 100f };
    private static readonly Dictionary<string, BlenderVertexMap> Cache = new Dictionary<string, BlenderVertexMap>(StringComparer.Ordinal);

    /// <summary>For each Unity vertex, the Blender vertex it is.</summary>
    public readonly int[] BlenderIndex;
    /// <summary>Blender's positions to Unity's: the axis convention and unit scale that matched.</summary>
    public readonly Matrix4x4 Conversion;
    public readonly int BlenderVertexCount;

    private BlenderVertexMap(int[] blenderIndex, Matrix4x4 conversion, int blenderVertexCount)
    {
        BlenderIndex = blenderIndex;
        Conversion = conversion;
        BlenderVertexCount = blenderVertexCount;
    }

    /// <summary>The map of this mesh (by its content) to these rest positions, from the cache when they did not change.</summary>
    public static BlenderVertexMap For(Mesh mesh, float[] blenderRest, out string error)
    {
        error = null;
        var unity = mesh.vertices;
        string key = mesh.GetInstanceID() + ":" + Hash(unity) + ":" + Hash(blenderRest);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var map = Build(unity, blenderRest, out error);
        if (map != null) Cache[key] = map;
        return map;
    }

    /// <summary>Matches every Unity vertex to a Blender vertex; null (with the reason) when the meshes do not correspond.</summary>
    public static BlenderVertexMap Build(Vector3[] unity, float[] blenderRest, out string error)
    {
        error = null;
        int blenderCount = blenderRest.Length / 3;
        if (unity.Length == 0 || blenderCount == 0)
        {
            error = "the mesh has no vertices";
            return null;
        }
        if (blenderCount > unity.Length)
        {
            error = "Blender's mesh has more vertices (" + blenderCount + ") than Unity's (" + unity.Length + ")";
            return null;
        }

        var bounds = BoundsOf(unity);
        var blenderBounds = BoundsOf(Convert(blenderRest, Matrix4x4.identity));
        float tolerance = Mathf.Max(1e-6f, bounds.size.magnitude * 1e-4f);
        float slack = tolerance * 50f + bounds.size.magnitude * 0.01f;
        int bestMatched = -1, bestOrder = -1;
        int[] best = null;
        Matrix4x4 bestConversion = Matrix4x4.identity;
        foreach (float scale in Scales)
        {
            foreach (var convention in Conventions)
            {
                var conversion = Matrix4x4.Scale(Vector3.one * scale) * convention;
                // The bounds first (exact for these matrices): a wrong convention or unit is usually far off.
                var other = Transformed(blenderBounds, conversion);
                if ((other.size - bounds.size).magnitude > slack || (other.center - bounds.center).magnitude > slack) continue;
                var converted = Convert(blenderRest, conversion);
                var map = Match(unity, converted, tolerance, out int matched);
                // A symmetric body matches its mirror image too: between full matches, the one keeping Blender's vertex
                // order (FBX export and import keep it, apart from the splits) is the real one.
                int order = matched == unity.Length ? InOrder(map) : -1;
                if (matched > bestMatched || matched == bestMatched && order > bestOrder)
                {
                    bestMatched = matched;
                    bestOrder = order;
                    best = map;
                    bestConversion = conversion;
                }
            }
        }

        if (best == null || bestMatched < unity.Length)
        {
            error = best == null
                ? "the meshes do not line up (another mesh, or moved in Blender)"
                : (unity.Length - bestMatched) + " of " + unity.Length + " vertices have no Blender vertex at their place";
            return null;
        }
        return new BlenderVertexMap(best, bestConversion, blenderCount);
    }

    // How many neighbouring Unity vertices point to neighbouring (or the same) Blender vertices.
    private static int InOrder(int[] map)
    {
        int count = 0;
        for (int i = 1; i < map.Length; i++)
        {
            int step = map[i] - map[i - 1];
            if (step == 0 || step == 1) count++;
        }
        return count;
    }

    /// <summary>Blender's current positions as this mesh's vertices.</summary>
    public bool Apply(float[] blenderPositions, Vector3[] into)
    {
        if (blenderPositions.Length != BlenderVertexCount * 3 || into.Length != BlenderIndex.Length) return false;
        for (int i = 0; i < into.Length; i++)
        {
            int b = BlenderIndex[i] * 3;
            into[i] = Conversion.MultiplyPoint3x4(new Vector3(blenderPositions[b], blenderPositions[b + 1], blenderPositions[b + 2]));
        }
        return true;
    }

    private static int[] Match(Vector3[] unity, Vector3[] blender, float tolerance, out int matched)
    {
        float cell = tolerance * 4f;
        var grid = new Dictionary<Vector3Int, List<int>>();
        for (int i = 0; i < blender.Length; i++)
        {
            var c = Cell(blender[i], cell);
            if (!grid.TryGetValue(c, out var list)) grid[c] = list = new List<int>(1);
            list.Add(i);
        }

        var map = new int[unity.Length];
        matched = 0;
        float limit = tolerance * tolerance;
        for (int i = 0; i < unity.Length; i++)
        {
            map[i] = -1;
            var c = Cell(unity[i], cell);
            float nearest = limit;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (!grid.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var list)) continue;
                foreach (int candidate in list)
                {
                    float distance = (blender[candidate] - unity[i]).sqrMagnitude;
                    if (distance > nearest) continue;
                    nearest = distance;
                    map[i] = candidate;
                }
            }
            if (map[i] >= 0) matched++;
        }
        return map;
    }

    private static Vector3Int Cell(Vector3 p, float size) =>
        new Vector3Int(Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.y / size), Mathf.FloorToInt(p.z / size));

    private static Vector3[] Convert(float[] positions, Matrix4x4 conversion)
    {
        var result = new Vector3[positions.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = conversion.MultiplyPoint3x4(new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]));
        return result;
    }

    private static Bounds BoundsOf(Vector3[] points)
    {
        var bounds = new Bounds(points[0], Vector3.zero);
        for (int i = 1; i < points.Length; i++) bounds.Encapsulate(points[i]);
        return bounds;
    }

    private static Bounds Transformed(Bounds bounds, Matrix4x4 conversion)
    {
        var result = new Bounds(conversion.MultiplyPoint3x4(bounds.min), Vector3.zero);
        result.Encapsulate(conversion.MultiplyPoint3x4(bounds.max));
        return result;
    }

    private static Matrix4x4[] SignedPermutations()
    {
        var axes = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
        var result = new List<Matrix4x4>();
        foreach (var axis in axes)
        {
            for (int signs = 0; signs < 8; signs++)
            {
                var m = Matrix4x4.zero;
                m[3, 3] = 1f;
                for (int row = 0; row < 3; row++) m[row, axis[row]] = (signs & (1 << row)) != 0 ? -1f : 1f;
                result.Add(m);
            }
        }
        // Unity's import of a Blender export first, then no conversion at all (a stable order: a flat mesh matches
        // several conventions equally, and the earlier one is kept).
        var usual = Matrix4x4.Scale(new Vector3(-1, 1, 1));
        return result.OrderBy(m => Rank(m, usual)).ToArray();
    }

    private static int Rank(Matrix4x4 m, Matrix4x4 usual) => m == usual ? 0 : m == Matrix4x4.identity ? 1 : 2;

    private static string Hash(Vector3[] values)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            foreach (var v in values)
            {
                hash = (hash ^ v.x.GetHashCode()) * 1099511628211L;
                hash = (hash ^ v.y.GetHashCode()) * 1099511628211L;
                hash = (hash ^ v.z.GetHashCode()) * 1099511628211L;
            }
            return values.Length + "-" + hash.ToString("x");
        }
    }

    private static string Hash(float[] values)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            foreach (float v in values) hash = (hash ^ v.GetHashCode()) * 1099511628211L;
            return values.Length + "-" + hash.ToString("x");
        }
    }
}
#endif
