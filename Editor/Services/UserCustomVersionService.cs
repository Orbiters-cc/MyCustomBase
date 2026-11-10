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
    public string ownerId; // the signed-in user (AuthenticationService.GetAuth())
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

    /// <summary>
    /// Saves the model at <paramref name="fbxUnityPath"/> (and its Avatar) as a custom version unless an entry already
    /// holds exactly these bytes. Both decisions are proofs: the model is hashed from its bytes, an existing entry counts
    /// only when its saved copy still holds them, and a new copy is verified before its identity is recorded. The copy goes
    /// into a folder of its own, created by this call (<see cref="WriteBackup"/>): no earlier backup is ever overwritten
    /// or removed.
    /// </summary>
    /// <param name="created">Whether a new entry was saved.</param>
    public UserCustomVersionEntry SaveCurrent(string fbxUnityPath, Transform root, out bool created)
    {
        created = false;
        if (string.IsNullOrEmpty(fbxUnityPath)) return null;
        using var proof = new ContentHash.Operation();
        string currentHash = ContentHash.Proof(fbxUnityPath, proof);
        if (string.IsNullOrEmpty(currentHash)) return null;
        var existing = GetAll().Find(e => string.Equals(e.appliedUserAviHash, currentHash, StringComparison.OrdinalIgnoreCase) &&
                                          ContentHash.Verify(e.backupFbxPath, currentHash, operation: proof));
        if (existing != null) return existing;

        string folder = MCBUtils.USER_VERSIONS_DIR;
        if (!AssetDatabase.IsValidFolder(MCBUtils.ASSETS_BASE_FOLDER))
        {
            AssetDatabase.CreateFolder("Assets", "MCB");
        }
        if (!AssetDatabase.IsValidFolder(folder))
        {
            AssetDatabase.CreateFolder(MCBUtils.ASSETS_BASE_FOLDER, "userVersions");
        }

        DateTime now = DateTime.Now;
        SavedBackup backup;
        try
        {
            backup = WriteBackup(Path.GetFullPath(folder), Path.GetFullPath(fbxUnityPath), currentHash, now);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
        {
            MCBLogger.LogError($"[MCB] The custom FBX could not be saved; nothing was recorded ({ex.Message}): {fbxUnityPath}");
            return null;
        }

        string customFolderPath = MCBUtils.CombineUnityPath(folder, Path.GetFileName(backup.folder));
        string destFbxPath = MCBUtils.CombineUnityPath(customFolderPath, Path.GetFileName(backup.fbx));
        // Imports only this folder: the copied import settings carry a GUID of their own.
        AssetDatabase.ImportAsset(customFolderPath, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);

        // Determine avatar asset and copy it if possible (into this call's folder, never over an existing file)
        string avatarAssetSourcePath = TryGetAvatarAssetPath(fbxUnityPath, root);
        string copiedAvatarAssetPath = null;
        if (!string.IsNullOrEmpty(avatarAssetSourcePath) && File.Exists(Path.GetFullPath(avatarAssetSourcePath)))
        {
            string destAvatarPath = MCBUtils.CombineUnityPath(customFolderPath, "custom avatar.asset");
            if (File.Exists(Path.GetFullPath(destAvatarPath)))
            {
                MCBLogger.LogWarning($"[MCB] Could not copy avatar asset: {destAvatarPath} already exists.");
            }
            else if (AssetDatabase.CopyAsset(avatarAssetSourcePath, destAvatarPath))
            {
                copiedAvatarAssetPath = destAvatarPath;
            }
            else
            {
                try
                {
                    File.Copy(Path.GetFullPath(avatarAssetSourcePath), Path.GetFullPath(destAvatarPath), false);
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
            detectionDate = now.ToString("yyyy-MM-dd_HHmmss"),
            appliedUserAviHash = currentHash,
            ownerId = auth?.user,
            backupFbxPath = destFbxPath,
            appliedAvatarAsset = copiedAvatarAssetPath
        };
        GetAll().Add(entry);
        SaveAll();
        created = true;
        return entry;
    }

    internal sealed class SavedBackup
    {
        public string folder, fbx;
    }

    /// <summary>
    /// File-system part of <see cref="SaveCurrent"/>, no asset database: creates a new folder under
    /// <paramref name="rootFullPath"/> (its name ends in a random suffix and is checked not to exist, so two saves in the
    /// same second never share one), copies the model and its import settings (with a new GUID) into it without
    /// overwriting anything, and reads the copy back. When the copy does not hold <paramref name="expectedHash"/>, or
    /// anything fails, it removes only the files it created, each only while it still holds what it wrote, and the folder
    /// only when that leaves it empty, then throws.
    /// </summary>
    internal static SavedBackup WriteBackup(string rootFullPath, string sourceFbxFullPath, string expectedHash, DateTime now)
    {
        Directory.CreateDirectory(rootFullPath);
        string stamp = now.ToString("yyyy-MM-dd_HHmmss");
        string folderFull;
        do folderFull = Path.Combine(rootFullPath, "custom" + stamp + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        while (Directory.Exists(folderFull) || File.Exists(folderFull));
        Directory.CreateDirectory(folderFull);

        var written = new List<(string path, string hash)>();
        try
        {
            string fbx = Path.Combine(folderFull, Path.GetFileName(sourceFbxFullPath));
            try { File.Copy(sourceFbxFullPath, fbx, false); }
            catch
            {
                // A partial copy in this call's own new folder.
                if (File.Exists(fbx)) File.Delete(fbx);
                throw;
            }
            string copied = ContentHash.Proof(fbx);
            written.Add((fbx, copied));
            if (!string.Equals(copied, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the model changed while it was saved");

            string sourceMeta = sourceFbxFullPath + ".meta";
            if (File.Exists(sourceMeta))
            {
                string meta = fbx + ".meta";
                string text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(sourceMeta), @"(?m)^guid: [0-9a-f]{32}",
                    "guid: " + Guid.NewGuid().ToString("N"));
                using (var stream = new FileStream(meta, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream)) writer.Write(text);
                written.Add((meta, ContentHash.Proof(meta)));
            }
            return new SavedBackup { folder = folderFull, fbx = fbx };
        }
        catch
        {
            for (int i = written.Count - 1; i >= 0; i--)
            {
                var (path, hash) = written[i];
                try { if (ContentHash.Verify(path, hash)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            try { if (Directory.Exists(folderFull) && Directory.GetFileSystemEntries(folderFull).Length == 0) Directory.Delete(folderFull, false); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            throw;
        }
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
