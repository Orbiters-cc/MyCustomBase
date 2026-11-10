using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEngine;

// The comparison measures every part against the other side's union in one call per side and splits the distances
// back per part (MCBF-37); it remembers its reference offers until the sources or the previous version change (MCBF-45).
public sealed class VersionMeshComparisonUnionTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static (Vector3[] points, int[] triangles) Sheet(int n, Vector3 offset, float wave)
    {
        var points = new List<Vector3>();
        var triangles = new List<int>();
        for (int y = 0; y <= n; y++)
        for (int x = 0; x <= n; x++)
            points.Add(offset + new Vector3(x / (float)n, y / (float)n, wave * Mathf.Sin(x + y)));
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * (n + 1) + x;
            triangles.AddRange(new[] { i, i + 1, i + n + 1, i + 1, i + n + 2, i + n + 1 });
        }
        return (points.ToArray(), triangles.ToArray());
    }

    private static MeshSource Mesh((Vector3[] points, int[] triangles) sheet) => new MeshSource
    {
        Points = sheet.points,
        Vertices = sheet.points,
        Triangles = sheet.triangles,
        SubMeshes = new[] { sheet.triangles }
    };

    private static VersionMeshComparison Comparison(params (string key, MeshSource original, MeshSource after, MeshSource previous)[] parts)
    {
        // Built without its constructor: these tests feed the sources the loader would have read.
        var comparison = (VersionMeshComparison)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(VersionMeshComparison));
        var type = typeof(VersionMeshComparison);
        var sourcesType = type.GetNestedType("PartSources", Any);
        var sources = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sourcesType));
        foreach (var part in parts)
        {
            var source = Activator.CreateInstance(sourcesType, true);
            sourcesType.GetField("Key").SetValue(source, part.key);
            sourcesType.GetField("Name").SetValue(source, part.key);
            sourcesType.GetField("Original").SetValue(source, part.original);
            sourcesType.GetField("After").SetValue(source, part.after);
            sourcesType.GetField("Previous").SetValue(source, part.previous);
            foreach (string weights in new[] { "Weights", "AfterWeights", "PreviousWeights" })
                sourcesType.GetField(weights).SetValue(source, new Dictionary<string, float>(StringComparer.Ordinal));
            foreach (string materials in new[] { "NowMaterials", "AfterMaterials", "OriginalMaterials", "PreviousMaterials" })
                sourcesType.GetField(materials).SetValue(source, new Material[0]);
            sources.Add(source);
        }
        type.GetField("sources", Any).SetValue(comparison, sources);
        type.GetField("offered", Any).SetValue(comparison, new Dictionary<CompareReference, bool>());
        return comparison;
    }

    private static List<ComparedPart> Compare(VersionMeshComparison comparison, CompareReference reference) =>
        (List<ComparedPart>)typeof(VersionMeshComparison).GetMethod("Compare", Any).Invoke(comparison, new object[] { reference });

    [Test]
    public void OneQueryForEveryPartEqualsOneQueryPerPart()
    {
        var union = Sheet(24, Vector3.zero, 0.02f);
        var partA = Enumerable.Range(0, 300).Select(i => new Vector3((i % 17) / 17f, (i % 23) / 23f, 0.01f * (i % 5))).ToArray();
        var partB = Enumerable.Range(0, 120).Select(i => new Vector3(0.3f + (i % 7) / 30f, (i % 11) / 11f, -0.04f)).ToArray();
        const float size = 1.5f;

        var alone = MeshComparison.SurfaceDistances(union.points, union.triangles, partA, size)
            .Concat(MeshComparison.SurfaceDistances(union.points, union.triangles, partB, size)).ToArray();
        var together = MeshComparison.SurfaceDistances(union.points, union.triangles, partA.Concat(partB).ToArray(), size);

        Assert.That(together, Is.EqualTo(alone));
    }

    // A version that removes one piece (the layout changes) and re-exports the two others with new topology: each part's
    // distances are those of its own points measured against the other side's whole union.
    [Test]
    public void ALayoutChangeMeasuresEachPartAgainstTheOtherSidesUnion()
    {
        var bodyBefore = Sheet(10, Vector3.zero, 0.01f);
        var bodyAfter = Sheet(13, new Vector3(0f, 0f, 0.03f), 0.02f);
        var tail = Sheet(6, new Vector3(1.2f, 0f, 0f), 0f);
        var headBefore = Sheet(8, new Vector3(0f, 1.3f, 0f), 0f);
        var headAfter = Sheet(9, new Vector3(0f, 1.3f, 0.05f), 0.01f);
        var comparison = Comparison(
            ("body", Mesh(bodyBefore), Mesh(bodyAfter), null),
            ("tail", Mesh(tail), null, null),
            ("head", Mesh(headBefore), Mesh(headAfter), null));

        var parts = Compare(comparison, CompareReference.Original).ToDictionary(p => p.Key);
        Assert.That(parts["tail"].Change, Is.EqualTo(PartChange.Removed));

        // The size the comparison measured with (its bounds of both sides), and its "moved" floor for new topology.
        float size = Mathf.Max(comparison.Bounds.size.magnitude, 1e-4f);
        float moved = Mathf.Max(Mathf.Max(size * 2e-5f, 1e-6f), size * 5e-4f);
        float[] Floor(float[] distances) => distances.Select(d => d <= moved ? 0f : d).ToArray();

        var beforeUnion = Union(bodyBefore, tail, headBefore);
        var afterUnion = Union(bodyAfter, headAfter);
        foreach (var (key, before, after) in new[] { ("body", bodyBefore, bodyAfter), ("head", headBefore, headAfter) })
        {
            Assert.That(parts[key].AfterDistances,
                Is.EqualTo(Floor(MeshComparison.SurfaceDistances(beforeUnion.points, beforeUnion.triangles, after.points, size))), key + " after");
            Assert.That(parts[key].BeforeDistances,
                Is.EqualTo(Floor(MeshComparison.SurfaceDistances(afterUnion.points, afterUnion.triangles, before.points, size))), key + " before");
        }
    }

    private static (Vector3[] points, int[] triangles) Union(params (Vector3[] points, int[] triangles)[] pieces)
    {
        var points = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var piece in pieces)
        {
            int offset = points.Count;
            points.AddRange(piece.points);
            triangles.AddRange(piece.triangles.Select(i => i + offset));
        }
        return (points.ToArray(), triangles.ToArray());
    }

    [Test]
    public void OffersAreRememberedUntilThePreviousVersionIsDropped()
    {
        var original = Mesh(Sheet(6, Vector3.zero, 0f));
        var previous = Mesh(Sheet(6, new Vector3(0f, 0f, 0.2f), 0f));
        var after = Mesh(Sheet(7, Vector3.zero, 0.01f));
        var comparison = Comparison(("body", original, after, previous));
        typeof(VersionMeshComparison).GetProperty("PreviousVersion", Any).SetValue(comparison, new CustomBaseVersion { version = "1.0.0" });

        Assert.That(comparison.Offers(CompareReference.Original), Is.True, "the version before differs from the original");
        Assert.That(comparison.Offers(CompareReference.Original), Is.True);

        typeof(VersionMeshComparison).GetMethod("DropPrevious", Any).Invoke(comparison, new object[] { "gone", null });
        Assert.That(comparison.Offers(CompareReference.Original), Is.False, "no version before: answered again, not from memory");
    }

    [Test]
    public void NormalsAreTheSameWhenTheWeldMapIsReused()
    {
        var sheet = Sheet(9, Vector3.zero, 0.05f);
        var once = Mesh(sheet);
        var first = once.Normals(sheet.points);
        var again = once.Normals(sheet.points);
        var fresh = Mesh(sheet).Normals(sheet.points);
        Assert.That(again, Is.EqualTo(first));
        Assert.That(fresh, Is.EqualTo(first));
    }
}
