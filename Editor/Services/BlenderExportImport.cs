#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Brings a Blender export back into a custom base: the manifest.json an MCB export writes next to its FBX files, one
/// CUSTOM_BASE model per target FBX, plus the XMuscles correctives XMuscle Orbit Helper baked. Each model becomes the
/// creator's custom FBX for its target, imported with a generated Avatar, or, for an advanced-mesh export, is kept out of
/// the asset database and shown on the avatar as the version's payload will show it. Live edits of the base go through
/// Orbiters Mixer's link; an export is how topology changes and baked correctives come back.
/// </summary>
internal static class BlenderExportImport
{
    public const string ExportKind = "orbiters.mcb.blenderExport";
    /// <summary>The manifest version this reads, and the only one accepted.</summary>
    public const int ProtocolVersion = 2;
    /// <summary>The XMuscle Orbit Helper release whose exports MCB reads.</summary>
    public const string XMuscleOrbitHelperVersion = "0.10.0";
    /// <summary>Imported exports (the creator's custom FBX files), one folder per custom base.</summary>
    public const string ExportsFolder = "Assets/MCB/generated/blenderExports";
    /// <summary>Advanced-mesh exports, kept where Unity never imports them.</summary>
    public const string AdvancedExportsFolder = "Assets/MCB/generated/blenderAdvancedExports~";

    public static string LastStatus { get; private set; }
    public static MessageType LastStatusType { get; private set; } = MessageType.Info;
    public static event Action Changed;

