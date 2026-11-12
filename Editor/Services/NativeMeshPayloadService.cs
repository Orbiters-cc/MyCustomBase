#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCBEditorUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Meshes;

public static partial class NativeMeshPayloadService
{
    public const string ExtraCustomizationKey = "advancedMeshReplacement";
    public const string TransformName = "XOR_BIN_TO_UNITY_ASSET";
    // Unprotected versions ship the same plain payload for every original model.
    public const string PlainTransformName = "PLAIN_BIN_TO_UNITY_ASSET";
    public const string PayloadFormat = "MCB_NATIVE_MESH_PAYLOAD";
    public const string PayloadCompressionMetadataKey = "payloadCompression";
    public const string PayloadCompressionGZip = "GZIP";
    public const string PayloadCompressionNone = "NONE";

    private const int PayloadVersion = 1;
    private const string BinaryPayloadMagic = "MCB_NATIVE_MESH_PAYLOAD";
    private const float SparseVectorEpsilon = 0.00001f;
    private const string GeneratedFolder = "Assets/MCB/generated/advancedMeshPayloads";
    private const string BuildTempFolder = "Assets/MCB/generated/advancedMeshBuildTemp";

    public sealed class GeneratedPayloadStorageInfo
    {
        public GeneratedPayloadStorageInfo(long totalBytes, int assetCount, int fileCount)
        {
            TotalBytes = Math.Max(0L, totalBytes);
            AssetCount = Math.Max(0, assetCount);
            FileCount = Math.Max(0, fileCount);
        }

        public long TotalBytes { get; }
        public int AssetCount { get; }
        public int FileCount { get; }
        public bool HasContent => FileCount > 0;
        public string FormattedSize => FormatByteSize(TotalBytes);
    }

    private static void LogApplyProfile(
        string label,
        System.Diagnostics.Stopwatch step,
        System.Diagnostics.Stopwatch total,
        string details = null)
    {
        MCBLogger.Log(
            string.IsNullOrWhiteSpace(details)
                ? $"[NativeMeshPayloadProfile] {label}: step={step.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms"
                : $"[NativeMeshPayloadProfile] {label}: step={step.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms {details}");
        step.Restart();
    }

    private static string FormatByteSize(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double mb = 1024d * 1024d;
        const double kb = 1024d;
        if (bytes >= gb)
        {
            return $"{bytes / gb:F2} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:F1} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:F1} KB";
        }

        return $"{bytes} B";
    }

