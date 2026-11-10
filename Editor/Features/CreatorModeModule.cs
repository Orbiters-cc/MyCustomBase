#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

// Handles all UI and logic for the Creator Mode feature.
public partial class CreatorModeModule
{
    private const string InitialDefaultAviVersion = "1.0.0";
    private const long MaxVersionPackageUploadBytes = 600L * 1024L * 1024L;

    private readonly MCBEditor editor;
    private readonly NetworkService networkService;
    private readonly FileManagerService fileManagerService;
    private string cachedCustomFbxSignature;
    private List<string> cachedCustomFbxBlendshapeNames = new List<string>();

    // UI State
    private bool creatorBlendshapeEditorFoldout;
    private int newVersionMajor = 0, newVersionMinor = 1, newVersionPatch = 0;
    private Scope newVersionScope = Scope.BETA;
    private string newVersionTitle = "";
    private string newChangelog = "";

    // Build & Publish state: the version that was built (and applied locally as a temporary
    // version) but not yet published. Invalidated whenever the form changes.
    private CustomBaseVersion builtPendingVersion;
    private string builtPendingVersionSignature;

    private List<CustomBaseVersion> compatibleParentVersions;
    private List<string> parentVersionDisplayOptions;
    private int defaultParentIndex;
    private int selectedParentVersionIndex = -1;
    private CustomBaseVersion selectedParentVersionObject = null;
    private CustomBaseVersion previouslySelectedVersion = null;
    private const string CustomVeinsKey = "customVeins";
    private const string DynamicNormalBodyKey = "dynamicNormalBody";
    private const string DynamicNormalFlexingKey = "dynamicNormalFlexing";
    private const string SuggestRealisticKey = ExtraCustomizationUtils.SuggestRealisticKey;
    private string autoAssignedVeinsTexturePath;
    private CustomBaseVersion lastParentVersionForVeins;
    private bool isRestoringFromVersionState;
    
    public CreatorModeModule(MCBEditor editor)
    {
        this.editor = editor;
        this.networkService = new NetworkService();
        this.fileManagerService = new FileManagerService();
        creatorBlendshapeEditorFoldout = EditorPrefs.GetBool("MCB_CreatorMode_BlendshapeEditorFoldout", false);
    }

    private void ApplyCustomFbxForModelEntry(GameObject targetFbx, GameObject customFbx)
    {
        if (editor.customBaseTarget == null || targetFbx == null || customFbx == null)
        {
            return;
        }

        try
        {
            string targetPath = AssetDatabase.GetAssetPath(targetFbx);
            string customPath = AssetDatabase.GetAssetPath(customFbx);
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(customPath))
            {
                throw new InvalidOperationException("The target FBX or custom FBX has no AssetDatabase path.");
            }

            AssetDatabase.ImportAsset(customPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            Transform avatarRoot = AvatarPaths.Root(editor.customBaseTarget);
            var smrPaths = CollectTargetedSmrPathsForModelEntry(avatarRoot, targetPath, targetFbx, customFbx);
            if (smrPaths.Count == 0)
            {
                throw new InvalidOperationException("No avatar SkinnedMeshRenderer currently maps to the target FBX.");
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Custom FBX Meshes");
            Undo.RecordObject(editor.customBaseTarget, "Apply Custom FBX Meshes");

            int refreshedCount = SmrPathService.RefreshTargetMeshesFromFbx(
                avatarRoot,
                customPath,
                smrPaths,
                allowNameFallback: false);
            Undo.CollapseUndoOperations(undoGroup);

            if (refreshedCount == 0)
            {
                throw new InvalidOperationException("The custom FBX does not contain meshes matching the selected target FBX renderers.");
            }

            if (refreshedCount < smrPaths.Count)
            {
                MCBLogger.LogWarning($"[MCB] Applied custom FBX to {refreshedCount}/{smrPaths.Count} target renderer(s). Some target meshes were not found in the custom FBX.");
            }

            SaveTemporaryInternalVersion("custom FBX apply");

            EditorUtility.SetDirty(editor.customBaseTarget);
            MCBLogger.Log($"[MCB] Applied custom FBX '{customPath}' to {refreshedCount} renderer(s) mapped from target '{targetPath}'.");
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to apply custom FBX: {ex.Message}");
            EditorUtility.DisplayDialog("Apply Custom FBX", $"Could not apply the custom FBX:\n{ex.Message}", "OK");
        }
    }

    private static List<ModelFileSmrPathData> CollectTargetedSmrPathsForModelEntry(
        Transform avatarRoot,
        string targetPath,
        GameObject targetFbx,
        GameObject customFbx = null)
    {
        return SmrPathService.CollectModelRendererBindings(avatarRoot, targetPath, targetFbx, customFbx);
    }


    private void ApplyCustomAvatarForModelEntry(GameObject targetFbx, GameObject customFbx, Avatar customAvatar)
    {
        if (editor.customBaseTarget == null || targetFbx == null || customAvatar == null)
        {
            return;
        }

        try
        {
            if (customFbx == null)
            {
                AvatarDefinitionGenerationService.SetRootAnimatorAvatar(AvatarPaths.Root(editor.customBaseTarget), customAvatar);
                SaveTemporaryInternalVersion("custom avatar apply");
                EditorUtility.SetDirty(editor.customBaseTarget);
                return;
            }
            string targetPath = AssetDatabase.GetAssetPath(targetFbx);
            string customPath = AssetDatabase.GetAssetPath(customFbx);
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(customPath))
            {
                throw new InvalidOperationException("The target FBX or custom FBX has no AssetDatabase path.");
            }

            bool replacedFbx = fileManagerService.ReplaceFbxWithCustomCopy(targetPath, customPath);
            if (replacedFbx)
            {
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            Transform avatarRoot = AvatarPaths.Root(editor.customBaseTarget);
            var smrPaths = SmrPathService.CollectSmrPathsForFbx(avatarRoot, targetPath);
            if (smrPaths.Count == 0)
            {
                smrPaths = SmrPathService.CollectSmrPathsForFbx(avatarRoot, customPath);
            }

            SmrPathService.RefreshTargetMeshesFromFbx(avatarRoot, targetPath, smrPaths);

            var reimportedTargetFbx = AssetDatabase.LoadAssetAtPath<GameObject>(targetPath);
            AvatarDefinitionGenerationService.ApplyAvatarToFbxAndAnimator(
                reimportedTargetFbx != null ? reimportedTargetFbx : targetFbx,
                customAvatar,
                avatarRoot);

            SaveTemporaryInternalVersion("custom avatar apply");
            new VersionActions(editor, networkService, fileManagerService).StartRecalculateCurrentFbxHash();

            EditorUtility.SetDirty(editor.customBaseTarget);
            MCBLogger.Log($"[MCB] Applied custom Avatar '{AssetDatabase.GetAssetPath(customAvatar)}' to target '{targetPath}'.");
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to apply custom Avatar: {ex.Message}");
            EditorUtility.DisplayDialog("Apply Custom Avatar", $"Could not apply the custom Avatar:\n{ex.Message}", "OK");
        }
    }

    private const string CustomAvatarGuidance = "Optional — required if the armature was modified. For mesh-only edits, leave this empty to keep using the original FBX Avatar definition.";

    private void GenerateAvatarForModelEntry(GameObject targetFbx, GameObject customFbx, SerializedProperty avatarProp, string externalFbxPath = null)
    {
        if (targetFbx == null || !AvatarDefinitionGenerationService.HasGenerationSource(customFbx, externalFbxPath) || avatarProp == null)
        {
            return;
        }

        try
        {
            var result = customFbx == null
                ? AvatarDefinitionGenerationService.GenerateAvatarFromExternalFbx(externalFbxPath, targetFbx,
                    avatarProp.objectReferenceValue as Avatar)
                : AvatarDefinitionGenerationService.GenerateAvatarAsset(
                customFbx,
                targetFbx,
                applyGeneratedAvatarToFbx: false,
                keepImporterConfiguredForEditing: true);
            if (result?.avatar == null)
            {
                throw new InvalidOperationException("Unity did not return a generated Avatar.");
            }

            avatarProp.objectReferenceValue = result.avatar;
            editor.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(editor.customBaseTarget);
            AvatarDefinitionGenerationService.SetRootAnimatorAvatar(AvatarPaths.Root(editor.customBaseTarget), result.avatar);
            SaveTemporaryInternalVersion("avatar generation");

            MCBLogger.Log($"[MCB] Generated custom base Avatar '{result.avatarPath}'. {result.message}");
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to generate custom base Avatar: {ex.Message}");
            EditorUtility.DisplayDialog("Generate Avatar", $"Could not generate an Avatar asset:\n{ex.Message}", "OK");
        }
    }

    private void SyncModelFileBuildEntryCount()
    {
        if (editor.modelFileBuildEntriesProp == null || editor.baseFbxFilesProp == null) return;

        while (editor.modelFileBuildEntriesProp.arraySize < editor.baseFbxFilesProp.arraySize)
        {
            editor.modelFileBuildEntriesProp.InsertArrayElementAtIndex(editor.modelFileBuildEntriesProp.arraySize);
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(editor.modelFileBuildEntriesProp.arraySize - 1);
            entry.FindPropertyRelative("customFbx").objectReferenceValue = null;
            var externalCustomFbxPath = entry.FindPropertyRelative("externalCustomFbxPath");
            if (externalCustomFbxPath != null) externalCustomFbxPath.stringValue = "";
            entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        }

        while (editor.modelFileBuildEntriesProp.arraySize > editor.baseFbxFilesProp.arraySize)
        {
            editor.modelFileBuildEntriesProp.DeleteArrayElementAtIndex(editor.modelFileBuildEntriesProp.arraySize - 1);
        }
    }

    private bool AreModelFileBuildEntriesValid()
    {
        SyncModelFileBuildEntryCount();
        if (editor.modelFileBuildEntriesProp == null) return true;

        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            var sourceFbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i);
            var customFbx = entry.FindPropertyRelative("customFbx").objectReferenceValue as GameObject;
            var externalCustomFbxPath = entry.FindPropertyRelative("externalCustomFbxPath")?.stringValue;
            var avatar = entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue as Avatar;
            bool hasExternalCustomFbx = !string.IsNullOrWhiteSpace(externalCustomFbxPath) && File.Exists(Path.GetFullPath(externalCustomFbxPath));
            if ((customFbx != null || hasExternalCustomFbx || avatar != null) && sourceFbx == null) return false;
        }

