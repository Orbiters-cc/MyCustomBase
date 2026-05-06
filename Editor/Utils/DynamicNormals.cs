using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;

namespace MCBEditorUtils
{
    /// <summary>
    /// Recalculates normals/tangents for the requested blendshapes by baking them and rebuilding the mesh.
    /// </summary>
    public sealed class DynamicNormals
    {
#if UNITY_EDITOR
        private readonly Transform _root;
        private bool _enabled = true;
        private bool _limitToMeshes;
        private readonly HashSet<SkinnedMeshRenderer> _limitedMeshes = new HashSet<SkinnedMeshRenderer>();
        private readonly HashSet<string> _targetBlendshapes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _eraseCustomSplitBlendshapes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Quaternion> _boneRotations = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector3> _boneTranslations = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        private bool _saveAsAsset = true;

        public DynamicNormals(Transform root)
        {
            _root = root != null ? root : throw new ArgumentNullException(nameof(root));
        }

        public static DynamicNormals ForRoot(Transform root)
        {
            return new DynamicNormals(root);
        }

        public DynamicNormals limitToMeshes(IEnumerable<SkinnedMeshRenderer> meshes)
        {
            _limitToMeshes = true;
            _limitedMeshes.Clear();
            if (meshes == null) return this;

            foreach (var mesh in meshes)
            {
                if (mesh != null)
                {
                    _limitedMeshes.Add(mesh);
                }
            }

            return this;
        }

        public DynamicNormals dontLimitToMeshes()
        {
            _limitToMeshes = false;
            _limitedMeshes.Clear();
            return this;
        }

        public DynamicNormals applyToBlendshapes(IEnumerable<string> blendshapesList)
        {
            _targetBlendshapes.Clear();
            if (blendshapesList == null) return this;

            foreach (var name in blendshapesList)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    _targetBlendshapes.Add(name);
                }
            }

