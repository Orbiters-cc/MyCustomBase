#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

public sealed class CustomBaseSourceSetupTransaction : IDisposable
{
    public sealed class SourceKeyInstallRequest
    {
        public string referenceSourcePath;
        public string referenceHash;
        public string localTargetPath;
        public string externalSourcePath;
        public string sourceImportKind;
        public string sourcePackagePath;
    }

    private sealed class CreatedBackup
    {
        public string targetUnityPath;
        public string token;
        public string backupFullPath;
    }

    private readonly MyCustomBase target;
    private readonly FileManagerService fileManager = new FileManagerService();
    private readonly string originalInstanceId;
    private readonly List<AvatarPathOverrideEntry> originalOverrides;
    private readonly List<string> createdOriginalBasePaths = new List<string>();
    private readonly List<CreatedBackup> createdBackups = new List<CreatedBackup>();
    private bool completed;
    private bool rolledBack;

    public CustomBaseSourceSetupTransaction(MyCustomBase target)
    {
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        originalInstanceId = target.mcbInstanceId;
        originalOverrides = CloneOverrides(target.avatarPathOverrides);
    }

    public void CommitAlreadyCustomized(
        IEnumerable<ModelFileData> canonicalSourceFiles,
        IEnumerable<SourceKeyInstallRequest> installRequests)
    {
        var sources = NormalizeCanonicalSources(canonicalSourceFiles);
        var requests = (installRequests ?? Enumerable.Empty<SourceKeyInstallRequest>()).Where(request => request != null).ToList();
        var matches = AvatarBaseSourceMatcher.MatchOneToOne(
            sources.Select(source => new AvatarBaseSourceMatcher.SourceFile
            {
                id = source.id,
                path = source.path,
                hash = source.hash,
                tag = source
            }),
            requests.Select(request => new AvatarBaseSourceMatcher.LocalFile
            {
                path = request.referenceSourcePath,
                hash = request.referenceHash,
                tag = request
            }));
        if (matches == null)
        {
            throw new InvalidOperationException("The server source files no longer match the mapped original/default FBX files.");
        }

        var prepared = matches.Select(match => new
        {
            source = (ModelFileData)match.source.tag,
            request = (SourceKeyInstallRequest)match.local.tag
        }).ToList();
        PreflightAlreadyCustomized(prepared.Select(entry => entry.request));

        try
        {
            foreach (var entry in prepared)
            {
                string backupToken = ResolveOrCreateBackupToken(entry.source, entry.request.localTargetPath);
                bool createdKey = fileManager.EnsureOriginalBaseKey(
                    entry.request.localTargetPath,
                    entry.request.externalSourcePath,
                    entry.source.hash);
                if (createdKey)
                {
                    createdOriginalBasePaths.Add(MCBUtils.ResolveProjectAssetFullPath(
                        FileManagerService.GetOriginalBasePath(entry.request.localTargetPath)));
                }

                AvatarPathOverrideService.UpsertOverride(
                    target,
                    entry.source.id,
                    entry.source.path,
                    entry.source.hash,
                    entry.request.localTargetPath,
                    backupToken,
                    entry.request.sourceImportKind,
                    entry.request.sourcePackagePath);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    public void CommitDefaultBase(
        IEnumerable<ModelFileData> canonicalSourceFiles,
        IEnumerable<string> localTargetPaths)
    {
        var sources = NormalizeCanonicalSources(canonicalSourceFiles);
        var locals = BuildLocalInventory(localTargetPaths);
        var matches = AvatarBaseSourceMatcher.MatchOneToOne(
            sources.Select(source => new AvatarBaseSourceMatcher.SourceFile
            {
                id = source.id,
                path = source.path,
                hash = source.hash,
                tag = source
            }),
            locals);
        if (matches == null)
        {
            throw new InvalidOperationException("The current target FBX files do not match the selected default avatar base.");
        }

        try
        {
            foreach (var match in matches)
            {
                var source = (ModelFileData)match.source.tag;
                string localPath = match.local.path;
                bool createdKey = fileManager.EnsureOriginalBaseKey(
                    localPath,
                    MCBUtils.ResolveProjectAssetFullPath(localPath),
                    source.hash);
                if (createdKey)
                {
                    createdOriginalBasePaths.Add(MCBUtils.ResolveProjectAssetFullPath(
                        FileManagerService.GetOriginalBasePath(localPath)));
                }
                if (string.Equals(
                        AvatarPathOverrideService.NormalizeUnityPath(source.path),
                        AvatarPathOverrideService.NormalizeUnityPath(localPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AvatarPathOverrideService.UpsertOverride(
                    target,
                    source,
                    localPath,
                    null,
                    AvatarPathOverrideService.SourceImportKindDefaultBase,
                    null);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    public void Complete()
    {
        if (rolledBack) throw new InvalidOperationException("Cannot complete a rolled-back source setup transaction.");
        completed = true;
    }

    public void Dispose()
    {
        if (!completed) Rollback();
    }

    private static List<ModelFileData> NormalizeCanonicalSources(IEnumerable<ModelFileData> sourceFiles)
    {
        var supplied = (sourceFiles ?? Enumerable.Empty<ModelFileData>())
            .Where(source => source != null)
            .ToList();
        var result = supplied
            .Where(source => source != null &&
                             string.Equals(source.type, "FBX", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(source.role, "SOURCE", StringComparison.OrdinalIgnoreCase) &&
                             MCBUtils.TryResolveProjectAssetPath(source.path, out _, out _) &&
                             IsSha256(source.hash))
            .ToList();
        if (result.Count == 0 || result.Count != supplied.Count)
        {
            throw new InvalidOperationException("The server did not return canonical source FBX files.");
        }
        return result;
    }

    private static List<AvatarBaseSourceMatcher.LocalFile> BuildLocalInventory(IEnumerable<string> localTargetPaths)
    {
        var result = new List<AvatarBaseSourceMatcher.LocalFile>();
        foreach (string path in (localTargetPaths ?? Enumerable.Empty<string>())
                     .Select(AvatarPathOverrideService.NormalizeUnityPath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = MCBUtils.ResolveProjectAssetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Target FBX file was not found.", fullPath);
            result.Add(new AvatarBaseSourceMatcher.LocalFile
            {
                path = path,
                hash = MCBUtils.CalculateFileHash(fullPath)
            });
        }
        return result;
    }

    private static void PreflightAlreadyCustomized(IEnumerable<SourceKeyInstallRequest> requests)
    {
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            if (request == null ||
                !MCBUtils.TryResolveProjectAssetPath(request.localTargetPath, out string normalizedTarget, out string targetFullPath) ||
                !File.Exists(targetFullPath))
            {
                throw new InvalidOperationException($"Invalid or missing target FBX: {request?.localTargetPath}");
            }
            if (!seenTargets.Add(normalizedTarget))
            {
                throw new InvalidOperationException($"More than one source file maps to target FBX '{normalizedTarget}'.");
            }

            string externalPath = Path.GetFullPath(request.externalSourcePath ?? string.Empty);
            if (!File.Exists(externalPath) || !IsSha256(request.referenceHash))
            {
                throw new InvalidOperationException($"Invalid original/default source FBX for '{normalizedTarget}'.");
            }
            string actualHash = MCBUtils.CalculateFileHash(externalPath);
            if (!string.Equals(actualHash, request.referenceHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Original/default source FBX changed after mapping for '{normalizedTarget}'.");
            }

            string originalBaseFullPath = MCBUtils.ResolveProjectAssetFullPath(FileManagerService.GetOriginalBasePath(normalizedTarget));
            if (File.Exists(originalBaseFullPath))
            {
                string existingHash = MCBUtils.CalculateFileHash(originalBaseFullPath);
                if (!string.Equals(existingHash, request.referenceHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Existing original-base key has different content: {FileManagerService.GetOriginalBasePath(normalizedTarget)}");
                }
            }
        }
    }

    private string ResolveOrCreateBackupToken(ModelFileData source, string localTargetPath)
    {
        var existing = AvatarPathOverrideService.FindOverride(target, source);
        if (!string.IsNullOrWhiteSpace(existing?.preMcbBackupToken))
        {
            string existingBackup = MCBUtils.ResolveProjectAssetFullPath(
                FileManagerService.GetPreMcbBackupPath(localTargetPath, existing.preMcbBackupToken));
            if (File.Exists(existingBackup)) return existing.preMcbBackupToken;
        }

        string targetFullPath = MCBUtils.ResolveProjectAssetFullPath(localTargetPath);
        string token = fileManager.CreatePreMcbBackup(targetFullPath);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new IOException($"Could not create pre-MCB backup for '{localTargetPath}'.");
        }

        createdBackups.Add(new CreatedBackup
        {
            targetUnityPath = localTargetPath,
            token = token,
            backupFullPath = FileManagerService.GetPreMcbBackupPath(targetFullPath, token)
        });
        return token;
    }

    private void Rollback()
    {
        if (rolledBack) return;
        rolledBack = true;

        foreach (var backup in createdBackups.AsEnumerable().Reverse())
        {
            try
            {
                if (File.Exists(backup.backupFullPath))
                {
                    fileManager.RestorePreMcbBackup(backup.targetUnityPath, backup.token);
                    File.Delete(backup.backupFullPath);
                    if (File.Exists(backup.backupFullPath + ".meta")) File.Delete(backup.backupFullPath + ".meta");
                }
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[CustomBaseSourceSetup] Failed to restore pre-MCB backup: {ex.Message}");
            }
        }

        foreach (string path in createdOriginalBasePaths.AsEnumerable().Reverse())
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[CustomBaseSourceSetup] Failed to remove staged original-base key: {ex.Message}");
            }
        }

        target.mcbInstanceId = originalInstanceId;
        target.avatarPathOverrides = CloneOverrides(originalOverrides);
        EditorUtility.SetDirty(target);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }

    private static List<AvatarPathOverrideEntry> CloneOverrides(IEnumerable<AvatarPathOverrideEntry> source)
    {
        return (source ?? Enumerable.Empty<AvatarPathOverrideEntry>())
            .Where(entry => entry != null)
            .Select(entry => new AvatarPathOverrideEntry
            {
                sourceModelFileId = entry.sourceModelFileId,
                referenceSourcePath = entry.referenceSourcePath,
                referenceHash = entry.referenceHash,
                localTargetPath = entry.localTargetPath,
                localTargetGuid = entry.localTargetGuid,
                preMcbBackupToken = entry.preMcbBackupToken,
                sourceImportKind = entry.sourceImportKind,
                sourcePackagePath = entry.sourcePackagePath
            })
            .ToList();
    }

    private static bool IsSha256(string hash)
    {
        return !string.IsNullOrWhiteSpace(hash) && hash.Trim().Length == 64 && hash.Trim().All(Uri.IsHexDigit);
    }
}
#endif