    internal sealed class BlenderExportManifest
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
        public TargetInfo target;
        public List<ModelInfo> models;
        public bool advancedMeshBlenderLink;
        /// <summary>The XMuscles correctives of the export (XMuscle Orbit Helper's block), or null.</summary>
        public MuscleCorrectiveSet xmuscle;
    }

    internal sealed class TargetInfo
    {
        public string targetFbxPath;
        public string customBaseName;
    }

    internal sealed class ModelInfo
    {
        public string role;
        public string path;
        public string targetFbxPath;
        public string transportFormat;
        public string unityImportMode;
        public string payloadSourceFormat;
        public List<string> meshNames;
        /// <summary>The shape keys each exported mesh carries (XMuscles' baked shapes among them).</summary>
        public Dictionary<string, List<string>> shapeKeysByMesh;
    }

    /// <summary>A target FBX of the base with the avatar renderers that show its meshes.</summary>
    internal sealed class ExportTarget
    {
        public string UnityPath;
        public List<ModelFileSmrPathData> SmrPaths = new List<ModelFileSmrPathData>();
    }

    // ---- Targets ----------------------------------------------------------------------------------------------------

    /// <summary>The base's target FBX files: the selected asset's SOURCE files, else the FBX files detected on the avatar.</summary>
    public static List<string> TargetFbxPaths(MCBEditor editor)
    {
        if (editor == null) return new List<string>();
        var selectedAsset = editor.GetSelectedAsset();
        var sourceFbxPaths = selectedAsset?.sourceFiles?
            .Where(file => file != null
                           && string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(file.path)
                           && AssetImporter.GetAtPath(MCBUtils.ToUnityPath(file.path)) is ModelImporter)
            .Select(file => MCBUtils.ToUnityPath(file.path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (sourceFbxPaths.Count > 0) return sourceFbxPaths;
        return editor.GetDetectedAvatarFbxPaths()
            .Where(path => !string.IsNullOrWhiteSpace(path) && AssetImporter.GetAtPath(path) is ModelImporter)
            .Select(MCBUtils.ToUnityPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The targets with their renderer mappings: found on the avatar, completed with the asset's records.</summary>
    internal static List<ExportTarget> Targets(MCBEditor editor)
    {
        var paths = TargetFbxPaths(editor);
        if (paths.Count == 0 || editor.customBaseTarget == null) return new List<ExportTarget>();
        var selectedAsset = editor.GetSelectedAsset();
        var discovered = SmrPathService.CollectSmrPathsByFbx(editor.customBaseTarget.transform.root, paths);
        return paths.Select(path => new ExportTarget
        {
            UnityPath = path,
            SmrPaths = MergeSmrPaths(discovered.TryGetValue(path, out var entries) ? entries : null, SelectedAssetSmrPaths(selectedAsset, path)),
        }).ToList();
    }

    /// <summary>The avatar's renderers that show the base's targets, each with its target FBX and the FBX mesh it shows.</summary>
    internal static List<(SkinnedMeshRenderer Renderer, string TargetUnityPath, string FbxMesh)> TargetRenderers(MCBEditor editor)
    {
        var result = new List<(SkinnedMeshRenderer, string, string)>();
        if (editor == null || editor.customBaseTarget == null) return result;
        var avatarRoot = editor.customBaseTarget.transform.root;
        foreach (var target in Targets(editor))
            foreach (var entry in Renderers(avatarRoot, target.SmrPaths))
                if (result.All(r => r.Item1 != entry.Renderer)) result.Add((entry.Renderer, target.UnityPath, entry.FbxMesh));
        return result;
    }

    /// <summary>The avatar's renderers for a target FBX (by the renderer paths), each with the FBX mesh it shows.</summary>
    internal static List<(SkinnedMeshRenderer Renderer, string FbxMesh)> Renderers(Transform avatarRoot, IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        var list = new List<(SkinnedMeshRenderer, string)>();
        if (avatarRoot == null) return list;
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

    private static List<ModelFileSmrPathData> SelectedAssetSmrPaths(AvatarDiscoveredAsset selectedAsset, string unityPath)
    {
        if (selectedAsset?.sourceFiles == null || string.IsNullOrWhiteSpace(unityPath)) return new List<ModelFileSmrPathData>();
        var source = selectedAsset.sourceFiles.FirstOrDefault(file =>
            file != null &&
            string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MCBUtils.ToUnityPath(file.path), MCBUtils.ToUnityPath(unityPath), StringComparison.OrdinalIgnoreCase));
        return source?.smrPaths ?? new List<ModelFileSmrPathData>();
    }

    private static List<ModelFileSmrPathData> MergeSmrPaths(params IEnumerable<ModelFileSmrPathData>[] sources)
    {
        var result = new List<ModelFileSmrPathData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources ?? new IEnumerable<ModelFileSmrPathData>[0])
            foreach (var entry in source ?? Enumerable.Empty<ModelFileSmrPathData>())
            {
                if (entry == null) continue;
                string key = $"{entry.avatarPath}|{entry.fbxMeshPath}|{entry.meshName}|{entry.rendererName}";
                if (seen.Add(key)) result.Add(entry);
            }
        return result;
    }

    // ---- Import -----------------------------------------------------------------------------------------------------

    /// <summary>Asks for an export's manifest.json and imports it for the editor's custom base. False when the dialog was cancelled.</summary>
    public static bool PickAndImport(MCBEditor editor)
    {
        string path = EditorUtility.OpenFilePanel("Blender export (manifest.json)", LastExportFolder, "json");
        if (string.IsNullOrEmpty(path)) return false;
        LastExportFolder = Path.GetDirectoryName(path);
        Import(editor, path);
        return true;
    }

    private static string LastExportFolder
    {
        get => EditorPrefs.GetString("MCB.BlenderExport.LastFolder", "");
        set => EditorPrefs.SetString("MCB.BlenderExport.LastFolder", value ?? "");
    }

    /// <summary>Imports an export for the editor's custom base. Failures land in <see cref="LastStatus"/> and the Console, never thrown.</summary>
    public static void Import(MCBEditor editor, string manifestPath)
    {
        try
        {
            Apply(editor, manifestPath);
        }
        catch (Exception ex)
        {
            SetStatus("Blender export import failed: " + ex.Message + "\nThe avatar was not changed. Fix the export files or export again from Blender to retry.", MessageType.Error);
            MCBLogger.LogError($"[BlenderExport] Import failed for {manifestPath}: {ex}");
        }
    }

    private static void Apply(MCBEditor editor, string manifestPath)
    {
        if (editor == null || editor.customBaseTarget == null) throw new InvalidOperationException("Select the custom base in the scene first.");
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) throw new FileNotFoundException("Blender export manifest was not found.", manifestPath);
        var customBase = editor.customBaseTarget;
        var manifest = ReadExportManifest(manifestPath);
        var models = manifest.models?
            .Where(x => x != null && string.Equals(x.role, "CUSTOM_BASE", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<ModelInfo>();
        if (models.Count == 0) throw new InvalidOperationException("Blender export manifest has no CUSTOM_BASE model.");
        var targets = Targets(editor);
        if (targets.Count == 0) throw new InvalidOperationException("No target FBX was detected for this avatar: assign or detect the base FBX first.");

        string exportDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        string baseName = BaseFolderName(editor);
        // Check every model before touching the avatar, then apply them as one step: a bad second model must not leave
        // the first one applied.
        var plans = PlanModelUpdates(customBase, targets, manifest, models, exportDir);
        if (plans.Count == 0) throw new InvalidOperationException("Blender export manifest did not contain any usable CUSTOM_BASE model paths.");

        var updatedTargets = new List<string>();
        int generatedAvatarCount = 0;
        int advancedCount = 0;
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Import Blender export");
        using (var files = new ExportFileRollback())
        {
            try
            {
                foreach (var plan in plans)
                {
                    if (plan.advancedMesh)
                    {
                        string externalFbxPath = CopyModelToAdvancedExports(baseName, plan.sourceFbxPath, plan.targetFbxPath, plan.modelIndex, files);
                        ApplyAdvancedMeshPreview(customBase, baseName, externalFbxPath, plan.targetFbxPath, plan.previewMappings);
                        AssignCreatorExternalCustomFbx(customBase, plan.targetFbxPath, externalFbxPath);
                        updatedTargets.Add(externalFbxPath);
                        advancedCount++;
                        MCBLogger.Log($"[BlenderExport] Applied native mesh preview and stored the export for submission. source={plan.sourceFbxPath} external={externalFbxPath} target={plan.targetFbxPath}");
                    }
                    else
                    {
                        string exportedUnityPath = CopyModelToExports(baseName, plan.sourceFbxPath, plan.targetFbxPath, plan.modelIndex, files);
                        AssetDatabase.ImportAsset(exportedUnityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        files.Track(AvatarDefinitionGenerationService.GetDefaultAvatarPath(exportedUnityPath));
                        Avatar generatedAvatar = GenerateAvatarForImportedFbx(exportedUnityPath, plan.targetFbxPath);
                        if (generatedAvatar != null) generatedAvatarCount++;
                        RefreshTargetMeshesFromFbx(customBase, plan.target, exportedUnityPath, plan.model.meshNames);
                        AssignCreatorCustomFbx(customBase, plan.targetFbxPath, exportedUnityPath, generatedAvatar);
                        ApplyGeneratedAvatarToPreview(customBase, exportedUnityPath, generatedAvatar);
                        updatedTargets.Add(exportedUnityPath);
                        MCBLogger.Log($"[BlenderExport] Imported Blender export. source={plan.sourceFbxPath} imported={exportedUnityPath} target={plan.targetFbxPath}");
                    }
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EditorUtility.SetDirty(customBase);
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch
            {
                // Runs inside one editor update, so the group holds only this import's changes.
                Undo.RevertAllDownToGroup(undoGroup);
                files.Restore();
                throw;
            }
        }

        string action = advancedCount > 0 ? "Updated the avatar with native meshes from" : "Updated the avatar from";
        string avatarMessage = generatedAvatarCount > 0
            ? $"\nGenerated {generatedAvatarCount} Avatar asset(s)."
            : advancedCount > 0 ? "\nThe original FBX files are unchanged; the exports are ready for submission." : "\nNo Avatar asset was generated.";
        SetStatus($"Blender export imported for {customBase.name}.\n{action} {updatedTargets.Count} FBX file(s).{avatarMessage}", MessageType.Info);
        LogMuscleCorrectives(manifest.xmuscle);
        // Without XMuscle Orbit Helper in Blender there is no xmuscle block, and another API's block is not read: the stored
        // correctives stay as they are.
        if (manifest.xmuscle != null && manifest.xmuscle.apiVersion == MuscleCorrectiveSet.ApiVersion)
        {
            var exportedMeshes = models
                .Where(model => model.shapeKeysByMesh != null)
                .SelectMany(model => model.shapeKeysByMesh.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            MuscleCorrectiveStore.Apply(customBase, manifest.xmuscle, exportedMeshes);
        }
        // What other tools made from these meshes (My Avatar's Substance Painter project) can be made again.
        Orbiters.Toolkit.Editor.Meshes.AvatarMeshEdits.NotifyApplied(MCBReFitIntegration.Root(customBase));
    }

    /// <summary>Reads a Blender export manifest; throws unless it is an export of <see cref="ProtocolVersion"/>.</summary>
    internal static BlenderExportManifest ReadExportManifest(string manifestPath)
    {
        var manifest = JsonConvert.DeserializeObject<BlenderExportManifest>(File.ReadAllText(manifestPath));
        if (manifest == null || manifest.kind != ExportKind) throw new InvalidOperationException("manifest.json is not a Blender MCB export.");
        if (manifest.protocolVersion != ProtocolVersion) throw new InvalidOperationException(DescribeProtocolMismatch(manifest.protocolVersion));
        return manifest;
    }

    internal static string DescribeProtocolMismatch(int blenderProtocolVersion)
    {
        string outdated = blenderProtocolVersion > ProtocolVersion ? "the MCB Unity package" : "the Blender side that wrote the export";
        return $"The export uses MCB export protocol {blenderProtocolVersion} and this MCB reads protocol {ProtocolVersion}: update {outdated}.";
    }

    private static void LogMuscleCorrectives(MuscleCorrectiveSet set)
    {
        if (set == null) return;
        if (set.apiVersion != MuscleCorrectiveSet.ApiVersion)
        {
            MCBLogger.LogWarning($"[BlenderExport] XMuscles: the export comes from XMuscle Orbit Helper API {set.apiVersion}, MCB reads API " +
                                 $"{MuscleCorrectiveSet.ApiVersion}: update XMuscle Orbit Helper to {XMuscleOrbitHelperVersion} in Blender and export again.");
            return;
        }
        MCBLogger.Log($"[BlenderExport] The export carries {set.muscles?.Count ?? 0} XMuscle corrective(s) (XMuscle Orbit Helper API {set.apiVersion}).");
        foreach (string warning in set.warnings ?? new List<string>()) MCBLogger.LogWarning("[BlenderExport] XMuscles: " + warning);
    }

    private sealed class ModelUpdatePlan
    {
        public ModelInfo model;
        public int modelIndex;
        public string sourceFbxPath;
        public ExportTarget target;
        public string targetFbxPath => target.UnityPath;
        public bool advancedMesh;
        public List<ModelFileSmrPathData> previewMappings;
    }

    private static List<ModelUpdatePlan> PlanModelUpdates(MyCustomBase customBase, List<ExportTarget> targets, BlenderExportManifest manifest, List<ModelInfo> models, string exportDir)
    {
        var plans = new List<ModelUpdatePlan>();
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            var model = models[modelIndex];
            if (string.IsNullOrWhiteSpace(model.path)) continue;
            string sourceFbxPath = Path.GetFullPath(Path.Combine(exportDir, model.path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(sourceFbxPath)) throw new FileNotFoundException("Blender exported FBX was not found.", sourceFbxPath);
            var target = ResolveTarget(targets, manifest, model);
            var plan = new ModelUpdatePlan
            {
                model = model,
                modelIndex = modelIndex,
                sourceFbxPath = sourceFbxPath,
                target = target,
                advancedMesh = ShouldUseAdvancedMesh(manifest, model),
            };
            if (plan.advancedMesh) plan.previewMappings = PreviewMappings(target, model.meshNames);
            GetCreatorTargetIndex(customBase, target.UnityPath);
            plans.Add(plan);
        }
        return plans;
    }

    private static ExportTarget ResolveTarget(List<ExportTarget> targets, BlenderExportManifest manifest, ModelInfo model)
    {
        string requested = !string.IsNullOrWhiteSpace(model?.targetFbxPath) ? model.targetFbxPath : manifest?.target?.targetFbxPath;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string unityPath = MCBUtils.ToUnityPath(requested);
            var match = targets.FirstOrDefault(t => string.Equals(MCBUtils.ToUnityPath(t.UnityPath), unityPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            if (targets.Count > 1) throw new InvalidOperationException($"The export targets '{requested}', which is not one of this base's target FBX files.");
        }
        return targets[0];
    }

    private static bool ShouldUseAdvancedMesh(BlenderExportManifest manifest, ModelInfo model)
    {
        if (!FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK)) return false;
        if (manifest?.advancedMeshBlenderLink == true) return true;
        return model != null &&
               (string.Equals(model.unityImportMode, "nativeMeshPayload", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.transportFormat, NativeMeshPayloadService.PayloadFormat, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ModelFileSmrPathData> PreviewMappings(ExportTarget target, IEnumerable<string> meshNames)
    {
        var names = new HashSet<string>(meshNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var mappings = target.SmrPaths
            .Where(entry => entry != null && (names.Count == 0 || names.Contains(entry.meshName) || names.Contains(entry.rendererName)))
            .ToList();
        if (mappings.Count == 0)
            throw new InvalidOperationException($"No avatar renderer shows the meshes the export has for '{target.UnityPath}'. Select the intended custom base and import again.");
        return mappings;
    }

    // The export read straight from Blender's FBX, with the avatar's dynamic normals baked in (as a version's payload has them).
    private static void ApplyAdvancedMeshPreview(MyCustomBase customBase, string baseName, string externalFbxPath, string targetFbxPath, List<ModelFileSmrPathData> mappings)
    {
        NativeMeshPayloadService.ApplyBlenderExportPreview(customBase.transform.root, externalFbxPath,
            MCBUtils.ToUnityPath(targetFbxPath), mappings,
            MCBUtils.CombineUnityPath(MCBUtils.ASSETS_BASE_FOLDER, "generated", "blenderPreviews", baseName),
            customBase.useDynamicNormals ? DynamicNormalsService.Selections(customBase) : null,
            renderer => DynamicNormalsService.LegSeparation(customBase, renderer));
    }

    // Snapshots the project files an import overwrites or creates, so a failed import can put them back.
    private sealed class ExportFileRollback : IDisposable
    {
        private readonly string folder = Path.Combine(GetProjectRoot(), "Library", "MCB", "BlenderExportRollback", Guid.NewGuid().ToString("N"));
        private readonly List<KeyValuePair<string, string>> backups = new List<KeyValuePair<string, string>>();
        private bool restoreFailed;

        public void Track(string path)
        {
            string absolutePath = UnityPathToAbsolute(path);
            foreach (string file in new[] { absolutePath, absolutePath + ".meta" })
            {
                if (backups.Any(entry => string.Equals(entry.Key, file, StringComparison.OrdinalIgnoreCase))) continue;
                string backup = null;
                if (File.Exists(file))
                {
                    Directory.CreateDirectory(folder);
                    backup = Path.Combine(folder, backups.Count.ToString());
                    File.Copy(file, backup);
                }
                backups.Add(new KeyValuePair<string, string>(file, backup));
            }
        }

        public void Restore()
        {
            var reimport = new List<string>();
            foreach (var entry in backups)
            {
                string unityPath = MCBUtils.ToUnityPath(entry.Key);
                bool isAsset = !entry.Key.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(unityPath));
                try
                {
                    if (entry.Value != null)
                    {
                        File.Copy(entry.Value, entry.Key, true);
                        if (isAsset) reimport.Add(unityPath);
                    }
                    else if (isAsset) AssetDatabase.DeleteAsset(unityPath);
                    else if (File.Exists(entry.Key)) File.Delete(entry.Key);
                }
                catch (Exception ex)
                {
                    restoreFailed = true;
                    MCBLogger.LogError($"[BlenderExport] Could not restore '{entry.Key}' after a failed import: {ex.Message}");
                }
            }
            foreach (string unityPath in reimport)
            {
                try { AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); }
                catch { restoreFailed = true; throw; }
            }
        }

        public void Dispose()
        {
            if (restoreFailed)
            {
                MCBLogger.LogError($"[BlenderExport] Some files could not be restored. Recovery copies were kept at '{folder}'.");
                return;
            }
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) { MCBLogger.LogWarning($"[BlenderExport] Could not delete the import backups '{folder}': {ex.Message}"); }
        }
    }

    private static string CopyModelToExports(string baseName, string sourceFbxPath, string targetFbxPath, int modelIndex, ExportFileRollback files)
    {
        string folderUnityPath = MCBUtils.CombineUnityPath(ExportsFolder, baseName);
        string folderAbsolutePath = UnityPathToAbsolute(folderUnityPath);
        Directory.CreateDirectory(folderAbsolutePath);
        string fileName = $"{modelIndex + 1:00}_{SanitizeFileName(Path.GetFileNameWithoutExtension(targetFbxPath))}.fbx";
        string destination = Path.Combine(folderAbsolutePath, fileName);
        files.Track(destination);
        File.Copy(sourceFbxPath, destination, true);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        return MCBUtils.CombineUnityPath(folderUnityPath, fileName);
    }

    private static string CopyModelToAdvancedExports(string baseName, string sourceFbxPath, string targetFbxPath, int modelIndex, ExportFileRollback files)
    {
        if (string.IsNullOrWhiteSpace(sourceFbxPath) || !File.Exists(sourceFbxPath)) throw new FileNotFoundException("Blender exported FBX was not found.", sourceFbxPath);
        string folder = Path.Combine(UnityPathToAbsolute(AdvancedExportsFolder), baseName);
        Directory.CreateDirectory(folder);
        string fileName = $"{modelIndex + 1:00}_{SanitizeFileName(Path.GetFileNameWithoutExtension(targetFbxPath))}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.fbx";
        string destination = Path.Combine(folder, fileName);
        files.Track(destination);
        File.Copy(sourceFbxPath, destination, true);
        return Path.GetFullPath(destination);
    }

    private static void RefreshTargetMeshesFromFbx(MyCustomBase customBase, ExportTarget target, string sourceFbxPath, IEnumerable<string> meshNames)
    {
        if (customBase == null || string.IsNullOrWhiteSpace(sourceFbxPath)) return;
        SmrPathService.RefreshTargetMeshesFromFbx(customBase.transform.root, MCBUtils.ToUnityPath(sourceFbxPath), target?.SmrPaths, meshNamesToRefresh: meshNames);
    }

    private static Avatar GenerateAvatarForImportedFbx(string customFbxUnityPath, string sourceMappingFbxPath)
    {
        try
        {
            var result = AvatarDefinitionGenerationService.GenerateAvatarAsset(customFbxUnityPath, sourceMappingFbxPath, outputPath: null,
                applyGeneratedAvatarToFbx: false, keepImporterConfiguredForEditing: true);
            if (result?.avatar != null)
            {
                MCBLogger.Log($"[BlenderExport] Generated Avatar '{result.avatarPath}' for '{customFbxUnityPath}'. {result.message}");
                return result.avatar;
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[BlenderExport] Could not generate an Avatar for '{customFbxUnityPath}': {ex.Message}");
        }
        return null;
    }

    private static void ApplyGeneratedAvatarToPreview(MyCustomBase customBase, string customFbxUnityPath, Avatar generatedAvatar)
    {
        if (customBase == null || generatedAvatar == null || string.IsNullOrWhiteSpace(customFbxUnityPath)) return;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(customFbxUnityPath)) == null) return;
        AvatarDefinitionGenerationService.SetRootAnimatorAvatar(customBase.transform.root, generatedAvatar);
    }

    private static SerializedProperty CreatorEntry(SerializedObject serialized, int targetIndex)
    {
        var entriesProp = serialized.FindProperty("modelFileBuildEntries");
        if (entriesProp == null) return null;
        while (entriesProp.arraySize <= targetIndex)
        {
            entriesProp.InsertArrayElementAtIndex(entriesProp.arraySize);
            var newEntry = entriesProp.GetArrayElementAtIndex(entriesProp.arraySize - 1);
            newEntry.FindPropertyRelative("customFbx").objectReferenceValue = null;
            var newExternalPath = newEntry.FindPropertyRelative("externalCustomFbxPath");
            if (newExternalPath != null) newExternalPath.stringValue = "";
            newEntry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        }
        return entriesProp.GetArrayElementAtIndex(targetIndex);
    }

    private static void AssignCreatorCustomFbx(MyCustomBase customBase, string targetFbxPath, string customFbxUnityPath, Avatar customBaseAvatar)
    {
        if (customBase == null || string.IsNullOrWhiteSpace(customFbxUnityPath)) return;
        var customFbx = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(customFbxUnityPath));
        if (customFbx == null) return;
        int targetIndex = GetCreatorTargetIndex(customBase, targetFbxPath);
        var serialized = new SerializedObject(customBase);
        var entry = CreatorEntry(serialized, targetIndex);
        if (entry == null) return;
        entry.FindPropertyRelative("customFbx").objectReferenceValue = customFbx;
        var externalPath = entry.FindPropertyRelative("externalCustomFbxPath");
        if (externalPath != null) externalPath.stringValue = "";
        if (customBaseAvatar != null) entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = customBaseAvatar;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(customBase);
    }

    private static void AssignCreatorExternalCustomFbx(MyCustomBase customBase, string targetFbxPath, string externalFbxPath)
    {
        if (customBase == null || string.IsNullOrWhiteSpace(externalFbxPath)) return;
        int targetIndex = GetCreatorTargetIndex(customBase, targetFbxPath);
        var serialized = new SerializedObject(customBase);
        var entry = CreatorEntry(serialized, targetIndex);
        if (entry == null) return;
        entry.FindPropertyRelative("customFbx").objectReferenceValue = null;
        var externalPath = entry.FindPropertyRelative("externalCustomFbxPath");
        if (externalPath != null) externalPath.stringValue = Path.GetFullPath(externalFbxPath);
        entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        var advancedReplacementProp = serialized.FindProperty("useAdvancedMeshReplacementForCreator");
        if (advancedReplacementProp != null && FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR)) advancedReplacementProp.boolValue = true;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(customBase);
    }

    /// <summary>The index of a target FBX among the creator's source files; throws when it is not one of them.</summary>
    internal static int GetCreatorTargetIndex(MyCustomBase customBase, string targetFbxPath)
    {
        int index = customBase.baseFbxFiles.FindIndex(source => source != null &&
            string.Equals(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(source)), MCBUtils.ToUnityPath(targetFbxPath), StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"The Blender target '{targetFbxPath}' is not in this creator's source files. Select the intended custom base before importing.");
        return index;
    }

    private static string BaseFolderName(MCBEditor editor)
    {
        string name = editor.GetSelectedAssetDisplayName();
        if (string.IsNullOrWhiteSpace(name)) name = editor.customBaseTarget != null ? editor.customBaseTarget.name : null;
        return SanitizeFileName(name);
    }

    /// <summary>A file or folder name from any text: invalid characters become underscores, empty becomes "mcb".</summary>
    public static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "mcb";
        string cleaned = value;
        foreach (char c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        cleaned = cleaned.Replace("/", "_").Replace("\\", "_").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "mcb" : cleaned;
    }

    internal static void SetStatus(string message, MessageType type)
    {
        LastStatus = message;
        LastStatusType = type;
        MCBLogger.Log("[BlenderExport] " + message);
        Changed?.Invoke();
    }

    private static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static string UnityPathToAbsolute(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath)) return unityPath;
        if (Path.IsPathRooted(unityPath)) return Path.GetFullPath(unityPath);
        return Path.GetFullPath(Path.Combine(GetProjectRoot(), unityPath));
    }
}
#endif