            return this;
        }

        public DynamicNormals enable(bool value)
        {
            _enabled = value;
            return this;
        }

        public DynamicNormals saveAsAsset(bool value)
        {
            _saveAsAsset = value;
            return this;
        }

        public DynamicNormals withBoneRotations(IDictionary<string, Quaternion> rotations)
        {
            if (rotations != null)
            {
                foreach (var kvp in rotations)
                {
                    _boneRotations[kvp.Key] = kvp.Value;
                }
            }
            return this;
        }

        public DynamicNormals withBoneTranslations(IDictionary<string, Vector3> translations)
        {
            if (translations != null)
            {
                foreach (var kvp in translations)
                {
                    _boneTranslations[kvp.Key] = kvp.Value;
                }
            }
            return this;
        }

        public DynamicNormals eraseCustomSplitNormalsFor(IEnumerable<string> blendshapes)
        {
            _eraseCustomSplitBlendshapes.Clear();
            if (blendshapes == null) return this;

            foreach (var name in blendshapes)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    _eraseCustomSplitBlendshapes.Add(name);
                }
            }

            return this;
        }

        /// <summary>
        /// Executes the recalculation with the current configuration.
        /// </summary>
        public void Apply()
        {
            if (!_enabled) return;

            var renderers = _limitToMeshes
                ? _limitedMeshes.Where(r => r != null).ToArray()
                : _root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (var smr in renderers)
            {
                if (smr == null) continue;

                var mesh = smr.sharedMesh;
                if (mesh == null) continue;

                var blendShapeNames = Enumerable.Range(0, mesh.blendShapeCount)
                    .Select(mesh.GetBlendShapeName)
                    .ToList();

                var applicableBlendShapes = _targetBlendshapes.Count == 0
                    ? new List<string>(blendShapeNames)
                    : blendShapeNames
                        .Where(name => _targetBlendshapes.Contains(name))
                        .Distinct()
                        .ToList();

                if (applicableBlendShapes.Count == 0) continue;

                var eraseCustom = _eraseCustomSplitBlendshapes.Count == 0
                    ? new List<string>()
                    : blendShapeNames
                        .Where(name => _eraseCustomSplitBlendshapes.Contains(name))
                        .Distinct()
                        .ToList();

                RecalculateNormalsOf(smr, blendShapeNames, applicableBlendShapes, eraseCustom, _boneRotations, _boneTranslations, _saveAsAsset);
            }
        }

        private static void RecalculateNormalsOf(SkinnedMeshRenderer smr, List<string> smrBlendShapes, List<string> applicableBlendShapes, List<string> eraseCustomSplitNormalsBlendShapes, Dictionary<string, Quaternion> boneRotations, Dictionary<string, Vector3> boneTranslations, bool saveAsAsset)
        {
            var originalMesh = smr.sharedMesh;
            if (originalMesh == null) return;

            var baker = Object.Instantiate(smr);
            baker.sharedMesh = originalMesh;
            baker.gameObject.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                for (var i = 0; i < smrBlendShapes.Count; i++)
                {
                    baker.SetBlendShapeWeight(i, 0f);
                }

                var bindPoseBones = originalMesh.bindposes
                    .Select((bindPose, index) =>
                    {
                        var inverse = Matrix4x4.Inverse(bindPose);
                        var go = new GameObject { hideFlags = HideFlags.HideAndDontSave };
                        go.transform.parent = baker.transform;
                        ExtractFromTRS(inverse, out var pos, out var rot, out var scale);
                        go.transform.localPosition = pos;
                        go.transform.localRotation = rot;
                        go.transform.localScale = scale;

                        if (boneRotations != null && index < smr.bones.Length)
                        {
                            var bone = smr.bones[index];
                            if (bone != null && boneRotations.TryGetValue(bone.name, out var offset))
                            {
                                go.transform.localRotation *= offset;
                            }
                        }

                        if (boneTranslations != null && index < smr.bones.Length)
                        {
                            var bone = smr.bones[index];
                            if (bone != null && boneTranslations.TryGetValue(bone.name, out var offset))
                            {
                                go.transform.localPosition += offset;
                            }
                        }

                        return go.transform;
                    })
                    .ToArray();
                baker.bones = bindPoseBones;

                var baseMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    baker.BakeMesh(baseMesh);

                    var indicesWithSamePosNorm = StoreIndicesWithSamePositionAndNormal(baseMesh);
                    baseMesh.RecalculateNormals();
                    ReRecalculateNormalsInUVSeams(baseMesh, indicesWithSamePosNorm);
                    baseMesh.RecalculateTangents();

                    var originalNormals = originalMesh.normals;
                    var originalTangents = originalMesh.tangents;

                    var baseNormals = baseMesh.normals;
                    var baseTangents = baseMesh.tangents;

                    var ignored = new Vector3[originalMesh.vertexCount];
                    var deltaVertices = new Vector3[originalMesh.vertexCount];
                    var deltaNormals = new Vector3[originalMesh.vertexCount];
                    var deltaTangents = new Vector3[originalMesh.vertexCount];
                    var copyVertices = new Vector3[originalMesh.vertexCount];
                    var copyNormals = new Vector3[originalMesh.vertexCount];
                    var copyTangents = new Vector3[originalMesh.vertexCount];

                    // Get the original mesh asset path to determine where to save the dynamic normals mesh
                    string originalMeshPath = saveAsAsset ? AssetDatabase.GetAssetPath(originalMesh) : null;
                    string assetPath = null;
                    
                    // Only save as asset if the original mesh is an asset (not a scene-only mesh)
                    if (!string.IsNullOrEmpty(originalMeshPath))
                    {
                        // Create asset path: same directory as original mesh, with "_DynamicNormals.asset" suffix
                        string directory = System.IO.Path.GetDirectoryName(originalMeshPath);
                        string meshNameSafe = originalMesh.name.Replace(" ", "_").Replace("(", "").Replace(")", "");
                        assetPath = System.IO.Path.Combine(directory, $"{meshNameSafe}_DynamicNormals.asset").Replace("\\", "/");
                        
                        // Delete existing asset if it exists
                        if (AssetDatabase.LoadAssetAtPath<Mesh>(assetPath) != null)
                        {
                            AssetDatabase.DeleteAsset(assetPath);
                        }
                    }
                     
                    var newMesh = Object.Instantiate(originalMesh);
                    newMesh.name = $"{originalMesh.name} (DynamicNormals)";
                    newMesh.ClearBlendShapes();

                    for (var shapeIndex = 0; shapeIndex < originalMesh.blendShapeCount; shapeIndex++)
                    {
                        var blendShape = originalMesh.GetBlendShapeName(shapeIndex);
                        var frameCount = originalMesh.GetBlendShapeFrameCount(shapeIndex);
                        bool recalculateFrame = applicableBlendShapes.Contains(blendShape);
                        bool eraseCustomSplitNormals = eraseCustomSplitNormalsBlendShapes.Contains(blendShape);

                        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            var frameWeight = originalMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                            if (!recalculateFrame)
                            {
                                originalMesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, copyVertices, copyNormals, copyTangents);
                                newMesh.AddBlendShapeFrame(blendShape, frameWeight, copyVertices, copyNormals, copyTangents);
                                continue;
                            }

                            var bakedMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                            try
                            {
                                baker.SetBlendShapeWeight(shapeIndex, frameWeight);
                                baker.BakeMesh(bakedMesh);
                                baker.SetBlendShapeWeight(shapeIndex, 0f);

                                var indicesInBaked = StoreIndicesWithSamePositionAndNormal(bakedMesh);
                                bakedMesh.RecalculateNormals();
                                ReRecalculateNormalsInUVSeams(bakedMesh, indicesInBaked);
                                bakedMesh.RecalculateTangents();

                                var bakedNormals = bakedMesh.normals;
                                var bakedTangents = bakedMesh.tangents;

                                originalMesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, deltaVertices, ignored, ignored);
                                for (var i = 0; i < originalMesh.vertexCount; i++)
                                {
                                    deltaNormals[i] = bakedNormals[i] - baseNormals[i];
                                    deltaTangents[i] = (Vector3)(bakedTangents[i] - baseTangents[i]);
                                }

                                if (eraseCustomSplitNormals)
                                {
                                    var nonZero = 0;
                                    var zero = 0;
                                    for (var i = 0; i < originalMesh.vertexCount; i++)
                                    {
                                        if (deltaVertices[i] != Vector3.zero || deltaNormals[i] != Vector3.zero)
                                        {
                                            nonZero++;
                                            deltaNormals[i] = bakedNormals[i] - originalNormals[i];
                                            deltaTangents[i] = (Vector3)(bakedTangents[i] - originalTangents[i]);
                                        }
                                        else
                                        {
                                            zero++;
                                        }
                                    }

                                    Debug.Log($"({nameof(DynamicNormals)}) Erasing custom split normals on blendshape {blendShape} in SMR {smr.name} resulted in {nonZero} non-zero vertices and {zero} zero vertices");
                                }

                                newMesh.AddBlendShapeFrame(blendShape, frameWeight, deltaVertices, deltaNormals, deltaTangents);
                            }
                            finally
                            {
                                Object.DestroyImmediate(bakedMesh);
                            }
                        }
                    }
                     
                    // Save as asset if we have a valid path
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        AssetDatabase.CreateAsset(newMesh, assetPath);
                        AssetDatabase.SaveAssets();
                        // Reload the asset to ensure we're using the saved version
                        newMesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                        Debug.Log($"[DynamicNormals] Saved dynamic normals mesh as asset at: {assetPath}");
                    }
                    else
                    {
                        Debug.LogWarning($"[DynamicNormals] Could not save mesh as asset (original mesh has no asset path). Mesh will be in-memory only.");
                    }
                    
                    smr.sharedMesh = newMesh;
                }
                finally
                {
                    Object.DestroyImmediate(baseMesh);
                }
            }
            finally
            {
                Object.DestroyImmediate(baker.gameObject);
            }
        }

        private static void ReRecalculateNormalsInUVSeams(Mesh mesh, List<int[]> indicesWithSamePosNorm)
        {
            var normals = mesh.normals;
            foreach (var indices in indicesWithSamePosNorm)
            {
                var normal = Vector3.zero;
                foreach (var index in indices)
                {
                    normal += normals[index];
                }
                normal /= indices.Length;
                normal = normal.normalized;

                foreach (var index in indices)
                {
                    normals[index] = normal;
                }
            }

            mesh.normals = normals;
        }

        private static List<int[]> StoreIndicesWithSamePositionAndNormal(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;

            var map = new Dictionary<PosNorm, List<int>>();
            for (var i = 0; i < vertices.Length; i++)
            {
                var key = new PosNorm(vertices[i], normals[i]);
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    map.Add(key, list);
                }

                list.Add(i);
            }

            return map
                .Where(pair => pair.Value.Count >= 2)
                .Select(pair => pair.Value.ToArray())
                .ToList();
        }

        private static void RebuildBlendshapesOnCopy(Mesh meshCopy, Mesh originalMesh, Dictionary<string, DeltaMeshBake[]> nameToFrameDeltaBakes)
        {
            var verts = new Vector3[originalMesh.vertexCount];
            var norms = new Vector3[originalMesh.vertexCount];
            var tans = new Vector3[originalMesh.vertexCount];

            meshCopy.ClearBlendShapes();
            for (var shapeIndex = 0; shapeIndex < originalMesh.blendShapeCount; shapeIndex++)
            {
                var name = originalMesh.GetBlendShapeName(shapeIndex);
                if (!nameToFrameDeltaBakes.TryGetValue(name, out var bakedFrames))
                {
                    var frameCount = originalMesh.GetBlendShapeFrameCount(shapeIndex);
                    for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        var weight = originalMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                        originalMesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, verts, norms, tans);
                        meshCopy.AddBlendShapeFrame(name, weight, verts, norms, tans);
                    }
                }
                else
                {
                    for (var frameIndex = 0; frameIndex < bakedFrames.Length; frameIndex++)
                    {
                        var weight = originalMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                        var frame = bakedFrames[frameIndex];
                        meshCopy.AddBlendShapeFrame(name, weight, frame.vertices, frame.normals, frame.tangents);
                    }
                }
            }
        }

        private static void ExtractFromTRS(Matrix4x4 matrix, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            var c0 = matrix.GetColumn(0);
            var c1 = matrix.GetColumn(1);
            var c2 = matrix.GetColumn(2);
            var c3 = matrix.GetColumn(3);

            pos = c3;
            rot = Quaternion.LookRotation(c2, c1);
            scale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
        }

        private readonly struct PosNorm : IEquatable<PosNorm>
        {
            public readonly Vector3 position;
            public readonly Vector3 normal;

            public PosNorm(Vector3 position, Vector3 normal)
            {
                this.position = position;
                this.normal = normal;
            }

            public bool Equals(PosNorm other)
            {
                return position.Equals(other.position) && normal.Equals(other.normal);
            }

            public override bool Equals(object obj)
            {
                return obj is PosNorm other && Equals(other);
            }

            public override int GetHashCode()
            {
#if UNITY_2022_1_OR_NEWER
                return HashCode.Combine(position, normal);
#else
                unchecked
                {
                    return (position.GetHashCode() * 397) ^ normal.GetHashCode();
                }
#endif
            }
        }

        private struct DeltaMeshBake
        {
            public Vector3[] vertices;
            public Vector3[] normals;
            public Vector3[] tangents;
        }
#endif
    }
}

