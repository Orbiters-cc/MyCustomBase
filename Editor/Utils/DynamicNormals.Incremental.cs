using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MCBEditorUtils
{
    public sealed partial class DynamicNormals
    {
#if UNITY_EDITOR
        /// <summary>
        /// The normals and tangents of one blendshape frame, recomputed only where the frame changes them.
        /// <para>
        /// The full bake computed, per frame and over every vertex: the skinned mesh (BakeMesh), the seam groups (vertices
        /// with exactly the same baked position and normal), Unity's RecalculateNormals, the seam average and
        /// RecalculateTangents. Here the frame is still baked by Unity (positions and normals exactly as the full bake sees
        /// them), but only the vertices whose baked position or normal differs from the rest bake are looked at. A vertex's
        /// normal depends only on the triangles around it and on its seam partners, its tangent on those triangles and its
        /// own normal, so the affected vertices (corners of triangles with a moved vertex, and the members of every seam
        /// group a changed vertex belongs to) are recomputed with the same Unity calls on a small mesh holding every
        /// triangle around them, in the full mesh's triangle order; the others keep their rest values and a zero delta.
        /// </para>
        /// <para>
        /// Seam groups are decided with the full bake's own rule (<see cref="PosNorm"/> keys in a dictionary) in its own
        /// space (baked positions and normals). When a changed vertex's key equals the key of a vertex outside its rest
        /// group, the frame would regroup: <see cref="Solve"/> returns null and the caller runs the full bake for it.
        /// Otherwise the frame's groups are the rest groups split by exact key, which is what this path averages.
        /// </para>
        /// </summary>
        private sealed class IncrementalFrameNormals : IDisposable
        {
            readonly SkinnedMeshRenderer baker;
            readonly int vertexCount;
            readonly Vector3[] restPositions, restNormals;
            readonly Vector2[] uvs;
            readonly int[] triangles, triangleStart, vertexTriangles, groupOf;
            readonly List<int[]> groups = new List<int[]>();
            // The full bake's grouping of the rest pose: key -> the vertices holding it, in vertex order.
            readonly Dictionary<PosNorm, List<int>> restKeys = new Dictionary<PosNorm, List<int>>();
            Mesh frameBake = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            // Whether frameBake holds the bake of the frame Solve just handed back (null), untouched since.
            bool bakeHandedBack;
            readonly Mesh local = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };

            // Per-frame marks, reset by moving to a new stamp instead of clearing.
            readonly int[] vertexMark, triangleMark, groupMark, localIndex, changedMark;
            int stamp;
            readonly List<Vector3> framePositionList = new List<Vector3>(), frameNormalList = new List<Vector3>();
            Vector3[] framePositions, frameNormals;
            readonly List<int> affected = new List<int>(), changed = new List<int>(), touchedGroups = new List<int>(),
                frameTriangles = new List<int>(), localVertices = new List<int>(), localTriangles = new List<int>();
            readonly Dictionary<PosNorm, int> changedKeys = new Dictionary<PosNorm, int>();
            readonly Dictionary<PosNorm, List<int>> seamKeys = new Dictionary<PosNorm, List<int>>();
            readonly List<Vector3> localPositions = new List<Vector3>(), localNormals = new List<Vector3>();
            readonly List<Vector2> localUvs = new List<Vector2>();
            readonly List<Vector4> localTangents = new List<Vector4>();

            /// <summary>Only meshes made of triangles: other topologies run the full bake.</summary>
            public static bool Supports(Mesh mesh)
            {
                for (int i = 0; i < mesh.subMeshCount; i++)
                    if (mesh.GetTopology(i) != MeshTopology.Triangles) return false;
                return true;
            }

            /// <param name="baker">The full bake's renderer: rest pose bones, every blendshape at 0.</param>
            public IncrementalFrameNormals(SkinnedMeshRenderer baker, Mesh originalMesh)
            {
                this.baker = baker;
                vertexCount = originalMesh.vertexCount;
                // The rest bake before any normal is recalculated: what the full bake groups at rest.
                baker.BakeMesh(frameBake);
                restPositions = frameBake.vertices;
                restNormals = frameBake.normals;
                if (restNormals.Length != vertexCount) restNormals = new Vector3[vertexCount];
                var uvList = new List<Vector2>();
                originalMesh.GetUVs(0, uvList);
                uvs = uvList.Count == vertexCount ? uvList.ToArray() : null;

                // Vertex -> triangles, compressed: triangleStart[v]..triangleStart[v + 1] in vertexTriangles.
                var all = new List<int>();
                for (int i = 0; i < originalMesh.subMeshCount; i++) all.AddRange(originalMesh.GetTriangles(i));
                triangles = all.ToArray();
                int triangleCount = triangles.Length / 3;
                triangleStart = new int[vertexCount + 1];
                foreach (int v in triangles) triangleStart[v + 1]++;
                for (int v = 0; v < vertexCount; v++) triangleStart[v + 1] += triangleStart[v];
                vertexTriangles = new int[triangles.Length];
                var next = new int[vertexCount];
                Array.Copy(triangleStart, next, vertexCount);
                for (int i = 0; i < triangles.Length; i++) vertexTriangles[next[triangles[i]]++] = i / 3;

                // As StoreIndicesWithSamePositionAndNormal on the rest bake.
                groupOf = new int[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    groupOf[v] = -1;
                    var key = new PosNorm(restPositions[v], restNormals[v]);
                    if (!restKeys.TryGetValue(key, out var list)) restKeys.Add(key, list = new List<int>(1));
                    list.Add(v);
                }
                foreach (var list in restKeys.Values)
                {
                    if (list.Count < 2) continue;
                    foreach (int v in list) groupOf[v] = groups.Count;
                    groups.Add(list.ToArray());
                }

                vertexMark = new int[vertexCount];
                localIndex = new int[vertexCount];
                changedMark = new int[vertexCount];
                triangleMark = new int[triangleCount];
                groupMark = new int[groups.Count];
            }

            int NextStamp()
            {
                if (stamp >= int.MaxValue - 1)
                {
                    Array.Clear(vertexMark, 0, vertexMark.Length);
                    Array.Clear(triangleMark, 0, triangleMark.Length);
                    Array.Clear(groupMark, 0, groupMark.Length);
                    Array.Clear(changedMark, 0, changedMark.Length);
                    stamp = 0;
                }
                return ++stamp;
            }

            /// <summary>
            /// Bakes the frame, writes the recomputed normal and tangent of each vertex it may change into
            /// <paramref name="normals"/> and <paramref name="tangents"/> (the other entries are left alone) and returns those
            /// vertices; null when the frame regroups seam vertices (<see cref="SeamGroupsHold"/>), for the full bake.
            /// </summary>
            public List<int> Solve(int shapeIndex, float frameWeight, Vector3[] normals, Vector4[] tangents)
            {
                bakeHandedBack = false;
                affected.Clear();
                changed.Clear();
                touchedGroups.Clear();
                baker.SetBlendShapeWeight(shapeIndex, frameWeight);
                try { baker.BakeMesh(frameBake); }
                finally { baker.SetBlendShapeWeight(shapeIndex, 0f); }
                frameBake.GetVertices(framePositionList);
                frameBake.GetNormals(frameNormalList);
                if (framePositionList.Count != vertexCount || frameNormalList.Count != vertexCount) return HandBack();
                if (framePositions == null) { framePositions = new Vector3[vertexCount]; frameNormals = new Vector3[vertexCount]; }
                framePositionList.CopyTo(framePositions);
                frameNormalList.CopyTo(frameNormals);

                int frame = NextStamp();
                for (int v = 0; v < vertexCount; v++)
                {
                    // Exact component comparison: any change may move the vertex out of (or into) a seam group.
                    bool moved = !SameBits(framePositions[v], restPositions[v]);
                    if (!moved && SameBits(frameNormals[v], restNormals[v])) continue;
                    changedMark[v] = frame;
                    changed.Add(v);
                    if (groupOf[v] >= 0) TouchGroup(groupOf[v], frame);
                    if (!moved) continue;
                    for (int k = triangleStart[v]; k < triangleStart[v + 1]; k++)
                    {
                        int t = vertexTriangles[k];
                        if (triangleMark[t] == frame) continue;
                        triangleMark[t] = frame;
                        for (int c = 0; c < 3; c++) Mark(triangles[3 * t + c], frame);
                    }
                }
                if (!SeamGroupsHold(frame)) return HandBack();
                for (int i = 0; i < affected.Count; i++)
                    if (groupOf[affected[i]] >= 0) TouchGroup(groupOf[affected[i]], frame);
                foreach (int g in touchedGroups)
                    foreach (int v in groups[g]) Mark(v, frame);
                if (affected.Count == 0) return affected;

                // Every triangle around an affected vertex, in the full mesh's order.
                int around = NextStamp();
                frameTriangles.Clear();
                foreach (int v in affected)
                    for (int k = triangleStart[v]; k < triangleStart[v + 1]; k++)
                    {
                        int t = vertexTriangles[k];
                        if (triangleMark[t] == around) continue;
                        triangleMark[t] = around;
                        frameTriangles.Add(t);
                    }
                frameTriangles.Sort();

                int numbered = NextStamp();
                localVertices.Clear(); localTriangles.Clear(); localPositions.Clear(); localUvs.Clear();
                foreach (int t in frameTriangles)
                    for (int c = 0; c < 3; c++)
                    {
                        int corner = triangles[3 * t + c];
                        if (vertexMark[corner] != numbered && vertexMark[corner] != frame || vertexMark[corner] == frame && localIndex[corner] < 0)
                        {
                            // Affected vertices keep their mark (localIndex then tells them apart); others get this one.
                            if (vertexMark[corner] != frame) vertexMark[corner] = numbered;
                            localIndex[corner] = localVertices.Count;
                            localVertices.Add(corner);
                            localPositions.Add(framePositions[corner]);
                            if (uvs != null) localUvs.Add(uvs[corner]);
                        }
                        localTriangles.Add(localIndex[corner]);
                    }

                local.Clear();
                local.indexFormat = IndexFormat.UInt32;
                local.SetVertices(localPositions);
                if (uvs != null) local.SetUVs(0, localUvs);
                local.subMeshCount = 1;
                local.SetTriangles(localTriangles, 0, false);
                local.RecalculateNormals();
                local.GetNormals(localNormals);
                foreach (int g in touchedGroups) AverageSeam(groups[g]);
                local.SetNormals(localNormals);
                local.RecalculateTangents();
                local.GetTangents(localTangents);

                for (int i = affected.Count - 1; i >= 0; i--)
                {
                    int v = affected[i], index = localIndex[v];
                    // A vertex no triangle uses has nothing to recompute.
                    if (index < 0) { affected.RemoveAt(i); continue; }
                    normals[v] = localNormals[index];
                    tangents[v] = localTangents[index];
                }
                return affected;
            }

            List<int> HandBack()
            {
                bakeHandedBack = true;
                return null;
            }

            /// <summary>
            /// The frame's bake when the last <see cref="Solve"/> handed the frame back (the full bake then needs no second
            /// BakeMesh), else null. The caller owns and destroys the mesh it gets.
            /// </summary>
            public Mesh TakeBake()
            {
                if (!bakeHandedBack) return null;
                bakeHandedBack = false;
                var bake = frameBake;
                frameBake = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                return bake;
            }

            static bool SameBits(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);

            /// <summary>
            /// Whether the full bake's grouping of this frame is the rest groups split by key. Only a changed vertex can
            /// change its key; unchanged vertices keep their rest keys. The frame regroups when a changed vertex's key, as
            /// the full bake's dictionary sees it, is held by an unchanged vertex outside its rest group or by a changed
            /// vertex of another rest group (or by one outside any group).
            /// </summary>
            bool SeamGroupsHold(int frame)
            {
                changedKeys.Clear();
                foreach (int v in changed)
                {
                    var key = new PosNorm(framePositions[v], frameNormals[v]);
                    if (restKeys.TryGetValue(key, out var holders))
                        foreach (int w in holders)
                            if (w != v && changedMark[w] != frame && !SameRestGroup(v, w)) return false;
                    if (changedKeys.TryGetValue(key, out int other))
                    {
                        if (!SameRestGroup(v, other)) return false;
                    }
                    else changedKeys.Add(key, v);
                }
                return true;
            }

            bool SameRestGroup(int a, int b) => groupOf[a] >= 0 && groupOf[a] == groupOf[b];

            void Mark(int v, int frame)
            {
                if (vertexMark[v] == frame) return;
                vertexMark[v] = frame;
                localIndex[v] = -1;
                affected.Add(v);
            }

            void TouchGroup(int g, int frame)
            {
                if (groupMark[g] == frame) return;
                groupMark[g] = frame;
                touchedGroups.Add(g);
            }

            // As StoreIndicesWithSamePositionAndNormal then ReRecalculateNormalsInUVSeams, on one rest group: members are
            // grouped by the full bake's key in the same kind of dictionary, and each group of two or more shares the
            // normalised mean of their recalculated normals, summed in vertex order.
            void AverageSeam(int[] members)
            {
                seamKeys.Clear();
                foreach (int v in members)
                {
                    var key = new PosNorm(framePositions[v], frameNormals[v]);
                    if (!seamKeys.TryGetValue(key, out var list)) seamKeys.Add(key, list = new List<int>(members.Length));
                    list.Add(v);
                }
                foreach (var seam in seamKeys.Values)
                {
                    if (seam.Count < 2) continue;
                    var sum = Vector3.zero;
                    foreach (int v in seam) if (localIndex[v] >= 0) sum += localNormals[localIndex[v]];
                    sum /= seam.Count;
                    sum = sum.normalized;
                    foreach (int v in seam) if (localIndex[v] >= 0) localNormals[localIndex[v]] = sum;
                }
            }

            public void Dispose()
            {
                Object.DestroyImmediate(local);
                Object.DestroyImmediate(frameBake);
            }
        }
#endif
    }
}
