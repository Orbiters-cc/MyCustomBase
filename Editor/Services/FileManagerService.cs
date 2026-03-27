#if UNITY_EDITOR
using System;
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
        if (File.Exists(backupPath)) File.Delete(backupPath);
        File.Move(fbxPath, backupPath);
    }
    
    public bool BackupExists(string fbxPath)
    {
        return !string.IsNullOrEmpty(fbxPath) && File.Exists(fbxPath + OriginalSuffix);
    }

    public void RestoreBackup(string fbxPath)
    {
        string backupPath = fbxPath + OriginalSuffix;
        if (!File.Exists(backupPath)) return;
        if (File.Exists(fbxPath)) File.Delete(fbxPath);
        File.Move(backupPath, fbxPath);
    }

    // Force-restore a specific FBX regardless of current selection/state.
    // Always delete the .fbx (if present) and rename .fbx.old back to .fbx, then force re-import.
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

        if (File.Exists(fullFbxPath))
        {
            File.Delete(fullFbxPath);
        }

        File.Move(fullBackupPath, fullFbxPath);

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

        // FIX: Re-enabled the actual avatar application logic. This call re-imports the model with the new avatar.
        CustomBaseAvatarUtility.ApplyExternalAvatar(fbx, unityAvatarPath);

        SetRootAnimatorAvatar(root, unityAvatarPath);
    }

    public void InstantiateLogicPrefab(string packagePath, Transform parent)
    {
        if (string.IsNullOrEmpty(packagePath) || parent == null) return;

        string unityPackagePath = MCBUtils.ToUnityPath(packagePath);
        string absolutePackagePath = Path.GetFullPath(unityPackagePath);
        
        // Import the unitypackage if it exists
        if (File.Exists(absolutePackagePath))
        {
            MCBLogger.Log($"[FileManager] Importing unity package at {unityPackagePath}");
            AssetDatabase.ImportPackage(unityPackagePath, false);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            MCBLogger.Log("[FileManager] Unity package import completed.");
        }
        else
        {
            MCBLogger.LogWarning($"[FileManager] Unity package not found at '{absolutePackagePath}'.");
        }
        
        string versionDataFolderUnity = MCBUtils.ToUnityPath(Path.GetDirectoryName(unityPackagePath));
        string prefabPath = MCBUtils.CombineUnityPath(versionDataFolderUnity, "mcb logic.prefab");
        string absolutePrefabPath = Path.GetFullPath(prefabPath);

        if (!File.Exists(absolutePrefabPath))
        {
            MCBLogger.LogWarning($"[FileManager] Expected prefab not found at '{absolutePrefabPath}'.");
            return;
        }

        MCBLogger.Log($"[FileManager] Importing logic prefab at {prefabPath}");
        AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        MCBLogger.Log("[FileManager] Logic prefab import completed.");

        GameObject logicPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
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

    private void SetRootAnimatorAvatar(Transform root, string avatarAssetPath)
    {
        if (root == null) return;

        Animator animator = root.GetComponent<Animator>();
        if (animator == null)
        {
            animator = Undo.AddComponent<Animator>(root.gameObject);
        }

        Avatar avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarAssetPath);
        if (animator.avatar != avatar)
        {
            Undo.RecordObject(animator, "Set Root Animator Avatar");
            animator.avatar = avatar;
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

        string versionFolderUnityPath = MCBUtils.GetVersionDataPath(version.version, version.defaultAviVersion);
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
        string newVersionString, 
        string baseFbxVersion,
        string originalFbxPath,
        GameObject customFbx,
        Avatar customBaseAvatar,
        GameObject logicPrefab,
        CustomBaseVersion parentVersion,
        bool includeCustomVeins,
        Texture2D customVeinsTexture,
        IEnumerable<string> additionalAnimationAssetPaths = null)
    {
        string newVersionDataPath = MCBUtils.GetVersionDataPath(newVersionString, baseFbxVersion);
        string newVersionDataFullPath = Path.GetFullPath(newVersionDataPath);
        string tempZipPath = Path.Combine(Path.GetTempPath(), $"mcb_upload_{Guid.NewGuid()}.zip");

        try
        {
            MCBUtils.EnsureDirectoryExists(newVersionDataPath, canBeFilePath: false);
            
            // 1. Copy Avatars
            string customBaseAvatarSourcePath = AssetDatabase.GetAssetPath(customBaseAvatar);
            AssetDatabase.CopyAsset(customBaseAvatarSourcePath, MCBUtils.CombineUnityPath(newVersionDataPath, MCBUtils.CUSTOM_BASE_AVATAR_NAME));
            
            string defaultAvatarSourcePath = "Packages/orbiters.mcb/creator assets/default avatar.asset";
            var defaultAvatarAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(defaultAvatarSourcePath);
            if (defaultAvatarAsset == null)
                throw new FileNotFoundException("Could not find the required 'default avatar.asset' in 'Packages/orbiters.mcb/creator assets'.");
            AssetDatabase.CopyAsset(defaultAvatarSourcePath, MCBUtils.CombineUnityPath(newVersionDataPath, MCBUtils.DEFAULT_AVATAR_NAME));

            // 2. Create .bin file (Encrypt custom FBX against original base FBX)
            string customFbxPath = AssetDatabase.GetAssetPath(customFbx);
            byte[] baseData = File.ReadAllBytes(originalFbxPath);
            byte[] targetData = File.ReadAllBytes(customFbxPath);
            // In this case, we are creating the "key" (the .bin file). The operation is the same.
            byte[] encryptedData = XorTransform(baseData, targetData); 
            string binFilePath = Path.Combine(newVersionDataFullPath, "mcb.bin");
            File.WriteAllBytes(binFilePath, encryptedData);

            // 3. Create logic package
            string prefabSourcePath = AssetDatabase.GetAssetPath(logicPrefab);
            string prefabDestPath = MCBUtils.CombineUnityPath(newVersionDataPath, "mcb logic.prefab");
            AssetDatabase.CopyAsset(prefabSourcePath, prefabDestPath);
            
            string packageUnityPath = MCBUtils.CombineUnityPath(newVersionDataPath, "mcb logic.unitypackage");
            string packagePath = Path.GetFullPath(packageUnityPath);

            var exportAssets = new HashSet<string>(AssetDatabase.GetDependencies(prefabSourcePath, true), StringComparer.Ordinal);
            if (additionalAnimationAssetPaths != null)
            {
                foreach (var animationPath in additionalAnimationAssetPaths)
                {
                    if (string.IsNullOrWhiteSpace(animationPath)) continue;
                    if (AssetDatabase.LoadAssetAtPath<AnimationClip>(animationPath) == null) continue;
                    exportAssets.Add(animationPath);
                }
            }

            AssetDatabase.ExportPackage(exportAssets.ToArray(), packagePath, ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);

            // 4. Copy custom veins normal map if requested
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

            // 5. Create ZIP
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            ZipFile.CreateFromDirectory(newVersionDataFullPath, tempZipPath, CompressionLevel.Optimal, false);

            return tempZipPath;
        }
        catch (Exception)
        {
            // Cleanup on failure
            if (Directory.Exists(newVersionDataPath)) Directory.Delete(newVersionDataPath, true);
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            throw; // Re-throw the exception for the module to catch and display
        }
    }
}
#endif




