#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

public static class AvatarPathOverrideService
{
    public const string SourceImportKindDefaultBase = "default-base";
    public const string SourceImportKindUnityPackage = "unitypackage";
    public const string SourceImportKindRawFbx = "raw-fbx";
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class DiscoveryPathOverridePayload
    {
        [JsonProperty] public int sourceModelFileId;
        [JsonProperty] public string referenceSourcePath;
        [JsonProperty] public string referenceHash;
        [JsonProperty] public string localTargetPath;
    }

    public static bool EnsureInstanceId(MyCustomBase target)
    {
        if (target == null) return false;
        if (!string.IsNullOrWhiteSpace(target.mcbInstanceId)) return false;

        target.mcbInstanceId = Guid.NewGuid().ToString("N");
        EditorUtility.SetDirty(target);
        return true;
    }

    public static string NormalizeUnityPath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : MCBUtils.ToUnityPath(path).Replace("\\", "/");
    }

    public static string GetOriginalBasePath(string localTargetPath)
    {
        string normalized = NormalizeUnityPath(localTargetPath);
        return string.IsNullOrWhiteSpace(normalized) ? null : FileManagerService.GetOriginalBasePath(normalized);
    }

    public static string ResolveLocalTargetPath(
        MyCustomBase target,
        ModelFileData sourceFile,
        bool updateStoredPathFromGuid = true)
    {
        if (sourceFile == null)
        {
            return null;
        }

        return ResolveLocalTargetPath(
            target,
            sourceFile.path,
            sourceFile.id,
            sourceFile.hash,
            updateStoredPathFromGuid);
    }

    public static string ResolveLocalTargetPath(
        MyCustomBase target,
        string referenceSourcePath,
        int sourceModelFileId = 0,
        string referenceHash = null,
        bool updateStoredPathFromGuid = true)
    {
        var entry = FindOverride(target, referenceSourcePath, sourceModelFileId, referenceHash);
        if (entry != null)
        {
            string path = NormalizeUnityPath(entry.localTargetPath);
            if (ProjectFileExists(path))
            {
                return path;
            }

            string guidPath = ResolveGuidPath(entry.localTargetGuid);
            if (ProjectFileExists(guidPath))
            {
                if (updateStoredPathFromGuid &&
                    !string.Equals(entry.localTargetPath, guidPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryMigrateManagedSidecars(entry.localTargetPath, guidPath, entry, out string migrationError))
                    {
                        entry.localTargetPath = guidPath;
                        EditorUtility.SetDirty(target);
                    }
                    else
                    {
                        MCBLogger.LogError($"[AvatarPathOverride] Could not update moved target path: {migrationError}");
                        return null;
                    }
                }

                return guidPath;
            }

            return null;
        }

        string referencePath = NormalizeUnityPath(referenceSourcePath);
        return ProjectFileExists(referencePath) ? referencePath : null;
    }

    public static AvatarPathOverrideEntry FindOverride(
        MyCustomBase target,
        ModelFileData sourceFile)
    {
        if (sourceFile == null) return null;
        return FindOverride(target, sourceFile.path, sourceFile.id, sourceFile.hash);
    }

    public static AvatarPathOverrideEntry FindOverride(
        MyCustomBase target,
        string referenceSourcePath,
        int sourceModelFileId = 0,
        string referenceHash = null)
    {
        if (target?.avatarPathOverrides == null || target.avatarPathOverrides.Count == 0)
        {
            return null;
        }

        string normalizedReferencePath = NormalizeUnityPath(referenceSourcePath);
        string normalizedReferenceHash = NormalizeHash(referenceHash);

        var entries = target.avatarPathOverrides.Where(entry => entry != null).ToList();
        if (sourceModelFileId > 0)
        {
            var byId = entries.Where(entry =>
                    entry.sourceModelFileId == sourceModelFileId &&
                    IdentityHashMatches(entry, normalizedReferenceHash) &&
                    IdentityPathMatchesWhenProvided(entry, normalizedReferencePath))
                .ToList();
            if (byId.Count == 1)
            {
                return byId[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedReferencePath))
        {
            var byPath = entries.Where(entry =>
                    string.Equals(NormalizeUnityPath(entry.referenceSourcePath), normalizedReferencePath, StringComparison.OrdinalIgnoreCase) &&
                    IdentityHashMatches(entry, normalizedReferenceHash))
                .ToList();
            if (byPath.Count == 1)
            {
                return byPath[0];
            }
        }

        if (sourceModelFileId <= 0 &&
            string.IsNullOrWhiteSpace(normalizedReferencePath) &&
            !string.IsNullOrWhiteSpace(normalizedReferenceHash))
        {
            var byHash = entries.Where(entry =>
                    string.Equals(NormalizeHash(entry.referenceHash), normalizedReferenceHash, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return byHash.Count == 1 ? byHash[0] : null;
        }

        return null;
    }

    public static AvatarPathOverrideEntry FindOverrideForLocalTarget(MyCustomBase target, string localTargetPath)
    {
        string normalizedLocalPath = NormalizeUnityPath(localTargetPath);
        if (target?.avatarPathOverrides == null || string.IsNullOrWhiteSpace(normalizedLocalPath))
        {
            return null;
        }

        return target.avatarPathOverrides.FirstOrDefault(entry =>
            entry != null &&
            string.Equals(ResolveLocalTargetPath(target, entry.referenceSourcePath, entry.sourceModelFileId, entry.referenceHash), normalizedLocalPath, StringComparison.OrdinalIgnoreCase));
    }

    public static AvatarPathOverrideEntry UpsertOverride(
        MyCustomBase target,
        ModelFileData sourceFile,
        string localTargetPath,
        string preMcbBackupToken = null,
        string sourceImportKind = null,
        string sourcePackagePath = null)
    {
        if (sourceFile == null) throw new ArgumentNullException(nameof(sourceFile));
        return UpsertOverride(
            target,
            sourceFile.id,
            sourceFile.path,
            sourceFile.hash,
            localTargetPath,
            preMcbBackupToken,
            sourceImportKind,
            sourcePackagePath);
    }

    public static AvatarPathOverrideEntry UpsertOverride(
        MyCustomBase target,
        int sourceModelFileId,
        string referenceSourcePath,
        string referenceHash,
        string localTargetPath,
        string preMcbBackupToken = null,
        string sourceImportKind = null,
        string sourcePackagePath = null)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        EnsureInstanceId(target);
        if (target.avatarPathOverrides == null)
        {
            target.avatarPathOverrides = new List<AvatarPathOverrideEntry>();
        }

        string normalizedReferencePath = NormalizeUnityPath(referenceSourcePath);
        string normalizedLocalTargetPath = NormalizeUnityPath(localTargetPath);
        string normalizedReferenceHash = NormalizeHash(referenceHash);
        if (!MCBUtils.TryResolveProjectAssetPath(normalizedReferencePath, out normalizedReferencePath, out _) ||
            !IsSha256(normalizedReferenceHash))
        {
            throw new ArgumentException("Override source identity requires a safe project path and SHA-256 hash.");
        }
        if (!MCBUtils.TryResolveProjectAssetPath(normalizedLocalTargetPath, out normalizedLocalTargetPath, out string localTargetFullPath) ||
            !File.Exists(localTargetFullPath))
        {
            throw new ArgumentException("Override target must be an existing Unity project asset.", nameof(localTargetPath));
        }
        string localTargetGuid = ResolveAssetGuid(normalizedLocalTargetPath);

        var entry = FindOverride(target, normalizedReferencePath, sourceModelFileId, normalizedReferenceHash);
        if (entry == null)
        {
            entry = new AvatarPathOverrideEntry();
            target.avatarPathOverrides.Add(entry);
        }

        entry.sourceModelFileId = sourceModelFileId;
        entry.referenceSourcePath = normalizedReferencePath;
        entry.referenceHash = normalizedReferenceHash;
        entry.localTargetPath = normalizedLocalTargetPath;
        entry.localTargetGuid = localTargetGuid;
        if (!string.IsNullOrWhiteSpace(preMcbBackupToken)) entry.preMcbBackupToken = preMcbBackupToken;
        if (!string.IsNullOrWhiteSpace(sourceImportKind)) entry.sourceImportKind = sourceImportKind;
        if (!string.IsNullOrWhiteSpace(sourcePackagePath)) entry.sourcePackagePath = Path.GetFileName(sourcePackagePath);

        EditorUtility.SetDirty(target);
        return entry;
    }

    public static List<DiscoveryPathOverridePayload> BuildDiscoveryPayload(MyCustomBase target)
    {
        if (target?.avatarPathOverrides == null)
        {
            return new List<DiscoveryPathOverridePayload>();
        }

        return target.avatarPathOverrides
            .Where(entry => entry != null &&
                            !string.IsNullOrWhiteSpace(entry.referenceSourcePath) &&
                            IsSha256(entry.referenceHash))
            .Select(entry => new DiscoveryPathOverridePayload
            {
                sourceModelFileId = entry.sourceModelFileId,
                referenceSourcePath = NormalizeUnityPath(entry.referenceSourcePath),
                referenceHash = NormalizeHash(entry.referenceHash),
                localTargetPath = ResolveLocalTargetPath(
                    target,
                    entry.referenceSourcePath,
                    entry.sourceModelFileId,
                    entry.referenceHash)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.referenceSourcePath) &&
                            IsSha256(entry.referenceHash) &&
                            !string.IsNullOrWhiteSpace(entry.localTargetPath))
            .ToList();
    }

    public static void SyncOverridesForSelectedAsset(
        MyCustomBase target,
        IEnumerable<ModelFileData> sourceFiles,
        IEnumerable<string> candidateLocalPaths)
    {
        if (target == null || sourceFiles == null || candidateLocalPaths == null)
        {
            return;
        }

        var localInventory = BuildLocalInventory(candidateLocalPaths);
        var sources = sourceFiles
            .Where(sourceFile => sourceFile != null &&
                                 string.Equals(sourceFile.type, "FBX", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(sourceFile.role, "SOURCE", StringComparison.OrdinalIgnoreCase))
            .Select(sourceFile => new AvatarBaseSourceMatcher.SourceFile
            {
                id = sourceFile.id,
                path = NormalizeUnityPath(sourceFile.path),
                hash = NormalizeHash(sourceFile.hash),
                tag = sourceFile
            })
            .ToList();
        var locals = localInventory.Select(entry => new AvatarBaseSourceMatcher.LocalFile
        {
            path = entry.Path,
            hash = entry.Hash,
            tag = entry
        });
        var matches = AvatarBaseSourceMatcher.MatchOneToOne(sources, locals, requireEqualCount: false);
        if (matches == null) return;

        foreach (var match in matches)
        {
            var sourceFile = (ModelFileData)match.source.tag;
            string referencePath = NormalizeUnityPath(sourceFile.path);
            string localPath = NormalizeUnityPath(match.local.path);
            if (string.Equals(localPath, referencePath, StringComparison.OrdinalIgnoreCase)) continue;

            UpsertOverride(target, sourceFile, localPath, null, SourceImportKindDefaultBase, null);
            MCBLogger.Log($"[AvatarPathOverride] Matched renamed default FBX by hash: {referencePath} -> {localPath}");
        }
    }

    public static void SyncSourceModelFileIds(MyCustomBase target, IEnumerable<ModelFileData> sourceFiles)
    {
        if (target?.avatarPathOverrides == null || sourceFiles == null)
        {
            return;
        }

        bool changed = false;
        foreach (var sourceFile in sourceFiles)
        {
            if (sourceFile == null || sourceFile.id <= 0)
            {
                continue;
            }

            var entry = FindOverride(target, sourceFile.path, 0, sourceFile.hash);
            if (entry != null && entry.sourceModelFileId != sourceFile.id)
            {
                entry.sourceModelFileId = sourceFile.id;
                changed = true;
            }
        }

        if (changed)
        {
            EditorUtility.SetDirty(target);
        }
    }

    public static bool HasRestorablePreMcbBackup(MyCustomBase target)
    {
        return GetRestorablePreMcbBackups(target).Count > 0;
    }

    public static int RestorePreMcbBackups(MyCustomBase target)
    {
        var backups = GetRestorablePreMcbBackups(target);
        if (backups.Count == 0) return 0;

        var staged = new List<PreMcbRestoreFile>();
        try
        {
            foreach (var backup in backups)
            {
                string suffix = ".restore-" + Guid.NewGuid().ToString("N");
                backup.rollbackPath = backup.targetFullPath + suffix + ".rollback";
                backup.replacementPath = backup.targetFullPath + suffix + ".replacement";
                staged.Add(backup);
                File.Copy(backup.targetFullPath, backup.rollbackPath, false);
                File.Copy(backup.backupFullPath, backup.replacementPath, false);
                if (!string.Equals(
                        MCBUtils.CalculateFileHash(backup.backupFullPath),
                        MCBUtils.CalculateFileHash(backup.replacementPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Pre-MCB backup staging failed for '{backup.targetUnityPath}'.");
                }
            }

            foreach (var backup in staged)
            {
                File.Copy(backup.replacementPath, backup.targetFullPath, true);
                AssetDatabase.ImportAsset(
                    backup.targetUnityPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return staged.Count;
        }
        catch
        {
            foreach (var backup in staged.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(backup.rollbackPath))
                    {
                        File.Copy(backup.rollbackPath, backup.targetFullPath, true);
                        AssetDatabase.ImportAsset(
                            backup.targetUnityPath,
                            ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    }
                }
                catch { }
            }
            throw;
        }
        finally
        {
            foreach (var backup in staged)
            {
                try
                {
                    if (File.Exists(backup.rollbackPath)) File.Delete(backup.rollbackPath);
                    if (File.Exists(backup.replacementPath)) File.Delete(backup.replacementPath);
                }
                catch { }
            }
        }
    }

    private static List<PreMcbRestoreFile> GetRestorablePreMcbBackups(MyCustomBase target)
    {
        var result = new List<PreMcbRestoreFile>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in target?.avatarPathOverrides ?? Enumerable.Empty<AvatarPathOverrideEntry>())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.preMcbBackupToken)) continue;
            string targetPath = ResolveLocalTargetPath(
                target,
                entry.referenceSourcePath,
                entry.sourceModelFileId,
                entry.referenceHash);
            if (!MCBUtils.TryResolveProjectAssetPath(targetPath, out string targetUnityPath, out string targetFullPath) ||
                !File.Exists(targetFullPath) ||
                !seenTargets.Add(targetUnityPath))
            {
                continue;
            }

            string backupUnityPath = FileManagerService.GetPreMcbBackupPath(targetUnityPath, entry.preMcbBackupToken);
            if (!MCBUtils.TryResolveProjectAssetPath(backupUnityPath, out _, out string backupFullPath) ||
                !File.Exists(backupFullPath))
            {
                continue;
            }

            result.Add(new PreMcbRestoreFile
            {
                targetUnityPath = targetUnityPath,
                targetFullPath = targetFullPath,
                backupFullPath = backupFullPath
            });
        }
        return result;
    }

    private static List<LocalFbxInventoryEntry> BuildLocalInventory(IEnumerable<string> candidateLocalPaths)
    {
        return (candidateLocalPaths ?? Enumerable.Empty<string>())
            .Select(NormalizeUnityPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new LocalFbxInventoryEntry
            {
                Path = path,
                Hash = CalculateHash(path)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Hash))
            .ToList();
    }

    private static string ResolveGuidPath(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return null;
        return NormalizeUnityPath(AssetDatabase.GUIDToAssetPath(guid));
    }

    private static string ResolveAssetGuid(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath)) return null;
        string guid = AssetDatabase.AssetPathToGUID(unityPath);
        return string.IsNullOrWhiteSpace(guid) ? null : guid;
    }

    private static bool ProjectFileExists(string unityPath)
    {
        return MCBUtils.TryResolveProjectAssetPath(unityPath, out _, out string fullPath) && File.Exists(fullPath);
    }

    private static string CalculateHash(string unityPath)
    {
        if (!ProjectFileExists(unityPath)) return null;
        try
        {
            return MCBUtils.CalculateFileHash(MCBUtils.ResolveProjectAssetFullPath(unityPath));
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeHash(string hash)
    {
        return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().ToLowerInvariant();
    }

    private static bool IsSha256(string hash)
    {
        string normalized = NormalizeHash(hash);
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Length == 64 &&
               normalized.All(Uri.IsHexDigit);
    }

    private static bool IdentityHashMatches(AvatarPathOverrideEntry entry, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return true;
        return string.Equals(NormalizeHash(entry?.referenceHash), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdentityPathMatchesWhenProvided(AvatarPathOverrideEntry entry, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath)) return true;
        return string.Equals(NormalizeUnityPath(entry?.referenceSourcePath), expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMigrateManagedSidecars(
        string oldLocalTargetPath,
        string newLocalTargetPath,
        AvatarPathOverrideEntry entry,
        out string error)
    {
        error = null;
        string oldPath = NormalizeUnityPath(oldLocalTargetPath);
        string newPath = NormalizeUnityPath(newLocalTargetPath);
        if (string.IsNullOrWhiteSpace(oldPath) ||
            string.IsNullOrWhiteSpace(newPath) ||
            string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var moves = new List<ManagedSidecarMove>();
        if (!TryPrepareManagedSidecarMove(
                FileManagerService.GetOriginalBasePath(oldPath),
                FileManagerService.GetOriginalBasePath(newPath),
                moves,
                out error)) return false;

        if (!string.IsNullOrWhiteSpace(entry?.preMcbBackupToken) &&
            !TryPrepareManagedSidecarMove(
                FileManagerService.GetPreMcbBackupPath(oldPath, entry.preMcbBackupToken),
                FileManagerService.GetPreMcbBackupPath(newPath, entry.preMcbBackupToken),
                moves,
                out error)) return false;

        var completedMoves = new List<ManagedSidecarMove>();
        try
        {
            foreach (var move in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.newFullPath));
                completedMoves.Add(move);
                File.Move(move.oldFullPath, move.newFullPath);
                if (File.Exists(move.oldMetaPath)) File.Move(move.oldMetaPath, move.newMetaPath);
            }
        }
        catch (Exception ex)
        {
            foreach (var move in completedMoves.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(move.newMetaPath) && !File.Exists(move.oldMetaPath)) File.Move(move.newMetaPath, move.oldMetaPath);
                    if (File.Exists(move.newFullPath) && !File.Exists(move.oldFullPath)) File.Move(move.newFullPath, move.oldFullPath);
                }
                catch { }
            }
            error = ex.Message;
            return false;
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        return true;
    }

    private static bool TryPrepareManagedSidecarMove(
        string oldUnityPath,
        string newUnityPath,
        List<ManagedSidecarMove> moves,
        out string error)
    {
        error = null;
        if (!MCBUtils.TryResolveProjectAssetPath(oldUnityPath, out _, out string oldFullPath) ||
            !MCBUtils.TryResolveProjectAssetPath(newUnityPath, out _, out string newFullPath))
        {
            error = $"Unsafe sidecar path '{oldUnityPath}' or '{newUnityPath}'.";
            return false;
        }
        if (!File.Exists(oldFullPath)) return true;

        if (File.Exists(newFullPath))
        {
            string oldHash = MCBUtils.CalculateFileHash(oldFullPath);
            string newHash = MCBUtils.CalculateFileHash(newFullPath);
            if (!string.Equals(oldHash, newHash, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Destination sidecar already exists with different content: {newUnityPath}";
                return false;
            }
            return true;
        }

        string oldMeta = oldFullPath + ".meta";
        string newMeta = newFullPath + ".meta";
        if (File.Exists(oldMeta) && File.Exists(newMeta))
        {
            error = $"Destination sidecar metadata already exists: {newUnityPath}.meta";
            return false;
        }

        moves.Add(new ManagedSidecarMove
        {
            oldFullPath = oldFullPath,
            newFullPath = newFullPath,
            oldMetaPath = oldMeta,
            newMetaPath = newMeta
        });
        return true;
    }

    private sealed class ManagedSidecarMove
    {
        public string oldFullPath;
        public string newFullPath;
        public string oldMetaPath;
        public string newMetaPath;
    }

    private sealed class PreMcbRestoreFile
    {
        public string targetUnityPath;
        public string targetFullPath;
        public string backupFullPath;
        public string rollbackPath;
        public string replacementPath;
    }

    private sealed class LocalFbxInventoryEntry
    {
        public string Path;
        public string Hash;
    }
}
#endif
