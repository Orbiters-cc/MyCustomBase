using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEngine;

// The comparison measures every part against the other side's union in one call per side (MCBF-37): that is only
// right if a point's distance does not depend on the other points of the query.
public sealed class VersionMeshComparisonUnionTests
{
    private static (Vector3[] points, int[] triangles) Sheet(int n, float z)
    {
        var points = new List<Vector3>();
        var triangles = new List<int>();
        for (int y = 0; y <= n; y++)
        for (int x = 0; x <= n; x++)
            points.Add(new Vector3(x / (float)n, y / (float)n, z + 0.02f * Mathf.Sin(x + y)));
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * (n + 1) + x;
            triangles.AddRange(new[] { i, i + 1, i + n + 1, i + 1, i + n + 2, i + n + 1 });
        }
        return (points.ToArray(), triangles.ToArray());
    }

    [Test]
    public void OneQueryForEveryPartEqualsOneQueryPerPart()
    {
        var union = Sheet(24, 0f);
        var partA = Enumerable.Range(0, 300).Select(i => new Vector3((i % 17) / 17f, (i % 23) / 23f, 0.01f * (i % 5))).ToArray();
        var partB = Enumerable.Range(0, 120).Select(i => new Vector3(0.3f + (i % 7) / 30f, (i % 11) / 11f, -0.04f)).ToArray();
        const float size = 1.5f;

        var alone = MeshComparison.SurfaceDistances(union.points, union.triangles, partA, size)
            .Concat(MeshComparison.SurfaceDistances(union.points, union.triangles, partB, size)).ToArray();
        var together = MeshComparison.SurfaceDistances(union.points, union.triangles, partA.Concat(partB).ToArray(), size);

        Assert.That(together, Is.EqualTo(alone));
    }
}
