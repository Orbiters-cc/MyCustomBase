#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;

public class VersionActions
{
    private const float BlendshapeWeightEpsilon = 0.001f;

    private readonly MCBEditor editor;
    private readonly NetworkService networkService;
    private readonly FileManagerService fileManagerService;

    public VersionActions(MCBEditor editor, NetworkService network, FileManagerService files)
    {
        this.editor = editor;
        this.networkService = network;
        this.fileManagerService = files;
    }
    
    // Coroutine Starters
    public void StartVersionFetch() => EditorCoroutineUtility.StartCoroutineOwnerless(FetchVersionsCoroutine());
    public void StartVersionDownload(CustomBaseVersion ver, bool apply) => EditorCoroutineUtility.StartCoroutineOwnerless(DownloadVersionCoroutine(ver, apply));
    public void StartVersionDelete(CustomBaseVersion ver) => EditorCoroutineUtility.StartCoroutineOwnerless(DeleteVersionCoroutine(ver));
    public void StartApplyVersion() => EditorCoroutineUtility.StartCoroutineOwnerless(ApplyOrResetCoroutine(editor.selectedVersionForAction, false));
    public void StartReset() => EditorCoroutineUtility.StartCoroutineOwnerless(ApplyOrResetCoroutine(null, true));
    public void StartRecalculateCurrentFbxHash() => EditorCoroutineUtility.StartCoroutineOwnerless(RecalculateCurrentFbxHashCoroutine());
    public void StartApplyCustomVersion() => EditorCoroutineUtility.StartCoroutineOwnerless(ApplyCustomVersionCoroutine(editor.selectedCustomVersionForAction));
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
            editor.LoadImportedVersions();
            editor.Repaint();
        }
    }

    private IEnumerator FetchVersionsCoroutine()
    {
        if (editor.isFetching) yield break;
        editor.isFetching = true;
        editor.warningsModule.Clear();
        editor.accessDeniedAssetId = null;
        editor.fetchAttempted = true;
        editor.Repaint();

        UpdateCurrentBaseFbxHash();
        MCBLogger.Log("[VersionActions] ApplyOrResetCoroutine completed. Updating hash.");

        if (string.IsNullOrEmpty(editor.currentBaseFbxHash))
        {
            MCBLogger.LogWarning("[VersionActions] Version fetch aborted because currentBaseFbxHash is empty.");
            editor.serverVersions.Clear();
            UpdateAppliedVersionAndState(); // This will clear the applied state
            editor.isFetching = false;
            editor.Repaint();
            yield break;
        }

        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null)
        {
            MCBLogger.LogWarning("[VersionActions] Version fetch aborted because no asset is selected in the gallery.");
            editor.serverVersions.Clear();
            editor.recommendedVersion = null;
            editor.isFetching = false;
            editor.Repaint();
            yield break;
        }

        string currentFbxPath = GetCurrentFBXPath();
        string url = $"{MCBUtils.getApiUrl()}{MCBUtils.GetAssetVersionEndpoint(selectedAsset.id)}?d={editor.currentBaseFbxHash}&t={editor.authToken}";
        string sanitizedUrl = System.Text.RegularExpressions.Regex.Replace(url, @"([?&]t=)([^&]+)", "$1<redacted>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        MCBLogger.Log($"[VersionActions] Starting version fetch. assetId={selectedAsset.id} | url={sanitizedUrl} | currentFbxPath={currentFbxPath} | currentBaseFbxHash={editor.currentBaseFbxHash}");
        var fetchTask = networkService.FetchVersionsAsync(url);
        
        while (!fetchTask.IsCompleted)
        {
            yield return null;
        }
        
        var (success, response, error) = fetchTask.Result;
        if (success)
        {
            editor.serverVersions = response?.versions ?? new System.Collections.Generic.List<CustomBaseVersion>();
            editor.recommendedVersion = editor.serverVersions.FirstOrDefault(v => v.version == response.recommendedVersion);
            UpdateAppliedVersionAndState();
            SmartSelectVersion();
        }
        else
        {
            editor.selectedVersionForAction = null;
            // Handle access denied specially (error encoded as ACCESS_DENIED:{assetId})
            if (!string.IsNullOrEmpty(error) && error.StartsWith("ACCESS_DENIED:"))
            {
                editor.accessDeniedAssetId = error.Substring("ACCESS_DENIED:".Length);
                editor.warningsModule.Clear(); // Do not show generic error box
                editor.serverVersions.Clear();
                editor.recommendedVersion = null;
                UpdateAppliedVersionAndState(); // Clear state on error too
            }
            else
            {
                MCBLogger.LogError($"[VersionActions] Version fetch failed. currentFbxPath={currentFbxPath} | currentBaseFbxHash={editor.currentBaseFbxHash} | error={error}");
                editor.warningsModule.AddWarning(error, MessageType.Error, "Fetch failed");
                editor.serverVersions.Clear();
                editor.recommendedVersion = null;
                UpdateAppliedVersionAndState(); // Clear state on error too
            }
        }
        
        editor.isFetching = false;
        editor.Repaint();
    }
    
    private IEnumerator DownloadVersionCoroutine(CustomBaseVersion version, bool applyAfter)
    {
        if (editor.isDownloading) yield break;
        editor.isDownloading = true;
        editor.warningsModule.Clear();
        editor.Repaint();

        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null)
        {
            MCBLogger.LogWarning("[VersionActions] Download aborted because no asset is selected in the gallery.");
            editor.isDownloading = false;
            editor.Repaint();
            yield break;
        }
        
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"mcb_dl_{Guid.NewGuid()}.zip");
        string url = $"{MCBUtils.getApiUrl()}{MCBUtils.GetAssetModelEndpoint(selectedAsset.id)}?version={version.version}&d={editor.currentBaseFbxHash}&t={editor.authToken}";
        
        // --- Setup phase (no yield returns) ---
        var downloadTask = networkService.DownloadFileAsync(url, tempZipPath);
        bool setupSucceeded = downloadTask != null;
        
        if (!setupSucceeded)
        {
            editor.warningsModule.AddWarning("Failed to start download task", MessageType.Error, "Download failed");
            editor.isDownloading = false;
            editor.Repaint();
            yield break;
        }
        
        // --- Download phase (with yield returns, NOT in try/catch) ---
        while (!downloadTask.IsCompleted)
        {
            yield return null;
        }
        
        // --- Process result and cleanup ---
        var (success, error) = downloadTask.Result;
        bool extractionSucceeded = false;
        string tempExtractPath = null;
        
        try
        {
            if (!success)
            {
                editor.warningsModule.AddWarning(error, MessageType.Error, "Download failed");
            }
            else
            {
                string finalDest = MCBUtils.GetVersionDataPath(version);
                tempExtractPath = Path.Combine(Path.GetTempPath(), $"mcb_extract_{Guid.NewGuid()}");
                
                fileManagerService.UnzipAndMove(tempZipPath, tempExtractPath, finalDest);
                extractionSucceeded = true;
                
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
        }
        catch (Exception e) 
        { 
            editor.warningsModule.AddWarning($"Extraction failed: {e.Message}", MessageType.Error, "Extraction failed"); 
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            if (!string.IsNullOrEmpty(tempExtractPath) && Directory.Exists(tempExtractPath)) 
                Directory.Delete(tempExtractPath, true);
            
            editor.isDownloading = false;
            editor.Repaint();
        }
        
        // --- Post-processing phase (outside try/catch) ---
        if (extractionSucceeded)
        {
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                yield return null;
            }
            MCBLogger.Log("[VersionActions] Editor finished pending compilation/import work.");            
            if (applyAfter) 
            {
                editor.selectedVersionForAction = version;
                yield return ApplyOrResetCoroutine(version, false);
            }
        }
    }

    private IEnumerator DeleteVersionCoroutine(CustomBaseVersion version)
    {
        if (editor.isDeleting) yield break;
        editor.isDeleting = true;
        editor.warningsModule.Clear();
        editor.Repaint();

        string path = MCBUtils.GetVersionDataPath(version);
        bool deleted = false;
        try
        {
            fileManagerService.DeleteVersionFolder(path);
            deleted = true;
        }
        catch (Exception e) { editor.warningsModule.AddWarning($"Failed to delete folder: {e.Message}", MessageType.Error, "Deletion failed"); }

        if (deleted)
        {
            if (version.isUnsubmitted)
            {
                editor.creatorModule.RemoveUnsubmittedVersion(version);
            }
            AssetDatabase.Refresh();
            editor.LoadImportedVersions();
        }

        editor.isDeleting = false;
        editor.Repaint();
    }

    internal IEnumerator ApplyOrResetCoroutine(CustomBaseVersion version, bool isReset)
    {
        var root = editor.customBaseTarget.transform.root;
        bool preserveBlendshapeValues = editor.customBaseTarget != null && editor.customBaseTarget.preserveBlendshapeValuesOnVersionSwitch;
        var blendshapeSnapshot = preserveBlendshapeValues ? CaptureBlendshapeState(root) : null;
        fileManagerService.RemoveExistingLogic(root);

        MCBLogger.Log($"[VersionActions] ApplyOrReset start (reset={isReset}, version={(version != null ? version.version : "null")})");

        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrEmpty(fbxPath)) yield break;
        
        bool success = false;
        try
        {
            if (isReset)
            {
                fileManagerService.ForceRestoreBackupAtPath(fbxPath);
            }
            else
            {
                if (version == null) throw new ArgumentNullException(nameof(version), "A version must be provided to apply.");

                ApplyVersionModelFilePatches(version, fbxPath);
            }
            success = true;
        }
        catch(Exception e)
        {
            MCBLogger.LogError($"[MCB] Operation failed: {e.Message}");
            if(!isReset && fileManagerService.BackupExists(fbxPath)) fileManagerService.RestoreBackup(fbxPath);
        }
        
        if (success)
        {
            var affectedFbxPaths = GetAffectedFbxPaths(version, fbxPath);
            foreach (string affectedPath in affectedFbxPaths)
            {
                MCBLogger.Log($"[VersionActions] Importing modified FBX at {affectedPath}");
                AssetDatabase.ImportAsset(affectedPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            MCBLogger.Log("[VersionActions] FBX import completed.");
            
            // Wait until Unity has finished compiling (if any compilation was triggered).
            // This is essential to prevent race conditions.
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                yield return null;
            }
            MCBLogger.Log("[VersionActions] Editor finished pending compilation/import work.");
            //  --- END CRITICAL FIX ---
            
            var versionForAssets = isReset ? (editor.customBaseTarget.appliedCustomBaseVersion ?? editor.selectedVersionForAction) : version;
            if (versionForAssets != null)
            {
                ApplyAvatarImportsForVersion(root, versionForAssets, isReset);
                while (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    yield return null;
                }
                MCBLogger.Log("[VersionActions] Avatar import completed.");

                if (!isReset)
                {
                    string packagePath = MCBUtils.GetLogicPackagePath(versionForAssets);
                    fileManagerService.InstantiateLogicPrefab(packagePath, root);
                }
            }
            editor.customBaseTarget.appliedCustomBaseVersion = version;
            SyncAppliedVersionBlendshapeLinkCache(version);
            
            // Check feature flags
            bool hasCustomVeins = !isReset && version != null && (version.extraCustomization?.Contains("customVeins") ?? false);
            bool hasDynamicNormalBody = !isReset && version != null && (version.extraCustomization?.Contains("dynamicNormalBody") ?? false);
            bool hasDynamicNormalFlexing = !isReset && version != null && (version.extraCustomization?.Contains("dynamicNormalFlexing") ?? false);
            bool shouldApplyDynamicNormals = (hasDynamicNormalBody || hasDynamicNormalFlexing) && editor.customBaseTarget.useDynamicNormals;
            
            // Apply or remove dynamic normals based on version feature flags
            // CRITICAL FIX: Execute INSIDE the coroutine (not via delayCall) with proper yield statements
            var dynamicNormalsService = new DynamicNormalsService(editor);

            if (!shouldApplyDynamicNormals)
            {
                MCBLogger.Log("[VersionActions] Removing dynamic normals.");
                dynamicNormalsService.Remove();
                
                // Wait for any asset processing triggered by removal
                yield return null;
                while (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    yield return null;
                }
                MCBLogger.Log("[VersionActions] Dynamic normals removal completed.");
            }

            RefreshTargetMeshesFromFBXs(root, affectedFbxPaths);
            
            if (shouldApplyDynamicNormals)
            {
                bool applyBody = hasDynamicNormalBody;
                bool applyFlex = hasDynamicNormalFlexing;
                MCBLogger.Log("[VersionActions] Applying dynamic normals.");
                dynamicNormalsService.Apply(applyBody, applyFlex);
                
                // Wait for any asset processing triggered by dynamic normals
                yield return null;
                while (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    yield return null;
                }
                MCBLogger.Log("[VersionActions] Dynamic normals application completed.");
            }
            
            // Apply or remove custom veins based on version feature flag
            var materialService = new MaterialService(root);
            var targetMaterialRenderers = materialService.GetSkinnedMeshRenderersForFbxPaths(affectedFbxPaths);
            
            if (hasCustomVeins)
            {
                // Apply custom veins
                string versionFolder = MCBUtils.GetVersionDataPath(version);
                string veinsNormalPath = MCBUtils.CombineUnityPath(versionFolder, "veins normal.png");
                string veinsNormalAbsolutePath = Path.GetFullPath(veinsNormalPath);

                if (File.Exists(veinsNormalAbsolutePath))
                {
                    bool veinsApplied = false;
                    foreach (var renderer in targetMaterialRenderers)
                    {
                        if (materialService.SetDetailNormalMap(renderer, veinsNormalPath))
                        {
                            materialService.SetDetailNormalOpacity(renderer, 1.0f);
                            veinsApplied = true;
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
                foreach (var renderer in targetMaterialRenderers)
                {
                    materialService.RemoveDetailNormalMap(renderer);
                }
                // Sync the toggle state - set to false when removing veins
                EditorPrefs.SetBool(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY, false);
                MCBLogger.Log("[VersionActions] Custom veins removed");
            }
            
            // Restore blendshape values by name after all mesh swaps are complete.
            if (!isReset && version != null)
            {
                if (preserveBlendshapeValues)
                {
                    RestoreBlendshapeState(root, blendshapeSnapshot, BuildBlendshapeDefaultLookup(version));
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
                // Clear custom overrides when resetting
                editor.customBaseTarget.customBlendshapeOverrideNames.Clear();
                editor.customBaseTarget.customBlendshapeOverrideValues.Clear();
                editor.customBaseTarget.useCustomSliderSelection = false;
                editor.customBaseTarget.customSliderSelectionNames.Clear();
                editor.customBaseTarget.useCustomSlidersState = false;
                editor.customBaseTarget.customSlidersState = true;
                SyncAppliedVersionBlendshapeLinkCache(null);

                // Delete sliders GameObject on reset
                var slidersTransform = root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
                if (slidersTransform != null)
                {
                    MCBLogger.Log("[VersionActions] Reset requested. Deleting sliders GameObject.");
                    Undo.DestroyObjectImmediate(slidersTransform.gameObject);
                }
            }
        }
        
        MCBLogger.Log("[VersionActions] ApplyOrResetCoroutine completed. Updating hash.");
        // Force a recalculation of the current FBX hash and applied state
        EditorCoroutineUtility.StartCoroutineOwnerless(RecalculateCurrentFbxHashCoroutine());

        EditorUtility.SetDirty(editor.customBaseTarget);
        editor.Repaint();
    }

    private void ApplyVersionModelFilePatches(CustomBaseVersion version, string fallbackFbxPath)
    {
        var patchFiles = version.versionFiles?
            .Where(file => file != null && string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (patchFiles == null || patchFiles.Length == 0)
        {
            MCBLogger.Log("[VersionActions] Version has no model file patches. Skipping FBX transformation.");
            return;
        }

        foreach (var patchFile in patchFiles)
        {
            string transform = string.IsNullOrWhiteSpace(patchFile.transform)
                ? "XOR_BIN_TO_FBX"
                : patchFile.transform;

            if (string.Equals(transform, "XOR_BIN_TO_UNITY_ASSET", StringComparison.OrdinalIgnoreCase))
            {
                ApplyXorBinToUnityAsset(version, patchFile);
                continue;
            }

            if (string.Equals(transform, "DIRECT_ASSET", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(transform, "XOR_BIN_TO_FBX", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"Unsupported model file transform '{transform}'.");
            }

            string targetFbxPath = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
            string binPath = ResolveVersionPatchPath(version, patchFile);
            ApplyXorBinToFbx(binPath, targetFbxPath);
        }
    }

    private void ApplyXorBinToFbx(string binPath, string fbxPath)
    {
        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
            throw new FileNotFoundException("Apply failed: .bin file not found. Please download or build it first.");

        if (string.IsNullOrWhiteSpace(fbxPath) || !File.Exists(fbxPath))
            throw new FileNotFoundException("Apply failed: target FBX file not found.", fbxPath);

        string originalFbxPath = fbxPath.EndsWith(FileManagerService.OriginalSuffix) ? fbxPath : fbxPath + FileManagerService.OriginalSuffix;
        if (!File.Exists(originalFbxPath))
        {
            fileManagerService.CreateBackup(fbxPath);
            originalFbxPath = fbxPath + FileManagerService.OriginalSuffix;
        }

        byte[] baseData = File.ReadAllBytes(originalFbxPath);
        byte[] binData = File.ReadAllBytes(binPath);
        byte[] transformedData = fileManagerService.XorTransform(baseData, binData);

        File.WriteAllBytes(fbxPath, transformedData);
    }

    private string ResolveTargetFbxPath(CustomBaseVersion version, ModelFileData patchFile, string fallbackFbxPath)
    {
        var source = version.sourceFiles?.FirstOrDefault(file =>
            file != null &&
            patchFile.sourceModelFileId.HasValue &&
            file.id == patchFile.sourceModelFileId.Value);

        string sourcePath = source?.path;
        if (string.IsNullOrWhiteSpace(sourcePath) && patchFile.metadata != null)
        {
            if (patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue) ||
                patchFile.metadata.TryGetValue("targetPath", out sourcePathValue))
            {
                sourcePath = sourcePathValue?.ToString();
            }
        }
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            sourcePath = version.sourceFiles?.FirstOrDefault(file =>
                file != null &&
                string.Equals(file.path, patchFile.path, StringComparison.OrdinalIgnoreCase))?.path;
        }

        return string.IsNullOrWhiteSpace(sourcePath)
            ? fallbackFbxPath
            : MCBUtils.ToUnityPath(sourcePath);
    }

    private string ResolveVersionPatchPath(CustomBaseVersion version, ModelFileData patchFile)
    {
        string versionFolder = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrWhiteSpace(versionFolder)) return null;

        string candidateName = string.IsNullOrWhiteSpace(patchFile.path)
            ? null
            : Path.GetFileName(patchFile.path);
        if (!string.IsNullOrWhiteSpace(candidateName))
        {
            string candidatePath = MCBUtils.CombineUnityPath(versionFolder, candidateName);
            if (File.Exists(Path.GetFullPath(candidatePath))) return candidatePath;
        }

        return MCBUtils.GetVersionBinPath(version);
    }

    private void ApplyXorBinToUnityAsset(CustomBaseVersion version, ModelFileData patchFile)
    {
        // TODO: Implement the native Unity mesh asset workflow. This must still decrypt only on the client:
        // original FBX bytes + encrypted BIN patch -> generated Unity Mesh/Prefab assets.
        throw new NotSupportedException("Unity .asset mesh patch delivery is not implemented yet.");
    }

    private List<string> GetAffectedFbxPaths(CustomBaseVersion version, string fallbackFbxPath)
    {
        var paths = new List<string>();
        if (version?.versionFiles != null)
        {
            foreach (var patchFile in version.versionFiles)
            {
                if (patchFile == null) continue;
                if (!string.Equals(patchFile.transform ?? "XOR_BIN_TO_FBX", "XOR_BIN_TO_FBX", StringComparison.OrdinalIgnoreCase)) continue;
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

    private void ApplyAvatarImportsForVersion(Transform root, CustomBaseVersion version, bool isReset)
    {
        if (isReset)
        {
            string defaultAvatarPath = MCBUtils.GetDefaultAvatarPath(version);
            for (int i = 0; i < editor.baseFbxFilesProp.arraySize; i++)
            {
                var fbxGameObject = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                fileManagerService.ApplyAvatarToModel(root, fbxGameObject, defaultAvatarPath);
            }
            return;
        }

        foreach (var patchFile in version.versionFiles ?? Array.Empty<ModelFileData>())
        {
            string avatarPath = null;
            if (patchFile?.metadata != null && patchFile.metadata.TryGetValue("customAvatarPath", out object avatarPathValue))
            {
                avatarPath = avatarPathValue?.ToString();
            }

            if (string.IsNullOrWhiteSpace(avatarPath))
            {
                MCBLogger.Log($"[VersionActions] Patch file {patchFile?.path} has no custom avatar. Skipping avatar import.");
                continue;
            }

            string sourcePath = ResolveTargetFbxPath(version, patchFile, null);
            var fbxGameObject = string.IsNullOrWhiteSpace(sourcePath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            MCBLogger.Log($"[VersionActions] Applying avatar import settings from {avatarPath} to {sourcePath}");
            fileManagerService.ApplyAvatarToModel(root, fbxGameObject, avatarPath);
        }
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

    private Dictionary<string, float> BuildBlendshapeDefaultLookup(CustomBaseVersion version)
    {
        var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        if (version?.customBlendshapes == null) return defaults;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null || string.IsNullOrEmpty(entry.name)) continue;
            defaults[entry.name] = ParseBlendshapeDefaultValue(entry.defaultValue);
        }

        return defaults;
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
            float valueToApply = defaultValue;
            int overrideIdx = editor.customBaseTarget.customBlendshapeOverrideNames.IndexOf(entry.name);
            if (overrideIdx >= 0 && overrideIdx < editor.customBaseTarget.customBlendshapeOverrideValues.Count)
            {
                valueToApply = editor.customBaseTarget.customBlendshapeOverrideValues[overrideIdx];
            }

            foreach (var renderer in renderers)
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(entry.name);
                if (index >= 0)
                {
                    renderer.SetBlendShapeWeight(index, valueToApply);
                    EditorUtility.SetDirty(renderer);
                }
            }

            editor.customBaseTarget.blendShapeValues.Add(valueToApply);
        }

    }

    private IEnumerable<SkinnedMeshRenderer> GetTargetBlendshapeRenderers(Transform root)
    {
        if (root == null) yield break;

        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (editor.baseFbxFilesProp != null)
        {
            for (int i = 0; i < editor.baseFbxFilesProp.arraySize; i++)
            {
                var fbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                string path = fbx != null ? AssetDatabase.GetAssetPath(fbx) : null;
                if (!string.IsNullOrWhiteSpace(path)) targetPaths.Add(MCBUtils.ToUnityPath(path));
            }
        }

        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer?.sharedMesh == null || renderer.sharedMesh.blendShapeCount == 0) continue;
            if (targetPaths.Count == 0)
            {
                yield return renderer;
                continue;
            }

            string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh));
            if (!string.IsNullOrWhiteSpace(meshPath) && targetPaths.Contains(meshPath))
            {
                yield return renderer;
            }
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

    private void RefreshTargetMeshesFromFBXs(Transform root, IEnumerable<string> fbxPaths)
    {
        if (root == null || fbxPaths == null) return;

        var targetPaths = new HashSet<string>(
            fbxPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(MCBUtils.ToUnityPath),
            StringComparer.OrdinalIgnoreCase);
        if (targetPaths.Count == 0) return;

        var meshesByFbxPath = new Dictionary<string, Dictionary<string, Mesh>>(StringComparer.OrdinalIgnoreCase);
        foreach (string fbxPath in targetPaths)
        {
            var fbxObject = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbxObject == null) continue;

            var meshLookup = new Dictionary<string, Mesh>(StringComparer.Ordinal);
            foreach (var smr in fbxObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr?.sharedMesh == null || meshLookup.ContainsKey(smr.sharedMesh.name)) continue;
                meshLookup.Add(smr.sharedMesh.name, smr.sharedMesh);
            }
            meshesByFbxPath[fbxPath] = meshLookup;
        }

        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr?.sharedMesh == null) continue;
            string currentAssetPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(smr.sharedMesh));
            if (string.IsNullOrWhiteSpace(currentAssetPath) || !targetPaths.Contains(currentAssetPath)) continue;
            if (!meshesByFbxPath.TryGetValue(currentAssetPath, out var meshLookup)) continue;
            if (!meshLookup.TryGetValue(smr.sharedMesh.name, out var replacementMesh)) continue;

            Undo.RecordObject(smr, "Refresh Mesh from FBX");
            smr.sharedMesh = replacementMesh;
            EditorUtility.SetDirty(smr);
        }
    }
    
    public void DisplayErrors()
    {
        // Deprecated: Errors are now handled by WarningsModule
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
    
    private IEnumerator RecalculateCurrentFbxHashCoroutine()
    {
        var paths = GetCurrentFBXPaths();
        string path = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(path)) yield break;

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

        // Ensure any imports/updates are finished
        while (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            yield return null;
        }

        // Calculate hashes
        var calcTask = hashService.CalculateFBXHashesAsync(Path.GetFullPath(path));
        while (!calcTask.IsCompleted)
        {
            yield return null;
        }
        var (currentHash, originalHash) = calcTask.Result;

        foreach (string targetPath in paths.Skip(1))
        {
            var targetHashTask = hashService.CalculateFileHashAsync(targetPath, null, true);
            while (!targetHashTask.IsCompleted)
            {
                yield return null;
            }
        }

        // Update editor state
        editor.currentBaseFbxHash = hasBackup ? originalHash : currentHash;
        UpdateAppliedVersionAndState(currentHash);
        editor.Repaint();
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
                MCBLogger.Log("[VersionActions] UpdateAppliedVersionAndState deferred (hash not cached yet).");
                return;
            }
        }

        // Track applied hash consistently
        editor.currentAppliedFbxHash = currentFileHash;

        var candidateVersions = editor.GetAllVersions() ?? new System.Collections.Generic.List<CustomBaseVersion>();
        MCBLogger.Log($"[VersionActions] UpdateAppliedVersionAndState hash={currentFileHash} candidates={candidateVersions.Count}");

        // If we have no candidates yet, don't decide custom state prematurely
        if (string.IsNullOrEmpty(currentFileHash) || (!editor.fetchAttempted && candidateVersions.Count == 0))
        {
            MCBLogger.Log("[VersionActions] Deferring state detection (waiting for versions).\n" +
                              $"fetchAttempted={editor.fetchAttempted}, candidates={candidateVersions.Count}");
            return;
        }

        var matchingVersion = FindMatchingAppliedVersion(candidateVersions, currentFileHash);

        if (editor?.customBaseTarget == null) return;
        Undo.RecordObject(editor.customBaseTarget, "Update MCB State");

        if (matchingVersion != null)
        {
            MCBLogger.Log($"[VersionActions] Matched applied version: {matchingVersion.version}");
            editor.isCustomBase = true;
            editor.currentIsCustom = false;
            editor.customBaseTarget.appliedCustomBaseVersion = matchingVersion;
            SyncAppliedVersionBlendshapeLinkCache(matchingVersion);
        }
        else
        {
            editor.isCustomBase = false;
            editor.customBaseTarget.appliedCustomBaseVersion = null;
            
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
                        var entry = UserCustomVersionService.Instance.CreateFromCurrent(fbxPath, currentFileHash, editor.customBaseTarget.transform.root);
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
            
            MCBLogger.Log("[VersionActions] No matching version hash found. Marking state as non-custom-base.");
        }

        EditorUtility.SetDirty(editor.customBaseTarget);
    }

    private void SyncAppliedVersionBlendshapeLinkCache(CustomBaseVersion version)
    {
        if (editor?.customBaseTarget == null) return;

        var cache = editor.customBaseTarget.appliedVersionBlendshapeLinksCache;
        if (cache == null)
        {
            cache = new List<CreatorBlendshapeEntry>();
            editor.customBaseTarget.appliedVersionBlendshapeLinksCache = cache;
        }

        cache.Clear();
        if (version?.customBlendshapes == null || version.customBlendshapes.Length == 0) return;

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

        var directMatch = candidateVersions.FirstOrDefault(v =>
            !string.IsNullOrEmpty(v.appliedCustomAviHash) &&
            v.appliedCustomAviHash.Equals(currentFileHash, StringComparison.OrdinalIgnoreCase));
        if (directMatch != null)
        {
            return directMatch;
        }

        var currentHashesByPath = GetCurrentTargetHashes(currentFileHash);
        if (currentHashesByPath.Count == 0)
        {
            return null;
        }

        return candidateVersions.FirstOrDefault(version => DoesVersionMatchCurrentTargetHashes(version, currentHashesByPath));
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

    private bool DoesVersionMatchCurrentTargetHashes(CustomBaseVersion version, Dictionary<string, string> currentHashesByPath)
    {
        if (version?.sourceFiles == null || version.sourceFiles.Length == 0)
        {
            return false;
        }

        bool comparedAny = false;
        foreach (var sourceFile in version.sourceFiles)
        {
            if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.path))
            {
                continue;
            }

            string sourcePath = MCBUtils.ToUnityPath(sourceFile.path);
            if (!currentHashesByPath.TryGetValue(sourcePath, out string currentHash))
            {
                return false;
            }

            string expectedHash = ResolveExpectedHashForSource(version, sourceFile);
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                return false;
            }

            if (!expectedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            comparedAny = true;
        }

        return comparedAny;
    }

    private string ResolveExpectedHashForSource(CustomBaseVersion version, ModelFileData sourceFile)
    {
        var patchFile = version.versionFiles?.FirstOrDefault(file =>
            file != null &&
            string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.transform ?? "XOR_BIN_TO_FBX", "XOR_BIN_TO_FBX", StringComparison.OrdinalIgnoreCase) &&
            IsPatchForSourceFile(file, sourceFile));

        if (!string.IsNullOrWhiteSpace(patchFile?.outputHash))
        {
            return patchFile.outputHash;
        }

        return sourceFile.hash;
    }

    private bool IsPatchForSourceFile(ModelFileData patchFile, ModelFileData sourceFile)
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
            (patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue) ||
             patchFile.metadata.TryGetValue("targetPath", out sourcePathValue)))
        {
            patchSourcePath = sourcePathValue?.ToString();
        }

        return !string.IsNullOrWhiteSpace(sourcePath) &&
               !string.IsNullOrWhiteSpace(patchSourcePath) &&
               string.Equals(sourcePath, MCBUtils.ToUnityPath(patchSourcePath), StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerator ApplyCustomVersionCoroutine(UserCustomVersionEntry entry)
    {
        if (entry == null) yield break;
        if (!FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION)) yield break;
        var root = editor.customBaseTarget.transform.root;
        fileManagerService.RemoveExistingLogic(root);

        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrEmpty(fbxPath)) yield break;

        bool success = false;
        try
        {
            // Ensure original backup exists; if not, create one now from current file
            if (!fileManagerService.BackupExists(fbxPath))
            {
                fileManagerService.CreateBackup(fbxPath);
            }

            // Copy saved custom FBX over the current FBX
            string srcUnity = MCBUtils.ToUnityPath(entry.backupFbxPath);
            string srcAbs = System.IO.Path.GetFullPath(srcUnity);
            string dstUnity = MCBUtils.ToUnityPath(fbxPath);
            string dstAbs = System.IO.Path.GetFullPath(dstUnity);
            if (!System.IO.File.Exists(srcAbs)) throw new System.IO.FileNotFoundException("Saved custom FBX not found", srcAbs);

            System.IO.File.Copy(srcAbs, dstAbs, true);
            success = true;
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to apply custom version: {ex.Message}");
        }

        if (success)
        {
            // Force reimport
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            while (EditorApplication.isCompiling || EditorApplication.isUpdating) { yield return null; }

            // Apply avatar if we saved one
            var fbxGameObject = editor.baseFbxFilesProp.arraySize > 0 ? editor.baseFbxFilesProp.GetArrayElementAtIndex(0).objectReferenceValue as GameObject : null;
            if (!string.IsNullOrEmpty(entry.appliedAvatarAsset))
            {
                fileManagerService.ApplyAvatarToModel(root, fbxGameObject, entry.appliedAvatarAsset);
                while (EditorApplication.isCompiling || EditorApplication.isUpdating) { yield return null; }
            }

            // Update state flags
            editor.isCustomBase = false;
            editor.customBaseTarget.appliedCustomBaseVersion = null;
            SyncAppliedVersionBlendshapeLinkCache(null);
            editor.currentIsCustom = true;

            // Recalculate hashes and update state
            StartRecalculateCurrentFbxHash();
        }
    }
}
#endif
