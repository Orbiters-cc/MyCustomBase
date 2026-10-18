#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;

/// <summary>
/// What Blender starts from for a target FBX: the FBX itself while the avatar shows its meshes, otherwise a copy of it
/// holding the meshes the avatar shows (an applied version's: their geometry, UVs, colours, normals, skin weights,
/// blendshapes, and the bones where those meshes are bound). The copy keeps the FBX's own records for everything else
/// (bone hierarchy, materials, settings), so Blender imports it as it imports the FBX, and its exports reach the same
/// renderers.
/// </summary>
internal static class BlenderSourceFbx
{
    /// <summary>The avatar's renderers for a target FBX (by the session's renderer paths), each with the FBX mesh it shows in.</summary>
    internal static List<(SkinnedMeshRenderer Renderer, string FbxMesh)> Renderers(Transform avatarRoot, IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        var list = new List<(SkinnedMeshRenderer, string)>();
        foreach (var entry in smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>())
        {
            if (entry == null || string.IsNullOrEmpty(entry.avatarPath)) continue;
            var found = avatarRoot.Find(entry.avatarPath);
            var renderer = found ? found.GetComponent<SkinnedMeshRenderer>() : null;
            if (!renderer || !renderer.sharedMesh || list.Any(r => r.Item1 == renderer)) continue;
            string mesh = !string.IsNullOrEmpty(entry.fbxMeshPath) ? entry.fbxMeshPath.Split('/').Last() : entry.meshName ?? renderer.name;
            list.Add((renderer, mesh));
        }
        return list;
    }

