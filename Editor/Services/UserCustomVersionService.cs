#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

[Serializable]
public class UserCustomVersionEntry
{
    public string detectionDate; // e.g. 2025-11-16_2022
    public string appliedUserAviHash; // hash of the currently applied FBX when detected
    public string ownerId; // from AuthenticationModule.GetAuth()
    public string backupFbxPath; // Unity path to the copied custom FBX
    public string appliedAvatarAsset; // Unity path to the avatar asset applied to this FBX
}

public class UserCustomVersionService
{
    private static UserCustomVersionService _instance;
    public static UserCustomVersionService Instance => _instance ?? (_instance = new UserCustomVersionService());

    private List<UserCustomVersionEntry> _cache = null;

    public List<UserCustomVersionEntry> GetAll()
    {
        if (_cache != null) return _cache;
        string path = MCBUtils.USER_VERSIONS_FILE;
        _cache = new List<UserCustomVersionEntry>();
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var list = JsonConvert.DeserializeObject<List<UserCustomVersionEntry>>(json);
                if (list != null) _cache = list;
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to load user custom versions: {ex.Message}");
        }
        return _cache;
    }

    public void SaveAll()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MCBUtils.USER_VERSIONS_FILE));
            File.WriteAllText(MCBUtils.USER_VERSIONS_FILE, JsonConvert.SerializeObject(_cache, Formatting.Indented));
            AssetDatabase.Refresh();
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to save user custom versions: {ex.Message}");
        }
    }

    public bool ExistsByAppliedHash(string appliedHash)
    {
        if (string.IsNullOrEmpty(appliedHash)) return false;
        var list = GetAll();
        return list.Exists(e => string.Equals(e.appliedUserAviHash, appliedHash, StringComparison.OrdinalIgnoreCase));
    }

    public UserCustomVersionEntry CreateFromCurrent(string fbxUnityPath, string currentHash, Transform root)
    {
        if (string.IsNullOrEmpty(fbxUnityPath) || string.IsNullOrEmpty(currentHash)) return null;
        if (ExistsByAppliedHash(currentHash))
        {
            return GetAll().Find(e => e.appliedUserAviHash == currentHash);
        }

        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string folder = MCBUtils.USER_VERSIONS_DIR;
        if (!AssetDatabase.IsValidFolder(MCBUtils.ASSETS_BASE_FOLDER))
        {
            AssetDatabase.CreateFolder("Assets", "MCB");
        }
        if (!AssetDatabase.IsValidFolder(folder))
        {
            AssetDatabase.CreateFolder(MCBUtils.ASSETS_BASE_FOLDER, "userVersions");
        }

        string customFolderName = $"custom{timestamp}";
        string customFolderPath = MCBUtils.CombineUnityPath(folder, customFolderName);
        AssetDatabase.CreateFolder(folder, customFolderName);

        // Copy FBX
        string fbxFileName = Path.GetFileName(fbxUnityPath);
        string destFbxPath = MCBUtils.CombineUnityPath(customFolderPath, fbxFileName);
        if (!AssetDatabase.CopyAsset(fbxUnityPath, destFbxPath))
        {
            // Fallback to file copy
            try
            {
                File.Copy(Path.GetFullPath(fbxUnityPath), Path.GetFullPath(destFbxPath), true);
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[MCB] Failed to copy custom FBX: {ex.Message}");
            }
        }

        // Determine avatar asset and copy it if possible
        string avatarAssetSourcePath = TryGetAvatarAssetPath(fbxUnityPath, root);
        string copiedAvatarAssetPath = null;
        if (!string.IsNullOrEmpty(avatarAssetSourcePath) && File.Exists(Path.GetFullPath(avatarAssetSourcePath)))
        {
            string destAvatarPath = MCBUtils.CombineUnityPath(customFolderPath, "custom avatar.asset");
            if (AssetDatabase.CopyAsset(avatarAssetSourcePath, destAvatarPath))
            {
                copiedAvatarAssetPath = destAvatarPath;
            }
            else
            {
                try
                {
                    File.Copy(Path.GetFullPath(avatarAssetSourcePath), Path.GetFullPath(destAvatarPath), true);
                    copiedAvatarAssetPath = destAvatarPath;
                }
                catch (Exception ex)
                {
                    MCBLogger.LogWarning($"[MCB] Could not copy avatar asset: {ex.Message}");
                }
            }
        }

        var auth = AuthenticationService.GetAuth();

        var entry = new UserCustomVersionEntry
        {
            detectionDate = timestamp,
            appliedUserAviHash = currentHash,
            ownerId = auth?.user,
            backupFbxPath = destFbxPath,
            appliedAvatarAsset = copiedAvatarAssetPath
        };
        GetAll().Add(entry);
        SaveAll();
        return entry;
    }

    public bool Delete(UserCustomVersionEntry entry)
    {
        if (entry == null) return false;
        try
        {
            // Resolve the folder from the stored FBX path
            string fbxUnityPath = MCBUtils.ToUnityPath(entry.backupFbxPath);
            string folderUnityPath = Path.GetDirectoryName(fbxUnityPath)?.Replace("\\", "/");

            bool deleted = false;
            if (!string.IsNullOrEmpty(folderUnityPath) && AssetDatabase.IsValidFolder(folderUnityPath))
            {
                deleted = AssetDatabase.DeleteAsset(folderUnityPath);
            }

            if (!deleted)
            {
                // Fallback to IO delete
                string folderAbsPath = Path.GetFullPath(folderUnityPath ?? string.Empty);
                if (Directory.Exists(folderAbsPath))
                {
                    Directory.Delete(folderAbsPath, true);
                    string meta = folderAbsPath + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                    deleted = true;
                }
            }

            // Remove entry from cache regardless of file deletion result
            var list = GetAll();
            list.RemoveAll(e => string.Equals(e.appliedUserAviHash, entry.appliedUserAviHash, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(e.detectionDate, entry.detectionDate, StringComparison.OrdinalIgnoreCase));
            SaveAll();

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return true;
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to delete user custom version: {ex.Message}");
            return false;
        }
    }

    private string TryGetAvatarAssetPath(string fbxUnityPath, Transform root)
    {
        // First: ModelImporter setup
        var importer = AssetImporter.GetAtPath(fbxUnityPath) as ModelImporter;
        if (importer != null && importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther && importer.sourceAvatar != null)
        {
            string p = AssetDatabase.GetAssetPath(importer.sourceAvatar);
            if (!string.IsNullOrEmpty(p)) return p;
        }

        // Fallback: Animator on root
        if (root != null)
        {
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.avatar != null)
            {
                string p = AssetDatabase.GetAssetPath(animator.avatar);
                if (!string.IsNullOrEmpty(p)) return p;
            }
        }

        return null;
    }
}
#endif
