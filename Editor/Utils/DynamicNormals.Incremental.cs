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
        /// The full bake computed, per frame and over every vertex: the skinned mesh (BakeMesh), Unity's RecalculateNormals,
        /// the UV-seam average over vertices with the same baked position and normal, and RecalculateTangents. A vertex's
        /// normal depends only on the triangles around it and on its seam partners, its tangent on those triangles and its
        /// own normal; vertices away from the frame's moved triangles keep their rest values, so their deltas stay zero.
        /// The affected vertices (corners of triangles with a moved vertex, their seam partners, and the seam groups of
        /// vertices whose normal delta changes their seam key) are recomputed with the same Unity calls on a small mesh that
        /// holds every triangle around them, in the full mesh's triangle order.
        /// </para>
        /// <para>
        /// Skinning is linear in a vertex's position: a moved vertex's baked position is its rest bake plus its delta times
        /// its skinning matrix, measured once with three bakes of unit-offset copies (Unity's own skinning, weights and
        /// quality). Seam groups are the rest pose's, split per frame where members no longer share a position and normal.
        /// Against the full bake the results differ by float rounding only (the linearised skinning, about 1e-6 on unit
        /// vectors). Not reproduced: a frame moving a vertex onto an unrelated vertex with the very same normal would have
        /// been merged with it into a new seam group.
        /// </para>
        /// </summary>
        private sealed class IncrementalFrameNormals : IDisposable
        {
            readonly int vertexCount;
            readonly Vector3[] restPositions, sourceNormals, jx, jy, jz, framePositions;
            readonly Vector2[] uvs;
            readonly int[] triangles, triangleStart, vertexTriangles, groupOf;
            readonly List<int[]> groups;
            readonly Mesh local = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };

            // Per-frame marks, reset by moving to a new stamp instead of clearing.
            readonly int[] vertexMark, triangleMark, groupMark, localIndex;
            int stamp;
            readonly List<int> affected = new List<int>(), touchedGroups = new List<int>(), frameTriangles = new List<int>(),
                localVertices = new List<int>(), localTriangles = new List<int>(), seam = new List<int>();
            readonly List<bool> seamDone = new List<bool>();
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

            /// <param name="baker">Skins with the rest pose's bones; its mesh is swapped for the unit bakes and put back.</param>
            /// <param name="restBake">The rest pose's bake, before its normals were recalculated.</param>
            /// <param name="restGroups">The seam groups of <paramref name="restBake"/>, members in vertex order.</param>
            public IncrementalFrameNormals(SkinnedMeshRenderer baker, Mesh originalMesh, Mesh restBake, List<int[]> restGroups)
            {
                vertexCount = originalMesh.vertexCount;
                restPositions = restBake.vertices;
                framePositions = (Vector3[])restPositions.Clone();
                sourceNormals = originalMesh.normals;
                if (sourceNormals.Length != vertexCount) sourceNormals = new Vector3[vertexCount];
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

                groups = restGroups;
                groupOf = new int[vertexCount];
                for (int v = 0; v < vertexCount; v++) groupOf[v] = -1;
                for (int g = 0; g < groups.Count; g++) foreach (int v in groups[g]) groupOf[v] = g;

                MeasureSkinning(baker, originalMesh, out jx, out jy, out jz);

                vertexMark = new int[vertexCount];
                localIndex = new int[vertexCount];
                triangleMark = new int[triangleCount];
                groupMark = new int[groups.Count];
            }

            // A vertex's baked position moves by J·delta: J's columns are the bakes of copies offset by one unit per axis.
            static void MeasureSkinning(SkinnedMeshRenderer baker, Mesh originalMesh, out Vector3[] x, out Vector3[] y, out Vector3[] z)
            {
                var vertices = originalMesh.vertices;
                var basis = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                var bake = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    basis.vertices = vertices;
                    var bonesPerVertex = originalMesh.GetBonesPerVertex();
                    if (bonesPerVertex.Length == vertices.Length) basis.SetBoneWeights(bonesPerVertex, originalMesh.GetAllBoneWeights());
                    basis.bindposes = originalMesh.bindposes;
                    baker.sharedMesh = basis;
                    baker.BakeMesh(bake);
                    var rest = bake.vertices;
                    var shifted = new Vector3[vertices.Length];
                    Vector3[] Column(Vector3 axis)
                    {
                        for (int i = 0; i < shifted.Length; i++) shifted[i] = vertices[i] + axis;
                        basis.vertices = shifted;
                        baker.BakeMesh(bake);
                        var moved = bake.vertices;
                        for (int i = 0; i < moved.Length; i++) moved[i] -= rest[i];
                        return moved;
                    }
                    x = Column(Vector3.right);
                    y = Column(Vector3.up);
                    z = Column(Vector3.forward);
                }
                finally
                {
                    baker.sharedMesh = originalMesh;
                    for (int i = 0; i < originalMesh.blendShapeCount; i++) baker.SetBlendShapeWeight(i, 0f);
                    Object.DestroyImmediate(basis);
                    Object.DestroyImmediate(bake);
                }
            }

            int NextStamp()
            {
                if (stamp >= int.MaxValue - 1)
                {
                    Array.Clear(vertexMark, 0, vertexMark.Length);
                    Array.Clear(triangleMark, 0, triangleMark.Length);
                    Array.Clear(groupMark, 0, groupMark.Length);
                    stamp = 0;
                }
                return ++stamp;
            }

            /// <summary>
            /// Recomputes a frame given its deltas, writes the recomputed normal and tangent of each vertex it may change
            /// into <paramref name="normals"/> and <paramref name="tangents"/> (the other entries are left alone) and returns
            /// those vertices.
            /// </summary>
            public List<int> Solve(Vector3[] deltaVertices, Vector3[] deltaNormals, Vector3[] normals, Vector4[] tangents)
            {
                affected.Clear();
                touchedGroups.Clear();
                int frame = NextStamp();

                for (int v = 0; v < vertexCount; v++)
                {
                    Vector3 d = deltaVertices[v];
                    if (d.x != 0f || d.y != 0f || d.z != 0f)
                    {
                        framePositions[v] = restPositions[v] + jx[v] * d.x + jy[v] * d.y + jz[v] * d.z;
                        for (int k = triangleStart[v]; k < triangleStart[v + 1]; k++)
                        {
                            int t = vertexTriangles[k];
                            if (triangleMark[t] == frame) continue;
                            triangleMark[t] = frame;
                            for (int c = 0; c < 3; c++) Mark(triangles[3 * t + c], frame);
                        }
                    }
                    else
                    {
                        framePositions[v] = restPositions[v];
                        // The vertex stays, but its seam key changes with its normal delta.
                        if (deltaNormals != null && deltaNormals[v] != Vector3.zero && groupOf[v] >= 0) TouchGroup(groupOf[v], frame);
                    }
                }
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
                foreach (int g in touchedGroups) AverageSeam(groups[g], deltaNormals);
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

            // As ReRecalculateNormalsInUVSeams: members that still share a position and normal share the normalised mean
            // of their recalculated normals, summed in vertex order.
            void AverageSeam(int[] members, Vector3[] deltaNormals)
            {
                seamDone.Clear();
                for (int i = 0; i < members.Length; i++) seamDone.Add(false);
                for (int i = 0; i < members.Length; i++)
                {
                    if (seamDone[i]) continue;
                    Vector3 position = framePositions[members[i]], normal = SeamNormal(members[i], deltaNormals);
                    seam.Clear();
                    for (int j = i; j < members.Length; j++)
                    {
                        int other = members[j];
                        if (seamDone[j] || !framePositions[other].Equals(position) || !SeamNormal(other, deltaNormals).Equals(normal)) continue;
                        seamDone[j] = true;
                        seam.Add(other);
                    }
                    if (seam.Count < 2) continue;
                    var sum = Vector3.zero;
                    foreach (int v in seam) if (localIndex[v] >= 0) sum += localNormals[localIndex[v]];
                    sum /= seam.Count;
                    sum = sum.normalized;
                    foreach (int v in seam) if (localIndex[v] >= 0) localNormals[localIndex[v]] = sum;
                }
            }

            Vector3 SeamNormal(int v, Vector3[] deltaNormals) => deltaNormals != null ? sourceNormals[v] + deltaNormals[v] : sourceNormals[v];

            public void Dispose() => Object.DestroyImmediate(local);
        }
#endif
    }
}