        return true;
    }

    private static bool HasRequiredNewVersionMetadata(string version, Scope scope, string title, string defaultAviVersion, out string message)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(version))
        {
            missing.Add("version");
        }
        if (!Enum.IsDefined(typeof(Scope), scope))
        {
            missing.Add("scope");
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            missing.Add("version title");
        }
        if (string.IsNullOrWhiteSpace(defaultAviVersion))
        {
            missing.Add("default AVI version");
        }

        if (missing.Count == 0)
        {
            message = null;
            return true;
        }

        message = "Required version metadata is missing: " + string.Join(", ", missing) + ".";
        return false;
    }

    private bool HasModelFileBuildEntryPayload()
    {
        SyncModelFileBuildEntryCount();
        if (editor.modelFileBuildEntriesProp == null || editor.baseFbxFilesProp == null) return false;

        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            var sourceFbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i);
            var customFbx = entry.FindPropertyRelative("customFbx").objectReferenceValue as GameObject;
            var externalCustomFbxPath = entry.FindPropertyRelative("externalCustomFbxPath")?.stringValue;
            var avatar = entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue as Avatar;
            bool hasExternalCustomFbx = !string.IsNullOrWhiteSpace(externalCustomFbxPath) && File.Exists(Path.GetFullPath(externalCustomFbxPath));
            if (sourceFbx != null && (customFbx != null || hasExternalCustomFbx || avatar != null)) return true;
        }

        // Meshes Mixer committed on the base become their model's custom FBX when the version is built.
        return VersionAvatarMeshes.Find(editor).Committed.Count > 0;
    }

    private bool HasSuggestRealisticPayload()
    {
        return editor.includeSuggestRealisticForCreatorProp != null &&
               editor.includeSuggestRealisticForCreatorProp.boolValue &&
               GetSerializedStringList(editor.suggestRealisticMeshPathsForCreatorProp).Count > 0;
    }

    private void SaveTemporaryInternalVersion(string reason)
    {
        if (isRestoringFromVersionState || editor?.customBaseTarget == null)
        {
            return;
        }

        string newVersionString = $"{newVersionMajor}.{newVersionMinor}.{newVersionPatch}";
        if (!IsNewVersionValid(newVersionString))
        {
            MCBLogger.LogWarning($"[CreatorMode] Temporary version was not saved after {reason}: version {newVersionString} is not higher than the selected parent version.");
            return;
        }

        bool hasVersionPayload = HasModelFileBuildEntryPayload() ||
                                 editor.avatarLogicPrefabProp.objectReferenceValue != null ||
                                 (editor.includeCustomVeinsForCreatorProp.boolValue && editor.customVeinsNormalMapProp.objectReferenceValue != null) ||
                                 editor.includeDynamicNormalsBodyForCreatorProp.boolValue ||
                                 editor.includeDynamicNormalsFlexingForCreatorProp.boolValue ||
                                 HasTypedCustomization() || HasSuggestRealisticPayload() ||
                                 editor.customBlendshapesForCreatorProp.arraySize > 0;
        if (!hasVersionPayload)
        {
            return;
        }

        Exception buildError = null;

        try
        {
            var artifact = BuildNewVersion();
            if (artifact?.Metadata == null)
            {
                return;
            }

            // The artifact is already committed and persisted as unsubmitted.
            editor.LoadUnsubmittedVersions(true);
            editor.selectedVersionForAction = artifact.Metadata;
            editor.selectedCustomVersionForAction = null;
            MCBLogger.Log($"[CreatorMode] Saved temporary internal version {artifact.Metadata.version} after {reason}.");
        }
        catch (Exception ex)
        {
            buildError = ex;
            MCBLogger.LogWarning($"[CreatorMode] Temporary version was not saved after {reason}: {ex.Message}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (buildError != null)
        {
            editor.warningsModule.AddWarning(buildError.Message, MessageType.Warning, "Temporary Version Not Saved");
        }
    }

    private void OnBlendshapeSearchConfirm(string selectedBlendshape)
    {
        if (string.IsNullOrEmpty(selectedBlendshape))
            return;

        // Add the selected blendshape to the list
        var prop = editor.customBlendshapesForCreatorProp;
        int index = prop.arraySize;
        prop.arraySize++;
        var element = prop.GetArrayElementAtIndex(index);
        element.FindPropertyRelative("name").stringValue = selectedBlendshape;
        element.FindPropertyRelative("defaultValue").stringValue = "0";
        element.FindPropertyRelative("isSlider").boolValue = false;
        element.FindPropertyRelative("isSliderDefault").boolValue = false;
        element.FindPropertyRelative("correctiveBlendshapes").arraySize = 0;
        editor.serializedObject.ApplyModifiedProperties();
    }

    private CorrectiveActivationType ApplyCorrectiveTypeChange(
        SerializedProperty typeProp,
        SerializedProperty stringProp,
        CorrectiveActivationType currentType,
        CorrectiveActivationType newType)
    {
        if (typeProp == null || newType == currentType) return currentType;

        typeProp.enumValueIndex = (int)newType;
        if (stringProp != null)
        {
            stringProp.stringValue = string.Empty;
        }
        return newType;
    }

    private static List<AnimationClip> FindAnimationClipsByName(string clipName) => AnimationClipLookup.Find(clipName);

    private static List<string> CollectAnimationAssetPathsFromFixedBy(IEnumerable<CustomBlendshapeEntry> customBlendshapeEntries)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (customBlendshapeEntries == null) return paths.ToList();

        foreach (var blendshape in customBlendshapeEntries)
        {
            if (blendshape?.correctiveBlendshapes == null) continue;
            foreach (var corrective in blendshape.correctiveBlendshapes)
            {
                if (corrective == null) continue;
                if (corrective.fixedByType != CorrectiveActivationType.Animation) continue;
                if (string.IsNullOrWhiteSpace(corrective.fixedBy)) continue;

                foreach (var clip in FindAnimationClipsByName(corrective.fixedBy))
                {
                    if (clip == null) continue;
                    string assetPath = AssetDatabase.GetAssetPath(clip);
                    if (string.IsNullOrWhiteSpace(assetPath)) continue;
                    paths.Add(assetPath);
                }
            }
        }

        return paths.ToList();
    }

    private IEnumerable<string> GetAllBlendshapeNamesFromCustomFbx()
    {
        SyncModelFileBuildEntryCount();
        var signatureParts = new List<string>();
        for (int entryIndex = 0; entryIndex < editor.modelFileBuildEntriesProp.arraySize; entryIndex++)
        {
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(entryIndex);
            var fbxObject = entry.FindPropertyRelative("customFbx").objectReferenceValue as GameObject;
            if (fbxObject == null && editor.baseFbxFilesProp != null && entryIndex < editor.baseFbxFilesProp.arraySize)
            {
                fbxObject = editor.baseFbxFilesProp.GetArrayElementAtIndex(entryIndex).objectReferenceValue as GameObject;
            }
            if (fbxObject == null) continue;

            string path = AssetDatabase.GetAssetPath(fbxObject);
            signatureParts.Add(string.IsNullOrWhiteSpace(path) ? fbxObject.GetInstanceID().ToString() : MCBUtils.ToUnityPath(path));
        }

        string signature = string.Join("|", signatureParts.OrderBy(part => part, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(signature) &&
            string.Equals(signature, cachedCustomFbxSignature, StringComparison.Ordinal) &&
            cachedCustomFbxBlendshapeNames != null)
        {
            return cachedCustomFbxBlendshapeNames;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int entryIndex = 0; entryIndex < editor.modelFileBuildEntriesProp.arraySize; entryIndex++)
        {
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(entryIndex);
            var fbxObject = entry.FindPropertyRelative("customFbx").objectReferenceValue as GameObject;
            if (fbxObject == null && editor.baseFbxFilesProp != null && entryIndex < editor.baseFbxFilesProp.arraySize)
            {
                fbxObject = editor.baseFbxFilesProp.GetArrayElementAtIndex(entryIndex).objectReferenceValue as GameObject;
            }
            if (fbxObject == null) continue;

            foreach (var smr in fbxObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr?.sharedMesh == null) continue;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    string name = smr.sharedMesh.GetBlendShapeName(i);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        cachedCustomFbxBlendshapeNames = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        cachedCustomFbxSignature = signature;
        return cachedCustomFbxBlendshapeNames;
    }

    private static void AddCorrectivePair(
        SerializedProperty correctiveProp,
        CorrectiveActivationType toFixType = CorrectiveActivationType.Blendshape,
        string toFix = "",
        CorrectiveActivationType fixedByType = CorrectiveActivationType.Blendshape,
        string fixedBy = "")
    {
        if (correctiveProp == null) return;
        int i = correctiveProp.arraySize;
        correctiveProp.InsertArrayElementAtIndex(i);
        var row = correctiveProp.GetArrayElementAtIndex(i);
        row.FindPropertyRelative("toFixType").enumValueIndex = (int)toFixType;
        row.FindPropertyRelative("toFix").stringValue = toFix ?? string.Empty;
        row.FindPropertyRelative("fixedByType").enumValueIndex = (int)fixedByType;
        row.FindPropertyRelative("fixedBy").stringValue = fixedBy ?? string.Empty;
    }

    private List<SuggestRealisticMeshOption> GetSuggestRealisticMeshOptions()
    {
        var options = new List<SuggestRealisticMeshOption>();
        if (editor?.customBaseTarget == null)
        {
            return options;
        }

        Transform root = AvatarPaths.Root(editor.customBaseTarget);
        if (root == null)
        {
            return options;
        }

        var targetFbxPaths = GetCreatorTargetFbxPaths();
        var pathsByFbx = SmrPathService.CollectSmrPathsByFbx(root, targetFbxPaths);
        var selectedAsset = editor.GetSelectedAsset();
        foreach (string targetFbxPath in targetFbxPaths)
        {
            bool hasLiveEntries = pathsByFbx.TryGetValue(targetFbxPath, out var liveEntries) &&
                                  liveEntries != null &&
                                  liveEntries.Count > 0;
            if (hasLiveEntries)
            {
                continue;
            }

            var sourceFile = selectedAsset?.sourceFiles?.FirstOrDefault(file =>
                file != null &&
                string.Equals(MCBUtils.ToUnityPath(file.path), targetFbxPath, StringComparison.OrdinalIgnoreCase));
            if (sourceFile?.smrPaths != null && sourceFile.smrPaths.Count > 0)
            {
                pathsByFbx[targetFbxPath] = sourceFile.smrPaths;
            }
        }

        foreach (var pair in pathsByFbx.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in pair.Value ?? new List<ModelFileSmrPathData>())
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.avatarPath))
                {
                    continue;
                }

                options.Add(new SuggestRealisticMeshOption
                {
                    avatarPath = entry.avatarPath,
                    sourceFbxPath = pair.Key,
                    fbxMeshPath = entry.fbxMeshPath,
                    meshName = entry.meshName,
                    rendererName = entry.rendererName
                });
            }
        }

        return options
            .GroupBy(option => option.avatarPath, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(option => option.avatarPath, StringComparer.Ordinal)
            .ToList();
    }

    private List<string> GetCreatorTargetFbxPaths()
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

    private static List<string> GetSerializedStringList(SerializedProperty arrayProp)
    {
        var values = new List<string>();
        if (arrayProp == null || !arrayProp.isArray)
        {
            return values;
        }

        for (int i = 0; i < arrayProp.arraySize; i++)
        {
            string value = arrayProp.GetArrayElementAtIndex(i).stringValue;
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool SerializedStringListContains(SerializedProperty arrayProp, string value)
    {
        if (arrayProp == null || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return GetSerializedStringList(arrayProp)
            .Any(entry => string.Equals(entry, value, StringComparison.Ordinal));
    }

    private static void SetSerializedStringListContains(SerializedProperty arrayProp, string value, bool selected)
    {
        if (arrayProp == null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var values = GetSerializedStringList(arrayProp);
        values.RemoveAll(entry => string.Equals(entry, value, StringComparison.Ordinal));
        if (selected)
        {
            values.Add(value);
        }

        SetSerializedStringList(arrayProp, values);
    }

    private static void SetSerializedStringList(SerializedProperty arrayProp, IEnumerable<string> values)
    {
        if (arrayProp == null)
        {
            return;
        }

        var normalized = (values ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        arrayProp.ClearArray();
        for (int i = 0; i < normalized.Count; i++)
        {
            arrayProp.InsertArrayElementAtIndex(i);
            arrayProp.GetArrayElementAtIndex(i).stringValue = normalized[i];
        }
    }

    private sealed class SuggestRealisticMeshOption
    {
        public string avatarPath;
        public string sourceFbxPath;
        public string fbxMeshPath;
        public string meshName;
        public string rendererName;

        public string Label
        {
            get
            {
                string meshLabel = !string.IsNullOrWhiteSpace(meshName) ? $" ({meshName})" : string.Empty;
                return $"{avatarPath}{meshLabel}";
            }
        }

        public string Tooltip
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(sourceFbxPath)) parts.Add($"Source FBX: {sourceFbxPath}");
                if (!string.IsNullOrWhiteSpace(fbxMeshPath)) parts.Add($"FBX mesh path: {fbxMeshPath}");
                if (!string.IsNullOrWhiteSpace(rendererName)) parts.Add($"Renderer: {rendererName}");
                return string.Join("\n", parts);
            }
        }
    }

    private static bool TryGetCustomVeinsTextureImporter(string assetPath, out TextureImporter importer)
    {
        importer = null;
        if (string.IsNullOrWhiteSpace(assetPath)) return false;

        importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        return importer != null;
    }

    private static bool IsValidCustomVeinsNormalMapImport(TextureImporter importer)
    {
        if (importer == null) return false;

        return importer.textureType == TextureImporterType.NormalMap &&
               importer.mipmapEnabled &&
               !importer.sRGBTexture &&
               !importer.convertToNormalmap &&
               !importer.isReadable &&
               !importer.streamingMipmaps &&
               importer.textureShape == TextureImporterShape.Texture2D &&
               importer.alphaSource == TextureImporterAlphaSource.FromInput &&
               !importer.alphaIsTransparency;
    }

    private static void FixCustomVeinsNormalMapImport(string assetPath)
    {
        if (!TryGetCustomVeinsTextureImporter(assetPath, out var importer))
        {
            EditorUtility.DisplayDialog("MCB", "Could not find a texture importer for the selected file.", "OK");
            return;
        }

        importer.textureType = TextureImporterType.NormalMap;
        importer.mipmapEnabled = true;
        importer.sRGBTexture = false;
        importer.convertToNormalmap = false;
        importer.isReadable = false;
        importer.streamingMipmaps = false;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = false;
        importer.npotScale = TextureImporterNPOTScale.ToNearest;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Bilinear;
        importer.anisoLevel = 1;

        importer.SaveAndReimport();
    }

    private bool ParentSupportsCustomVeins(CustomBaseVersion version)
    {
        return ExtraCustomizationUtils.HasFlag(version?.extraCustomization, CustomVeinsKey);
    }

    private void HandleParentVersionChanged(CustomBaseVersion newParent)
    {
        if (ReferenceEquals(lastParentVersionForVeins, newParent) && !isRestoringFromVersionState)
        {
            return;
        }

        lastParentVersionForVeins = newParent;
        if (!isRestoringFromVersionState && newParent != null)
        {
            editor.serializedObject.ApplyModifiedProperties();
            editor.customBaseTarget.creatorCustomization = VersionCustomization.Read(newParent.extraCustomization);
            editor.serializedObject.Update();
        }

        var includeProp = editor.includeCustomVeinsForCreatorProp;
        var textureProp = editor.customVeinsNormalMapProp;

        // Only auto-populate if parent supports custom veins
        if (ParentSupportsCustomVeins(newParent))
        {
            // Auto-check the checkbox if not already checked (but only when not restoring from saved state)
            if (!isRestoringFromVersionState && !includeProp.boolValue)
            {
                includeProp.boolValue = true;
            }

            string parentTexturePath = MCBUtils.GetVersionVeinsTexturePath(newParent);
            if (!string.IsNullOrEmpty(parentTexturePath))
            {
                Texture2D parentTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(parentTexturePath);
                if (parentTexture != null)
                {
                    string currentTexturePath = AssetDatabase.GetAssetPath(textureProp.objectReferenceValue);
                    bool wasAutoAssigned = !string.IsNullOrEmpty(autoAssignedVeinsTexturePath) && autoAssignedVeinsTexturePath == currentTexturePath;

                    // Auto-assign the texture if empty or was previously auto-assigned
                    if (textureProp.objectReferenceValue == null || wasAutoAssigned)
                    {
                        textureProp.objectReferenceValue = parentTexture;
                        autoAssignedVeinsTexturePath = parentTexturePath;
                    }
                }
            }
        }
        // If parent doesn't support custom veins, don't uncheck or clear - just leave it as is
        
        // Auto-populate dynamic normals checkboxes from parent version
        var includeBodyProp = editor.includeDynamicNormalsBodyForCreatorProp;
        var includeFlexingProp = editor.includeDynamicNormalsFlexingForCreatorProp;
        
        if (newParent?.extraCustomization != null && !isRestoringFromVersionState)
        {
            bool parentHasBody = ExtraCustomizationUtils.HasFlag(newParent.extraCustomization, DynamicNormalBodyKey);
            bool parentHasFlexing = ExtraCustomizationUtils.HasFlag(newParent.extraCustomization, DynamicNormalFlexingKey);
            
            // Auto-check the checkboxes if parent has the features
            if (parentHasBody && !includeBodyProp.boolValue)
            {
                includeBodyProp.boolValue = true;
            }
            if (parentHasFlexing && !includeFlexingProp.boolValue)
            {
                includeFlexingProp.boolValue = true;
            }

            var parentSuggestRealisticPaths = ExtraCustomizationUtils.GetStringList(newParent.extraCustomization, SuggestRealisticKey);
            if (parentSuggestRealisticPaths.Count > 0 &&
                editor.includeSuggestRealisticForCreatorProp != null &&
                editor.suggestRealisticMeshPathsForCreatorProp != null)
            {
                editor.includeSuggestRealisticForCreatorProp.boolValue = true;
                if (GetSerializedStringList(editor.suggestRealisticMeshPathsForCreatorProp).Count == 0)
                {
                    SetSerializedStringList(editor.suggestRealisticMeshPathsForCreatorProp, parentSuggestRealisticPaths);
                }
            }
        }
        
        // Blendshape/corrective import is now explicit via UI button to avoid long UI stalls on toggle.
    }

    private void ImportBlendshapesFromParent(CustomBaseVersion newParent)
    {
        if (newParent == null || newParent.customBlendshapes == null) return;

        var imported = new List<CreatorBlendshapeEntry>(newParent.customBlendshapes.Length);
        foreach (var src in newParent.customBlendshapes)
        {
            if (src == null) continue;
            var dst = new CreatorBlendshapeEntry
            {
                name = src.name,
                defaultValue = src.defaultValue,
                isSlider = src.isSlider,
                isSliderDefault = src.isSliderDefault,
                correctiveBlendshapes = new List<CreatorCorrectiveBlendshapeEntry>()
            };

            if (src.correctiveBlendshapes != null)
            {
                for (int i = 0; i < src.correctiveBlendshapes.Length; i++)
                {
                    var c = src.correctiveBlendshapes[i];
                    if (c == null) continue;
                    dst.correctiveBlendshapes.Add(new CreatorCorrectiveBlendshapeEntry
                    {
                        toFixType = c.toFixType,
                        toFix = c.toFix,
                        fixedByType = c.fixedByType,
                        fixedBy = c.fixedBy
                    });
                }
            }

            imported.Add(dst);
        }

        Undo.RecordObject(editor.customBaseTarget, "Import Parent Blendshapes");
        editor.customBaseTarget.customBlendshapesForCreator = imported;
        EditorUtility.SetDirty(editor.customBaseTarget);
        editor.serializedObject.Update();
    }

    private static void CopyCorrectivesToSerialized(SerializedProperty blendshapeElement, CorrectiveBlendshapeEntry[] source)
    {
        var correctiveProp = blendshapeElement.FindPropertyRelative("correctiveBlendshapes");
        if (correctiveProp == null) return;

        correctiveProp.arraySize = 0;
        if (source == null || source.Length == 0) return;

        foreach (var corrective in source)
        {
            if (corrective == null) continue;
            AddCorrectivePair(correctiveProp, corrective.toFixType, corrective.toFix, corrective.fixedByType, corrective.fixedBy);
        }
    }
    
    private void SetDefaultVersionNumbers(CustomBaseVersion baseVersionObject)
    {
        string currentVersionStr = baseVersionObject?.version;
        
        if (string.IsNullOrEmpty(currentVersionStr))
        {
            newVersionMajor = 0; newVersionMinor = 1; newVersionPatch = 0;
        }
        else
        {
            Version baseVersion = editor.ParseVersion(currentVersionStr);
            newVersionMajor = baseVersion.Major; 
            newVersionMinor = baseVersion.Minor; 
            newVersionPatch = baseVersion.Build + 1;
        }
    }
    
    private void PopulateParentVersionDropdown()
    {
        var applied = editor.customBaseTarget.appliedCustomBaseVersion;
        compatibleParentVersions = VersionRepository.GetCreatorParentVersions(
                editor.GetSelectedAsset()?.id ?? 0, editor.serverVersions, editor.importedVersions, applied)
            .OrderByDescending(v => editor.ParseVersion(v.version)).ToList();
        parentVersionDisplayOptions = compatibleParentVersions.Select(v => $"{v.version} ({v.scope})").ToList();
        defaultParentIndex = compatibleParentVersions.FindIndex(v => v.Equals(applied));
        if (defaultParentIndex < 0) defaultParentIndex = compatibleParentVersions.Count > 0 ? 0 : -1;
        // A refresh can reorder or replace DTOs without changing the selected version.
        selectedParentVersionIndex = compatibleParentVersions.FindIndex(v => v.Equals(selectedParentVersionObject));
        if (selectedParentVersionIndex >= 0)
            selectedParentVersionObject = compatibleParentVersions[selectedParentVersionIndex];
        else if (defaultParentIndex >= 0)
        {
            selectedParentVersionIndex = defaultParentIndex;
            selectedParentVersionObject = compatibleParentVersions[defaultParentIndex];
            SetDefaultVersionNumbers(compatibleParentVersions[0]);
        }
        else selectedParentVersionObject = null;
    }

    private bool IsNewVersionValid(string newVersionString)
    {
        string baseVersionString = selectedParentVersionObject?.version;
        if (string.IsNullOrEmpty(baseVersionString)) return true;
        return editor.CompareVersions(newVersionString, baseVersionString) > 0;
    }

    private bool RequiresParentVersion()
    {
        return compatibleParentVersions != null && compatibleParentVersions.Count > 0;
    }

    /// <summary>
    /// Collects the creator-form state into packaging entries and delegates to
    /// VersionBuilder, which owns the artifact invariants (staging folder, manifest
    /// written last, atomic commit). Returns the committed artifact; the version folder
    /// and its unsubmitted state are already persisted when this returns.
    /// </summary>
    private VersionArtifact BuildNewVersion()
    {
        VersionArtifact result = null;
        MCBWork.Drain(BuildNewVersionCoroutine(value => result = value));
        return result;
    }

    /// <summary>What the last build took from the avatar into its models' custom FBX files (<see cref="VersionAvatarMeshes"/>); empty when nothing.</summary>
    internal IReadOnlyList<VersionAvatarMeshes.Taken> TakenFromAvatar { get; private set; } = Array.Empty<VersionAvatarMeshes.Taken>();

    /// <summary>
    /// The shared build path (creator window, temporary versions and MCP). It holds the process-wide
    /// VersionOperationGuard unless the caller already does (<paramref name="operation"/>), so a build never runs next to
    /// another build or a publish. The version holds what the avatar shows: meshes Mixer committed on the base's renderers
    /// are written into their model's custom FBX first (<see cref="TakenFromAvatar"/>), unless the creator chose to leave
    /// them out (<paramref name="takeAvatarMeshes"/> false), and a mesh still in a Mixer session stops the build.
    /// </summary>
    public IEnumerator BuildNewVersionCoroutine(Action<VersionArtifact> completed, VersionOperationGuard.Scope operation = null, bool takeAvatarMeshes = true)
    {
        var owned = operation != null && operation.IsHeld ? null : VersionOperationGuard.Acquire("building a version");
        try { yield return BuildNewVersionCoroutineCore(completed, takeAvatarMeshes); }
        finally { owned?.Dispose(); }
    }

    private IEnumerator BuildNewVersionCoroutineCore(Action<VersionArtifact> completed, bool takeAvatarMeshes)
    {
        TakenFromAvatar = Array.Empty<VersionAvatarMeshes.Taken>();
        var logicPrefab = editor.avatarLogicPrefabProp.objectReferenceValue as GameObject;
        bool shouldIncludeCustomVeins = editor.includeCustomVeinsForCreatorProp.boolValue;
        var customVeinsTexture = editor.customVeinsNormalMapProp.objectReferenceValue as Texture2D;
        bool shouldIncludeDynamicNormalsBody = editor.includeDynamicNormalsBodyForCreatorProp.boolValue;
        bool shouldIncludeDynamicNormalsFlexing = editor.includeDynamicNormalsFlexingForCreatorProp.boolValue;
        var suggestRealisticMeshPaths = GetSerializedStringList(editor.suggestRealisticMeshPathsForCreatorProp);
        bool shouldIncludeSuggestRealistic = editor.includeSuggestRealisticForCreatorProp != null &&
                                             editor.includeSuggestRealisticForCreatorProp.boolValue;

        if (shouldIncludeCustomVeins)
        {
            if (customVeinsTexture == null)
                throw new Exception("Custom veins is enabled but no normal map texture is assigned.");
        }
        if (shouldIncludeSuggestRealistic && suggestRealisticMeshPaths.Count == 0)
        {
            throw new Exception("Suggest realistic is enabled but no target meshes are selected.");
        }

        bool requiresParentVersion = RequiresParentVersion();
        if (requiresParentVersion && selectedParentVersionObject == null)
            throw new Exception("A Parent Version must be selected.");
        string defaultAviVersion = selectedParentVersionObject?.defaultAviVersion ?? InitialDefaultAviVersion;

        // Convert CreatorBlendshapeEntry list to CustomBlendshapeEntry array.
        var customBlendshapeEntries = editor.customBaseTarget.customBlendshapesForCreator
            .Select(entry =>
            {
                var corrective = (entry.correctiveBlendshapes ?? new List<CreatorCorrectiveBlendshapeEntry>())
                    .Where(c => c != null
                                && !string.IsNullOrWhiteSpace(c.toFix)
                                && !string.IsNullOrWhiteSpace(c.fixedBy))
                    .Select(c => new CorrectiveBlendshapeEntry
                    {
                        toFixType = c.toFixType,
                        toFix = c.toFix,
                        fixedByType = c.fixedByType,
                        fixedBy = c.fixedBy
                    })
                    .ToArray();

                return new CustomBlendshapeEntry
                {
                    name = entry.name,
                    defaultValue = entry.defaultValue,
                    isSlider = entry.isSlider,
                    isSliderDefault = entry.isSliderDefault,
                    correctiveBlendshapes = corrective.Length > 0 ? corrective : null
                };
            })
            .ToArray();

        // Corrective and mode animations ship in the logic package so their GUIDs resolve for users.
        var fixedByAnimationAssetPaths = CollectAnimationAssetPathsFromFixedBy(customBlendshapeEntries).Concat(ModeAnimationAssetPaths()).Distinct().ToList();
        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null || selectedAsset.id <= 0)
        {
            throw new InvalidOperationException("Select a custom base asset before building a version.");
        }
        int assetId = selectedAsset.id;

        string newVersionString = $"{newVersionMajor}.{newVersionMinor}.{newVersionPatch}";
        if (!HasRequiredNewVersionMetadata(newVersionString, newVersionScope, newVersionTitle, defaultAviVersion, out string metadataValidationMessage))
        {
            throw new InvalidOperationException(metadataValidationMessage);
        }
        // Builds started without the form (MCP) get the same version rule as the form's buttons and the server.
        VersionPublisher.ValidateNewVersionNumber(newVersionString, selectedParentVersionObject?.version,
            selectedParentVersionObject != null ? null : VersionRepository.GetCreatorParentVersions(assetId, editor.serverVersions,
                editor.importedVersions, editor.customBaseTarget.appliedCustomBaseVersion).Select(v => v.version),
            editor.CompareVersions);

        EditorUtility.DisplayProgressBar("Preparing Build", "Creating version package...", 0.2f);

        SyncModelFileBuildEntryCount();
        // With a dialog or without (MCP, temporary versions): a Mixer working copy is not built, committed meshes go into
        // their model's custom FBX before the entries are read.
        var shown = VersionAvatarMeshes.Find(editor.customBaseTarget, editor);
        string inSession = VersionAvatarMeshes.InSessionMessage(shown.InSession.Select(s => s.Renderer));
        if (inSession != null) throw new InvalidOperationException(inSession);
        if (shown.Committed.Count > 0 && takeAvatarMeshes)
        {
            EditorUtility.DisplayProgressBar("Preparing Build", "Writing the meshes Mixer committed into custom FBX files...", 0.25f);
            TakenFromAvatar = VersionAvatarMeshes.TakeIntoEntries(editor.customBaseTarget, editor.serializedObject, shown.Committed);
            yield return null;
        }
        else if (shown.Committed.Count > 0)
            MCBLogger.LogWarning("[MCB] Building a version without the meshes Mixer committed, as asked: " +
                                 VersionAvatarMeshes.LeftOutMessage(shown.Committed.Select(s => s.Renderer)));
        var packageEntries = new List<FileManagerService.ModelFilePackageEntry>();
        var sourceFbxByPath = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            var sourceFbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            var entryProp = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i);
            var customFbx = entryProp.FindPropertyRelative("customFbx").objectReferenceValue as GameObject;
            string externalCustomFbxPath = entryProp.FindPropertyRelative("externalCustomFbxPath")?.stringValue;
            var customAvatar = entryProp.FindPropertyRelative("customBaseAvatar").objectReferenceValue as Avatar;
            bool hasCustomFbx = customFbx != null;
            bool hasExternalCustomFbx = !string.IsNullOrWhiteSpace(externalCustomFbxPath) && File.Exists(Path.GetFullPath(externalCustomFbxPath));
            bool hasCustomAvatar = customAvatar != null;
            if (!hasCustomFbx && string.IsNullOrWhiteSpace(externalCustomFbxPath) && !hasCustomAvatar) continue;
            if (!hasCustomFbx && !string.IsNullOrWhiteSpace(externalCustomFbxPath) && !hasExternalCustomFbx)
                throw new FileNotFoundException($"External Blender FBX for target {i + 1} no longer exists. Import the Blender export again from the Blender card, or choose Use original.", externalCustomFbxPath);
            if (sourceFbx == null)
                throw new Exception($"Target model file {i + 1} is missing its source FBX.");

            string sourceFbxPath = AssetDatabase.GetAssetPath(sourceFbx);
            if (string.IsNullOrWhiteSpace(sourceFbxPath))
                throw new Exception($"Source FBX {i + 1} is missing.");
            string localTargetPath = MCBUtils.ToUnityPath(sourceFbxPath);
            sourceFbxByPath[localTargetPath] = sourceFbx;

            var pathOverride = AvatarPathOverrideService.FindOverrideForLocalTarget(editor.customBaseTarget, localTargetPath);
            string referenceSourcePath = !string.IsNullOrWhiteSpace(pathOverride?.referenceSourcePath)
                ? AvatarPathOverrideService.NormalizeUnityPath(pathOverride.referenceSourcePath)
                : localTargetPath;

            string originalFbxPath = FileManagerService.GetOriginalBasePath(localTargetPath);
            if (!File.Exists(originalFbxPath))
            {
                originalFbxPath = localTargetPath;
            }
            if (!File.Exists(originalFbxPath))
                throw new Exception($"FBX file not found for target {i + 1}: {localTargetPath}");

            packageEntries.Add(new FileManagerService.ModelFilePackageEntry
            {
                sourceFbxPath = originalFbxPath,
                referenceSourcePath = referenceSourcePath,
                localTargetPath = localTargetPath,
                pathOverride = pathOverride,
                customFbx = customFbx,
                externalCustomFbxPath = hasExternalCustomFbx ? Path.GetFullPath(externalCustomFbxPath) : null,
                customBaseAvatar = customAvatar
            });
        }

        bool useAdvancedMeshReplacement =
            FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR) &&
            editor.useAdvancedMeshReplacementForCreatorProp != null &&
            editor.useAdvancedMeshReplacementForCreatorProp.boolValue;
        bool useHdiffFbxDelta =
            !useAdvancedMeshReplacement &&
            editor.useHdiffFbxDeltaForCreatorProp != null &&
            editor.useHdiffFbxDeltaForCreatorProp.boolValue;
        foreach (var packageEntry in packageEntries)
        {
            string sourceUnityPath = !string.IsNullOrWhiteSpace(packageEntry.localTargetPath)
                ? MCBUtils.ToUnityPath(packageEntry.localTargetPath)
                : MCBUtils.ToUnityPath(packageEntry.sourceFbxPath);
            sourceFbxByPath.TryGetValue(sourceUnityPath, out var sourceFbx);

            packageEntry.useAdvancedMeshReplacement = useAdvancedMeshReplacement &&
                                                     (packageEntry.customFbx != null ||
                                                      !string.IsNullOrWhiteSpace(packageEntry.externalCustomFbxPath));
            packageEntry.useHdiffFbxDelta = useHdiffFbxDelta &&
                                            !packageEntry.useAdvancedMeshReplacement &&
                                            (packageEntry.customFbx != null ||
                                             !string.IsNullOrWhiteSpace(packageEntry.externalCustomFbxPath));
            // Paths from the avatar's root, where a version apply looks them up (an avatar under an organizer object too).
            packageEntry.smrPaths = CollectTargetedSmrPathsForModelEntry(
                AvatarPaths.Root(editor.customBaseTarget),
                sourceUnityPath,
                sourceFbx,
                packageEntry.customFbx);
        }

        var protection = ResolveVersionProtection();
        if (!protection.xor)
        {
            if (packageEntries.Count == 0 || packageEntries.Any(entry => !entry.useAdvancedMeshReplacement))
                throw new InvalidOperationException("Turn on advanced mesh replacement to publish without XOR protection: only advanced meshes ship as one package for every original.");
            foreach (var packageEntry in packageEntries) packageEntry.encryptPayload = false;
        }

        // Metadata is constructed after packaging because the entry hashes are filled
        // in while the version folder is populated.
        Func<CustomBaseVersion> metadataFactory = () =>
        {
        EditorUtility.DisplayProgressBar("Preparing Build", "Calculating hashes and dependencies...", 0.5f);
        var sourceFileEntries = new List<ModelFileData>();
        var versionFileEntries = new List<ModelFileData>();
        foreach (var packageEntry in packageEntries)
        {
            string localTargetPath = !string.IsNullOrWhiteSpace(packageEntry.localTargetPath)
                ? MCBUtils.ToUnityPath(packageEntry.localTargetPath)
                : MCBUtils.ToUnityPath(packageEntry.sourceFbxPath);
            string referenceSourcePath = !string.IsNullOrWhiteSpace(packageEntry.referenceSourcePath)
                ? MCBUtils.ToUnityPath(packageEntry.referenceSourcePath)
                : localTargetPath;

            var sourceMetas = ResolveSourceMetasForPath(referenceSourcePath);
            string customFbxPath = packageEntry.customFbx != null
                ? AssetDatabase.GetAssetPath(packageEntry.customFbx)
                : packageEntry.externalCustomFbxPath;
            var customMetas = CollectModelImporterMetaForUnityPath(customFbxPath);

            var sourceFileEntry = new ModelFileData
            {
                path = referenceSourcePath,
                hash = packageEntry.sourceHash,
                type = "FBX",
                role = "SOURCE",
                metas = sourceMetas,
                smrPaths = packageEntry.smrPaths ?? new List<ModelFileSmrPathData>()
            };
            sourceFileEntries.Add(sourceFileEntry);

            var modelFileMetadata = new Dictionary<string, object>
            {
                { "sourcePath", referenceSourcePath },
                { "sourceHash", packageEntry.sourceHash }
            };
            if (!string.IsNullOrWhiteSpace(customFbxPath))
            {
                modelFileMetadata["customFbxPath"] = customFbxPath;
            }
            if (packageEntry.useAdvancedMeshReplacement)
            {
                modelFileMetadata["advancedRendererCount"] = packageEntry.advancedRendererCount.ToString();
            }
            if (!string.IsNullOrWhiteSpace(packageEntry.avatarUnityPath))
            {
                modelFileMetadata["customAvatarPath"] = Path.GetFileName(packageEntry.avatarUnityPath);
            }

            if (!string.IsNullOrWhiteSpace(packageEntry.binUnityPath))
            {
                var rawModelFileMetadata = new Dictionary<string, object>(modelFileMetadata);
                if (packageEntry.useAdvancedMeshReplacement)
                {
                    rawModelFileMetadata["payloadFormat"] = NativeMeshPayloadService.PayloadFormat;
                    rawModelFileMetadata[NativeMeshPayloadService.PayloadCompressionMetadataKey] = packageEntry.payloadCompression;
                    rawModelFileMetadata["deliveryVariants"] = packageEntry.payloadVariants;
                }
                else if (packageEntry.usedHdiffFbxDelta && packageEntry.hdiffBuildInfo != null)
                {
                    rawModelFileMetadata[HdiffService.DeltaFormatMetadataKey] = HdiffService.DeltaFormat;
                    rawModelFileMetadata[HdiffService.DeltaCompressionMetadataKey] = packageEntry.hdiffBuildInfo.compressionType;
                    rawModelFileMetadata[HdiffService.DeltaOriginalSizeMetadataKey] = packageEntry.hdiffBuildInfo.baseBytes.ToString();
                    rawModelFileMetadata[HdiffService.DeltaOutputSizeMetadataKey] = packageEntry.hdiffBuildInfo.outputBytes.ToString();
                    rawModelFileMetadata[HdiffService.DeltaPatchSizeMetadataKey] = packageEntry.hdiffBuildInfo.patchBytes.ToString();
                    rawModelFileMetadata[HdiffService.DeltaPatchRatioMetadataKey] = packageEntry.hdiffBuildInfo.patchRatio.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                versionFileEntries.Add(new ModelFileData
                {
                    path = Path.GetFileName(packageEntry.binUnityPath),
                    hash = packageEntry.binHash,
                    type = "BIN",
                    role = "PATCH",
                    compression = packageEntry.useAdvancedMeshReplacement
                        ? packageEntry.payloadCompression
                        : null,
                    transform = packageEntry.useAdvancedMeshReplacement
                        ? (packageEntry.encryptPayload ? ModelFileTransforms.XorBinToUnityAsset : NativeMeshPayloadService.PlainTransformName)
                        : (!string.IsNullOrWhiteSpace(packageEntry.patchTransform)
                            ? packageEntry.patchTransform
                            : ModelFileTransforms.XorBinToFbx),
                    outputHash = packageEntry.outputHash,
                    metas = customMetas,
                    metadata = rawModelFileMetadata
                });
            }
            else if (!string.IsNullOrWhiteSpace(packageEntry.avatarUnityPath))
            {
                versionFileEntries.Add(new ModelFileData
                {
                    path = Path.GetFileName(packageEntry.avatarUnityPath),
                    hash = packageEntry.avatarHash,
                    type = "PREFAB",
                    role = "PATCH",
                    transform = ModelFileTransforms.DirectAsset,
                    metadata = modelFileMetadata
                });
            }
        }

        var extraCustomization = ExtraCustomizationUtils.CloneEntries(selectedParentVersionObject?.extraCustomization);
        var customization = editor.customBaseTarget.creatorCustomization.Clone();
        customization.rendererLayout = CompleteRendererLayout(customization.rendererLayout, packageEntries.Where(e => e.rendererSlots != null).SelectMany(e => e.rendererSlots));
        customization.Validate();
        customization.Write(extraCustomization);

        bool hasAdvancedMeshPayload = packageEntries.Any(entry =>
            entry != null && entry.useAdvancedMeshReplacement && (entry.customFbx != null || !string.IsNullOrWhiteSpace(entry.externalCustomFbxPath)));
        if (hasAdvancedMeshPayload)
        {
            ExtraCustomizationUtils.SetFlag(extraCustomization, NativeMeshPayloadService.ExtraCustomizationKey, true);
        }
        else
        {
            ExtraCustomizationUtils.SetFlag(extraCustomization, NativeMeshPayloadService.ExtraCustomizationKey, false);
        }

        ExtraCustomizationUtils.SetFlag(extraCustomization, CustomVeinsKey, shouldIncludeCustomVeins);

        // Handle dynamic normals for body
        ExtraCustomizationUtils.SetFlag(extraCustomization, DynamicNormalBodyKey, shouldIncludeDynamicNormalsBody);

        // Handle dynamic normals for flexing
        ExtraCustomizationUtils.SetFlag(extraCustomization, DynamicNormalFlexingKey, shouldIncludeDynamicNormalsFlexing);
        ExtraCustomizationUtils.SetStringList(
            extraCustomization,
            SuggestRealisticKey,
            shouldIncludeSuggestRealistic ? suggestRealisticMeshPaths : null);

        string customVeinsAssetPath = shouldIncludeCustomVeins ? AssetDatabase.GetAssetPath(customVeinsTexture) : null;

        var metadata = new CustomBaseVersion {
            assetId = assetId,
            version = newVersionString,
            title = newVersionTitle,
            scope = newVersionScope,
            changelog = newChangelog,
            defaultAviVersion = defaultAviVersion,
            parentVersion = selectedParentVersionObject?.version,
            dependencies = fileManagerService.FindPrefabDependencies(logicPrefab),
            customBlendshapes = customBlendshapeEntries,
            extraCustomization = ExtraCustomizationUtils.ToArrayOrNull(extraCustomization),
            includeCustomVeins = shouldIncludeCustomVeins ? true : (bool?)null,
            customVeinsTexturePath = customVeinsAssetPath,
            includeDynamicNormalsBody = shouldIncludeDynamicNormalsBody ? true : (bool?)null,
            includeDynamicNormalsFlexing = shouldIncludeDynamicNormalsFlexing ? true : (bool?)null,
            // Local-only data for repopulating fields
            baseFbxHash = sourceFileEntries.FirstOrDefault()?.hash,
            customFbxPath = packageEntries.FirstOrDefault(entry => entry.customFbx != null) is { } firstFbxEntry ? AssetDatabase.GetAssetPath(firstFbxEntry.customFbx) : null,
            customBaseAvatarPath = packageEntries.FirstOrDefault(entry => entry.customBaseAvatar != null) is { } firstAvatarEntry ? AssetDatabase.GetAssetPath(firstAvatarEntry.customBaseAvatar) : null,
            logicPrefabPath = logicPrefab != null ? AssetDatabase.GetAssetPath(logicPrefab) : null,
            // Hashes of created files
            customAviHash = versionFileEntries.FirstOrDefault()?.hash,
            appliedCustomAviHash = versionFileEntries.FirstOrDefault()?.outputHash,
            sourceFiles = sourceFileEntries.ToArray(),
            versionFiles = NativeMeshPayloadService.ExpandRendererParts(versionFileEntries, packageEntries),
            protection = protection,
            skeleton = protection.xor ? null : RequiredSkeleton(packageEntries.Where(e => e.skeletonBones != null).SelectMany(e => e.skeletonBones), selectedAsset)
        };

        return metadata;
        };

        yield return VersionBuilder.BuildCoroutine(completed,
            fileManagerService,
            assetId,
            newVersionString,
            defaultAviVersion,
            packageEntries,
            logicPrefab,
            shouldIncludeCustomVeins,
            customVeinsTexture,
            shouldIncludeDynamicNormalsBody,
            shouldIncludeDynamicNormalsFlexing,
            fixedByAnimationAssetPaths,
            metadataFactory,
            ComputeFormSignature(),
            // One plain package serves every original; XOR versions are re-encrypted for each selected original.
            protection.xor ? OriginalBaseLibrary.Versions(selectedAsset).Where(v => OriginalBaseLibrary.Selection(selectedAsset).Contains(v.key)).ToArray() : null,
            protection.xor ? OriginalBaseLibrary.ActiveKey(selectedAsset) : null, editor.customBaseTarget.creatorCustomization,
            OriginalModelPaths());
    }

    // Every original model of the avatar, changed by this version or not: the logic package must not ship them.
    private List<string> OriginalModelPaths()
    {
        var paths = new List<string>();
        for (int i = 0; i < (editor.baseFbxFilesProp?.arraySize ?? 0); i++)
        {
            var model = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue;
            string path = model != null ? AssetDatabase.GetAssetPath(model) : null;
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// Canonical signature over EVERY form field that affects build outputs (the old
    /// signature only covered version|scope|title|changelog|parent, letting model-entry
    /// or toggle edits slip past the Publish gate). Stored in the artifact manifest so
    /// the pending-build state survives domain reloads.
    /// </summary>
    private string ComputeFormSignature()
    {
        var canonical = new System.Text.StringBuilder(512);
        var supportAsset = editor.GetSelectedAsset();
        if (supportAsset != null) canonical.Append(string.Join(",", OriginalBaseLibrary.Selection(supportAsset).OrderBy(key => key, StringComparer.Ordinal)));
        canonical.Append(newVersionMajor).Append('.').Append(newVersionMinor).Append('.').Append(newVersionPatch);
        canonical.Append('|').Append(newVersionScope);
        canonical.Append('|').Append(newVersionTitle);
        canonical.Append('|').Append(newChangelog);
        canonical.Append('|').Append(selectedParentVersionObject?.version);
        canonical.Append('|').Append(GetAssetPathOf(editor.avatarLogicPrefabProp));
        canonical.Append('|').Append(editor.includeCustomVeinsForCreatorProp.boolValue ? GetAssetPathOf(editor.customVeinsNormalMapProp) ?? "veins" : null);
        canonical.Append('|').Append(editor.customBaseTarget.creatorCustomization.Signature());
        var protectionSignature = ResolveVersionProtection();
        canonical.Append('|').Append(protectionSignature.xor).Append(',').Append(protectionSignature.discordRole);
        canonical.Append('|').Append(editor.includeDynamicNormalsBodyForCreatorProp.boolValue);
        canonical.Append('|').Append(editor.includeDynamicNormalsFlexingForCreatorProp.boolValue);
        canonical.Append('|').Append(editor.useAdvancedMeshReplacementForCreatorProp != null && editor.useAdvancedMeshReplacementForCreatorProp.boolValue);
        canonical.Append('|').Append(editor.useHdiffFbxDeltaForCreatorProp != null && editor.useHdiffFbxDeltaForCreatorProp.boolValue);
        canonical.Append('|').Append(HasSuggestRealisticPayload()
            ? string.Join(",", GetSerializedStringList(editor.suggestRealisticMeshPathsForCreatorProp))
            : null);

        SyncModelFileBuildEntryCount();
        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i);
            var sourceFbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue;
            canonical.Append("|entry:")
                .Append(sourceFbx != null ? AssetDatabase.GetAssetPath(sourceFbx) : null).Append(';')
                .Append(GetAssetPathOf(entry.FindPropertyRelative("customFbx"))).Append(';')
                .Append(entry.FindPropertyRelative("externalCustomFbxPath")?.stringValue).Append(';')
                .Append(GetAssetPathOf(entry.FindPropertyRelative("customBaseAvatar")));
        }

        canonical.Append("|blendshapes:").Append(JsonConvert.SerializeObject(
            editor.customBaseTarget != null ? editor.customBaseTarget.customBlendshapesForCreator : null));

        return VersionManifest.ComputeStringHash(canonical.ToString());
    }

    private static string GetAssetPathOf(SerializedProperty objectProp)
    {
        var obj = objectProp?.objectReferenceValue;
        return obj != null ? AssetDatabase.GetAssetPath(obj) : null;
    }

    /// <summary>
    /// Unsubmitted versions are persisted inside their version folder (version.json +
    /// manifest written by the repository at build time), so "removing" one only needs
    /// to clear the pending-build state and refresh the lists — the folder deletion or
    /// publish transition already happened in the repository.
    /// </summary>
    public void RemoveUnsubmittedVersion(CustomBaseVersion versionToRemove)
    {
        if (builtPendingVersion != null && builtPendingVersion.Equals(versionToRemove))
        {
            ClearBuiltPendingVersion();
        }

        VersionRepository.InvalidateCache();
        editor.LoadUnsubmittedVersions(true);
        editor.Repaint();
    }

    private void PopulateFieldsFromVersion(CustomBaseVersion ver)
    {
        isRestoringFromVersionState = true;
        try
        {
            var parsedVersion = editor.ParseVersion(ver.version);
            newVersionMajor = parsedVersion.Major;
            newVersionMinor = parsedVersion.Minor;
            newVersionPatch = parsedVersion.Build;
            newVersionScope = ver.scope;
            newVersionTitle = ver.title ?? "";
            newChangelog = ver.changelog ?? "";

            if (!string.IsNullOrEmpty(ver.parentVersion))
            {
                int parentIdx = compatibleParentVersions.FindIndex(p => p.version == ver.parentVersion);
                if (parentIdx != -1)
                {
                    selectedParentVersionIndex = parentIdx;
                    selectedParentVersionObject = compatibleParentVersions[parentIdx];
                }
                else
                {
                    selectedParentVersionIndex = -1;
                    selectedParentVersionObject = null;
                }
            }
            else
            {
                selectedParentVersionIndex = -1;
                selectedParentVersionObject = null;
            }

            // Restoring a saved/built version must keep the artifact's own version.
            // Parent selection is restored above only to preserve form context.
            newVersionMajor = parsedVersion.Major;
            newVersionMinor = parsedVersion.Minor;
            newVersionPatch = parsedVersion.Build;

            ApplyVersionContentToForm(ver, fillFromParent: false);
        }
        finally
        {
            isRestoringFromVersionState = false;
        }
    }

    /// <summary>
    /// Fills the new-version form with the parent's content (model files, avatars, logic, blendshapes, options),
    /// keeping this version's number, scope, title, changelog and parent. A file field is only ever set, never cleared:
    /// published versions do not carry the creator's local files, so those come from the parent's downloaded version
    /// folder when there is one, and otherwise the field keeps what it has. Blendshapes stay when the parent has none.
    /// </summary>
    private void FillFormFromParent(CustomBaseVersion parent)
    {
        if (parent == null) return;
        parentFill = new ParentFill { version = parent, downloadedWhenFilled = MCBUtils.IsVersionDownloaded(parent) };
        isRestoringFromVersionState = true;
        try
        {
            ApplyVersionContentToForm(parent, fillFromParent: true);
        }
        finally
        {
            isRestoringFromVersionState = false;
        }
    }

    // Filling replaces what the form holds: asked first, as Build and Publish are, unless the form holds nothing of its own.
    private bool ConfirmFillFromParent(CustomBaseVersion parent) =>
        parent != null && (!CreatorFormHasOwnContent() || EditorUtility.DisplayDialog("Fill from Parent",
            $"Fill this form with {parent.version}'s content?\n\nIts model files, avatars, logic prefab, blendshapes and options replace " +
            $"what the form has; fields {parent.version} cannot fill keep what they have. The version number, scope, title and changelog stay.",
            "Fill", "Cancel"));

    // Model files, avatars, logic prefab, blendshapes or a veins texture the creator chose (not the parent's veins the
    // form picked by itself).
    private bool CreatorFormHasOwnContent()
    {
        editor.serializedObject.Update();
        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i);
            if (entry.FindPropertyRelative("customFbx")?.objectReferenceValue != null ||
                entry.FindPropertyRelative("customBaseAvatar")?.objectReferenceValue != null ||
                !string.IsNullOrWhiteSpace(entry.FindPropertyRelative("externalCustomFbxPath")?.stringValue))
                return true;
        }
        string veins = AssetDatabase.GetAssetPath(editor.customVeinsNormalMapProp.objectReferenceValue);
        return editor.avatarLogicPrefabProp.objectReferenceValue != null || editor.customBlendshapesForCreatorProp.arraySize > 0 ||
               (!string.IsNullOrEmpty(veins) && veins != autoAssignedVeinsTexturePath);
    }

    // What the last Fill from Parent could not fill, and why: the note under the Parent Version dropdown.
    private sealed class ParentFill
    {
        public CustomBaseVersion version;
        public bool downloadedWhenFilled;
        // Files that come from the parent's downloaded version folder.
        public readonly List<string> fromFolder = new List<string>();
        // Content the parent itself does not have.
        public readonly List<string> missing = new List<string>();
        public bool customFbx, logicNeedsImport;
    }

    private ParentFill parentFill;

    /// <summary>
    /// The note under the Parent Version dropdown, written each time it shows: a parent downloaded since the fill says
    /// that filling again completes the form, instead of still asking for the download.
    /// </summary>
    private string ParentFillNote()
    {
        var fill = parentFill;
        if (fill == null || !Equals(fill.version, selectedParentVersionObject)) return null;
        string parent = fill.version.version;
        var note = new List<string>();
        if (fill.fromFolder.Count > 0)
        {
            string fields = JoinFieldNames(fill.fromFolder.Distinct().ToList());
            note.Add(!MCBUtils.IsVersionDownloaded(fill.version)
                ? $"Download {parent} to also fill the {fields}: until then those fields keep what they had."
                : fill.downloadedWhenFilled
                    ? $"{parent} has no {fields}: those fields keep what they had."
                    : $"{parent} is downloaded now: press Fill from Parent again to also fill the {fields}.");
        }
        if (fill.missing.Count > 0)
            note.Add($"{parent} has no {JoinFieldNames(fill.missing.Distinct().ToList())}: the form keeps its own.");
        if (fill.logicNeedsImport)
            note.Add($"{parent}'s logic prefab comes with its logic package, which this project has not imported yet: apply {parent} once, then press Fill from Parent again.");
        if (fill.customFbx)
            note.Add($"{parent} does not record its custom FBX on this computer: the custom FBX field keeps what it had.");
        return note.Count == 0 ? null : string.Join(" ", note);
    }

    private static string JoinFieldNames(IReadOnlyList<string> names) => names.Count == 1 ? names[0]
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];

    private void ApplyVersionContentToForm(CustomBaseVersion ver, bool fillFromParent)
    {
        SyncModelFileBuildEntryCount();
        var versionFilesBySourcePath = new Dictionary<string, ModelFileData>(StringComparer.OrdinalIgnoreCase);
        foreach (var versionFile in ver.versionFiles ?? Array.Empty<ModelFileData>())
        {
            string sourcePath = GetMetadataString(versionFile, "sourcePath");
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string normalizedSourcePath = MCBUtils.ToUnityPath(sourcePath);
            versionFilesBySourcePath[normalizedSourcePath] = versionFile;
            var sourceFile = ver.sourceFiles?.FirstOrDefault(file =>
                file != null &&
                (versionFile.sourceModelFileId.HasValue && file.id == versionFile.sourceModelFileId.Value ||
                 string.Equals(MCBUtils.ToUnityPath(file.path), normalizedSourcePath, StringComparison.OrdinalIgnoreCase)));
            string localTargetPath = AvatarPathOverrideService.ResolveLocalTargetPath(
                editor.customBaseTarget,
                sourceFile,
                updateStoredPathFromGuid: false);
            if (!string.IsNullOrWhiteSpace(localTargetPath))
            {
                versionFilesBySourcePath[MCBUtils.ToUnityPath(localTargetPath)] = versionFile;
            }
        }

        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            var entryProp = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i);
            var sourceFbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            string sourcePath = sourceFbx != null ? MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(sourceFbx)) : null;
            ModelFileData versionFile = null;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                versionFilesBySourcePath.TryGetValue(sourcePath, out versionFile);
            }

            string customFbxPath = GetMetadataString(versionFile, "customFbxPath");
            string customAvatarPath = GetMetadataString(versionFile, "customAvatarPath");
            if (!string.IsNullOrWhiteSpace(customAvatarPath))
            {
                customAvatarPath = MCBUtils.GetVersionAvatarPath(ver, Path.GetFileName(customAvatarPath));
            }
            else if (fillFromParent && (versionFile == null || editor.modelFileBuildEntriesProp.arraySize == 1))
            {
                // A model the parent's metadata does not match (its original moved): its avatar by the model's name.
                customAvatarPath = FileManagerService.VersionModelAvatarPath(MCBUtils.GetVersionDataPath(ver), sourcePath,
                    editor.modelFileBuildEntriesProp.arraySize);
            }
            var restoredCustomFbx = AssetDatabase.LoadAssetAtPath<GameObject>(customFbxPath);
            bool externalFbx = restoredCustomFbx == null && !string.IsNullOrWhiteSpace(customFbxPath);
            if (fillFromParent && restoredCustomFbx == null && !(externalFbx && File.Exists(customFbxPath)))
            {
                parentFill.customFbx = true;
            }
            else
            {
                entryProp.FindPropertyRelative("customFbx").objectReferenceValue = restoredCustomFbx;
                var externalCustomFbxPath = entryProp.FindPropertyRelative("externalCustomFbxPath");
                if (externalCustomFbxPath != null)
                {
                    externalCustomFbxPath.stringValue = externalFbx ? customFbxPath : "";
                }
            }
            if (!AssignFormAsset(entryProp.FindPropertyRelative("customBaseAvatar"), AssetDatabase.LoadAssetAtPath<Avatar>(customAvatarPath), fillFromParent))
                parentFill.fromFolder.Add("custom avatar");
        }
        if (editor.useAdvancedMeshReplacementForCreatorProp != null)
        {
            editor.useAdvancedMeshReplacementForCreatorProp.boolValue = NativeMeshPayloadService.VersionUsesAdvancedMesh(ver);
        }
        if (editor.useHdiffFbxDeltaForCreatorProp != null)
        {
            editor.useHdiffFbxDeltaForCreatorProp.boolValue = (ver.versionFiles ?? Array.Empty<ModelFileData>())
                .Any(file => !string.IsNullOrWhiteSpace(GetMetadataString(file, HdiffService.DeltaFormatMetadataKey)));
        }
        string logicPrefabPath = ver.logicPrefabPath;
        bool logicNeedsImport = false;
        // A published parent names the creator's prefab, which this project may not have (or has elsewhere): the prefab its
        // logic package carries, as this project has it.
        if (fillFromParent && (string.IsNullOrWhiteSpace(logicPrefabPath) || AssetDatabase.LoadAssetAtPath<GameObject>(logicPrefabPath) == null))
            logicPrefabPath = FileManagerService.VersionLogicPrefabPath(ver, out logicNeedsImport);
        var logicPrefab = string.IsNullOrWhiteSpace(logicPrefabPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(logicPrefabPath);
        if (!AssignFormAsset(editor.avatarLogicPrefabProp, logicPrefab, fillFromParent))
        {
            if (logicNeedsImport) parentFill.logicNeedsImport = true;
            else parentFill.fromFolder.Add("logic prefab");
        }

        if (fillFromParent && (ver.customBlendshapes == null || ver.customBlendshapes.Length == 0))
        {
            // Never cleared: the form keeps the blendshapes it has.
            if (editor.customBlendshapesForCreatorProp.arraySize > 0) parentFill.missing.Add("blendshapes");
        }
        else
        {
            editor.customBlendshapesForCreatorProp.ClearArray();
            for (int i = 0; i < (ver.customBlendshapes?.Length ?? 0); i++)
            {
                editor.customBlendshapesForCreatorProp.InsertArrayElementAtIndex(i);
                var element = editor.customBlendshapesForCreatorProp.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("name").stringValue = ver.customBlendshapes[i].name;
                element.FindPropertyRelative("defaultValue").stringValue = ver.customBlendshapes[i].defaultValue;
                element.FindPropertyRelative("isSlider").boolValue = ver.customBlendshapes[i].isSlider;
                element.FindPropertyRelative("isSliderDefault").boolValue = ver.customBlendshapes[i].isSliderDefault;
                CopyCorrectivesToSerialized(element, ver.customBlendshapes[i].correctiveBlendshapes);
            }
        }

        bool includeCustomVeins = ver.includeCustomVeins ?? ExtraCustomizationUtils.HasFlag(ver.extraCustomization, CustomVeinsKey);
        editor.includeCustomVeinsForCreatorProp.boolValue = includeCustomVeins;

        string veinsTexturePath = ver.customVeinsTexturePath;
        // As for the logic prefab: the creator's texture when this project has it, else the parent folder's copy.
        if (fillFromParent && includeCustomVeins &&
            (string.IsNullOrEmpty(veinsTexturePath) || AssetDatabase.LoadAssetAtPath<Texture2D>(veinsTexturePath) == null))
            veinsTexturePath = MCBUtils.GetVersionVeinsTexturePath(ver);
        if (!AssignFormAsset(editor.customVeinsNormalMapProp, AssetDatabase.LoadAssetAtPath<Texture2D>(veinsTexturePath), fillFromParent) && includeCustomVeins)
            parentFill.fromFolder.Add("veins texture");

        string assignedPath = AssetDatabase.GetAssetPath(editor.customVeinsNormalMapProp.objectReferenceValue);
        string parentPath = MCBUtils.GetVersionVeinsTexturePath(selectedParentVersionObject);
        autoAssignedVeinsTexturePath = (!string.IsNullOrEmpty(assignedPath) && assignedPath == parentPath) ? assignedPath : null;
        lastParentVersionForVeins = selectedParentVersionObject;

        if (editor.includeSuggestRealisticForCreatorProp != null && editor.suggestRealisticMeshPathsForCreatorProp != null)
        {
            var suggestRealisticPaths = ExtraCustomizationUtils.GetStringList(ver.extraCustomization, SuggestRealisticKey);
            editor.includeSuggestRealisticForCreatorProp.boolValue = suggestRealisticPaths.Count > 0;
            SetSerializedStringList(editor.suggestRealisticMeshPathsForCreatorProp, suggestRealisticPaths);
        }

        editor.serializedObject.ApplyModifiedProperties();
        editor.customBaseTarget.creatorCustomization = VersionCustomization.Read(ver.extraCustomization);
        EditorUtility.SetDirty(editor.customBaseTarget);
        editor.serializedObject.Update();
        editor.Repaint();
    }

    // Filling from a parent only sets fields it can resolve; restoring a saved build sets each field to what the build had.
    // Returns whether the field was set.
    private static bool AssignFormAsset(SerializedProperty prop, UnityEngine.Object asset, bool fillFromParent)
    {
        if (asset == null && fillFromParent) return false;
        prop.objectReferenceValue = asset;
        return true;
    }

    private static string GetMetadataString(ModelFileData file, string key)
    {
        if (file?.metadata == null || string.IsNullOrWhiteSpace(key)) return null;
        return file.metadata.TryGetValue(key, out object value) ? value?.ToString() : null;
    }

    private List<Dictionary<string, string>> ResolveSourceMetasForPath(string sourceUnityPath)
    {
        string normalizedPath = MCBUtils.ToUnityPath(sourceUnityPath);
        var selectedAsset = editor.GetSelectedAsset();
        var sourceFile = selectedAsset?.sourceFiles?.FirstOrDefault(file =>
            file != null &&
            string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MCBUtils.ToUnityPath(file.path), normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (sourceFile?.metas != null && sourceFile.metas.Count > 0)
        {
            return CloneMetas(sourceFile.metas);
        }

        return CollectModelImporterMetaForUnityPath(normalizedPath);
    }

    private static List<Dictionary<string, string>> CollectModelImporterMetaForUnityPath(string unityPath)
    {
        var metas = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(unityPath)) return metas;

        string normalizedPath = MCBUtils.ToUnityPath(unityPath);
        string metaPath = normalizedPath + ".meta";
        string fullMetaPath = Path.GetFullPath(metaPath);
        if (!File.Exists(fullMetaPath)) return metas;

        metas.Add(new Dictionary<string, string>
        {
            { "file", Path.GetFileName(normalizedPath) },
            { "meta", File.ReadAllText(fullMetaPath) }
        });
        return metas;
    }

    private static List<Dictionary<string, string>> CloneMetas(IEnumerable<Dictionary<string, string>> metas)
    {
        return (metas ?? Enumerable.Empty<Dictionary<string, string>>())
            .Where(entry => entry != null)
            .Select(entry => new Dictionary<string, string>(entry, StringComparer.Ordinal))
            .ToList();
    }

    private const string PendingBuildSessionKeyPrefix = "MCB_PendingBuild_";
    private bool pendingBuildRecoveryAttempted;

    public bool HasBuiltPendingVersion
    {
        get
        {
            if (builtPendingVersion == null)
            {
                TryRecoverPendingBuildAfterReload();
            }

            return builtPendingVersion != null &&
                   !string.IsNullOrEmpty(builtPendingVersionSignature) &&
                   string.Equals(builtPendingVersionSignature, ComputeFormSignature(), StringComparison.Ordinal);
        }
    }

    private void ClearBuiltPendingVersion()
    {
        if (builtPendingVersion != null && builtPendingVersion.assetId > 0)
        {
            SessionState.EraseString(PendingBuildSessionKeyPrefix + builtPendingVersion.assetId);
        }

        builtPendingVersion = null;
        builtPendingVersionSignature = null;
    }

    private void RememberPendingBuild(CustomBaseVersion version, string formSignature = null)
    {
        builtPendingVersion = version;
        builtPendingVersionSignature = !string.IsNullOrEmpty(formSignature)
            ? formSignature
            : ComputeFormSignature();
        if (version != null && version.assetId > 0)
        {
            SessionState.SetString(PendingBuildSessionKeyPrefix + version.assetId, FormatPendingBuildKey(version));
        }
    }

    // The full storage identity: builds for an original-base source live in their own "-<key>" folder.
    internal static string FormatPendingBuildKey(CustomBaseVersion version) =>
        $"{version.assetId}|{version.version}|{version.defaultAviVersion}|{version.sourceVersionKey}";

    internal static CustomBaseVersion ParsePendingBuildKey(string stored)
    {
        string[] parts = (stored ?? string.Empty).Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[0], out int assetId)) return null;
        return new CustomBaseVersion
        {
            assetId = assetId, version = parts[1], defaultAviVersion = parts[2],
            sourceVersionKey = string.IsNullOrEmpty(parts[3]) ? null : parts[3], isUnsubmitted = true
        };
    }

    /// <summary>
    /// Domain reloads used to silently drop the pending build (Publish disappeared even
    /// though the artifact was on disk). The artifact manifest carries the form
    /// signature, so the pending state can be recovered from SessionState + disk.
    /// </summary>
    private void TryRecoverPendingBuildAfterReload()
    {
        if (pendingBuildRecoveryAttempted)
        {
            return;
        }

        pendingBuildRecoveryAttempted = true;
        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null || selectedAsset.id <= 0)
        {
            pendingBuildRecoveryAttempted = false; // retry once an asset is selected
            return;
        }

        var key = ParsePendingBuildKey(SessionState.GetString(PendingBuildSessionKeyPrefix + selectedAsset.id, string.Empty));
        if (key == null)
        {
            return;
        }

        var artifact = VersionRepository.GetArtifact(key);
        if (artifact?.Manifest == null || !artifact.Manifest.unsubmitted || string.IsNullOrEmpty(artifact.Manifest.formSignature) || !artifact.FolderExists)
        {
            return;
        }

        artifact.Metadata.isUnsubmitted = true;
        builtPendingVersion = artifact.Metadata;
        builtPendingVersionSignature = artifact.Manifest.formSignature;
        MCBLogger.Log($"[CreatorMode] Recovered pending build v{artifact.Metadata.version} after domain reload.");
    }

    private IEnumerator BuildAndApplyLocalVersionCoroutine(bool takeAvatarMeshes)
    {
        // Build and apply hold the process-wide guard together: an MCP build or another avatar's publish waits.
        if (!VersionOperationGuard.TryAcquire("building and applying a version", out var operation, out string busy))
        {
            editor.submitError = busy;
            editor.warningsModule.AddWarning(busy, MessageType.Warning, "Build not started");
            RefreshEditorUi();
            yield break;
        }
        editor.isSubmitting = true;
        editor.submitError = "";
        editor.warningsModule.Clear();
        // Left out because the creator chose so: said after the build too.
        string leftOut = takeAvatarMeshes ? null : VersionAvatarMeshes.LeftOutMessage(VersionAvatarMeshes.Find(editor).Committed.Select(s => s.Renderer));
        editor.Repaint();

        VersionArtifact builtArtifact = null;
        Exception buildError = null;

        try
        {
            yield return MCBWork.Guard(BuildNewVersionCoroutine(value => builtArtifact = value, operation, takeAvatarMeshes), ex => buildError = ex);

            if (buildError != null)
            {
                ClearBuiltPendingVersion();
                editor.submitError = OperationErrorReporter.Report(
                    editor,
                    OperationErrorClassifier.Classify(buildError, "Building the version"),
                    "Build Failed");
            }
            else
            {
                // The artifact (version folder + version.json + manifest) is already
                // committed and persisted as unsubmitted by VersionBuilder.
                string taken = VersionAvatarMeshes.TakenMessage(TakenFromAvatar);
                if (taken != null) editor.warningsModule.AddWarning(taken, MessageType.Info, "Taken from the avatar");
                if (leftOut != null) editor.warningsModule.AddWarning(leftOut, MessageType.Warning, "Not in this version");
                var metadata = builtArtifact.Metadata;
                creatorWindow?.Built(metadata);
                RememberPendingBuild(metadata, builtArtifact.Manifest?.formSignature);
                editor.LoadUnsubmittedVersions(true);
                var versionActions = new VersionActions(editor, networkService, fileManagerService);

                AssetDatabase.Refresh();
                while(EditorApplication.isCompiling || EditorApplication.isUpdating) { yield return null; }

                // Apply the newly built version
                yield return versionActions.ApplyOrResetCoroutine(metadata, false);

                // Keep the creator form on the temporary version we just built: applying it can
                // change the applied/selected version and re-suggest a next version number,
                // which would otherwise reset the form (and disable Publish).
                editor.selectedVersionForAction = metadata;
                editor.selectedCustomVersionForAction = null;
                previouslySelectedVersion = metadata;
                PopulateFieldsFromVersion(metadata);
                RememberPendingBuild(metadata, builtArtifact.Manifest?.formSignature);
            }
        }
        finally
        {
            operation.Dispose();
            editor.isSubmitting = false;
            EditorUtility.ClearProgressBar();
            editor.Repaint();
        }

        RefreshSiblingVersionLists();
        RefreshEditorUi();
    }

    private IEnumerator PublishBuiltVersionCoroutine()
    {
        if (!HasBuiltPendingVersion)
        {
            EditorUtility.DisplayDialog("Publish", "Build the version first: the form changed since the last build or no build exists yet.", "OK");
            yield break;
        }

        // VersionPublisher validates the stored artifact against its manifest and
        // uploads exactly the built outputs. If files were deleted or modified since
        // the build, it blocks with an explicit "rebuild first" message — publishing
        // never silently repackages from live project state.
        yield return UploadUnsubmittedVersionCoroutine(builtPendingVersion);
    }

    /// <summary>
    /// Single upload path for both the creator-form Publish button and the
    /// version-list Upload button. The artifact is validated against its manifest and
    /// uploaded exactly as built (see VersionPublisher) — the old behavior of
    /// re-packaging the version from live project files at upload time is gone.
    /// </summary>
    public IEnumerator UploadUnsubmittedVersionCoroutine(CustomBaseVersion unsubmittedVersion)
    {
        yield return VersionPublisher.PublishCoroutine(
            editor,
            networkService,
            fileManagerService,
            unsubmittedVersion,
            onPublished: () =>
            {
                if (ReferenceEquals(unsubmittedVersion, builtPendingVersion) ||
                    (builtPendingVersion != null && builtPendingVersion.Equals(unsubmittedVersion)))
                {
                    ClearBuiltPendingVersion();
                }
            });
        RefreshSiblingVersionLists();
        RefreshEditorUi();
    }

    private void RefreshSiblingVersionLists()
    {
        foreach (var other in Resources.FindObjectsOfTypeAll<MCBEditor>())
        {
            if (other == editor || other.target == null || other.GetSelectedAsset()?.id != editor.GetSelectedAsset()?.id) continue;
            other.LoadUnsubmittedVersions(true);
            other.LoadImportedVersions(true);
            other.serverVersions = editor.serverVersions.ToList();
            other.serializedObject.Update();
            other.RefreshUiToolkitSections();
        }
    }
}
#endif


