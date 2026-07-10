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
    public const string MetadataMcbInstanceId = "mcbInstanceId";
    public const string MetadataReferenceSourcePath = "referenceSourcePath";
    public const string MetadataReferenceHash = "referenceHash";
    public const string MetadataLocalTargetPath = "localTargetPath";
    public const string MetadataLocalTargetGuid = "localTargetGuid";
    public const string MetadataPreMcbBackupToken = "preMcbBackupToken";
    public const string MetadataSourceImportKind = "sourceImportKind";
    public const string MetadataSourcePackagePath = "sourcePackagePath";

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class DiscoveryPathOverridePayload
    {
        [JsonProperty] public int sourceModelFileId;
        [JsonProperty] public string referenceSourcePath;
        [JsonProperty] public string referenceHash;
        [JsonProperty] public string localTargetPath;
        [JsonProperty] public string localTargetGuid;
        [JsonProperty] public string mcbInstanceId;
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
                    entry.localTargetPath = guidPath;
                    EditorUtility.SetDirty(target);
                }

                return guidPath;
            }
        }

        return NormalizeUnityPath(referenceSourcePath);
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

        if (sourceModelFileId > 0)
        {
            var byId = target.avatarPathOverrides.FirstOrDefault(entry =>
                entry != null && entry.sourceModelFileId == sourceModelFileId);
            if (byId != null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedReferencePath))
        {
            var byPath = target.avatarPathOverrides.FirstOrDefault(entry =>
                entry != null &&
                string.Equals(NormalizeUnityPath(entry.referenceSourcePath), normalizedReferencePath, StringComparison.OrdinalIgnoreCase));
            if (byPath != null)
            {
                return byPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedReferenceHash))
        {
            return target.avatarPathOverrides.FirstOrDefault(entry =>
                entry != null &&
                string.Equals(NormalizeHash(entry.referenceHash), normalizedReferenceHash, StringComparison.OrdinalIgnoreCase));
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
        string localTargetGuid = ResolveAssetGuid(normalizedLocalTargetPath);

        var entry = FindOverride(target, normalizedReferencePath, sourceModelFileId, referenceHash);
        if (entry == null)
        {
            entry = new AvatarPathOverrideEntry();
            target.avatarPathOverrides.Add(entry);
        }

        entry.sourceModelFileId = sourceModelFileId;
        entry.referenceSourcePath = normalizedReferencePath;
        entry.referenceHash = NormalizeHash(referenceHash);
        entry.localTargetPath = normalizedLocalTargetPath;
        entry.localTargetGuid = localTargetGuid;
        if (!string.IsNullOrWhiteSpace(preMcbBackupToken)) entry.preMcbBackupToken = preMcbBackupToken;
        if (!string.IsNullOrWhiteSpace(sourceImportKind)) entry.sourceImportKind = sourceImportKind;
        if (!string.IsNullOrWhiteSpace(sourcePackagePath)) entry.sourcePackagePath = Path.GetFileName(sourcePackagePath);

        EditorUtility.SetDirty(target);
        return entry;
    }

    public static void ApplyOverrideMetadata(ModelFileData file, AvatarPathOverrideEntry entry, MyCustomBase target)
    {
        if (file == null || entry == null) return;
        if (file.metadata == null)
        {
            file.metadata = new Dictionary<string, object>();
        }

        if (target != null)
        {
            EnsureInstanceId(target);
            file.metadata[MetadataMcbInstanceId] = target.mcbInstanceId;
        }

        PutIfNotEmpty(file.metadata, MetadataReferenceSourcePath, entry.referenceSourcePath);
        PutIfNotEmpty(file.metadata, MetadataReferenceHash, entry.referenceHash);
        PutIfNotEmpty(file.metadata, MetadataLocalTargetPath, entry.localTargetPath);
        PutIfNotEmpty(file.metadata, MetadataLocalTargetGuid, entry.localTargetGuid);
        PutIfNotEmpty(file.metadata, MetadataPreMcbBackupToken, entry.preMcbBackupToken);
        PutIfNotEmpty(file.metadata, MetadataSourceImportKind, entry.sourceImportKind);
        PutIfNotEmpty(file.metadata, MetadataSourcePackagePath, entry.sourcePackagePath);
    }

    public static List<DiscoveryPathOverridePayload> BuildDiscoveryPayload(MyCustomBase target)
    {
        if (target?.avatarPathOverrides == null)
        {
            return new List<DiscoveryPathOverridePayload>();
        }

        EnsureInstanceId(target);
        return target.avatarPathOverrides
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.referenceSourcePath))
            .Select(entry => new DiscoveryPathOverridePayload
            {
                sourceModelFileId = entry.sourceModelFileId,
                referenceSourcePath = NormalizeUnityPath(entry.referenceSourcePath),
                referenceHash = NormalizeHash(entry.referenceHash),
                localTargetPath = ResolveLocalTargetPath(
                    target,
                    entry.referenceSourcePath,
                    entry.sourceModelFileId,
                    entry.referenceHash),
                localTargetGuid = entry.localTargetGuid,
                mcbInstanceId = target.mcbInstanceId
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.referenceSourcePath) &&
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

        EnsureInstanceId(target);
        var localInventory = BuildLocalInventory(candidateLocalPaths);
        foreach (var sourceFile in sourceFiles)
        {
            if (sourceFile == null ||
                !string.Equals(sourceFile.type, "FBX", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sourceFile.role, "SOURCE", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(sourceFile.path) ||
                string.IsNullOrWhiteSpace(sourceFile.hash))
            {
                continue;
            }

            string referencePath = NormalizeUnityPath(sourceFile.path);
            if (ProjectFileExists(referencePath))
            {
                string exactHash = CalculateHash(referencePath);
                if (string.Equals(exactHash, sourceFile.hash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var matches = localInventory
                .Where(entry => string.Equals(entry.Hash, sourceFile.hash, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                continue;
            }

            var match = matches[0];
            if (string.Equals(match.Path, referencePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            UpsertOverride(
                target,
                sourceFile,
                match.Path,
                null,
                SourceImportKindDefaultBase,
                null);
            MCBLogger.Log($"[AvatarPathOverride] Matched renamed default FBX by hash: {referencePath} -> {match.Path}");
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
        if (string.IsNullOrWhiteSpace(unityPath)) return false;
        try
        {
            return File.Exists(Path.GetFullPath(unityPath));
        }
        catch
        {
            return false;
        }
    }

    private static string CalculateHash(string unityPath)
    {
        if (!ProjectFileExists(unityPath)) return null;
        try
        {
            return MCBUtils.CalculateFileHash(Path.GetFullPath(unityPath));
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

    private static void PutIfNotEmpty(Dictionary<string, object> metadata, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value;
        }
    }

    private sealed class LocalFbxInventoryEntry
    {
        public string Path;
        public string Hash;
    }
}
#endif
