#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCBEditorUtils;

public class VersionActions
{
    private const float BlendshapeWeightEpsilon = 0.001f;
    private const string AdvancedMeshDeliveryMode = "UNITY_NATIVE_MESH_ASSET";
    private const string FbxReplacementDeliveryMode = "FBX_REPLACEMENT";
    private const string UpdateStateUndoName = "Update MCB State";
    private const double ApplyProgressIntroSeconds = 0.08d;
    private const double ApplyProgressCompletionHoldSeconds = 0.6d;
    private const float DownloadApplyProgressStart = 0.02f;
    private const float DownloadApplyProgressComplete = 0.66f;
    private const float DownloadApplySyntheticProgressPerSecond = 0.10f;
    private const float DownloadApplySyntheticProgressMax = DownloadApplyProgressComplete - 0.04f;
    private const float DownloadApplyExtractionComplete = 0.68f;
    private const float DownloadApplyImportComplete = 0.70f;
    private const long InMemoryVersionPackageHardCapBytes = 10L * 1024L * 1024L * 1024L;
    private const long InMemoryVersionPackageEstimatedMultiplier = 5L;

    private sealed class ApplyTimingProfile
    {
        private readonly bool enabled;
        private readonly string operation;
        private readonly System.Diagnostics.Stopwatch total = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Diagnostics.Stopwatch step = System.Diagnostics.Stopwatch.StartNew();

        public ApplyTimingProfile(CustomBaseVersion version, bool isReset, bool usesAdvancedMesh)
        {
            enabled = usesAdvancedMesh;
            operation = $"{(isReset ? "reset" : "apply")} version={(version != null ? version.version : "null")}";

            if (enabled)
            {
                MCBLogger.Log($"[VersionApplyProfile] START {operation}");
            }
        }

        public void Mark(string label)
        {
            if (!enabled)
            {
                return;
            }

            MCBLogger.Log($"[VersionApplyProfile] {label}: step={step.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms");
            step.Restart();
        }

        public void Done(string label = "DONE")
        {
            if (!enabled)
            {
                return;
            }

            MCBLogger.Log($"[VersionApplyProfile] {label} {operation}: total={total.Elapsed.TotalMilliseconds:F1} ms");
        }
    }

    private readonly MCBEditor editor;
    private readonly NetworkService networkService;
    private readonly FileManagerService fileManagerService;
    private VersionTransitionRollbackSnapshot activeTransitionRollback;
    private static int fbxHashRecalculationGeneration;
    private int applyProgressGeneration;
    private float applyProgressBase;
    private float applyProgressScale = 1f;
    private readonly Dictionary<string, NativeMeshPayloadService.NativeMeshPayloadPreparationPreload> advancedMeshPreparationPreloads =
        new Dictionary<string, NativeMeshPayloadService.NativeMeshPayloadPreparationPreload>(StringComparer.OrdinalIgnoreCase);
    public VersionApplyProgressState ApplyProgress { get; } = new VersionApplyProgressState();
    /// <summary>How far the running version download is (0 to 1).</summary>
    public float DownloadProgress { get; private set; }

    public VersionActions(MCBEditor editor, NetworkService network, FileManagerService files)
    {
        this.editor = editor;
        this.networkService = network;
        this.fileManagerService = files;
    }
    
    // Coroutine Starters
    public void StartVersionFetch() => EditorCoroutineUtility.StartCoroutineOwnerless(FetchVersionsCoroutine());
    public void StartVersionDownload(CustomBaseVersion ver, bool apply)
    {
        if (apply && RefitBlocksSwitch()) return;
        if (apply && !editor.isDownloading)
        {
            ResetApplyProgressRange();
            StartApplyProgress("Starting download...", 0f);
        }

        EditorCoroutineUtility.StartCoroutineOwnerless(DownloadVersionCoroutine(ver, apply));
    }
    public void StartVersionDelete(CustomBaseVersion ver) => EditorCoroutineUtility.StartCoroutineOwnerless(DeleteVersionCoroutine(ver));
    public void StartApplyVersion() => StartApplyOrResetWithIntro(editor.selectedVersionForAction, false, "Starting version switch...");
    public void StartReset() => StartApplyOrResetWithIntro(null, true, "Starting reset...");
    public void StartRecalculateCurrentFbxHash()
    {
        int hashGeneration = ++fbxHashRecalculationGeneration;
        EditorCoroutineUtility.StartCoroutineOwnerless(RecalculateCurrentFbxHashCoroutine(hashGeneration, null));
    }
    public void StartApplyCustomVersion()
    {
        if (editor.isApplying || ApplyProgress.IsRunning || RefitBlocksSwitch()) return;
        EditorCoroutineUtility.StartCoroutineOwnerless(ApplyCustomVersionCoroutine(editor.selectedCustomVersionForAction));
    }
    public void ConfigureApplyProgressColor(Color color) => ApplyProgress.SetFillColor(color);

    public bool CanResetToDefaultBaseWithoutFbxImport()
    {
        if (editor?.customBaseTarget == null || fileManagerService == null)
        {
            return false;
        }

        var versionForAssets = ResolvePersistedAppliedVersion() ?? editor.selectedVersionForAction;
        if (!NativeMeshPayloadService.VersionUsesAdvancedMesh(versionForAssets))
        {
            return false;
        }

        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrWhiteSpace(fbxPath))
        {
            return false;
        }

        var pathsToRestore = GetResetAffectedFbxPaths(versionForAssets, fbxPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && fileManagerService.BackupExists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return pathsToRestore.Count > 0 && pathsToRestore.All(fileManagerService.FbxMatchesBackupAtPath);
    }

    public void ExportOfflineVersion(CustomBaseVersion version)
    {
        if (version == null) return;

        string versionFolderPath = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrWhiteSpace(versionFolderPath) || !Directory.Exists(Path.GetFullPath(versionFolderPath)))
        {
            editor.warningsModule.AddWarning("The selected version is not available locally, so it cannot be exported.", MessageType.Error, "Export failed");
            editor.Repaint();
            return;
        }

        string suggestedFileName = $"MCB_saved_version_{version.version.Replace('.', '_')}.unitypackage";
        string savePath = EditorUtility.SaveFilePanel(
            "Export Saved Version",
            "",
            suggestedFileName,
            "unitypackage");

        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        try
        {
            EditorUtility.DisplayProgressBar("Exporting Saved Version", $"Building offline package for {version.version}...", 0.5f);
            fileManagerService.ExportOfflineVersionPackage(version, savePath);
            EditorUtility.DisplayDialog("Export Complete", $"Saved version {version.version} has been exported to:\n{savePath}", "OK");
        }
        catch (Exception ex)
        {
            editor.warningsModule.AddWarning(ex.Message, MessageType.Error, "Export failed");
            MCBLogger.LogError($"[VersionActions] Offline export failed: {ex}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            editor.LoadImportedVersions(true);
            editor.Repaint();
        }
    }

    private IEnumerator FetchVersionsCoroutine()
    {
        if (!editor.HasServerAccess) yield break;
        if (editor.isFetching) yield break;
        editor.isFetching = true;
        editor.warningsModule.Clear();
        editor.accessDeniedAssetId = null;
        editor.fetchAttempted = true;
        editor.Repaint();

        UpdateCurrentBaseFbxHash();

        if (string.IsNullOrEmpty(editor.currentBaseFbxHash))
        {
            MCBLogger.LogWarning("[VersionActions] Version fetch aborted because currentBaseFbxHash is empty.");
            editor.serverVersions = new System.Collections.Generic.List<CustomBaseVersion>();
            UpdateAppliedVersionAndState(); // This will clear the applied state
            editor.isFetching = false;
            editor.Repaint();
            yield break;
        }

        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null)
        {
            MCBLogger.Log("[VersionActions] Version fetch skipped because no asset is selected in the gallery.");
            editor.serverVersions = new System.Collections.Generic.List<CustomBaseVersion>();
            editor.recommendedVersion = null;
            editor.isFetching = false;
            editor.Repaint();
            yield break;
        }

        string currentFbxPath = GetCurrentFBXPath();
        int requestedAssetId = selectedAsset.id;
        string requestedToken = editor.authToken;
        string requestedBaseHash = editor.currentBaseFbxHash;
        string requestedSourceVersionKey = OriginalBaseLibrary.ActiveKey(selectedAsset);
        string url = $"{MCBUtils.getApiUrl()}{MCBUtils.GetAssetVersionEndpoint(selectedAsset.id)}?d={editor.currentBaseFbxHash}&sourceKey={OriginalBaseLibrary.ActiveKey(selectedAsset)}";
        MCBLogger.Log($"[VersionActions] Starting version fetch. assetId={selectedAsset.id} | url={url} | currentFbxPath={currentFbxPath} | currentBaseFbxHash={editor.currentBaseFbxHash}");
        var fetchTask = networkService.FetchVersionsAsync(url, requestedToken);
        
        while (!fetchTask.IsCompleted)
        {
            yield return null;
        }
        
        var (success, response, error) = fetchTask.Result;
        if (editor.GetSelectedAsset()?.id != requestedAssetId || editor.authToken != requestedToken ||
            editor.currentBaseFbxHash != requestedBaseHash || OriginalBaseLibrary.ActiveKey(editor.GetSelectedAsset()) != requestedSourceVersionKey)
        {
            editor.isFetching = false;
            yield break;
        }
        if (success)
        {
            editor.serverVersions = response?.versions ?? new System.Collections.Generic.List<CustomBaseVersion>();
            editor.recommendedVersion = editor.serverVersions.FirstOrDefault(v => v.version == response?.recommendedVersion);
            PersistentCache.Instance.CacheVersions(requestedBaseHash, editor.serverVersions,
                editor.recommendedVersion, requestedToken, requestedAssetId, requestedSourceVersionKey);
            UpdateAppliedVersionAndState();
            SmartSelectVersion();
        }
        else
        {
            // No access (403 ACCESS_DENIED): the version list shows the store link instead of an error, and nothing cached.
            if (!string.IsNullOrEmpty(error) && error.StartsWith(NetworkService.AccessDeniedPrefix, StringComparison.Ordinal))
            {
                editor.accessDeniedAssetId = error.Substring(NetworkService.AccessDeniedPrefix.Length);
                editor.warningsModule.Clear(); // Do not show generic error box
                editor.serverVersions = new System.Collections.Generic.List<CustomBaseVersion>();
                editor.recommendedVersion = null;
                PersistentCache.Instance.RemoveCachedVersions(requestedBaseHash, requestedToken, requestedAssetId, requestedSourceVersionKey);
            }
            else
            {
                MCBLogger.LogError($"[VersionActions] Version fetch failed. currentFbxPath={currentFbxPath} | currentBaseFbxHash={editor.currentBaseFbxHash} | error={error}");
                editor.warningsModule.AddWarning(error, MessageType.Error, "Fetch failed");
                // A transient backend failure must not discard the last known version metadata
                // or recalculate applied state from an unchanged FBX hash. Local imported data and
                // the persisted applied marker remain authoritative while offline.
            }
        }
        
