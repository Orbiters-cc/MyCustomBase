using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>What a version is compared with.</summary>
internal enum CompareReference
{
    /// <summary>The avatar as it is in the scene now.</summary>
    AvatarNow,
    /// <summary>The avatar with its original model, as with no version applied.</summary>
    Original
}

/// <summary>
/// A mesh as plain arrays in the avatar root's space, safe to use off the main thread: drawable vertices (with their UVs
/// and submeshes) and the points and triangles compared, which for a mesh read from an FBX are its control points.
/// </summary>
internal sealed class MeshSource
{
    public Vector3[] Vertices;
    public readonly List<Vector4>[] Uvs = new List<Vector4>[4];
    public int[][] SubMeshes = new int[0][];
    /// <summary>The point each drawable vertex comes from; null when the vertices are the points.</summary>
    public int[] VertexPoint;
    public Vector3[] Points;
    public int[] Triangles;
    /// <summary>Full-weight blendshape offsets, by point.</summary>
    public readonly Dictionary<string, (int[] points, Vector3[] offsets)> Shapes = new Dictionary<string, (int[] points, Vector3[] offsets)>(StringComparer.Ordinal);
    public string[] ShapeNames = new string[0];
    /// <summary>Read from an FBX: blendshape name → signature of its offsets.</summary>
    public Dictionary<string, long> ShapeSignatures;
    /// <summary>Read from an FBX: the material of each slot (submesh).</summary>
    public string[] Materials;
    public bool FromFile;

    public static MeshSource FromFbx(FbxMesh mesh, Matrix4x4 toRoot)
    {
        var source = new MeshSource { FromFile = true, Materials = mesh.Materials, ShapeSignatures = mesh.Shapes, ShapeNames = mesh.Shapes.Keys.ToArray() };
        source.Points = new Vector3[mesh.Points.Length];
        for (int i = 0; i < mesh.Points.Length; i++) source.Points[i] = toRoot.MultiplyPoint3x4(mesh.Points[i]);
        source.Triangles = mesh.Triangles;
        source.VertexPoint = mesh.RenderPoint;
        source.Vertices = source.Map(source.Points);
        source.SubMeshes = mesh.SubMeshes.ToArray();
        source.Uvs[0] = mesh.RenderUvs.Select(uv => new Vector4(uv.x, uv.y, 0f, 0f)).ToList();
        foreach (var shape in mesh.ShapeOffsets ?? new Dictionary<string, (int[] points, Vector3[] offsets)>())
        {
            var offsets = new Vector3[shape.Value.offsets.Length];
            for (int i = 0; i < offsets.Length; i++) offsets[i] = toRoot.MultiplyVector(shape.Value.offsets[i]);
            source.Shapes[shape.Key] = (shape.Value.points, offsets);
        }
        return source;
    }

