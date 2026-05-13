#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCBEditorUtils;

public class VersionActions
{
    private const float BlendshapeWeightEpsilon = 0.001f;
    private const string AdvancedMeshDeliveryMode = "UNITY_NATIVE_MESH_ASSET";
    private const string FbxReplacementDeliveryMode = "FBX_REPLACEMENT";

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
                UnityEngine.Debug.Log($"[VersionApplyProfile] START {operation}");
            }
        }

        public void Mark(string label)
        {
            if (!enabled)
            {
                return;
            }

            UnityEngine.Debug.Log($"[VersionApplyProfile] {label}: step={step.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms");
            step.Restart();
        }

        public void Done(string label = "DONE")
        {
            if (!enabled)
            {
                return;
            }

            UnityEngine.Debug.Log($"[VersionApplyProfile] {label} {operation}: total={total.Elapsed.TotalMilliseconds:F1} ms");
        }
    }

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
        if (!editor.HasServerAccess) yield break;
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
            try
            {
                PrecacheAdvancedMeshPayloadAssets(version);
            }
            catch (Exception ex)
            {
                editor.warningsModule.AddWarning($"Advanced mesh cache preparation failed: {ex.Message}", MessageType.Error, "Download failed");
                editor.Repaint();
                yield break;
            }

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
        var versionForAssets = isReset ? (ResolvePersistedAppliedVersion() ?? editor.selectedVersionForAction) : version;
        bool versionUsesAdvancedMesh = NativeMeshPayloadService.VersionUsesAdvancedMesh(versionForAssets);
        var profile = new ApplyTimingProfile(versionForAssets, isReset, versionUsesAdvancedMesh);
        var dynamicNormalsService = new DynamicNormalsService(editor);
        profile.Mark("Setup target, version state, and blendshape snapshot");

        dynamicNormalsService.Remove();
        fileManagerService.RemoveExistingLogic(root);
        profile.Mark("Removed current DynamicNormals objects and existing MCB logic");

        MCBLogger.Log($"[VersionActions] ApplyOrReset start (reset={isReset}, version={(version != null ? version.version : "null")})");

        string fbxPath = GetCurrentFBXPath();
        profile.Mark("Resolved current FBX path");
        if (string.IsNullOrEmpty(fbxPath))
        {
            profile.Done("ABORT missing FBX path");
            yield break;
        }
        
        bool success = false;
        try
        {
            if (isReset)
            {
                RestoreBackupsForVersion(versionForAssets, fbxPath, !versionUsesAdvancedMesh);
                if (versionUsesAdvancedMesh)
                {
                    NativeMeshPayloadService.RestoreOriginalMeshesFromFbx(
                        root,
                        versionForAssets,
                        GetResetAffectedFbxPaths(versionForAssets, fbxPath));
                }
            }
            else
            {
                if (version == null) throw new ArgumentNullException(nameof(version), "A version must be provided to apply.");

                ApplyVersionModelFilePatches(version, fbxPath);
            }
            success = true;
            profile.Mark(isReset ? "Restored base/native meshes" : "Applied model file patches");
        }
        catch(Exception e)
        {
            profile.Mark("Model patch/reset failed");
            MCBLogger.LogError($"[MCB] Operation failed: {e.Message}");
            if(!isReset && fileManagerService.BackupExists(fbxPath)) fileManagerService.RestoreBackup(fbxPath);
        }
        
        if (success)
        {
            var affectedFbxPaths = isReset
                ? GetResetAffectedFbxPaths(versionForAssets, fbxPath)
                : GetAffectedFbxPaths(version, fbxPath);
            var fbxImportPaths = isReset
                ? (versionUsesAdvancedMesh ? new List<string>() : affectedFbxPaths)
                : GetFbxImportPaths(version, fbxPath);
            profile.Mark("Resolved affected FBX/import path lists");
            foreach (string affectedPath in fbxImportPaths)
            {
                MCBLogger.Log($"[VersionActions] Importing modified FBX at {affectedPath}");
                AssetDatabase.ImportAsset(affectedPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            MCBLogger.Log(fbxImportPaths.Count > 0
                ? "[VersionActions] FBX import completed."
                : "[VersionActions] No FBX import required for this version operation.");
            
            // Wait until Unity has finished compiling (if any compilation was triggered).
            // This is essential to prevent race conditions.
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                yield return null;
            }
            MCBLogger.Log("[VersionActions] Editor finished pending compilation/import work.");
            profile.Mark("FBX import and pending editor update wait");
            //  --- END CRITICAL FIX ---
            
            if (isReset)
            {
                if (versionUsesAdvancedMesh)
                {
                    ApplyDefaultAvatarToRootForReset(root, versionForAssets);
                }
                else
                {
                    ApplyDefaultAvatarImportsForReset(root, versionForAssets);
                }
                while (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    yield return null;
                }
                MCBLogger.Log("[VersionActions] Default avatar import completed.");
                profile.Mark("Default avatar import/reset wait");
                if (versionUsesAdvancedMesh)
                {
                    NativeMeshPayloadService.RestoreOriginalAuthoringPoseFromFbx(root, GetResetAffectedFbxPaths(versionForAssets, fbxPath));
                    profile.Mark("Restored native mesh reset authoring pose");
                    MCBLogger.Log("[VersionActions] Restored source authoring pose after advanced mesh reset.");
                }
            }
            else if (versionForAssets != null && versionForAssets != VersionListDrawer.RESET_VERSION)
            {
                ApplyAvatarImportsForVersion(root, versionForAssets, isReset);
                profile.Mark("Applied avatar import settings for version");
                while (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    yield return null;
                }
                MCBLogger.Log("[VersionActions] Avatar import completed.");
                profile.Mark("Avatar import/update wait");
                if (versionUsesAdvancedMesh)
                {
                    ApplyAdvancedMeshAuthoringPose(versionForAssets, fbxPath);
                    profile.Mark("Restored advanced mesh authoring pose");
                    MCBLogger.Log("[VersionActions] Restored payload authoring pose after advanced mesh apply.");
                }

                if (!isReset)
                {
                    string packagePath = MCBUtils.GetLogicPackagePath(versionForAssets);
                    IEnumerator importLogicRoutine = null;
                    try
                    {
                        importLogicRoutine = fileManagerService.InstantiateLogicPrefabCoroutine(packagePath, root);
                    }
                    catch (Exception ex)
                    {
                        editor.warningsModule.AddWarning(ex.Message, MessageType.Error, "Logic package import failed");
                        yield break;
                    }

                    while (true)
                    {
                        object current;
                        try
                        {
                            if (importLogicRoutine == null || !importLogicRoutine.MoveNext())
                            {
                                break;
                            }

                            current = importLogicRoutine.Current;
                        }
                        catch (Exception ex)
                        {
                            editor.warningsModule.AddWarning(ex.Message, MessageType.Error, "Logic package import failed");
                            yield break;
                        }

                        yield return current;
                    }
                    profile.Mark("Imported/instantiated logic prefab");
                }
            }
            if (isReset)
            {
                ClearAppliedVersionState();
            }
            else
            {
                PersistAppliedVersionState(version);
            }
            profile.Mark("Updated persisted applied-version state");
            
            // Check feature flags
            bool hasCustomVeins = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "customVeins");
            bool hasDynamicNormalBody = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "dynamicNormalBody");
            bool hasDynamicNormalFlexing = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "dynamicNormalFlexing");
            bool shouldApplyDynamicNormals = (hasDynamicNormalBody || hasDynamicNormalFlexing) && editor.customBaseTarget.useDynamicNormals;
            
            // Apply or remove dynamic normals based on version feature flags
            // CRITICAL FIX: Execute INSIDE the coroutine (not via delayCall) with proper yield statements
            if (!shouldApplyDynamicNormals && !versionUsesAdvancedMesh)
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
                profile.Mark("Removed DynamicNormals and waited for editor update");
            }
            else if (!shouldApplyDynamicNormals && versionUsesAdvancedMesh)
            {
                MCBLogger.Log("[VersionActions] Native mesh payload already replaced the active mesh; dynamic normals removal is not required.");
                profile.Mark("Skipped DynamicNormals removal for advanced native mesh");
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
                    dynamicNormalsService.Apply(applyBody, applyFlex);
                    
                    // Wait for any asset processing triggered by dynamic normals
                    yield return null;
                    while (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    {
                        yield return null;
                    }
                    MCBLogger.Log("[VersionActions] Dynamic normals application completed.");
                    profile.Mark("Applied DynamicNormals and waited for editor update");
                }
            }
            
            // Apply or remove custom veins based on version feature flag
            var materialService = new MaterialService(root);
            var targetMaterialRenderers = versionUsesAdvancedMesh
                ? NativeMeshPayloadService.ResolveRenderersForSourcePaths(root, versionForAssets, affectedFbxPaths)
                : materialService.GetSkinnedMeshRenderersForFbxPaths(affectedFbxPaths);
            profile.Mark($"Resolved material target renderers ({targetMaterialRenderers.Count})");
            
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
            profile.Mark(hasCustomVeins ? "Applied custom veins materials" : "Removed custom veins materials");
            
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
        }

        if (!success)
        {
            editor.Repaint();
            profile.Done("FAILED");
            yield break;
        }
        
        MCBLogger.Log("[VersionActions] ApplyOrResetCoroutine completed. Updating applied state.");
        if (versionUsesAdvancedMesh)
        {
            MCBLogger.Log("[VersionActions] Skipping FBX hash recalculation for native mesh apply because the source FBX bytes were not changed.");
            profile.Mark("Skipped asynchronous FBX hash/state recalculation for advanced native mesh");
        }
        else
        {
            // Force a recalculation of the current FBX hash and applied state
            EditorCoroutineUtility.StartCoroutineOwnerless(RecalculateCurrentFbxHashCoroutine());
            profile.Mark("Started asynchronous FBX hash/state recalculation");
        }

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
            return;
        }

        foreach (var patchFile in patchFiles)
        {
            if (string.IsNullOrWhiteSpace(patchFile.transform))
            {
                throw new InvalidDataException("Model file patch is missing required transform metadata.");
            }

            string transform = patchFile.transform;

            if (string.Equals(transform, NativeMeshPayloadService.TransformName, StringComparison.OrdinalIgnoreCase))
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

    private void RestoreBackupsForVersion(CustomBaseVersion version, string fallbackFbxPath, bool requireBackup = true)
    {
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
            fileManagerService.ForceRestoreBackupAtPath(path);
        }
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
            if (patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue))
            {
                sourcePath = sourcePathValue?.ToString();
            }
        }
        if (string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(fallbackFbxPath))
        {
            throw new InvalidDataException("Model file patch is missing required sourcePath metadata.");
        }

        return string.IsNullOrWhiteSpace(sourcePath) ? null : MCBUtils.ToUnityPath(sourcePath);
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

        string originalFbxPath = ResolveOriginalFbxKeyPath(targetFbxPath);
        string binPath = ResolveVersionPatchPath(version, patchFile);
        NativeMeshPayloadService.ApplyEncryptedPayload(
            editor.customBaseTarget.transform.root,
            version,
            patchFile,
            binPath,
            originalFbxPath,
            fileManagerService);
    }

    private void PrecacheAdvancedMeshPayloadAssets(CustomBaseVersion version)
    {
        if (!NativeMeshPayloadService.VersionUsesAdvancedMesh(version))
        {
            return;
        }

        var advancedPatchFiles = version.versionFiles?
            .Where(file => file != null &&
                           string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(file.transform, NativeMeshPayloadService.TransformName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (advancedPatchFiles == null || advancedPatchFiles.Length == 0)
        {
            throw new InvalidDataException("Advanced mesh version is missing required native mesh payload patches.");
        }

        try
        {
            EditorUtility.DisplayProgressBar("Preparing Advanced Mesh", $"Building cached Unity mesh assets for {version.version}...", 0.75f);
            string fallbackFbxPath = GetCurrentFBXPath();
            foreach (var patchFile in advancedPatchFiles)
            {
                string targetFbxPath = ResolveTargetFbxPath(version, patchFile, fallbackFbxPath);
                if (string.IsNullOrWhiteSpace(targetFbxPath))
                {
                    throw new FileNotFoundException("Advanced mesh cache preparation failed: target FBX path could not be resolved.");
                }

                NativeMeshPayloadService.MaterializeEncryptedPayloadAsset(
                    version,
                    patchFile,
                    ResolveVersionPatchPath(version, patchFile),
                    ResolveOriginalFbxKeyPath(targetFbxPath),
                    fileManagerService);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
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
                           string.Equals(file.transform, NativeMeshPayloadService.TransformName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (advancedPatchFiles == null || advancedPatchFiles.Length == 0)
        {
            throw new InvalidDataException("Advanced mesh version is missing required native mesh payload patches.");
        }

        Transform root = editor.customBaseTarget.transform.root;
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
                ResolveOriginalFbxKeyPath(targetFbxPath),
                fileManagerService);
            NativeMeshPayloadService.ApplyPayloadAuthoringPose(root, payload);
        }
    }

    private static string ResolveOriginalFbxKeyPath(string targetFbxPath)
    {
        string originalFbxPath = targetFbxPath.EndsWith(FileManagerService.OriginalSuffix, StringComparison.OrdinalIgnoreCase)
            ? targetFbxPath
            : targetFbxPath + FileManagerService.OriginalSuffix;
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
                if (!string.Equals(transform, "XOR_BIN_TO_FBX", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(transform, NativeMeshPayloadService.TransformName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(transform, "DIRECT_ASSET", StringComparison.OrdinalIgnoreCase))
                {
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
                if (!string.Equals(patchFile.transform, "XOR_BIN_TO_FBX", StringComparison.OrdinalIgnoreCase)) continue;
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
            ApplyDefaultAvatarImportsForReset(root, version);
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

    private void ApplyDefaultAvatarImportsForReset(Transform root, CustomBaseVersion resetFromVersion)
    {
        string defaultAvatarPath = ResolveDefaultAvatarPathForReset(resetFromVersion);
        if (string.IsNullOrWhiteSpace(defaultAvatarPath))
        {
            MCBLogger.LogWarning("[VersionActions] Reset restored FBX bytes, but no default avatar.asset could be resolved for importer reset.");
            return;
        }

        for (int i = 0; i < editor.baseFbxFilesProp.arraySize; i++)
        {
            var fbxGameObject = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            string fbxPath = fbxGameObject != null ? AssetDatabase.GetAssetPath(fbxGameObject) : null;
            MCBLogger.Log($"[VersionActions] Restoring default avatar import settings from {defaultAvatarPath} to {fbxPath}");
            fileManagerService.ApplyAvatarToModel(root, fbxGameObject, defaultAvatarPath);
        }
    }

    private void ApplyDefaultAvatarToRootForReset(Transform root, CustomBaseVersion resetFromVersion)
    {
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

        const string packageDefaultAvatarPath = "Packages/orbiters.mcb/creator assets/default avatar.asset";
        return AssetExists(packageDefaultAvatarPath) ? packageDefaultAvatarPath : null;
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

        foreach (string fbxPath in targetPaths)
        {
            var smrPaths = SmrPathService.ResolveSmrPathsForSource(version, fbxPath);
            SmrPathService.RefreshTargetMeshesFromFbx(root, fbxPath, smrPaths);
        }
    }

    private bool BodyRendererUsesNativeMeshPayload(Transform root, CustomBaseVersion version)
    {
        if (root == null || version == null)
        {
            return false;
        }

        var bodyRenderer = MeshFinder.FindMeshPrioritizingRoot(root, "Body");
        if (bodyRenderer?.sharedMesh == null)
        {
            return false;
        }

        string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(bodyRenderer.sharedMesh));
        if (string.IsNullOrWhiteSpace(meshPath) ||
            meshPath.IndexOf("Assets/MCB/generated/advancedMeshPayloads/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return version.versionFiles?
            .Where(file => file != null &&
                           string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(file.transform, NativeMeshPayloadService.TransformName, StringComparison.OrdinalIgnoreCase))
            .Any(file => !string.IsNullOrWhiteSpace(file.outputHash) &&
                         meshPath.IndexOf(file.outputHash, StringComparison.OrdinalIgnoreCase) >= 0) == true;
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
        var profile = new System.Diagnostics.Stopwatch();
        var step = new System.Diagnostics.Stopwatch();
        profile.Start();
        step.Start();
        UnityEngine.Debug.Log("[VersionApplyProfile] Async hash/state recalculation START");

        var paths = GetCurrentFBXPaths();
        string path = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(path))
        {
            UnityEngine.Debug.Log($"[VersionApplyProfile] Async hash/state recalculation ABORT missing FBX path total={profile.Elapsed.TotalMilliseconds:F1} ms");
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
        UnityEngine.Debug.Log($"[VersionApplyProfile] Invalidated FBX hash cache: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms paths={paths.Count}");
        step.Restart();

        // Ensure any imports/updates are finished
        while (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            yield return null;
        }
        UnityEngine.Debug.Log($"[VersionApplyProfile] Waited for editor import/update before hashing: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        // Calculate hashes
        var calcTask = hashService.CalculateFBXHashesAsync(Path.GetFullPath(path));
        while (!calcTask.IsCompleted)
        {
            yield return null;
        }
        var (currentHash, originalHash) = calcTask.Result;
        UnityEngine.Debug.Log($"[VersionApplyProfile] Calculated primary FBX/current+backup hashes: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        foreach (string targetPath in paths.Skip(1))
        {
            var targetHashTask = hashService.CalculateFileHashAsync(targetPath, null, true);
            while (!targetHashTask.IsCompleted)
            {
                yield return null;
            }
        }
        UnityEngine.Debug.Log($"[VersionApplyProfile] Calculated additional FBX hashes: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        // Update editor state
        editor.currentBaseFbxHash = hasBackup ? originalHash : currentHash;
        UpdateAppliedVersionAndState(currentHash);
        editor.Repaint();
        UnityEngine.Debug.Log($"[VersionApplyProfile] Async hash/state recalculation DONE: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
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
                if (pendingMarkerVersion != null && NativeMeshPayloadService.VersionUsesAdvancedMesh(pendingMarkerVersion))
                {
                    MCBLogger.Log($"[VersionActions] Keeping applied native mesh version from persisted state before hash is ready: {pendingMarkerVersion.version}");
                    editor.isCustomBase = true;
                    editor.currentIsCustom = false;
                    editor.customBaseTarget.appliedCustomBaseVersion = pendingMarkerVersion;
                    SyncAppliedVersionBlendshapeLinkCache(pendingMarkerVersion);
                    SyncAppliedVersionAnimationPositionOffsetCache(pendingMarkerVersion);
                    EditorUtility.SetDirty(editor.customBaseTarget);
                    return;
                }

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

        var markerVersion = ResolvePersistedAppliedVersion(candidateVersions);
        if (markerVersion != null && NativeMeshPayloadService.VersionUsesAdvancedMesh(markerVersion))
        {
            MCBLogger.Log($"[VersionActions] Keeping applied native mesh version from persisted state: {markerVersion.version}");
            editor.isCustomBase = true;
            editor.currentIsCustom = false;
            editor.customBaseTarget.appliedCustomBaseVersion = markerVersion;
            SyncAppliedVersionBlendshapeLinkCache(markerVersion);
            SyncAppliedVersionAnimationPositionOffsetCache(markerVersion);
            EditorUtility.SetDirty(editor.customBaseTarget);
            return;
        }

        if (matchingVersion != null)
        {
            MCBLogger.Log($"[VersionActions] Matched applied version: {matchingVersion.version}");
            editor.isCustomBase = true;
            editor.currentIsCustom = false;
            editor.customBaseTarget.appliedCustomBaseVersion = matchingVersion;
            SyncAppliedVersionBlendshapeLinkCache(matchingVersion);
            SyncAppliedVersionAnimationPositionOffsetCache(matchingVersion);
        }
        else
        {
            editor.isCustomBase = false;
            editor.customBaseTarget.appliedCustomBaseVersion = null;
            SyncAppliedVersionAnimationPositionOffsetCache(null);
            
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

    private void PersistAppliedVersionState(CustomBaseVersion version)
    {
        if (editor?.customBaseTarget == null || version == null || version == VersionListDrawer.RESET_VERSION)
        {
            ClearAppliedVersionState();
            return;
        }

        editor.customBaseTarget.appliedCustomBaseVersion = version;
        editor.customBaseTarget.appliedCustomBaseAssetId = version.assetId;
        editor.customBaseTarget.appliedCustomBaseVersionString = version.version ?? "";
        editor.customBaseTarget.appliedCustomBaseDefaultAviVersion = version.defaultAviVersion ?? "";
        editor.customBaseTarget.appliedCustomBaseDeliveryMode = NativeMeshPayloadService.VersionUsesAdvancedMesh(version)
            ? AdvancedMeshDeliveryMode
            : FbxReplacementDeliveryMode;
        editor.isCustomBase = true;
        editor.currentIsCustom = false;
        SyncAppliedVersionBlendshapeLinkCache(version);
        SyncAppliedVersionAnimationPositionOffsetCache(version);
        EditorUtility.SetDirty(editor.customBaseTarget);
    }

    private void ClearAppliedVersionState()
    {
        if (editor?.customBaseTarget == null) return;

        editor.customBaseTarget.appliedCustomBaseVersion = null;
        editor.customBaseTarget.appliedCustomBaseAssetId = 0;
        editor.customBaseTarget.appliedCustomBaseVersionString = "";
        editor.customBaseTarget.appliedCustomBaseDefaultAviVersion = "";
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
        if (applied != null && applied != VersionListDrawer.RESET_VERSION)
        {
            return applied;
        }

        string versionString = editor.customBaseTarget.appliedCustomBaseVersionString;
        var candidates = new List<CustomBaseVersion>();
        if (candidateVersions != null) candidates.AddRange(candidateVersions.Where(v => v != null));
        if (editor.GetAllVersions() != null) candidates.AddRange(editor.GetAllVersions().Where(v => v != null));
        if (editor.selectedVersionForAction != null) candidates.Add(editor.selectedVersionForAction);
        if (editor.recommendedVersion != null) candidates.Add(editor.recommendedVersion);

        candidates = candidates
            .Where(v => v != null)
            .GroupBy(v => $"{v.assetId}|{v.version}|{v.defaultAviVersion}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (string.IsNullOrWhiteSpace(versionString))
        {
            return InferAdvancedVersionFromGeneratedMeshPaths(candidates)
                   ?? candidates.FirstOrDefault(v =>
                       v != null &&
                       NativeMeshPayloadService.VersionUsesAdvancedMesh(v) &&
                       NativeMeshPayloadService.HasAnyAdvancedMeshApplied(editor.customBaseTarget.transform.root, v));
        }

        int assetId = editor.customBaseTarget.appliedCustomBaseAssetId;
        string defaultAviVersion = editor.customBaseTarget.appliedCustomBaseDefaultAviVersion;
        return candidates.FirstOrDefault(v =>
            v != null &&
            string.Equals(v.version, versionString, StringComparison.Ordinal) &&
            (assetId <= 0 || v.assetId == assetId) &&
            (string.IsNullOrWhiteSpace(defaultAviVersion) || string.Equals(v.defaultAviVersion, defaultAviVersion, StringComparison.Ordinal)));
    }

    private CustomBaseVersion InferAdvancedVersionFromGeneratedMeshPaths(IReadOnlyList<CustomBaseVersion> candidates)
    {
        if (editor?.customBaseTarget == null || candidates == null || candidates.Count == 0)
        {
            return null;
        }

        const string prefix = "Assets/MCB/generated/advancedMeshPayloads/";
        foreach (var renderer in editor.customBaseTarget.transform.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer?.sharedMesh == null)
            {
                continue;
            }

            string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh));
            if (string.IsNullOrWhiteSpace(meshPath) ||
                !meshPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string remainder = meshPath.Substring(prefix.Length);
            string[] parts = remainder.Split('/');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int assetId))
            {
                continue;
            }

            string versionString = parts[1];
            var match = candidates.FirstOrDefault(v =>
                v != null &&
                NativeMeshPayloadService.VersionUsesAdvancedMesh(v) &&
                v.assetId == assetId &&
                string.Equals(v.version, versionString, StringComparison.Ordinal));
            if (match != null)
            {
                MCBLogger.Log($"[VersionActions] Inferred native mesh applied version {match.version} from generated mesh path: {meshPath}");
                return match;
            }
        }

        return null;
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

    private void SyncAppliedVersionAnimationPositionOffsetCache(CustomBaseVersion version)
    {
        if (editor?.customBaseTarget == null) return;

        var cache = editor.customBaseTarget.appliedVersionAnimationPositionOffsetsCache;
        if (cache == null)
        {
            cache = new List<AnimationPositionOffsetEntry>();
            editor.customBaseTarget.appliedVersionAnimationPositionOffsetsCache = cache;
        }

        cache.Clear();
        if (version == null) return;

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
                NativeMeshPayloadService.IsVersionApplied(editor.customBaseTarget.transform.root, matchingPersisted))
            {
                return matchingPersisted;
            }
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
            string.Equals(file.transform, "XOR_BIN_TO_FBX", StringComparison.OrdinalIgnoreCase) &&
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
            patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue))
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
            SyncAppliedVersionAnimationPositionOffsetCache(null);
            editor.currentIsCustom = true;

            // Recalculate hashes and update state
            StartRecalculateCurrentFbxHash();
        }
    }
}
#endif
