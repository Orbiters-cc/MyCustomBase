#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEditor;

/// <summary>
/// Owner of local version-storage lifecycle: scanning/classifying version folders,
/// atomic build commits (staging + manifest-last + trash-swap), deletion, publish
/// state, flushing removable data, and the one-time migration of the legacy central
/// unsubmitted index.
///
/// Path construction itself stays in MCBUtils.GetVersionDataPath (the single
/// pre-existing path builder); this class owns every WRITE/MOVE/DELETE of version
/// folders. Read-only consumers resolve files via VersionArtifact / MCBUtils helpers.
///
/// Persistence model:
/// - version.json (wire-format CustomBaseVersion) inside each version folder is the
///   metadata store for unsubmitted and imported versions alike.
/// - manifest.json marks a folder as a *built artifact*; manifest.unsubmitted carries
///   the unsubmitted/published state (CustomBaseVersion.isUnsubmitted is [JsonIgnore]).
/// - The legacy central Assets/MCB/unsubmittedVersions.json is migrated once into the
///   folders, then deleted. No ongoing legacy support (project no-retrocompat rule).
/// </summary>
public static class VersionRepository
{
    private const string BuildingInfix = ".building-";
    private const string TrashInfix = ".trash-";
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly string[] LocalOnlyFileNames =
    {
        "version.json", "version.json.meta",
        VersionManifest.FileName, VersionManifest.FileName + ".meta"
    };

    private static List<CustomBaseVersion> cachedImported;
    private static List<CustomBaseVersion> cachedUnsubmitted;
    private static DateTime cachedScanAtUtc = DateTime.MinValue;
    private static bool migrationChecked;

    private static readonly HashSet<string> inProgressFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ in-progress guard

    /// <summary>Registers a version folder as busy (building/publishing/applying) so it is
    /// excluded from flush, delete and garbage collection. Dispose to release.</summary>
    public static IDisposable BeginOperation(CustomBaseVersion version)
    {
        string folder = MCBUtils.GetVersionDataPath(version);
        return BeginOperationOnFolder(folder);
    }

    public static IDisposable BeginOperationOnFolder(string folderUnityPath)
    {
        string key = NormalizeFullPath(folderUnityPath);
        if (key != null)
        {
            lock (inProgressFolders)
            {
                if (inProgressFolders.Contains(key))
                {
                    throw new InvalidOperationException("This version is already being built, published, applied, deleted, or flushed. Wait for that operation to finish and try again.");
                }

                inProgressFolders.Add(key);
            }
        }

        return new OperationScope(key);
    }

    public static bool IsInProgress(string folderUnityOrFullPath)
    {
        string key = NormalizeFullPath(folderUnityOrFullPath);
        if (key == null) return false;
        lock (inProgressFolders) { return inProgressFolders.Contains(key); }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly string key;
        private bool disposed;
        public OperationScope(string key) { this.key = key; }

        public void Dispose()
        {
            if (disposed || key == null) return;
            disposed = true;
            lock (inProgressFolders) { inProgressFolders.Remove(key); }
        }
    }

    private static string NormalizeFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ scan

    public class ScanResult
    {
        public List<CustomBaseVersion> imported = new List<CustomBaseVersion>();
        public List<CustomBaseVersion> unsubmitted = new List<CustomBaseVersion>();
    }