    /// <summary>Whether every renderer shows the target FBX's own mesh: Blender can start from the FBX as it is.</summary>
    internal static bool ShowsTarget(string targetUnityPath, IEnumerable<(SkinnedMeshRenderer Renderer, string FbxMesh)> renderers) =>
        renderers.All(r => string.Equals(AssetDatabase.GetAssetPath(r.Renderer.sharedMesh), targetUnityPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A fingerprint of what the renderers show (each mesh's vertices, triangles and blendshape names, the skeleton it is
    /// bound to): two equal fingerprints, the same starting point for Blender.
    /// </summary>
    internal static string Fingerprint(Transform avatarRoot, IEnumerable<(SkinnedMeshRenderer Renderer, string FbxMesh)> renderers)
    {
        using (var sha = SHA256.Create())
        {
            var buffer = new List<byte>();
            foreach (var (renderer, fbxMesh) in renderers.OrderBy(r => r.FbxMesh, StringComparer.Ordinal))
            {
                var mesh = renderer.sharedMesh;
                buffer.AddRange(System.Text.Encoding.UTF8.GetBytes(fbxMesh + "|" + mesh.vertexCount + "|" + mesh.subMeshCount + "|" + mesh.blendShapeCount + "\n"));
                if (mesh.isReadable)
                {
                    var vertices = mesh.vertices;
                    var bytes = new byte[vertices.Length * 12];
                    Buffer.BlockCopy(vertices.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray(), 0, bytes, 0, bytes.Length);
                    buffer.AddRange(sha.ComputeHash(bytes));
                    var triangles = mesh.triangles;
                    var tri = new byte[triangles.Length * 4];
                    Buffer.BlockCopy(triangles, 0, tri, 0, tri.Length);
                    buffer.AddRange(sha.ComputeHash(tri));
                }
                for (int i = 0; i < mesh.blendShapeCount; i++) buffer.AddRange(System.Text.Encoding.UTF8.GetBytes(mesh.GetBlendShapeName(i) + "\n"));
                foreach (var bind in mesh.bindposes)
                    for (int k = 0; k < 16; k++) buffer.AddRange(BitConverter.GetBytes((float)Math.Round(bind[k], 5)));
            }
            return BitConverter.ToString(sha.ComputeHash(buffer.ToArray())).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>The fingerprint of several targets' renderers together (each target's, in path order).</summary>
    internal static string FingerprintOf(Transform avatarRoot, IEnumerable<(string Target, List<(SkinnedMeshRenderer Renderer, string FbxMesh)> Renderers)> targets)
    {
        var parts = targets.OrderBy(t => t.Target, StringComparer.OrdinalIgnoreCase).Select(t => t.Target + "=" + Fingerprint(avatarRoot, t.Renderers));
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts)))).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>Writes the copy of <paramref name="targetUnityPath"/> with the meshes the renderers show to <paramref name="output"/>.</summary>
    internal static void Write(Transform avatarRoot, string targetUnityPath, IReadOnlyList<(SkinnedMeshRenderer Renderer, string FbxMesh)> renderers, string output)
    {
        var document = FbxDocument.Read(File.ReadAllBytes(Path.GetFullPath(targetUnityPath)));
        var scene = new Scene(document);
        var toUnity = document.ToUnity();
        var toFbx = toUnity.inverse;
        var rootFromWorld = avatarRoot.worldToLocalMatrix;

        // Where each bone is bound, from the meshes' bind poses (the version's skeleton), in the file's space.
        var bound = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        foreach (var (renderer, _) in renderers)
        {
            var bindposes = renderer.sharedMesh.bindposes;
            var meshToRoot = rootFromWorld * renderer.transform.localToWorldMatrix;
            for (int i = 0; i < renderer.bones.Length && i < bindposes.Length; i++)
                if (renderer.bones[i] && !bound.ContainsKey(renderer.bones[i].name))
                    bound[renderer.bones[i].name] = toFbx * (meshToRoot * bindposes[i].inverse) * toUnity;
        }
        scene.MoveBones(bound);

        foreach (var (renderer, fbxMesh) in renderers)
        {
            var model = scene.Model(fbxMesh) ?? scene.Model(renderer.name)
                        ?? throw new InvalidOperationException($"{Path.GetFileName(targetUnityPath)} has no mesh {fbxMesh} for {renderer.name}.");
            if (!renderer.sharedMesh.isReadable)
                throw new InvalidOperationException($"The mesh {renderer.sharedMesh.name} on {renderer.name} can't be read: enable Read/Write on its model.");
            // Through the avatar root, as the importer placed the mesh: its frame in the file's space (the skin's bind of the
            // mesh), and its points in the model's geometry space.
            var meshToRoot = rootFromWorld * renderer.transform.localToWorldMatrix;
            var meshFrame = toFbx * meshToRoot * toUnity;
            var pointsToGeometry = (scene.Global(model) * FbxTransform.Geometric(model)).inverse * toFbx * meshToRoot;
            scene.ReplaceMesh(model, renderer, pointsToGeometry, meshFrame, bound);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllBytes(output, document.Write());
    }

    // The FBX's objects and connections, edited in place.
    private sealed class Scene
    {
        private readonly FbxDocument document;
        private readonly FbxNode objectsNode, connectionsNode;
        private readonly Dictionary<long, FbxNode> objects;
        private readonly HashSet<long> used;
        private readonly Dictionary<long, long> modelParent = new Dictionary<long, long>();
        private readonly Dictionary<string, FbxNode> modelsByName = new Dictionary<string, FbxNode>(StringComparer.Ordinal);
        private readonly Dictionary<long, Matrix4x4> globals = new Dictionary<long, Matrix4x4>();

        public Scene(FbxDocument document)
        {
            this.document = document;
            objectsNode = document.Top("Objects") ?? throw new InvalidDataException("The FBX has no objects.");
            connectionsNode = document.Top("Connections") ?? throw new InvalidDataException("The FBX has no connections.");
            objects = document.Objects();
            used = new HashSet<long>(objects.Keys);
            foreach (var (child, parent, _) in document.Connections())
                if (objects.TryGetValue(child, out var c) && c.Name == "Model" && objects.TryGetValue(parent, out var p) && p.Name == "Model")
                    modelParent[child] = parent;
            foreach (var model in objects.Values.Where(o => o.Name == "Model"))
                if (!modelsByName.ContainsKey(model.ObjectName)) modelsByName[model.ObjectName] = model;
        }

        public FbxNode Model(string name) => name != null && modelsByName.TryGetValue(name, out var model) ? model : null;

        // A model in the file's root space, from its records (memoised: bones moved below update it).
        public Matrix4x4 Global(FbxNode model)
        {
            if (globals.TryGetValue(model.Id, out var known)) return known;
            var matrix = FbxTransform.Local(model);
            if (modelParent.TryGetValue(model.Id, out long parent) && objects.TryGetValue(parent, out var parentModel)) matrix = Global(parentModel) * matrix;
            globals[model.Id] = matrix;
            return matrix;
        }

        /// <summary>
        /// Puts every bone with a bind pose where it is bound (parents first), writing its Lcl translation and rotation;
        /// bones bound where the file has them keep their records untouched.
        /// </summary>
        public void MoveBones(Dictionary<string, Matrix4x4> bound)
        {
            var bones = bound.Keys.Select(Model).Where(m => m != null).OrderBy(Depth).ToList();
            foreach (var bone in bones)
            {
                var target = bound[bone.ObjectName];
                if (Close(Global(bone), target)) continue;
                var parent = modelParent.TryGetValue(bone.Id, out long p) && objects.TryGetValue(p, out var pm) ? Global(pm) : Matrix4x4.identity;
                var local = parent.inverse * target;
                var pre = FbxTransform.Euler(FbxTransform.Vector(bone, "PreRotation", Vector3.zero), 0);
                var post = FbxTransform.Euler(FbxTransform.Vector(bone, "PostRotation", Vector3.zero), 0);
                if (FbxTransform.RotationOrder(bone) != 0) throw new NotSupportedException($"Bone {bone.ObjectName} uses rotation order {FbxTransform.RotationOrder(bone)}.");
                var rotation = pre.inverse * Orthonormal(local) * post;
                SetVector(bone, "Lcl Translation", "Lcl Translation", local.GetColumn(3));
                SetVector(bone, "Lcl Rotation", "Lcl Rotation", FbxTransform.EulerXyz(rotation));
                // Its children are placed from its new place.
                InvalidateBelow(bone.Id);
            }
        }

        private int Depth(FbxNode model)
        {
            int depth = 0;
            for (long id = model.Id; modelParent.TryGetValue(id, out long parent) && depth < 512; id = parent) depth++;
            return depth;
        }

        private void InvalidateBelow(long id)
        {
            globals.Remove(id);
            foreach (var child in modelParent.Where(p => p.Value == id).Select(p => p.Key).ToList()) InvalidateBelow(child);
        }

        private static bool Close(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Math.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        // The rotation of a matrix that may carry a tiny scale (float noise): its axes normalised.
        private static Matrix4x4 Orthonormal(Matrix4x4 m)
        {
            Vector3 x = ((Vector3)m.GetColumn(0)).normalized, y = ((Vector3)m.GetColumn(1)).normalized;
            Vector3 z = Vector3.Cross(x, y).normalized;
            y = Vector3.Cross(z, x);
            var r = Matrix4x4.identity;
            r.SetColumn(0, x); r.SetColumn(1, y); r.SetColumn(2, z);
            return r;
        }

        private static void SetVector(FbxNode model, string name, string type, Vector3 value)
        {
            var properties = model.Child("Properties70") ?? model.Add("Properties70");
            var p = model.Property70(name);
            if (p == null) p = properties.Add("P", name, type, "", "A");
            while (p.Properties.Count > 4) p.Properties.RemoveAt(p.Properties.Count - 1);
            p.Properties.Add((double)value.x);
            p.Properties.Add((double)value.y);
            p.Properties.Add((double)value.z);
        }

        /// <summary>
        /// Replaces a model's mesh with the renderer's: control points (Unity's vertices merged back where only UVs, normals
        /// or colours split them), polygons by material slot, normals, UVs and colours by corner, a skin cluster per bone
        /// and a blendshape channel per shape. The old geometry's deformers go.
        /// </summary>
        public void ReplaceMesh(FbxNode model, SkinnedMeshRenderer renderer, Matrix4x4 toGeometry, Matrix4x4 meshFrame, Dictionary<string, Matrix4x4> bound)
        {
            var geometry = Children(model.Id).FirstOrDefault(o => o.Name == "Geometry" && o.Class == "Mesh")
                           ?? throw new InvalidDataException($"The model {model.ObjectName} has no mesh geometry.");
            string uvName = geometry.All("LayerElementUV").FirstOrDefault()?.Value<string>("Name") ?? "map1";
            // Unity's slots are the file's materials in the order its polygons first use them; a polygon names its material
            // by the model's material connections, so each slot is written as the connection it came from.
            var connectionOfSlot = new List<int>();
            foreach (int index in geometry.All("LayerElementMaterial").FirstOrDefault()?.Value<int[]>("Materials") ?? new int[0])
                if (!connectionOfSlot.Contains(index)) connectionOfSlot.Add(index);
            var oldSkin = Children(geometry.Id).FirstOrDefault(o => o.Name == "Deformer" && o.Class == "Skin");
            double[] associate = oldSkin != null
                ? Children(oldSkin.Id).Select(c => c.Value<double[]>("TransformAssociateModel")).FirstOrDefault(a => a != null)
                : null;
            string skinName = oldSkin?.Properties[1] as string ?? "Armature\0\u0001Deformer";
            string blendName = Children(geometry.Id).FirstOrDefault(o => o.Name == "Deformer" && o.Class == "BlendShape")?.Properties[1] as string
                               ?? geometry.ObjectName + "\0\u0001Deformer";
            RemoveDeformers(geometry.Id);

            var mesh = renderer.sharedMesh;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            var colors = mesh.colors;
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var weightStart = new int[vertices.Length];
            for (int v = 1; v < vertices.Length; v++) weightStart[v] = weightStart[v - 1] + bonesPerVertex[v - 1];

            // Control points: Unity's vertices at the same place with the same weights are one.
            var pointOf = new int[vertices.Length];
            var firstVertex = new List<int>();
            var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int v = 0; v < vertices.Length; v++)
            {
                var key = new System.Text.StringBuilder();
                key.Append(BitConverter.SingleToInt32Bits(vertices[v].x)).Append(',').Append(BitConverter.SingleToInt32Bits(vertices[v].y)).Append(',')
                   .Append(BitConverter.SingleToInt32Bits(vertices[v].z));
                for (int w = 0; w < bonesPerVertex[v]; w++)
                {
                    var bw = weights[weightStart[v] + w];
                    key.Append('|').Append(bw.boneIndex).Append(':').Append(BitConverter.SingleToInt32Bits(bw.weight));
                }
                string k = key.ToString();
                if (!lookup.TryGetValue(k, out int point)) { point = firstVertex.Count; lookup[k] = point; firstVertex.Add(v); }
                pointOf[v] = point;
            }
            var points = new double[firstVertex.Count * 3];
            for (int p = 0; p < firstVertex.Count; p++)
            {
                var position = toGeometry.MultiplyPoint3x4(vertices[firstVertex[p]]);
                points[p * 3] = position.x; points[p * 3 + 1] = position.y; points[p * 3 + 2] = position.z;
            }

            // Polygons (triangles), with the winding the mirror between the spaces needs, and their corners' attributes.
            bool mirrored = toGeometry.determinant < 0f;
            var normalMatrix = toGeometry.inverse.transpose;
            var polygonIndex = new List<int>();
            var materialOf = new List<int>();
            var cornerNormals = new List<double>();
            var uvList = new List<double>(); var uvIndex = new List<int>(); var uvLookup = new Dictionary<Vector2, int>();
            var colorList = new List<double>(); var colorIndex = new List<int>(); var colorLookup = new Dictionary<Color, int>();
            bool hasUv = uvs != null && uvs.Length == vertices.Length, hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasColors = colors != null && colors.Length == vertices.Length;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) throw new NotSupportedException($"{mesh.name} has a submesh that isn't triangles.");
                var triangles = mesh.GetTriangles(sub);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = mirrored ? triangles[t + 2] : triangles[t + 1], c = mirrored ? triangles[t + 1] : triangles[t + 2];
                    foreach (int corner in new[] { a, b, c })
                    {
                        polygonIndex.Add(corner == c ? ~pointOf[corner] : pointOf[corner]);
                        if (hasNormals)
                        {
                            var n = normalMatrix.MultiplyVector(normals[corner]).normalized;
                            cornerNormals.Add(n.x); cornerNormals.Add(n.y); cornerNormals.Add(n.z);
                        }
                        if (hasUv)
                        {
                            if (!uvLookup.TryGetValue(uvs[corner], out int u)) { u = uvLookup.Count; uvLookup[uvs[corner]] = u; uvList.Add(uvs[corner].x); uvList.Add(uvs[corner].y); }
                            uvIndex.Add(u);
                        }
                        if (hasColors)
                        {
                            if (!colorLookup.TryGetValue(colors[corner], out int col))
                            {
                                col = colorLookup.Count; colorLookup[colors[corner]] = col;
                                colorList.Add(colors[corner].r); colorList.Add(colors[corner].g); colorList.Add(colors[corner].b); colorList.Add(colors[corner].a);
                            }
                            colorIndex.Add(col);
                        }
                    }
                    materialOf.Add(sub < connectionOfSlot.Count ? connectionOfSlot[sub] : sub);
                }
            }

            // The geometry record, rebuilt in place (same id, name and model).
            var shapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToList();
            geometry.Children.Clear();
            var properties = geometry.Add("Properties70");
            foreach (string shape in shapes) properties.Add("P", shape, "Number", "", "A", 0.0);
            geometry.Add("GeometryVersion", 124);
            geometry.Add("Vertices", points);
            geometry.Add("PolygonVertexIndex", polygonIndex.ToArray());
            var layer = new FbxNode("Layer", 0);
            layer.Add("Version", 100);
            if (hasNormals) AddLayer(geometry, layer, "LayerElementNormal", "", "Direct", ("Normals", cornerNormals.ToArray()));
            if (hasColors) AddLayer(geometry, layer, "LayerElementColor", "colorSet0", "IndexToDirect", ("Colors", colorList.ToArray()), ("ColorIndex", colorIndex.ToArray()));
            if (hasUv) AddLayer(geometry, layer, "LayerElementUV", uvName, "IndexToDirect", ("UV", uvList.ToArray()), ("UVIndex", uvIndex.ToArray()));
            var material = geometry.Add("LayerElementMaterial", 0);
            material.Add("Version", 101);
            material.Add("Name", "");
            material.Add("MappingInformationType", "ByPolygon");
            material.Add("ReferenceInformationType", "IndexToDirect");
            material.Add("Materials", materialOf.ToArray());
            var materialEntry = layer.Add("LayerElement");
            materialEntry.Add("Type", "LayerElementMaterial");
            materialEntry.Add("TypedIndex", 0);
            geometry.Children.Add(layer);

            // Skin: a cluster per bone that holds weight.
            long skinId = Add(new FbxNode("Deformer", 0L, skinName, "Skin"));
            objects[skinId].Add("Version", 101);
            objects[skinId].Add("Link_DeformAcuracy", 50.0);
            Connect(skinId, geometry.Id);
            var perBone = new Dictionary<int, (List<int> points, List<double> weights)>();
            for (int p = 0; p < firstVertex.Count; p++)
            {
                int v = firstVertex[p];
                for (int w = 0; w < bonesPerVertex[v]; w++)
                {
                    var bw = weights[weightStart[v] + w];
                    if (bw.weight <= 0f) continue;
                    if (!perBone.TryGetValue(bw.boneIndex, out var list)) perBone[bw.boneIndex] = list = (new List<int>(), new List<double>());
                    list.points.Add(p); list.weights.Add(bw.weight);
                }
            }
            foreach (var pair in perBone.OrderBy(p => p.Key))
            {
                var bone = pair.Key < renderer.bones.Length ? renderer.bones[pair.Key] : null;
                var boneModel = bone ? Model(bone.name) : null;
                if (boneModel == null) throw new InvalidOperationException($"{mesh.name} is weighted to a bone the FBX doesn't have: {(bone ? bone.name : "#" + pair.Key)}.");
                long clusterId = Add(new FbxNode("Deformer", 0L, bone.name + "\0\u0001SubDeformer", "Cluster"));
                var cluster = objects[clusterId];
                cluster.Add("Version", 100);
                cluster.Add("UserData", "", "");
                cluster.Add("Indexes", pair.Value.points.ToArray());
                cluster.Add("Weights", pair.Value.weights.ToArray());
                var link = bound.TryGetValue(bone.name, out var bind) ? bind : Global(boneModel);
                // As Blender writes it: the mesh in the bone's space (FBX readers read it so, whatever the SDK says).
                cluster.Add("Transform", FbxTransform.ToDoubles(link.inverse * meshFrame));
                cluster.Add("TransformLink", FbxTransform.ToDoubles(link));
                cluster.Add("TransformAssociateModel", associate ?? FbxTransform.ToDoubles(Matrix4x4.identity));
                Connect(clusterId, skinId);
                Connect(boneModel.Id, clusterId);
            }

            // Blendshapes: a channel per shape, its full-weight frame as the shape.
            if (shapes.Count == 0) return;
            long blendId = Add(new FbxNode("Deformer", 0L, blendName, "BlendShape"));
            objects[blendId].Add("Version", 100);
            Connect(blendId, geometry.Id);
            var deltas = new Vector3[vertices.Length];
            var deltaNormals = new Vector3[vertices.Length];
            var deltaTangents = new Vector3[vertices.Length];
            for (int s = 0; s < shapes.Count; s++)
            {
                int frame = mesh.GetBlendShapeFrameCount(s) - 1;
                mesh.GetBlendShapeFrameVertices(s, frame, deltas, deltaNormals, deltaTangents);
                var indexes = new List<int>();
                var offsets = new List<double>();
                var normalOffsets = new List<double>();
                for (int p = 0; p < firstVertex.Count; p++)
                {
                    var d = deltas[firstVertex[p]];
                    if (d.sqrMagnitude < 1e-14f) continue;
                    var moved = toGeometry.MultiplyVector(d);
                    var turned = normalMatrix.MultiplyVector(deltaNormals[firstVertex[p]]);
                    indexes.Add(p);
                    offsets.Add(moved.x); offsets.Add(moved.y); offsets.Add(moved.z);
                    normalOffsets.Add(turned.x); normalOffsets.Add(turned.y); normalOffsets.Add(turned.z);
                }
                // A shape that moves nothing still gets a point: an empty one would be dropped.
                if (indexes.Count == 0) { indexes.Add(0); offsets.AddRange(new[] { 0d, 0d, 0d }); normalOffsets.AddRange(new[] { 0d, 0d, 0d }); }
                long channelId = Add(new FbxNode("Deformer", 0L, shapes[s] + "\0\u0001SubDeformer", "BlendShapeChannel"));
                objects[channelId].Add("Version", 100);
                objects[channelId].Add("DeformPercent", 0.0);
                objects[channelId].Add("FullWeights", Enumerable.Repeat(100.0, indexes.Count).ToArray());
                Connect(channelId, blendId);
                long shapeId = Add(new FbxNode("Geometry", 0L, shapes[s] + "\0\u0001Geometry", "Shape"));
                var shapeNode = objects[shapeId];
                shapeNode.Add("Properties70");
                shapeNode.Add("Version", 100);
                shapeNode.Add("Indexes", indexes.ToArray());
                shapeNode.Add("Vertices", offsets.ToArray());
                shapeNode.Add("Normals", normalOffsets.ToArray());
                Connect(shapeId, channelId);
            }
        }

        private static void AddLayer(FbxNode geometry, FbxNode layer, string type, string name, string reference, params (string Name, object Values)[] arrays)
        {
            var element = geometry.Add(type, 0);
            element.Add("Version", 101);
            element.Add("Name", name);
            element.Add("MappingInformationType", "ByPolygonVertex");
            element.Add("ReferenceInformationType", reference);
            foreach (var (arrayName, values) in arrays) element.Add(arrayName, values);
            var entry = layer.Add("LayerElement");
            entry.Add("Type", type);
            entry.Add("TypedIndex", 0);
        }

        private IEnumerable<FbxNode> Children(long parent) =>
            document.Connections().Where(c => c.Parent == parent && objects.ContainsKey(c.Child)).Select(c => objects[c.Child]);

        // A geometry's deformers, their clusters, channels and shape geometries, with every connection to them.
        private void RemoveDeformers(long geometryId)
        {
            var gone = new HashSet<long>();
            var queue = new Queue<long>(Children(geometryId).Where(o => o.Name == "Deformer").Select(o => o.Id));
            while (queue.Count > 0)
            {
                long id = queue.Dequeue();
                if (!gone.Add(id)) continue;
                foreach (var child in Children(id).Where(o => o.Name == "Deformer" || o.Name == "Geometry" && o.Class == "Shape")) queue.Enqueue(child.Id);
            }
            objectsNode.Children.RemoveAll(n => gone.Contains(n.Id));
            connectionsNode.Children.RemoveAll(c => c.Name == "C" && c.Properties.Count >= 3 &&
                                                    (c.Properties[1] is long child && gone.Contains(child) || c.Properties[2] is long parent && gone.Contains(parent)));
            foreach (long id in gone) objects.Remove(id);
        }

        private long Add(FbxNode node)
        {
            long id = document.NewId(used);
            node.Properties[0] = id;
            objectsNode.Children.Add(node);
            objects[id] = node;
            return id;
        }

        private void Connect(long child, long parent) => connectionsNode.Add("C", "OO", child, parent);
    }
}
#endif