    private static string ResolvePayloadCompression(ModelFileData patchFile)
    {
        string compression = patchFile?.compression;
        string metadataCompression = null;
        if (patchFile?.metadata != null &&
            patchFile.metadata.TryGetValue(PayloadCompressionMetadataKey, out object metadataValue))
        {
            metadataCompression = metadataValue?.ToString();
        }

        if (string.IsNullOrWhiteSpace(compression) || string.IsNullOrWhiteSpace(metadataCompression))
        {
            throw new InvalidDataException("Native mesh payload metadata must include both compression and payloadCompression.");
        }

        string normalizedCompression = NormalizePayloadCompression(compression);
        string normalizedMetadataCompression = NormalizePayloadCompression(metadataCompression);
        if (!string.Equals(normalizedCompression, normalizedMetadataCompression, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native mesh payload compression metadata is inconsistent.");
        }

        return normalizedCompression;
    }

    private static bool PayloadUsesGZip(string payloadCompression)
    {
        return string.Equals(NormalizePayloadCompression(payloadCompression), PayloadCompressionGZip, StringComparison.Ordinal);
    }

    private static string NormalizePayloadCompression(string payloadCompression)
    {
        if (string.Equals(payloadCompression, MCBCompression.Lz4, StringComparison.OrdinalIgnoreCase)) return MCBCompression.Lz4;
        if (string.Equals(payloadCompression, MCBCompression.Zstd, StringComparison.OrdinalIgnoreCase)) return MCBCompression.Zstd;
        if (string.Equals(payloadCompression, PayloadCompressionNone, StringComparison.OrdinalIgnoreCase))
        {
            return PayloadCompressionNone;
        }

        if (string.Equals(payloadCompression, PayloadCompressionGZip, StringComparison.OrdinalIgnoreCase))
        {
            return PayloadCompressionGZip;
        }

        throw new InvalidDataException($"Unsupported native mesh payload compression: {payloadCompression}");
    }

    public sealed class NativeMeshPayloadBuildResult
    {
        public List<NativeMeshPayloadBuildResult> parts;
        public string contentHash;
        public List<MCBPayloadVariant> variants;
        public string payloadHash;
        public string binHash;
        public string payloadCompression;
        public int rendererCount;
        public long payloadBytes;
        public long blendShapeVertexBytes;
        public long blendShapeNormalBytes;
        public long blendShapeTangentBytes;
        public long skippedBlendShapeNormalBytes;
        public long skippedBlendShapeTangentBytes;
    }

    public sealed class NativeMeshPayloadPreparationPreload
    {
        internal readonly Task<PreparedPayloadAssetData> task;
        internal readonly NativeMeshPayloadPreparationStatus status;
        private readonly string payloadAssetPath;
        private readonly string payloadHash;
        private readonly string payloadCompression;

        internal NativeMeshPayloadPreparationPreload(
            string payloadAssetPath,
            string payloadHash,
            string payloadCompression,
            NativeMeshPayloadPreparationStatus status,
            Task<PreparedPayloadAssetData> task)
        {
            this.payloadAssetPath = payloadAssetPath;
            this.payloadHash = payloadHash;
            this.payloadCompression = payloadCompression;
            this.status = status;
            this.task = task;
        }

        internal bool Matches(string assetPath, string hash, string compression)
        {
            return string.Equals(MCBUtils.ToUnityPath(payloadAssetPath), MCBUtils.ToUnityPath(assetPath), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(payloadHash, hash, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       NormalizePayloadCompression(payloadCompression),
                       NormalizePayloadCompression(compression),
                       StringComparison.Ordinal);
        }
    }

    public static NativeMeshPayloadBuildResult WriteEncryptedPayload(
        string sourceFbxPath,
        GameObject customFbx,
        IEnumerable<ModelFileSmrPathData> smrPaths,
        FileManagerService fileManagerService,
        string outputBinPath,
        bool bakeDynamicNormals = false,
        bool includeDynamicNormalsBody = true,
        bool includeDynamicNormalsFlexing = true,
        bool compressPayload = false,
        Transform sourcePoseRoot = null,
        bool createDeliveryVariants = false,
        IReadOnlyList<MeshBlendshapeSelection> dynamicNormalSelections = null,
        bool encrypt = true)
    {
        NativeMeshPayloadBuildResult result = null;
        MCBWork.Drain(WriteEncryptedPayloadCoroutine(value => result = value, sourceFbxPath, customFbx, smrPaths, fileManagerService, outputBinPath, bakeDynamicNormals, includeDynamicNormalsBody, includeDynamicNormalsFlexing, compressPayload, sourcePoseRoot, createDeliveryVariants, dynamicNormalSelections, encrypt));
        return result;
    }

    public static System.Collections.IEnumerator WriteEncryptedPayloadCoroutine(
        Action<NativeMeshPayloadBuildResult> completed,
        string sourceFbxPath,
        GameObject customFbx,
        IEnumerable<ModelFileSmrPathData> smrPaths,
        FileManagerService fileManagerService,
        string outputBinPath,
        bool bakeDynamicNormals = false,
        bool includeDynamicNormalsBody = true,
        bool includeDynamicNormalsFlexing = true,
        bool compressPayload = false,
        Transform sourcePoseRoot = null,
        bool createDeliveryVariants = false,
        IReadOnlyList<MeshBlendshapeSelection> dynamicNormalSelections = null,
        bool encrypt = true)
    {
        // A plain payload is one shared renderer-based delivery; it must not depend on the authoring original.
        if (!encrypt && !createDeliveryVariants)
        {
            throw new ArgumentException("Plain advanced mesh payloads are written as renderer delivery variants.", nameof(createDeliveryVariants));
        }

        if (string.IsNullOrWhiteSpace(sourceFbxPath))
        {
            throw new ArgumentNullException(nameof(sourceFbxPath));
        }

        if (customFbx == null)
        {
            throw new ArgumentNullException(nameof(customFbx));
        }

        if (fileManagerService == null)
        {
            throw new ArgumentNullException(nameof(fileManagerService));
        }

        if (string.IsNullOrWhiteSpace(outputBinPath))
        {
            throw new ArgumentNullException(nameof(outputBinPath));
        }

        string fullSourcePath = Path.GetFullPath(sourceFbxPath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Source FBX file for native mesh payload was not found.", fullSourcePath);
        }
        if (sourcePoseRoot == null) RequireOriginalModelImported(sourceFbxPath);

        GameObject payloadSource = customFbx;
        // The original model (the XOR key) is read on a worker while the renderers and normals are prepared.
        var keyRead = Task.Run(() => File.ReadAllBytes(fullSourcePath));
        var rendererSources = ResolvePayloadRendererSources(payloadSource, smrPaths);
        if (rendererSources.Count == 0)
        {
            throw new InvalidOperationException("The custom FBX did not provide any matching skinned mesh data for the native mesh payload.");
        }
        if (dynamicNormalSelections?.Count > 0)
        {
            foreach (var selection in dynamicNormalSelections)
            {
                var mapping = (smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>()).FirstOrDefault(m => m.avatarPath == selection.mesh);
                if (mapping == null && smrPaths != null && smrPaths.Any()) continue;
                string meshPath = mapping?.fbxMeshPath ?? selection.mesh;
                var renderer = AvatarPaths.Resolve(customFbx.transform, meshPath).GetComponent<SkinnedMeshRenderer>();
                var source = rendererSources.FirstOrDefault(r => r.renderer == renderer);
                if (source == null) throw new InvalidOperationException("Missing normal renderer: " + selection.mesh);
                foreach (var name in selection.names)
                    if (renderer.sharedMesh.GetBlendShapeIndex(name) < 0) throw new InvalidOperationException("Missing normal blendshape: " + selection.mesh + "/" + name);
                source.normalFrames = new Dictionary<(string shape, int frame), DynamicNormals.NormalFrame>();
                foreach (var name in selection.names)
                {
                    foreach (var frame in DynamicNormals.CaptureNormalFrames(renderer, new[] { name })) source.normalFrames[frame.Key] = frame.Value;
                    yield return null;
                }
            }
        }
        else
        if (bakeDynamicNormals)
        {
            var body = MeshFinder.FindMeshPrioritizingRoot(customFbx.transform, "Body");
            var bodySource = rendererSources.FirstOrDefault(source => source.renderer == body);
            if (bodySource != null)
            {
                var shapes = Enumerable.Range(0, body.sharedMesh.blendShapeCount)
                    .Select(body.sharedMesh.GetBlendShapeName)
                    .Where(name => (includeDynamicNormalsBody && name.IndexOf("muscle", StringComparison.OrdinalIgnoreCase) >= 0)
                        || (includeDynamicNormalsFlexing && name.IndexOf("flex", StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();
                if (shapes.Count > 0) bodySource.normalFrames = DynamicNormals.CaptureNormalFrames(body, shapes);
            }
        }

        string outputFullPath = Path.GetFullPath(outputBinPath);
        string outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        while (!keyRead.IsCompleted) yield return null;
        byte[] baseData = keyRead.GetAwaiter().GetResult();
        var metrics = new NativeMeshPayloadBuildMetrics();
        if (createDeliveryVariants)
        {
            yield return WriteDeliveryVariantsCoroutine(sourceFbxPath, payloadSource, rendererSources, encrypt ? baseData : null, outputFullPath, metrics, sourcePoseRoot, completed);
            yield break;
        }

        using (var payloadSha = MCBHashing.CreateSha256())
        using (var binSha = MCBHashing.CreateSha256())
        using (var output = File.Create(outputFullPath))
        using (var binHashStream = new HashingWriteStream(output, binSha, leaveOpen: false))
        using (var xorStream = new XorWriteStream(binHashStream, baseData, leaveOpen: false))
        using (var payloadHashStream = new HashingWriteStream(xorStream, payloadSha, leaveOpen: false))
        {
            using (var buffered = new BufferedStream(payloadHashStream, 256 * 1024))
            {
                if (compressPayload)
                {
                    using (var gzip = new GZipStream(buffered, System.IO.Compression.CompressionLevel.Optimal, true))
                        yield return WriteBinaryPayloadContentsCoroutine(sourceFbxPath, payloadSource, rendererSources, gzip, metrics, sourcePoseRoot);
                }
                else yield return WriteBinaryPayloadContentsCoroutine(sourceFbxPath, payloadSource, rendererSources, buffered, metrics, sourcePoseRoot);
                buffered.Flush();
                payloadHashStream.CompleteHash();
                xorStream.Flush();
                binHashStream.CompleteHash();
            }

            var result = new NativeMeshPayloadBuildResult
            {
                payloadHash = MCBHashing.Hex(payloadSha.Hash),
                binHash = MCBHashing.Hex(binSha.Hash),
                payloadCompression = compressPayload ? PayloadCompressionGZip : PayloadCompressionNone,
                rendererCount = rendererSources.Count,
                payloadBytes = payloadHashStream.BytesWritten,
                blendShapeVertexBytes = metrics.blendShapeVertexBytes,
                blendShapeNormalBytes = metrics.blendShapeNormalBytes,
                blendShapeTangentBytes = metrics.blendShapeTangentBytes,
                skippedBlendShapeNormalBytes = metrics.skippedBlendShapeNormalBytes,
                skippedBlendShapeTangentBytes = metrics.skippedBlendShapeTangentBytes
            };

            MCBLogger.Log(
                $"[NativeMeshPayload] Built native mesh payload: renderers={result.rendererCount}, compression={result.payloadCompression}, payloadBytes={FormatBytes(result.payloadBytes)}, " +
                $"blendshapeVertices={FormatBytes(result.blendShapeVertexBytes)}, blendshapeNormals={FormatBytes(result.blendShapeNormalBytes)}, blendshapeTangents={FormatBytes(result.blendShapeTangentBytes)}, " +
                $"skippedNormals={FormatBytes(result.skippedBlendShapeNormalBytes)}, skippedTangents={FormatBytes(result.skippedBlendShapeTangentBytes)}");
            completed(result);
        }
    }

    // XOR payloads need the user's original model as their key; plain payloads must not receive one.
    private static void RequireOriginalKey(ModelFileData patchFile, string originalFbxPath)
    {
        if (IsPlainPayloadTransform(patchFile?.transform))
        {
            if (!string.IsNullOrEmpty(originalFbxPath)) throw new InvalidOperationException("A plain advanced mesh payload has no original FBX key.");
            return;
        }
        if (string.IsNullOrWhiteSpace(originalFbxPath) || !File.Exists(originalFbxPath))
            throw new FileNotFoundException("Original FBX key file not found for native mesh payload.", originalFbxPath);
    }

    /// <summary>
    /// Shows a Blender export on the avatar as a version's payload would: its meshes are read straight from the FBX
    /// (<see cref="FbxModel"/>, no import), the dynamic-normal blendshapes get their normals baked as a version build bakes
    /// them (with the legs apart given by <paramref name="boneTranslations"/>), the other blendshapes carry positions only,
    /// and the meshes go into a payload asset as they are, with no byte payload written and read back.
    /// </summary>
    /// <param name="dynamicNormals">The blendshapes whose normals are recalculated, by avatar renderer path.</param>
    public static NativeMeshPayloadAsset ApplyBlenderExportPreview(
        Transform avatarRoot, string exportFbxPath, string sourceFbxPath, IEnumerable<ModelFileSmrPathData> smrPaths,
        string outputFolder, IEnumerable<MeshBlendshapeSelection> dynamicNormals,
        Func<SkinnedMeshRenderer, Dictionary<string, Vector3>> boneTranslations)
    {
        if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
        if (string.IsNullOrWhiteSpace(outputFolder)) throw new ArgumentNullException(nameof(outputFolder));
        var profile = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        var mappings = (smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>()).Where(m => m != null).ToList();
        byte[] bytes = File.ReadAllBytes(Path.GetFullPath(exportFbxPath));
        var document = FbxDocument.Read(bytes);
        string name = Path.GetFileNameWithoutExtension(exportFbxPath);
        LogApplyProfile("Read the Blender export", step, profile, $"bytes={bytes.Length}");

        // The dynamic-normal blendshapes alone first, to bake their normals: every blendshape frame is then added once.
        var selected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var selection in dynamicNormals ?? Enumerable.Empty<MeshBlendshapeSelection>())
        {
            var mapping = mappings.FirstOrDefault(m => m.avatarPath == selection.mesh);
            string mesh = LastPathSegment(mapping?.fbxMeshPath) ?? mapping?.meshName;
            if (mesh == null || selection.names == null) continue;
            if (!selected.TryGetValue(mesh, out var names)) selected[mesh] = names = new HashSet<string>(StringComparer.Ordinal);
            names.UnionWith(selection.names);
        }
        var frames = new Dictionary<(string mesh, string shape, int frame), FbxModel.FrameNormals>();
        // The legs apart (or not) while normals are baked change them: part of the preview's identity.
        var poseKey = new StringBuilder();
        if (selected.Count > 0)
        {
            var light = FbxModel.Load(document, name, new FbxModel.Options { IncludeShape = (mesh, shape) => selected.TryGetValue(mesh, out var names) && names.Contains(shape) });
            try
            {
                foreach (var renderer in light.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!selected.TryGetValue(renderer.name, out var names) || renderer.sharedMesh.blendShapeCount == 0) continue;
                    var shapes = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.sharedMesh.GetBlendShapeName).Where(names.Contains).ToList();
                    var translations = boneTranslations?.Invoke(renderer);
                    foreach (var bone in (translations ?? new Dictionary<string, Vector3>()).OrderBy(b => b.Key, StringComparer.Ordinal))
                        poseKey.Append(renderer.name).Append('|').Append(bone.Key).Append('|').Append(bone.Value.ToString("F6")).Append(';');
                    foreach (var pair in DynamicNormals.CaptureNormalFrames(renderer, shapes, translations))
                        frames[(renderer.name, pair.Key.shape, pair.Key.frame)] = new FbxModel.FrameNormals { Normals = pair.Value.normals, Tangents = pair.Value.tangents };
                }
            }
            finally
            {
                FbxModel.Release(light);
            }
            LogApplyProfile("Baked dynamic normals", step, profile, $"frames={frames.Count}");
        }

        var model = FbxModel.Load(document, name, new FbxModel.Options
        {
            Normals = (mesh, shape, frame) => frames.TryGetValue((mesh, shape, frame), out var baked) ? baked : null,
        });
        try
        {
            LogApplyProfile("Built the meshes", step, profile);
            var sources = ResolvePayloadRendererSources(model, mappings);
            if (sources.Count == 0) throw new InvalidOperationException("The preview contains no matching skinned meshes.");
            // Content-addressed assets keep earlier previews valid for Undo and scene saves.
            var key = Hash128.Compute(bytes);
            foreach (var mapping in mappings) key.Append(mapping.avatarPath + "|" + mapping.fbxMeshPath + "|" + mapping.meshName);
            foreach (var frame in frames.Keys) key.Append(frame.mesh + "|" + frame.shape);
            if (poseKey.Length > 0) key.Append(poseKey.ToString());
            string hash = key.ToString();
            string path = MCBUtils.CombineUnityPath(outputFolder, hash + ".asset");
            var payload = LoadPayloadAsset(path);
            if (payload == null || payload.payloadHash != hash)
            {
                EnsureAssetFolder(outputFolder);
                DeleteAssetFile(path);
                payload = BuildPayloadAsset(sourceFbxPath, model, sources, hash);
                AssetDatabase.CreateAsset(payload, path);
                foreach (var record in payload.renderers) AssetDatabase.AddObjectToAsset(record.mesh, payload);
                AssetDatabase.SetMainObject(payload, path);
                EditorUtility.SetDirty(payload);
                AssetDatabase.SaveAssetIfDirty(payload);
                LogApplyProfile("Saved the preview asset", step, profile, path);
            }
            ApplyPayloadToAvatar(avatarRoot, payload);
            ApplyPayloadAuthoringPose(avatarRoot, payload);
            LogApplyProfile("Applied the preview", step, profile);
            return payload;
        }
        finally
        {
            FbxModel.Release(model);
        }
    }

    // A payload asset of a model's renderers, their meshes as they are (renamed as a payload names them).
    private static NativeMeshPayloadAsset BuildPayloadAsset(string sourceFbxPath, GameObject customFbx, List<PayloadRendererSource> sources, string payloadHash)
    {
        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        payload.payloadVersion = PayloadVersion;
        payload.sourceFbxPath = StripOriginalSuffix(MCBUtils.ToUnityPath(sourceFbxPath)) ?? "";
        payload.sourceFbxHash = ContentHash.Proof(sourceFbxPath) ?? "";
        payload.payloadHash = payloadHash;
        var usedMeshNames = new HashSet<string>(StringComparer.Ordinal);
        var usedAssetNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var record = RendererRecord(customFbx.transform, source);
            var mesh = source.renderer.sharedMesh;
            string meshName = BuildUniqueSubAssetName(string.IsNullOrWhiteSpace(source.entry.meshName) ? mesh.name : source.entry.meshName, usedMeshNames);
            mesh.name = BuildUniqueSubAssetName($"{meshName}_{ShortHash(payloadHash)}", usedAssetNames);
            record.mesh = mesh;
            payload.renderers.Add(record);
        }
        payload.authoringPoseBones = BuildAuthoringPoseDeltas(sourceFbxPath, customFbx.transform, null);
        payload.bones = PayloadBones(customFbx.transform, payload.renderers, payload.authoringPoseBones, true);
        payload.name = payloadHash;
        return payload;
    }

    private static string LastPathSegment(string path) => string.IsNullOrEmpty(path) ? null : path.Substring(path.LastIndexOf('/') + 1);

    public static GameObject ImportExternalFbxForPayload(string externalFbxPath, out string tempUnityPath)
    {
        tempUnityPath = null;
        if (string.IsNullOrWhiteSpace(externalFbxPath))
        {
            throw new ArgumentNullException(nameof(externalFbxPath));
        }

        string sourcePath = Path.GetFullPath(externalFbxPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("External Blender FBX file was not found.", sourcePath);
        }

        string extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".fbx";
        }

        string tempFolder = MCBUtils.CombineUnityPath(BuildTempFolder, "externalFbxImports");
        EnsureAssetFolder(tempFolder);
        string tempName = $"{SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath))}_{Guid.NewGuid():N}{extension}";
        tempUnityPath = MCBUtils.CombineUnityPath(tempFolder, tempName);
        File.Copy(sourcePath, Path.GetFullPath(tempUnityPath), true);
        // Its import settings are written before its first import: readable (turning Read/Write on afterwards imported a
        // body with hundreds of blendshapes a second time, about 30 s), without what MCB never reads from it.
        File.WriteAllText(Path.GetFullPath(tempUnityPath) + ".meta",
            "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\nModelImporter:\n  serializedVersion: 22200\n" +
            "  animations:\n    isReadable: 1\n  meshes:\n    importCameras: 0\n    importLights: 0\n  importAnimation: 0\n");
        AssetDatabase.ImportAsset(tempUnityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        var imported = AssetDatabase.LoadAssetAtPath<GameObject>(tempUnityPath);
        if (imported == null)
        {
            DeleteTemporaryImportedFbx(tempUnityPath);
            throw new InvalidOperationException($"External Blender FBX could not be imported for native mesh payload creation: {tempUnityPath}");
        }

        return imported;
    }

    public static void DeleteTemporaryImportedFbx(string tempUnityPath)
    {
        if (string.IsNullOrWhiteSpace(tempUnityPath))
        {
            return;
        }

        string normalized = MCBUtils.ToUnityPath(tempUnityPath);
        if (AssetDatabase.LoadMainAssetAtPath(normalized) != null)
        {
            AssetDatabase.DeleteAsset(normalized);
            return;
        }

        string fullPath = Path.GetFullPath(normalized);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        if (File.Exists(fullPath + ".meta")) File.Delete(fullPath + ".meta");
    }

    public static NativeMeshPayloadAsset ApplyEncryptedPayload(
        Transform avatarRoot,
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService,
        string basePoseFbxPath = null)
    {
        if (avatarRoot == null)
        {
            throw new ArgumentNullException(nameof(avatarRoot));
        }

        if (version == null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (patchFile == null)
        {
            throw new ArgumentNullException(nameof(patchFile));
        }

        if (fileManagerService == null)
        {
            throw new ArgumentNullException(nameof(fileManagerService));
        }

        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
        {
            throw new FileNotFoundException("Apply failed: native mesh .bin file not found.", binPath);
        }

        RequireOriginalKey(patchFile, originalFbxPath);

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        MCBLogger.Log(
            $"[NativeMeshPayloadProfile] START apply payload version={version.version}");

        NativeMeshPayloadAsset payload = MaterializeEncryptedPayloadAsset(version, patchFile, binPath, originalFbxPath, fileManagerService);
        LogApplyProfile("Loaded/materialized native mesh payload asset", step, total, $"renderers={payload.renderers.Count} bones={payload.bones.Count}");
        ApplyPayloadToAvatar(avatarRoot, payload);
        LogApplyProfile("Applied native mesh payload to avatar", step, total);
        ApplyPayloadAuthoringPose(avatarRoot, payload, basePoseFbxPath);
        LogApplyProfile("Applied native mesh authoring pose to avatar", step, total, $"bones={payload.authoringPoseBones.Count}");
        MCBLogger.Log($"[NativeMeshPayloadProfile] DONE apply payload total={total.Elapsed.TotalMilliseconds:F1} ms");
        return payload;
    }

    public static IEnumerator ApplyEncryptedPayloadCoroutine(
        Transform avatarRoot,
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService,
        Action<float, string> reportProgress,
        NativeMeshPayloadPreparationPreload preparationPreload = null)
    {
        if (avatarRoot == null)
        {
            throw new ArgumentNullException(nameof(avatarRoot));
        }

        if (version == null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (patchFile == null)
        {
            throw new ArgumentNullException(nameof(patchFile));
        }

        if (fileManagerService == null)
        {
            throw new ArgumentNullException(nameof(fileManagerService));
        }

        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
        {
            throw new FileNotFoundException("Apply failed: native mesh .bin file not found.", binPath);
        }

        RequireOriginalKey(patchFile, originalFbxPath);

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        MCBLogger.Log($"[NativeMeshPayloadProfile] START async apply payload version={version.version}");

        NativeMeshPayloadAsset payload = null;
        var materialize = MaterializeEncryptedPayloadAssetCoroutine(
            version,
            patchFile,
            binPath,
            originalFbxPath,
            fileManagerService,
            (progress, label) => reportProgress?.Invoke(Mathf.Lerp(0.02f, 0.72f, progress), label),
            result => payload = result,
            preparationPreload);
        while (materialize.MoveNext())
        {
            yield return materialize.Current;
        }

        LogApplyProfile("Loaded/materialized native mesh payload asset", step, total, $"renderers={payload.renderers.Count} bones={payload.bones.Count}");
        reportProgress?.Invoke(0.76f, "Assigning advanced mesh to avatar...");
        ApplyPayloadToAvatar(avatarRoot, payload);
        LogApplyProfile("Applied native mesh payload to avatar", step, total);
        yield return null;

        reportProgress?.Invoke(0.90f, "Applying advanced mesh authoring pose...");
        ApplyPayloadAuthoringPose(avatarRoot, payload);
        LogApplyProfile("Applied native mesh authoring pose to avatar", step, total, $"bones={payload.authoringPoseBones.Count}");
        reportProgress?.Invoke(1f, "Advanced mesh applied...");
        MCBLogger.Log($"[NativeMeshPayloadProfile] DONE async apply payload total={total.Elapsed.TotalMilliseconds:F1} ms");
    }

    public static NativeMeshPayloadAsset MaterializeEncryptedPayloadAsset(
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService)
    {
        if (fileManagerService == null)
        {
            throw new ArgumentNullException(nameof(fileManagerService));
        }

        var request = PayloadRequest.Resolve(version, patchFile, binPath, originalFbxPath);
        string payloadHash = request.payloadHash;
        string payloadCompression = request.payloadCompression;
        string payloadAssetPath = request.assetPath;
        var existing = request.FindCached();
        if (existing != null)
        {
            MCBLogger.Log($"[NativeMeshPayloadProfile] Using cached native mesh payload asset: {payloadAssetPath}");
            return existing;
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        long binBytes = new FileInfo(binPath).Length;
        MCBLogger.Log(
            $"[NativeMeshPayloadProfile] Materializing payload asset cache version={version.version} compression={payloadCompression} bin={FormatByteSize(binBytes)} plain={IsPlainPayloadTransform(patchFile.transform)}");

        // The same preparation and commit as the asynchronous apply, run on the calling thread.
        var prepared = PrepareNativeMeshPayloadData(binPath, originalFbxPath, null, null,
            Path.GetFileNameWithoutExtension(MCBUtils.ToUnityPath(payloadAssetPath)), payloadHash, payloadCompression,
            new NativeMeshPayloadPreparationStatus());
        LogApplyProfile("Prepared native mesh payload", step, total, $"renderers={prepared.renderers.Count}");
        NativeMeshPayloadAsset payload = null;
        MCBWork.Drain(CommitPreparedPayloadAssetCoroutine(payloadAssetPath, prepared, payloadHash, payloadCompression, null,
            result => payload = result, yieldToEditor: false));
        LogApplyProfile("Wrote native mesh payload asset cache", step, total, $"path={payloadAssetPath} renderers={payload.renderers.Count} bones={payload.bones.Count}");
        return payload;
    }

    /// <summary>
    /// Reads an advanced mesh patch for a preview, off the main thread: decrypted and parsed in memory, so nothing is written
    /// to the project. Throws when the original model is not the patch's key.
    /// </summary>
    internal static PreparedPayloadAssetData ReadPayloadForPreview(ModelFileData patchFile, string binPath, string originalFbxPath)
    {
        return PrepareNativeMeshPayloadData(binPath, originalFbxPath, null, null, Path.GetFileNameWithoutExtension(binPath),
            GetPayloadIdentity(patchFile), ResolvePayloadCompression(patchFile), new NativeMeshPayloadPreparationStatus());
    }

    /// <summary>The payload an earlier apply cached for this patch, or null: reading it is faster than decoding (main thread).</summary>
    internal static NativeMeshPayloadAsset FindCachedPayload(CustomBaseVersion version, ModelFileData patchFile)
    {
        return PayloadRequest.Of(version, patchFile).FindCached();
    }

    public static NativeMeshPayloadPreparationPreload StartEncryptedPayloadPreparation(
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        Task<byte[]> binDataTask = null,
        Task<byte[]> originalFbxDataTask = null)
    {
        var request = PayloadRequest.Resolve(version, patchFile, binPath, originalFbxPath,
            binInMemory: binDataTask != null, keyInMemory: originalFbxDataTask != null);
        string payloadHash = request.payloadHash;
        string payloadCompression = request.payloadCompression;
        string payloadAssetPath = request.assetPath;
        if (request.FindCached() != null)
        {
            return null;
        }

        var status = new NativeMeshPayloadPreparationStatus();
        string assetName = Path.GetFileNameWithoutExtension(MCBUtils.ToUnityPath(payloadAssetPath));
        var task = Task.Run(() =>
        {
            byte[] binData = binDataTask != null ? binDataTask.GetAwaiter().GetResult() : null;
            byte[] originalFbxData = originalFbxDataTask != null ? originalFbxDataTask.GetAwaiter().GetResult() : null;
            return PrepareNativeMeshPayloadData(
                binPath,
                originalFbxPath,
                binData,
                originalFbxData,
                assetName,
                payloadHash,
                payloadCompression,
                status);
        });

        MCBLogger.Log($"[NativeMeshPayloadProfile] Started advanced mesh payload preparation preload: {payloadAssetPath}");
        return new NativeMeshPayloadPreparationPreload(payloadAssetPath, payloadHash, payloadCompression, status, task);
    }

    public static IEnumerator MaterializeEncryptedPayloadAssetCoroutine(
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService,
        Action<float, string> reportProgress,
        Action<NativeMeshPayloadAsset> completed,
        NativeMeshPayloadPreparationPreload preparationPreload = null)
    {
        if (fileManagerService == null)
        {
            throw new ArgumentNullException(nameof(fileManagerService));
        }

        var request = PayloadRequest.Resolve(version, patchFile, binPath, originalFbxPath);
        string payloadHash = request.payloadHash;
        string payloadCompression = request.payloadCompression;
        string payloadAssetPath = request.assetPath;
        var existing = request.FindCached();
        if (existing != null)
        {
            MCBLogger.Log($"[NativeMeshPayloadProfile] Using cached native mesh payload asset: {payloadAssetPath}");
            reportProgress?.Invoke(1f, "Loaded cached advanced mesh...");
            completed?.Invoke(existing);
            yield break;
        }

        long binBytes = new FileInfo(binPath).Length;
        MCBLogger.Log(
            $"[NativeMeshPayloadProfile] Async materializing payload asset cache version={version.version} compression={payloadCompression} bin={FormatByteSize(binBytes)} plain={IsPlainPayloadTransform(patchFile.transform)}");

        string assetName = Path.GetFileNameWithoutExtension(MCBUtils.ToUnityPath(payloadAssetPath));
        NativeMeshPayloadPreparationStatus status;
        Task<PreparedPayloadAssetData> prepareTask;
        if (preparationPreload != null && preparationPreload.Matches(payloadAssetPath, payloadHash, payloadCompression))
        {
            status = preparationPreload.status;
            prepareTask = preparationPreload.task;
            MCBLogger.Log($"[NativeMeshPayloadProfile] Reusing advanced mesh payload preparation preload: {payloadAssetPath}");
        }
        else
        {
            status = new NativeMeshPayloadPreparationStatus();
            prepareTask = Task.Run(() => PrepareNativeMeshPayloadData(
                binPath,
                originalFbxPath,
                null,
                null,
                assetName,
                payloadHash,
                payloadCompression,
                status));
        }

        while (!prepareTask.IsCompleted)
        {
            status.Snapshot(out float progress, out string label);
            reportProgress?.Invoke(Mathf.Lerp(0.02f, 0.68f, progress), label);
            yield return null;
        }

        if (prepareTask.IsFaulted)
        {
            throw prepareTask.Exception?.GetBaseException() ?? new InvalidOperationException("Advanced mesh payload preparation failed.");
        }

        var prepared = prepareTask.Result;
        NativeMeshPayloadAsset payload = null;
        var commit = CommitPreparedPayloadAssetCoroutine(
            payloadAssetPath,
            prepared,
            payloadHash,
            payloadCompression,
            (progress, label) => reportProgress?.Invoke(Mathf.Lerp(0.68f, 1f, progress), label),
            result => payload = result);
        while (commit.MoveNext())
        {
            yield return commit.Current;
        }

        completed?.Invoke(payload);
    }

    /// <param name="missingRenderers">Receives the replaced renderers the avatar no longer has (deleted or renamed by the
    /// user): they are skipped, the other renderers are still restored.</param>
    public static int RestoreOriginalMeshesFromFbx(
        Transform avatarRoot,
        CustomBaseVersion version,
        IEnumerable<string> sourceFbxPaths,
        MyCustomBase target = null,
        CustomBaseVersion preserveVersion = null,
        List<string> missingRenderers = null)
    {
        if (avatarRoot == null)
        {
            return 0;
        }

        int restoredTotal = 0;
        foreach (string rawPath in sourceFbxPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string sourcePath = MCBUtils.ToUnityPath(rawPath);
            // A plain version was authored on another original: restore by the avatar's own model, keeping
            // renderers the user deleted from their avatar out of the restoration.
            bool plain = VersionProtection.IsPlain(version);
            var smrPaths = plain ? new List<ModelFileSmrPathData>() : SmrPathService.ResolveSmrPathsForSource(version, sourcePath, target);
            var missing = new List<string>();
            int restored = SmrPathService.RestoreTargetStateFromFbx(avatarRoot, sourcePath, smrPaths,
                renderer => preserveVersion != null && IsSharedMeshForVersion(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)), preserveVersion),
                skipMissingRenderers: true, missingRenderers: missing);
            restoredTotal += restored;
            missingRenderers?.AddRange(missing);
            if (restored == 0 && missing.Count == 0)
            {
                throw new InvalidOperationException(
                    $"None of your avatar's body parts could be matched with its original base model '{sourcePath}'. Nothing was changed.");
            }
        }

        return restoredTotal;
    }

    public static int RestoreOriginalAuthoringPoseFromFbx(Transform avatarRoot, IEnumerable<string> sourceFbxPaths)
    {
        if (avatarRoot == null)
        {
            return 0;
        }

        int restoredTotal = 0;
        foreach (string rawPath in sourceFbxPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            restoredTotal += SmrPathService.RestoreTargetTransformHierarchyFromFbx(avatarRoot, MCBUtils.ToUnityPath(rawPath));
        }

        if (restoredTotal > 0)
        {
            RefreshAvatarSkinnedRenderers(avatarRoot);
        }
        return restoredTotal;
    }

    public static GeneratedPayloadStorageInfo GetGeneratedPayloadStorageInfo()
    {
        return GetGeneratedPayloadStorageInfo(GeneratedFolder);
    }

    /// <summary>
    /// Moves the version's generated payloads that nothing uses to the Recycle Bin at once (health checks); meshes an
    /// avatar still shows are kept. Editor actions use <see cref="QueueUnusedPayloadCleanup(CustomBaseVersion)"/>.
    /// </summary>
    public static GeneratedPayloadStorageInfo DeleteGeneratedPayloadsForVersion(CustomBaseVersion version)
    {
        return DeleteUnreferencedPayloadsOfVersion(version);
    }

    /// <summary>Names of the original bones a payload's renderers bind to: an avatar needs them all to use a plain payload.
    /// Bones only the custom model has are created on apply and are not required.</summary>
    public static List<string> CollectSkeletonBones(GameObject customFbx, string sourceFbxPath, IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        RequireOriginalModelImported(sourceFbxPath);
        var source = ResolveSourcePoseRoot(sourceFbxPath);
        if (source == null) throw new InvalidDataException("The original model is needed to list the skeleton of a plain payload: " + sourceFbxPath);
        var originalNames = new HashSet<string>(source.GetComponentsInChildren<Transform>(true).Where(t => t != source).Select(t => t.name), StringComparer.Ordinal);
        return ResolvePayloadRendererSources(customFbx, smrPaths)
            .SelectMany(s => (s.renderer.bones ?? Array.Empty<Transform>()).Append(s.renderer.rootBone))
            .Where(bone => bone != null && originalNames.Contains(bone.name))
            .Select(bone => bone.name).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Each payload renderer's material slot names as its model declares them.</summary>
    public static List<RendererSlotNames> CollectRendererSlots(GameObject customFbx, IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        return ResolvePayloadRendererSources(customFbx, smrPaths).Select(s => new RendererSlotNames {
            path = s.entry.avatarPath, slots = MaterialSlotNames.Of(s.renderer).ToList() }).ToList();
    }

    private static List<PayloadRendererSource> ResolvePayloadRendererSources(
        GameObject customFbx,
        IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        var result = new List<PayloadRendererSource>();
        if (customFbx == null)
        {
            return result;
        }

        Transform customRoot = customFbx.transform;
        var entries = (smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>())
            .Where(entry => entry != null)
            .ToList();

        if (entries.Count == 0)
        {
            entries = customFbx.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer != null && renderer.sharedMesh != null)
                .Select(renderer => new ModelFileSmrPathData
                {
                    avatarPath = GetRelativeTransformPath(customRoot, renderer.transform),
                    fbxMeshPath = GetRelativeTransformPath(customRoot, renderer.transform),
                    meshName = renderer.sharedMesh.name,
                    rendererName = renderer.transform.name
                })
                .ToList();
        }

        var seenRenderers = new HashSet<int>();
        foreach (var entry in entries)
        {
            var sourceRenderer = ResolveCustomRenderer(customRoot, entry);
            if (sourceRenderer == null || sourceRenderer.sharedMesh == null)
            {
                throw new InvalidDataException($"Custom model is missing mesh '{entry.fbxMeshPath}' for avatar renderer '{entry.avatarPath}'. No partial advanced mesh payload was built.");
            }

            if (!seenRenderers.Add(sourceRenderer.GetInstanceID()))
            {
                continue;
            }

            result.Add(new PayloadRendererSource
            {
                entry = entry,
                renderer = sourceRenderer
            });
        }

        return result;
    }

    // What a payload says of one of the model's renderers, its mesh aside.
    private static NativeMeshPayloadRenderer RendererRecord(Transform customRoot, PayloadRendererSource source)
    {
        var renderer = source.renderer;
        var mesh = renderer.sharedMesh;
        var bonePaths = (renderer.bones ?? Array.Empty<Transform>())
            .Select(bone => bone != null ? GetRelativeTransformPath(customRoot, bone) : "")
            .ToList();
        if (mesh.bindposes != null && mesh.bindposes.Length > 0 && mesh.bindposes.Length != bonePaths.Count)
        {
            MCBLogger.LogWarning($"[NativeMeshPayload] Renderer '{renderer.name}' has {mesh.bindposes.Length} bindposes but {bonePaths.Count} bones. The applied mesh may deform incorrectly.");
        }
        return new NativeMeshPayloadRenderer
        {
            avatarPath = source.entry.avatarPath ?? "",
            fbxMeshPath = source.entry.fbxMeshPath ?? "",
            meshName = source.entry.meshName ?? "",
            rendererName = source.entry.rendererName ?? "",
            localPosition = renderer.transform.localPosition,
            localRotation = renderer.transform.localRotation,
            localScale = renderer.transform.localScale,
            rootBonePath = renderer.rootBone != null ? GetRelativeTransformPath(customRoot, renderer.rootBone) : "",
            bonePaths = bonePaths
        };
    }

    // The model's bones a payload carries: those its renderers use, and without a source model every posed one (unweighted
    // original bones, contacts and physbone roots, need absolute poses too).
    private static List<NativeMeshPayloadBone> PayloadBones(Transform customRoot, IEnumerable<NativeMeshPayloadRenderer> renderers,
        IEnumerable<NativeMeshPayloadAuthoringPoseBone> authoringPoseBones, bool bindSource)
    {
        var payloadBonePaths = CollectPayloadBonePaths(renderers);
        if (!bindSource) foreach (var poseBone in authoringPoseBones) payloadBonePaths.Add(poseBone.path);
        return customRoot.GetComponentsInChildren<Transform>(true)
            .Where(transform => transform != null && transform != customRoot)
            .Where(transform => payloadBonePaths.Count > 0 && payloadBonePaths.Contains(GetRelativeTransformPath(customRoot, transform)))
            .Select(transform => new NativeMeshPayloadBone
            {
                path = GetRelativeTransformPath(customRoot, transform),
                localPosition = transform.localPosition,
                localRotation = transform.localRotation,
                localScale = transform.localScale
            })
            .ToList();
    }

    private static List<NativeMeshPayloadAuthoringPoseBone> BuildAuthoringPoseDeltas(
        string sourceFbxPath,
        Transform customRoot,
        Transform sourcePoseRoot)
    {
        if (customRoot == null)
        {
            throw new ArgumentNullException(nameof(customRoot));
        }

        Transform sourceRoot = sourcePoseRoot ?? ResolveSourcePoseRoot(sourceFbxPath);
        Transform authoringRoot = customRoot;
        if (sourceRoot == null)
        {
            throw new InvalidDataException($"Could not resolve source FBX pose root for native mesh payload: {sourceFbxPath}");
        }

        if (authoringRoot == null)
        {
            throw new InvalidDataException("Could not resolve authoring pose root for native mesh payload.");
        }

        var result = new List<NativeMeshPayloadAuthoringPoseBone>();
        foreach (var customTransform in customRoot.GetComponentsInChildren<Transform>(true))
        {
            if (customTransform == null || customTransform == customRoot)
            {
                continue;
            }

            string path = GetRelativeTransformPath(customRoot, customTransform);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var sourceTransform = FindTransformByRelativePath(sourceRoot, path);
            var authoringTransform = FindTransformByRelativePath(authoringRoot, path);
            if (sourceTransform == null || authoringTransform == null)
            {
                continue;
            }

            result.Add(new NativeMeshPayloadAuthoringPoseBone
            {
                path = path,
                localPositionOffset = authoringTransform.localPosition - sourceTransform.localPosition,
                localRotationDelta = Quaternion.Inverse(sourceTransform.localRotation) * authoringTransform.localRotation,
                localScaleMultiplier = DivideScale(authoringTransform.localScale, sourceTransform.localScale)
            });
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("Native mesh payload authoring pose capture did not find any matching bones.");
        }

        return result;
    }

    private static bool IsModelWithHash(string modelPath, string hash)
    {
        if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(hash)) return false;
        string fullPath = Path.GetFullPath(MCBUtils.ToUnityPath(modelPath));
        return ContentHash.Verify(fullPath, hash);
    }

    private static Transform ResolveSourcePoseRoot(string sourceFbxPath)
    {
        string sourceUnityPath = StripOriginalSuffix(MCBUtils.ToUnityPath(sourceFbxPath));
        if (string.IsNullOrWhiteSpace(sourceUnityPath))
        {
            return null;
        }

        if (!IsAssetDatabasePath(sourceUnityPath))
        {
            return null;
        }

        var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourceUnityPath);
        return sourceAsset != null ? sourceAsset.transform : null;
    }

