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

// The avatar's model files during a switch: XOR/HDiff patches, original backups, which files a version replaces and their importers.
public partial class VersionActions
{
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
                string actualOutputHash = MCBHashing.HashFile(tempOutputPath);
                ContentHash.Remember(tempOutputPath, actualOutputHash);
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
                string actualOutputHash = MCBHashing.HashFile(tempOutputPath);
                ContentHash.Remember(tempOutputPath, actualOutputHash);
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

    /// <summary>
    /// Before an original-base backup is used: a base package re-imported while a version was applied replaces the stale
    /// backup (<see cref="FileManagerService.RefreshStaleOriginalBackup"/>).
    /// </summary>
    private void RefreshOriginalBackup(string fbxPath, params CustomBaseVersion[] versions)
    {
        if (string.IsNullOrWhiteSpace(fbxPath)) return;
        var known = versions.Append(session?.customBaseTarget != null ? session.customBaseTarget.appliedCustomBaseVersion : null)
            .Concat(session?.GetAllVersions() ?? Enumerable.Empty<CustomBaseVersion>())
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
            string currentHash = ContentHash.Proof(fbxPath);
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

        string originalHash = ContentHash.Proof(originalFbxPath);
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

        string actualHash = ContentHash.Proof(originalFbxPath);
        if (!string.Equals(actualHash, sourceFile.hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"HDiff patch base FBX hash mismatch for '{sourceFile.path}'. Expected {sourceFile.hash}, got {actualHash ?? "<missing>"}.");
        }
    }

    private void ApplyXorBinToUnityAsset(CustomBaseVersion version, ModelFileData patchFile)
    {
        if (session?.customBaseTarget == null)
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
        NativeMeshPayloadService.ApplyEncryptedPayload(AvatarPaths.Root(session.customBaseTarget), version, patchFile,
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
            session?.customBaseTarget,
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
        if (session?.customBaseTarget == null || !NativeMeshPayloadService.VersionUsesAdvancedMesh(version))
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

        Transform root = AvatarPaths.Root(session.customBaseTarget);
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
        var root = session?.customBaseTarget != null ? AvatarPaths.Root(session.customBaseTarget) : null;
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
        session.warningsModule?.AddWarning(message, MessageType.Warning, "Renderers skipped");
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

        int selectedAssetId = session.GetSelectedAsset()?.id ?? 0;
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

        var applied = session.customBaseTarget != null ? session.customBaseTarget.appliedCustomBaseVersion : null;
        if (applied != null && applied != resetFromVersion && applied != VersionListDrawer.RESET_VERSION)
        {
            yield return applied;
        }

        if (session.selectedVersionForAction != null &&
            session.selectedVersionForAction != resetFromVersion &&
            session.selectedVersionForAction != VersionListDrawer.RESET_VERSION)
        {
            yield return session.selectedVersionForAction;
        }

        if (session.recommendedVersion != null &&
            session.recommendedVersion != resetFromVersion &&
            session.recommendedVersion != VersionListDrawer.RESET_VERSION)
        {
            yield return session.recommendedVersion;
        }

        foreach (var version in session.GetAllVersions() ?? new List<CustomBaseVersion>())
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
                session.customBaseTarget, preserveVersion, missingRenderers);
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
            var smrPaths = SmrPathService.ResolveSmrPathsForSource(version, fbxPath, session?.customBaseTarget);
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
}
#endif