    /// <summary>A Unity mesh (main thread). Only the blendshapes in <paramref name="shapes"/> are read, the others named.</summary>
    public static MeshSource FromMesh(Mesh mesh, Matrix4x4 toRoot, ICollection<string> shapes)
    {
        var source = new MeshSource { ShapeNames = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray() };
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++) vertices[i] = toRoot.MultiplyPoint3x4(vertices[i]);
        source.Vertices = source.Points = vertices;
        for (int channel = 0; channel < source.Uvs.Length; channel++)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel)) continue;
            var uvs = new List<Vector4>();
            mesh.GetUVs(channel, uvs);
            source.Uvs[channel] = uvs;
        }
        source.SubMeshes = Enumerable.Range(0, mesh.subMeshCount)
            .Select(i => mesh.GetTopology(i) == MeshTopology.Triangles ? mesh.GetTriangles(i) : new int[0]).ToArray();
        source.Triangles = source.SubMeshes.SelectMany(t => t).ToArray();
        var deltas = new Vector3[vertices.Length];
        foreach (string name in shapes ?? new string[0])
        {
            int index = mesh.GetBlendShapeIndex(name);
            if (index < 0 || mesh.GetBlendShapeFrameCount(index) == 0) continue;
            mesh.GetBlendShapeFrameVertices(index, mesh.GetBlendShapeFrameCount(index) - 1, deltas, null, null);
            source.Shapes[name] = Sparse(deltas, toRoot);
        }
        return source;
    }

    /// <summary>An advanced mesh read off the main thread.</summary>
    public static MeshSource FromPrepared(NativeMeshPayloadService.PreparedMeshData mesh, Matrix4x4 toRoot)
    {
        var source = new MeshSource { ShapeNames = mesh.blendShapes.Select(shape => shape.name).ToArray() };
        var vertices = new Vector3[mesh.vertices.Length];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = toRoot.MultiplyPoint3x4(mesh.vertices[i]);
        source.Vertices = source.Points = vertices;
        for (int channel = 0; channel < source.Uvs.Length && channel < mesh.uvs.Length; channel++)
            if (mesh.uvs[channel] != null && mesh.uvs[channel].Count == vertices.Length) source.Uvs[channel] = mesh.uvs[channel];
        source.SubMeshes = mesh.subMeshes.Select(sub => sub.topology == MeshTopology.Triangles ? sub.indices : new int[0]).ToArray();
        source.Triangles = source.SubMeshes.SelectMany(t => t).ToArray();
        foreach (var shape in mesh.blendShapes)
        {
            var last = shape.frames.LastOrDefault();
            if (last?.deltaVertices != null) source.Shapes[shape.name] = Sparse(last.deltaVertices, toRoot);
        }
        return source;
    }

    private static (int[] points, Vector3[] offsets) Sparse(Vector3[] deltas, Matrix4x4 toRoot)
    {
        var points = new List<int>();
        var offsets = new List<Vector3>();
        for (int i = 0; i < deltas.Length; i++)
        {
            if (deltas[i].sqrMagnitude < 1e-14f) continue;
            points.Add(i);
            offsets.Add(toRoot.MultiplyVector(deltas[i]));
        }
        return (points.ToArray(), offsets.ToArray());
    }

    /// <summary>The points with blendshapes at the given weights (0 to 100).</summary>
    public Vector3[] Weighted(IReadOnlyDictionary<string, float> weights)
    {
        var result = (Vector3[])Points.Clone();
        foreach (var shape in Shapes)
        {
            if (!weights.TryGetValue(shape.Key, out float weight) || Mathf.Abs(weight) < 0.01f) continue;
            float factor = weight / 100f;
            var (points, offsets) = shape.Value;
            for (int k = 0; k < points.Length; k++)
                if (points[k] < result.Length) result[points[k]] += offsets[k] * factor;
        }
        return result;
    }

    /// <summary>Per drawable vertex, from per point values.</summary>
    public T[] Map<T>(T[] perPoint)
    {
        if (VertexPoint == null) return perPoint;
        var result = new T[VertexPoint.Length];
        for (int i = 0; i < result.Length; i++) result[i] = perPoint[VertexPoint[i]];
        return result;
    }

    /// <summary>
    /// Smooth normals over the surface, welded across UV seams, so both sides of a comparison shade the same wherever their
    /// shape is the same, whichever file they come from.
    /// </summary>
    public Vector3[] Normals(Vector3[] weightedPoints)
    {
        int[] weld;
        int count;
        if (VertexPoint != null)
        {
            weld = null;
            count = weightedPoints.Length;
        }
        else
        {
            var ids = new Dictionary<Vector3, int>();
            weld = new int[weightedPoints.Length];
            for (int i = 0; i < weld.Length; i++)
            {
                if (!ids.TryGetValue(Points[i], out int id)) ids[Points[i]] = id = ids.Count;
                weld[i] = id;
            }
            count = ids.Count;
        }

        var sums = new Vector3[count];
        for (int t = 0; t + 2 < Triangles.Length; t += 3)
        {
            int a = Triangles[t], b = Triangles[t + 1], c = Triangles[t + 2];
            var normal = Vector3.Cross(weightedPoints[b] - weightedPoints[a], weightedPoints[c] - weightedPoints[a]);
            if (weld != null) { a = weld[a]; b = weld[b]; c = weld[c]; }
            sums[a] += normal; sums[b] += normal; sums[c] += normal;
        }
        var perPoint = new Vector3[weightedPoints.Length];
        for (int i = 0; i < perPoint.Length; i++) perPoint[i] = (weld != null ? sums[weld[i]] : sums[i]).normalized;
        return Map(perPoint);
    }
}

/// <summary>One mesh of the avatar, before and after the version.</summary>
internal sealed class ComparedPart
{
    public string Key = string.Empty;
    public string Name = string.Empty;
    public MeshSource Before, After;
    public Material[] BeforeMaterials = new Material[0], AfterMaterials = new Material[0];
    public PartChange Change;
    public float MaxDistance;
    public int MovedPoints, PointCount;
    /// <summary>Where it changed, in the avatar root's space: what the camera frames when this part is focused.</summary>
    public Bounds Focus;
    public readonly List<string> ShapesAdded = new List<string>(), ShapesRemoved = new List<string>(), ShapesChanged = new List<string>();
    public Mesh BeforeMesh, AfterMesh;

    internal Vector3[] BeforeVertices, BeforeNormals, AfterVertices, AfterNormals;
    internal List<Vector4> BeforeHeat, AfterHeat;
    internal float[] BeforeDistances, AfterDistances;

    public bool Changed => Change != PartChange.Same;
    public float ChangedShare => PointCount > 0 ? (float)MovedPoints / PointCount : Change == PartChange.Added || Change == PartChange.Removed ? 1f : 0f;
}

/// <summary>A renderer the version does not touch, shown around the compared parts (clothes, accessories).</summary>
internal sealed class ContextPart
{
    public Mesh Mesh;
    public Matrix4x4 Matrix;
    public Material[] Materials;
    public bool Owned;
}

