#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MCBEditorUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class NativeMeshPayloadService
{
    public const string ExtraCustomizationKey = "advancedMeshReplacement";
    public const string TransformName = "XOR_BIN_TO_UNITY_ASSET";
    public const string PayloadFormat = "MCB_NATIVE_MESH_PAYLOAD";
    public const string PayloadCompressionMetadataKey = "payloadCompression";
    public const string PayloadCompressionGZip = "GZIP";
    public const string PayloadCompressionNone = "NONE";

    private const int PayloadVersion = 1;
    private const string BinaryPayloadMagic = "MCB_NATIVE_MESH_PAYLOAD";
    private const float SparseVectorEpsilon = 0.00001f;
    private const string GeneratedFolder = "Assets/MCB/generated/advancedMeshPayloads";
    private const string BuildTempFolder = "Assets/MCB/generated/advancedMeshBuildTemp";

    private static void LogApplyProfile(
        string label,
        System.Diagnostics.Stopwatch step,
        System.Diagnostics.Stopwatch total,
        string details = null)
    {
        UnityEngine.Debug.Log(
            string.IsNullOrWhiteSpace(details)
                ? $"[NativeMeshPayloadProfile] {label}: step={step.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms"
                : $"[NativeMeshPayloadProfile] {label}: step={step.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms {details}");
        step.Restart();
    }

    private static string FormatByteSize(long bytes)
    {
        const double mb = 1024d * 1024d;
        const double kb = 1024d;
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
        Transform sourcePoseRoot = null)
    {
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

        GameObject payloadSource = customFbx;
        GameObject temporaryPayloadSource = null;
        try
        {
            if (bakeDynamicNormals)
            {
                payloadSource = CreateDynamicNormalsPayloadSource(customFbx, includeDynamicNormalsBody, includeDynamicNormalsFlexing);
                temporaryPayloadSource = payloadSource != customFbx ? payloadSource : null;
            }

            byte[] baseData = File.ReadAllBytes(fullSourcePath);
            var rendererSources = ResolvePayloadRendererSources(payloadSource, smrPaths);
            if (rendererSources.Count == 0)
            {
                throw new InvalidOperationException("The custom FBX did not provide any matching skinned mesh data for the native mesh payload.");
            }

            string outputFullPath = Path.GetFullPath(outputBinPath);
            string outputDirectory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var metrics = new NativeMeshPayloadBuildMetrics();
            using (var payloadSha = SHA256.Create())
            using (var binSha = SHA256.Create())
            using (var output = File.Create(outputFullPath))
            using (var binHashStream = new HashingWriteStream(output, binSha, leaveOpen: false))
            using (var xorStream = new XorWriteStream(binHashStream, baseData, leaveOpen: false))
            using (var payloadHashStream = new HashingWriteStream(xorStream, payloadSha, leaveOpen: false))
            {
                CreateBinaryPayload(sourceFbxPath, payloadSource, rendererSources, payloadHashStream, metrics, compressPayload, sourcePoseRoot);
                payloadHashStream.CompleteHash();
                xorStream.Flush();
                binHashStream.CompleteHash();

                var result = new NativeMeshPayloadBuildResult
                {
                    payloadHash = BytesToHex(payloadSha.Hash),
                    binHash = BytesToHex(binSha.Hash),
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
                return result;
            }
        }
        finally
        {
            if (temporaryPayloadSource != null)
            {
                UnityEngine.Object.DestroyImmediate(temporaryPayloadSource);
            }
        }
    }

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

    private static GameObject CreateDynamicNormalsPayloadSource(GameObject customFbx, bool includeBody, bool includeFlexing)
    {
        var instance = UnityEngine.Object.Instantiate(customFbx);
        instance.name = $"{customFbx.name}_NativeMeshPayloadBuild";
        instance.hideFlags = HideFlags.HideAndDontSave;

        var bodyRenderer = MeshFinder.FindMeshPrioritizingRoot(instance.transform, "Body");
        if (bodyRenderer?.sharedMesh == null)
        {
            MCBLogger.LogWarning("[NativeMeshPayload] Dynamic normals bake was requested, but the custom payload source has no Body renderer.");
            return instance;
        }

        var targetBlendshapes = new List<string>();
        Mesh mesh = bodyRenderer.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string name = mesh.GetBlendShapeName(i);
            string lower = name.ToLowerInvariant();
            if ((includeBody && lower.Contains("muscle")) ||
                (includeFlexing && lower.Contains("flex")))
            {
                targetBlendshapes.Add(name);
            }
        }

        if (targetBlendshapes.Count == 0)
        {
            MCBLogger.LogWarning("[NativeMeshPayload] Dynamic normals bake was requested, but no Body blendshapes matched the selected dynamic normals filters.");
            return instance;
        }

        DynamicNormals.ForRoot(instance.transform)
            .limitToMeshes(new[] { bodyRenderer })
            .applyToBlendshapes(targetBlendshapes)
            .enable(true)
            .saveAsAsset(false)
            .Apply();

        MCBLogger.Log($"[NativeMeshPayload] Baked DynamicNormals into creator payload for {targetBlendshapes.Count} Body blendshape(s).");
        return instance;
    }

    public static NativeMeshPayloadAsset ApplyEncryptedPayload(
        Transform avatarRoot,
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService)
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

        if (string.IsNullOrWhiteSpace(originalFbxPath) || !File.Exists(originalFbxPath))
        {
            throw new FileNotFoundException("Apply failed: original FBX key file not found.", originalFbxPath);
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        UnityEngine.Debug.Log(
            $"[NativeMeshPayloadProfile] START apply payload version={version.version}");

        NativeMeshPayloadAsset payload = MaterializeEncryptedPayloadAsset(version, patchFile, binPath, originalFbxPath, fileManagerService);
        LogApplyProfile("Loaded/materialized native mesh payload asset", step, total, $"renderers={payload.renderers.Count} bones={payload.bones.Count}");
        ApplyPayloadToAvatar(avatarRoot, payload);
        LogApplyProfile("Applied native mesh payload to avatar", step, total);
        ApplyPayloadAuthoringPose(avatarRoot, payload);
        LogApplyProfile("Applied native mesh authoring pose to avatar", step, total, $"bones={payload.authoringPoseBones.Count}");
        UnityEngine.Debug.Log($"[NativeMeshPayloadProfile] DONE apply payload total={total.Elapsed.TotalMilliseconds:F1} ms");
        return payload;
    }

    public static IEnumerator ApplyEncryptedPayloadCoroutine(
        Transform avatarRoot,
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService,
        Action<float, string> reportProgress)
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

        if (string.IsNullOrWhiteSpace(originalFbxPath) || !File.Exists(originalFbxPath))
        {
            throw new FileNotFoundException("Apply failed: original FBX key file not found.", originalFbxPath);
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        UnityEngine.Debug.Log($"[NativeMeshPayloadProfile] START async apply payload version={version.version}");

        NativeMeshPayloadAsset payload = null;
        var materialize = MaterializeEncryptedPayloadAssetCoroutine(
            version,
            patchFile,
            binPath,
            originalFbxPath,
            fileManagerService,
            (progress, label) => reportProgress?.Invoke(Mathf.Lerp(0.02f, 0.72f, progress), label),
            result => payload = result);
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
        UnityEngine.Debug.Log($"[NativeMeshPayloadProfile] DONE async apply payload total={total.Elapsed.TotalMilliseconds:F1} ms");
    }

    public static NativeMeshPayloadAsset MaterializeEncryptedPayloadAsset(
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService)
    {
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

        if (!string.Equals(patchFile.transform, TransformName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Native mesh payload has unsupported transform '{patchFile.transform}'.");
        }

        if (string.IsNullOrWhiteSpace(patchFile.outputHash))
        {
            throw new InvalidDataException("Native mesh payload metadata is missing outputHash.");
        }

        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
        {
            throw new FileNotFoundException("Native mesh .bin file not found.", binPath);
        }

        if (string.IsNullOrWhiteSpace(originalFbxPath) || !File.Exists(originalFbxPath))
        {
            throw new FileNotFoundException("Original FBX key file not found for native mesh payload.", originalFbxPath);
        }

        string payloadHash = patchFile.outputHash;
        string payloadCompression = ResolvePayloadCompression(patchFile);
        string payloadAssetPath = GetGeneratedPayloadPath(version, patchFile, payloadHash);
        var existing = LoadPayloadAsset(payloadAssetPath);
        if (CachedPayloadMatches(existing, payloadHash, payloadCompression))
        {
            UnityEngine.Debug.Log($"[NativeMeshPayloadProfile] Using cached native mesh payload asset: {payloadAssetPath}");
            return existing;
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        long originalBytes = new FileInfo(originalFbxPath).Length;
        long binBytes = new FileInfo(binPath).Length;
        UnityEngine.Debug.Log(
            $"[NativeMeshPayloadProfile] Materializing payload asset cache version={version.version} compression={payloadCompression} bin={FormatByteSize(binBytes)} originalFbxKey={FormatByteSize(originalBytes)}");

        byte[] baseData = File.ReadAllBytes(originalFbxPath);
        LogApplyProfile("Read original FBX XOR key", step, total, $"bytes={FormatByteSize(baseData.LongLength)}");
        byte[] binData = File.ReadAllBytes(binPath);
        LogApplyProfile("Read encrypted advanced mesh .bin", step, total, $"bytes={FormatByteSize(binData.LongLength)}");
        byte[] payloadBytes = fileManagerService.XorTransform(baseData, binData);
        LogApplyProfile("XOR decrypted native mesh payload", step, total, $"bytes={FormatByteSize(payloadBytes.LongLength)}");

        NativeMeshPayloadAsset payload = WriteBinaryPayloadAsset(payloadAssetPath, payloadBytes, payloadHash, payloadCompression);
        LogApplyProfile("Wrote native mesh payload asset cache", step, total, $"path={payloadAssetPath} renderers={payload.renderers.Count} bones={payload.bones.Count}");
        return payload;
    }

    public static IEnumerator MaterializeEncryptedPayloadAssetCoroutine(
        CustomBaseVersion version,
        ModelFileData patchFile,
        string binPath,
        string originalFbxPath,
        FileManagerService fileManagerService,
        Action<float, string> reportProgress,
        Action<NativeMeshPayloadAsset> completed)
    {
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

        if (!string.Equals(patchFile.transform, TransformName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Native mesh payload has unsupported transform '{patchFile.transform}'.");
        }

        if (string.IsNullOrWhiteSpace(patchFile.outputHash))
        {
            throw new InvalidDataException("Native mesh payload metadata is missing outputHash.");
        }

        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
        {
            throw new FileNotFoundException("Native mesh .bin file not found.", binPath);
        }

        if (string.IsNullOrWhiteSpace(originalFbxPath) || !File.Exists(originalFbxPath))
        {
            throw new FileNotFoundException("Original FBX key file not found for native mesh payload.", originalFbxPath);
        }

        string payloadHash = patchFile.outputHash;
        string payloadCompression = ResolvePayloadCompression(patchFile);
        string payloadAssetPath = GetGeneratedPayloadPath(version, patchFile, payloadHash);
        var existing = LoadPayloadAsset(payloadAssetPath);
        if (CachedPayloadMatches(existing, payloadHash, payloadCompression))
        {
            UnityEngine.Debug.Log($"[NativeMeshPayloadProfile] Using cached native mesh payload asset: {payloadAssetPath}");
            reportProgress?.Invoke(1f, "Loaded cached advanced mesh...");
            completed?.Invoke(existing);
            yield break;
        }

        var status = new NativeMeshPayloadPreparationStatus();
        long originalBytes = new FileInfo(originalFbxPath).Length;
        long binBytes = new FileInfo(binPath).Length;
        UnityEngine.Debug.Log(
            $"[NativeMeshPayloadProfile] Async materializing payload asset cache version={version.version} compression={payloadCompression} bin={FormatByteSize(binBytes)} originalFbxKey={FormatByteSize(originalBytes)}");

        string assetName = Path.GetFileNameWithoutExtension(MCBUtils.ToUnityPath(payloadAssetPath));
        var prepareTask = Task.Run(() => PrepareNativeMeshPayloadData(
            binPath,
            originalFbxPath,
            assetName,
            payloadHash,
            payloadCompression,
            status));

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

    public static void RestoreOriginalMeshesFromFbx(Transform avatarRoot, CustomBaseVersion version, IEnumerable<string> sourceFbxPaths)
    {
        if (avatarRoot == null)
        {
            return;
        }

        foreach (string rawPath in sourceFbxPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string sourcePath = MCBUtils.ToUnityPath(rawPath);
            ApplySourceBoneTransforms(avatarRoot, sourcePath);
            var smrPaths = SmrPathService.ResolveSmrPathsForSource(version, sourcePath);
            SmrPathService.RefreshTargetMeshesFromFbx(avatarRoot, sourcePath, smrPaths);
        }
    }

    public static void RestoreOriginalAuthoringPoseFromFbx(Transform avatarRoot, IEnumerable<string> sourceFbxPaths)
    {
        if (avatarRoot == null)
        {
            return;
        }

        foreach (string rawPath in sourceFbxPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            ApplySourceBoneTransforms(avatarRoot, MCBUtils.ToUnityPath(rawPath));
        }

        RefreshAvatarSkinnedRenderers(avatarRoot);
    }

    public static bool IsVersionApplied(Transform avatarRoot, CustomBaseVersion version)
    {
        if (avatarRoot == null || version == null)
        {
            return false;
        }

        var advancedPatches = GetAdvancedPatchFiles(version).ToList();
        if (advancedPatches.Count == 0)
        {
            return false;
        }

        foreach (var patch in advancedPatches)
        {
            if (string.IsNullOrWhiteSpace(patch.outputHash))
            {
                return false;
            }

            bool matchedPatch = false;
            foreach (var source in GetSourceFilesForPatch(version, patch))
            {
                foreach (var renderer in ResolveRenderersForSource(avatarRoot, source))
                {
                    string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh));
                    string meshName = renderer.sharedMesh != null ? renderer.sharedMesh.name : null;
                    string shortHash = ShortHash(patch.outputHash);
                    if ((!string.IsNullOrWhiteSpace(meshPath) &&
                         meshPath.IndexOf(patch.outputHash, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrWhiteSpace(meshName) &&
                         meshName.IndexOf(shortHash, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        matchedPatch = true;
                        break;
                    }
                }

                if (matchedPatch)
                {
                    break;
                }
            }

            if (!matchedPatch)
            {
                return false;
            }
        }

        return true;
    }

    public static bool HasAnyAdvancedMeshApplied(Transform avatarRoot, CustomBaseVersion version)
    {
        if (avatarRoot == null || version == null)
        {
            return false;
        }

        foreach (var patch in GetAdvancedPatchFiles(version))
        {
            if (string.IsNullOrWhiteSpace(patch.outputHash))
            {
                continue;
            }

            foreach (var source in GetSourceFilesForPatch(version, patch))
            {
                foreach (var renderer in ResolveRenderersForSource(avatarRoot, source))
                {
                    string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh));
                    string meshName = renderer.sharedMesh != null ? renderer.sharedMesh.name : null;
                    string shortHash = ShortHash(patch.outputHash);
                    if ((!string.IsNullOrWhiteSpace(meshPath) &&
                         meshPath.IndexOf(patch.outputHash, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrWhiteSpace(meshName) &&
                         meshName.IndexOf(shortHash, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static List<SkinnedMeshRenderer> ResolveRenderersForSourcePaths(
        Transform avatarRoot,
        CustomBaseVersion version,
        IEnumerable<string> sourceFbxPaths)
    {
        var renderers = new List<SkinnedMeshRenderer>();
        var seen = new HashSet<int>();
        if (avatarRoot == null)
        {
            return renderers;
        }

        foreach (string rawPath in sourceFbxPaths ?? Enumerable.Empty<string>())
        {
            string sourcePath = MCBUtils.ToUnityPath(rawPath);
            var source = version?.sourceFiles?.FirstOrDefault(file =>
                file != null &&
                string.Equals(MCBUtils.ToUnityPath(file.path), sourcePath, StringComparison.OrdinalIgnoreCase));
            foreach (var renderer in ResolveRenderersForSource(avatarRoot, source))
            {
                if (renderer != null && seen.Add(renderer.GetInstanceID()))
                {
                    renderers.Add(renderer);
                }
            }
        }

        return renderers;
    }

    public static bool VersionUsesAdvancedMesh(CustomBaseVersion version)
    {
        if (version == null)
        {
            return false;
        }

        bool hasExtra = ExtraCustomizationUtils.HasFlag(version.extraCustomization, ExtraCustomizationKey);
        return hasExtra || GetAdvancedPatchFiles(version).Any();
    }

    public static bool IsAdvancedMeshPatchTransform(string transform)
    {
        return string.Equals(transform, TransformName, StringComparison.OrdinalIgnoreCase);
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
                continue;
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

    private static void CreateBinaryPayload(
        string sourceFbxPath,
        GameObject customFbx,
        List<PayloadRendererSource> rendererSources,
        Stream payloadOutput,
        NativeMeshPayloadBuildMetrics metrics,
        bool compressPayload,
        Transform sourcePoseRoot)
    {
        if (compressPayload)
        {
            using (var gzip = new GZipStream(payloadOutput, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                WriteBinaryPayloadContents(sourceFbxPath, customFbx, rendererSources, gzip, metrics, sourcePoseRoot);
            }

            return;
        }

        WriteBinaryPayloadContents(sourceFbxPath, customFbx, rendererSources, payloadOutput, metrics, sourcePoseRoot);
    }

    private static void WriteBinaryPayloadContents(
        string sourceFbxPath,
        GameObject customFbx,
        List<PayloadRendererSource> rendererSources,
        Stream payloadOutput,
        NativeMeshPayloadBuildMetrics metrics,
        Transform sourcePoseRoot)
    {
        Transform customRoot = customFbx.transform;
        string sourceFbxHash = MCBUtils.CalculateFileHash(Path.GetFullPath(sourceFbxPath));
        var rendererRecordsForBones = new List<NativeMeshPayloadRenderer>();
        var usedMeshNames = new HashSet<string>(StringComparer.Ordinal);

        using (var writer = new BinaryWriter(payloadOutput, Encoding.UTF8, true))
        {
            writer.Write(BinaryPayloadMagic);
            writer.Write(PayloadVersion);
            writer.Write(StripOriginalSuffix(MCBUtils.ToUnityPath(sourceFbxPath)) ?? "");
            writer.Write(sourceFbxHash ?? "");
            writer.Write(rendererSources.Count);

            foreach (var source in rendererSources)
            {
                var renderer = source.renderer;
                var entry = source.entry;
                Mesh mesh = renderer.sharedMesh;
                string meshName = string.IsNullOrWhiteSpace(entry.meshName)
                    ? mesh.name
                    : entry.meshName;
                string uniqueMeshName = BuildUniqueSubAssetName(meshName, usedMeshNames);
                var bonePaths = (renderer.bones ?? Array.Empty<Transform>())
                    .Select(bone => bone != null ? GetRelativeTransformPath(customRoot, bone) : "")
                    .ToList();
                if (mesh.bindposes != null && mesh.bindposes.Length > 0 && mesh.bindposes.Length != bonePaths.Count)
                {
                    MCBLogger.LogWarning($"[NativeMeshPayload] Renderer '{renderer.name}' has {mesh.bindposes.Length} bindposes but {bonePaths.Count} bones. The applied mesh may deform incorrectly.");
                }
                string rootBonePath = renderer.rootBone != null
                    ? GetRelativeTransformPath(customRoot, renderer.rootBone)
                    : "";

                writer.Write(entry.avatarPath ?? "");
                writer.Write(entry.fbxMeshPath ?? "");
                writer.Write(entry.meshName ?? "");
                writer.Write(entry.rendererName ?? "");
                WriteVector3(writer, renderer.transform.localPosition);
                WriteQuaternion(writer, renderer.transform.localRotation);
                WriteVector3(writer, renderer.transform.localScale);
                writer.Write(rootBonePath ?? "");
                WriteStringList(writer, bonePaths);
                WriteMesh(writer, mesh, uniqueMeshName, metrics);

                rendererRecordsForBones.Add(new NativeMeshPayloadRenderer
                {
                    rootBonePath = rootBonePath,
                    bonePaths = bonePaths
                });
            }

            var payloadBonePaths = CollectPayloadBonePaths(rendererRecordsForBones);
            var bones = customFbx.GetComponentsInChildren<Transform>(true)
                .Where(transform => transform != null && transform != customRoot)
                .Where(transform =>
                {
                    if (payloadBonePaths.Count == 0) return false;
                    string path = GetRelativeTransformPath(customRoot, transform);
                    return payloadBonePaths.Contains(path);
                })
                .Select(transform => new NativeMeshPayloadBone
                {
                    path = GetRelativeTransformPath(customRoot, transform),
                    localPosition = transform.localPosition,
                    localRotation = transform.localRotation,
                    localScale = transform.localScale
                })
                .ToList();

            writer.Write(bones.Count);
            foreach (var bone in bones)
            {
                writer.Write(bone.path ?? "");
                WriteVector3(writer, bone.localPosition);
                WriteQuaternion(writer, bone.localRotation);
                WriteVector3(writer, bone.localScale);
            }

            var authoringPoseBones = BuildAuthoringPoseDeltas(sourceFbxPath, customRoot, sourcePoseRoot);
            writer.Write(authoringPoseBones.Count);
            foreach (var bone in authoringPoseBones)
            {
                writer.Write(bone.path ?? "");
                WriteVector3(writer, bone.localPositionOffset);
                WriteQuaternion(writer, bone.localRotationDelta);
                WriteVector3(writer, bone.localScaleMultiplier);
            }
            MCBLogger.Log($"[NativeMeshPayload] Captured authoring pose deltas: bones={authoringPoseBones.Count}");

            writer.Flush();
        }
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

    private static Transform ResolveSourcePoseRoot(string sourceFbxPath)
    {
        string sourceUnityPath = StripOriginalSuffix(MCBUtils.ToUnityPath(sourceFbxPath));
        if (string.IsNullOrWhiteSpace(sourceUnityPath))
        {
            return null;
        }

        var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourceUnityPath);
        return sourceAsset != null ? sourceAsset.transform : null;
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

    private static NativeMeshPayloadAsset WriteBinaryPayloadAsset(string assetPath, byte[] payloadBytes, string payloadHash, string payloadCompression)
    {
        string normalizedPath = MCBUtils.ToUnityPath(assetPath);
        EnsureAssetFolder(Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/'));

        var existing = LoadPayloadAsset(normalizedPath);
        if (CachedPayloadMatches(existing, payloadHash, payloadCompression))
        {
            return existing;
        }

        DeleteAssetFile(normalizedPath);

        string assetName = Path.GetFileNameWithoutExtension(normalizedPath);
        var payload = ReadBinaryPayloadAsset(payloadBytes, assetName, payloadHash, payloadCompression);
        payload.payloadCompression = NormalizePayloadCompression(payloadCompression);
        AssetDatabase.CreateAsset(payload, normalizedPath);
        foreach (var renderer in payload.renderers ?? new List<NativeMeshPayloadRenderer>())
        {
            if (renderer?.mesh != null)
            {
                AssetDatabase.AddObjectToAsset(renderer.mesh, payload);
            }
        }

        EditorUtility.SetDirty(payload);
        AssetDatabase.SaveAssets();

        return payload;
    }

    private static NativeMeshPayloadAsset ReadBinaryPayloadAsset(byte[] payloadBytes, string assetName, string payloadHash, string payloadCompression)
    {
        if (payloadBytes == null || payloadBytes.Length == 0)
        {
            throw new InvalidDataException("Native mesh payload is empty.");
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using (var stream = new MemoryStream(payloadBytes, false))
            {
                if (PayloadUsesGZip(payloadCompression))
                {
                    using (var gzip = new GZipStream(stream, System.IO.Compression.CompressionMode.Decompress))
                    using (var reader = new BinaryReader(gzip, Encoding.UTF8))
                    {
                        return ReadBinaryPayloadAssetContents(reader, assetName, payloadHash, total, step);
                    }
                }

                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    return ReadBinaryPayloadAssetContents(reader, assetName, payloadHash, total, step);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Native mesh payload could not be read with the expected MCB payload format.", ex);
        }
    }

    private static NativeMeshPayloadAsset ReadBinaryPayloadAssetContents(
        BinaryReader reader,
        string assetName,
        string payloadHash,
        System.Diagnostics.Stopwatch total,
        System.Diagnostics.Stopwatch step)
    {
        string magic = reader.ReadString();
        if (!string.Equals(magic, BinaryPayloadMagic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native mesh payload is not an MCB binary payload.");
        }

        int version = reader.ReadInt32();
        if (version != PayloadVersion)
        {
            throw new InvalidDataException($"Unsupported native mesh payload version: {version}");
        }

        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        payload.name = assetName;
        payload.payloadVersion = version;
        payload.sourceFbxPath = reader.ReadString();
        payload.sourceFbxHash = reader.ReadString();
        payload.payloadHash = payloadHash;

        int rendererCount = reader.ReadInt32();
        LogApplyProfile("Read payload header", step, total, $"renderers={rendererCount}");
        var usedMeshNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rendererCount; i++)
        {
            var rendererStep = System.Diagnostics.Stopwatch.StartNew();
            var record = new NativeMeshPayloadRenderer
            {
                avatarPath = reader.ReadString(),
                fbxMeshPath = reader.ReadString(),
                meshName = reader.ReadString(),
                rendererName = reader.ReadString(),
                localPosition = ReadVector3(reader),
                localRotation = ReadQuaternion(reader),
                localScale = ReadVector3(reader),
                rootBonePath = reader.ReadString(),
                bonePaths = ReadStringList(reader)
            };

            record.mesh = ReadMesh(reader);
            record.mesh.name = BuildUniqueSubAssetName($"{record.mesh.name}_{ShortHash(payloadHash)}", usedMeshNames);
            payload.renderers.Add(record);
            UnityEngine.Debug.Log(
                $"[NativeMeshPayloadProfile] Read renderer {i + 1}/{rendererCount} '{record.rendererName}' mesh='{record.mesh.name}' " +
                $"verts={record.mesh.vertexCount} subMeshes={record.mesh.subMeshCount} blendShapes={record.mesh.blendShapeCount}: " +
                $"step={rendererStep.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms");
            step.Restart();
        }

        int boneCount = reader.ReadInt32();
        for (int i = 0; i < boneCount; i++)
        {
            payload.bones.Add(new NativeMeshPayloadBone
            {
                path = reader.ReadString(),
                localPosition = ReadVector3(reader),
                localRotation = ReadQuaternion(reader),
                localScale = ReadVector3(reader)
            });
        }

        LogApplyProfile("Read payload bone transform table", step, total, $"bones={boneCount}");
        int authoringPoseBoneCount = reader.ReadInt32();
        for (int i = 0; i < authoringPoseBoneCount; i++)
        {
            payload.authoringPoseBones.Add(new NativeMeshPayloadAuthoringPoseBone
            {
                path = reader.ReadString(),
                localPositionOffset = ReadVector3(reader),
                localRotationDelta = ReadQuaternion(reader),
                localScaleMultiplier = ReadVector3(reader)
            });
        }

        LogApplyProfile("Read payload authoring pose delta table", step, total, $"bones={authoringPoseBoneCount}");
        return payload;
    }

    private static void WriteMesh(BinaryWriter writer, Mesh mesh, string meshName, NativeMeshPayloadBuildMetrics metrics)
    {
        if (mesh == null)
        {
            throw new InvalidOperationException("Native mesh payload cannot serialize a null mesh.");
        }

        try
        {
            var vertices = mesh.vertices;
            writer.Write(string.IsNullOrWhiteSpace(meshName) ? mesh.name : meshName);
            writer.Write((int)mesh.indexFormat);
            WriteVector3Array(writer, vertices);
            WriteVector3Array(writer, mesh.normals);
            WriteVector4Array(writer, mesh.tangents);
            WriteColor32Array(writer, mesh.colors32);

            for (int channel = 0; channel < 8; channel++)
            {
                var uvs = new List<Vector4>();
                mesh.GetUVs(channel, uvs);
                WriteVector4List(writer, uvs);
            }

            WriteBoneWeightArray(writer, mesh.boneWeights);
            WriteMatrixArray(writer, mesh.bindposes);

            writer.Write(mesh.subMeshCount);
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                writer.Write((int)mesh.GetTopology(i));
                WriteIntArray(writer, mesh.GetIndices(i, true));
            }

            WriteBounds(writer, mesh.bounds);

            int vertexCount = vertices.Length;
            writer.Write(mesh.blendShapeCount);
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                string shapeName = mesh.GetBlendShapeName(shape) ?? "";
                bool includeNormalTangents = ShouldSerializeBlendShapeNormalTangents(mesh, shapeName);
                writer.Write(shapeName);
                int frameCount = mesh.GetBlendShapeFrameCount(shape);
                writer.Write(frameCount);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    Array.Clear(deltaVertices, 0, deltaVertices.Length);
                    Array.Clear(deltaNormals, 0, deltaNormals.Length);
                    Array.Clear(deltaTangents, 0, deltaTangents.Length);
                    mesh.GetBlendShapeFrameVertices(
                        shape,
                        frame,
                        deltaVertices,
                        includeNormalTangents ? deltaNormals : null,
                        includeNormalTangents ? deltaTangents : null);
                    writer.Write(mesh.GetBlendShapeFrameWeight(shape, frame));
                    long vertexBytes = WriteSparseVector3Array(writer, deltaVertices);
                    writer.Write(includeNormalTangents);
                    long normalBytes = includeNormalTangents ? WriteSparseVector3Array(writer, deltaNormals) : 0L;
                    writer.Write(includeNormalTangents);
                    long tangentBytes = includeNormalTangents ? WriteSparseVector3Array(writer, deltaTangents) : 0L;
                    if (metrics != null)
                    {
                        metrics.blendShapeVertexBytes += vertexBytes;
                        metrics.skippedBlendShapeNormalBytes += includeNormalTangents ? 0L : EstimateSparseVector3ArrayWorstCaseBytes(vertexCount);
                        metrics.skippedBlendShapeTangentBytes += includeNormalTangents ? 0L : EstimateSparseVector3ArrayWorstCaseBytes(vertexCount);
                        metrics.blendShapeNormalBytes += normalBytes;
                        metrics.blendShapeTangentBytes += tangentBytes;
                    }
                }
            }
        }
        catch (UnityException ex)
        {
            throw new InvalidOperationException(
                $"Native mesh payload cannot read mesh '{mesh.name}'. Enable Read/Write on the custom model import settings or resend the edit through Blender Link.",
                ex);
        }
    }

    private static Mesh ReadMesh(BinaryReader reader)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        var mesh = new Mesh();
        mesh.name = reader.ReadString();
        mesh.indexFormat = (IndexFormat)reader.ReadInt32();

        var vertices = ReadVector3Array(reader);
        mesh.vertices = vertices;

        var normals = ReadVector3Array(reader);
        if (normals.Length == vertices.Length) mesh.normals = normals;

        var tangents = ReadVector4Array(reader);
        if (tangents.Length == vertices.Length) mesh.tangents = tangents;

        var colors = ReadColor32Array(reader);
        if (colors.Length == vertices.Length) mesh.colors32 = colors;

        for (int channel = 0; channel < 8; channel++)
        {
            var uvs = ReadVector4List(reader);
            if (uvs.Count == vertices.Length)
            {
                mesh.SetUVs(channel, uvs);
            }
        }

        var boneWeights = ReadBoneWeightArray(reader);
        if (boneWeights.Length == vertices.Length) mesh.boneWeights = boneWeights;

        var bindposes = ReadMatrixArray(reader);
        if (bindposes.Length > 0) mesh.bindposes = bindposes;
        LogApplyProfile(
            $"Read mesh '{mesh.name}' vertex streams",
            step,
            total,
            $"verts={vertices.Length} normals={normals.Length} tangents={tangents.Length} bindposes={bindposes.Length}");

        int subMeshCount = reader.ReadInt32();
        mesh.subMeshCount = subMeshCount;
        for (int i = 0; i < subMeshCount; i++)
        {
            var topology = (MeshTopology)reader.ReadInt32();
            var indices = ReadIntArray(reader);
            mesh.SetIndices(indices, topology, i, false);
        }

        mesh.bounds = ReadBounds(reader);
        LogApplyProfile($"Read mesh '{mesh.name}' submeshes", step, total, $"subMeshes={subMeshCount}");

        int blendShapeCount = reader.ReadInt32();
        int blendShapeFrameCount = 0;
        for (int shape = 0; shape < blendShapeCount; shape++)
        {
            string shapeName = reader.ReadString();
            int frameCount = reader.ReadInt32();
            blendShapeFrameCount += frameCount;
            for (int frame = 0; frame < frameCount; frame++)
            {
                float weight = reader.ReadSingle();
                var deltaVertices = ReadSparseVector3Array(reader);
                var deltaNormals = reader.ReadBoolean() ? ReadSparseVector3Array(reader) : null;
                var deltaTangents = reader.ReadBoolean() ? ReadSparseVector3Array(reader) : null;
                mesh.AddBlendShapeFrame(shapeName, weight, deltaVertices, deltaNormals, deltaTangents);
            }
        }
        LogApplyProfile(
            $"Read mesh '{mesh.name}' blendshapes",
            step,
            total,
            $"blendShapes={blendShapeCount} frames={blendShapeFrameCount}");

        return mesh;
    }

    private static PreparedPayloadAssetData PrepareNativeMeshPayloadData(
        string binPath,
        string originalFbxPath,
        string assetName,
        string payloadHash,
        string payloadCompression,
        NativeMeshPayloadPreparationStatus status)
    {
        status.Report(0.02f, "Reading original FBX key...");
        byte[] baseData = File.ReadAllBytes(originalFbxPath);
        status.Report(0.12f, "Reading advanced mesh payload...");
        byte[] binData = File.ReadAllBytes(binPath);
        status.Report(0.24f, "Decrypting advanced mesh payload...");
        byte[] payloadBytes = XorTransformWithProgress(baseData, binData, status, 0.24f, 0.48f);
        status.Report(0.50f, PayloadUsesGZip(payloadCompression) ? "Decompressing advanced mesh payload..." : "Parsing advanced mesh payload...");
        var payload = ReadPreparedPayloadAsset(payloadBytes, assetName, payloadHash, payloadCompression, status, 0.50f, 1f);
        status.Report(1f, "Advanced mesh payload prepared...");
        return payload;
    }

    private static byte[] XorTransformWithProgress(
        byte[] baseData,
        byte[] keyData,
        NativeMeshPayloadPreparationStatus status,
        float startProgress,
        float endProgress)
    {
        if (baseData == null || baseData.Length == 0)
        {
            throw new InvalidDataException("Original FBX key data is empty.");
        }

        byte[] transformedData = new byte[keyData.Length];
        const int progressStride = 4 * 1024 * 1024;
        for (int i = 0; i < keyData.Length; i++)
        {
            transformedData[i] = (byte)(keyData[i] ^ baseData[i % baseData.Length]);
            if (i > 0 && i % progressStride == 0)
            {
                float local = keyData.Length > 0 ? i / (float)keyData.Length : 1f;
                status.Report(Lerp(startProgress, endProgress, local), "Decrypting advanced mesh payload...");
            }
        }

        return transformedData;
    }

    private static PreparedPayloadAssetData ReadPreparedPayloadAsset(
        byte[] payloadBytes,
        string assetName,
        string payloadHash,
        string payloadCompression,
        NativeMeshPayloadPreparationStatus status,
        float startProgress,
        float endProgress)
    {
        if (payloadBytes == null || payloadBytes.Length == 0)
        {
            throw new InvalidDataException("Native mesh payload is empty.");
        }

        try
        {
            using (var stream = new MemoryStream(payloadBytes, false))
            {
                if (PayloadUsesGZip(payloadCompression))
                {
                    using (var gzip = new GZipStream(stream, System.IO.Compression.CompressionMode.Decompress))
                    using (var reader = new BinaryReader(gzip, Encoding.UTF8))
                    {
                        return ReadPreparedPayloadAssetContents(reader, assetName, payloadHash, status, startProgress, endProgress);
                    }
                }

                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    return ReadPreparedPayloadAssetContents(reader, assetName, payloadHash, status, startProgress, endProgress);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("Native mesh payload could not be read with the expected MCB payload format.", ex);
        }
    }

    private static PreparedPayloadAssetData ReadPreparedPayloadAssetContents(
        BinaryReader reader,
        string assetName,
        string payloadHash,
        NativeMeshPayloadPreparationStatus status,
        float startProgress,
        float endProgress)
    {
        string magic = reader.ReadString();
        if (!string.Equals(magic, BinaryPayloadMagic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native mesh payload is not an MCB binary payload.");
        }

        int version = reader.ReadInt32();
        if (version != PayloadVersion)
        {
            throw new InvalidDataException($"Unsupported native mesh payload version: {version}");
        }

        var payload = new PreparedPayloadAssetData
        {
            assetName = assetName,
            payloadVersion = version,
            sourceFbxPath = reader.ReadString(),
            sourceFbxHash = reader.ReadString(),
            payloadHash = payloadHash
        };

        int rendererCount = reader.ReadInt32();
        status.Report(startProgress, $"Parsing advanced mesh header ({rendererCount} renderer{(rendererCount == 1 ? "" : "s")})...");
        var usedMeshNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rendererCount; i++)
        {
            var record = new PreparedPayloadRendererData
            {
                avatarPath = reader.ReadString(),
                fbxMeshPath = reader.ReadString(),
                meshName = reader.ReadString(),
                rendererName = reader.ReadString(),
                localPosition = ReadVector3(reader),
                localRotation = ReadQuaternion(reader),
                localScale = ReadVector3(reader),
                rootBonePath = reader.ReadString(),
                bonePaths = ReadStringList(reader)
            };

            record.mesh = ReadPreparedMesh(reader);
            record.mesh.name = BuildUniqueSubAssetName($"{record.mesh.name}_{ShortHash(payloadHash)}", usedMeshNames);
            payload.renderers.Add(record);
            float local = rendererCount > 0 ? (i + 1f) / rendererCount : 1f;
            status.Report(Lerp(startProgress, Lerp(startProgress, endProgress, 0.82f), local), $"Parsing advanced mesh {i + 1}/{rendererCount}...");
        }

        int boneCount = reader.ReadInt32();
        for (int i = 0; i < boneCount; i++)
        {
            payload.bones.Add(new NativeMeshPayloadBone
            {
                path = reader.ReadString(),
                localPosition = ReadVector3(reader),
                localRotation = ReadQuaternion(reader),
                localScale = ReadVector3(reader)
            });
        }

        status.Report(Lerp(startProgress, endProgress, 0.88f), $"Parsing advanced mesh bone table ({boneCount})...");
        int authoringPoseBoneCount = reader.ReadInt32();
        for (int i = 0; i < authoringPoseBoneCount; i++)
        {
            payload.authoringPoseBones.Add(new NativeMeshPayloadAuthoringPoseBone
            {
                path = reader.ReadString(),
                localPositionOffset = ReadVector3(reader),
                localRotationDelta = ReadQuaternion(reader),
                localScaleMultiplier = ReadVector3(reader)
            });
        }

        status.Report(endProgress, $"Parsed advanced mesh authoring pose ({authoringPoseBoneCount})...");
        return payload;
    }

    private static PreparedMeshData ReadPreparedMesh(BinaryReader reader)
    {
        var mesh = new PreparedMeshData
        {
            name = reader.ReadString(),
            indexFormat = (IndexFormat)reader.ReadInt32(),
            vertices = ReadVector3Array(reader),
            normals = ReadVector3Array(reader),
            tangents = ReadVector4Array(reader),
            colors = ReadColor32Array(reader),
            uvs = new List<Vector4>[8]
        };

        for (int channel = 0; channel < 8; channel++)
        {
            mesh.uvs[channel] = ReadVector4List(reader);
        }

        mesh.boneWeights = ReadBoneWeightArray(reader);
        mesh.bindposes = ReadMatrixArray(reader);

        int subMeshCount = reader.ReadInt32();
        for (int i = 0; i < subMeshCount; i++)
        {
            mesh.subMeshes.Add(new PreparedSubMeshData
            {
                topology = (MeshTopology)reader.ReadInt32(),
                indices = ReadIntArray(reader)
            });
        }

        mesh.bounds = ReadBounds(reader);
        int blendShapeCount = reader.ReadInt32();
        for (int shape = 0; shape < blendShapeCount; shape++)
        {
            var blendShape = new PreparedBlendShapeData
            {
                name = reader.ReadString()
            };
            int frameCount = reader.ReadInt32();
            for (int frame = 0; frame < frameCount; frame++)
            {
                blendShape.frames.Add(new PreparedBlendShapeFrameData
                {
                    weight = reader.ReadSingle(),
                    deltaVertices = ReadSparseVector3Array(reader),
                    deltaNormals = reader.ReadBoolean() ? ReadSparseVector3Array(reader) : null,
                    deltaTangents = reader.ReadBoolean() ? ReadSparseVector3Array(reader) : null
                });
            }

            mesh.blendShapes.Add(blendShape);
        }

        return mesh;
    }

    private static IEnumerator CommitPreparedPayloadAssetCoroutine(
        string assetPath,
        PreparedPayloadAssetData prepared,
        string payloadHash,
        string payloadCompression,
        Action<float, string> reportProgress,
        Action<NativeMeshPayloadAsset> completed)
    {
        string normalizedPath = MCBUtils.ToUnityPath(assetPath);
        EnsureAssetFolder(Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/'));

        var existing = LoadPayloadAsset(normalizedPath);
        if (CachedPayloadMatches(existing, payloadHash, payloadCompression))
        {
            reportProgress?.Invoke(1f, "Loaded cached advanced mesh...");
            completed?.Invoke(existing);
            yield break;
        }

        DeleteAssetFile(normalizedPath);

        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        payload.name = prepared.assetName;
        payload.payloadVersion = prepared.payloadVersion;
        payload.sourceFbxPath = prepared.sourceFbxPath;
        payload.sourceFbxHash = prepared.sourceFbxHash;
        payload.payloadHash = prepared.payloadHash;
        payload.payloadCompression = NormalizePayloadCompression(payloadCompression);
        payload.bones.AddRange(prepared.bones);
        payload.authoringPoseBones.AddRange(prepared.authoringPoseBones);

        int rendererCount = prepared.renderers.Count;
        for (int i = 0; i < rendererCount; i++)
        {
            var preparedRenderer = prepared.renderers[i];
            var renderer = new NativeMeshPayloadRenderer
            {
                avatarPath = preparedRenderer.avatarPath,
                fbxMeshPath = preparedRenderer.fbxMeshPath,
                meshName = preparedRenderer.meshName,
                rendererName = preparedRenderer.rendererName,
                localPosition = preparedRenderer.localPosition,
                localRotation = preparedRenderer.localRotation,
                localScale = preparedRenderer.localScale,
                rootBonePath = preparedRenderer.rootBonePath,
                bonePaths = preparedRenderer.bonePaths ?? new List<string>(),
                mesh = CreateMeshFromPreparedData(preparedRenderer.mesh)
            };
            payload.renderers.Add(renderer);
            reportProgress?.Invoke(rendererCount > 0 ? (i + 1f) / rendererCount * 0.72f : 0.72f, $"Creating Unity mesh {i + 1}/{rendererCount}...");
            yield return null;
        }

        reportProgress?.Invoke(0.78f, "Creating advanced mesh cache asset...");
        AssetDatabase.CreateAsset(payload, normalizedPath);
        for (int i = 0; i < payload.renderers.Count; i++)
        {
            var renderer = payload.renderers[i];
            if (renderer?.mesh != null)
            {
                AssetDatabase.AddObjectToAsset(renderer.mesh, payload);
            }
            reportProgress?.Invoke(Mathf.Lerp(0.78f, 0.90f, payload.renderers.Count > 0 ? (i + 1f) / payload.renderers.Count : 1f), $"Registering cached mesh {i + 1}/{payload.renderers.Count}...");
            yield return null;
        }

        reportProgress?.Invoke(0.94f, "Saving advanced mesh cache...");
        EditorUtility.SetDirty(payload);
        AssetDatabase.SaveAssets();
        reportProgress?.Invoke(1f, "Advanced mesh cache ready...");
        completed?.Invoke(payload);
    }

    private static Mesh CreateMeshFromPreparedData(PreparedMeshData data)
    {
        var mesh = new Mesh();
        mesh.name = data.name;
        mesh.indexFormat = data.indexFormat;
        mesh.vertices = data.vertices ?? Array.Empty<Vector3>();

        int vertexCount = mesh.vertexCount;
        if (data.normals != null && data.normals.Length == vertexCount) mesh.normals = data.normals;
        if (data.tangents != null && data.tangents.Length == vertexCount) mesh.tangents = data.tangents;
        if (data.colors != null && data.colors.Length == vertexCount) mesh.colors32 = data.colors;

        for (int channel = 0; channel < 8; channel++)
        {
            var uvs = data.uvs != null && channel < data.uvs.Length ? data.uvs[channel] : null;
            if (uvs != null && uvs.Count == vertexCount)
            {
                mesh.SetUVs(channel, uvs);
            }
        }

        if (data.boneWeights != null && data.boneWeights.Length == vertexCount) mesh.boneWeights = data.boneWeights;
        if (data.bindposes != null && data.bindposes.Length > 0) mesh.bindposes = data.bindposes;

        mesh.subMeshCount = data.subMeshes?.Count ?? 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            var subMesh = data.subMeshes[i];
            mesh.SetIndices(subMesh.indices ?? Array.Empty<int>(), subMesh.topology, i, false);
        }

        mesh.bounds = data.bounds;
        foreach (var blendShape in data.blendShapes ?? new List<PreparedBlendShapeData>())
        {
            foreach (var frame in blendShape.frames ?? new List<PreparedBlendShapeFrameData>())
            {
                mesh.AddBlendShapeFrame(blendShape.name, frame.weight, frame.deltaVertices, frame.deltaNormals, frame.deltaTangents);
            }
        }

        return mesh;
    }

    private static float Lerp(float from, float to, float value)
    {
        return from + (to - from) * Math.Max(0f, Math.Min(1f, value));
    }

    private sealed class NativeMeshPayloadPreparationStatus
    {
        private readonly object gate = new object();
        private float progress;
        private string label = "Preparing advanced mesh payload...";

        public void Report(float progress, string label)
        {
            lock (gate)
            {
                this.progress = Math.Max(0f, Math.Min(1f, progress));
                if (!string.IsNullOrWhiteSpace(label))
                {
                    this.label = label;
                }
            }
        }

        public void Snapshot(out float progress, out string label)
        {
            lock (gate)
            {
                progress = this.progress;
                label = this.label;
            }
        }
    }

    private sealed class PreparedPayloadAssetData
    {
        public string assetName;
        public int payloadVersion;
        public string sourceFbxPath;
        public string sourceFbxHash;
        public string payloadHash;
        public readonly List<PreparedPayloadRendererData> renderers = new List<PreparedPayloadRendererData>();
        public readonly List<NativeMeshPayloadBone> bones = new List<NativeMeshPayloadBone>();
        public readonly List<NativeMeshPayloadAuthoringPoseBone> authoringPoseBones = new List<NativeMeshPayloadAuthoringPoseBone>();
    }

    private sealed class PreparedPayloadRendererData
    {
        public string avatarPath;
        public string fbxMeshPath;
        public string meshName;
        public string rendererName;
        public PreparedMeshData mesh;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
        public string rootBonePath;
        public List<string> bonePaths = new List<string>();
    }

    private sealed class PreparedMeshData
    {
        public string name;
        public IndexFormat indexFormat;
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector4[] tangents;
        public Color32[] colors;
        public List<Vector4>[] uvs;
        public BoneWeight[] boneWeights;
        public Matrix4x4[] bindposes;
        public readonly List<PreparedSubMeshData> subMeshes = new List<PreparedSubMeshData>();
        public Bounds bounds;
        public readonly List<PreparedBlendShapeData> blendShapes = new List<PreparedBlendShapeData>();
    }

    private sealed class PreparedSubMeshData
    {
        public MeshTopology topology;
        public int[] indices;
    }

    private sealed class PreparedBlendShapeData
    {
        public string name;
        public readonly List<PreparedBlendShapeFrameData> frames = new List<PreparedBlendShapeFrameData>();
    }

    private sealed class PreparedBlendShapeFrameData
    {
        public float weight;
        public Vector3[] deltaVertices;
        public Vector3[] deltaNormals;
        public Vector3[] deltaTangents;
    }

    private static void WriteStringList(BinaryWriter writer, List<string> values)
    {
        writer.Write(values?.Count ?? 0);
        foreach (string value in values ?? new List<string>())
        {
            writer.Write(value ?? "");
        }
    }

    private static List<string> ReadStringList(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(reader.ReadString());
        }

        return values;
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteVector4(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
        writer.Write(value.w);
    }

    private static Vector4 ReadVector4(BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
    {
        writer.Write(value.x);
        writer.Write(value.y);
        writer.Write(value.z);
        writer.Write(value.w);
    }

    private static Quaternion ReadQuaternion(BinaryReader reader)
    {
        return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteVector3Array(BinaryWriter writer, Vector3[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<Vector3>())
        {
            WriteVector3(writer, value);
        }
    }

    private static Vector3[] ReadVector3Array(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadVector3(reader);
        }

        return values;
    }

    private static long WriteSparseVector3Array(BinaryWriter writer, Vector3[] values)
    {
        int length = values?.Length ?? 0;
        writer.Write(length);
        if (length == 0)
        {
            writer.Write(0);
            return 8;
        }

        var nonZeroValues = new List<KeyValuePair<int, Vector3>>();
        for (int i = 0; i < length; i++)
        {
            Vector3 value = values[i];
            if (Mathf.Abs(value.x) <= SparseVectorEpsilon &&
                Mathf.Abs(value.y) <= SparseVectorEpsilon &&
                Mathf.Abs(value.z) <= SparseVectorEpsilon)
            {
                continue;
            }

            nonZeroValues.Add(new KeyValuePair<int, Vector3>(i, value));
        }

        writer.Write(nonZeroValues.Count);
        foreach (var item in nonZeroValues)
        {
            writer.Write(item.Key);
            WriteVector3(writer, item.Value);
        }

        return EstimateSparseVector3ArrayBytes(nonZeroValues.Count);
    }

    private static long EstimateSparseVector3ArrayBytes(int nonZeroCount)
    {
        return 8L + (long)Mathf.Max(0, nonZeroCount) * 16L;
    }

    private static long EstimateSparseVector3ArrayWorstCaseBytes(int vertexCount)
    {
        return EstimateSparseVector3ArrayBytes(vertexCount);
    }

    private static bool ShouldSerializeBlendShapeNormalTangents(Mesh mesh, string shapeName)
    {
        if (mesh == null || string.IsNullOrWhiteSpace(shapeName))
        {
            return false;
        }

        if (mesh.name?.IndexOf("DynamicNormals", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        string lower = shapeName.ToLowerInvariant();
        return lower.Contains("muscle") || lower.Contains("flex");
    }

    private static Vector3[] ReadSparseVector3Array(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        int nonZeroCount = reader.ReadInt32();
        var values = new Vector3[length];
        for (int i = 0; i < nonZeroCount; i++)
        {
            int index = reader.ReadInt32();
            Vector3 value = ReadVector3(reader);
            if (index >= 0 && index < length)
            {
                values[index] = value;
            }
        }

        return values;
    }

    private static void WriteVector4Array(BinaryWriter writer, Vector4[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<Vector4>())
        {
            WriteVector4(writer, value);
        }
    }

    private static Vector4[] ReadVector4Array(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadVector4(reader);
        }

        return values;
    }

    private static void WriteVector4List(BinaryWriter writer, List<Vector4> values)
    {
        writer.Write(values?.Count ?? 0);
        foreach (var value in values ?? new List<Vector4>())
        {
            WriteVector4(writer, value);
        }
    }

    private static List<Vector4> ReadVector4List(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new List<Vector4>(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(ReadVector4(reader));
        }

        return values;
    }

    private static void WriteColor32Array(BinaryWriter writer, Color32[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<Color32>())
        {
            writer.Write(value.r);
            writer.Write(value.g);
            writer.Write(value.b);
            writer.Write(value.a);
        }
    }

    private static Color32[] ReadColor32Array(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new Color32[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        }

        return values;
    }

    private static void WriteBoneWeightArray(BinaryWriter writer, BoneWeight[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var value in values ?? Array.Empty<BoneWeight>())
        {
            writer.Write(value.boneIndex0);
            writer.Write(value.boneIndex1);
            writer.Write(value.boneIndex2);
            writer.Write(value.boneIndex3);
            writer.Write(value.weight0);
            writer.Write(value.weight1);
            writer.Write(value.weight2);
            writer.Write(value.weight3);
        }
    }

    private static BoneWeight[] ReadBoneWeightArray(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new BoneWeight[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = new BoneWeight
            {
                boneIndex0 = reader.ReadInt32(),
                boneIndex1 = reader.ReadInt32(),
                boneIndex2 = reader.ReadInt32(),
                boneIndex3 = reader.ReadInt32(),
                weight0 = reader.ReadSingle(),
                weight1 = reader.ReadSingle(),
                weight2 = reader.ReadSingle(),
                weight3 = reader.ReadSingle()
            };
        }

        return values;
    }

    private static void WriteMatrixArray(BinaryWriter writer, Matrix4x4[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (var matrix in values ?? Array.Empty<Matrix4x4>())
        {
            for (int i = 0; i < 16; i++)
            {
                writer.Write(matrix[i]);
            }
        }
    }

    private static Matrix4x4[] ReadMatrixArray(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            var matrix = new Matrix4x4();
            for (int j = 0; j < 16; j++)
            {
                matrix[j] = reader.ReadSingle();
            }
            values[i] = matrix;
        }

        return values;
    }

    private static void WriteIntArray(BinaryWriter writer, int[] values)
    {
        writer.Write(values?.Length ?? 0);
        foreach (int value in values ?? Array.Empty<int>())
        {
            writer.Write(value);
        }
    }

    private static int[] ReadIntArray(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadInt32();
        }

        return values;
    }

    private static void WriteBounds(BinaryWriter writer, Bounds bounds)
    {
        WriteVector3(writer, bounds.center);
        WriteVector3(writer, bounds.size);
    }

    private static Bounds ReadBounds(BinaryReader reader)
    {
        return new Bounds(ReadVector3(reader), ReadVector3(reader));
    }

    private sealed class PayloadRendererSource
    {
        public ModelFileSmrPathData entry;
        public SkinnedMeshRenderer renderer;
    }

    private sealed class NativeMeshPayloadBuildMetrics
    {
        public long blendShapeVertexBytes;
        public long blendShapeNormalBytes;
        public long blendShapeTangentBytes;
        public long skippedBlendShapeNormalBytes;
        public long skippedBlendShapeTangentBytes;
    }

    private static SkinnedMeshRenderer ResolveCustomRenderer(Transform customRoot, ModelFileSmrPathData entry)
    {
        if (customRoot == null || entry == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(entry.fbxMeshPath))
        {
            var transform = FindTransformByRelativePath(customRoot, entry.fbxMeshPath);
            var renderer = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null)
            {
                return renderer;
            }
        }

        var renderers = customRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (!string.IsNullOrWhiteSpace(entry.meshName))
        {
            var byMesh = renderers.FirstOrDefault(renderer =>
                renderer != null &&
                renderer.sharedMesh != null &&
                string.Equals(renderer.sharedMesh.name, entry.meshName, StringComparison.Ordinal));
            if (byMesh != null)
            {
                return byMesh;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.rendererName))
        {
            return renderers.FirstOrDefault(renderer =>
                renderer != null &&
                string.Equals(renderer.transform.name, entry.rendererName, StringComparison.Ordinal));
        }

        return null;
    }

    private static void ApplyPayloadToAvatar(Transform avatarRoot, NativeMeshPayloadAsset payload)
    {
        if (avatarRoot == null || payload == null)
        {
            return;
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        int rendererIndex = 0;
        int rendererTotal = payload.renderers?.Count ?? 0;
        foreach (var record in payload.renderers ?? new List<NativeMeshPayloadRenderer>())
        {
            rendererIndex++;
            var rendererStep = System.Diagnostics.Stopwatch.StartNew();
            if (record == null || record.mesh == null)
            {
                continue;
            }

            var targetRenderer = ResolveAvatarRenderer(avatarRoot, record);
            if (targetRenderer == null)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not find target renderer for '{record.rendererName}' / '{record.avatarPath}'.");
                continue;
            }

            ApplyPayloadBoneTransformsForRenderer(avatarRoot, targetRenderer, payload, record);

            Undo.RecordObject(targetRenderer.transform, "Apply Native Mesh Renderer Transform");
            targetRenderer.transform.localPosition = record.localPosition;
            targetRenderer.transform.localRotation = record.localRotation;
            targetRenderer.transform.localScale = record.localScale;
            EditorUtility.SetDirty(targetRenderer.transform);

            Undo.RecordObject(targetRenderer, "Apply Native Mesh Payload");

            var bonePaths = record.bonePaths ?? new List<string>();
            var resolvedBones = ResolveBoneArray(avatarRoot, bonePaths, targetRenderer);
            if (record.mesh.bindposes != null && record.mesh.bindposes.Length > 0 && record.mesh.bindposes.Length != resolvedBones.Length)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Renderer '{targetRenderer.name}' resolved {resolvedBones.Length} bones for {record.mesh.bindposes.Length} bindposes. The mesh may deform incorrectly.");
            }
            if (resolvedBones.Length == bonePaths.Count && resolvedBones.Length > 0)
            {
                targetRenderer.bones = resolvedBones;
            }
            else if (bonePaths.Count > 0)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve all bones for renderer '{targetRenderer.name}'. Resolved {resolvedBones.Length}/{bonePaths.Count}; the mesh may deform incorrectly.");
            }

            var rootBone = ResolveAvatarTransform(avatarRoot, record.rootBonePath);
            if (rootBone == null && !string.IsNullOrWhiteSpace(record.rootBonePath))
            {
                rootBone = ResolveBoneByName(targetRenderer, GetLastPathSegment(record.rootBonePath));
            }
            if (rootBone != null)
            {
                targetRenderer.rootBone = rootBone;
            }

            targetRenderer.sharedMesh = record.mesh;
            RefreshSkinnedRenderer(targetRenderer, record.mesh);
            EditorUtility.SetDirty(targetRenderer);
            UnityEngine.Debug.Log(
                $"[NativeMeshPayloadProfile] Applied renderer {rendererIndex}/{rendererTotal} '{targetRenderer.name}' " +
                $"mesh='{record.mesh.name}' bones={(record.bonePaths?.Count ?? 0)} verts={record.mesh.vertexCount}: " +
                $"step={rendererStep.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms");
            step.Restart();
        }
        LogApplyProfile("Finished renderer mesh assignment", step, total, $"renderers={rendererTotal}");
    }

    public static void ApplyPayloadAuthoringPose(Transform avatarRoot, NativeMeshPayloadAsset payload)
    {
        if (avatarRoot == null || payload == null)
        {
            return;
        }

        if (payload.authoringPoseBones == null || payload.authoringPoseBones.Count == 0)
        {
            throw new InvalidDataException("Native mesh payload is missing required authoring pose data.");
        }

        var payloadBonesByPath = (payload.bones ?? new List<NativeMeshPayloadBone>())
            .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.path))
            .GroupBy(bone => bone.path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        ApplyPayloadStructuralBoneTransforms(avatarRoot, payload);
        Transform sourceRoot = ResolveSourcePoseRoot(payload.sourceFbxPath);
        if (sourceRoot == null)
        {
            MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve source pose root '{payload.sourceFbxPath}'. Falling back to structural native mesh pose as the authoring base.");
        }

        int applied = 0;
        foreach (var poseBone in payload.authoringPoseBones)
        {
            if (poseBone == null || string.IsNullOrWhiteSpace(poseBone.path))
            {
                continue;
            }

            var target = ResolveAvatarTransform(avatarRoot, poseBone.path);
            if (target == null)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve authoring pose transform '{poseBone.path}'.");
                continue;
            }

            var source = sourceRoot != null ? FindTransformByRelativePath(sourceRoot, poseBone.path) : null;
            Vector3 basePosition;
            Quaternion baseRotation;
            Vector3 baseScale;
            if (source != null)
            {
                basePosition = source.localPosition;
                baseRotation = source.localRotation;
                baseScale = source.localScale;
            }
            else if (payloadBonesByPath.TryGetValue(poseBone.path, out var payloadBone))
            {
                basePosition = payloadBone.localPosition;
                baseRotation = payloadBone.localRotation;
                baseScale = payloadBone.localScale;
            }
            else
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve authoring base transform '{poseBone.path}'.");
                continue;
            }

            Undo.RecordObject(target, "Apply Native Mesh Authoring Pose");
            target.localPosition = basePosition + poseBone.localPositionOffset;
            target.localRotation = baseRotation * poseBone.localRotationDelta;
            target.localScale = Vector3.Scale(baseScale, poseBone.localScaleMultiplier);
            EditorUtility.SetDirty(target);
            applied++;
        }

        RefreshAvatarSkinnedRenderers(avatarRoot);
        MCBLogger.Log($"[NativeMeshPayload] Applied authoring pose deltas to {applied}/{payload.authoringPoseBones.Count} transforms.");
    }

    private static void ApplyPayloadStructuralBoneTransforms(Transform avatarRoot, NativeMeshPayloadAsset payload)
    {
        if (avatarRoot == null || payload?.bones == null)
        {
            return;
        }

        foreach (var payloadBone in payload.bones)
        {
            if (payloadBone == null || string.IsNullOrWhiteSpace(payloadBone.path))
            {
                continue;
            }

            var target = ResolveAvatarTransform(avatarRoot, payloadBone.path);
            if (target == null)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve structural bone transform '{payloadBone.path}'.");
                continue;
            }

            Undo.RecordObject(target, "Apply Native Mesh Structural Bone Pose");
            target.localPosition = payloadBone.localPosition;
            target.localRotation = payloadBone.localRotation;
            target.localScale = payloadBone.localScale;
            EditorUtility.SetDirty(target);
        }
    }

    private static void RefreshSkinnedRenderer(SkinnedMeshRenderer renderer, Mesh mesh)
    {
        if (renderer == null)
        {
            return;
        }

        bool wasEnabled = renderer.enabled;
        bool previousUpdateWhenOffscreen = renderer.updateWhenOffscreen;

        renderer.updateWhenOffscreen = true;
        renderer.sharedMesh = null;
        renderer.sharedMesh = mesh;

        if (wasEnabled)
        {
            renderer.enabled = false;
            renderer.enabled = true;
        }

        renderer.updateWhenOffscreen = previousUpdateWhenOffscreen;
    }

    private static void RefreshAvatarSkinnedRenderers(Transform avatarRoot)
    {
        if (avatarRoot == null)
        {
            return;
        }

        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer != null)
            {
                RefreshSkinnedRenderer(renderer, renderer.sharedMesh);
            }
        }
    }

    private static void ApplyPayloadBoneTransformsForRenderer(
        Transform avatarRoot,
        SkinnedMeshRenderer targetRenderer,
        NativeMeshPayloadAsset payload,
        NativeMeshPayloadRenderer record)
    {
        if (avatarRoot == null || targetRenderer == null || payload == null || record == null)
        {
            return;
        }

        var payloadBonesByPath = (payload.bones ?? new List<NativeMeshPayloadBone>())
            .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.path))
            .GroupBy(bone => bone.path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (string path in CollectPayloadBonePaths(new[] { record }))
        {
            if (!payloadBonesByPath.TryGetValue(path, out var payloadBone))
            {
                continue;
            }

            var target = ResolveAvatarTransform(avatarRoot, path);
            if (target == null)
            {
                target = ResolveBoneByName(targetRenderer, GetLastPathSegment(path));
            }

            if (target == null)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve bone transform '{path}' for renderer '{targetRenderer.name}'.");
                continue;
            }

            Undo.RecordObject(target, "Apply Native Mesh Bone Pose");
            target.localPosition = payloadBone.localPosition;
            target.localRotation = payloadBone.localRotation;
            target.localScale = payloadBone.localScale;
            EditorUtility.SetDirty(target);
        }
    }

    private static HashSet<string> CollectPayloadBonePaths(IEnumerable<NativeMeshPayloadRenderer> renderers)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var renderer in renderers ?? Enumerable.Empty<NativeMeshPayloadRenderer>())
        {
            if (renderer == null)
            {
                continue;
            }

            AddPathAndAncestors(paths, renderer.rootBonePath);
            foreach (string bonePath in renderer.bonePaths ?? new List<string>())
            {
                AddPathAndAncestors(paths, bonePath);
            }
        }

        return paths;
    }

    private static void AddPathAndAncestors(HashSet<string> paths, string path)
    {
        if (paths == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string[] parts = path.Split('/');
        string current = "";
        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim();
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";
            paths.Add(current);
        }
    }

    private static Transform[] ResolveBoneArray(Transform avatarRoot, List<string> bonePaths, SkinnedMeshRenderer targetRenderer)
    {
        if (avatarRoot == null || bonePaths == null || bonePaths.Count == 0)
        {
            return Array.Empty<Transform>();
        }

        var result = new List<Transform>(bonePaths.Count);
        for (int i = 0; i < bonePaths.Count; i++)
        {
            string path = bonePaths[i];
            var bone = ResolveAvatarTransform(avatarRoot, path);
            if (bone == null)
            {
                bone = ResolveBoneByName(targetRenderer, GetLastPathSegment(path));
            }
            if (bone == null &&
                targetRenderer != null &&
                targetRenderer.bones != null &&
                i < targetRenderer.bones.Length)
            {
                bone = targetRenderer.bones[i];
            }
            if (bone == null)
            {
                return Array.Empty<Transform>();
            }

            result.Add(bone);
        }

        return result.ToArray();
    }

    private static Transform ResolveBoneByName(SkinnedMeshRenderer renderer, string boneName)
    {
        if (renderer == null || string.IsNullOrWhiteSpace(boneName))
        {
            return null;
        }

        return (renderer.bones ?? Array.Empty<Transform>())
            .FirstOrDefault(bone => bone != null && string.Equals(bone.name, boneName, StringComparison.Ordinal));
    }

    private static SkinnedMeshRenderer ResolveAvatarRenderer(Transform avatarRoot, NativeMeshPayloadRenderer record)
    {
        if (avatarRoot == null || record == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(record.avatarPath))
        {
            var transform = FindTransformByRelativePath(avatarRoot, record.avatarPath);
            var renderer = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null)
            {
                return renderer;
            }
        }

        var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (!string.IsNullOrWhiteSpace(record.rendererName))
        {
            var byRendererName = renderers.FirstOrDefault(renderer =>
                renderer != null &&
                string.Equals(renderer.transform.name, record.rendererName, StringComparison.Ordinal));
            if (byRendererName != null)
            {
                return byRendererName;
            }
        }

        if (!string.IsNullOrWhiteSpace(record.meshName))
        {
            return renderers.FirstOrDefault(renderer =>
                renderer != null &&
                renderer.sharedMesh != null &&
                string.Equals(renderer.sharedMesh.name, record.meshName, StringComparison.Ordinal));
        }

        return null;
    }

    private static void ApplySourceBoneTransforms(Transform avatarRoot, string sourceFbxPath)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(sourceFbxPath));
        if (avatarRoot == null || source == null)
        {
            return;
        }

        Transform sourceRoot = source.transform;
        foreach (var sourceTransform in source.GetComponentsInChildren<Transform>(true))
        {
            if (sourceTransform == null || sourceTransform == sourceRoot)
            {
                continue;
            }

            string path = GetRelativeTransformPath(sourceRoot, sourceTransform);
            var target = ResolveAvatarTransform(avatarRoot, path);
            if (target == null)
            {
                continue;
            }

            Undo.RecordObject(target, "Restore Native Mesh Bone Pose");
            target.localPosition = sourceTransform.localPosition;
            target.localRotation = sourceTransform.localRotation;
            target.localScale = sourceTransform.localScale;
            EditorUtility.SetDirty(target);
        }
    }

    private static IEnumerable<ModelFileData> GetSourceFilesForPatch(CustomBaseVersion version, ModelFileData patch)
    {
        if (version?.sourceFiles == null || patch == null)
        {
            yield break;
        }

        if (patch.sourceModelFileId.HasValue)
        {
            var source = version.sourceFiles.FirstOrDefault(file => file != null && file.id == patch.sourceModelFileId.Value);
            if (source != null)
            {
                yield return source;
                yield break;
            }
        }

        string sourcePath = null;
        if (patch.metadata != null &&
            patch.metadata.TryGetValue("sourcePath", out object sourcePathValue))
        {
            sourcePath = sourcePathValue?.ToString();
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var source = version.sourceFiles.FirstOrDefault(file =>
                file != null &&
                string.Equals(MCBUtils.ToUnityPath(file.path), MCBUtils.ToUnityPath(sourcePath), StringComparison.OrdinalIgnoreCase));
            if (source != null)
            {
                yield return source;
                yield break;
            }
        }
    }

    private static IEnumerable<ModelFileData> GetAdvancedPatchFiles(CustomBaseVersion version)
    {
        return version?.versionFiles?
            .Where(file => file != null &&
                           string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                           IsAdvancedMeshPatchTransform(file.transform)) ??
               Enumerable.Empty<ModelFileData>();
    }

    private static IEnumerable<SkinnedMeshRenderer> ResolveRenderersForSource(Transform avatarRoot, ModelFileData source)
    {
        if (avatarRoot == null || source == null)
        {
            yield break;
        }

        foreach (var entry in source.smrPaths ?? new List<ModelFileSmrPathData>())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.avatarPath))
            {
                continue;
            }

            var transform = FindTransformByRelativePath(avatarRoot, entry.avatarPath);
            var renderer = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null)
            {
                yield return renderer;
            }
        }
    }

    private static Transform ResolveAvatarTransform(Transform avatarRoot, string path)
    {
        if (avatarRoot == null)
        {
            return null;
        }

        var byPath = FindTransformByRelativePath(avatarRoot, path);
        if (byPath != null)
        {
            return byPath;
        }

        string name = GetLastPathSegment(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var matches = avatarRoot
            .GetComponentsInChildren<Transform>(true)
            .Where(transform => transform != null && string.Equals(transform.name, name, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static Transform FindTransformByRelativePath(Transform root, string relativePath)
    {
        if (root == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        Transform current = root;
        foreach (string rawPart in relativePath.Split('/'))
        {
            string part = rawPart.Trim();
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            current = current.Find(part);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static string GetRelativeTransformPath(Transform root, Transform target)
    {
        if (root == null || target == null || target == root)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        Transform current = target;
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string GetGeneratedPayloadPath(CustomBaseVersion version, ModelFileData patchFile, string payloadHash)
    {
        string sourceName = "nativeMesh";
        if (patchFile?.metadata != null &&
            patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue) &&
            sourcePathValue != null)
        {
            sourceName = Path.GetFileNameWithoutExtension(sourcePathValue.ToString());
        }
        else if (!string.IsNullOrWhiteSpace(patchFile?.path))
        {
            sourceName = Path.GetFileNameWithoutExtension(patchFile.path);
        }

        string folder = MCBUtils.CombineUnityPath(
            GeneratedFolder,
            version.assetId.ToString(),
            SanitizeFileName(version.version));
        string file = $"{SanitizeFileName(sourceName)}_{payloadHash}.asset";
        return MCBUtils.CombineUnityPath(folder, file);
    }

    private static void DeleteAssetFile(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        if (AssetDatabase.LoadMainAssetAtPath(normalizedPath) != null)
        {
            AssetDatabase.DeleteAsset(normalizedPath);
            return;
        }

        string fullPath = Path.GetFullPath(normalizedPath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        if (File.Exists(fullPath + ".meta")) File.Delete(fullPath + ".meta");
    }

    private static NativeMeshPayloadAsset LoadPayloadAsset(string assetPath)
    {
        string normalizedPath = MCBUtils.ToUnityPath(assetPath);
        return AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(normalizedPath);
    }

    private static bool CachedPayloadMatches(NativeMeshPayloadAsset payload, string payloadHash, string payloadCompression)
    {
        if (payload == null ||
            payload.payloadVersion != PayloadVersion ||
            !string.Equals(payload.payloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(
                NormalizePayloadCompression(payload.payloadCompression),
                NormalizePayloadCompression(payloadCompression),
                StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void EnsureAssetFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(parent))
        {
            EnsureAssetFolder(parent);
        }

        string parentFolder = string.IsNullOrWhiteSpace(parent) ? "Assets" : parent;
        string folderName = Path.GetFileName(folderPath);
        if (!AssetDatabase.IsValidFolder(folderPath))
        {
            AssetDatabase.CreateFolder(parentFolder, folderName);
        }
    }

    private static string BytesToHex(byte[] hash)
    {
        var builder = new StringBuilder();
        foreach (byte b in hash ?? Array.Empty<byte>())
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
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

    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly HashAlgorithm hashAlgorithm;
        private readonly bool leaveOpen;
        private bool hashCompleted;

        public HashingWriteStream(Stream inner, HashAlgorithm hashAlgorithm, bool leaveOpen)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.hashAlgorithm = hashAlgorithm ?? throw new ArgumentNullException(nameof(hashAlgorithm));
            this.leaveOpen = leaveOpen;
        }

        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (hashCompleted)
            {
                throw new InvalidOperationException("Cannot write after hash completion.");
            }

            if (count <= 0)
            {
                return;
            }

            hashAlgorithm.TransformBlock(buffer, offset, count, null, 0);
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public void CompleteHash()
        {
            if (hashCompleted)
            {
                return;
            }

            inner.Flush();
            hashAlgorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            hashCompleted = true;
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CompleteHash();
                if (!leaveOpen)
                {
                    inner.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class XorWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly byte[] key;
        private readonly bool leaveOpen;

        public XorWriteStream(Stream inner, byte[] key, bool leaveOpen)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.key = key ?? throw new ArgumentNullException(nameof(key));
            this.leaveOpen = leaveOpen;
            if (key.Length == 0)
            {
                throw new ArgumentException("XOR key cannot be empty.", nameof(key));
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            byte[] encrypted = new byte[count];
            for (int i = 0; i < count; i++)
            {
                int keyIndex = (int)((Position + i) % key.Length);
                encrypted[i] = (byte)(buffer[offset + i] ^ key[keyIndex]);
            }

            inner.Write(encrypted, 0, encrypted.Length);
            Position += count;
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
#endif