    /// <summary>
    /// Enumerates all local version folders (version.json scan) and classifies them as
    /// unsubmitted artifacts (manifest.unsubmitted == true) or imported versions.
    /// Also runs the one-time legacy migration and garbage-collects stale
    /// .building-* / .trash-* folders.
    /// </summary>
    public static ScanResult Scan(bool forceRefresh = false)
    {
        if (!forceRefresh &&
            cachedScanAtUtc != DateTime.MinValue &&
            DateTime.UtcNow - cachedScanAtUtc < ScanCacheDuration &&
            cachedImported != null && cachedUnsubmitted != null)
        {
            return new ScanResult
            {
                imported = new List<CustomBaseVersion>(cachedImported),
                unsubmitted = new List<CustomBaseVersion>(cachedUnsubmitted)
            };
        }

        MigrateLegacyUnsubmittedIndexOnce();
        CollectGarbage();

        var result = new ScanResult();
        string versionsRoot = Path.GetFullPath(MCBUtils.ASSET_VERSIONS_FOLDER);
        if (Directory.Exists(versionsRoot))
        {
            try
            {
                foreach (string versionJsonPath in Directory.GetFiles(versionsRoot, "version.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        string folderFullPath = Path.GetDirectoryName(versionJsonPath);
                        string folderName = Path.GetFileName(folderFullPath ?? string.Empty);
                        if (folderName.Contains(BuildingInfix) || folderName.Contains(TrashInfix))
                        {
                            continue; // incomplete or trashed artifact, never listed
                        }

                        var version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(versionJsonPath));
                        if (version == null || version.assetId <= 0 ||
                            string.IsNullOrWhiteSpace(version.version) ||
                            string.IsNullOrWhiteSpace(version.defaultAviVersion))
                        {
                            MCBLogger.LogWarning($"[VersionRepository] Ignoring invalid version metadata at {versionJsonPath}");
                            continue;
                        }

                        var manifest = VersionManifest.Load(MCBUtils.ToUnityPath(folderFullPath));
                        if (manifest != null && manifest.unsubmitted)
                        {
                            version.isUnsubmitted = true;
                            result.unsubmitted.Add(version);
                        }
                        else
                        {
                            version.isImported = true;
                            result.imported.Add(version);
                        }
                    }
                    catch (Exception ex)
                    {
                        MCBLogger.LogWarning($"[VersionRepository] Failed to parse version metadata at {versionJsonPath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[VersionRepository] Failed to scan version folders: {ex.Message}");
            }
        }

        cachedImported = new List<CustomBaseVersion>(result.imported);
        cachedUnsubmitted = new List<CustomBaseVersion>(result.unsubmitted);
        cachedScanAtUtc = DateTime.UtcNow;
        return result;
    }

    public static void InvalidateCache()
    {
        cachedScanAtUtc = DateTime.MinValue;
        cachedImported = null;
        cachedUnsubmitted = null;
    }

    // ------------------------------------------------------------------ artifact access

    public static VersionArtifact GetArtifact(CustomBaseVersion version)
    {
        string folder = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var metadata = LoadVersionJson(folder) ?? version;
        metadata.isUnsubmitted = version.isUnsubmitted;
        var manifest = VersionManifest.Load(folder);
        return new VersionArtifact(folder, metadata, manifest);
    }

    /// <summary>Validates outputs (hard publish gate) then inputs (drift warning).</summary>
    public static ArtifactValidationResult Validate(VersionArtifact artifact)
    {
        var result = new ArtifactValidationResult();
        if (artifact == null || !artifact.FolderExists)
        {
            result.state = ArtifactState.Corrupt;
            return result;
        }

        if (artifact.Manifest == null)
        {
            result.state = ArtifactState.NoManifest;
            return result;
        }

        string folderFullPath = Path.GetFullPath(artifact.FolderUnityPath);
        foreach (var output in artifact.Manifest.outputs ?? new List<VersionManifestFile>())
        {
            if (output == null || string.IsNullOrWhiteSpace(output.path)) continue;
            string fullPath = Path.Combine(folderFullPath, output.path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                result.missingOutputs.Add(output.path);
            }
            else if (!string.Equals(MCBUtils.CalculateFileHash(fullPath), output.hash, StringComparison.OrdinalIgnoreCase))
            {
                result.modifiedOutputs.Add(output.path);
            }
        }

        if (result.missingOutputs.Count > 0)
        {
            result.state = ArtifactState.OutputsMissing;
            return result;
        }

        if (result.modifiedOutputs.Count > 0)
        {
            result.state = ArtifactState.OutputsModified;
            return result;
        }

        foreach (var input in artifact.Manifest.inputs ?? new List<VersionManifestInput>())
        {
            if (input == null || string.IsNullOrWhiteSpace(input.path)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(MCBUtils.ToUnityPath(input.path)); }
            catch { result.driftedInputs.Add(input.path); continue; }

            if (!File.Exists(fullPath) ||
                !string.Equals(MCBUtils.CalculateFileHash(fullPath), input.hash, StringComparison.OrdinalIgnoreCase))
            {
                result.driftedInputs.Add(input.path);
            }
        }

        result.state = result.driftedInputs.Count > 0 ? ArtifactState.SourceDrift : ArtifactState.Valid;
        return result;
    }

    // ------------------------------------------------------------------ staging + commit

    /// <summary>
    /// Creates a same-parent staging folder ("u{ver}d{avi}.building-{guid}") for a build.
    /// The folder is AssetDatabase-importable on purpose: the packaging pipeline uses
    /// AssetDatabase.CopyAsset / asset generation, which do not work in hidden folders.
    /// </summary>
    public static string CreateStagingFolder(int assetId, string version, string defaultAviVersion)
    {
        string finalPath = MCBUtils.GetVersionDataPath(assetId, version, defaultAviVersion);
        if (string.IsNullOrEmpty(finalPath))
        {
            throw new ArgumentException("A valid assetId, version, and base FBX version are required to create a version folder.");
        }

        string staging = $"{finalPath}{BuildingInfix}{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        string finalKey = NormalizeFullPath(finalPath);
        string stagingKey = NormalizeFullPath(staging);

        lock (inProgressFolders)
        {
            if (finalKey == null || stagingKey == null)
            {
                throw new InvalidOperationException("Version folder path could not be normalized.");
            }

            if (inProgressFolders.Contains(finalKey))
            {
                throw new InvalidOperationException($"Version {version} is already being built, published, applied, deleted, or flushed.");
            }

            if (inProgressFolders.Contains(stagingKey))
            {
                throw new InvalidOperationException($"Staging folder is already in use: {staging}");
            }

            inProgressFolders.Add(stagingKey);
            inProgressFolders.Add(finalKey);
        }

        try
        {
            MCBUtils.EnsureDirectoryExists(staging, canBeFilePath: false);
        }
        catch
        {
            lock (inProgressFolders)
            {
                inProgressFolders.Remove(stagingKey);
                inProgressFolders.Remove(finalKey);
            }

            throw;
        }

        return staging;
    }

    /// <summary>
    /// Atomically replaces the final version folder with the staging folder:
    /// version.json then manifest.json (commit marker, written LAST) are written into
    /// staging, the previous folder (if any) is moved aside to a .trash-* sibling, the
    /// staging folder is moved onto the final name, and the trash is deleted only after
    /// the new folder is in place. A crash at any point leaves either the old valid
    /// artifact or both — never zero — and stale folders are GC'd on the next scan.
    /// </summary>
    public static VersionArtifact CommitStaging(string stagingUnityPath, CustomBaseVersion metadata, VersionManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(stagingUnityPath)) throw new ArgumentNullException(nameof(stagingUnityPath));
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));

        string finalUnityPath = MCBUtils.GetVersionDataPath(metadata);
        if (string.IsNullOrEmpty(finalUnityPath))
        {
            throw new ArgumentException("Version metadata does not resolve to a version folder path.");
        }

        SaveVersionJson(stagingUnityPath, metadata);
        manifest.Save(stagingUnityPath); // commit marker — always last

        string stagingFull = Path.GetFullPath(stagingUnityPath);
        string finalFull = Path.GetFullPath(finalUnityPath);
        string trashFull = $"{finalFull}{TrashInfix}{Guid.NewGuid().ToString("N").Substring(0, 8)}";

        bool oldMovedToTrash = false;
        try
        {
            if (Directory.Exists(finalFull))
            {
                Directory.Move(finalFull, trashFull);
                oldMovedToTrash = true;
            }

            Directory.Move(stagingFull, finalFull);
        }
        catch
        {
            // Never destroy the last valid artifact: put the old folder back if the
            // replacement could not be completed.
            if (oldMovedToTrash && !Directory.Exists(finalFull) && Directory.Exists(trashFull))
            {
                try { Directory.Move(trashFull, finalFull); }
                catch (Exception restoreEx)
                {
                    MCBLogger.LogError($"[VersionRepository] Could not restore previous version folder from trash: {restoreEx.Message}. It remains at {trashFull}.");
                }
            }

            throw;
        }
        finally
        {
            lock (inProgressFolders)
            {
                inProgressFolders.Remove(NormalizeFullPath(stagingUnityPath));
            }
        }

        // The new folder is committed; everything below is cleanup.
        TryDeleteDirectory(trashFull);
        TryDeleteFile(stagingFull + ".meta"); // orphan meta of the staging folder name

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        InvalidateCache();

        return new VersionArtifact(finalUnityPath, metadata, manifest);
    }

    /// <summary>Deletes an abandoned staging folder after a failed build.</summary>
    public static void DeleteStaging(string stagingUnityPath)
    {
        if (string.IsNullOrWhiteSpace(stagingUnityPath)) return;
        string full = Path.GetFullPath(stagingUnityPath);
        TryDeleteDirectory(full);
        TryDeleteFile(full + ".meta");
        lock (inProgressFolders)
        {
            inProgressFolders.Remove(NormalizeFullPath(stagingUnityPath));
        }
    }

    /// <summary>Releases the in-progress guard a build placed on the final folder path.</summary>
    public static void ReleaseFolderGuard(int assetId, string version, string defaultAviVersion)
    {
        string finalPath = MCBUtils.GetVersionDataPath(assetId, version, defaultAviVersion);
        string key = NormalizeFullPath(finalPath);
        if (key == null) return;
        lock (inProgressFolders) { inProgressFolders.Remove(key); }
    }

    // ------------------------------------------------------------------ lifecycle

    public static void Delete(CustomBaseVersion version)
    {
        string folder = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrEmpty(folder)) return;
        if (IsInProgress(folder))
        {
            throw new InvalidOperationException($"Version {version.version} is currently being built or published and cannot be deleted.");
        }

        string full = Path.GetFullPath(folder);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, true);
        }

        TryDeleteFile(full + ".meta");
        InvalidateCache();
    }

    /// <summary>Removes the unsubmitted status after a confirmed upload. The folder is
    /// kept as a flushable local cache (it now scans as an imported version).</summary>
    public static void MarkPublished(VersionArtifact artifact)
    {
        if (artifact?.Manifest == null || !artifact.FolderExists) return;
        artifact.Manifest.unsubmitted = false;
        artifact.Manifest.Save(artifact.FolderUnityPath);
        InvalidateCache();
    }

    public static void SaveVersionJson(string folderUnityPath, CustomBaseVersion metadata)
    {
        string fullPath = Path.Combine(Path.GetFullPath(folderUnityPath), "version.json");
        File.WriteAllText(fullPath, JsonConvert.SerializeObject(metadata, Formatting.Indented, new StringEnumConverter()));
    }

    public static CustomBaseVersion LoadVersionJson(string folderUnityPath)
    {
        try
        {
            string fullPath = Path.Combine(Path.GetFullPath(folderUnityPath), "version.json");
            if (!File.Exists(fullPath)) return null;
            return JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(fullPath));
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[VersionRepository] Failed to read version.json in '{folderUnityPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds a manifest by hashing every file currently in the folder (excluding the
    /// local-only version.json / manifest.json pair, which are never uploaded).
    /// </summary>
    public static VersionManifest CreateManifestFromFolder(
        string folderUnityPath,
        CustomBaseVersion metadata,
        bool unsubmitted,
        string formSignature,
        List<VersionManifestInput> inputs)
    {
        var manifest = new VersionManifest
        {
            builderVersion = MCBPackageVersionService.CurrentStatus?.currentVersion,
            createdUtc = DateTime.UtcNow.ToString("o"),
            assetId = metadata.assetId,
            version = metadata.version,
            defaultAviVersion = metadata.defaultAviVersion,
            unsubmitted = unsubmitted,
            formSignature = formSignature,
            inputs = inputs ?? new List<VersionManifestInput>()
        };

        string folderFull = Path.GetFullPath(folderUnityPath);
        foreach (string file in Directory.GetFiles(folderFull, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string relative = MCBUtils.ToUnityPath(file.Substring(folderFull.Length).TrimStart('\\', '/'));
            if (LocalOnlyFileNames.Any(name => string.Equals(relative, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            manifest.outputs.Add(new VersionManifestFile
            {
                path = relative,
                hash = MCBUtils.CalculateFileHash(file),
                bytes = new FileInfo(file).Length
            });
        }

        return manifest;
    }

    // ------------------------------------------------------------------ flush / disk

    /// <summary>
    /// Deletes data MCB can recreate at any time: generated advanced mesh caches and
    /// downloaded version files — keeping the applied version, unsubmitted artifacts,
    /// and anything currently in progress. Returns a human-readable summary.
    /// (Absorbed from the deleted DiskSpaceService.)
    /// </summary>
    public static string FlushRemovableData(MCBEditor editor)
    {
        long freedBytes = 0;
        int deletedFolders = 0;
        var errors = new List<string>();

        try
        {
            var deleted = NativeMeshPayloadService.DeleteAllGeneratedPayloads();
            freedBytes += deleted.TotalBytes;
        }
        catch (Exception ex)
        {
            errors.Add("generated advanced meshes: " + ex.Message);
        }

        try
        {
            var keepFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Keep(CustomBaseVersion version)
            {
                string path = NormalizeFullPath(MCBUtils.GetVersionDataPath(version));
                if (path != null) keepFolders.Add(path);
            }

            Keep(editor != null && editor.customBaseTarget != null ? editor.customBaseTarget.appliedCustomBaseVersion : null);
            foreach (var unsubmitted in Scan(true).unsubmitted)
            {
                Keep(unsubmitted);
            }

            string versionsRoot = Path.GetFullPath(MCBUtils.ASSET_VERSIONS_FOLDER);
            if (Directory.Exists(versionsRoot))
            {
                foreach (string assetDir in Directory.GetDirectories(versionsRoot))
                {
                    string versionsDir = Path.Combine(assetDir, "versions");
                    if (!Directory.Exists(versionsDir)) continue;

                    foreach (string versionDir in Directory.GetDirectories(versionsDir))
                    {
                        string normalized = NormalizeFullPath(versionDir);
                        if (normalized == null || keepFolders.Contains(normalized) || IsInProgress(normalized))
                        {
                            continue;
                        }

                        try
                        {
                            freedBytes += GetDirectorySize(versionDir);
                            Directory.Delete(versionDir, true);
                            TryDeleteFile(versionDir.TrimEnd(Path.DirectorySeparatorChar) + ".meta");
                            deletedFolders++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(Path.GetFileName(versionDir) + ": " + ex.Message);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("version files: " + ex.Message);
        }

        AssetDatabase.Refresh();
        InvalidateCache();
        try
        {
            editor?.LoadImportedVersions(true);
            editor?.Repaint();
        }
        catch
        {
        }

        string summary = $"Freed {DiskUtils.FormatBytes(freedBytes)} ({deletedFolders} version folder(s) + generated advanced mesh caches). " +
                         $"Remaining free space: {DiskUtils.FormatBytes(Math.Max(0, DiskUtils.GetFreeBytesForProjectDrive()))}.";
        if (errors.Count > 0)
        {
            summary += "\n\nSome items could not be deleted:\n- " + string.Join("\n- ", errors.Take(5));
        }

        MCBLogger.Log($"[VersionRepository] Flush removable data: {summary}");
        return summary;
    }

    private static long GetDirectorySize(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => { try { return new FileInfo(file).Length; } catch { return 0L; } });
        }
        catch
        {
            return 0L;
        }
    }

    // ------------------------------------------------------------------ migration + GC

    /// <summary>
    /// One-time migration: entries in the legacy central unsubmitted index whose folder
    /// still exists get a version.json + manifest generated from the files currently on
    /// disk (their current hashes become the integrity baseline — build-time hashes are
    /// unknowable retroactively). The legacy file is then deleted; there is no ongoing
    /// legacy read path and no downgrade support.
    /// </summary>
    private static void MigrateLegacyUnsubmittedIndexOnce()
    {
        if (migrationChecked) return;
        migrationChecked = true;

        string legacyPath = MCBUtils.UNSUBMITTED_VERSIONS_FILE;
        string legacyFullPath;
        try { legacyFullPath = Path.GetFullPath(legacyPath); }
        catch { return; }

        if (!File.Exists(legacyFullPath)) return;

        List<CustomBaseVersion> legacyEntries;
        try
        {
            legacyEntries = JsonConvert.DeserializeObject<List<CustomBaseVersion>>(File.ReadAllText(legacyFullPath))
                            ?? new List<CustomBaseVersion>();
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[VersionRepository] Legacy unsubmitted index is corrupt and was set aside as .bak: {ex.Message}");
            try { File.Move(legacyFullPath, legacyFullPath + ".bak"); } catch { }
            return;
        }

        int migrated = 0;
        foreach (var entry in legacyEntries)
        {
            if (entry == null) continue;
            try
            {
                string folder = MCBUtils.GetVersionDataPath(entry);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(Path.GetFullPath(folder)))
                {
                    MCBLogger.Log($"[VersionRepository] Dropping legacy unsubmitted entry {entry.version}: its version folder no longer exists.");
                    continue;
                }

                var manifest = CreateManifestFromFolder(folder, entry, unsubmitted: true, formSignature: null, inputs: null);
                SaveVersionJson(folder, entry);
                manifest.Save(folder);
                migrated++;
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[VersionRepository] Failed to migrate legacy unsubmitted entry {entry.version}: {ex.Message}");
            }
        }

        TryDeleteFile(legacyFullPath);
        TryDeleteFile(legacyFullPath + ".meta");
        AssetDatabase.Refresh();
        MCBLogger.Log($"[VersionRepository] Migrated {migrated} legacy unsubmitted version(s) to per-folder persistence and removed the legacy index.");
    }

    /// <summary>Deletes leftover .building-* / .trash-* folders from crashed builds.</summary>
    private static void CollectGarbage()
    {
        string versionsRoot = Path.GetFullPath(MCBUtils.ASSET_VERSIONS_FOLDER);
        if (!Directory.Exists(versionsRoot)) return;

        try
        {
            foreach (string assetDir in Directory.GetDirectories(versionsRoot))
            {
                string versionsDir = Path.Combine(assetDir, "versions");
                if (!Directory.Exists(versionsDir)) continue;

                var staleGroups = Directory.GetDirectories(versionsDir)
                    .Where(IsStaleBuildFolder)
                    .GroupBy(GetFinalPathForStaleBuildFolder, StringComparer.OrdinalIgnoreCase);

                foreach (var group in staleGroups)
                {
                    string finalPath = group.Key;
                    var staleFolders = group
                        .Where(dir => !IsInProgress(dir))
                        .ToList();
                    if (staleFolders.Count == 0)
                    {
                        continue;
                    }

                    string recoveredFolder = null;
                    if (!string.IsNullOrEmpty(finalPath) && !Directory.Exists(finalPath))
                    {
                        recoveredFolder = SelectInterruptedCommitRecoveryFolder(staleFolders);
                        if (!string.IsNullOrEmpty(recoveredFolder))
                        {
                            try
                            {
                                Directory.Move(recoveredFolder, finalPath);
                                TryDeleteFile(recoveredFolder.TrimEnd(Path.DirectorySeparatorChar) + ".meta");
                                MCBLogger.Log($"[VersionRepository] Recovered interrupted version commit: {Path.GetFileName(recoveredFolder)} -> {Path.GetFileName(finalPath)}");
                            }
                            catch (Exception ex)
                            {
                                MCBLogger.LogWarning($"[VersionRepository] Could not recover interrupted build folder '{recoveredFolder}': {ex.Message}");
                                recoveredFolder = null;
                            }
                        }
                    }

                    foreach (string dir in staleFolders)
                    {
                        if (string.Equals(dir, recoveredFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        MCBLogger.Log($"[VersionRepository] Removing stale build folder: {Path.GetFileName(dir)}");
                        TryDeleteDirectory(dir);
                        TryDeleteFile(dir.TrimEnd(Path.DirectorySeparatorChar) + ".meta");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[VersionRepository] Garbage collection failed: {ex.Message}");
        }
    }

    private static bool IsStaleBuildFolder(string fullPath)
    {
        string name = Path.GetFileName(fullPath);
        return name.Contains(BuildingInfix) || name.Contains(TrashInfix);
    }

    private static string GetFinalPathForStaleBuildFolder(string fullPath)
    {
        string directory = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        int buildingIndex = name.IndexOf(BuildingInfix, StringComparison.Ordinal);
        int trashIndex = name.IndexOf(TrashInfix, StringComparison.Ordinal);
        int index;
        if (buildingIndex >= 0 && trashIndex >= 0)
        {
            index = Math.Min(buildingIndex, trashIndex);
        }
        else
        {
            index = Math.Max(buildingIndex, trashIndex);
        }

        if (string.IsNullOrEmpty(directory) || index <= 0)
        {
            return null;
        }

        return Path.Combine(directory, name.Substring(0, index));
    }

    private static string SelectInterruptedCommitRecoveryFolder(List<string> staleFolders)
    {
        return staleFolders
            .Where(IsBuildingFolder)
            .FirstOrDefault(HasPublishableManifestOutputs)
               ?? staleFolders
                   .Where(IsTrashFolder)
                   .FirstOrDefault(HasPublishableManifestOutputs)
               ?? staleFolders
                   .Where(IsTrashFolder)
                   .FirstOrDefault(HasVersionContent)
               ?? staleFolders
                   .Where(IsBuildingFolder)
                   .FirstOrDefault(HasVersionContent);
    }

    private static bool IsBuildingFolder(string fullPath)
    {
        return Path.GetFileName(fullPath).Contains(BuildingInfix);
    }

    private static bool IsTrashFolder(string fullPath)
    {
        return Path.GetFileName(fullPath).Contains(TrashInfix);
    }

    private static bool HasPublishableManifestOutputs(string fullPath)
    {
        string unityPath = MCBUtils.ToUnityPath(fullPath);
        var manifest = VersionManifest.Load(unityPath);
        if (manifest == null)
        {
            return false;
        }

        var metadata = LoadVersionJson(unityPath) ?? new CustomBaseVersion
        {
            assetId = manifest.assetId,
            version = manifest.version,
            defaultAviVersion = manifest.defaultAviVersion
        };
        var validation = Validate(new VersionArtifact(unityPath, metadata, manifest));
        return validation.state == ArtifactState.Valid || validation.state == ArtifactState.SourceDrift;
    }

    private static bool HasVersionContent(string fullPath)
    {
        try
        {
            return File.Exists(Path.Combine(fullPath, "version.json")) ||
                   File.Exists(Path.Combine(fullPath, VersionManifest.FileName)) ||
                   Directory.EnumerateFileSystemEntries(fullPath).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string fullPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(fullPath) && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, true);
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[VersionRepository] Could not delete '{fullPath}': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string fullPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[VersionRepository] Could not delete '{fullPath}': {ex.Message}");
        }
    }
}
#endif