    /// <summary>
    /// Pose deltas and skeleton lists are read from the model imported at the source path. While a version's model is
    /// imported there instead of the original (its .originalbase), they would describe that version: refuse the build.
    /// </summary>
    internal static void RequireOriginalModelImported(string sourceFbxPath)
    {
        string modelPath = StripOriginalSuffix(MCBUtils.ToUnityPath(sourceFbxPath));
        if (!IsAssetDatabasePath(modelPath)) return;
        string originalPath = Path.GetFullPath(FileManagerService.GetOriginalBasePath(modelPath));
        string importedPath = Path.GetFullPath(modelPath);
        if (!File.Exists(originalPath) || !File.Exists(importedPath)) return;
        if (ContentHash.SameContent(importedPath, originalPath)) return;
        throw new InvalidOperationException(
            $"'{Path.GetFileName(modelPath)}' is currently replaced by a version's model, not its original. Reset the avatar to its original base, then build again.");
    }

    private static bool IsAssetDatabasePath(string unityPath)
    {
        return !string.IsNullOrWhiteSpace(unityPath) &&
               (unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                unityPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));
    }

    private static Vector3 DivideScale(Vector3 numerator, Vector3 denominator)
    {
        return new Vector3(
            DivideScaleComponent(numerator.x, denominator.x),
            DivideScaleComponent(numerator.y, denominator.y),
            DivideScaleComponent(numerator.z, denominator.z));
    }

    private static float DivideScaleComponent(float numerator, float denominator)
    {
        return Mathf.Abs(denominator) < 0.000001f ? 1f : numerator / denominator;
    }

    private static string ShortHash(string hash)
    {
        return string.IsNullOrWhiteSpace(hash)
            ? string.Empty
            : hash.Substring(0, Math.Min(12, hash.Length));
    }

    private static string FormatBytes(long bytes)
    {
        return $"{Math.Round(bytes / 1024d / 1024d, 2)} MB";
    }

    private static string StripOriginalSuffix(string path)
    {
        string normalized = MCBUtils.ToUnityPath(path);
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.EndsWith(FileManagerService.OriginalSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring(0, normalized.Length - FileManagerService.OriginalSuffix.Length)
            : normalized;
    }

    private static bool SourceFileMatchesPath(
        ModelFileData file,
        string normalizedPath,
        MyCustomBase target = null)
    {
        if (file == null || string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        string sourcePath = MCBUtils.ToUnityPath(file.path);
        if (string.Equals(sourcePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return target != null && string.Equals(
            AvatarPathOverrideService.ResolveLocalTargetPath(target, file, updateStoredPathFromGuid: false),
            normalizedPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildUniqueSubAssetName(string baseName, HashSet<string> usedNames)
    {
        string safeName = string.IsNullOrWhiteSpace(baseName) ? "Mesh" : baseName;
        string candidate = safeName;
        int index = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{safeName}_{index++}";
        }

        return candidate;
    }

    private static string GetLastPathSegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').Trim('/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "mcb";
        }

        string result = value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(result) ? "mcb" : result;
    }
}
#endif
