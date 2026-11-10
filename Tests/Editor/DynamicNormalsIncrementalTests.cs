using System;
using System.Collections.Generic;
using System.Linq;
using MCBEditorUtils;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Dynamic normals recomputed around moved vertices only (MCBS-29) against the full re-bake of MCB 1.12.24, kept below as
/// the reference: they may differ by float rounding only.
/// </summary>
public sealed class DynamicNormalsIncrementalTests
{
    const float Tolerance = 1e-4f;
    readonly List<Object> created = new List<Object>();

    [TearDown] public void TearDown()
    {
        foreach (var obj in created) if (obj != null) Object.DestroyImmediate(obj);
        created.Clear();
    }

    // A bent grid of n × n quads skinned to two bones, with a UV seam down the middle (duplicated vertices sharing their
    // position and normal) and blendshapes that move a patch across the seam, move one vertex, or only change normals.
    SkinnedMeshRenderer Rig(int n)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var weights = new List<BoneWeight>();
        var index = new int[n + 1, n + 2];
        int seam = n / 2;
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                for (int side = 0; side < (x == seam ? 2 : 1); side++)
                {
                    index[y, x + (x > seam || side == 1 ? 1 : 0)] = positions.Count;
                    float fx = x / (float)n, fy = y / (float)n;
                    positions.Add(new Vector3(fx, fy, .2f * Mathf.Sin(fx * 3f) * Mathf.Cos(fy * 2f)));
                    uvs.Add(new Vector2(side == 1 ? fx + .5f : fx, fy));
                    weights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1 - fy, boneIndex1 = 1, weight1 = fy });
                }
            }
        var triangles = new List<int>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int cx = x + (x >= seam ? 1 : 0), nx = x + 1 + (x + 1 > seam ? 1 : 0);
                if (x + 1 == seam) nx = seam; // the left side of the seam column
                int a = index[y, cx], b = index[y, nx], c = index[y + 1, cx], d = index[y + 1, nx];
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }

        var mesh = new Mesh { name = "Body" };
        created.Add(mesh);
        mesh.SetVertices(positions);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        // Smooth across the seam: both copies get the mean normal, as an importer writes them.
        var normals = mesh.normals;
        var byPosition = Enumerable.Range(0, positions.Count).GroupBy(i => positions[i]).Where(g => g.Count() > 1);
        foreach (var group in byPosition)
        {
            var mean = group.Aggregate(Vector3.zero, (sum, i) => sum + normals[i]).normalized;
            foreach (int i in group) normals[i] = mean;
        }
        mesh.normals = normals;
        mesh.RecalculateTangents();
        mesh.boneWeights = weights.ToArray();

        var root = new GameObject("Rig");
        created.Add(root);
        var hips = new GameObject("Hips").transform; hips.SetParent(root.transform, false);
        var chest = new GameObject("Chest").transform; chest.SetParent(hips, false);
        chest.localPosition = new Vector3(0, .5f, 0);
        chest.localRotation = Quaternion.Euler(10, 0, 5);
        mesh.bindposes = new[] { hips.worldToLocalMatrix, chest.worldToLocalMatrix };

        int count = positions.Count;
        var delta = new Vector3[count];
        var deltaNormals = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 p = positions[i];
            float r = Vector2.Distance(new Vector2(p.x, p.y), new Vector2(.5f, .5f));
            if (r < .25f) delta[i] = new Vector3(.01f * Mathf.Cos(p.y * 7), 0, .08f * (1 - r / .25f));
        }
        mesh.AddBlendShapeFrame("muscle bulge", 50, delta.Select(d => d * .5f).ToArray(), null, null);
        mesh.AddBlendShapeFrame("muscle bulge", 100, delta, null, null);
        var one = new Vector3[count];
        one[index[1, 1]] = new Vector3(0, 0, .05f);
        mesh.AddBlendShapeFrame("flex one", 100, one, null, null);
        for (int y = 0; y <= n; y++) deltaNormals[index[y, seam]] = new Vector3(.1f, 0, 0);
        mesh.AddBlendShapeFrame("flex normals only", 100, new Vector3[count], deltaNormals, null);
        mesh.AddBlendShapeFrame("muscle nothing", 100, new Vector3[count], null, null);

        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.bones = new[] { hips, chest };
        renderer.rootBone = hips;
        return renderer;
    }

    [TestCase(8, false)]
    [TestCase(31, true)]
    public void MatchesTheFullRebake(int n, bool legsApart)
    {
        var renderer = Rig(n);
        var shapes = new[] { "muscle bulge", "flex one", "flex normals only", "muscle nothing" };
        var translations = legsApart ? new Dictionary<string, Vector3> { { "Chest", new Vector3(.1f, 0, 0) } } : null;
        var expected = LegacyCapture(renderer, shapes, translations);
        var actual = DynamicNormals.CaptureNormalFrames(renderer, shapes, translations);
        CollectionAssert.AreEquivalent(expected.Keys, actual.Keys);
        foreach (var key in expected.Keys)
        {
            float normalError = 0, tangentError = 0;
            for (int i = 0; i < renderer.sharedMesh.vertexCount; i++)
            {
                normalError = Mathf.Max(normalError, (expected[key].normals[i] - actual[key].normals[i]).magnitude);
                tangentError = Mathf.Max(tangentError, (expected[key].tangents[i] - actual[key].tangents[i]).magnitude);
            }
            Assert.LessOrEqual(normalError, Tolerance, $"normals of {key}");
            Assert.LessOrEqual(tangentError, Tolerance, $"tangents of {key}");
        }
        Assert.IsTrue(actual[("muscle nothing", 0)].normals.All(v => v == Vector3.zero), "A frame that moves nothing changes no normal.");
        Assert.IsTrue(actual[("muscle bulge", 1)].normals.Any(v => v.sqrMagnitude > 1e-4f), "The bulge changes normals.");
    }

    // Three triangles: A and B share an edge's positions with split vertices and different normals (a hard edge), C has
    // A's normals. "flex merge normal" gives B's vertex on the edge A's normal (a normal-only merge); "muscle merge
    // position" moves C's corner exactly onto A's corner with the same normal (a positional merge). The full bake groups
    // both pairs into new seams; the incremental path must leave those frames to it.
    SkinnedMeshRenderer HardEdgeRig()
    {
        var up = new Vector3(0, 0, 1);
        var side = new Vector3(0, 1, 0);
        var mesh = new Mesh { name = "Body" };
        created.Add(mesh);
        mesh.vertices = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 1),
            new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(2, 1, 0)
        };
        mesh.triangles = new[] { 0, 1, 2, 3, 5, 4, 6, 8, 7 }; // C faces away from A: merged, their normals cancel
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.right, Vector2.up, Vector2.one, Vector2.zero, Vector2.right, Vector2.up };
        mesh.normals = new[] { up, up, up, side, side, side, up, up, up };
        mesh.RecalculateTangents();
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 9).ToArray();
        mesh.bindposes = new[] { Matrix4x4.identity };
        var deltaNormals = new Vector3[9];
        deltaNormals[3] = up - side; // exactly (0, -1, 1): side + delta == up
        mesh.AddBlendShapeFrame("flex merge normal", 100, new Vector3[9], deltaNormals, null);
        var moved = new Vector3[9];
        moved[6] = new Vector3(-1, 0, 0); // (2, 0, 0) + (-1, 0, 0) == vertex 1's (1, 0, 0)
        mesh.AddBlendShapeFrame("muscle merge position", 100, moved, null, null);
        var apart = new Vector3[9];
        apart[7] = new Vector3(0, 0, .25f);
        mesh.AddBlendShapeFrame("muscle apart", 100, apart, null, null);

        var root = new GameObject("Rig");
        created.Add(root);
        var hips = new GameObject("Hips").transform;
        hips.SetParent(root.transform, false);
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.bones = new[] { hips };
        renderer.rootBone = hips;
        return renderer;
    }

    [Test] public void FramesThatMergeSeamsMatchTheFullRebake()
    {
        var renderer = HardEdgeRig();
        var shapes = new[] { "flex merge normal", "muscle merge position", "muscle apart" };
        var expected = LegacyCapture(renderer, shapes, null);
        var actual = DynamicNormals.CaptureNormalFrames(renderer, shapes);
        foreach (var key in expected.Keys)
            for (int i = 0; i < renderer.sharedMesh.vertexCount; i++)
            {
                Assert.LessOrEqual((expected[key].normals[i] - actual[key].normals[i]).magnitude, Tolerance, $"normal {i} of {key}");
                Assert.LessOrEqual((expected[key].tangents[i] - actual[key].tangents[i]).magnitude, Tolerance, $"tangent {i} of {key}");
            }
        // The merges are real: the full bake averaged the pair, so their deltas moved.
        Assert.Greater(expected[("flex merge normal", 0)].normals[1].sqrMagnitude + expected[("flex merge normal", 0)].normals[3].sqrMagnitude, 1e-6f);
        Assert.Greater(expected[("muscle merge position", 0)].normals[1].sqrMagnitude, 1e-6f);
    }

    // ---- MCB 1.12.24's capture: every frame skinned, recalculated and seam-averaged over the whole mesh.

    static Dictionary<(string, int), (Vector3[] normals, Vector3[] tangents)> LegacyCapture(SkinnedMeshRenderer smr, IList<string> shapes,
        Dictionary<string, Vector3> boneTranslations)
    {
        var result = new Dictionary<(string, int), (Vector3[], Vector3[])>();
        var originalMesh = smr.sharedMesh;
        int vertexCount = originalMesh.vertexCount;
        var baker = Object.Instantiate(smr);
        baker.sharedMesh = originalMesh;
        baker.gameObject.hideFlags = HideFlags.HideAndDontSave;
        var baseMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            for (int i = 0; i < originalMesh.blendShapeCount; i++) baker.SetBlendShapeWeight(i, 0f);
            baker.bones = originalMesh.bindposes.Select((bindPose, index) =>
            {
                var go = new GameObject { hideFlags = HideFlags.HideAndDontSave };
                go.transform.parent = baker.transform;
                ExtractFromTRS(Matrix4x4.Inverse(bindPose), out var pos, out var rot, out var scale);
                go.transform.localPosition = pos; go.transform.localRotation = rot; go.transform.localScale = scale;
                if (boneTranslations != null && index < smr.bones.Length && smr.bones[index] != null &&
                    boneTranslations.TryGetValue(smr.bones[index].name, out var offset)) go.transform.localPosition += offset;
                return go.transform;
            }).ToArray();
            baker.BakeMesh(baseMesh);
            var groups = Groups(baseMesh);
            baseMesh.RecalculateNormals();
            Seams(baseMesh, groups);
            baseMesh.RecalculateTangents();
            var baseNormals = baseMesh.normals;
            var baseTangents = baseMesh.tangents;
            for (int shape = 0; shape < originalMesh.blendShapeCount; shape++)
            {
                string name = originalMesh.GetBlendShapeName(shape);
                if (!shapes.Contains(name)) continue;
                for (int frame = 0; frame < originalMesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    try
                    {
                        baker.SetBlendShapeWeight(shape, originalMesh.GetBlendShapeFrameWeight(shape, frame));
                        baker.BakeMesh(baked);
                        baker.SetBlendShapeWeight(shape, 0f);
                        var frameGroups = Groups(baked);
                        baked.RecalculateNormals();
                        Seams(baked, frameGroups);
                        baked.RecalculateTangents();
                        var normals = baked.normals;
                        var tangents = baked.tangents;
                        var deltaNormals = new Vector3[vertexCount];
                        var deltaTangents = new Vector3[vertexCount];
                        for (int i = 0; i < vertexCount; i++)
                        {
                            deltaNormals[i] = normals[i] - baseNormals[i];
                            deltaTangents[i] = (Vector3)(tangents[i] - baseTangents[i]);
                        }
                        result.Add((name, frame), (deltaNormals, deltaTangents));
                    }
                    finally { Object.DestroyImmediate(baked); }
                }
            }
        }
        finally
        {
            Object.DestroyImmediate(baseMesh);
            Object.DestroyImmediate(baker.gameObject);
        }
        return result;
    }

    static List<int[]> Groups(Mesh mesh)
    {
        var vertices = mesh.vertices;
        var normals = mesh.normals;
        var map = new Dictionary<(Vector3, Vector3), List<int>>();
        for (int i = 0; i < vertices.Length; i++)
        {
            var key = (vertices[i], normals[i]);
            if (!map.TryGetValue(key, out var list)) map.Add(key, list = new List<int>());
            list.Add(i);
        }
        return map.Values.Where(list => list.Count >= 2).Select(list => list.ToArray()).ToList();
    }

    static void Seams(Mesh mesh, List<int[]> groups)
    {
        var normals = mesh.normals;
        foreach (var indices in groups)
        {
            var normal = Vector3.zero;
            foreach (int index in indices) normal += normals[index];
            normal /= indices.Length;
            normal = normal.normalized;
            foreach (int index in indices) normals[index] = normal;
        }
        mesh.normals = normals;
    }

    static void ExtractFromTRS(Matrix4x4 matrix, out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        var c0 = matrix.GetColumn(0); var c1 = matrix.GetColumn(1); var c2 = matrix.GetColumn(2); var c3 = matrix.GetColumn(3);
        pos = c3;
        rot = Quaternion.LookRotation(c2, c1);
        scale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
    }
}
