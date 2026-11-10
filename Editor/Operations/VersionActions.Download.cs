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

// Downloading a version (to memory or disk), preparing its payloads, deleting and exporting local versions.
public partial class VersionActions
{
    public void ExportOfflineVersion(CustomBaseVersion version)
    {
        if (version == null) return;

        string versionFolderPath = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrWhiteSpace(versionFolderPath) || !Directory.Exists(Path.GetFullPath(versionFolderPath)))
        {
            session.warningsModule.AddWarning("The selected version is not available locally, so it cannot be exported.", MessageType.Error, "Export failed");
            session.Repaint();
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
            session.warningsModule.AddWarning(ex.Message, MessageType.Error, "Export failed");
            MCBLogger.LogError($"[VersionActions] Offline export failed: {ex}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            session.LoadImportedVersions(true);
            session.Repaint();
        }
    }

    private IEnumerator DownloadVersionCoroutine(CustomBaseVersion version, bool applyAfter)
    {
        // A Download and Apply started the apply progress (StartVersionDownload): every way out before the apply runs ends
        // it, or the version buttons stay disabled until the next reload.
        void EndUnstarted()
        {
            if (applyAfter) FinishApplyProgress(false);
            else session.RequestRefresh(McbViewParts.All);
            session.Repaint();
        }

        if (version.isUnsubmitted || version.localArtifactSourceVersionKey != null)
        {
            bool local = MCBUtils.IsVersionDownloaded(version);
            if (local || version.isUnsubmitted)
            {
                if (!local)
                {
                    session.warningsModule.AddWarning("This local build is incomplete. Rebuild it in the creator form before previewing or applying it.", MessageType.Error, "Local version unavailable");
                    EndUnstarted();
                    yield break;
                }
                if (applyAfter) yield return ApplyOrResetCoroutine(version, false);
                session.RequestRefresh(McbViewParts.All);
                yield break;
            }
            // A published version whose local artifact no longer holds this original: download the original's own copy.
            version = VersionRepository.InLocalArtifact(version, null);
        }
        MCBPerformance.PauseForeground();
        if (session.isDownloading) yield break;
        session.warningsModule.Clear();

        // Before downloading anything, and before the download counts as running.
        string blocked = DownloadBlocker(version, out string blockedTitle);
        if (blocked != null)
        {
            session.warningsModule.AddWarning(blocked, MessageType.Error, blockedTitle);
            EndUnstarted();
            yield break;
        }

        var selectedAsset = session.GetSelectedAsset();
        if (selectedAsset == null)
        {
            MCBLogger.LogWarning("[VersionActions] Download aborted because no asset is selected in the gallery.");
            EndUnstarted();
            yield break;
        }

        session.isDownloading = true;
        DownloadProgress = 0f;
        if (applyAfter)
        {
            BeginApplyProgress("Downloading version...", 0.02f);
        }
        session.Repaint();

        // Every way the download ends on its own clears isDownloading. One that throws (a locked FBX while hashing or
        // preloading it) still ends it here, or the version buttons stay disabled until the next reload.
        var download = DownloadStartedVersionCoroutine(version, applyAfter, selectedAsset);
        try
        {
            while (download.MoveNext()) yield return download.Current;
        }
        finally
        {
            if (session.isDownloading)
            {
                session.isDownloading = false;
                session.warningsModule.AddWarning($"Version {version.version} could not be downloaded. See the Console for details.", MessageType.Error, "Download failed");
                EndUnstarted();
            }
        }
    }

