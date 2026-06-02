#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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
    public const string OriginalSuffix = ".old";

    public class ModelFilePackageEntry
    {
        public string sourceFbxPath;
        public GameObject customFbx;
        public string externalCustomFbxPath;
        public Avatar customBaseAvatar;
        public bool useAdvancedMeshReplacement;
        public List<ModelFileSmrPathData> smrPaths;
        public string binUnityPath;
        public string avatarUnityPath;
        public string sourceHash;
        public string binHash;
        public string avatarHash;
        public string outputHash;
        public string payloadCompression;
        public int advancedRendererCount;
    }

    public string CalculateFileHash(string path)
    {
        if (!File.Exists(path)) return null;
        using (var sha256 = SHA256.Create())
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
        string backupPath = fbxPath + OriginalSuffix;
        if (File.Exists(backupPath)) return;
        File.Copy(fbxPath, backupPath);
    }
    
    public bool BackupExists(string fbxPath)
    {
        return !string.IsNullOrEmpty(fbxPath) && File.Exists(fbxPath + OriginalSuffix);
    }

    public bool FbxMatchesBackupAtPath(string unityFbxPath)
    {
        if (string.IsNullOrEmpty(unityFbxPath))
        {
            return false;
        }

        string unityPath = MCBUtils.ToUnityPath(unityFbxPath);
        string fullFbxPath = Path.GetFullPath(unityPath);
        string fullBackupPath = fullFbxPath + OriginalSuffix;
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

        string backupPath = targetFullPath + OriginalSuffix;
        if (!File.Exists(backupPath))
        {
            File.Copy(targetFullPath, backupPath);
            MCBLogger.Log($"[FileManager] Created original FBX backup: {backupPath}");
        }

        if (FilesAreEqual(targetFullPath, customFullPath))
        {
            return false;
        }

        File.Copy(customFullPath, targetFullPath, true);
        return true;
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
        string backupPath = fbxPath + OriginalSuffix;
        if (!File.Exists(backupPath)) return;
        File.Copy(backupPath, fbxPath, true);
    }

    // Force-restore a specific FBX regardless of current selection/state.
    // The .old file is the immutable default-base source and must remain in place.
    public void ForceRestoreBackupAtPath(string unityFbxPath)
    {
        if (string.IsNullOrEmpty(unityFbxPath)) throw new ArgumentNullException(nameof(unityFbxPath));
        string unityPath = MCBUtils.ToUnityPath(unityFbxPath);
        string fullFbxPath = Path.GetFullPath(unityPath);
        string fullBackupPath = fullFbxPath + OriginalSuffix;

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
    
    public void DeleteVersionFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        Directory.Delete(path, true);
        if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
    }

    public byte[] XorTransform(byte[] baseData, byte[] keyData)
    {
        // The XOR key should be the BASE data, and the TARGET data is what's being 'encrypted'.
        // The provided code has keyData (the .bin) as the main loop, this is correct for decryption.
        byte[] transformedData = new byte[keyData.Length];
        for (int i = 0; i < keyData.Length; i++)
        {
            transformedData[i] = (byte)(keyData[i] ^ baseData[i % baseData.Length]);
        }
        return transformedData;
    }

    public void UnzipAndMove(string zipPath, string extractPath, string finalDestinationPath)
    {
        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
        Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(zipPath, extractPath);

        string contentSourcePath = extractPath;
        var rootDirs = Directory.GetDirectories(extractPath);
        if (rootDirs.Length == 1 && Directory.GetFiles(extractPath).Length == 0)
        {
            contentSourcePath = rootDirs[0];
        }

        if (Directory.Exists(finalDestinationPath)) Directory.Delete(finalDestinationPath, true);
        CopyDirectory(contentSourcePath, finalDestinationPath);
    }

    public Dictionary<string, byte[]> UnzipAndMoveFromMemory(
        byte[] zipBytes,
        string finalDestinationPath,
        ISet<string> captureRelativePaths = null)
    {
        if (zipBytes == null || zipBytes.Length == 0)
        {
            throw new InvalidDataException("Version ZIP data is empty.");
        }

        if (string.IsNullOrWhiteSpace(finalDestinationPath))
        {
            throw new ArgumentNullException(nameof(finalDestinationPath));
        }

        var captured = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var normalizedCapturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in captureRelativePaths ?? new HashSet<string>())
        {
            string normalized = NormalizeZipRelativePath(path);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                normalizedCapturePaths.Add(normalized);
            }
        }

        if (Directory.Exists(finalDestinationPath)) Directory.Delete(finalDestinationPath, true);
        Directory.CreateDirectory(finalDestinationPath);

        string destinationRoot = Path.GetFullPath(finalDestinationPath);
        using (var zipStream = new MemoryStream(zipBytes, false))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, false))
        {
            var fileEntries = archive.Entries
                .Where(entry => entry != null && !IsZipDirectory(entry))
                .ToList();
            string rootPrefix = ResolveSingleZipRootPrefix(fileEntries);

            foreach (var entry in fileEntries)
            {
                string relativePath = NormalizeZipRelativePath(entry.FullName);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(rootPrefix) &&
                    relativePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = relativePath.Substring(rootPrefix.Length);
                }

                relativePath = NormalizeZipRelativePath(relativePath);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                string outputPath = Path.GetFullPath(Path.Combine(
                    finalDestinationPath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsSameOrChildPath(outputPath, destinationRoot))
                {
                    throw new InvalidDataException($"ZIP entry resolves outside the version folder: {entry.FullName}");
                }

                string outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                bool shouldCapture = normalizedCapturePaths.Contains(relativePath);
                if (shouldCapture)
                {
                    using (var entryStream = entry.Open())
                    using (var memory = new MemoryStream())
                    {
                        entryStream.CopyTo(memory);
                        byte[] bytes = memory.ToArray();
                        File.WriteAllBytes(outputPath, bytes);
                        captured[relativePath] = bytes;
                    }
                }
                else
                {
                    using (var entryStream = entry.Open())
                    using (var fileStream = File.Create(outputPath))
                    {
                        entryStream.CopyTo(fileStream);
                    }
                }
            }
        }

        return captured;
    }

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

    private static bool IsZipDirectory(ZipArchiveEntry entry)
    {
        return entry == null ||
               string.IsNullOrEmpty(entry.Name) ||
               entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
               entry.FullName.EndsWith("\\", StringComparison.Ordinal);
    }

    private static string ResolveSingleZipRootPrefix(IEnumerable<ZipArchiveEntry> entries)
    {
        string root = null;
        foreach (var entry in entries ?? Enumerable.Empty<ZipArchiveEntry>())
        {
            string path = NormalizeZipRelativePath(entry.FullName);
            int slashIndex = path.IndexOf('/');
            if (slashIndex <= 0)
            {
                return null;
            }

            string candidate = path.Substring(0, slashIndex);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = candidate;
            }
            else if (!string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return string.IsNullOrWhiteSpace(root) ? null : root + "/";
    }

    private static string NormalizeZipRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        return normalized;
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

    public IEnumerator InstantiateLogicPrefabCoroutine(string packagePath, Transform parent)
    {
        if (string.IsNullOrEmpty(packagePath) || parent == null) yield break;

        string unityPackagePath = MCBUtils.ToUnityPath(packagePath);
        string absolutePackagePath = Path.GetFullPath(unityPackagePath);
        string versionDataFolderUnity = MCBUtils.ToUnityPath(Path.GetDirectoryName(unityPackagePath));
        string prefabPath = MCBUtils.GetLogicPrefabPath(versionDataFolderUnity);
        bool prefabReady = !string.IsNullOrEmpty(prefabPath) &&
                           File.Exists(Path.GetFullPath(prefabPath)) &&
                           AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
        
        if (!prefabReady && File.Exists(absolutePackagePath))
        {
            MCBLogger.Log($"[FileManager] Importing unity package at {unityPackagePath}");
            bool packageImportFinished = false;
            bool packageImportFailed = false;
            bool packageImportCancelled = false;
            string packageImportFailureMessage = null;

            void HandleImportCompleted(string _) => packageImportFinished = true;
            void HandleImportCancelled(string _) => packageImportCancelled = true;
            void HandleImportFailed(string _, string error)
            {
                packageImportFailed = true;
                packageImportFailureMessage = error;
            }

            AssetDatabase.importPackageCompleted += HandleImportCompleted;
            AssetDatabase.importPackageCancelled += HandleImportCancelled;
            AssetDatabase.importPackageFailed += HandleImportFailed;

            try
            {
                AssetDatabase.ImportPackage(unityPackagePath, false);

                double startTime = EditorApplication.timeSinceStartup;
                const double timeoutSeconds = 30.0d;
                while (!packageImportFinished && !packageImportFailed && !packageImportCancelled)
                {
                    if (EditorApplication.timeSinceStartup - startTime > timeoutSeconds)
                    {
                        throw new TimeoutException($"Timed out while importing nested package '{unityPackagePath}'.");
                    }

                    yield return null;
                }
            }
            finally
            {
                AssetDatabase.importPackageCompleted -= HandleImportCompleted;
                AssetDatabase.importPackageCancelled -= HandleImportCancelled;
                AssetDatabase.importPackageFailed -= HandleImportFailed;
            }

            if (packageImportCancelled)
            {
                MCBLogger.LogWarning($"[FileManager] Unity package import was cancelled: {unityPackagePath}");
            }
            else if (packageImportFailed)
            {
                throw new InvalidOperationException($"Nested unitypackage import failed for '{unityPackagePath}': {packageImportFailureMessage}");
            }

            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                yield return null;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            MCBLogger.Log("[FileManager] Unity package import completed.");
            prefabPath = MCBUtils.GetLogicPrefabPath(versionDataFolderUnity);
        }
        else if (!prefabReady)
        {
            MCBLogger.LogWarning($"[FileManager] Unity package not found at '{absolutePackagePath}'.");
        }
        else
        {
            MCBLogger.Log($"[FileManager] Using existing logic prefab at {prefabPath}");
        }
        
        if (string.IsNullOrEmpty(prefabPath))
        {
            MCBLogger.LogWarning($"[FileManager] No logic prefab found in '{versionDataFolderUnity}'.");
            yield break;
        }

        string absolutePrefabPath = Path.GetFullPath(prefabPath);

        if (!File.Exists(absolutePrefabPath))
        {
            MCBLogger.LogWarning($"[FileManager] Expected prefab not found at '{absolutePrefabPath}'.");
            yield break;
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
    
    public string CreateVersionPackageForUpload(
        int assetId,
        string newVersionString,
        string baseFbxVersion,
        IList<ModelFilePackageEntry> modelEntries,
        GameObject logicPrefab,
        bool includeCustomVeins,
        Texture2D customVeinsTexture,
        bool includeDynamicNormalsBody,
        bool includeDynamicNormalsFlexing,
        bool compressAdvancedMeshPayload,
        IEnumerable<string> additionalAnimationAssetPaths = null)
    {
        string newVersionDataPath = MCBUtils.GetVersionDataPath(assetId, newVersionString, baseFbxVersion);
        if (string.IsNullOrEmpty(newVersionDataPath))
        {
            throw new ArgumentException("A valid assetId, version, and base FBX version are required to create a version package.");
        }

        string newVersionDataFullPath = Path.GetFullPath(newVersionDataPath);
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"mcb_upload_{Guid.NewGuid()}.zip");

        try
        {
            if (Directory.Exists(newVersionDataFullPath))
            {
                Directory.Delete(newVersionDataFullPath, true);
            }

            MCBUtils.EnsureDirectoryExists(newVersionDataPath, canBeFilePath: false);

            string defaultAvatarSourcePath = "Packages/orbiters.mcb/creator assets/default avatar.asset";
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(defaultAvatarSourcePath) == null)
                throw new FileNotFoundException("Could not find the required 'default avatar.asset' in 'Packages/orbiters.mcb/creator assets'.");
            AssetDatabase.CopyAsset(defaultAvatarSourcePath, MCBUtils.CombineUnityPath(newVersionDataPath, MCBUtils.DEFAULT_AVATAR_NAME));

            var entries = modelEntries ?? Array.Empty<ModelFilePackageEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
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

                            var payloadResult = NativeMeshPayloadService.WriteEncryptedPayload(
                                entry.sourceFbxPath,
                                payloadSource,
                                entry.smrPaths,
                                this,
                                Path.GetFullPath(binUnityPath),
                                includeDynamicNormalsBody || includeDynamicNormalsFlexing,
                                includeDynamicNormalsBody,
                                includeDynamicNormalsFlexing,
                                compressAdvancedMeshPayload);
                            entry.outputHash = payloadResult.payloadHash;
                            entry.payloadCompression = payloadResult.payloadCompression;
                            entry.advancedRendererCount = payloadResult.rendererCount;
                            entry.binUnityPath = binUnityPath;
                            entry.binHash = payloadResult.binHash;

                            if (importedExternalFbx != null && entry.customBaseAvatar == null)
                            {
                                string sourceMappingPath = MCBUtils.ToUnityPath(entry.sourceFbxPath);
                                if (sourceMappingPath.EndsWith(OriginalSuffix, StringComparison.OrdinalIgnoreCase))
                                {
                                    sourceMappingPath = sourceMappingPath.Substring(0, sourceMappingPath.Length - OriginalSuffix.Length);
                                }

                                var sourceMappingFbx = AssetDatabase.LoadAssetAtPath<GameObject>(sourceMappingPath);
                                string avatarName = $"{i + 1:00}_{safeBaseName} avatar.asset";
                                string avatarUnityPath = MCBUtils.CombineUnityPath(newVersionDataPath, avatarName);
                                var avatarResult = AvatarDefinitionGenerationService.GenerateAvatarAsset(
                                    importedExternalFbx,
                                    sourceMappingFbx,
                                    avatarUnityPath,
                                    applyGeneratedAvatarToFbx: false,
                                    keepImporterConfiguredForEditing: false);
                                if (avatarResult?.avatar != null)
                                {
                                    entry.avatarUnityPath = avatarUnityPath;
                                    entry.avatarHash = CalculateFileHash(Path.GetFullPath(avatarUnityPath));
                                }
                            }
                        }
                        else
                        {
                            byte[] baseData = File.ReadAllBytes(entry.sourceFbxPath);
                            byte[] targetData = File.ReadAllBytes(customFbxPath);
                            byte[] encryptedData = XorTransform(baseData, targetData);
                            entry.outputHash = CalculateFileHash(customFbxPath);

                            File.WriteAllBytes(Path.GetFullPath(binUnityPath), encryptedData);

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

            CopyLogicAndExtras(newVersionDataPath, logicPrefab, includeCustomVeins, customVeinsTexture, additionalAnimationAssetPaths);

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            ZipFile.CreateFromDirectory(newVersionDataFullPath, tempZipPath, CompressionLevel.Optimal, false);

            return tempZipPath;
        }
        catch (Exception)
        {
            if (Directory.Exists(newVersionDataFullPath)) Directory.Delete(newVersionDataFullPath, true);
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
#endif