        editor.isFetching = false;
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }
    
    private IEnumerator DownloadVersionCoroutine(CustomBaseVersion version, bool applyAfter)
    {
        if (version.isUnsubmitted || version.localArtifactSourceVersionKey != null)
        {
            if (MCBUtils.IsVersionDownloaded(version))
            {
                if (applyAfter) yield return ApplyOrResetCoroutine(version, false);
            }
            else editor.warningsModule.AddWarning("This local build is incomplete. Rebuild it in the creator form before previewing or applying it.", MessageType.Error, "Local version unavailable");
            editor.RefreshUiToolkitSections();
            yield break;
        }
        MCBPerformance.PauseForeground();
        if (editor.isDownloading) yield break;
        editor.isDownloading = true;
        DownloadProgress = 0f;
        if (applyAfter)
        {
            BeginApplyProgress("Downloading version...", 0.02f);
        }
        editor.warningsModule.Clear();
        editor.Repaint();

        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null)
        {
            MCBLogger.LogWarning("[VersionActions] Download aborted because no asset is selected in the gallery.");
            editor.isDownloading = false;
            if (applyAfter) FinishApplyProgress(false);
            editor.Repaint();
            yield break;
        }
        
        // Inspector initialization may first detect a garment before the selected
        // asset's source mapping is loaded. Never use that stale UI hash to download.
        string requestedSourcePath = GetCurrentFBXPath();
        string requestedToken = editor.authToken;
        var sourceHashesTask = AsyncHashService.Instance.CalculateFBXHashesAsync(requestedSourcePath);
        while (!sourceHashesTask.IsCompleted) yield return null;
        var sourceHashes = sourceHashesTask.Result;
        RefreshOriginalBackup(requestedSourcePath, version);
        string requestBaseHash = fileManagerService.BackupExists(requestedSourcePath)
            ? sourceHashes.originalHash : sourceHashes.currentHash;
        if (editor.GetSelectedAsset()?.id != selectedAsset.id || editor.authToken != requestedToken ||
            !string.Equals(requestedSourcePath, GetCurrentFBXPath(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(requestBaseHash))
        {
            editor.isDownloading = false;
            if (applyAfter) FinishApplyProgress(false);
            editor.warningsModule.AddWarning("The source model changed or could not be read. Select the version again to retry.", MessageType.Warning, "Download paused");
            editor.RefreshUiToolkitSections();
            yield break;
        }
        editor.currentBaseFbxHash = requestBaseHash;
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"mcb_dl_{Guid.NewGuid()}.zip");
        string url = $"{MCBUtils.getApiUrl()}{MCBUtils.GetAssetModelEndpoint(selectedAsset.id)}?version={version.version}&d={requestBaseHash}&sourceKey={version.sourceVersionKey}";
        var deliveryDecision = MCBPerformance.Choose(version.deliveryVariants);
        var delivery = version.deliveryVariants?.FirstOrDefault(v => v.codec == deliveryDecision.codec);
        if (delivery != null) url += "&codec=" + Uri.EscapeDataString(delivery.codec);
        bool advancedMeshApply = applyAfter && NativeMeshPayloadService.VersionUsesAdvancedMesh(version);
        advancedMeshPreparationPreloads.Clear();
        var originalFbxPreloadTasks = advancedMeshApply
            ? StartAdvancedMeshOriginalFbxPreloads(version)
            : new Dictionary<string, Task<byte[]>>(StringComparer.OrdinalIgnoreCase);

        bool useInMemoryPackage = false;
        string advancedMeshPipelineDecision = null;
        if (advancedMeshApply && version.meshDelivery != 1)
        {
            // The package size comes with the version's delivery variants. Without them the download goes to disk: probing
            // the model URL would redirect to signed storage (which refuses HEAD) and count as a download.
            if (delivery != null) useInMemoryPackage = CanUseInMemoryVersionPackage(delivery.packageBytes, out advancedMeshPipelineDecision);
            else advancedMeshPipelineDecision = "using disk because the version does not list its package size.";
            MCBLogger.Log($"[VersionActions] Advanced mesh RAM download decision: {advancedMeshPipelineDecision}");
        }
        
        // --- Setup phase (no yield returns) ---
        float downloadProgress = 0f;
        ulong downloadedBytes = 0;
        double downloadStartedAt = EditorApplication.timeSinceStartup;
        float visibleDownloadProgress = DownloadApplyProgressStart;
        Task<(bool success, string error)> diskDownloadTask = null;
        Task<(bool success, byte[] data, string error)> memoryDownloadTask = null;
        if (version.meshDelivery == 1)
        {
            useInMemoryPackage = false;
            diskDownloadTask = MCBMeshDelivery.DownloadAsync(networkService, url, version, tempZipPath,
                progress => downloadProgress = Mathf.Clamp01(progress), bytes => downloadedBytes = bytes, authToken: requestedToken);
        }
        else if (useInMemoryPackage)
        {
            MCBLogger.Log(
                "[VersionActions] Advanced mesh reconstruction preparation path: RAM. " +
                "The ZIP and advanced mesh .bin stay in memory through payload preparation; version files and the final Unity .asset are still persisted to disk. " +
                advancedMeshPipelineDecision);
            memoryDownloadTask = networkService.DownloadBytesAsync(url, progress =>
            {
                downloadProgress = Mathf.Clamp01(progress);
            }, bytes =>
            {
                downloadedBytes = bytes;
            }, requestedToken);
        }
        else
        {
            if (advancedMeshApply)
            {
                MCBLogger.Log(
                    "[VersionActions] Advanced mesh reconstruction preparation path: DISK fallback. " +
                    "The ZIP and extracted advanced mesh .bin are processed from disk. " +
                    (advancedMeshPipelineDecision ?? "No RAM-path decision was available."));
            }

            diskDownloadTask = networkService.DownloadFileAsync(url, tempZipPath, progress =>
            {
                downloadProgress = Mathf.Clamp01(progress);
            }, bytes =>
            {
                downloadedBytes = bytes;
            }, requestedToken);
        }
        bool setupSucceeded = memoryDownloadTask != null || diskDownloadTask != null;
        
        if (!setupSucceeded)
        {
            editor.warningsModule.AddWarning("Failed to start download task", MessageType.Error, "Download failed");
            if (applyAfter) FinishApplyProgress(false);
            editor.isDownloading = false;
            editor.Repaint();
            yield break;
        }
        
        // --- Download phase (with yield returns, NOT in try/catch) ---
        while ((memoryDownloadTask != null && !memoryDownloadTask.IsCompleted) ||
               (diskDownloadTask != null && !diskDownloadTask.IsCompleted))
        {
            DownloadProgress = downloadProgress;
            if (applyAfter)
            {
                visibleDownloadProgress = GetVisibleDownloadApplyProgress(downloadStartedAt, downloadProgress, visibleDownloadProgress);
                string stepText = "Preparing download...";
                if (downloadProgress > 0.001f)
                {
                    stepText = $"Downloading version... {Mathf.RoundToInt(downloadProgress * 100f)}%";
                }
                else if (downloadedBytes > 0)
                {
                    stepText = "Downloading version...";
                }
                ReportApplyProgress(visibleDownloadProgress, stepText);
            }
            yield return null;
        }
        
        // The speed sample covers the transfer only, not the checks and extraction below.
        double downloadMilliseconds = (EditorApplication.timeSinceStartup - downloadStartedAt) * 1000;

        // --- Process result and cleanup ---
        bool success;
        string error;
        byte[] downloadedZipBytes = null;
        try
        {
            if (memoryDownloadTask != null)
            {
                var result = memoryDownloadTask.Result;
                success = result.success;
                error = result.error;
                downloadedZipBytes = result.data;
            }
            else
            {
                (success, error) = diskDownloadTask.Result;
            }
        }
        catch (Exception ex)
        {
            success = false;
            error = $"Download failed: {ex.GetBaseException().Message}";
            MCBLogger.LogError($"[VersionActions] Download task failed unexpectedly: {ex}");
        }
        VersionContentTrust.CreatorTrustSnapshot currentCreatorTrust = null;
        List<string> downloadedCode = null;
        if (success)
        {
            try
            {
                using (var zip = downloadedZipBytes != null ? new MemoryStream(downloadedZipBytes, false) : (Stream)File.OpenRead(tempZipPath))
                    downloadedCode = VersionContentTrust.ListCode(zip);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                success = false;
                error = $"The downloaded version could not be read: {ex.Message}";
            }
        }
        // Only code needs the creator's current trust. One retry: a failed lookup would warn about a trusted creator.
        for (int attempt = 0; success && downloadedCode.Count > 0 && currentCreatorTrust == null && attempt < 2; attempt++)
        {
            if (applyAfter) ReportApplyProgress(DownloadApplyProgressComplete, "Checking the creator...");
            var trustRequest = networkService.FetchCreatorTrustAsync(MCBUtils.GetAssetModelTrustUrl(
                selectedAsset.id, version.version, requestBaseHash, version.sourceVersionKey), requestedToken);
            while (!trustRequest.IsCompleted) yield return null;
            if (!trustRequest.IsFaulted && !trustRequest.IsCanceled) currentCreatorTrust = trustRequest.Result;
        }
        if (success)
        {
            if (editor.authToken != requestedToken || AuthenticationService.GetAuth()?.token != requestedToken ||
                editor.GetSelectedAsset()?.id != selectedAsset.id)
            {
                success = false;
                error = "The signed-in account or selected asset changed during download. Select the version again to retry.";
            }
        }
        bool extractionSucceeded = false;
        if (version.meshDelivery != 1) MCBPerformance.RecordDownload(deliveryDecision, (long)downloadedBytes, downloadMilliseconds, success);
        string tempExtractPath = null;
        
        try
        {
            if (!success)
            {
                editor.warningsModule.AddWarning(error, MessageType.Error, "Download failed");
                if (applyAfter) FinishApplyProgress(false);
            }
            else if (!VersionContentTrust.ConfirmDownloadedCode(version, selectedAsset, downloadedCode, currentCreatorTrust))
            {
                downloadedZipBytes = null;
                editor.warningsModule.AddWarning(
                    $"Version {version.version} was not installed: it contains code and you chose not to import it. Nothing was added to your project.",
                    MessageType.Info, "Download cancelled");
                if (applyAfter) FinishApplyProgress(false);
            }
            else
            {
                if (applyAfter)
                {
                    ReportApplyProgress(DownloadApplyProgressComplete, "Download complete...");
                    ReportApplyProgress(DownloadApplyExtractionComplete, "Extracting version files...");
                }
                string finalDest = MCBUtils.GetVersionDataPath(version);
                void ValidateDownload(string staging)
                {
                    MCBVersionDelivery.ApplyLocalDelivery(version, staging);
                    foreach (var file in version.versionFiles ?? Array.Empty<ModelFileData>())
                    {
                        if (file == null) throw new InvalidDataException("Missing version file metadata.");
                        string path = VersionStorage.ContainedPath(staging, file.path);
                        if (!File.Exists(path)) throw new InvalidDataException("Downloaded version is missing a required file.");
                        if (!string.IsNullOrWhiteSpace(file.hash) &&
                            !string.Equals(fileManagerService.CalculateFileHash(path), file.hash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Downloaded version failed file integrity verification.");
                    }
                    VersionRepository.SaveVersionJson(staging, version);
                    var manifest = VersionRepository.CreateManifestFromFolder(staging, version, false, null, null);
                    manifest.Save(staging);
                    if (!VersionStorage.IsComplete(staging, version)) throw new InvalidDataException("Downloaded version is incomplete.");
                }
                Dictionary<string, byte[]> inMemoryPatchBytes = null;
                if (downloadedZipBytes != null)
                {
                    inMemoryPatchBytes = fileManagerService.UnzipAndMoveFromMemory(
                        downloadedZipBytes,
                        finalDest,
                        GetAdvancedMeshPatchFileNames(version), ValidateDownload);
                    downloadedZipBytes = null;
                }
                else
                {
                    tempExtractPath = Path.Combine(Path.GetTempPath(), $"mcb_extract_{Guid.NewGuid()}");
                    fileManagerService.UnzipAndMove(tempZipPath, tempExtractPath, finalDest, ValidateDownload);
                }

                editor.LoadImportedVersions(true);
                extractionSucceeded = true;
                if (advancedMeshApply)
                {
                    StartAdvancedMeshPayloadPreparations(version, inMemoryPatchBytes, originalFbxPreloadTasks);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
        }
        catch (Exception e) 
        { 
            editor.warningsModule.AddWarning($"Extraction failed: {e.Message}", MessageType.Error, "Extraction failed"); 
            if (applyAfter) FinishApplyProgress(false);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            if (!string.IsNullOrEmpty(tempExtractPath) && Directory.Exists(tempExtractPath)) 
                Directory.Delete(tempExtractPath, true);
            
            editor.isDownloading = false;
            if (!applyAfter)
            {
                editor.RefreshUiToolkitSections();
            }
            editor.Repaint();
        }
        
        // --- Post-processing phase (outside try/catch) ---
        if (extractionSucceeded)
        {
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (applyAfter)
                {
                    ReportApplyProgress(DownloadApplyImportComplete, "Waiting for Unity to finish importing downloaded files...");
                }
                yield return null;
            }
            MCBLogger.Log("[VersionActions] Editor finished pending compilation/import work.");            

            if (applyAfter) 
            {
                editor.selectedVersionForAction = version;
                SetApplyProgressRange(DownloadApplyImportComplete, 1f - DownloadApplyImportComplete);
                yield return ApplyOrResetCoroutine(version, false);
                advancedMeshPreparationPreloads.Clear();
            }
        }
    }

    // Runs on the downloaded archive before anything is extracted under Assets/.
    private bool CanUseInMemoryVersionPackage(long zipSizeBytes, out string decision)
    {
        if (zipSizeBytes <= 0L)
        {
            decision = "using disk because the ZIP size is unknown.";
            return false;
        }

        long estimatedBytes;
        try
        {
            checked
            {
                estimatedBytes = zipSizeBytes * InMemoryVersionPackageEstimatedMultiplier;
            }
        }
        catch (OverflowException)
        {
            decision = $"using disk because ZIP size {FormatByteSize(zipSizeBytes)} overflows the RAM estimate.";
            return false;
        }

        if (estimatedBytes > InMemoryVersionPackageHardCapBytes)
        {
            decision = $"using disk because estimated RAM {FormatByteSize(estimatedBytes)} exceeds hard cap {FormatByteSize(InMemoryVersionPackageHardCapBytes)}.";
            return false;
        }

        if (!TryGetAvailablePhysicalMemory(out long availableBytes))
        {
            decision = $"using disk because available physical RAM could not be measured for ZIP size {FormatByteSize(zipSizeBytes)}.";
            return false;
        }

        if (estimatedBytes > availableBytes)
        {
            decision = $"using disk because estimated RAM {FormatByteSize(estimatedBytes)} exceeds available RAM {FormatByteSize(availableBytes)}.";
            return false;
        }

        decision = $"using RAM because ZIP size {FormatByteSize(zipSizeBytes)} estimates to {FormatByteSize(estimatedBytes)} with {FormatByteSize(availableBytes)} available.";
        return true;
    }

    private Dictionary<string, Task<byte[]>> StartAdvancedMeshOriginalFbxPreloads(CustomBaseVersion version)
    {
        var tasks = new Dictionary<string, Task<byte[]>>(StringComparer.OrdinalIgnoreCase);
        string fallbackFbxPath = GetCurrentFBXPath();
        foreach (var patchFile in GetAdvancedMeshPatchFiles(version))
        {
            if (NativeMeshPayloadService.IsPlainPayloadTransform(patchFile.transform)) continue;
            try
            {
                string targetFbxPath = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
                if (string.IsNullOrWhiteSpace(targetFbxPath))
                {
                    continue;
                }

                string originalFbxPath = EnsureOriginalFbxKeyPath(version, patchFile, targetFbxPath, "advanced mesh preload");
                string absolutePath = Path.GetFullPath(originalFbxPath);
                if (tasks.ContainsKey(absolutePath))
                {
                    continue;
                }

                tasks[absolutePath] = Task.Run(() => File.ReadAllBytes(absolutePath));
                MCBLogger.Log($"[VersionActions] Preloading original FBX key for advanced mesh: {MCBUtils.ToUnityPath(originalFbxPath)}");
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[VersionActions] Could not preload original FBX key for advanced mesh: {ex.GetBaseException().Message}");
            }
        }

        return tasks;
    }

    private void StartAdvancedMeshPayloadPreparations(
        CustomBaseVersion version,
        Dictionary<string, byte[]> inMemoryPatchBytes,
        Dictionary<string, Task<byte[]>> originalFbxPreloadTasks)
    {
        advancedMeshPreparationPreloads.Clear();
        string fallbackFbxPath = GetCurrentFBXPath();
        foreach (var patchFile in GetAdvancedMeshPatchFiles(version))
        {
            try
            {
                string targetFbxPath = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
                if (string.IsNullOrWhiteSpace(targetFbxPath))
                {
                    continue;
                }

                string binPath = ResolveVersionPatchPath(version, patchFile);
                string originalFbxPath = ResolvePayloadKeyPath(version, patchFile, targetFbxPath, "advanced mesh preparation");
                string patchFileName = Path.GetFileName(patchFile.path);
                Task<byte[]> binDataTask = null;
                if (!string.IsNullOrWhiteSpace(patchFileName) &&
                    inMemoryPatchBytes != null &&
                    inMemoryPatchBytes.TryGetValue(patchFileName, out byte[] binBytes) &&
                    binBytes != null &&
                    binBytes.Length > 0)
                {
                    binDataTask = Task.FromResult(binBytes);
                }

                Task<byte[]> originalFbxDataTask = null;
                if (originalFbxPath != null && originalFbxPreloadTasks != null)
                {
                    originalFbxPreloadTasks.TryGetValue(Path.GetFullPath(originalFbxPath), out originalFbxDataTask);
                }

                var preload = NativeMeshPayloadService.StartEncryptedPayloadPreparation(
                    version,
                    patchFile,
                    binPath,
                    originalFbxPath,
                    binDataTask,
                    originalFbxDataTask);
                if (preload != null)
                {
                    advancedMeshPreparationPreloads[BuildAdvancedMeshPreparationKey(version, patchFile)] = preload;
                }
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[VersionActions] Could not start advanced mesh payload preload: {ex.GetBaseException().Message}");
            }
        }
    }

    private NativeMeshPayloadService.NativeMeshPayloadPreparationPreload GetAdvancedMeshPreparationPreload(
        CustomBaseVersion version,
        ModelFileData patchFile)
    {
        if (advancedMeshPreparationPreloads.TryGetValue(BuildAdvancedMeshPreparationKey(version, patchFile), out var preload))
        {
            return preload;
        }

        return null;
    }

    private static IEnumerable<ModelFileData> GetAdvancedMeshPatchFiles(CustomBaseVersion version)
    {
        return version?.versionFiles?
                   .Where(file => file != null &&
                                  string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                                  NativeMeshPayloadService.IsAdvancedMeshPatchTransform(file.transform)) ??
               Enumerable.Empty<ModelFileData>();
    }

    private static HashSet<string> GetAdvancedMeshPatchFileNames(CustomBaseVersion version)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patchFile in GetAdvancedMeshPatchFiles(version))
        {
            string name = string.IsNullOrWhiteSpace(patchFile.path)
                ? null
                : Path.GetFileName(patchFile.path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string BuildAdvancedMeshPreparationKey(CustomBaseVersion version, ModelFileData patchFile)
    {
        string path = string.IsNullOrWhiteSpace(patchFile?.path) ? "" : Path.GetFileName(patchFile.path);
        return $"{version?.assetId ?? 0}:{version?.version ?? ""}:{path}:{patchFile?.outputHash ?? ""}";
    }

    private static bool TryGetAvailablePhysicalMemory(out long availableBytes)
    {
        availableBytes = 0L;
        if (Application.platform != RuntimePlatform.WindowsEditor)
        {
            return false;
        }

        var status = new MemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        if (!GlobalMemoryStatusEx(ref status) || status.ullAvailPhys > long.MaxValue)
        {
            return false;
        }

        availableBytes = (long)status.ullAvailPhys;
        return availableBytes > 0L;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private IEnumerator DeleteVersionCoroutine(CustomBaseVersion version)
    {
        if (editor.isDeleting) yield break;
        editor.isDeleting = true;
        editor.warningsModule.Clear();
        editor.Repaint();

        bool deleted = false;
        try
        {
            // Repository delete: refuses while the version is being built/published.
            VersionRepository.Delete(version);
            deleted = true;
        }
        catch (Exception e) { editor.warningsModule.AddWarning($"Failed to delete folder: {e.Message}", MessageType.Error, "Deletion failed"); }

        if (deleted)
        {
            try
            {
                var deletedPayloads = NativeMeshPayloadService.DeleteGeneratedPayloadsForVersion(version);
                if (deletedPayloads.HasContent)
                {
                    MCBLogger.Log(
                        $"[VersionActions] Deleted generated advanced mesh cache for assetId={version.assetId} version={version.version}: {deletedPayloads.AssetCount} assets, {deletedPayloads.FormattedSize}.");
                }
            }
            catch (Exception e)
            {
                editor.warningsModule.AddWarning(
                    $"Deleted version files, but failed to delete generated advanced meshes: {e.Message}",
                    MessageType.Error,
                    "Advanced mesh deletion failed");
            }

            if (version.isUnsubmitted)
            {
                editor.creatorModule.RemoveUnsubmittedVersion(version);
            }
            AssetDatabase.Refresh();
            editor.LoadImportedVersions(true);
        }

        editor.isDeleting = false;
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private void BeginApplyProgress(string stepText, float progress)
    {
        editor.isApplying = true;
        if (!ApplyProgress.IsRunning)
        {
            applyProgressGeneration++;
            ApplyProgress.Begin(stepText);
        }

        ApplyProgress.Report(MapApplyProgress(progress), stepText);
        editor.Repaint();
    }

    private void ReportApplyProgress(float progress, string stepText)
    {
        if (!ApplyProgress.IsRunning)
        {
            applyProgressGeneration++;
            editor.isApplying = true;
            ApplyProgress.Begin(stepText);
        }

        ApplyProgress.Report(MapApplyProgress(progress), stepText);
        editor.Repaint();
    }

    private void FinishApplyProgress(bool success)
    {
        if (success)
        {
            int generation = ++applyProgressGeneration;
            ApplyProgress.Report(1f, "Apply complete...");
            ResetApplyProgressRange();
            editor.Repaint();
            EditorCoroutineUtility.StartCoroutineOwnerless(CompleteApplyProgressAfterVisualHold(generation));
            // Other tools (My Avatar) check the avatar's clothing against the new custom base.
            if (editor.customBaseTarget != null) MCBReFitIntegration.NotifyCustomBaseChanged(editor.customBaseTarget);
        }
        else
        {
            applyProgressGeneration++;
            editor.isApplying = false;
            ResetApplyProgressRange();
            ApplyProgress.Fail();
            editor.Repaint();
            editor.RefreshUiToolkitSections();
        }
    }

    private int StartApplyProgress(string stepText, float progress)
    {
        ResetApplyProgressRange();
        applyProgressGeneration++;
        editor.isApplying = true;
        ApplyProgress.Begin(stepText);
        ApplyProgress.Report(MapApplyProgress(progress), stepText);
        editor.Repaint();
        return applyProgressGeneration;
    }

    private void SetApplyProgressRange(float progressBase, float progressScale)
    {
        applyProgressBase = Mathf.Clamp01(progressBase);
        applyProgressScale = Mathf.Clamp01(progressScale);
    }

    private void ResetApplyProgressRange()
    {
        applyProgressBase = 0f;
        applyProgressScale = 1f;
    }

    private float MapApplyProgress(float progress)
    {
        return Mathf.Clamp01(applyProgressBase + Mathf.Clamp01(progress) * applyProgressScale);
    }

    private static float GetVisibleDownloadApplyProgress(double downloadStartedAt, float downloadProgress, float previousProgress)
    {
        float realProgress = Mathf.Lerp(DownloadApplyProgressStart, DownloadApplyProgressComplete, Mathf.Clamp01(downloadProgress));
        float syntheticProgress = DownloadApplyProgressStart +
            (float)(EditorApplication.timeSinceStartup - downloadStartedAt) * DownloadApplySyntheticProgressPerSecond;
        syntheticProgress = Mathf.Min(syntheticProgress, DownloadApplySyntheticProgressMax);

        return Mathf.Clamp01(Mathf.Max(previousProgress, Mathf.Max(realProgress, syntheticProgress)));
    }

    private void StartApplyOrResetWithIntro(CustomBaseVersion version, bool isReset, string stepText)
    {
        if (editor.isApplying || ApplyProgress.IsRunning || RefitBlocksSwitch())
        {
            return;
        }

        int generation = StartApplyProgress(stepText, 0f);
        EditorCoroutineUtility.StartCoroutineOwnerless(ApplyOrResetAfterIntroCoroutine(version, isReset, stepText, generation));
    }

    private IEnumerator ApplyOrResetAfterIntroCoroutine(CustomBaseVersion version, bool isReset, string stepText, int generation)
    {
        double startTime = EditorApplication.timeSinceStartup;
        while (applyProgressGeneration == generation &&
               EditorApplication.timeSinceStartup - startTime < ApplyProgressIntroSeconds)
        {
            float localProgress = (float)((EditorApplication.timeSinceStartup - startTime) / ApplyProgressIntroSeconds);
            ApplyProgress.Report(Mathf.Lerp(0f, 0.04f, Mathf.Clamp01(localProgress)), stepText);
            editor.Repaint();
            yield return null;
        }

        if (applyProgressGeneration != generation)
        {
            yield break;
        }

        yield return ApplyOrResetCoroutine(version, isReset);
    }

    private IEnumerator CompleteApplyProgressAfterVisualHold(int generation)
    {
        double startTime = EditorApplication.timeSinceStartup;
        while (applyProgressGeneration == generation &&
               EditorApplication.timeSinceStartup - startTime < ApplyProgressCompletionHoldSeconds)
        {
            editor.Repaint();
            yield return null;
        }

        if (applyProgressGeneration != generation)
        {
            yield break;
        }

        editor.isApplying = false;
        ApplyProgress.Complete();
        editor.Repaint();
        editor.RefreshUiToolkitSections();
    }

    internal IEnumerator ApplyOrResetCoroutine(CustomBaseVersion version, bool isReset)
    {
        IEnumerator routine = null;
        try
        {
            routine = RunPreparedMutation(ThenShowApplying(PrepareVersionSwitchCoroutine(version, isReset), isReset), () => ApplyOrResetCore(version, isReset));
        }
        catch (Exception ex)
        {
            HandleApplyOrResetException(ex, version, isReset);
            yield break;
        }

        while (routine != null)
        {
            object current = null;
            bool moved = false;
            try
            {
                moved = routine.MoveNext();
                if (moved)
                {
                    current = routine.Current;
                }
            }
            catch (Exception ex)
            {
                HandleApplyOrResetException(ex, version, isReset);
                yield break;
            }

            if (!moved)
            {
                yield break;
            }

            yield return current;
        }
    }

    private void HandleApplyOrResetException(Exception ex, CustomBaseVersion version, bool isReset)
    {
        string operation = isReset ? "Reset" : "Apply";
        string versionLabel = version != null ? version.version : "null";
        string message = $"{operation} failed: {ex.GetBaseException().Message}";
        MCBLogger.LogError($"[VersionActions] {operation} failed unexpectedly. version={versionLabel} reset={isReset}: {ex}");
        RollbackActiveTransition();
        editor.warningsModule?.AddWarning(message, MessageType.Error, $"{operation} failed");
        editor.isDownloading = false;
        advancedMeshPreparationPreloads.Clear();
        FinishApplyProgress(false);
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private IEnumerator PrepareVersionSwitchCoroutine(CustomBaseVersion version, bool isReset)
    {
        var requestedTarget = editor.customBaseTarget;
        string requestedToken = editor.authToken;
        if (!isReset)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            MCBVersionDelivery.ApplyLocalDelivery(version);
            var logic = fileManagerService.PrepareLogicPrefabCoroutine(MCBUtils.GetLogicPackagePath(version));
            while (logic.MoveNext()) yield return logic.Current;
            foreach (var patch in version.versionFiles ?? Array.Empty<ModelFileData>())
            {
                if (patch == null || !string.Equals(patch.role, "PATCH", StringComparison.OrdinalIgnoreCase) ||
                    !NativeMeshPayloadService.IsAdvancedMeshPatchTransform(patch.transform)) continue;
                string targetPath = ResolveTargetFbxPath(version, patch, GetCurrentFBXPath());
                string originalPath = ResolvePayloadKeyPath(version, patch, targetPath, "native mesh preparation");
                var prepare = NativeMeshPayloadService.MaterializeEncryptedPayloadAssetCoroutine(
                    version, patch, ResolveVersionPatchPath(version, patch), originalPath, fileManagerService,
                    (progress, label) => ReportApplyProgress(Mathf.Lerp(0.05f, 0.30f, progress), label),
                    _ => { }, GetAdvancedMeshPreparationPreload(version, patch));
                while (prepare.MoveNext()) yield return prepare.Current;
            }
        }
        while (EditorApplication.isCompiling || EditorApplication.isUpdating) yield return null;
        if (requestedTarget == null || editor.customBaseTarget != requestedTarget || editor.authToken != requestedToken)
            throw new InvalidOperationException("The avatar or signed-in account changed while preparing the version switch.");

    }

    // The Action boundary prevents a mutation phase from silently becoming a yielding coroutine.
    // The switch itself runs in one editor frame (one Undo step) and the Inspector cannot repaint meanwhile: say so first.
    private IEnumerator ThenShowApplying(IEnumerator preparation, bool isReset)
    {
        while (preparation.MoveNext()) yield return preparation.Current;
        BeginApplyProgress(isReset ? "Resetting... Unity may pause for a moment" : "Applying version... Unity may pause for a moment", 0f);
        yield return null;
    }

    internal static IEnumerator RunPreparedMutation(IEnumerator preparation, Action mutate)
    {
        while (preparation.MoveNext()) yield return preparation.Current;
        mutate();
    }

    private void ApplyOrResetCore(CustomBaseVersion version, bool isReset, bool finalize = true)
    {
        // The intro spans frames: a refit may have started meanwhile.
        if (RefitBlocksSwitch())
        {
            FinishApplyProgress(false);
            return;
        }
        MCBPerformance.PauseForeground();
        if (!isReset) MCBVersionDelivery.ApplyLocalDelivery(version);
        float startingProgress = applyProgressBase > 0f
            ? 0f
            : (ApplyProgress.IsRunning ? Mathf.Max(ApplyProgress.Progress, 0.18f) : 0f);
        BeginApplyProgress(isReset ? "Preparing reset..." : "Preparing version switch...", startingProgress);
        var root = AvatarPaths.Root(editor.customBaseTarget);
        bool preserveBlendshapeValues = editor.customBaseTarget != null && editor.customBaseTarget.preserveBlendshapeValuesOnVersionSwitch;
        var blendshapeSnapshot = preserveBlendshapeValues ? CaptureBlendshapeState(root) : null;
        var previousVersion = ResolvePersistedAppliedVersion();
        // The original base has none of the custom base's blendshapes: remember their values for its next install.
        if (isReset) RememberVersionBlendshapes(root, previousVersion);
        var versionForAssets = isReset ? (previousVersion ?? editor.selectedVersionForAction) : version;
        bool previousUsesAdvancedMesh = NativeMeshPayloadService.VersionUsesAdvancedMesh(previousVersion);
        bool versionUsesAdvancedMesh = NativeMeshPayloadService.VersionUsesAdvancedMesh(versionForAssets);
        var transitionVersions = GetDistinctTransitionVersions(previousVersion, versionForAssets);
        bool isAdvancedTransition = previousUsesAdvancedMesh || versionUsesAdvancedMesh;
        var profile = new ApplyTimingProfile(versionForAssets, isReset, versionUsesAdvancedMesh);
        var dynamicNormalsService = new DynamicNormalsService(editor);
        profile.Mark("Setup target, version state, and blendshape snapshot");

        MCBLogger.Log($"[VersionActions] ApplyOrReset start (reset={isReset}, version={(version != null ? version.version : "null")})");

        // Leaving the original state records the avatar's own models; while a version is applied they are what
        // transitions and the reset restore, whatever the version or the selected asset name as sources.
        var target = editor.customBaseTarget;
        if (previousVersion == null && target.appliedCustomBaseAssetId == 0)
        {
            if (!isReset)
            {
                Undo.RecordObject(target, "Record original models");
                target.versionOriginalModels = target.baseFbxFiles.Where(model => model != null).ToList();
            }
        }
        else if (target.versionOriginalModels.Count > 0 && !target.baseFbxFiles.SequenceEqual(target.versionOriginalModels))
        {
            editor.serializedObject.ApplyModifiedProperties();
            FileManagerService.SetCreatorSourceFiles(target, target.versionOriginalModels);
            editor.serializedObject.Update();
        }

        string fbxPath = GetCurrentFBXPath();
        profile.Mark("Resolved current FBX path");
        if (string.IsNullOrEmpty(fbxPath))
        {
            profile.Done("ABORT missing FBX path");
            FinishApplyProgress(false);
            return;
        }

        if (activeTransitionRollback != null)
            throw new InvalidOperationException("Another version transition is still active.");
        activeTransitionRollback = VersionTransitionRollbackSnapshot.Capture(
            GetTransitionAffectedFbxPaths(transitionVersions, fbxPath), editor.customBaseTarget, editor);

        ModeService.Restore(editor.customBaseTarget);
        NativeRendererLayoutService.Restore(editor.customBaseTarget);
        NativeMeshPayloadService.RestoreOriginalBoneParents(editor.customBaseTarget);
        MCBReFitIntegration.SaveVersionFits(editor.customBaseTarget, previousVersion);
        MCBReFitIntegration.RestoreOriginalAssetMeshes(editor.customBaseTarget);
        editor.serializedObject.Update();

        var previousAffectedFbxPaths = GetResetAffectedFbxPaths(previousVersion ?? versionForAssets, fbxPath);
        if (!isAdvancedTransition)
        {
            dynamicNormalsService.Remove(previousAffectedFbxPaths);
        }
        fileManagerService.RemoveExistingLogic(root);
        ReportApplyProgress(0.05f, "Removing existing MCB objects...");
        profile.Mark(isAdvancedTransition
            ? "Deferred DynamicNormals cleanup to transactional full FBX-state restoration and removed existing MCB logic"
            : "Removed current DynamicNormals objects and existing MCB logic");
        
        bool success = false;
        Exception operationException = null;
        var restoredPreviousModels = new List<string>();
        if (isReset)
        {
            try
            {
                ReportApplyProgress(0.10f, "Restoring base mesh state...");
                if (versionUsesAdvancedMesh)
                {
                    MCBLogger.Log("[VersionActions] Advanced mesh reset will restore renderer meshes from the imported default FBX asset and skip FBX reimport when the source file already matches its backup.");
                }

                RestoreBackupsForVersion(versionForAssets, fbxPath, !versionUsesAdvancedMesh);
                if (versionUsesAdvancedMesh)
                {
                    int restoredTransforms = NativeMeshPayloadService.RestoreOriginalAuthoringPoseFromFbx(root, new[] { fbxPath });
                    if (restoredTransforms == 0)
                    {
                        throw new InvalidOperationException("Advanced mesh reset could not safely restore the canonical FBX armature pose.");
                    }
                    var missingRenderers = new List<string>();
                    int restoredRenderers = NativeMeshPayloadService.RestoreOriginalMeshesFromFbx(
                        root,
                        versionForAssets,
                        GetResetAffectedFbxPaths(versionForAssets, fbxPath),
                        editor.customBaseTarget,
                        missingRenderers: missingRenderers);
                    if (restoredRenderers == 0 && missingRenderers.Count == 0)
                    {
                        throw new InvalidOperationException("Advanced mesh reset could not safely restore any renderer from the original FBX.");
                    }
                    WarnSkippedRenderers(missingRenderers);
                    NativeMeshPayloadService.RemoveGeneratedBones(editor.customBaseTarget);
                    // After the skeleton is final: the Animator caches the bones it binds.
                    ApplyDefaultAvatarToRootForReset(root, versionForAssets);
                }

            }
            catch (Exception e)
            {
                operationException = e;
            }
        }
        else
        {
            try
            {
                if (version == null) throw new ArgumentNullException(nameof(version), "A version must be provided to apply.");
                if (isAdvancedTransition)
                {
                    ReportApplyProgress(0.10f, "Restoring original FBX state...");
                    // Shared meshes the next version reuses may stay, unless it maps renderers by material slot names:
                    // its layout must start from the original renderers.
                    bool keepShared = VersionCustomization.Read(version.extraCustomization).rendererLayout.IsEmpty;
                    RestoreOriginalFbxStateForTransition(root, transitionVersions, fbxPath,
                        previousVersion != null && !previousUsesAdvancedMesh, keepShared ? version : null);
                    NativeMeshPayloadService.RemoveGeneratedBones(editor.customBaseTarget);
                    profile.Mark("Restored original FBX renderer and armature state for advanced mesh transition");
                }
                else if (previousVersion != null)
                {
                    ReportApplyProgress(0.10f, "Restoring models of the previous version...");
                    restoredPreviousModels = RestoreModelsLeftByPreviousVersion(root, previousVersion, version, fbxPath);
                    profile.Mark($"Restored {restoredPreviousModels.Count} models the previous version changed");
                }
                // The models this version gives its own Avatar: their import settings are kept for the reset.
                foreach (string avatarModelPath in GetCustomAvatarFbxPaths(version))
                    AvatarDefinitionGenerationService.BackupOriginalImportSettings(avatarModelPath);
                NativeRendererLayoutService.Apply(editor.customBaseTarget, version, GetCurrentFBXPaths());
                ApplyVersionModelFilePatches(version, fbxPath);
            }
            catch (Exception e)
            {
                operationException = e;
            }
        }

        if (operationException == null)
        {
            try
            {
                success = true;
                profile.Mark(isReset ? "Restored base/native meshes" : "Applied model file patches");
                if (!isReset && versionUsesAdvancedMesh)
                {
                    advancedMeshPreparationPreloads.Clear();
                }
            }
            catch (Exception e)
            {
                operationException = e;
            }
        }

        if (operationException != null)
        {
            profile.Mark("Model patch/reset failed");
            MCBLogger.LogError($"[MCB] Operation failed: {operationException.Message}");
            editor.warningsModule?.AddWarning(operationException.Message, MessageType.Error, isReset ? "Reset failed" : "Apply failed");
            if (activeTransitionRollback != null)
            {
                RollbackActiveTransition();
            }
            advancedMeshPreparationPreloads.Clear();
            FinishApplyProgress(false);
        }
        
        if (success)
        {
            var affectedFbxPaths = isReset
                ? GetResetAffectedFbxPaths(versionForAssets, fbxPath)
                : GetAffectedFbxPaths(version, fbxPath).Union(restoredPreviousModels, StringComparer.OrdinalIgnoreCase).ToList();
            var fbxImportPaths = isReset
                ? (versionUsesAdvancedMesh ? new List<string>() : affectedFbxPaths)
                : GetFbxImportPaths(version, fbxPath);
            ReportApplyProgress(versionUsesAdvancedMesh ? 0.72f : 0.30f, versionUsesAdvancedMesh ? "Advanced mesh data prepared..." : "Preparing FBX imports...");
            profile.Mark("Resolved affected FBX/import path lists");
            int importIndex = 0;
            foreach (string affectedPath in fbxImportPaths)
            {
                importIndex++;
                ReportApplyProgress(Mathf.Lerp(0.30f, 0.55f, fbxImportPaths.Count == 0 ? 1f : importIndex / (float)fbxImportPaths.Count), $"Importing FBX {importIndex}/{fbxImportPaths.Count}...");
                MCBLogger.Log($"[VersionActions] Importing modified FBX at {affectedPath}");
                AssetDatabase.ImportAsset(affectedPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            MCBLogger.Log(fbxImportPaths.Count > 0
                ? "[VersionActions] FBX import completed."
                : "[VersionActions] No FBX import required for this version operation.");
            
            // Model imports above are synchronous; preparation finished any asynchronous package import.
            MCBLogger.Log("[VersionActions] Synchronous model imports completed.");
            profile.Mark("FBX import and pending editor update wait");
            //  --- END CRITICAL FIX ---
            
            if (isReset)
            {
                if (versionUsesAdvancedMesh)
                {
                    ReportApplyProgress(0.78f, "Applying default avatar...");
                    if (NativeMeshPayloadService.RestoreOriginalAuthoringPoseFromFbx(root, new[] { fbxPath }) == 0)
                        throw new InvalidOperationException("Advanced mesh reset could not safely finalize the canonical FBX armature pose.");
                    ApplyDefaultAvatarToRootForReset(root, versionForAssets);
                }
                else
                {
                    ReportApplyProgress(0.60f, "Restoring original import settings...");
                    RestoreImportersForReset(root, versionForAssets, fbxPath);
                }
                MCBLogger.Log("[VersionActions] Default avatar import completed.");
                profile.Mark("Default avatar import/reset wait");
                if (versionUsesAdvancedMesh)
                {
                    profile.Mark("Restored native mesh reset authoring pose");
                    MCBLogger.Log("[VersionActions] Restored source authoring pose after advanced mesh reset.");
                }
            }
            else if (versionForAssets != null && versionForAssets != VersionListDrawer.RESET_VERSION)
            {
                ReportApplyProgress(versionUsesAdvancedMesh ? 0.78f : 0.60f, "Applying avatar definition...");
                ApplyAvatarImportsForVersion(root, versionForAssets, isReset);
                profile.Mark("Applied avatar import settings for version");
                MCBLogger.Log("[VersionActions] Avatar import completed.");
                profile.Mark("Avatar import/update wait");
                if (versionUsesAdvancedMesh)
                {
                    ReportApplyProgress(0.82f, "Restoring advanced mesh authoring pose...");
                    ApplyAdvancedMeshAuthoringPose(versionForAssets, fbxPath);
                    profile.Mark("Restored advanced mesh authoring pose");
                    MCBLogger.Log("[VersionActions] Restored payload authoring pose after advanced mesh apply.");
                }

                if (!isReset)
                {
                    string packagePath = MCBUtils.GetLogicPackagePath(versionForAssets);
                    fileManagerService.InstantiatePreparedLogicPrefab(packagePath, root);
                    profile.Mark("Imported/instantiated logic prefab");
                }
            }
            // Check feature flags
            bool hasCustomVeins = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "customVeins");
            bool hasDynamicNormalBody = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "dynamicNormalBody");
            bool hasDynamicNormalFlexing = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "dynamicNormalFlexing");
            var normalSelection = VersionCustomization.Read(version?.extraCustomization).dynamicNormalBlendshapes;
            bool shouldApplyDynamicNormals = (!isReset && normalSelection.Count > 0 || hasDynamicNormalBody || hasDynamicNormalFlexing) && editor.customBaseTarget.useDynamicNormals;
            
            // Apply or remove dynamic normals based on version feature flags
            // Complete these synchronous mutations inside the version transaction.
            if (!shouldApplyDynamicNormals && !versionUsesAdvancedMesh)
            {
                MCBLogger.Log("[VersionActions] Removing dynamic normals.");
                dynamicNormalsService.Remove(affectedFbxPaths);
                MCBLogger.Log("[VersionActions] Dynamic normals removal completed.");
                profile.Mark("Removed DynamicNormals and waited for editor update");
            }

            if (!versionUsesAdvancedMesh)
            {
                RefreshTargetMeshesFromFBXs(root, affectedFbxPaths, versionForAssets);
                profile.Mark("Refreshed target meshes from imported FBXs");
            }
            
            if (shouldApplyDynamicNormals)
            {
                if (versionUsesAdvancedMesh)
                {
                    MCBLogger.Log("[VersionActions] Dynamic normals are expected to be pre-baked in the advanced native mesh payload; skipping user-side regeneration.");
                    profile.Mark("Skipped user-side DynamicNormals regeneration for advanced native mesh");
                }
                else
                {
                    bool applyBody = hasDynamicNormalBody;
                    bool applyFlex = hasDynamicNormalFlexing;
                    MCBLogger.Log("[VersionActions] Applying dynamic normals.");
                    if (normalSelection.Count > 0) dynamicNormalsService.Apply(normalSelection);
                    else dynamicNormalsService.Apply(applyBody, applyFlex);
                    MCBLogger.Log("[VersionActions] Dynamic normals application completed.");
                    profile.Mark("Applied DynamicNormals and waited for editor update");
                }
            }
            
            // Apply or remove custom veins based on version feature flag
            var materialService = new MaterialService(root);
            var targetMaterialRenderers = versionUsesAdvancedMesh
                ? NativeMeshPayloadService.ResolveRenderersForSourcePaths(root, versionForAssets, affectedFbxPaths, editor.customBaseTarget)
                : materialService.GetSkinnedMeshRenderersForFbxPaths(affectedFbxPaths);
            // An unencrypted version names no source renderers: its targets are the renderers carrying its meshes.
            if (targetMaterialRenderers.Count == 0 && versionUsesAdvancedMesh && !isReset)
                targetMaterialRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => renderer.sharedMesh != null
                    && NativeMeshPayloadService.IsSharedMeshForVersion(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)), versionForAssets)).ToList();
            ReportApplyProgress(0.90f, "Resolving materials...");
            profile.Mark($"Resolved material target renderers ({targetMaterialRenderers.Count})");
            
            bool materialStateChanged = false;
            if (hasCustomVeins)
            {
                // Apply custom veins
                string versionFolder = MCBUtils.GetVersionDataPath(version);
                string veinsNormalPath = MCBUtils.CombineUnityPath(versionFolder, "veins normal.png");
                string veinsNormalAbsolutePath = Path.GetFullPath(veinsNormalPath);

                if (File.Exists(veinsNormalAbsolutePath))
                {
                    bool veinsApplied = false;
                    foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetMaterialRenderers))
                    {
                        if (materialService.SetDetailNormalMap(renderer, veinsNormalPath, false))
                        {
                            materialService.SetDetailNormalOpacity(renderer, 1.0f, false);
                            veinsApplied = true;
                            materialStateChanged = true;
                        }
                    }

                    if (veinsApplied)
                    {
                        // Sync the toggle state with the applied state
                        EditorPrefs.SetBool(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY, true);
                        MCBLogger.Log($"[VersionActions] Custom veins applied from version {version.version}");
                    }
                }
                else
                {
                    MCBLogger.LogWarning($"[VersionActions] Custom veins file not found at: {veinsNormalPath}");
                }
            }
            else
            {
                // Remove custom veins when switching to a version without the feature or resetting
                foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetMaterialRenderers))
                {
                    if (materialService.RemoveDetailNormalMap(renderer, false))
                    {
                        materialStateChanged = true;
                    }
                }
                if (materialService.RemoveVersionVeins()) materialStateChanged = true;
                // Sync the toggle state - set to false when removing veins
                EditorPrefs.SetBool(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY, false);
                MCBLogger.Log("[VersionActions] Custom veins removed");
            }
            if (materialStateChanged)
            {
                materialService.SaveTouchedMaterials();
            }
            ReportApplyProgress(0.93f, hasCustomVeins ? "Applied custom veins..." : "Updated material state...");
            profile.Mark(hasCustomVeins ? "Applied custom veins materials" : "Removed custom veins materials");
            
            if (!isReset && version != null)
            {
                int restoredRefits = MCBReFitIntegration.RestoreVersionFits(editor.customBaseTarget, version);
                editor.serializedObject.Update();
                profile.Mark($"Restored {restoredRefits} saved accessory ReFits");
            }

            // Restore blendshape values by name after all mesh swaps are complete.
            if (!isReset && version != null)
            {
                ReportApplyProgress(0.95f, "Applying blendshapes and sliders...");
                if (preserveBlendshapeValues)
                {
                    var remembered = RememberedBlendshapes(version);
                    // Coming from the original base, a same-named original shape must not replace the custom base's value.
                    if (previousVersion == null)
                        foreach (var weights in blendshapeSnapshot.Values)
                            foreach (string name in remembered.Keys) weights.Remove(name);
                    RestoreBlendshapeState(root, blendshapeSnapshot, BuildBlendshapeDefaultLookup(version, remembered));
                    SyncReFitTransferredBlendshapeWeights(root, version);
                    SyncBlendshapeOverridesFromCurrentWeights(root, version);
                    MCBLogger.Log($"[VersionActions] Restored blendshape values by name (saved renderers: {blendshapeSnapshot.Count}, overrides: {editor.customBaseTarget.customBlendshapeOverrideNames.Count})");
                }
                else
                {
                    ApplyVersionBlendshapeValues(root, version);
                MCBLogger.Log("[VersionActions] Blendshape preservation on version switch is disabled. Applied version defaults/overrides.");
                }

                // Handle "mcb sliders" GameObject state and deletion
                var slidersTransform = root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
                var hasSliders = version.customBlendshapes != null && version.customBlendshapes.Any(e => e.isSlider);

                if (!hasSliders)
                {
                    if (slidersTransform != null)
                    {
                        MCBLogger.Log("[VersionActions] Version has no sliders. Deleting sliders GameObject.");
                        Undo.DestroyObjectImmediate(slidersTransform.gameObject);
                    }
                }
                else
                {
                    // Ensure sliders are applied/updated for the new version
                    var allSliderEntries = version.customBlendshapes.Where(e => e.isSlider).ToList();
                    List<CustomBlendshapeEntry> selectedSliders;

                    if (editor.customBaseTarget.useCustomSliderSelection)
                    {
                        var savedNames = new HashSet<string>(editor.customBaseTarget.customSliderSelectionNames ?? new List<string>());
                        selectedSliders = allSliderEntries.Where(e => savedNames.Contains(e.name)).ToList();
                    }
                    else
                    {
                        selectedSliders = allSliderEntries.Where(e => e.isSliderDefault).ToList();
                    }

                    MCBLogger.Log($"[VersionActions] Applying sliders for version {version.version}. Count: {selectedSliders.Count}");
                    VRCFuryService.Instance.ApplySliders(root.gameObject, editor.customBaseTarget.slidersMenuName, selectedSliders);

                    // Ensure the active state is correct (ApplySliders handles creation state, but we enforce it here for existing ones too)
                    slidersTransform = root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
                    if (slidersTransform != null)
                    {
                        bool desiredState = editor.customBaseTarget.useCustomSlidersState ? editor.customBaseTarget.customSlidersState : true;
                        if (slidersTransform.gameObject.activeSelf != desiredState)
                        {
                            MCBLogger.Log($"[VersionActions] Setting sliders GameObject active state to: {desiredState}");
                            Undo.RecordObject(slidersTransform.gameObject, "Set Sliders Active State");
                            slidersTransform.gameObject.SetActive(desiredState);
                        }
                    }
                }
            }
            else if (isReset)
            {
                ReportApplyProgress(0.95f, "Clearing custom version controls...");
                // Clear custom overrides when resetting
                editor.customBaseTarget.customBlendshapeOverrideNames.Clear();
                editor.customBaseTarget.customBlendshapeOverrideValues.Clear();
                editor.customBaseTarget.useCustomSliderSelection = false;
                editor.customBaseTarget.customSliderSelectionNames.Clear();
                editor.customBaseTarget.useCustomSlidersState = false;
                editor.customBaseTarget.customSlidersState = true;
                SyncAppliedVersionBlendshapeLinkCache(null);
                SyncAppliedVersionAnimationPositionOffsetCache(null);

                // Delete sliders GameObject on reset
                var slidersTransform = root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
                if (slidersTransform != null)
                {
                    MCBLogger.Log("[VersionActions] Reset requested. Deleting sliders GameObject.");
                    Undo.DestroyObjectImmediate(slidersTransform.gameObject);
                }
            }
            profile.Mark("Applied blendshape defaults/preservation and slider state");

            if (isReset)
            {
                // Back on the original base: a later install starts from the version's default modes.
                int resetAssetId = editor.customBaseTarget.appliedCustomBaseAssetId;
                editor.customBaseTarget.modeChoices.RemoveAll(choice => choice.assetId == resetAssetId);
                ClearAppliedVersionState();
            }
            else
            {
                try
                {
                    PersistAppliedVersionState(version);
                    ModeService.Install(editor.customBaseTarget, version);
                }
                catch (Exception ex)
                {
                    success = false;
                    MCBLogger.LogError("[VersionActions] Mode setup failed; restoring the previous version. " + ex.Message);
                }
            }
            ReportApplyProgress(0.98f, "Saving applied version state...");
            profile.Mark("Updated persisted applied-version state");
        }

        if (!success)
        {
            RollbackActiveTransition();
            editor.Repaint();
            FinishApplyProgress(false);
            profile.Done("FAILED");
            return;
        }
        
        CommitActiveTransition();
        if (previousVersion != null && (isReset || !string.Equals($"{previousVersion.assetId}:{previousVersion.version}", $"{version?.assetId}:{version?.version}", StringComparison.Ordinal)))
        {
            try
            {
                var deletedPayloads = NativeMeshPayloadService.DeleteUnreferencedPayloadsOfVersion(previousVersion);
                if (deletedPayloads.HasContent)
                    MCBLogger.Log($"[VersionActions] Deleted {deletedPayloads.AssetCount} generated advanced mesh assets of version {previousVersion.version} nothing uses any more ({deletedPayloads.FormattedSize}).");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                MCBLogger.LogWarning($"[VersionActions] Could not delete unused generated advanced meshes of version {previousVersion.version}: {ex.Message}");
            }
        }
        if (!finalize) return; // A saved-custom transition will commit or restore its encompassing snapshot.
        MCBLogger.Log("[VersionActions] ApplyOrResetCoroutine completed. Updating hash and applied state.");
        // Force a recalculation of current/default FBX hashes after every switch. Advanced
        // transitions can restore .originalbase bytes even though the visible mesh is a native
        // payload, and the persisted advanced marker keeps version detection authoritative.
        int hashGeneration = ++fbxHashRecalculationGeneration;
        profile.Mark(isAdvancedTransition
            ? "Started asynchronous FBX hash/state recalculation after advanced transition"
            : "Started asynchronous FBX hash/state recalculation");

        EditorUtility.SetDirty(editor.customBaseTarget);
        if (versionUsesAdvancedMesh)
        {
            MCBLogger.Log("[VersionActions] Skipping synchronous project auto-save for native mesh apply. The scene remains dirty so Unity can save it normally.");
            profile.Mark("Skipped synchronous auto-save for advanced native mesh");
        }
        else
        {
            AutoSaveProjectAfterVersionSwitch();
            profile.Mark("Auto-saved assets and scenes");
        }
        editor.Repaint();
        FinishApplyProgress(true);
        // Completion advances the visual generation; it must not cancel its own refresh.
        EditorCoroutineUtility.StartCoroutineOwnerless(
            RecalculateCurrentFbxHashCoroutine(hashGeneration, applyProgressGeneration));
        profile.Done();
    }

    private void ApplyVersionModelFilePatches(CustomBaseVersion version, string fallbackFbxPath)
    {
        var patchFiles = version.versionFiles?
            .Where(file => file != null && string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (patchFiles == null || patchFiles.Length == 0)
        {
            MCBLogger.Log("[VersionActions] Version has no model file patches. Skipping FBX transformation.");
            ReportApplyProgress(0.70f, "No mesh patch required...");
            return;
        }

        for (int i = 0; i < patchFiles.Length; i++)
        {
            var patchFile = patchFiles[i];
            if (string.IsNullOrWhiteSpace(patchFile.transform))
            {
                throw new InvalidDataException("Model file patch is missing required transform metadata.");
            }

            string transform = patchFile.transform;
            if (NativeMeshPayloadService.IsAdvancedMeshPatchTransform(transform))
            {
                ReportApplyProgress(0.12f, $"Preparing advanced mesh {i + 1}/{patchFiles.Length}...");
                ApplyXorBinToUnityAsset(version, patchFile);
                continue;
            }

            if (string.Equals(transform, ModelFileTransforms.DirectAsset, StringComparison.OrdinalIgnoreCase))
            {
                ReportApplyProgress(0.20f, $"Using direct asset patch {i + 1}/{patchFiles.Length}...");
                continue;
            }

            if (!ModelFileTransforms.IsFbxReplacementTransform(transform))
            {
                throw new NotSupportedException($"Unsupported model file transform '{transform}'.");
            }

            ReportApplyProgress(Mathf.Lerp(0.12f, 0.28f, (i + 1f) / patchFiles.Length), $"Applying FBX patch {i + 1}/{patchFiles.Length}...");
            string targetFbxPath = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
            string binPath = ResolveVersionPatchPath(version, patchFile);
            if (ModelFileTransforms.IsHdiffFbxReplacementTransform(transform))
            {
                ApplyHdiffXorBinToFbx(version, patchFile, binPath, targetFbxPath);
            }
            else
            {
                ApplyXorBinToFbx(version, patchFile, binPath, targetFbxPath);
            }
        }
    }

    private void ApplyXorBinToFbx(CustomBaseVersion version, ModelFileData patchFile, string binPath, string fbxPath)
    {
        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
            throw new FileNotFoundException("Apply failed: .bin file not found. Please download or build it first.");

        if (string.IsNullOrWhiteSpace(fbxPath) || !File.Exists(fbxPath))
            throw new FileNotFoundException("Apply failed: target FBX file not found.", fbxPath);

        string originalFbxPath = EnsureOriginalFbxKeyPath(version, patchFile, fbxPath, "XOR FBX patch");

        byte[] baseData = File.ReadAllBytes(originalFbxPath);
        byte[] binData = File.ReadAllBytes(binPath);
        byte[] transformedData = fileManagerService.XorTransform(baseData, binData);
        string tempOutputPath = HdiffService.CreateTempWorkPath(".fbx");
        try
        {
            File.WriteAllBytes(tempOutputPath, transformedData);
            if (!string.IsNullOrWhiteSpace(patchFile?.outputHash))
            {
                string actualOutputHash = fileManagerService.CalculateFileHash(tempOutputPath);
                if (!string.Equals(actualOutputHash, patchFile.outputHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"XOR apply failed integrity verification for '{Path.GetFileName(fbxPath)}'. Expected {patchFile.outputHash}, got {actualOutputHash ?? "<missing>"}.");
                }
            }

            fileManagerService.ReplaceFbxWithCustomCopy(fbxPath, tempOutputPath);
        }
        finally
        {
            if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath);
        }
    }

    private void ApplyHdiffXorBinToFbx(CustomBaseVersion version, ModelFileData patchFile, string binPath, string fbxPath)
    {
        if (patchFile == null)
        {
            throw new ArgumentNullException(nameof(patchFile));
        }

        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
        {
            throw new FileNotFoundException("Apply failed: HDiff .bin file not found. Please download or build it first.", binPath);
        }

        if (string.IsNullOrWhiteSpace(fbxPath) || !File.Exists(fbxPath))
        {
            throw new FileNotFoundException("Apply failed: target FBX file not found.", fbxPath);
        }

        string originalFbxPath = EnsureOriginalFbxKeyPath(version, patchFile, fbxPath, "HDiff FBX patch");
        VerifyPatchSourceHash(version, patchFile, originalFbxPath);

        string tempOutputPath = HdiffService.CreateTempWorkPath(".fbx");
        try
        {
            HdiffService.ApplyXorEncryptedPatchToTempFbx(
                originalFbxPath,
                binPath,
                tempOutputPath,
                fileManagerService);

            if (!string.IsNullOrWhiteSpace(patchFile.outputHash))
            {
                string actualOutputHash = fileManagerService.CalculateFileHash(tempOutputPath);
                if (!string.Equals(actualOutputHash, patchFile.outputHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"HDiff apply failed integrity verification for '{Path.GetFileName(fbxPath)}'. Expected {patchFile.outputHash}, got {actualOutputHash ?? "<missing>"}.");
                }
            }

            fileManagerService.ReplaceFbxWithCustomCopy(fbxPath, tempOutputPath);
        }
        finally
        {
            if (File.Exists(tempOutputPath))
            {
                try
                {
                    File.Delete(tempOutputPath);
                }
                catch (Exception ex)
                {
                    MCBLogger.LogWarning($"[VersionActions] Failed to delete HDiff temp FBX '{tempOutputPath}': {ex.Message}");
                }
            }
        }
    }

    // A refit saves and restores fits per version: switching while one runs would mix two custom bases.
    private bool RefitBlocksSwitch()
    {
        if (editor == null || !MCBReFitIntegration.IsRefitRunning(editor.customBaseTarget)) return false;
        editor.warningsModule.AddWarning(MCBReFitIntegration.RefitRunningMessage, MessageType.Warning, "Refit running");
        editor.Repaint();
        return true;
    }

    /// <summary>
    /// Before an original-base backup is used: a base package re-imported while a version was applied replaces the stale
    /// backup (<see cref="FileManagerService.RefreshStaleOriginalBackup"/>).
    /// </summary>
    private void RefreshOriginalBackup(string fbxPath, params CustomBaseVersion[] versions)
    {
        if (string.IsNullOrWhiteSpace(fbxPath)) return;
        var known = versions.Append(editor?.customBaseTarget != null ? editor.customBaseTarget.appliedCustomBaseVersion : null)
            .Concat(editor?.GetAllVersions() ?? Enumerable.Empty<CustomBaseVersion>())
            .Where(value => value != null).Distinct().ToList();
        if (fileManagerService.RefreshStaleOriginalBackup(fbxPath, known) == FileManagerService.OriginalBackupState.Unknown)
            MCBLogger.LogWarning($"[VersionActions] {Path.GetFileName(fbxPath)} differs from its original-base backup and from every known version: the backup is used as the original.");
    }

    private string EnsureOriginalFbxKeyPath(CustomBaseVersion version, ModelFileData patchFile, string fbxPath, string operation)
    {
        RefreshOriginalBackup(fbxPath, version);
        var sourceFile = ResolveSourceFileForPatch(version, patchFile);
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.hash))
        {
            throw new InvalidDataException($"Apply failed: source FBX hash metadata is missing for {operation}.");
        }

        string originalFbxPath = FileManagerService.GetOriginalBasePath(fbxPath);
        if (!File.Exists(originalFbxPath))
        {
            string currentHash = fileManagerService.CalculateFileHash(fbxPath);
            bool currentMatchesReference = string.Equals(currentHash, sourceFile.hash, StringComparison.OrdinalIgnoreCase);
            if (!currentMatchesReference)
            {
                string sourceLabel = !string.IsNullOrWhiteSpace(sourceFile?.path)
                    ? sourceFile.path
                    : Path.GetFileName(fbxPath);
                throw new FileNotFoundException(
                    $"Apply failed: original FBX key file is missing for {operation}. Import the original/default FBX source for '{sourceLabel}' so MCB can create {Path.GetFileName(fbxPath)}{FileManagerService.OriginalBaseSuffix}.",
                    originalFbxPath);
            }

            fileManagerService.CreateBackup(fbxPath);
            originalFbxPath = FileManagerService.GetOriginalBasePath(fbxPath);
        }

        if (!File.Exists(originalFbxPath))
        {
            throw new FileNotFoundException($"Apply failed: original FBX key file could not be prepared for {operation}.", originalFbxPath);
        }

        string originalHash = fileManagerService.CalculateFileHash(originalFbxPath);
        if (!string.Equals(originalHash, sourceFile.hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Apply failed: original FBX key hash mismatch for '{sourceFile.path}'. Expected {sourceFile.hash}, got {originalHash ?? "<missing>"}.");
        }

        return originalFbxPath;
    }

    /// <summary>The original-model key of an XOR payload; null for a plain payload, which has none.</summary>
    private string ResolvePayloadKeyPath(CustomBaseVersion version, ModelFileData patchFile, string fbxPath, string operation)
    {
        return NativeMeshPayloadService.IsPlainPayloadTransform(patchFile?.transform)
            ? null
            : EnsureOriginalFbxKeyPath(version, patchFile, fbxPath, operation);
    }

    private void VerifyPatchSourceHash(CustomBaseVersion version, ModelFileData patchFile, string originalFbxPath)
    {
        ModelFileData sourceFile = ResolveSourceFileForPatch(version, patchFile);
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.hash))
        {
            throw new InvalidDataException("HDiff patch is missing source file hash metadata.");
        }

        string actualHash = fileManagerService.CalculateFileHash(originalFbxPath);
        if (!string.Equals(actualHash, sourceFile.hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"HDiff patch base FBX hash mismatch for '{sourceFile.path}'. Expected {sourceFile.hash}, got {actualHash ?? "<missing>"}.");
        }
    }

    private void ApplyXorBinToUnityAsset(CustomBaseVersion version, ModelFileData patchFile)
    {
        if (editor?.customBaseTarget == null)
        {
            throw new InvalidOperationException("Apply failed: no My Custom Base target is available.");
        }

        string targetFbxPath = ResolveTargetFbxPath(version, patchFile, GetCurrentFBXPath());
        if (string.IsNullOrWhiteSpace(targetFbxPath))
        {
            throw new FileNotFoundException("Apply failed: target FBX path for native mesh payload could not be resolved.");
        }

        string originalFbxPath = ResolvePayloadKeyPath(version, patchFile, targetFbxPath, "native mesh payload");
        string binPath = ResolveVersionPatchPath(version, patchFile);
        // Preparation has already materialized the payload cache; assign meshes/pose synchronously.
        NativeMeshPayloadService.ApplyEncryptedPayload(AvatarPaths.Root(editor.customBaseTarget), version, patchFile,
            binPath, originalFbxPath, fileManagerService, targetFbxPath);
    }

    private void RestoreBackupsForVersion(CustomBaseVersion version, string fallbackFbxPath, bool requireBackup = true)
    {
        if (requireBackup)
        {
            // Every model the version replaced needs its original; restoring only some would report a mixed avatar as reset.
            EnsureOriginalBackups(GetFbxImportPaths(version, null), fileManagerService.BackupExists);
        }

        var affectedPaths = GetResetAffectedFbxPaths(version, fallbackFbxPath);
        var pathsToRestore = affectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && fileManagerService.BackupExists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pathsToRestore.Count == 0)
        {
            if (requireBackup)
            {
                throw new FileNotFoundException("No original FBX backup was found for the selected custom base version.");
            }

            return;
        }

        foreach (string path in pathsToRestore)
        {
            RefreshOriginalBackup(path, version);
            fileManagerService.ForceRestoreBackupAtPath(path);
        }
    }

    internal static void EnsureOriginalBackups(IEnumerable<string> replacedFbxPaths, Func<string, bool> backupExists)
    {
        var missing = (replacedFbxPaths ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && !backupExists(path))
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"Reset stopped: the original backup ({FileManagerService.OriginalBaseSuffix}) is missing for {string.Join(", ", missing)}. " +
                "Nothing was restored. Re-import the original model files, then reset again.");
        }
    }

    private string ResolveTargetFbxPath(CustomBaseVersion version, ModelFileData patchFile, string fallbackFbxPath)
    {
        // A plain payload is not bound to an original model: it targets the avatar's current model.
        if (NativeMeshPayloadService.IsPlainPayloadTransform(patchFile?.transform))
        {
            return GetCurrentFBXPath();
        }

        var source = version.sourceFiles?.FirstOrDefault(file =>
            file != null &&
            patchFile.sourceModelFileId.HasValue &&
            file.id == patchFile.sourceModelFileId.Value);

        string sourcePath = source?.path;
        if (string.IsNullOrWhiteSpace(sourcePath) && patchFile.metadata != null)
        {
            if (patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue))
            {
                sourcePath = sourcePathValue?.ToString();
            }
        }
        if (string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(fallbackFbxPath))
        {
            throw new InvalidDataException("Model file patch is missing required sourcePath metadata.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        int sourceModelFileId = source != null
            ? source.id
            : (patchFile?.sourceModelFileId ?? 0);
        return AvatarPathOverrideService.ResolveLocalTargetPath(
            editor?.customBaseTarget,
            sourcePath,
            sourceModelFileId,
            source?.hash);
    }

    private string ResolveVersionPatchPath(CustomBaseVersion version, ModelFileData patchFile)
    {
        string versionFolder = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrWhiteSpace(versionFolder))
        {
            throw new DirectoryNotFoundException("Version data folder could not be resolved.");
        }

        string candidateName = string.IsNullOrWhiteSpace(patchFile.path)
            ? null
            : Path.GetFileName(patchFile.path);
        if (string.IsNullOrWhiteSpace(candidateName))
        {
            throw new InvalidDataException("Model file patch is missing required path metadata.");
        }

        string candidatePath = MCBUtils.CombineUnityPath(versionFolder, candidateName);
        if (!File.Exists(Path.GetFullPath(candidatePath)))
        {
            throw new FileNotFoundException("Version patch .bin file was not found.", candidatePath);
        }

        return candidatePath;
    }

    private void ApplyAdvancedMeshAuthoringPose(CustomBaseVersion version, string fallbackFbxPath)
    {
        if (editor?.customBaseTarget == null || !NativeMeshPayloadService.VersionUsesAdvancedMesh(version))
        {
            return;
        }

        var advancedPatchFiles = version.versionFiles?
            .Where(file => file != null &&
                           string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                           NativeMeshPayloadService.IsAdvancedMeshPatchTransform(file.transform))
            .ToArray();
        if (advancedPatchFiles == null || advancedPatchFiles.Length == 0)
        {
            throw new InvalidDataException("Advanced mesh version is missing required native mesh payload patches.");
        }

        Transform root = AvatarPaths.Root(editor.customBaseTarget);
        foreach (var patchFile in advancedPatchFiles)
        {
            string targetFbxPath = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
            if (string.IsNullOrWhiteSpace(targetFbxPath))
            {
                throw new FileNotFoundException("Advanced mesh authoring pose restore failed: target FBX path could not be resolved.");
            }

            var payload = NativeMeshPayloadService.MaterializeEncryptedPayloadAsset(
                version,
                patchFile,
                ResolveVersionPatchPath(version, patchFile),
                ResolvePayloadKeyPath(version, patchFile, targetFbxPath, "advanced mesh authoring pose"),
                fileManagerService);
            NativeMeshPayloadService.ApplyPayloadAuthoringPose(root, payload, targetFbxPath);
        }
    }

    private static string ResolveOriginalFbxKeyPath(string targetFbxPath)
    {
        string originalFbxPath = FileManagerService.GetOriginalBasePath(targetFbxPath);
        if (!File.Exists(originalFbxPath))
        {
            originalFbxPath = targetFbxPath;
        }
        if (!File.Exists(originalFbxPath))
        {
            throw new FileNotFoundException("Original FBX key file not found for native mesh payload.", originalFbxPath);
        }

        return originalFbxPath;
    }

    /// <summary>A model file a version puts on the avatar, located for reading (see <see cref="ResolveModelPatches"/>).</summary>
    public sealed class VersionModelPatch
    {
        public ModelFileData Patch;
        public ModelFileData Source;
        /// <summary>Advanced meshes (Unity meshes for the avatar's renderers) rather than a replacement FBX.</summary>
        public bool AdvancedMesh;
        public bool Hdiff;
        /// <summary>The avatar's model the patch replaces.</summary>
        public string TargetFbxPath;
        public string BinPath;
        /// <summary>The original model: the key of the patch, and what the avatar looks like with no version.</summary>
        public string OriginalFbxPath;
    }

    /// <summary>Whether a version ships meshes (a replacement FBX or advanced meshes), so its changes can be shown in 3D.</summary>
    public static bool ShipsMeshes(CustomBaseVersion version)
    {
        return version?.versionFiles != null && version.versionFiles.Any(file =>
            file != null &&
            string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
            (ModelFileTransforms.IsFbxReplacementTransform(file.transform) || NativeMeshPayloadService.IsAdvancedMeshPatchTransform(file.transform)));
    }

    /// <summary>
    /// The model patches of a downloaded version, located to read what it would put on the avatar. Unlike applying, this
    /// changes nothing: the original model must already be there (its .originalbase copy, or the model itself while no
    /// replacement is applied); readers check it against <see cref="VersionModelPatch.Source"/>'s hash.
    /// </summary>
    public List<VersionModelPatch> ResolveModelPatches(CustomBaseVersion version)
    {
        var patches = new List<VersionModelPatch>();
        string fallbackFbxPath = GetCurrentFBXPath();
        foreach (var patchFile in version?.versionFiles ?? Array.Empty<ModelFileData>())
        {
            if (patchFile == null || !string.Equals(patchFile.role, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool advanced = NativeMeshPayloadService.IsAdvancedMeshPatchTransform(patchFile.transform);
            if (!advanced && !ModelFileTransforms.IsFbxReplacementTransform(patchFile.transform))
            {
                continue;
            }

            string targetFbxPath = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
            if (string.IsNullOrWhiteSpace(targetFbxPath))
            {
                throw new FileNotFoundException($"The avatar model replaced by '{patchFile.path}' could not be found.");
            }

            patches.Add(new VersionModelPatch
            {
                Patch = patchFile,
                Source = ResolveSourceFileForPatch(version, patchFile),
                AdvancedMesh = advanced,
                Hdiff = ModelFileTransforms.IsHdiffFbxReplacementTransform(patchFile.transform),
                TargetFbxPath = targetFbxPath,
                BinPath = ResolveVersionPatchPath(version, patchFile),
                OriginalFbxPath = NativeMeshPayloadService.IsPlainPayloadTransform(patchFile.transform) ? null : ResolveOriginalFbxKeyPath(targetFbxPath)
            });
        }

        return patches;
    }

    private List<string> GetAffectedFbxPaths(CustomBaseVersion version, string fallbackFbxPath)
    {
        var paths = new List<string>();
        if (version?.versionFiles != null)
        {
            foreach (var patchFile in version.versionFiles)
            {
                if (patchFile == null) continue;
                if (string.IsNullOrWhiteSpace(patchFile.transform)) continue;
                string transform = patchFile.transform;
                if (!ModelFileTransforms.AffectsFbxPath(transform))
                {
                    continue;
                }

                if (NativeMeshPayloadService.IsPlainPayloadTransform(transform))
                {
                    paths.AddRange(ResolvePlainPayloadFbxPaths(version, patchFile));
                    continue;
                }

                string path = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
                if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
            }
        }

        if (paths.Count == 0 && !string.IsNullOrWhiteSpace(fallbackFbxPath))
        {
            paths.Add(fallbackFbxPath);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A plain payload names avatar renderers, not an original model: it changes the base models those renderers come
    /// from. They are read from the cached payload, else from the renderers carrying the version's meshes.
    /// </summary>
    private List<string> ResolvePlainPayloadFbxPaths(CustomBaseVersion version, ModelFileData patchFile)
    {
        var models = GetCurrentFBXPaths();
        var root = editor?.customBaseTarget != null ? AvatarPaths.Root(editor.customBaseTarget) : null;
        if (models.Count < 2 || root == null) return models.Take(1).ToList();
        var payload = NativeMeshPayloadService.FindCachedPayload(version, patchFile);
        var rendererPaths = payload != null
            ? payload.renderers.Where(record => record != null).Select(record => record.avatarPath).ToList()
            : NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(root, version)
                .Select(renderer => GetRelativeTransformPath(root, renderer.transform)).ToList();
        var sources = SmrPathService.ResolveSourceModels(root, rendererPaths, models);
        return sources.Count > 0 ? sources : models.Take(1).ToList();
    }

    /// <summary>The models whose importer an FBX version points at its own Avatar (advanced versions set the Animator only).</summary>
    private List<string> GetCustomAvatarFbxPaths(CustomBaseVersion version)
    {
        var paths = new List<string>();
        if (version?.versionFiles == null || NativeMeshPayloadService.VersionUsesAdvancedMesh(version)) return paths;
        foreach (var patchFile in version.versionFiles)
        {
            if (patchFile?.metadata == null || !patchFile.metadata.TryGetValue("customAvatarPath", out object avatarPath) ||
                string.IsNullOrWhiteSpace(avatarPath?.ToString())) continue;
            string path = ResolveTargetFbxPath(version, patchFile, null);
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(MCBUtils.ToUnityPath(path));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// An FBX version patches its models from their originals, so switching from another FBX version puts back what that
    /// version changed and the next one does not: model bytes, and import settings of models the next one gives no Avatar.
    /// </summary>
    private List<string> RestoreModelsLeftByPreviousVersion(Transform root, CustomBaseVersion previousVersion, CustomBaseVersion nextVersion, string fbxPath)
    {
        var patchedNext = new HashSet<string>(GetFbxImportPaths(nextVersion, fbxPath), StringComparer.OrdinalIgnoreCase);
        var avatarNext = new HashSet<string>(GetCustomAvatarFbxPaths(nextVersion), StringComparer.OrdinalIgnoreCase);
        var restored = new List<string>();
        foreach (string path in GetResetAffectedFbxPaths(previousVersion, fbxPath).Union(GetCustomAvatarFbxPaths(previousVersion), StringComparer.OrdinalIgnoreCase))
        {
            bool changed = false;
            if (!patchedNext.Contains(path) && fileManagerService.BackupExists(path) && !fileManagerService.FbxMatchesBackupAtPath(path))
            {
                RefreshOriginalBackup(path, previousVersion, nextVersion);
                if (fileManagerService.FbxMatchesBackupAtPath(path)) continue;
                fileManagerService.ForceRestoreBackupAtPath(path);
                changed = true;
            }
            if (!avatarNext.Contains(path) && RestoreOriginalImporter(root, previousVersion, path)) changed = true;
            if (changed) restored.Add(path);
        }
        return restored;
    }

    /// <summary>Puts back the import settings of the models the version changed; other models are left as they are.</summary>
    private void RestoreImportersForReset(Transform root, CustomBaseVersion version, string fbxPath)
    {
        foreach (string path in GetResetAffectedFbxPaths(version, fbxPath).Union(GetCustomAvatarFbxPaths(version), StringComparer.OrdinalIgnoreCase))
        {
            RestoreOriginalImporter(root, version, path);
        }
    }

    /// <summary>
    /// The model's import settings from before the version changed them, and its Avatar back on the Animator when the
    /// version had replaced it. Without saved settings, a model the version gave its own Avatar gets the version's default
    /// Avatar instead. False when nothing was restored.
    /// </summary>
    private bool RestoreOriginalImporter(Transform root, CustomBaseVersion version, string fbxPath)
    {
        bool versionAvatar = GetCustomAvatarFbxPaths(version).Contains(fbxPath, StringComparer.OrdinalIgnoreCase);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (AvatarDefinitionGenerationService.RestoreOriginalImportSettings(fbxPath))
        {
            model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var animator = model != null ? model.GetComponent<Animator>() : null;
            if (versionAvatar && animator != null && animator.avatar != null)
                AvatarDefinitionGenerationService.SetRootAnimatorAvatar(root, animator.avatar);
            return true;
        }
        if (!versionAvatar || model == null) return false;

        string defaultAvatarPath = ResolveDefaultAvatarPathForReset(version);
        if (string.IsNullOrWhiteSpace(defaultAvatarPath))
        {
            MCBLogger.LogWarning($"[VersionActions] '{fbxPath}' keeps the version's Avatar: neither its original import settings nor a default avatar.asset were found.");
            return false;
        }
        MCBLogger.Log($"[VersionActions] Restoring default avatar import settings from {defaultAvatarPath} to {fbxPath}");
        fileManagerService.ApplyAvatarToModel(root, model, defaultAvatarPath);
        return true;
    }

    private void WarnSkippedRenderers(List<string> missingRenderers)
    {
        if (missingRenderers == null || missingRenderers.Count == 0) return;
        string message = "These renderers the version replaced are no longer on the avatar (deleted or renamed) and were skipped: " +
                         string.Join(", ", missingRenderers.Distinct(StringComparer.Ordinal));
        MCBLogger.LogWarning("[VersionActions] " + message);
        editor.warningsModule?.AddWarning(message, MessageType.Warning, "Renderers skipped");
    }

    private List<string> GetResetAffectedFbxPaths(CustomBaseVersion version, string fallbackFbxPath)
    {
        var paths = GetAffectedFbxPaths(version, null);
        if (paths.Count == 0)
        {
            paths = GetCurrentFBXPaths()
                .Where(path => fileManagerService.BackupExists(path))
                .ToList();
        }

        if (paths.Count == 0 && !string.IsNullOrWhiteSpace(fallbackFbxPath))
        {
            paths.Add(fallbackFbxPath);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> GetFbxImportPaths(CustomBaseVersion version, string fallbackFbxPath)
    {
        var paths = new List<string>();
        if (version?.versionFiles != null)
        {
            foreach (var patchFile in version.versionFiles)
            {
                if (patchFile == null) continue;
                if (!ModelFileTransforms.IsFbxReplacementTransform(patchFile.transform)) continue;
                string path = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
                if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ApplyAvatarImportsForVersion(Transform root, CustomBaseVersion version, bool isReset)
    {
        if (isReset)
        {
            RestoreImportersForReset(root, version, null);
            return;
        }

        foreach (var patchFile in version.versionFiles ?? Array.Empty<ModelFileData>())
        {
            string avatarPath = null;
            if (patchFile?.metadata != null && patchFile.metadata.TryGetValue("customAvatarPath", out object avatarPathValue))
            {
                avatarPath = avatarPathValue?.ToString();
            }

            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                avatarPath = MCBUtils.GetVersionAvatarPath(version, Path.GetFileName(avatarPath));
            }

            if (string.IsNullOrWhiteSpace(avatarPath))
            {
                MCBLogger.Log($"[VersionActions] Patch file {patchFile?.path} has no custom avatar. Skipping avatar import.");
                continue;
            }

            string sourcePath = ResolveTargetFbxPath(version, patchFile, null);
            var fbxGameObject = string.IsNullOrWhiteSpace(sourcePath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (NativeMeshPayloadService.VersionUsesAdvancedMesh(version))
            {
                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(MCBUtils.ToUnityPath(avatarPath));
                if (avatar != null)
                {
                    MCBLogger.Log($"[VersionActions] Applying native mesh Avatar directly from {avatarPath}.");
                    AvatarDefinitionGenerationService.SetRootAnimatorAvatar(root, avatar);
                }
                else
                {
                    MCBLogger.LogWarning($"[VersionActions] Could not load native mesh Avatar at {avatarPath}.");
                }
                continue;
            }

            MCBLogger.Log($"[VersionActions] Applying avatar import settings from {avatarPath} to {sourcePath}");
            fileManagerService.ApplyAvatarToModel(root, fbxGameObject, avatarPath);
        }
    }

    private void ApplyDefaultAvatarToRootForReset(Transform root, CustomBaseVersion resetFromVersion)
    {
        // A bundled custom version can support originals with different humanoid rigs.
        // Restore the actual local source definition, rather than the build's primary original.
        foreach (string source in GetResetAffectedFbxPaths(resetFromVersion, null))
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(source);
            var originalAnimator = model != null ? model.GetComponent<Animator>() : null;
            if (originalAnimator != null && originalAnimator.avatar != null)
            {
                AvatarDefinitionGenerationService.SetRootAnimatorAvatar(root, originalAnimator.avatar);
                return;
            }
        }
        string defaultAvatarPath = ResolveDefaultAvatarPathForReset(resetFromVersion);
        if (string.IsNullOrWhiteSpace(defaultAvatarPath))
        {
            MCBLogger.LogWarning("[VersionActions] Reset restored native mesh data, but no default avatar.asset could be resolved for Animator reset.");
            return;
        }

        var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(MCBUtils.ToUnityPath(defaultAvatarPath));
        if (avatar == null)
        {
            MCBLogger.LogWarning($"[VersionActions] Could not load default Avatar at {defaultAvatarPath}.");
            return;
        }

        AvatarDefinitionGenerationService.SetRootAnimatorAvatar(root, avatar);
    }

    private string ResolveDefaultAvatarPathForReset(CustomBaseVersion resetFromVersion)
    {
        foreach (var version in GetDefaultAvatarCandidateVersions(resetFromVersion))
        {
            string path = MCBUtils.GetDefaultAvatarPath(version);
            if (AssetExists(path))
            {
                return MCBUtils.ToUnityPath(path);
            }
        }

        int selectedAssetId = editor.GetSelectedAsset()?.id ?? 0;
        if (selectedAssetId > 0)
        {
            string assetVersionsRoot = MCBUtils.ToUnityPath($"{MCBUtils.ASSET_VERSIONS_FOLDER}/{selectedAssetId}/versions");
            string fullRoot = Path.GetFullPath(assetVersionsRoot);
            if (Directory.Exists(fullRoot))
            {
                string defaultAvatarName = MCBUtils.DEFAULT_AVATAR_NAME;
                string found = Directory.GetFiles(fullRoot, defaultAvatarName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(MCBUtils.ToUnityPath)
                    .FirstOrDefault(AssetExists);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private IEnumerable<CustomBaseVersion> GetDefaultAvatarCandidateVersions(CustomBaseVersion resetFromVersion)
    {
        if (resetFromVersion != null && resetFromVersion != VersionListDrawer.RESET_VERSION)
        {
            yield return resetFromVersion;
        }

        var applied = editor.customBaseTarget != null ? editor.customBaseTarget.appliedCustomBaseVersion : null;
        if (applied != null && applied != resetFromVersion && applied != VersionListDrawer.RESET_VERSION)
        {
            yield return applied;
        }

        if (editor.selectedVersionForAction != null &&
            editor.selectedVersionForAction != resetFromVersion &&
            editor.selectedVersionForAction != VersionListDrawer.RESET_VERSION)
        {
            yield return editor.selectedVersionForAction;
        }

        if (editor.recommendedVersion != null &&
            editor.recommendedVersion != resetFromVersion &&
            editor.recommendedVersion != VersionListDrawer.RESET_VERSION)
        {
            yield return editor.recommendedVersion;
        }

        foreach (var version in editor.GetAllVersions() ?? new List<CustomBaseVersion>())
        {
            if (version != null && version != resetFromVersion && version != VersionListDrawer.RESET_VERSION)
            {
                yield return version;
            }
        }
    }

    private static bool AssetExists(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath))
        {
            return false;
        }

        string normalizedPath = MCBUtils.ToUnityPath(unityPath);
        return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalizedPath) != null ||
               File.Exists(Path.GetFullPath(normalizedPath));
    }
    
    private Dictionary<string, Dictionary<string, float>> CaptureBlendshapeState(Transform root)
    {
        var snapshot = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
        if (root == null) return snapshot;

        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.sharedMesh == null) continue;

            var weights = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                string blendshapeName = renderer.sharedMesh.GetBlendShapeName(i);
                if (string.IsNullOrEmpty(blendshapeName)) continue;
                weights[blendshapeName] = renderer.GetBlendShapeWeight(i);
            }

            snapshot[GetRelativeTransformPath(root, renderer.transform)] = weights;
        }

        return snapshot;
    }

    private void RestoreBlendshapeState(Transform root, Dictionary<string, Dictionary<string, float>> snapshot, Dictionary<string, float> defaultValuesByName)
    {
        if (root == null) return;

        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.sharedMesh == null) continue;

            string rendererPath = GetRelativeTransformPath(root, renderer.transform);
            snapshot.TryGetValue(rendererPath, out var savedWeightsForRenderer);

            Undo.RecordObject(renderer, "Restore Blendshape Values");
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                string blendshapeName = renderer.sharedMesh.GetBlendShapeName(i);
                float valueToApply = 0f;

                if (!string.IsNullOrEmpty(blendshapeName))
                {
                    if (savedWeightsForRenderer != null && savedWeightsForRenderer.TryGetValue(blendshapeName, out var savedValue))
                    {
                        valueToApply = savedValue;
                    }
                    else if (defaultValuesByName != null && defaultValuesByName.TryGetValue(blendshapeName, out var defaultValue))
                    {
                        valueToApply = defaultValue;
                    }
                }

                renderer.SetBlendShapeWeight(i, valueToApply);
            }

            EditorUtility.SetDirty(renderer);
        }
    }

    private Dictionary<string, float> BuildBlendshapeDefaultLookup(CustomBaseVersion version, Dictionary<string, float> remembered = null)
    {
        var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        if (version?.customBlendshapes == null) return defaults;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null || string.IsNullOrEmpty(entry.name)) continue;
            defaults[entry.name] = remembered != null && remembered.TryGetValue(entry.name, out float value)
                ? value : ParseBlendshapeDefaultValue(entry.defaultValue);
        }

        return defaults;
    }

    /// <summary>Saves the applied version's exposed blendshape values for this asset before the avatar returns to its original base.</summary>
    private void RememberVersionBlendshapes(Transform root, CustomBaseVersion version)
    {
        var owner = editor.customBaseTarget;
        if (root == null || owner == null || version?.customBlendshapes == null || version.assetId <= 0) return;
        var renderers = GetTargetBlendshapeRenderers(root).ToList();
        var memory = new BlendshapeMemory { assetId = version.assetId };
        foreach (var entry in version.customBlendshapes.Where(e => e != null && !string.IsNullOrEmpty(e.name)))
        {
            var renderer = renderers.OrderByDescending(r => r.name == "Body").FirstOrDefault(r => r.sharedMesh.GetBlendShapeIndex(entry.name) >= 0);
            if (renderer == null) continue;
            memory.names.Add(entry.name);
            memory.values.Add(renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(entry.name)));
        }
        if (memory.names.Count == 0) return;
        Undo.RecordObject(owner, "Remember custom base blendshapes");
        owner.blendshapeMemory.RemoveAll(m => m.assetId == version.assetId);
        owner.blendshapeMemory.Add(memory);
        EditorUtility.SetDirty(owner);
    }

    private Dictionary<string, float> RememberedBlendshapes(CustomBaseVersion version)
    {
        var memory = editor.customBaseTarget?.blendshapeMemory.FirstOrDefault(m => version != null && m.assetId == version.assetId);
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int i = 0; memory != null && i < memory.names.Count && i < memory.values.Count; i++) result[memory.names[i]] = memory.values[i];
        return result;
    }

    private void SyncBlendshapeOverridesFromCurrentWeights(Transform root, CustomBaseVersion version)
    {
        editor.customBaseTarget.customBlendshapeOverrideNames.Clear();
        editor.customBaseTarget.customBlendshapeOverrideValues.Clear();
        editor.customBaseTarget.blendShapeValues.Clear();

        if (root == null || version?.customBlendshapes == null) return;

        var renderers = GetTargetBlendshapeRenderers(root).ToList();
        if (renderers.Count == 0) return;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null)
            {
                editor.customBaseTarget.blendShapeValues.Add(0f);
                continue;
            }

            float defaultValue = ParseBlendshapeDefaultValue(entry.defaultValue);
            float currentValue = defaultValue;
            foreach (var renderer in renderers)
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(entry.name);
                if (index >= 0)
                {
                    currentValue = renderer.GetBlendShapeWeight(index);
                    break;
                }
            }

            editor.customBaseTarget.blendShapeValues.Add(currentValue);

            if (Mathf.Abs(currentValue - defaultValue) > BlendshapeWeightEpsilon)
            {
                editor.customBaseTarget.customBlendshapeOverrideNames.Add(entry.name);
                editor.customBaseTarget.customBlendshapeOverrideValues.Add(currentValue);
            }
        }
    }

    private void ApplyVersionBlendshapeValues(Transform root, CustomBaseVersion version)
    {
        editor.customBaseTarget.blendShapeValues.Clear();

        if (root == null || version?.customBlendshapes == null || version.customBlendshapes.Length == 0) return;

        var renderers = GetTargetBlendshapeRenderers(root).ToList();
        if (renderers.Count == 0) return;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null)
            {
                editor.customBaseTarget.blendShapeValues.Add(0f);
                continue;
            }

            float defaultValue = ParseBlendshapeDefaultValue(entry.defaultValue);
            float valueToApply = RememberedBlendshapes(version).TryGetValue(entry.name, out float remembered) ? remembered : defaultValue;
            int overrideIdx = editor.customBaseTarget.customBlendshapeOverrideNames.IndexOf(entry.name);
            if (overrideIdx >= 0 && overrideIdx < editor.customBaseTarget.customBlendshapeOverrideValues.Count)
            {
                valueToApply = editor.customBaseTarget.customBlendshapeOverrideValues[overrideIdx];
            }

            MCBReFitIntegration.ApplyBlendShapeWeightWithTransferredReFit(
                editor.customBaseTarget,
                renderers,
                entry.name,
                valueToApply);

            editor.customBaseTarget.blendShapeValues.Add(valueToApply);
        }

    }

    private void SyncReFitTransferredBlendshapeWeights(Transform root, CustomBaseVersion version)
    {
        if (root == null || version?.customBlendshapes == null || version.customBlendshapes.Length == 0) return;

        var renderers = GetTargetBlendshapeRenderers(root).ToList();
        if (renderers.Count == 0) return;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null || string.IsNullOrEmpty(entry.name)) continue;
            if (!TryGetCurrentBlendshapeWeight(renderers, entry.name, out float currentWeight)) continue;
            MCBReFitIntegration.ApplyBlendShapeWeightWithTransferredReFit(
                editor.customBaseTarget,
                renderers,
                entry.name,
                currentWeight);
        }
    }

    private void RestoreOriginalFbxStateForTransition(
        Transform root,
        IEnumerable<CustomBaseVersion> transitionVersions,
        string fallbackFbxPath,
        bool restoreImporter = true,
        CustomBaseVersion preserveVersion = null)
    {
        if (root == null)
        {
            return;
        }

        var versions = (transitionVersions ?? Enumerable.Empty<CustomBaseVersion>())
            .Where(value => value != null)
            .ToList();
        if (versions.Count == 0) return;

        var affectedPathsByVersion = versions.ToDictionary(
            value => value,
            value => GetResetAffectedFbxPaths(value, fallbackFbxPath));
        var affectedPaths = affectedPathsByVersion.Values
            .SelectMany(value => value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string path in affectedPaths.Where(fileManagerService.BackupExists))
        {
            RefreshOriginalBackup(path, versions.ToArray());
            fileManagerService.ForceRestoreBackupAtPath(path);
        }

        // Native versions assign the Animator Avatar directly. Repointing the FBX
        // importer to another identical per-version Avatar copy forces an FBX import.
        // Only an actual transition from an FBX-based version needs importer repair.
        if (restoreImporter)
        {
            foreach (var transitionVersion in versions) RestoreImportersForReset(root, transitionVersion, fallbackFbxPath);
        }
        string canonicalFbxPath = !string.IsNullOrWhiteSpace(fallbackFbxPath)
            ? MCBUtils.ToUnityPath(fallbackFbxPath)
            : affectedPaths.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(canonicalFbxPath))
        {
            int restoredTransforms = SmrPathService.RestoreTargetTransformHierarchyFromFbx(root, canonicalFbxPath);
            if (restoredTransforms == 0)
            {
                throw new InvalidOperationException(
                    $"Advanced mesh transition could not safely restore the canonical FBX armature pose from '{canonicalFbxPath}'.");
            }
        }
        // After the skeleton is final: the Animator caches the bones it binds.
        ApplyDefaultAvatarToRootForReset(root, versions.LastOrDefault());

        int restoredRenderers = 0;
        var missingRenderers = new List<string>();
        foreach (var transitionVersion in versions)
        {
            restoredRenderers += NativeMeshPayloadService.RestoreOriginalMeshesFromFbx(
                root,
                transitionVersion,
                affectedPathsByVersion[transitionVersion],
                editor.customBaseTarget, preserveVersion, missingRenderers);
        }

        if (restoredRenderers == 0 && missingRenderers.Count == 0)
        {
            throw new InvalidOperationException("Advanced mesh transition could not safely restore any renderer from the original FBX state.");
        }
        WarnSkippedRenderers(missingRenderers);
    }

    private static List<CustomBaseVersion> GetDistinctTransitionVersions(params CustomBaseVersion[] versions)
    {
        return (versions ?? Array.Empty<CustomBaseVersion>())
            .Where(value => value != null && value != VersionListDrawer.RESET_VERSION)
            .GroupBy(
                value => $"{value.assetId}:{value.version}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private List<string> GetTransitionAffectedFbxPaths(
        IEnumerable<CustomBaseVersion> transitionVersions,
        string fallbackFbxPath)
    {
        var paths = (transitionVersions ?? Enumerable.Empty<CustomBaseVersion>())
            .Where(value => value != null)
            .SelectMany(value => GetResetAffectedFbxPaths(value, fallbackFbxPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(MCBUtils.ToUnityPath)
            .ToList();
        paths.AddRange(GetCurrentFBXPaths()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(MCBUtils.ToUnityPath));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryGetCurrentBlendshapeWeight(IEnumerable<SkinnedMeshRenderer> renderers, string blendshapeName, out float weight)
    {
        weight = 0f;
        if (renderers == null || string.IsNullOrEmpty(blendshapeName)) return false;

        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.sharedMesh == null) continue;
            int index = renderer.sharedMesh.GetBlendShapeIndex(blendshapeName);
            if (index < 0) continue;
            weight = renderer.GetBlendShapeWeight(index);
            return true;
        }

        return false;
    }

    private IEnumerable<SkinnedMeshRenderer> GetTargetBlendshapeRenderers(Transform root)
    {
        if (root == null) yield break;

        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer?.sharedMesh == null || renderer.sharedMesh.blendShapeCount == 0) continue;
            yield return renderer;
        }
    }

    private static float ParseBlendshapeDefaultValue(string defaultValue)
    {
        return float.TryParse(defaultValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : 0f;
    }

    private static string GetRelativeTransformPath(Transform root, Transform target)
    {
        if (root == null || target == null) return string.Empty;
        if (target == root) return string.Empty;

        var segments = new List<string>();
        var current = target;
        while (current != null && current != root)
        {
            segments.Add(current.name);
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private void AutoSaveProjectAfterVersionSwitch()
    {
        try
        {
            AssetDatabase.SaveAssets();
            bool savedScenes = EditorSceneManager.SaveOpenScenes();
            if (!savedScenes)
            {
                MCBLogger.LogWarning("[VersionActions] Auto-save completed for assets, but one or more open scenes could not be saved.");
            }
            else
            {
                MCBLogger.Log("[VersionActions] Auto-saved assets and open scenes after version change.");
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[VersionActions] Auto-save after version change failed: {ex.Message}");
            editor.warningsModule.AddWarning("The version switch succeeded, but MCB could not auto-save the project. Save the project manually to persist the scene state.", MessageType.Warning, "Auto-save failed");
        }
    }

    private void RefreshTargetMeshesFromFBXs(Transform root, IEnumerable<string> fbxPaths, CustomBaseVersion version)
    {
        if (root == null || fbxPaths == null) return;

        var targetPaths = new HashSet<string>(
            fbxPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(MCBUtils.ToUnityPath),
            StringComparer.OrdinalIgnoreCase);
        if (targetPaths.Count == 0) return;

        bool restoredEverySource = true;
        var missingRenderers = new List<string>();
        foreach (string fbxPath in targetPaths)
        {
            var smrPaths = SmrPathService.ResolveSmrPathsForSource(version, fbxPath, editor?.customBaseTarget);
            var missing = new List<string>();
            int restored = SmrPathService.RestoreTargetStateFromFbx(root, fbxPath, smrPaths, skipMissingRenderers: true, missingRenderers: missing);
            missingRenderers.AddRange(missing);
            if (restored == 0 && missing.Count == 0)
            {
                restoredEverySource = false;
                if (activeTransitionRollback != null)
                {
                    throw new InvalidOperationException(
                        $"No renderer could be safely refreshed from '{fbxPath}' during the advanced mesh transition.");
                }
                MCBLogger.LogWarning($"[VersionActions] No renderer could be safely refreshed from '{fbxPath}'. The existing mesh and bone bindings were left together unchanged.");
            }
        }

        WarnSkippedRenderers(missingRenderers);
        if (!restoredEverySource) return;

        string canonicalFbxPath = GetCurrentFBXPath();
        if (string.IsNullOrWhiteSpace(canonicalFbxPath))
        {
            canonicalFbxPath = targetPaths.First();
        }
        int restoredTransforms = SmrPathService.RestoreTargetTransformHierarchyFromFbx(root, canonicalFbxPath);
        if (restoredTransforms == 0)
        {
            if (activeTransitionRollback != null)
            {
                throw new InvalidOperationException(
                    $"No avatar transforms could be safely restored from the canonical FBX '{canonicalFbxPath}' during the advanced mesh transition.");
            }
            MCBLogger.LogWarning($"[VersionActions] No avatar transforms were restored from the canonical FBX '{canonicalFbxPath}'.");
        }
    }

    public void UpdateCurrentBaseFbxHash()
    {
        var paths = GetCurrentFBXPaths();
        string path = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(path))
        {
            editor.currentBaseFbxHash = null;
            editor.currentAppliedFbxHash = null;
            editor.currentIsCustom = false;
            UpdateAppliedVersionAndState(null);
            return;
        }

        // Try to get cached hashes first (non-blocking)
        var hashService = AsyncHashService.Instance;
        string cachedCurrentHash = hashService.GetHashIfCached(path);
        bool allCurrentHashesCached = paths.All(targetPath => !string.IsNullOrEmpty(hashService.GetHashIfCached(targetPath)));
        
        string originalPath = path + FileManagerService.OriginalSuffix;
        string cachedOriginalHash = null;
        bool hasBackup = fileManagerService.BackupExists(path);
        
        if (hasBackup)
        {
            cachedOriginalHash = hashService.GetHashIfCached(originalPath);
        }

        // Use cached hashes if available, otherwise start async calculation
        if (cachedCurrentHash != null && allCurrentHashesCached && (!hasBackup || cachedOriginalHash != null))
        {
            // We have all needed cached hashes - use them immediately
            editor.currentAppliedFbxHash = cachedCurrentHash;
            editor.currentBaseFbxHash = hasBackup ? cachedOriginalHash : cachedCurrentHash;
            UpdateAppliedVersionAndState(cachedCurrentHash);
        }
        else
        {
            // Missing cache - start async hash calculation and use placeholder for now
            editor.currentBaseFbxHash = null; // Will be updated when async calculation completes
            var requestedTarget = editor.target;
            int? requestedAssetId = editor.GetSelectedAsset()?.id;
            
            // Start async hash calculation in background
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    foreach (string targetPath in paths)
                    {
                        await hashService.CalculateFileHashAsync(targetPath, null, true);
                    }

                    string currentHash = await hashService.CalculateFileHashAsync(path, null, true);
                    string originalHash = null;
                    
                    if (hasBackup)
                    {
                        originalHash = await hashService.CalculateFileHashAsync(originalPath, null, true);
                    }
                    
                    // Update on main thread when calculation completes
                    AsyncTaskManager.Instance.ExecuteOnMainThread(() =>
                    {
                        if (editor == null || editor.target != requestedTarget ||
                            editor.GetSelectedAsset()?.id != requestedAssetId ||
                            !GetCurrentFBXPaths().SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) return;
                        editor.currentAppliedFbxHash = currentHash;
                        editor.currentBaseFbxHash = hasBackup ? originalHash : currentHash;
                        UpdateAppliedVersionAndState(currentHash);
                        editor.Repaint();
                    });
                }
                catch (System.Exception ex)
                {
                    MCBLogger.LogError($"[VersionActions] Async hash calculation failed: {ex.Message}");
                }
            });
        }
    }
    
    private IEnumerator RecalculateCurrentFbxHashCoroutine(
        int expectedHashGeneration,
        int? expectedApplyGeneration)
    {
        var profile = new System.Diagnostics.Stopwatch();
        var step = new System.Diagnostics.Stopwatch();
        profile.Start();
        step.Start();
        MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation START");

        var paths = GetCurrentFBXPaths();
        string path = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(path))
        {
            MCBLogger.Log($"[VersionApplyProfile] Async hash/state recalculation ABORT missing FBX path total={profile.Elapsed.TotalMilliseconds:F1} ms");
            yield break;
        }

        var hashService = AsyncHashService.Instance;

        // Invalidate caches for current and backup FBX files
        foreach (string targetPath in paths)
        {
            hashService.InvalidateHashCache(targetPath);
        }
        string originalPath = path + FileManagerService.OriginalSuffix;
        bool hasBackup = File.Exists(originalPath);
        if (hasBackup)
        {
            hashService.InvalidateHashCache(originalPath);
        }
        MCBLogger.Log($"[VersionApplyProfile] Invalidated FBX hash cache: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms paths={paths.Count}");
        step.Restart();

        // Ensure any imports/updates are finished
        while (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            yield return null;
        }
        MCBLogger.Log($"[VersionApplyProfile] Waited for editor import/update before hashing: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        // Calculate hashes
        var currentHashTask = hashService.CalculateFileHashFreshAsync(Path.GetFullPath(path));
        var originalHashTask = hasBackup
            ? hashService.CalculateFileHashFreshAsync(Path.GetFullPath(originalPath))
            : null;
        while (!currentHashTask.IsCompleted || (originalHashTask != null && !originalHashTask.IsCompleted))
        {
            if (fbxHashRecalculationGeneration != expectedHashGeneration ||
                (expectedApplyGeneration.HasValue && applyProgressGeneration != expectedApplyGeneration.Value))
            {
                MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT superseded by a newer apply generation.");
                yield break;
            }
            yield return null;
        }
        string currentHash = currentHashTask.Result;
        string originalHash = originalHashTask != null ? originalHashTask.Result : null;
        if (string.IsNullOrWhiteSpace(currentHash) || (hasBackup && string.IsNullOrWhiteSpace(originalHash)))
        {
            MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT stale or unavailable hash result.");
            yield break;
        }
        MCBLogger.Log($"[VersionApplyProfile] Calculated primary FBX/current+backup hashes: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        foreach (string targetPath in paths.Skip(1))
        {
            var targetHashTask = hashService.CalculateFileHashFreshAsync(targetPath);
            while (!targetHashTask.IsCompleted)
            {
                if (fbxHashRecalculationGeneration != expectedHashGeneration ||
                    (expectedApplyGeneration.HasValue && applyProgressGeneration != expectedApplyGeneration.Value))
                {
                    MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT superseded by a newer apply generation.");
                    yield break;
                }
                yield return null;
            }
            if (string.IsNullOrWhiteSpace(targetHashTask.Result))
            {
                MCBLogger.Log($"[VersionApplyProfile] Async hash/state recalculation ABORT stale or unavailable hash for '{targetPath}'.");
                yield break;
            }
        }
        MCBLogger.Log($"[VersionApplyProfile] Calculated additional FBX hashes: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        // Update editor state
        if (fbxHashRecalculationGeneration != expectedHashGeneration ||
            (expectedApplyGeneration.HasValue && applyProgressGeneration != expectedApplyGeneration.Value))
        {
            MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT superseded before state update.");
            yield break;
        }

        editor.currentBaseFbxHash = hasBackup ? originalHash : currentHash;
        UpdateAppliedVersionAndState(currentHash);
        editor.Repaint();
        MCBLogger.Log($"[VersionApplyProfile] Async hash/state recalculation DONE: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
    }
    
    // FIX: Centralized and corrected state detection logic. This is the single source of truth.
    public void UpdateAppliedVersionAndState(string currentFileHash = null)
    {
        if (currentFileHash == null)
        {
            string path = GetCurrentFBXPath();
            if (!string.IsNullOrEmpty(path))
            {
                var hashService = AsyncHashService.Instance;
                currentFileHash = hashService.GetHashIfCached(path);
            }

            if (string.IsNullOrEmpty(currentFileHash))
            {
                var pendingMarkerVersion = ResolvePersistedAppliedVersion();
                if (pendingMarkerVersion != null && IsAdvancedMeshVersionApplied(pendingMarkerVersion))
                {
                    var previousVersion = editor.customBaseTarget.appliedCustomBaseVersion;
                    if (PersistAppliedVersionState(pendingMarkerVersion, true) || !Equals(previousVersion, pendingMarkerVersion))
                        MCBLogger.Log($"[VersionActions] Keeping applied native mesh version from persisted state before hash is ready: {pendingMarkerVersion.version}");
                    return;
                }

                MCBLogger.Log("[VersionActions] UpdateAppliedVersionAndState deferred (hash not cached yet).");
                return;
            }
        }

        // Track applied hash consistently
        editor.currentAppliedFbxHash = currentFileHash;

        var candidateVersions = editor.GetAllVersions() ?? new System.Collections.Generic.List<CustomBaseVersion>();

        // If we have no candidates yet, don't decide custom state prematurely
        if (string.IsNullOrEmpty(currentFileHash) || (!editor.fetchAttempted && candidateVersions.Count == 0))
        {
            MCBLogger.Log("[VersionActions] Deferring state detection (waiting for versions).\n" +
                              $"fetchAttempted={editor.fetchAttempted}, candidates={candidateVersions.Count}");
            return;
        }

        if (editor?.customBaseTarget == null) return;
        // This runs on every inspector rebuild: the custom base is recorded for undo and marked dirty only when one of its
        // values changes, and the outcome is logged only when it changes.
        var target = editor.customBaseTarget;
        var previous = target.appliedCustomBaseVersion;

        var markerVersion = ResolvePersistedAppliedVersion(candidateVersions);
        if (markerVersion != null && IsAdvancedMeshVersionApplied(markerVersion))
        {
            if (PersistAppliedVersionState(markerVersion, true) || !Equals(previous, markerVersion))
                MCBLogger.Log($"[VersionActions] Keeping applied native mesh version from persisted state: {markerVersion.version}");
            return;
        }

        var matchingVersion = FindMatchingAppliedVersion(candidateVersions, currentFileHash);
        bool changed;

        if (matchingVersion != null)
        {
            editor.isCustomBase = true;
            editor.currentIsCustom = false;
            var links = BuildAppliedVersionBlendshapeLinkCache(matchingVersion);
            var offsets = BuildAppliedVersionAnimationPositionOffsetCache(matchingVersion);
            // The applied version is saved with the custom base: another version, or the same one with new contents, counts.
            changed = JsonUtility.ToJson(previous) != JsonUtility.ToJson(matchingVersion) ||
                      !SameSerializedEntries(target.appliedVersionBlendshapeLinksCache, links) ||
                      !SameSerializedEntries(target.appliedVersionAnimationPositionOffsetsCache, offsets);
            if (changed)
            {
                Undo.RecordObject(target, UpdateStateUndoName);
                SetCache(ref target.appliedVersionBlendshapeLinksCache, links);
                SetCache(ref target.appliedVersionAnimationPositionOffsetsCache, offsets);
                MCBLogger.Log($"[VersionActions] Matched applied version: {matchingVersion.version}");
            }
            target.appliedCustomBaseVersion = matchingVersion;
        }
        else
        {
            editor.isCustomBase = false;
            changed = previous != null || target.appliedVersionAnimationPositionOffsetsCache?.Count > 0;
            if (changed)
            {
                Undo.RecordObject(target, UpdateStateUndoName);
                target.appliedVersionAnimationPositionOffsetsCache?.Clear();
            }
            target.appliedCustomBaseVersion = null;

            // Detect user-custom base only when feature is enabled and we have attempted fetching versions
            string fbxPath = GetCurrentFBXPath();
            bool hasBackup = !string.IsNullOrEmpty(fbxPath) && fileManagerService.BackupExists(fbxPath);
            bool canTreatAsCustom = FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) && editor.fetchAttempted && candidateVersions.Count > 0;
            if (hasBackup && canTreatAsCustom)
            {
                editor.currentIsCustom = true;
                // Persist the custom version entry (copy FBX and avatar) if not already present
                try
                {
                    if (!UserCustomVersionService.Instance.ExistsByAppliedHash(currentFileHash))
                    {
                        var entry = UserCustomVersionService.Instance.CreateFromCurrent(fbxPath, currentFileHash, AvatarPaths.Root(editor.customBaseTarget));
                        if (entry != null)
                        {
                            editor.userCustomVersions = UserCustomVersionService.Instance.GetAll();
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    MCBLogger.LogWarning($"[MCB] Failed to persist custom base info: {ex.Message}");
                }
            }
            else
            {
                editor.currentIsCustom = false;
            }
            
            if (changed)
                MCBLogger.Log("[VersionActions] No matching version hash found. Marking state as non-custom-base.");
        }

        if (changed) EditorUtility.SetDirty(target);
    }

    private void PersistAppliedVersionState(CustomBaseVersion version) => PersistAppliedVersionState(version, false);

    /// <param name="onlyWhenChanged">For state refreshes: the custom base is recorded for undo and marked dirty only
    /// when one of its values changes.</param>
    /// <returns>Whether the custom base was written.</returns>
    private bool PersistAppliedVersionState(CustomBaseVersion version, bool onlyWhenChanged)
    {
        if (editor?.customBaseTarget == null || version == null || version == VersionListDrawer.RESET_VERSION)
        {
            ClearAppliedVersionState();
            return true;
        }

        var target = editor.customBaseTarget;
        var appliedAsset = editor.GetSelectedAsset();
        string name = appliedAsset != null && appliedAsset.id == version.assetId && !string.IsNullOrWhiteSpace(appliedAsset.name)
            ? appliedAsset.name
            : target.appliedCustomBaseAssetId != version.assetId ? "" : target.appliedCustomBaseName;
        string versionString = version.version ?? "";
        string defaultAviVersion = version.defaultAviVersion ?? "";
        string sourceVersionKey = version.sourceVersionKey ?? "";
        string deliveryMode = NativeMeshPayloadService.VersionUsesAdvancedMesh(version)
            ? AdvancedMeshDeliveryMode
            : FbxReplacementDeliveryMode;
        var links = BuildAppliedVersionBlendshapeLinkCache(version);
        var offsets = BuildAppliedVersionAnimationPositionOffsetCache(version);
        var customization = VersionCustomization.Read(version.extraCustomization);

        target.appliedCustomBaseVersion = version;
        editor.isCustomBase = true;
        editor.currentIsCustom = false;
        if (onlyWhenChanged)
        {
            if (target.appliedCustomBaseName == name &&
                target.appliedCustomBaseAssetId == version.assetId &&
                target.appliedCustomBaseVersionString == versionString &&
                target.appliedCustomBaseDefaultAviVersion == defaultAviVersion &&
                target.appliedCustomBaseSourceVersionKey == sourceVersionKey &&
                target.appliedCustomBaseDeliveryMode == deliveryMode &&
                SameSerializedEntries(target.appliedVersionBlendshapeLinksCache, links) &&
                SameSerializedEntries(target.appliedVersionAnimationPositionOffsetsCache, offsets) &&
                JsonUtility.ToJson(target.appliedCustomization) == JsonUtility.ToJson(customization))
                return false;
            Undo.RecordObject(target, UpdateStateUndoName);
        }

        target.appliedCustomBaseName = name;
        target.appliedCustomBaseAssetId = version.assetId;
        target.appliedCustomBaseVersionString = versionString;
        target.appliedCustomBaseDefaultAviVersion = defaultAviVersion;
        target.appliedCustomBaseSourceVersionKey = sourceVersionKey;
        target.appliedCustomBaseDeliveryMode = deliveryMode;
        SetCache(ref target.appliedVersionBlendshapeLinksCache, links);
        SetCache(ref target.appliedVersionAnimationPositionOffsetsCache, offsets);
        target.appliedCustomization = customization;
        EditorUtility.SetDirty(target);
        editor.serializedObject.Update();
        return true;
    }

    private void ClearAppliedVersionState()
    {
        if (editor?.customBaseTarget == null) return;

        ModeService.Restore(editor.customBaseTarget);
        editor.customBaseTarget.appliedCustomization = new VersionCustomization();
        editor.customBaseTarget.appliedCustomBaseVersion = null;
        editor.customBaseTarget.appliedCustomBaseAssetId = 0;
        editor.customBaseTarget.versionOriginalModels.Clear();
        editor.customBaseTarget.appliedCustomBaseName = "";
        editor.customBaseTarget.appliedCustomBaseVersionString = "";
        editor.customBaseTarget.appliedCustomBaseDefaultAviVersion = "";
        editor.customBaseTarget.appliedCustomBaseSourceVersionKey = "";
        editor.customBaseTarget.appliedCustomBaseDeliveryMode = "";
        editor.isCustomBase = false;
        editor.currentIsCustom = false;
        SyncAppliedVersionBlendshapeLinkCache(null);
        SyncAppliedVersionAnimationPositionOffsetCache(null);
        EditorUtility.SetDirty(editor.customBaseTarget);
    }

    private CustomBaseVersion ResolvePersistedAppliedVersion(IReadOnlyList<CustomBaseVersion> candidateVersions = null)
    {
        if (editor?.customBaseTarget == null)
        {
            return null;
        }

        var applied = editor.customBaseTarget.appliedCustomBaseVersion;
        string versionString = editor.customBaseTarget.appliedCustomBaseVersionString;
        var candidates = new List<CustomBaseVersion>();
        if (candidateVersions != null) candidates.AddRange(candidateVersions.Where(v => v != null));
        var allVersions = editor.GetAllVersions();
        if (allVersions != null) candidates.AddRange(allVersions.Where(v => v != null));
        if (editor.selectedVersionForAction != null) candidates.Add(editor.selectedVersionForAction);
        if (editor.recommendedVersion != null) candidates.Add(editor.recommendedVersion);

        candidates = candidates
            .Where(v => v != null)
            .GroupBy(v => $"{v.assetId}|{v.version}|{v.defaultAviVersion}|{v.sourceVersionKey}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var inferredAdvanced = InferAdvancedVersionFromGeneratedMeshPaths(candidates, out bool hasGeneratedMeshes);
        if (inferredAdvanced != null)
        {
            return inferredAdvanced;
        }

        // Shared meshes without an unambiguous identity must not revive a stale marker.
        if (hasGeneratedMeshes)
            return null;

        if (applied != null && !string.IsNullOrWhiteSpace(applied.version) && applied != VersionListDrawer.RESET_VERSION)
        {
            return applied;
        }

        if (string.IsNullOrWhiteSpace(versionString))
        {
            return candidates.FirstOrDefault(v =>
                       v != null &&
                       NativeMeshPayloadService.VersionUsesAdvancedMesh(v) &&
                       NativeMeshPayloadService.HasAnyAdvancedMeshApplied(AvatarPaths.Root(editor.customBaseTarget), v));
        }

        int assetId = editor.customBaseTarget.appliedCustomBaseAssetId;
        string defaultAviVersion = editor.customBaseTarget.appliedCustomBaseDefaultAviVersion;
        return candidates.FirstOrDefault(v =>
            v != null &&
            string.Equals(v.version, versionString, StringComparison.Ordinal) &&
            (string.IsNullOrEmpty(editor.customBaseTarget.appliedCustomBaseSourceVersionKey) || v.sourceVersionKey == editor.customBaseTarget.appliedCustomBaseSourceVersionKey) &&
            (assetId <= 0 || v.assetId == assetId) &&
            (string.IsNullOrWhiteSpace(defaultAviVersion) || string.Equals(v.defaultAviVersion, defaultAviVersion, StringComparison.Ordinal)));
    }

    // Every version row asks for the applied version on each UI rebuild: the inference from the avatar's generated meshes
    // is reused while the meshes, the applied marker and the candidate versions are the same objects it was made from.
    private sealed class GeneratedMeshInference
    {
        public MyCustomBase Target;
        public int[] Meshes;
        public string[] Paths;
        public string Marker;
        public List<CustomBaseVersion> Available;
        public CustomBaseVersion Match;
    }

    private GeneratedMeshInference generatedMeshInference;

    private CustomBaseVersion InferAdvancedVersionFromGeneratedMeshPaths(IReadOnlyList<CustomBaseVersion> candidates, out bool hasGeneratedMeshes)
    {
        hasGeneratedMeshes = false;
        if (editor?.customBaseTarget == null)
        {
            return null;
        }

        var target = editor.customBaseTarget;
        var root = AvatarPaths.Root(target);
        var meshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Select(renderer => renderer.sharedMesh != null ? renderer.sharedMesh.GetInstanceID() : 0)
            .ToArray();
        var cached = generatedMeshInference;
        bool sameMeshes = cached != null && cached.Target == target && cached.Meshes.SequenceEqual(meshes);
        var paths = sameMeshes
            ? cached.Paths
            : NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(root)
                .Select(renderer => MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        hasGeneratedMeshes = paths.Length > 0;

        var available = new List<CustomBaseVersion>();
        if (hasGeneratedMeshes)
        {
            if (candidates != null)
            {
                available.AddRange(candidates.Where(v => v != null));
            }

            var local = VersionRepository.Scan();
            available.AddRange(local.imported.Where(v => v != null));
            available.AddRange(local.unsubmitted.Where(v => v != null));
            if (!string.IsNullOrEmpty(target.appliedCustomBaseSourceVersionKey))
                available = available.Where(v => v.sourceVersionKey == target.appliedCustomBaseSourceVersionKey).ToList();
        }

        string marker = $"{target.appliedCustomBaseAssetId}|{target.appliedCustomBaseVersionString}|" +
                        $"{target.appliedCustomBaseDefaultAviVersion}|{target.appliedCustomBaseSourceVersionKey}";
        if (!sameMeshes || cached.Marker != marker || !SameObjects(cached.Available, available))
        {
            var inferred = hasGeneratedMeshes
                ? NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(paths, available, target.appliedCustomBaseAssetId,
                    target.appliedCustomBaseVersionString, target.appliedCustomBaseDefaultAviVersion)
                : null;
            if (inferred != null && !inferred.Equals(cached?.Match))
                MCBLogger.Log($"[VersionActions] Inferred native mesh applied version {inferred.version} from generated mesh provenance.");
            cached = generatedMeshInference = new GeneratedMeshInference
            {
                Target = target, Meshes = meshes, Paths = paths, Marker = marker, Available = available, Match = inferred
            };
        }

        var match = cached.Match;
        if (match == null)
        {
            return null;
        }

        if (match.isUnsubmitted)
        {
            if (editor.unsubmittedVersions != null &&
                !editor.unsubmittedVersions.Any(v => v != null && v.Equals(match)))
            {
                editor.unsubmittedVersions.Add(match);
            }
        }
        else if (editor.importedVersions != null &&
                 !editor.importedVersions.Any(v => v != null && v.Equals(match)))
        {
            editor.importedVersions.Add(match);
        }

        return match;
    }

    private static bool SameObjects<T>(List<T> cached, List<T> current) where T : class
    {
        if (cached.Count != current.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(cached[i], current[i])) return false;
        }
        return true;
    }

    private bool IsAdvancedMeshVersionApplied(CustomBaseVersion version)
    {
        if (editor?.customBaseTarget == null || version == null)
        {
            return false;
        }

        bool isAdvanced = NativeMeshPayloadService.VersionUsesAdvancedMesh(version) ||
                          string.Equals(
                              editor.customBaseTarget.appliedCustomBaseDeliveryMode,
                              AdvancedMeshDeliveryMode,
                              StringComparison.Ordinal);
        return isAdvanced &&
               NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(
                   AvatarPaths.Root(editor.customBaseTarget),
                   version).Count > 0;
    }

    public bool IsVersionCurrentlyApplied(CustomBaseVersion version)
    {
        if (version == null || version == VersionListDrawer.RESET_VERSION || editor?.customBaseTarget == null)
        {
            return false;
        }

        var applied = ResolvePersistedAppliedVersion();
        return applied != null && version.Equals(applied);
    }

    public bool HasAppliedCustomBaseEvidence()
    {
        if (editor?.customBaseTarget == null)
        {
            return false;
        }

        return NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(
                   AvatarPaths.Root(editor.customBaseTarget)).Count > 0 ||
               ResolvePersistedAppliedVersion() != null ||
               editor.isCustomBase ||
               editor.currentIsCustom;
    }

    public bool IsDefaultBaseCurrentlyApplied()
    {
        return !HasAppliedCustomBaseEvidence();
    }

    private void SyncAppliedVersionBlendshapeLinkCache(CustomBaseVersion version)
    {
        if (editor?.customBaseTarget == null) return;
        SetCache(ref editor.customBaseTarget.appliedVersionBlendshapeLinksCache, BuildAppliedVersionBlendshapeLinkCache(version));
    }

    private void SyncAppliedVersionAnimationPositionOffsetCache(CustomBaseVersion version)
    {
        if (editor?.customBaseTarget == null) return;
        SetCache(ref editor.customBaseTarget.appliedVersionAnimationPositionOffsetsCache, BuildAppliedVersionAnimationPositionOffsetCache(version));
    }

    private static void SetCache<T>(ref List<T> cache, List<T> entries)
    {
        if (cache == null) cache = new List<T>();
        cache.Clear();
        cache.AddRange(entries);
    }

    // Compared as Unity serializes them: entries rebuilt from the same version are equal to the cached ones.
    private static bool SameSerializedEntries<T>(List<T> cache, List<T> entries)
    {
        if ((cache?.Count ?? 0) != entries.Count) return false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (JsonUtility.ToJson(cache[i]) != JsonUtility.ToJson(entries[i])) return false;
        }
        return true;
    }

    private static List<CreatorBlendshapeEntry> BuildAppliedVersionBlendshapeLinkCache(CustomBaseVersion version)
    {
        var cache = new List<CreatorBlendshapeEntry>();
        if (version?.customBlendshapes == null || version.customBlendshapes.Length == 0) return cache;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null) continue;
            var cached = new CreatorBlendshapeEntry
            {
                name = entry.name,
                defaultValue = entry.defaultValue,
                isSlider = entry.isSlider,
                isSliderDefault = entry.isSliderDefault,
                correctiveBlendshapes = new List<CreatorCorrectiveBlendshapeEntry>()
            };

            if (entry.correctiveBlendshapes != null)
            {
                foreach (var c in entry.correctiveBlendshapes)
                {
                    if (c == null) continue;
                    cached.correctiveBlendshapes.Add(new CreatorCorrectiveBlendshapeEntry
                    {
                        toFixType = c.toFixType,
                        toFix = c.toFix,
                        fixedByType = c.fixedByType,
                        fixedBy = c.fixedBy
                    });
                }
            }

            cache.Add(cached);
        }
        return cache;
    }

    private static List<AnimationPositionOffsetEntry> BuildAppliedVersionAnimationPositionOffsetCache(CustomBaseVersion version)
    {
        var cache = new List<AnimationPositionOffsetEntry>();
        if (version == null) return cache;

        foreach (var offset in AnimationPositionOffsetService.BuildOffsetsForVersion(version))
        {
            if (offset == null || string.IsNullOrWhiteSpace(offset.bonePath)) continue;
            cache.Add(new AnimationPositionOffsetEntry
            {
                sourceFbxPath = offset.sourceFbxPath,
                bonePath = offset.bonePath,
                offset = offset.offset
            });
        }
        return cache;
    }

    private void SmartSelectVersion()
    {
        var allVersions = editor.GetAllVersions() ?? new System.Collections.Generic.List<CustomBaseVersion>();
        CustomBaseVersion versionToSelect = null;

        if (editor.selectedVersionForAction != null && allVersions.Contains(editor.selectedVersionForAction))
        {
            versionToSelect = editor.selectedVersionForAction;
        }
        else if (editor.customBaseTarget.appliedCustomBaseVersion != null && allVersions.Contains(editor.customBaseTarget.appliedCustomBaseVersion))
        {
            versionToSelect = editor.customBaseTarget.appliedCustomBaseVersion;
        }
        else if (editor.recommendedVersion != null)
        {
            bool isNewer = editor.customBaseTarget.appliedCustomBaseVersion == null ||
                           editor.CompareVersions(editor.recommendedVersion.version, editor.customBaseTarget.appliedCustomBaseVersion.version) > 0;
            bool isDownloaded = MCBUtils.IsVersionDownloaded(editor.recommendedVersion);

            if (isNewer && isDownloaded)
            {
                versionToSelect = editor.recommendedVersion;
            }
        }

        editor.selectedVersionForAction = versionToSelect;
    }

    public string GetCurrentFBXPath()
    {
        return GetCurrentFBXPaths().FirstOrDefault();
    }

    private List<string> GetCurrentFBXPaths()
    {
        var paths = new List<string>();
        if (editor?.baseFbxFilesProp == null)
        {
            return paths;
        }

        for (int i = 0; i < editor.baseFbxFilesProp.arraySize; i++)
        {
            var fbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            string path = fbx != null ? AssetDatabase.GetAssetPath(fbx) : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(MCBUtils.ToUnityPath(path));
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CustomBaseVersion FindMatchingAppliedVersion(IReadOnlyList<CustomBaseVersion> candidateVersions, string currentFileHash)
    {
        if (candidateVersions == null || candidateVersions.Count == 0)
        {
            return null;
        }

        var persistedAdvanced = editor?.customBaseTarget?.appliedCustomBaseVersion;
        if (persistedAdvanced != null && NativeMeshPayloadService.VersionUsesAdvancedMesh(persistedAdvanced))
        {
            var matchingPersisted = candidateVersions.FirstOrDefault(v =>
                v != null &&
                v.assetId == persistedAdvanced.assetId &&
                string.Equals(v.version, persistedAdvanced.version, StringComparison.Ordinal) &&
                string.Equals(v.defaultAviVersion, persistedAdvanced.defaultAviVersion, StringComparison.Ordinal));
            if (matchingPersisted != null &&
                NativeMeshPayloadService.IsVersionApplied(AvatarPaths.Root(editor.customBaseTarget), matchingPersisted))
            {
                return matchingPersisted;
            }
        }

        var target = editor?.customBaseTarget;
        return SelectAppliedVersionByHashes(candidateVersions, currentFileHash, GetCurrentTargetHashes(currentFileHash),
            sourceFile => AvatarPathOverrideService.ResolveLocalTargetPath(target, sourceFile),
            version => target != null &&
                       version.assetId == target.appliedCustomBaseAssetId &&
                       string.Equals(version.version, target.appliedCustomBaseVersionString, StringComparison.Ordinal) &&
                       string.Equals(version.defaultAviVersion, target.appliedCustomBaseDefaultAviVersion, StringComparison.Ordinal) &&
                       string.Equals(version.sourceVersionKey ?? "", target.appliedCustomBaseSourceVersionKey ?? "", StringComparison.Ordinal));
    }

    /// <summary>
    /// A release is identified by every target model it changes. appliedCustomAviHash covers only the first model, so it
    /// cannot identify a release until secondary hashes are known. An existing persisted identity can be retained
    /// while hashing if no known hash contradicts it. It also picks among releases with identical mesh bytes.
    /// </summary>
    internal static CustomBaseVersion SelectAppliedVersionByHashes(
        IReadOnlyList<CustomBaseVersion> candidateVersions,
        string primaryHash,
        IReadOnlyDictionary<string, string> currentHashesByPath,
        Func<ModelFileData, string> resolveTargetPath,
        Func<CustomBaseVersion, bool> isPersistedIdentity)
    {
        var candidates = (candidateVersions ?? Array.Empty<CustomBaseVersion>()).Where(v => v != null).ToList();
        var matches = candidates.Where(v => MatchTargetHashes(v, currentHashesByPath, resolveTargetPath) == true).ToList();
        if (matches.Count == 0)
        {
            matches = candidates.Where(v =>
                !string.IsNullOrEmpty(v.appliedCustomAviHash) &&
                v.appliedCustomAviHash.Equals(primaryHash, StringComparison.OrdinalIgnoreCase) &&
                isPersistedIdentity(v) &&
                MatchTargetHashes(v, currentHashesByPath, resolveTargetPath) != false).ToList();
        }

        return matches.FirstOrDefault(isPersistedIdentity) ?? matches.FirstOrDefault();
    }

    // True when every target hash matches, false when a known target hash differs, null when a target hash is unknown.
    private static bool? MatchTargetHashes(
        CustomBaseVersion version,
        IReadOnlyDictionary<string, string> currentHashesByPath,
        Func<ModelFileData, string> resolveTargetPath)
    {
        if (version?.sourceFiles == null || version.sourceFiles.Length == 0)
        {
            return null;
        }

        bool unknown = false;
        bool comparedAny = false;
        foreach (var sourceFile in version.sourceFiles)
        {
            if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.path))
            {
                continue;
            }

            string targetPath = resolveTargetPath(sourceFile);
            if (string.IsNullOrWhiteSpace(targetPath) ||
                currentHashesByPath == null ||
                !currentHashesByPath.TryGetValue(targetPath, out string currentHash))
            {
                unknown = true;
                continue;
            }

            string expectedHash = ResolveExpectedHashForSource(version, sourceFile);
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !expectedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            comparedAny = true;
        }

        return comparedAny && !unknown ? true : (bool?)null;
    }

    private Dictionary<string, string> GetCurrentTargetHashes(string currentFileHash)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashService = AsyncHashService.Instance;
        var paths = GetCurrentFBXPaths();

        for (int i = 0; i < paths.Count; i++)
        {
            string path = MCBUtils.ToUnityPath(paths[i]);
            string hash = i == 0 ? currentFileHash : null;
            if (string.IsNullOrWhiteSpace(hash))
            {
                hash = hashService.GetHashIfCached(path);
            }

            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(hash))
            {
                hashes[path] = hash;
            }
        }

        return hashes;
    }

    private static string ResolveExpectedHashForSource(CustomBaseVersion version, ModelFileData sourceFile)
    {
        if (NativeMeshPayloadService.VersionUsesAdvancedMesh(version))
        {
            var advancedPatch = version.versionFiles?.FirstOrDefault(file =>
                file != null &&
                string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                NativeMeshPayloadService.IsAdvancedMeshPatchTransform(file.transform) &&
                IsPatchForSourceFile(file, sourceFile));
            return advancedPatch?.outputHash;
        }

        var patchFile = version.versionFiles?.FirstOrDefault(file =>
            file != null &&
            string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
            ModelFileTransforms.IsFbxReplacementTransform(file.transform) &&
            IsPatchForSourceFile(file, sourceFile));

        if (!string.IsNullOrWhiteSpace(patchFile?.outputHash))
        {
            return patchFile.outputHash;
        }

        return sourceFile.hash;
    }

    private ModelFileData ResolveSourceFileForPatch(CustomBaseVersion version, ModelFileData patchFile)
    {
        return version?.sourceFiles?.FirstOrDefault(sourceFile => IsPatchForSourceFile(patchFile, sourceFile));
    }

    private static bool IsPatchForSourceFile(ModelFileData patchFile, ModelFileData sourceFile)
    {
        if (patchFile == null || sourceFile == null)
        {
            return false;
        }

        if (patchFile.sourceModelFileId.HasValue && sourceFile.id == patchFile.sourceModelFileId.Value)
        {
            return true;
        }

        string sourcePath = MCBUtils.ToUnityPath(sourceFile.path);
        string patchSourcePath = null;
        if (patchFile.metadata != null &&
            patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue))
        {
            patchSourcePath = sourcePathValue?.ToString();
        }

        return !string.IsNullOrWhiteSpace(sourcePath) &&
               !string.IsNullOrWhiteSpace(patchSourcePath) &&
               string.Equals(sourcePath, MCBUtils.ToUnityPath(patchSourcePath), StringComparison.OrdinalIgnoreCase);
    }

    private void CommitActiveTransition()
    {
        if (activeTransitionRollback == null) return;

        activeTransitionRollback.Commit();
        activeTransitionRollback = null;
    }

    private void RollbackActiveTransition()
    {
        var snapshot = activeTransitionRollback;
        activeTransitionRollback = null;
        if (snapshot == null) return;

        try
        {
            snapshot.Rollback();
            MCBLogger.Log("[VersionActions] Restored the exact previous FBX and scene state after the failed version transition.");
        }
        catch (Exception rollbackException)
        {
            MCBLogger.LogError($"[VersionActions] Version transition rollback failed: {rollbackException}");
            ClearAppliedVersionState();
            editor.warningsModule?.AddWarning(
                "The version switch failed and MCB could not fully restore the previous state. The applied-version marker was cleared to avoid reporting a mismatched version.",
                MessageType.Error,
                "Version rollback failed");
        }
    }

    // Before the mutation: the files it may change, to restore them on failure. On success the state after it is kept
    // too, so Undo/Redo of the switch also puts those files back (VersionSwitchFileUndo).
    private sealed class VersionTransitionRollbackSnapshot
    {
        private const string UndoName = "Switch MCB Version";
        private readonly VersionSwitchUndoScope undo;
        private readonly string id = Guid.NewGuid().ToString("N");
        private readonly string folder;
        private readonly List<string> fbxPaths = new List<string>();
        private readonly List<VersionSwitchFileUndo.Entry> entries = new List<VersionSwitchFileUndo.Entry>();
        private VersionSwitchFileUndo.Setting veinsBefore;
        private readonly MCBEditor editor;
        private readonly Transform root;
        private readonly List<string> assetsBefore;
        private readonly bool editorWasCustomBase;
        private readonly bool editorCurrentWasCustom;
        private bool completed;

        private VersionTransitionRollbackSnapshot(MyCustomBase target, MCBEditor editor)
        {
            this.editor = editor;
            folder = Path.Combine(VersionSwitchFileUndo.Folder, id);
            editorWasCustomBase = editor != null && editor.isCustomBase;
            editorCurrentWasCustom = editor != null && editor.currentIsCustom;
            root = target != null ? AvatarPaths.Root(target) : null;
            assetsBefore = VersionSwitchFileUndo.UsedGeneratedAssets(root).ToList();
            undo = new VersionSwitchUndoScope(root, UndoName);
            if (target != null)
            {
                Undo.RegisterCompleteObjectUndo(target, UndoName);
            }
        }

        public static VersionTransitionRollbackSnapshot Capture(
            IEnumerable<string> fbxPaths,
            MyCustomBase target,
            MCBEditor editor)
        {
            var snapshot = new VersionTransitionRollbackSnapshot(target, editor);
            try
            {
                snapshot.fbxPaths.AddRange(fbxPaths ?? Enumerable.Empty<string>());
                foreach (string path in VersionSwitchFileUndo.AffectedPaths(snapshot.fbxPaths))
                {
                    snapshot.entries.Add(new VersionSwitchFileUndo.Entry
                    {
                        unityPath = path,
                        before = VersionSwitchFileUndo.Save(path, snapshot.folder, "before")
                    });
                }
                snapshot.veinsBefore = VersionSwitchFileUndo.SaveVeins();
                return snapshot;
            }
            catch
            {
                snapshot.CleanupFiles();
                snapshot.undo.Abandon();
                throw;
            }
        }

        public void Commit()
        {
            if (completed) return;

            try
            {
                foreach (string path in VersionSwitchFileUndo.AffectedPaths(fbxPaths))
                {
                    if (entries.Any(entry => string.Equals(entry.unityPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                    // Created by the switch (a dynamic normals mesh): Undo removes it.
                    entries.Add(new VersionSwitchFileUndo.Entry { unityPath = path, before = new VersionSwitchFileUndo.FileState() });
                }
                foreach (var entry in entries)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(entry.unityPath);
                    if (asset != null && !(asset is GameObject)) AssetDatabase.SaveAssetIfDirty(asset);
                    entry.after = VersionSwitchFileUndo.Save(entry.unityPath, folder, "after", entry.before);
                }
                VersionSwitchFileUndo.Record(id, folder, entries, veinsBefore, VersionSwitchFileUndo.SaveVeins(), UndoName,
                    assetsBefore, VersionSwitchFileUndo.UsedGeneratedAssets(root));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MCBLogger.LogWarning($"[VersionActions] The version switch worked, but its files could not be kept for Undo ({ex.Message}). Undoing it will only restore the scene.");
                CleanupFiles();
            }
            undo.Commit();
            completed = true;
        }

        public void Rollback()
        {
            if (completed) return;

            Exception rollbackFailure = null;
            foreach (var entry in entries)
            {
                try
                {
                    VersionSwitchFileUndo.Restore(entry.unityPath, entry.before);
                }
                catch (Exception ex)
                {
                    rollbackFailure = rollbackFailure ?? ex;
                }
            }

            try
            {
                undo.Rollback();
            }
            catch (Exception ex)
            {
                rollbackFailure = rollbackFailure ?? ex;
            }

            if (editor != null)
            {
                editor.isCustomBase = editorWasCustomBase;
                editor.currentIsCustom = editorCurrentWasCustom;
            }

            completed = true;
            if (rollbackFailure == null) CleanupFiles();

            if (rollbackFailure != null)
            {
                throw new InvalidOperationException("Could not restore the previous version-switch state. Recovery copies are retained at: " + folder, rollbackFailure);
            }
        }

        private void CleanupFiles()
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MCBLogger.LogWarning($"[VersionActions] Could not delete temporary version rollback files '{folder}': {ex.Message}");
            }
        }
    }

    private IEnumerator ApplyCustomVersionCoroutine(UserCustomVersionEntry entry)
    {
        if (entry == null || !FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION)) yield break;
        if (string.IsNullOrWhiteSpace(entry.backupFbxPath) || !File.Exists(entry.backupFbxPath))
        {
            editor.warningsModule?.AddWarning("The saved custom FBX no longer exists. The current version was kept.", MessageType.Error, "Custom version unavailable");
            yield break;
        }
        var requestedTarget = editor.customBaseTarget;
        while (EditorApplication.isCompiling || EditorApplication.isUpdating) yield return null;
        if (requestedTarget == null || editor.customBaseTarget != requestedTarget) yield break;

        var root = AvatarPaths.Root(requestedTarget);
        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrEmpty(fbxPath)) yield break;
        var previous = ResolvePersistedAppliedVersion();
        bool ReleaseApplied() => ResolvePersistedAppliedVersion() != null ||
                                 NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(root).Count > 0;
        VersionTransitionRollbackSnapshot snapshot = null;
        try
        {
            if (activeTransitionRollback != null) throw new InvalidOperationException("Another version transition is still active.");
            snapshot = VersionTransitionRollbackSnapshot.Capture(
                GetTransitionAffectedFbxPaths(GetDistinctTransitionVersions(previous), fbxPath), requestedTarget, editor);
            StartApplyProgress("Applying saved custom version...", 0f);
            // Reset and custom installation are one synchronous transaction: failure must restore the release as well.
            if (ReleaseApplied())
            {
                ApplyOrResetCore(null, true, finalize: false);
                if (ReleaseApplied()) throw new InvalidOperationException("The applied version could not be reset.");
            }
            fileManagerService.RemoveExistingLogic(root);
            if (!fileManagerService.BackupExists(fbxPath)) fileManagerService.CreateBackup(fbxPath);
            File.Copy(Path.GetFullPath(MCBUtils.ToUnityPath(entry.backupFbxPath)), Path.GetFullPath(fbxPath), true);
            fileManagerService.RecordWrittenFbx(fbxPath);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var fbxGameObject = editor.baseFbxFilesProp.arraySize > 0
                ? editor.baseFbxFilesProp.GetArrayElementAtIndex(0).objectReferenceValue as GameObject : null;
            if (!string.IsNullOrEmpty(entry.appliedAvatarAsset))
            {
                if (fbxGameObject != null) AvatarDefinitionGenerationService.BackupOriginalImportSettings(AssetDatabase.GetAssetPath(fbxGameObject));
                fileManagerService.ApplyAvatarToModel(root, fbxGameObject, entry.appliedAvatarAsset);
            }
            ClearAppliedVersionState();
            editor.currentIsCustom = true;
            snapshot.Commit();
            FinishApplyProgress(true);
            StartRecalculateCurrentFbxHash();
        }
        catch (Exception ex)
        {
            RollbackActiveTransition();
            try { snapshot?.Rollback(); }
            catch (Exception rollback)
            {
                ClearAppliedVersionState();
                editor.warningsModule?.AddWarning(rollback.Message, MessageType.Error, "Custom version rollback failed");
                MCBLogger.LogError($"[MCB] Custom version rollback failed: {rollback.Message}");
            }
            FinishApplyProgress(false);
            editor.warningsModule?.AddWarning(ex.Message, MessageType.Error, "Custom version not applied");
            MCBLogger.LogError($"[MCB] Failed to apply custom version: {ex.Message}");
        }
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

}
#endif
