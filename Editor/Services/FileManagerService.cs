#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEditor;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

public class FileManagerService
{
    public static void SetCreatorSourceFiles(MyCustomBase target, IList<GameObject> sourceFiles)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (sourceFiles == null) throw new ArgumentNullException(nameof(sourceFiles));
        var previous = new Dictionary<GameObject, CreatorModelFileBuildEntry>();
        for (int i = 0; i < target.baseFbxFiles.Count; i++)
        {
            var source = target.baseFbxFiles[i];
            if (source != null && i < target.modelFileBuildEntries.Count)
                previous[source] = target.modelFileBuildEntries[i];
        }
        Undo.RecordObject(target, "Update creator source files");
        target.baseFbxFiles = sourceFiles.ToList();
        target.modelFileBuildEntries = sourceFiles.Select(source =>
            source != null && previous.TryGetValue(source, out var entry) && entry != null
                ? entry : new CreatorModelFileBuildEntry()).ToList();
        EditorUtility.SetDirty(target);
    }

    public const string OriginalBaseSuffix = ".originalbase";
    public const string OriginalSuffix = OriginalBaseSuffix;
    public const string PreMcbBackupPrefix = ".backup";

    public class ModelFilePackageEntry
    {
        public string sourceFbxPath;
        public string referenceSourcePath;
        public string localTargetPath;
        public AvatarPathOverrideEntry pathOverride;
        public GameObject customFbx;
        public string externalCustomFbxPath;
        public Avatar customBaseAvatar;
        public bool useAdvancedMeshReplacement;
        public bool useHdiffFbxDelta;
        public bool usedHdiffFbxDelta;
        public List<ModelFileSmrPathData> smrPaths;
        public string binUnityPath;
        public string avatarUnityPath;
        public string sourceHash;
        public string binHash;
        public string avatarHash;
        public string outputHash;
        public string patchTransform;
        public string payloadCompression;
        public List<MCBPayloadVariant> payloadVariants;
        public List<NativeMeshPayloadService.NativeMeshPayloadBuildResult> payloadParts;
        public int advancedRendererCount;
        // False writes a plain payload shared by every original (unprotected versions).
        public bool encryptPayload = true;
        // Original bone names the payload binds to, and each payload renderer's material slot names.
        public List<string> skeletonBones;
        public List<RendererSlotNames> rendererSlots;
        public HdiffService.BuildInfo hdiffBuildInfo;
        public string hdiffFallbackReason;
    }

    public string CalculateFileHash(string path)
    {
        if (!File.Exists(path)) return null;
        using (var sha256 = MCBHashing.CreateSha256())
        using (var stream = File.OpenRead(path))
        {
            byte[] hash = sha256.ComputeHash(stream);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    public void CreateBackup(string fbxPath)
    {
        if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath)) return;
        string backupPath = GetOriginalBasePath(fbxPath);
        if (File.Exists(backupPath)) return;
        File.Copy(fbxPath, backupPath);
    }
    
    public bool BackupExists(string fbxPath)
    {
        return !string.IsNullOrEmpty(fbxPath) && File.Exists(GetOriginalBasePath(fbxPath));
    }

    public bool FbxMatchesBackupAtPath(string unityFbxPath)
    {
        if (string.IsNullOrEmpty(unityFbxPath))
        {
            return false;
        }

        string unityPath = MCBUtils.ToUnityPath(unityFbxPath);
        string fullFbxPath = Path.GetFullPath(unityPath);
        string fullBackupPath = GetOriginalBasePath(fullFbxPath);
        return File.Exists(fullFbxPath) &&
               File.Exists(fullBackupPath) &&
               FilesAreEqual(fullFbxPath, fullBackupPath);
    }

    public bool ReplaceFbxWithCustomCopy(string targetFbxPath, string customFbxPath)
    {
        if (string.IsNullOrWhiteSpace(targetFbxPath)) throw new ArgumentNullException(nameof(targetFbxPath));
        if (string.IsNullOrWhiteSpace(customFbxPath)) throw new ArgumentNullException(nameof(customFbxPath));

        string targetUnityPath = MCBUtils.ToUnityPath(targetFbxPath);
        string customUnityPath = MCBUtils.ToUnityPath(customFbxPath);
        string targetFullPath = Path.GetFullPath(targetUnityPath);
        string customFullPath = Path.GetFullPath(customUnityPath);

        if (!File.Exists(targetFullPath))
            throw new FileNotFoundException("Target FBX file was not found.", targetFullPath);
        if (!File.Exists(customFullPath))
            throw new FileNotFoundException("Custom FBX file was not found.", customFullPath);

        if (string.Equals(targetFullPath, customFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string backupPath = GetOriginalBasePath(targetFullPath);
        if (!File.Exists(backupPath))
        {
            File.Copy(targetFullPath, backupPath);
            MCBLogger.Log($"[FileManager] Created original FBX backup: {backupPath}");
        }

        if (FilesAreEqual(targetFullPath, customFullPath))
        {
            return false;
        }

        string pendingPath = targetFullPath + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(customFullPath, pendingPath, false);
            if (!FilesAreEqual(customFullPath, pendingPath))
            {
                throw new InvalidDataException("The staged FBX replacement failed integrity verification.");
            }

            File.Replace(pendingPath, targetFullPath, null);
            return true;
        }
        finally
        {
            if (File.Exists(pendingPath)) File.Delete(pendingPath);
        }
    }

    private static bool FilesAreEqual(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        const int bufferSize = 1024 * 1024;
        byte[] firstBuffer = new byte[bufferSize];
        byte[] secondBuffer = new byte[bufferSize];
        using (var first = File.OpenRead(firstPath))
        using (var second = File.OpenRead(secondPath))
        {
            while (true)
            {
                int firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
                int secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);
                if (firstRead != secondRead)
                {
                    return false;
                }

                if (firstRead == 0)
                {
                    return true;
                }

                for (int i = 0; i < firstRead; i++)
                {
                    if (firstBuffer[i] != secondBuffer[i])
                    {
                        return false;
                    }
                }
            }
        }
    }

    public void RestoreBackup(string fbxPath)
    {
        string backupPath = GetOriginalBasePath(fbxPath);
        if (!File.Exists(backupPath)) return;
        File.Copy(backupPath, fbxPath, true);
    }

    // Force-restore a specific FBX regardless of current selection/state.
    // The .originalbase file is the immutable default-base source and must remain in place.
    public void ForceRestoreBackupAtPath(string unityFbxPath)
    {
        if (string.IsNullOrEmpty(unityFbxPath)) throw new ArgumentNullException(nameof(unityFbxPath));
        string unityPath = MCBUtils.ToUnityPath(unityFbxPath);
        string fullFbxPath = Path.GetFullPath(unityPath);
        string fullBackupPath = GetOriginalBasePath(fullFbxPath);

        if (!File.Exists(fullBackupPath))
        {
            throw new FileNotFoundException($"Backup FBX not found: {fullBackupPath}");
        }

        if (FbxMatchesBackupAtPath(unityPath))
        {
            MCBLogger.Log($"[FileManager] Skipped original FBX restore; target already matches backup: {unityPath}");
            return;
        }

        File.Copy(fullBackupPath, fullFbxPath, true);
        MCBLogger.Log($"[FileManager] Restored original FBX backup and reimporting: {unityPath}");

        // Force Unity to reimport the restored FBX
        AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }

    public static string GetOriginalBasePath(string fbxPath)
    {
        if (string.IsNullOrEmpty(fbxPath)) return fbxPath;
        return fbxPath.EndsWith(OriginalBaseSuffix, StringComparison.OrdinalIgnoreCase)
            ? fbxPath
            : fbxPath + OriginalBaseSuffix;
    }

    public static string CreatePreMcbBackupToken(DateTime? timestampOverride = null)
    {
        DateTime timestamp = (timestampOverride ?? DateTime.Now).ToLocalTime();
        return timestamp.ToString("ddMMMyyyy-HHmmss", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    public static string GetPreMcbBackupPath(string fbxPath, string token)
    {
        if (string.IsNullOrWhiteSpace(fbxPath) || !IsSafeBackupToken(token)) return null;
        return fbxPath + PreMcbBackupPrefix + token;
    }

    public string CreatePreMcbBackup(string fbxPath, string preferredToken = null)
    {
        if (string.IsNullOrWhiteSpace(fbxPath) || !File.Exists(fbxPath)) return null;

        string tokenBase = string.IsNullOrWhiteSpace(preferredToken)
            ? CreatePreMcbBackupToken()
            : preferredToken.Trim();
        if (!IsSafeBackupToken(tokenBase))
        {
            throw new ArgumentException("Pre-MCB backup token contains unsupported characters.", nameof(preferredToken));
        }
        string token = tokenBase;
        string backupPath = GetPreMcbBackupPath(fbxPath, token);
        int attempt = 1;
        while (File.Exists(backupPath))
        {
            token = $"{tokenBase}-{attempt++}";
            backupPath = GetPreMcbBackupPath(fbxPath, token);
        }

        File.Copy(fbxPath, backupPath);
        return token;
    }

    public bool EnsureOriginalBaseKey(string targetUnityPath, string sourceFilePath, string expectedHash)
    {
        if (!MCBUtils.TryResolveProjectAssetPath(targetUnityPath, out _, out string targetFullPath))
        {
            throw new ArgumentException($"Target FBX path is outside the Unity project: {targetUnityPath}", nameof(targetUnityPath));
        }
        if (!File.Exists(targetFullPath))
        {
            throw new FileNotFoundException("Target FBX file was not found.", targetFullPath);
        }

        string sourceFullPath = Path.GetFullPath(sourceFilePath ?? string.Empty);
        if (!File.Exists(sourceFullPath))
        {
            throw new FileNotFoundException("Original/default source FBX was not found.", sourceFullPath);
        }

        string normalizedExpectedHash = expectedHash?.Trim().ToLowerInvariant();
        string sourceHash = CalculateFileHash(sourceFullPath);
        if (string.IsNullOrWhiteSpace(normalizedExpectedHash) ||
            normalizedExpectedHash.Length != 64 ||
            !normalizedExpectedHash.All(Uri.IsHexDigit) ||
            !string.Equals(sourceHash, normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected original/default FBX changed after it was mapped.");
        }

        string originalBaseFullPath = GetOriginalBasePath(targetFullPath);
        if (File.Exists(originalBaseFullPath))
        {
            string existingHash = CalculateFileHash(originalBaseFullPath);
            if (!string.Equals(existingHash, normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Existing original-base key has a different hash and was not overwritten: {GetOriginalBasePath(targetUnityPath)}");
            }
            return false;
        }

        string temporaryPath = originalBaseFullPath + ".pending-" + Guid.NewGuid().ToString("N");
        bool created = true;
        try
        {
            File.Copy(sourceFullPath, temporaryPath, false);
            if (!string.Equals(CalculateFileHash(temporaryPath), normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The staged original-base key failed hash verification.");
            }

            try
            {
                File.Move(temporaryPath, originalBaseFullPath);
            }
            catch (IOException) when (File.Exists(originalBaseFullPath))
            {
                string existingHash = CalculateFileHash(originalBaseFullPath);
                if (!string.Equals(existingHash, normalizedExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Original-base key was created concurrently with different content: {GetOriginalBasePath(targetUnityPath)}");
                }
                created = false;
            }
            return created;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void RestorePreMcbBackup(string targetUnityPath, string token)
    {
        if (!MCBUtils.TryResolveProjectAssetPath(targetUnityPath, out string normalizedTargetPath, out string targetFullPath))
        {
            throw new ArgumentException($"Target FBX path is outside the Unity project: {targetUnityPath}", nameof(targetUnityPath));
        }

        string backupFullPath = GetPreMcbBackupPath(targetFullPath, token);
        if (!File.Exists(backupFullPath))
        {
            throw new FileNotFoundException("Pre-MCB backup was not found.", backupFullPath);
        }

        File.Copy(backupFullPath, targetFullPath, true);
        AssetDatabase.ImportAsset(normalizedTargetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
    }

    private static bool IsSafeBackupToken(string token)
    {
        return !string.IsNullOrWhiteSpace(token) &&
               token.All(character => char.IsLetterOrDigit(character) || character == '-');
    }
    
    public void DeleteVersionFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Directory.Delete(path, true);
        if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
    }

    public byte[] XorTransform(byte[] baseData, byte[] keyData)
    {
        return MCBXor.Transform(baseData, keyData);
    }

    public void UnzipAndMove(string zipPath, string extractPath, string finalDestinationPath, Action<string> validateExtracted = null,
        VersionArchiveBudget budget = null)
    {
        // Both disk and RAM downloads use the same validated, transactional extraction path.
        using (var stream = File.OpenRead(zipPath))
            ExtractVersionArchive(stream, finalDestinationPath, null, validateExtracted, budget);
    }

    /// <returns>
    /// The captured entries' bytes. An entry that does not fit <see cref="VersionArchiveBudget.MaxCapturedBytes"/> is only
    /// written to disk, where callers read it instead.
    /// </returns>
    public Dictionary<string, byte[]> UnzipAndMoveFromMemory(
        byte[] zipBytes,
        string finalDestinationPath,
        ISet<string> captureRelativePaths = null, Action<string> validateExtracted = null, VersionArchiveBudget budget = null)
    {
        if (zipBytes == null || zipBytes.Length == 0)
        {
            throw new InvalidDataException("Version ZIP data is empty.");
        }

        using (var stream = new MemoryStream(zipBytes, false))
            return ExtractVersionArchive(stream, finalDestinationPath, captureRelativePaths, validateExtracted, budget);
    }

    // Validated, transactional extraction is Orbiters Toolkit's (SafeArchive), shared with My Avatar's gallery.
    private Dictionary<string, byte[]> ExtractVersionArchive(Stream zipStream, string finalDestinationPath,
        ISet<string> captureRelativePaths, Action<string> validateExtracted, VersionArchiveBudget budget) =>
        Orbiters.Toolkit.Editor.Storage.SafeArchive.Extract(zipStream, finalDestinationPath, captureRelativePaths, validateExtracted,
            budget ?? new VersionArchiveBudget());

    private void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    public void RemoveExistingLogic(Transform root)
    {
        if (root == null) return;
        // Remove any instances named "mcb logic" or "debug" anywhere under the avatar root (case-insensitive)
        var targets = root.GetComponentsInChildren<Transform>(true)
            .Where(t => t != null && (string.Equals(t.name, "mcb logic", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(t.name, "debug", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.gameObject)
            .Distinct()
            .ToList();
        foreach (var go in targets)
        {
            Undo.DestroyObjectImmediate(go);
        }
    }

    public void ApplyAvatarToModel(Transform root, GameObject fbx, string avatarPath)
    {
        if (fbx == null || string.IsNullOrEmpty(avatarPath)) return;

        string unityAvatarPath = MCBUtils.ToUnityPath(avatarPath);
        string absoluteAvatarPath = Path.GetFullPath(unityAvatarPath);
        if (!File.Exists(absoluteAvatarPath)) return;

        Avatar avatar = AssetDatabase.LoadAssetAtPath<Avatar>(unityAvatarPath);
        if (avatar == null)
        {
            MCBLogger.LogWarning($"[FileManager] Could not load Avatar at '{unityAvatarPath}'.");
            return;
        }

        AvatarDefinitionGenerationService.ApplyAvatarToFbxAndAnimator(fbx, avatar, root);
    }

    // Imports may compile scripts and span editor frames. Complete them before a version-switch Undo scope starts.
    public IEnumerator PrepareLogicPrefabCoroutine(string packagePath)
    {
        if (string.IsNullOrEmpty(packagePath)) yield break;
        string absolutePackagePath = Path.GetFullPath(MCBUtils.ToUnityPath(packagePath));
        string importPackagePath = File.Exists(absolutePackagePath) ? PrepareLogicPackageImport(absolutePackagePath) : null;
        if (importPackagePath == null) yield break;
        try
        {
            var import = Orbiters.Toolkit.Editor.UnityPackageImport.ImportAsync(importPackagePath);
            while (!import.IsCompleted) yield return null;
            import.GetAwaiter().GetResult();
            while (EditorApplication.isCompiling || EditorApplication.isUpdating) yield return null;
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
        finally
        {
            if (!string.Equals(importPackagePath, absolutePackagePath, StringComparison.OrdinalIgnoreCase) && File.Exists(importPackagePath))
                File.Delete(importPackagePath);
        }
    }

    public IEnumerator InstantiateLogicPrefabCoroutine(string packagePath, Transform parent)
    {
        var prepare = PrepareLogicPrefabCoroutine(packagePath);
        while (prepare.MoveNext()) yield return prepare.Current;
        InstantiatePreparedLogicPrefab(packagePath, parent);
    }

    public void InstantiatePreparedLogicPrefab(string packagePath, Transform parent)
    {
        if (string.IsNullOrEmpty(packagePath) || parent == null) return;
        string versionDataFolderUnity = MCBUtils.ToUnityPath(Path.GetDirectoryName(MCBUtils.ToUnityPath(packagePath)));
        string prefabPath = MCBUtils.GetLogicPrefabPath(versionDataFolderUnity);
        if (string.IsNullOrEmpty(prefabPath))
        {
            MCBLogger.LogWarning($"[FileManager] No logic prefab found in '{versionDataFolderUnity}'.");
            return;
        }

        string absolutePrefabPath = Path.GetFullPath(prefabPath);

        if (!File.Exists(absolutePrefabPath))
        {
            MCBLogger.LogWarning($"[FileManager] Expected prefab not found at '{absolutePrefabPath}'.");
            return;
        }

        GameObject logicPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (logicPrefab == null)
        {
            MCBLogger.Log($"[FileManager] Importing logic prefab at {prefabPath}");
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            logicPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            MCBLogger.Log("[FileManager] Logic prefab import completed.");
        }

        if (logicPrefab != null)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(logicPrefab, parent);
            instance.name = "mcb logic";
            Undo.RegisterCreatedObjectUndo(instance, "Install MCB Logic");
        }
        else
        {
            MCBLogger.LogWarning($"[FileManager] Could not load prefab at '{prefabPath}'.");
        }
    }

    /// <summary>
    /// Logic packages are exported with every prefab dependency, including copies of installed package files (VRCFury,
    /// VRChat SDK). Only the creator's assets under Assets/ are imported, so an old copy never overwrites a project package.
    /// </summary>
    internal static bool IsLogicPackageImportPath(string path) =>
        Orbiters.Toolkit.Editor.UnityPackageIndex.IsProjectPath(path) && path.StartsWith("Assets/", StringComparison.Ordinal);

    /// <summary>
    /// The package to import for a version's logic prefab: null when the project already has every creator asset it
    /// carries, a filtered temporary copy when it also carries entries outside Assets/.
    /// </summary>
    internal static string PrepareLogicPackageImport(string packagePath, IEnumerable<string> shippedPackages = null)
    {
        var index = Orbiters.Toolkit.Editor.UnityPackageIndex.Read(packagePath,
            maxExpandedBytes: VersionArchiveBudget.DefaultMaxTotalBytes, maxEntries: VersionArchiveBudget.DefaultMaxEntries);
        var wanted = LogicPackageImportEntries(packagePath, index, shippedPackages);
        if (wanted.Count == 0) return null;
        if (wanted.Count == index.Entries.Count) return packagePath;

        string filtered = Path.Combine(Path.GetTempPath(), "mcb-logic-" + Guid.NewGuid().ToString("N") + ".unitypackage");
        try
        {
            CopyUnityPackageEntries(packagePath, filtered, new HashSet<string>(wanted.Select(entry => entry.Guid), StringComparer.OrdinalIgnoreCase));
            return filtered;
        }
        catch
        {
            if (File.Exists(filtered)) File.Delete(filtered);
            throw;
        }
    }

    /// <summary>
    /// The entries of a logic package MCB imports: the creator's assets under Assets/ that the project does not have yet, and
    /// those the project has with different content when its copy is one MCB installed and nobody changed since.
    /// </summary>
    /// <remarks>
    /// Such dependencies live outside the version folder and can be shared with other versions and assets, or edited by the
    /// user. A project file equal to this package's copy is skipped. A different one is only replaced when it is byte for byte
    /// the copy a stored version shipped for that GUID (<paramref name="shippedPackages"/>, by default every logic package
    /// under <see cref="MCBUtils.ASSET_VERSIONS_FOLDER"/>): that is the version's update, not the user's work. Anything else
    /// (edited, or installed by other means) is kept and reported.
    /// </remarks>
    internal static List<Orbiters.Toolkit.Editor.UnityPackageIndex.Entry> LogicPackageImportEntries(string packagePath,
        Orbiters.Toolkit.Editor.UnityPackageIndex index, IEnumerable<string> shippedPackages = null)
    {
        var wanted = new List<Orbiters.Toolkit.Editor.UnityPackageIndex.Entry>();
        var installed = new List<(Orbiters.Toolkit.Editor.UnityPackageIndex.Entry entry, string path)>();
        foreach (var entry in index.Entries.Where(entry => IsLogicPackageImportPath(entry.Path)))
        {
            string projectPath = AssetDatabase.GUIDToAssetPath(entry.Guid);
            if (!ProjectHasAsset(projectPath)) wanted.Add(entry);
            // Only files of the project's own Assets/ are ever updated, never folders or installed packages.
            else if (projectPath.StartsWith("Assets/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(projectPath))
                installed.Add((entry, projectPath));
        }
        if (installed.Count == 0) return wanted;

        var packageHashes = UnityPackageAssetHashes(packagePath);
        var changed = new List<(Orbiters.Toolkit.Editor.UnityPackageIndex.Entry entry, string path, string hash)>();
        foreach (var (entry, projectPath) in installed)
        {
            // Records without content (folders) have nothing to update.
            if (!packageHashes.TryGetValue(entry.Guid, out string packageHash)) continue;
            string projectFile = Path.GetFullPath(projectPath);
            string projectHash = File.Exists(projectFile) ? MCBUtils.CalculateFileHash(projectFile) : null;
            if (projectHash != null && !string.Equals(projectHash, packageHash, StringComparison.OrdinalIgnoreCase))
                changed.Add((entry, projectPath, projectHash));
        }
        if (changed.Count == 0) return wanted;

        string self = Path.GetFullPath(packagePath);
        var shipped = (shippedPackages ?? StoredLogicPackages())
            .Where(path => !string.Equals(Path.GetFullPath(path), self, StringComparison.OrdinalIgnoreCase))
            .Select(TryUnityPackageAssetHashes)
            .ToList();
        foreach (var (entry, projectPath, projectHash) in changed)
        {
            if (shipped.Any(hashes => hashes.TryGetValue(entry.Guid, out string hash) && string.Equals(hash, projectHash, StringComparison.OrdinalIgnoreCase)))
                wanted.Add(entry);
            else
                MCBLogger.LogWarning($"[FileManager] Kept '{projectPath}': this version ships a different copy, but the project's copy is not one MCB installed, so it may hold your changes.");
        }
        return wanted;
    }

    private static bool ProjectHasAsset(string path) =>
        !string.IsNullOrEmpty(path) && (AssetDatabase.IsValidFolder(path) || AssetDatabase.GetMainAssetTypeAtPath(path) != null);

    // The logic package of every stored version.
    private static IEnumerable<string> StoredLogicPackages()
    {
        string root = Path.GetFullPath(MCBUtils.ASSET_VERSIONS_FOLDER);
        if (!Directory.Exists(root)) return Enumerable.Empty<string>();
        return Directory.GetDirectories(root)
            .Select(asset => Path.Combine(asset, "versions"))
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .SelectMany(version => Directory.GetFiles(version, "*.unitypackage", SearchOption.TopDirectoryOnly));
    }

    private static Dictionary<string, string> TryUnityPackageAssetHashes(string packagePath)
    {
        try { return UnityPackageAssetHashes(packagePath); }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
        {
            MCBLogger.LogWarning($"[FileManager] Could not read stored logic package '{packagePath}': {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>SHA-256 of each asset a Unity package carries, by GUID; cached while the package file is unchanged.</summary>
    internal static Dictionary<string, string> UnityPackageAssetHashes(string packagePath) =>
        Orbiters.Toolkit.Editor.UnityPackageFiles.AssetHashes(packagePath, VersionArchiveBudget.DefaultMaxTotalBytes);

    private static void CopyUnityPackageEntries(string source, string destination, ISet<string> guids) =>
        Orbiters.Toolkit.Editor.UnityPackageFiles.CopyEntries(source, destination, guids);

    public Dictionary<string, string> FindPrefabDependencies(GameObject prefab)
    {
        var dependencies = new Dictionary<string, string>();
        if (prefab == null) return dependencies;

        string[] dependencyPaths = AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true);
        
        foreach (string path in dependencyPaths)
        {
            if (path.EndsWith(".cs"))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;

                string packageDir = Path.GetDirectoryName(path);
                string packageJsonPath = null;
                while (!string.IsNullOrEmpty(packageDir))
                {
                    string potentialPath = Path.Combine(packageDir, "package.json");
                    if (File.Exists(potentialPath))
                    {
                        packageJsonPath = potentialPath;
                        break;
                    }
                    if (Path.GetFileName(packageDir) == "Packages" || Path.GetFileName(packageDir) == "Assets") break;
                    packageDir = Directory.GetParent(packageDir)?.FullName;
                }
                
                if (string.IsNullOrEmpty(packageJsonPath)) continue;

                try
                {
                    string json = File.ReadAllText(packageJsonPath);
                    var packageInfo = JsonUtility.FromJson<PackageJson>(json);
                    if (packageInfo != null && !dependencies.ContainsKey(packageInfo.name))
                    {
                        dependencies.Add(packageInfo.name, $"^{packageInfo.version}");
                    }
                }
                catch (Exception ex)
                {
                    MCBLogger.LogWarning($"Failed to parse {packageJsonPath}: {ex.Message}");
                }
            }
        }
        return dependencies;
    }
    
    [Serializable]
    private class PackageJson { public string name; public string version; }

    public void ExportOfflineVersionPackage(CustomBaseVersion version, string outputUnityPackagePath)
    {
        if (version == null) throw new ArgumentNullException(nameof(version));
        if (string.IsNullOrWhiteSpace(outputUnityPackagePath)) throw new ArgumentNullException(nameof(outputUnityPackagePath));

        string versionFolderUnityPath = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrWhiteSpace(versionFolderUnityPath))
            throw new InvalidOperationException("Version folder path could not be resolved.");

        string versionFolderFullPath = Path.GetFullPath(versionFolderUnityPath);
        if (!Directory.Exists(versionFolderFullPath))
            throw new DirectoryNotFoundException($"Version folder not found: {versionFolderFullPath}");

        string versionJsonUnityPath = MCBUtils.CombineUnityPath(versionFolderUnityPath, "version.json");
        string versionJsonFullPath = Path.GetFullPath(versionJsonUnityPath);
        bool versionJsonExisted = File.Exists(versionJsonFullPath);
        string metadataJson = JsonConvert.SerializeObject(version, Formatting.Indented, new StringEnumConverter());

        try
        {
            MCBUtils.EnsureDirectoryExists(versionJsonUnityPath);
            File.WriteAllText(versionJsonFullPath, metadataJson);
            AssetDatabase.ImportAsset(versionJsonUnityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            AssetDatabase.ExportPackage(
                new[] { versionFolderUnityPath },
                outputUnityPackagePath,
                ExportPackageOptions.Recurse);
        }
        finally
        {
            if (!versionJsonExisted)
            {
                AssetDatabase.DeleteAsset(versionJsonUnityPath);
            }
            else
            {
                AssetDatabase.ImportAsset(versionJsonUnityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
        }
    }
    
    /// <summary>
    /// Populates an (already created) version folder with every build output: default
    /// avatar, XOR / native-mesh-payload .bin patches, generated avatar assets, logic
    /// prefab + nested unitypackage, veins texture. The caller owns the folder
    /// lifecycle — builds run against a VersionRepository staging folder which is
    /// atomically committed afterwards; this method never deletes or replaces existing
    /// version folders and no longer produces the upload zip (publish re-creates the
    /// zip from the manifest-listed outputs).
    /// </summary>
    public void PopulateVersionFolder(
        string versionFolderUnityPath,
        IList<ModelFilePackageEntry> modelEntries,
        GameObject logicPrefab,
        bool includeCustomVeins,
        Texture2D customVeinsTexture,
        bool includeDynamicNormalsBody,
        bool includeDynamicNormalsFlexing,
        IEnumerable<string> additionalAnimationAssetPaths = null,
        IReadOnlyList<MeshBlendshapeSelection> dynamicNormalSelections = null)
    { MCBWork.Drain(PopulateVersionFolderCoroutine(versionFolderUnityPath, modelEntries, logicPrefab, includeCustomVeins, customVeinsTexture, includeDynamicNormalsBody, includeDynamicNormalsFlexing, additionalAnimationAssetPaths, dynamicNormalSelections)); }

    public System.Collections.IEnumerator PopulateVersionFolderCoroutine(
        string versionFolderUnityPath,
        IList<ModelFilePackageEntry> modelEntries,
        GameObject logicPrefab,
        bool includeCustomVeins,
        Texture2D customVeinsTexture,
        bool includeDynamicNormalsBody,
        bool includeDynamicNormalsFlexing,
        IEnumerable<string> additionalAnimationAssetPaths = null,
        IReadOnlyList<MeshBlendshapeSelection> dynamicNormalSelections = null)
    {
        string newVersionDataPath = versionFolderUnityPath;
        if (string.IsNullOrEmpty(newVersionDataPath))
        {
            throw new ArgumentException("A valid version folder path is required to create a version package.");
        }

        {
            MCBUtils.EnsureDirectoryExists(newVersionDataPath, canBeFilePath: false);

            var entries = modelEntries ?? Array.Empty<ModelFilePackageEntry>();
            // A reset definition belongs to the original model, never to a package-wide avatar.
            var defaultAvatar = entries.Where(entry => !string.IsNullOrWhiteSpace(entry?.sourceFbxPath))
                .Select(entry => AssetDatabase.LoadAssetAtPath<GameObject>(entry.sourceFbxPath)?.GetComponent<Animator>()?.avatar)
                .FirstOrDefault(avatar => avatar != null && avatar.isHuman);
            if (defaultAvatar != null)
                AvatarDefinitionGenerationService.SaveAvatarCopy(defaultAvatar,
                    MCBUtils.CombineUnityPath(newVersionDataPath, MCBUtils.DEFAULT_AVATAR_NAME));

            for (int i = 0; i < entries.Count; i++)
            {
                yield return null;
                var entry = entries[i];
                if (entry == null || (entry.customFbx == null && string.IsNullOrWhiteSpace(entry.externalCustomFbxPath) && entry.customBaseAvatar == null))
                    continue; // No supplied changes: retain the original model.
                bool hasExternalCustomFbx = entry != null &&
                                            !string.IsNullOrWhiteSpace(entry.externalCustomFbxPath) &&
                                            File.Exists(Path.GetFullPath(entry.externalCustomFbxPath));
                if (entry == null || string.IsNullOrWhiteSpace(entry.sourceFbxPath) || (entry.customFbx == null && !hasExternalCustomFbx && entry.customBaseAvatar == null))
                    throw new InvalidOperationException($"Target model file entry {i + 1} is incomplete.");

                string customFbxPath = entry.customFbx != null
                    ? AssetDatabase.GetAssetPath(entry.customFbx)
                    : (hasExternalCustomFbx ? Path.GetFullPath(entry.externalCustomFbxPath) : null);
                string customAvatarSourcePath = entry.customBaseAvatar != null ? AssetDatabase.GetAssetPath(entry.customBaseAvatar) : null;
                if (entry.customFbx != null && string.IsNullOrWhiteSpace(customFbxPath))
                    throw new InvalidOperationException($"Target model file entry {i + 1} has invalid asset references.");
                if (entry.customBaseAvatar != null && string.IsNullOrWhiteSpace(customAvatarSourcePath))
                    throw new InvalidOperationException($"Target model file entry {i + 1} has an invalid avatar asset reference.");

                string safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(entry.sourceFbxPath));
                entry.sourceHash = CalculateFileHash(entry.sourceFbxPath);

                GameObject importedExternalFbx = null;
                string importedExternalFbxPath = null;
                try
                {
                    if ((entry.customFbx != null || hasExternalCustomFbx))
                    {
                        string suffix = entry.useAdvancedMeshReplacement ? "_advancedMesh" : "";
                        string binName = $"{i + 1:00}_{safeBaseName}{suffix}.bin";
                        string binUnityPath = MCBUtils.CombineUnityPath(newVersionDataPath, binName);
                        if (entry.useAdvancedMeshReplacement)
                        {
                            GameObject payloadSource = entry.customFbx;
                            if (payloadSource == null)
                            {
                                payloadSource = NativeMeshPayloadService.ImportExternalFbxForPayload(customFbxPath, out importedExternalFbxPath);
                                importedExternalFbx = payloadSource;
                            }

                            NativeMeshPayloadService.NativeMeshPayloadBuildResult payloadResult = null;
                            yield return NativeMeshPayloadService.WriteEncryptedPayloadCoroutine(value => payloadResult = value,
                                entry.sourceFbxPath,
                                payloadSource,
                                entry.smrPaths,
                                this,
                                Path.GetFullPath(binUnityPath),
                                includeDynamicNormalsBody || includeDynamicNormalsFlexing || dynamicNormalSelections?.Count > 0,
                                includeDynamicNormalsBody,
                                includeDynamicNormalsFlexing,
                                createDeliveryVariants: true, dynamicNormalSelections: dynamicNormalSelections, encrypt: entry.encryptPayload);
                            entry.skeletonBones = NativeMeshPayloadService.CollectSkeletonBones(payloadSource, entry.localTargetPath ?? entry.sourceFbxPath, entry.smrPaths);
                            entry.rendererSlots = NativeMeshPayloadService.CollectRendererSlots(payloadSource, entry.smrPaths);
                            entry.outputHash = payloadResult.payloadHash;
                            entry.payloadCompression = payloadResult.payloadCompression;
                            entry.payloadVariants = payloadResult.variants;
                            entry.payloadParts = payloadResult.parts ?? new List<NativeMeshPayloadService.NativeMeshPayloadBuildResult> { payloadResult };
                            entry.advancedRendererCount = payloadResult.rendererCount;
                            entry.binUnityPath = binUnityPath;
                            entry.binHash = payloadResult.binHash;

                            // Avatar replacement is explicit and optional, also for Blender exports.
                        }
                        else
                        {
                            entry.outputHash = CalculateFileHash(customFbxPath);

                            bool wroteHdiff = false;
                            if (entry.useHdiffFbxDelta)
                            {
                                wroteHdiff = HdiffService.TryWriteXorEncryptedDiffBin(
                                    entry.sourceFbxPath,
                                    customFbxPath,
                                    Path.GetFullPath(binUnityPath),
                                    this,
                                    out var hdiffInfo,
                                    out string hdiffFailureReason);

                                if (wroteHdiff)
                                {
                                    entry.usedHdiffFbxDelta = true;
                                    entry.hdiffBuildInfo = hdiffInfo;
                                    entry.patchTransform = ModelFileTransforms.HdiffXorBinToFbx;
                                    MCBLogger.Log(
                                        $"[FileManager] Built HDiff FBX delta for '{safeBaseName}': patch={hdiffInfo.patchBytes} bytes, " +
                                        $"full={hdiffInfo.outputBytes} bytes, ratio={hdiffInfo.patchRatio:P1}, compression={hdiffInfo.compressionType ?? "unknown"}.");
                                }
                                else
                                {
                                    entry.hdiffFallbackReason = hdiffFailureReason;
                                    MCBLogger.LogWarning($"[FileManager] Falling back to XOR FBX patch for '{safeBaseName}': {hdiffFailureReason}");
                                }
                            }

                            if (!wroteHdiff)
                            {
                                byte[] baseData = File.ReadAllBytes(entry.sourceFbxPath);
                                byte[] targetData = File.ReadAllBytes(customFbxPath);
                                byte[] encryptedData = XorTransform(baseData, targetData);
                                File.WriteAllBytes(Path.GetFullPath(binUnityPath), encryptedData);
                                entry.patchTransform = ModelFileTransforms.XorBinToFbx;
                            }

                            entry.binUnityPath = binUnityPath;
                            entry.binHash = CalculateFileHash(Path.GetFullPath(binUnityPath));
                        }
                    }
                }
                finally
                {
                    if (importedExternalFbx != null || !string.IsNullOrWhiteSpace(importedExternalFbxPath))
                    {
                        NativeMeshPayloadService.DeleteTemporaryImportedFbx(importedExternalFbxPath);
                    }
                }

                if (entry.customBaseAvatar != null)
                {
                    string avatarName = $"{i + 1:00}_{safeBaseName} avatar.asset";
                    string avatarUnityPath = MCBUtils.CombineUnityPath(newVersionDataPath, avatarName);
                    if (!string.Equals(MCBUtils.ToUnityPath(customAvatarSourcePath), MCBUtils.ToUnityPath(avatarUnityPath), StringComparison.OrdinalIgnoreCase))
                    {
                        if (AssetDatabase.LoadAssetAtPath<Avatar>(avatarUnityPath) != null)
                        {
                            AssetDatabase.DeleteAsset(avatarUnityPath);
                        }
                        AssetDatabase.CopyAsset(customAvatarSourcePath, avatarUnityPath);
                    }

                    entry.avatarUnityPath = avatarUnityPath;
                    entry.avatarHash = CalculateFileHash(Path.GetFullPath(avatarUnityPath));
                }
            }

            yield return null;
            CopyLogicAndExtras(newVersionDataPath, logicPrefab, includeCustomVeins, customVeinsTexture, additionalAnimationAssetPaths);

            AssetDatabase.Refresh();
            while (EditorApplication.isUpdating || EditorApplication.isCompiling) yield return null;
        }
    }

    /// <summary>
    /// Creates the upload zip from exactly the manifest-listed output files of a built
    /// artifact, after their hashes were validated. Content-identical to the build-time
    /// folder content; version.json / manifest.json are local-only and excluded by the
    /// manifest itself. Returns the temp zip path (caller deletes it).
    /// </summary>
    public string CreateZipFromManifestOutputs(VersionArtifact artifact, System.Threading.CancellationToken cancellation = default, Action<float> progress = null)
    {
        if (artifact?.Manifest == null) throw new ArgumentNullException(nameof(artifact));

        string folderFullPath = Path.GetFullPath(artifact.FolderUnityPath);
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"mcb_upload_{Guid.NewGuid()}.zip");

        try
        {
            using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                foreach (var output in artifact.Manifest.outputs)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (output == null || string.IsNullOrWhiteSpace(output.path)) continue;
                    string sourcePath = Path.Combine(folderFullPath, output.path.Replace('/', Path.DirectorySeparatorChar));
                    var entry = zip.CreateEntry(output.path,
                        output.path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using (var input = File.OpenRead(sourcePath))
                    using (var destination = entry.Open())
                    {
                        var buffer = new byte[1024 * 1024];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            destination.Write(buffer, 0, count);
                        }
                    }
                    progress?.Invoke((artifact.Manifest.outputs.IndexOf(output) + 1f) / artifact.Manifest.outputs.Count);
                }
            }

            return tempZipPath;
        }
        catch (Exception)
        {
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            throw;
        }
    }

    private void CopyLogicAndExtras(
        string newVersionDataPath,
        GameObject logicPrefab,
        bool includeCustomVeins,
        Texture2D customVeinsTexture,
        IEnumerable<string> additionalAnimationAssetPaths)
    {
        var exportAssets = new HashSet<string>(StringComparer.Ordinal);
        if (logicPrefab != null)
        {
            string prefabSourcePath = AssetDatabase.GetAssetPath(logicPrefab);
            string prefabDestPath = MCBUtils.CombineUnityPath(newVersionDataPath, "mcb logic.prefab");
            AssetDatabase.CopyAsset(prefabSourcePath, prefabDestPath);
            foreach (string dependency in AssetDatabase.GetDependencies(prefabSourcePath, true))
            {
                exportAssets.Add(dependency);
            }
        }

        if (additionalAnimationAssetPaths != null)
        {
            foreach (var animationPath in additionalAnimationAssetPaths)
            {
                if (string.IsNullOrWhiteSpace(animationPath)) continue;
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(animationPath) == null) continue;
                exportAssets.Add(animationPath);
            }
        }

        if (exportAssets.Count > 0)
        {
            string packageUnityPath = MCBUtils.CombineUnityPath(newVersionDataPath, "mcb logic.unitypackage");
            string packagePath = Path.GetFullPath(packageUnityPath);
            AssetDatabase.ExportPackage(exportAssets.ToArray(), packagePath, ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);
        }

        if (includeCustomVeins)
        {
            if (customVeinsTexture == null)
                throw new ArgumentNullException(nameof(customVeinsTexture), "Custom veins texture is required when includeCustomVeins is enabled.");

            string sourceTexturePath = AssetDatabase.GetAssetPath(customVeinsTexture);
            if (string.IsNullOrEmpty(sourceTexturePath))
                throw new FileNotFoundException("Could not resolve asset path for the selected custom veins texture.");

            string sourceTextureFullPath = Path.GetFullPath(sourceTexturePath);
            if (!File.Exists(sourceTextureFullPath))
                throw new FileNotFoundException("Custom veins texture asset file not found on disk.", sourceTextureFullPath);

            if (!sourceTexturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Custom veins normal map must be provided as a PNG texture.");

            string veinsDestPath = MCBUtils.CombineUnityPath(newVersionDataPath, "veins normal.png");
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(veinsDestPath) != null)
            {
                AssetDatabase.DeleteAsset(veinsDestPath);
            }

            if (!AssetDatabase.CopyAsset(sourceTexturePath, veinsDestPath))
                throw new IOException($"Failed to copy custom veins normal map to {veinsDestPath}");
        }
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "model";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }
        return value;
    }
}

/// <summary>
/// How far a downloaded version archive may expand, shared by its extraction and by the scans of the Unity packages it
/// carries. Orbiters Toolkit's <see cref="Orbiters.Toolkit.Editor.Storage.ArchiveBudget"/> with version limits and wording.
/// </summary>
public sealed class VersionArchiveBudget : Orbiters.Toolkit.Editor.Storage.ArchiveBudget
{
    public VersionArchiveBudget(int maxEntries = DefaultMaxEntries, long maxTotalBytes = DefaultMaxTotalBytes,
        long maxEntryBytes = DefaultMaxEntryBytes, long maxCapturedBytes = DefaultMaxCapturedBytes)
        : base(maxEntries, maxTotalBytes, maxEntryBytes, maxCapturedBytes, "version") { }
}
#endif