/// <summary>
/// The meshes a version would put on the avatar, compared with the avatar now or with its original model. Loading reads
/// the downloaded version without changing the project: FBX replacements are decoded in memory (HDiff through a temporary
/// file), advanced meshes decrypted and parsed in memory, the models read with Toolkit's <see cref="FbxReader"/>. Parts are
/// shown with the avatar's blendshape values and materials, so the comparison is what the creator will see on their avatar.
/// </summary>
internal sealed class VersionMeshComparison : IDisposable
{
    // Heat channel of the drawn meshes (TEXCOORD7): x how much the surface moved (0 to 1), y what it is.
    public const float Unchanged = 0f, Reshaped = 1f, AddedPart = 2f, RemovedPart = 3f;

    private readonly VersionActions actions;
    private readonly MyCustomBase target;
    private readonly object gate = new object();
    private string step = "Preparing…";
    private float progress;

    private readonly List<PartSources> sources = new List<PartSources>();
    private bool contextBuilt;

    public readonly CustomBaseVersion Version;
    public readonly List<ComparedPart> Parts = new List<ComparedPart>();
    public readonly List<ContextPart> Context = new List<ContextPart>();
    public CompareReference Reference { get; private set; }
    /// <summary>Whether the avatar now differs from its original model (a version is applied).</summary>
    public bool AvatarHasVersion { get; private set; }
    public Bounds Bounds { get; private set; }
    /// <summary>The largest change of any part: the red end of the heat scale.</summary>
    public float MaxDistance { get; private set; }
    public float Threshold { get; private set; }
    public bool Ready { get; private set; }

    public string Step { get { lock (gate) return step; } }
    public float Progress { get { lock (gate) return progress; } }

    private sealed class PartSources
    {
        public string Key, Name;
        public MeshSource Now, Original, After;
        public Material[] NowMaterials = new Material[0];
        public Material[] AfterMaterials = new Material[0];
        public Material[] OriginalMaterials = new Material[0];
        /// <summary>The renderer's blendshape values, and the version's: the same, with its new blendshapes at their defaults.</summary>
        public Dictionary<string, float> Weights = new Dictionary<string, float>(StringComparer.Ordinal);
        public Dictionary<string, float> AfterWeights = new Dictionary<string, float>(StringComparer.Ordinal);
    }

    private sealed class FbxTarget
    {
        public string Path, OriginalPath, SourceHash;
        public VersionActions.VersionModelPatch Replacement;
        public readonly List<VersionActions.VersionModelPatch> Advanced = new List<VersionActions.VersionModelPatch>();
        public List<ModelFileSmrPathData> SmrPaths;
        public Matrix4x4 ToRoot = Matrix4x4.identity;
        public byte[] AfterBytes;
        public List<FbxMesh> OriginalMeshes, NowMeshes, AfterMeshes;
    }

    public VersionMeshComparison(VersionActions actions, MyCustomBase target, CustomBaseVersion version)
    {
        this.actions = actions;
        this.target = target;
        Version = version;
    }

    private void Report(float value, string text)
    {
        lock (gate)
        {
            progress = Mathf.Clamp01(value);
            if (!string.IsNullOrEmpty(text)) step = text;
        }
    }