    private IEnumerator DownloadStartedVersionCoroutine(CustomBaseVersion version, bool applyAfter, AvatarDiscoveredAsset selectedAsset)
    {
        // Inspector initialization may first detect a garment before the selected
        // asset's source mapping is loaded. Never use that stale UI hash to download.
        string requestedSourcePath = GetCurrentFBXPath();
        string requestedToken = session.authToken;
        var sourceHashesTask = FileManagerService.HashModelAndBackupAsync(requestedSourcePath);
        while (!sourceHashesTask.IsCompleted) yield return null;
        var sourceHashes = sourceHashesTask.Result;
        RefreshOriginalBackup(requestedSourcePath, version);
        string requestBaseHash = fileManagerService.BackupExists(requestedSourcePath)
            ? sourceHashes.originalHash : sourceHashes.currentHash;
        if (session.GetSelectedAsset()?.id != selectedAsset.id || session.authToken != requestedToken ||
            !string.Equals(requestedSourcePath, GetCurrentFBXPath(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(requestBaseHash))
        {
            session.isDownloading = false;
            if (applyAfter) FinishApplyProgress(false);
            session.warningsModule.AddWarning("The source model changed or could not be read. Select the version again to retry.", MessageType.Warning, "Download paused");
            session.RequestRefresh(McbViewParts.All);
            yield break;
        }
        session.currentBaseFbxHash = requestBaseHash;
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"mcb_dl_{Guid.NewGuid()}.zip");
        string url = $"{MCBUtils.getApiUrl()}{MCBUtils.GetAssetModelEndpoint(selectedAsset.id)}?version={Query(version.version)}&d={Query(requestBaseHash)}&sourceKey={Query(version.sourceVersionKey)}";
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
            }, requestedToken, NetworkService.DownloadBudget(delivery.packageBytes));
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
            }, requestedToken, NetworkService.DownloadBudget(delivery?.packageBytes ?? 0));
        }
        bool setupSucceeded = memoryDownloadTask != null || diskDownloadTask != null;
        
        if (!setupSucceeded)
        {
            session.warningsModule.AddWarning("Failed to start download task", MessageType.Error, "Download failed");
            session.isDownloading = false;
            if (applyAfter) FinishApplyProgress(false);
            else session.RequestRefresh(McbViewParts.All);
            session.Repaint();
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
            if (session.authToken != requestedToken || AuthenticationService.GetAuth()?.token != requestedToken ||
                session.GetSelectedAsset()?.id != selectedAsset.id)
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
                session.warningsModule.AddWarning(error, MessageType.Error, "Download failed");
                if (applyAfter) FinishApplyProgress(false);
            }
            else if (!VersionContentTrust.ConfirmDownloadedCode(version, selectedAsset, downloadedCode, currentCreatorTrust))
            {
                downloadedZipBytes = null;
                session.warningsModule.AddWarning(
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
                            !ContentHash.Verify(path, file.hash))
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

                session.LoadImportedVersions(true);
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
            session.warningsModule.AddWarning($"Extraction failed: {e.Message}", MessageType.Error, "Extraction failed"); 
            if (applyAfter) FinishApplyProgress(false);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            if (!string.IsNullOrEmpty(tempExtractPath) && Directory.Exists(tempExtractPath)) 
                Directory.Delete(tempExtractPath, true);
            
            session.isDownloading = false;
            // A failed download-and-apply rebuilt the panel while the download still counted as running: rebuild it
            // again, or its buttons stay disabled. A successful one goes on applying and refreshes when that ends.
            if (!applyAfter || !extractionSucceeded)
            {
                session.RequestRefresh(McbViewParts.All);
            }
            session.Repaint();
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
                session.selectedVersionForAction = version;
                SetApplyProgressRange(DownloadApplyImportComplete, 1f - DownloadApplyImportComplete);
                yield return ApplyOrResetCoroutine(version, false);
                advancedMeshPreparationPreloads.Clear();
            }
        }
    }

    /// <summary>
    /// Why <paramref name="version"/> cannot be downloaded into this project, or null: its files (and the .meta files
    /// shipped with them) must fit Windows' path limit at this project's location, and its labels, original and file paths
    /// must make paths inside its version folder. <paramref name="title"/> heads the warning.
    /// </summary>
    internal static string DownloadBlocker(CustomBaseVersion version, out string title)
    {
        title = null;
        try
        {
            Orbiters.Toolkit.Editor.Storage.SafeArchive.RequireRoom(MCBUtils.GetVersionDataPath(version),
                (version?.versionFiles ?? Array.Empty<ModelFileData>()).Where(file => file != null).SelectMany(file => new[] { file.path, file.path + ".meta" }));
            return null;
        }
        catch (PathTooLongException ex)
        {
            title = "Project path too long";
            return ex.Message;
        }
        // A label or file path the server sent that is no valid path here, or a versions folder reached through a link.
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidDataException ||
                                   ex is UnauthorizedAccessException || ex is NotSupportedException)
        {
            title = "Download failed";
            return (string.IsNullOrWhiteSpace(version?.version) ? "This version" : "Version " + version.version) +
                   " cannot be stored in this project: " + ex.Message;
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
        if (session.isDeleting) yield break;
        session.isDeleting = true;
        session.warningsModule.Clear();
        session.Repaint();

        bool deleted = false;
        try
        {
            // Repository delete: refuses while the version is being built/published.
            VersionRepository.Delete(version);
            deleted = true;
        }
        catch (Exception e) { session.warningsModule.AddWarning($"Failed to delete folder: {e.Message}", MessageType.Error, "Deletion failed"); }

        if (deleted)
        {
            // The version's generated meshes nothing uses follow in the background.
            _ = NativeMeshPayloadService.QueueUnusedPayloadCleanup(version);

            if (version.isUnsubmitted)
            {
                session.RemoveUnsubmittedVersion(version);
            }
            AssetDatabase.Refresh();
            session.LoadImportedVersions(true);
        }

        session.isDeleting = false;
        session.RequestRefresh(McbViewParts.All);
        session.Repaint();
    }
}
#endif