    /// <summary>
    /// Loads and compares, stepped by the caller once per editor tick (main thread): the heavy steps run on worker threads
    /// while it yields. Throws with a message for the creator when the version cannot be read.
    /// </summary>
    public IEnumerator Load(CompareReference reference)
    {
        if (target == null) throw new InvalidOperationException("The avatar is no longer in the scene.");
        Transform root = target.transform.root;
        Report(0.02f, "Finding the version's meshes…");
        var patches = actions.ResolveModelPatches(Version);
        if (patches.Count == 0) throw new InvalidOperationException("This version does not change any mesh.");

        var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var targets = new List<FbxTarget>();
        foreach (var group in patches.GroupBy(p => MCBUtils.ToUnityPath(p.TargetFbxPath), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var fbx = new FbxTarget
            {
                Path = group.Key,
                OriginalPath = first.OriginalFbxPath,
                SourceHash = first.Source?.hash,
                Replacement = group.FirstOrDefault(p => !p.AdvancedMesh),
                SmrPaths = first.Source?.smrPaths ?? new List<ModelFileSmrPathData>()
            };
            fbx.Advanced.AddRange(group.Where(p => p.AdvancedMesh));
            fbx.ToRoot = FbxToRoot(root, fbx, renderers);
            targets.Add(fbx);
        }

        // Advanced meshes an earlier apply cached are read from the cache (main thread); the others are decoded below.
        var cachedPayloads = new Dictionary<VersionActions.VersionModelPatch, NativeMeshPayloadAsset>();
        foreach (var patch in targets.SelectMany(t => t.Advanced))
        {
            var cached = NativeMeshPayloadService.FindCachedPayload(Version, patch.Patch);
            if (cached != null) cachedPayloads[patch] = cached;
        }

        // HDiff runs its native patcher on the main thread; XOR replacements decode on a worker below.
        foreach (var fbx in targets.Where(t => t.Replacement != null && t.Replacement.Hdiff))
        {
            Report(0.08f, "Unpacking the version…");
            yield return null;
            fbx.AfterBytes = DecodeHdiff(fbx.Replacement);
        }

        Report(0.12f, "Reading the models…");
        var prepared = new Dictionary<VersionActions.VersionModelPatch, NativeMeshPayloadService.PreparedPayloadAssetData>();
        var read = Task.Run(() =>
        {
            int done = 0, total = Math.Max(1, targets.Count);
            foreach (var fbx in targets)
            {
                byte[] original = File.ReadAllBytes(fbx.OriginalPath);
                if (!string.IsNullOrWhiteSpace(fbx.SourceHash) && !string.Equals(Sha256(original), fbx.SourceHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The original model '{Path.GetFileName(fbx.Path)}' is not the one this version was made for, so its meshes cannot be shown. Reset the avatar to its original model, then try again.");
                if (fbx.Replacement != null && !fbx.Replacement.Hdiff)
                    fbx.AfterBytes = MCBXor.Transform(original, File.ReadAllBytes(fbx.Replacement.BinPath));
                Report(0.2f + 0.2f * done / total, "Reading the models…");

                const FbxReadOptions options = FbxReadOptions.Render | FbxReadOptions.ShapeOffsets;
                var parseOriginal = Task.Run(() => FbxReader.Read(original, options));
                var parseAfter = fbx.AfterBytes != null ? Task.Run(() => FbxReader.Read(fbx.AfterBytes, options)) : null;
                // The model the avatar has now: the original itself while no replacement is applied.
                byte[] now = string.Equals(Path.GetFullPath(fbx.Path), Path.GetFullPath(fbx.OriginalPath), StringComparison.OrdinalIgnoreCase) ? original : File.ReadAllBytes(fbx.Path);
                var parseNow = now == original || now.AsSpan().SequenceEqual(original) ? null : Task.Run(() => FbxReader.Read(now, options));
                fbx.OriginalMeshes = parseOriginal.Result;
                fbx.AfterMeshes = parseAfter?.Result;
                fbx.NowMeshes = parseNow?.Result ?? fbx.OriginalMeshes;
                if (fbx.AfterBytes != null && fbx.AfterMeshes.Count == 0)
                    throw new InvalidDataException($"The version's model '{Path.GetFileName(fbx.Path)}' could not be read.");

                foreach (var patch in fbx.Advanced.Where(p => !cachedPayloads.ContainsKey(p)))
                {
                    Report(0.4f + 0.2f * done / total, "Unpacking the advanced meshes…");
                    prepared[patch] = NativeMeshPayloadService.ReadPayloadForPreview(patch.Patch, patch.BinPath, fbx.OriginalPath);
                }
                done++;
            }
        });
        while (!read.IsCompleted) yield return null;
        if (read.IsFaulted) throw read.Exception.GetBaseException();

        Report(0.62f, "Matching the avatar's meshes…");
        yield return null;
        Gather(root, targets, cachedPayloads, prepared);
        AvatarHasVersion = sources.Any(s => s.Now != s.Original);

        var compare = Run(reference);
        while (compare.MoveNext()) yield return compare.Current;
    }

    /// <summary>Compares with another reference; the models are already read.</summary>
    public IEnumerator Run(CompareReference reference)
    {
        Ready = false;
        Reference = reference;
        Report(0.7f, reference == CompareReference.Original ? "Comparing with the original…" : "Comparing with your avatar…");
        var work = Task.Run(() => Compare(reference));
        while (!work.IsCompleted) yield return null;
        if (work.IsFaulted) throw work.Exception.GetBaseException();

        Report(0.96f, "Preparing the preview…");
        yield return null;
        DestroyMeshes();
        Parts.Clear();
        Parts.AddRange(work.Result);
        foreach (var part in Parts)
        {
            part.BeforeMesh = CreateMesh(part.Name, part.Before, part.BeforeVertices, part.BeforeNormals, part.BeforeHeat);
            part.AfterMesh = CreateMesh(part.Name, part.After, part.AfterVertices, part.AfterNormals, part.AfterHeat);
            part.BeforeVertices = part.BeforeNormals = part.AfterVertices = part.AfterNormals = null;
            part.BeforeHeat = part.AfterHeat = null;
        }
        Report(1f, "Ready");
        Ready = true;
    }

    // Where the FBX's root is in the avatar: found from a renderer the FBX's meshes are on, and scaled like its import.
    private static Matrix4x4 FbxToRoot(Transform root, FbxTarget fbx, SkinnedMeshRenderer[] renderers)
    {
        var importer = AssetImporter.GetAtPath(fbx.Path) as ModelImporter;
        float scale = importer == null ? 1f : importer.globalScale * (importer.useFileScale ? 1f : 1f / Mathf.Max(importer.fileScale, 1e-6f));
        Transform fbxRoot = root;
        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.sharedMesh == null || !string.Equals(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)), fbx.Path, StringComparison.OrdinalIgnoreCase)) continue;
            string meshPath = fbx.SmrPaths.FirstOrDefault(entry => entry != null && string.Equals(entry.rendererName, renderer.name, StringComparison.Ordinal))?.fbxMeshPath ?? renderer.name;
            var node = renderer.transform;
            for (int depth = meshPath.Split('/').Length; depth > 0 && node != null && node != root; depth--) node = node.parent;
            if (node != null) fbxRoot = node;
            break;
        }
        return root.worldToLocalMatrix * fbxRoot.localToWorldMatrix * Matrix4x4.Scale(Vector3.one * scale);
    }

    private static byte[] DecodeHdiff(VersionActions.VersionModelPatch patch)
    {
        string output = HdiffService.CreateTempWorkPath(".fbx");
        try
        {
            HdiffService.ApplyXorEncryptedPatchToTempFbx(patch.OriginalFbxPath, patch.BinPath, output, new FileManagerService());
            return File.ReadAllBytes(output);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch (IOException) { }
        }
    }

    private static string Sha256(byte[] bytes)
    {
        using (var sha = MCBHashing.CreateSha256())
        {
            var hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }

    // Main thread: pairs each mesh of the version with the avatar's renderer, and reads what only the scene has (the
    // renderer's current mesh when it is not the FBX's, its materials and blendshape values).
    private void Gather(Transform root, List<FbxTarget> targets,
        Dictionary<VersionActions.VersionModelPatch, NativeMeshPayloadAsset> cachedPayloads,
        Dictionary<VersionActions.VersionModelPatch, NativeMeshPayloadService.PreparedPayloadAssetData> prepared)
    {
        sources.Clear();
        var byKey = new Dictionary<string, PartSources>(StringComparer.Ordinal);
        var rendererOf = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.Ordinal);
        // Blendshapes new to the avatar start at the value the creator set for them.
        var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var entry in Version.customBlendshapes ?? new CustomBlendshapeEntry[0])
            if (entry != null && !string.IsNullOrWhiteSpace(entry.name) &&
                float.TryParse(entry.defaultValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
                defaults[entry.name] = value;

        PartSources Part(string key, string name)
        {
            if (!byKey.TryGetValue(key, out var part))
            {
                byKey[key] = part = new PartSources { Key = key, Name = name };
                sources.Add(part);
            }
            return part;
        }

        void ReadRenderer(string key, SkinnedMeshRenderer renderer)
        {
            if (renderer == null || rendererOf.ContainsKey(key)) return;
            rendererOf[key] = renderer;
            var part = byKey[key];
            var mesh = renderer.sharedMesh;
            for (int i = 0; mesh != null && i < mesh.blendShapeCount; i++)
            {
                float weight = renderer.GetBlendShapeWeight(i);
                if (Mathf.Abs(weight) > 0.01f) part.Weights[mesh.GetBlendShapeName(i)] = weight;
            }
            part.AfterWeights = new Dictionary<string, float>(part.Weights, StringComparer.Ordinal);
            foreach (var pair in defaults)
                if (Math.Abs(pair.Value) > 0.01f && (mesh == null || mesh.GetBlendShapeIndex(pair.Key) < 0)) part.AfterWeights[pair.Key] = pair.Value;
        }

        // Unity meshes are read with only the blendshapes that are on.
        ICollection<string> Shapes(string key) => byKey[key].Weights.Keys.Union(byKey[key].AfterWeights.Keys).ToList();

        foreach (var fbx in targets)
        {
            // Every mesh of the FBX, by its path in the file.
            var paths = new List<string>();
            foreach (var list in new[] { fbx.OriginalMeshes, fbx.NowMeshes, fbx.AfterMeshes })
                foreach (var mesh in list ?? new List<FbxMesh>())
                    if (!paths.Contains(mesh.Path)) paths.Add(mesh.Path);

            var payloadKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var patch in fbx.Advanced)
            {
                if (cachedPayloads.TryGetValue(patch, out var cached))
                {
                    foreach (var record in cached.renderers.Where(r => r?.mesh != null))
                    {
                        var renderer = NativeMeshPayloadService.ResolveAvatarRenderer(root, record);
                        if (renderer == null) continue;
                        string key = KeyOf(root, renderer.transform);
                        payloadKeys.Add(key);
                        var part = Part(key, renderer.name);
                        ReadRenderer(key, renderer);
                        var toRoot = root.worldToLocalMatrix * ParentMatrix(renderer.transform) * Matrix4x4.TRS(record.localPosition, record.localRotation, record.localScale);
                        part.After = MeshSource.FromMesh(record.mesh, toRoot, Shapes(key));
                    }
                }
                else if (prepared.TryGetValue(patch, out var data))
                {
                    foreach (var record in data.renderers.Where(r => r?.mesh != null))
                    {
                        var renderer = NativeMeshPayloadService.ResolveAvatarRenderer(root, new NativeMeshPayloadRenderer { avatarPath = record.avatarPath });
                        if (renderer == null) continue;
                        string key = KeyOf(root, renderer.transform);
                        payloadKeys.Add(key);
                        var part = Part(key, renderer.name);
                        ReadRenderer(key, renderer);
                        var toRoot = root.worldToLocalMatrix * ParentMatrix(renderer.transform) * Matrix4x4.TRS(record.localPosition, record.localRotation, record.localScale);
                        part.After = MeshSource.FromPrepared(record.mesh, toRoot);
                    }
                }
            }

            foreach (string path in paths)
            {
                var renderer = FindRenderer(root, fbx, path);
                var inOriginal = fbx.OriginalMeshes.FirstOrDefault(m => m.Path == path);
                var inNow = fbx.NowMeshes.FirstOrDefault(m => m.Path == path);
                var inAfter = fbx.AfterMeshes != null ? fbx.AfterMeshes.FirstOrDefault(m => m.Path == path) : inOriginal;
                // A mesh the creator removed from their avatar stays removed whatever the version: nothing to show.
                if (renderer == null && (inNow != null || inOriginal != null)) continue;
                if (inNow == null && inOriginal == null && inAfter == null) continue;

                string key = renderer != null ? KeyOf(root, renderer.transform) : path;
                var part = Part(key, renderer != null ? renderer.name : inAfter.Name);
                ReadRenderer(key, renderer);
                if (renderer == null)
                    foreach (var pair in defaults) if (Math.Abs(pair.Value) > 0.01f) part.AfterWeights[pair.Key] = pair.Value;
                if (inOriginal != null) part.Original = MeshSource.FromFbx(inOriginal, fbx.ToRoot);
                if (!payloadKeys.Contains(key) && inAfter != null) part.After = inAfter == inOriginal ? part.Original : MeshSource.FromFbx(inAfter, fbx.ToRoot);
                // The avatar shows this FBX's mesh unless another mesh (an advanced version) is on its renderer.
                bool showsFbx = renderer == null || renderer.sharedMesh == null ||
                                string.Equals(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)), fbx.Path, StringComparison.OrdinalIgnoreCase);
                if (showsFbx) part.Now = inNow == inOriginal ? part.Original : inNow != null ? MeshSource.FromFbx(inNow, fbx.ToRoot) : null;
            }

            // Renderers an advanced version puts a mesh on that are not in the FBX.
            foreach (string key in payloadKeys)
            {
                var part = byKey[key];
                if (part.Original == null && fbx.OriginalMeshes.Count > 0 && rendererOf.TryGetValue(key, out var renderer))
                {
                    var mesh = fbx.OriginalMeshes.FirstOrDefault(m => m.Name == renderer.name);
                    if (mesh != null) part.Original = MeshSource.FromFbx(mesh, fbx.ToRoot);
                }
            }
        }

        // Renderers whose current mesh is not read from a file (an advanced version applied): the scene's mesh.
        foreach (var part in sources)
        {
            if (!rendererOf.TryGetValue(part.Key, out var renderer)) continue;
            var materials = renderer.sharedMaterials;
            part.NowMaterials = part.OriginalMaterials = part.AfterMaterials = materials;
            if (part.Now == null && renderer.sharedMesh != null)
            {
                var toRoot = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                part.Now = MeshSource.FromMesh(renderer.sharedMesh, toRoot, part.Weights.Keys.ToList());
            }
            // Slots of a file are matched by material name when both know them, as Unity's importer does.
            if (part.Now?.Materials != null)
            {
                part.AfterMaterials = MatchMaterials(part.Now.Materials, materials, part.After?.Materials);
                part.OriginalMaterials = MatchMaterials(part.Now.Materials, materials, part.Original?.Materials);
            }
        }
    }

    private static Material[] MatchMaterials(string[] slots, Material[] materials, string[] wanted)
    {
        if (wanted == null) return materials;
        var result = new Material[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
        {
            int slot = Array.IndexOf(slots, wanted[i]);
            result[i] = slot >= 0 && slot < materials.Length ? materials[slot] : i < materials.Length ? materials[i] : null;
        }
        return result;
    }

    private static Matrix4x4 ParentMatrix(Transform transform) => transform.parent != null ? transform.parent.localToWorldMatrix : Matrix4x4.identity;

    private static string KeyOf(Transform root, Transform transform)
    {
        var names = new List<string>();
        for (var current = transform; current != null && current != root; current = current.parent) names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private static SkinnedMeshRenderer FindRenderer(Transform root, FbxTarget fbx, string fbxMeshPath)
    {
        string avatarPath = fbx.SmrPaths.FirstOrDefault(entry => entry != null && entry.fbxMeshPath == fbxMeshPath)?.avatarPath ?? fbxMeshPath;
        var renderer = NativeMeshPayloadService.ResolveAvatarRenderer(root, new NativeMeshPayloadRenderer { avatarPath = avatarPath });
        if (renderer != null || avatarPath == fbxMeshPath) return renderer;
        return NativeMeshPayloadService.ResolveAvatarRenderer(root, new NativeMeshPayloadRenderer { avatarPath = fbxMeshPath });
    }

    // Worker thread: distances both ways, heat, and the vertices and normals to draw.
    private List<ComparedPart> Compare(CompareReference reference)
    {
        var parts = new List<ComparedPart>();
        var weightsOf = new Dictionary<ComparedPart, PartSources>();
        foreach (var source in sources)
        {
            var before = reference == CompareReference.Original ? source.Original : source.Now;
            if (before == null && source.After == null) continue;
            var part = new ComparedPart
            {
                Key = source.Key,
                Name = source.Name,
                Before = before,
                After = source.After,
                BeforeMaterials = reference == CompareReference.Original ? source.OriginalMaterials : source.NowMaterials,
                AfterMaterials = source.AfterMaterials
            };
            parts.Add(part);
            weightsOf[part] = source;
        }

        var weighted = new Dictionary<ComparedPart, (Vector3[] before, Vector3[] after)>();
        bool hasBounds = false;
        var bounds = new Bounds();
        foreach (var part in parts)
        {
            var b = part.Before?.Weighted(weightsOf[part].Weights);
            var a = part.After?.Weighted(weightsOf[part].AfterWeights);
            weighted[part] = (b, a);
            foreach (var points in new[] { b, a })
            {
                if (points == null || points.Length == 0) continue;
                var partBounds = BoundsOf(points);
                if (hasBounds) bounds.Encapsulate(partBounds);
                else { bounds = partBounds; hasBounds = true; }
            }
        }
        if (!hasBounds) bounds = new Bounds(Vector3.up, Vector3.one);
        float size = Mathf.Max(bounds.size.magnitude, 1e-4f);
        float threshold = Mathf.Max(size * 2e-5f, 1e-6f);

        // Parts one by one: each search already uses half the cores, the other half keeps the editor smooth.
        foreach (var part in parts)
        {
            var (b, a) = weighted[part];
            if (b == null)
            {
                part.Change = PartChange.Added;
                part.PointCount = part.MovedPoints = a.Length;
                part.Focus = BoundsOf(a);
                continue;
            }
            if (a == null)
            {
                part.Change = PartChange.Removed;
                part.PointCount = part.MovedPoints = b.Length;
                part.Focus = BoundsOf(b);
                continue;
            }

            bool sameTopology = part.Before.FromFile == part.After.FromFile && b.Length == a.Length;
            part.AfterDistances = sameTopology ? MeshComparison.Distances(b, a, size) : MeshComparison.SurfaceDistances(b, part.Before.Triangles, a, size);
            part.BeforeDistances = sameTopology ? part.AfterDistances : MeshComparison.SurfaceDistances(a, part.After.Triangles, b, size);
            // Measured to the other surface, a re-exported mesh is never exactly on it: allow for that.
            float moved = sameTopology ? threshold : Mathf.Max(threshold, size * 5e-4f);
            part.PointCount = a.Length;
            var changed = new List<Vector3>();
            for (int i = 0; i < a.Length; i++)
            {
                float distance = part.AfterDistances[i];
                if (distance <= moved) { part.AfterDistances[i] = 0f; continue; }
                part.MovedPoints++;
                part.MaxDistance = Mathf.Max(part.MaxDistance, distance);
                changed.Add(a[i]);
            }
            for (int i = 0; i < part.BeforeDistances.Length; i++)
            {
                if (part.BeforeDistances[i] <= moved) { if (!sameTopology) part.BeforeDistances[i] = 0f; continue; }
                if (!sameTopology) { part.MaxDistance = Mathf.Max(part.MaxDistance, part.BeforeDistances[i]); changed.Add(b[i]); }
            }
            part.Focus = changed.Count > 0 ? BoundsOf(changed) : BoundsOf(a);

            part.ShapesAdded.AddRange(part.After.ShapeNames.Except(part.Before.ShapeNames));
            part.ShapesRemoved.AddRange(part.Before.ShapeNames.Except(part.After.ShapeNames));
            // Offsets can only be compared on the same vertices: with a new topology every blendshape would differ.
            if (sameTopology && part.Before.ShapeSignatures != null && part.After.ShapeSignatures != null)
                part.ShapesChanged.AddRange(part.After.ShapeSignatures
                    .Where(pair => part.Before.ShapeSignatures.TryGetValue(pair.Key, out long old) && old != pair.Value)
                    .Select(pair => pair.Key));
            part.Change = part.MovedPoints > 0 ? PartChange.Reshaped
                : part.ShapesAdded.Count + part.ShapesRemoved.Count + part.ShapesChanged.Count > 0 ? PartChange.Shapes
                : PartChange.Same;
        }

        float max = parts.Select(p => p.MaxDistance).DefaultIfEmpty(0f).Max();
        float scale = Mathf.Max(max, threshold * 10f);
        foreach (var part in parts)
        {
            var (b, a) = weighted[part];
            if (b != null)
            {
                part.BeforeVertices = part.Before.Map(b);
                part.BeforeNormals = part.Before.Normals(b);
                part.BeforeHeat = Heat(part.Before, part.BeforeDistances, part.Change == PartChange.Removed ? RemovedPart : Reshaped, scale);
            }
            if (a != null)
            {
                part.AfterVertices = part.After.Map(a);
                part.AfterNormals = part.After.Normals(a);
                part.AfterHeat = Heat(part.After, part.AfterDistances, part.Change == PartChange.Added ? AddedPart : Reshaped, scale);
            }
        }

        // Most changed first, unchanged last.
        parts.Sort((x, y) => x.Changed != y.Changed ? (x.Changed ? -1 : 1) : y.MaxDistance.CompareTo(x.MaxDistance) != 0 ? y.MaxDistance.CompareTo(x.MaxDistance) : string.CompareOrdinal(x.Name, y.Name));
        Bounds = bounds;
        MaxDistance = max;
        Threshold = threshold;
        return parts;
    }

    // GeometryUtility.CalculateBounds only runs on the main thread.
    private static Bounds BoundsOf(IList<Vector3> points)
    {
        if (points == null || points.Count == 0) return new Bounds();
        Vector3 min = points[0], max = points[0];
        for (int i = 1; i < points.Count; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }
        return new Bounds((min + max) * 0.5f, max - min);
    }

    // Whole parts added or removed glow in their own colour; reshaped surfaces from nothing to the largest change.
    private static List<Vector4> Heat(MeshSource source, float[] distances, float kind, float scale)
    {
        int count = source.VertexPoint?.Length ?? source.Vertices.Length;
        var heat = new List<Vector4>(count);
        for (int i = 0; i < count; i++)
        {
            if (kind != Reshaped) { heat.Add(new Vector4(1f, kind, 0f, 0f)); continue; }
            int point = source.VertexPoint != null ? source.VertexPoint[i] : i;
            float distance = distances != null && point < distances.Length ? distances[point] : 0f;
            heat.Add(distance <= 0f ? Vector4.zero : new Vector4(Mathf.Pow(Mathf.Clamp01(distance / scale), 0.6f), Reshaped, 0f, 0f));
        }
        return heat;
    }

    private static Mesh CreateMesh(string name, MeshSource source, Vector3[] vertices, Vector3[] normals, List<Vector4> heat)
    {
        if (source == null || vertices == null) return null;
        var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = vertices.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.vertices = vertices;
        mesh.normals = normals;
        for (int channel = 0; channel < source.Uvs.Length; channel++)
            if (source.Uvs[channel] != null && source.Uvs[channel].Count == vertices.Length) mesh.SetUVs(channel, source.Uvs[channel]);
        mesh.SetUVs(7, heat);
        mesh.subMeshCount = Math.Max(1, source.SubMeshes.Length);
        for (int i = 0; i < source.SubMeshes.Length; i++) mesh.SetTriangles(source.SubMeshes[i], i, false);
        mesh.RecalculateBounds();
        if (source.Uvs[0] != null) mesh.RecalculateTangents();
        return mesh;
    }

    /// <summary>The avatar's other renderers, as they are now (main thread, built on first use).</summary>
    public void BuildContext()
    {
        if (contextBuilt || target == null) return;
        contextBuilt = true;
        Transform root = target.transform.root;
        var keys = new HashSet<string>(sources.Select(s => s.Key), StringComparer.Ordinal);
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || keys.Contains(KeyOf(root, renderer.transform))) continue;
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skinned.BakeMesh(baked, true);
                Context.Add(new ContextPart { Mesh = baked, Owned = true, Materials = skinned.sharedMaterials, Matrix = root.worldToLocalMatrix * Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, Vector3.one) });
            }
            else if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
            {
                Context.Add(new ContextPart { Mesh = filter.sharedMesh, Materials = renderer.sharedMaterials, Matrix = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix });
            }
        }
    }

    private void DestroyMeshes()
    {
        foreach (var part in Parts)
        {
            if (part.BeforeMesh != null) UnityEngine.Object.DestroyImmediate(part.BeforeMesh);
            if (part.AfterMesh != null) UnityEngine.Object.DestroyImmediate(part.AfterMesh);
            part.BeforeMesh = part.AfterMesh = null;
        }
    }

    public void Dispose()
    {
        DestroyMeshes();
        foreach (var part in Context) if (part.Owned && part.Mesh != null) UnityEngine.Object.DestroyImmediate(part.Mesh);
        Context.Clear();
    }
}
