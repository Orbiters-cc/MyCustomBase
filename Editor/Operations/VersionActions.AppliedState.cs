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

// Which version the avatar's files show: base and applied hashes, the persisted marker, and the cached answers views ask for.
public partial class VersionActions
{
    public void UpdateCurrentBaseFbxHash()
    {
        var paths = GetCurrentFBXPaths();
        string path = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(path))
        {
            session.currentBaseFbxHash = null;
            session.currentAppliedFbxHash = null;
            session.currentIsCustom = false;
            UpdateAppliedVersionAndState(null);
            return;
        }

        // Try to get cached hashes first (non-blocking)
        string cachedCurrentHash = ContentHash.GetIfCached(path);
        bool allCurrentHashesCached = paths.All(targetPath => !string.IsNullOrEmpty(ContentHash.GetIfCached(targetPath)));
        
        string originalPath = path + FileManagerService.OriginalSuffix;
        string cachedOriginalHash = null;
        bool hasBackup = fileManagerService.BackupExists(path);
        
        if (hasBackup)
        {
            cachedOriginalHash = ContentHash.GetIfCached(originalPath);
        }

        // Use cached hashes if available, otherwise start async calculation
        if (cachedCurrentHash != null && allCurrentHashesCached && (!hasBackup || cachedOriginalHash != null))
        {
            // We have all needed cached hashes - use them immediately
            session.currentAppliedFbxHash = cachedCurrentHash;
            session.currentBaseFbxHash = hasBackup ? cachedOriginalHash : cachedCurrentHash;
            UpdateAppliedVersionAndState(cachedCurrentHash);
        }
        else
        {
            // Missing cache - start async hash calculation and use placeholder for now
            session.currentBaseFbxHash = null; // Will be updated when async calculation completes
            var requestedTarget = session.customBaseTarget;
            int? requestedAssetId = session.GetSelectedAsset()?.id;
            
            // Start async hash calculation in background
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    foreach (string targetPath in paths)
                    {
                        await ContentHash.GetAsync(targetPath);
                    }

                    string currentHash = await ContentHash.GetAsync(path);
                    string originalHash = null;
                    
                    if (hasBackup)
                    {
                        originalHash = await ContentHash.GetAsync(originalPath);
                    }
                    
                    // Update on main thread when calculation completes
                    AsyncTaskManager.Instance.ExecuteOnMainThread(() =>
                    {
                        if (session == null || session.customBaseTarget != requestedTarget ||
                            session.GetSelectedAsset()?.id != requestedAssetId ||
                            !GetCurrentFBXPaths().SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) return;
                        session.currentAppliedFbxHash = currentHash;
                        session.currentBaseFbxHash = hasBackup ? originalHash : currentHash;
                        UpdateAppliedVersionAndState(currentHash);
                        session.Repaint();
                    });
                }
                catch (System.Exception ex)
                {
                    MCBLogger.LogError($"[VersionActions] Async hash calculation failed: {ex.Message}");
                }
            });
        }
    }
    

    private IEnumerator RecalculateCurrentFbxHashCoroutine(
        int expectedHashGeneration,
        int? expectedApplyGeneration)
    {
        var profile = new System.Diagnostics.Stopwatch();
        var step = new System.Diagnostics.Stopwatch();
        profile.Start();
        step.Start();
        MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation START");

        var paths = GetCurrentFBXPaths();
        string path = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(path))
        {
            MCBLogger.Log($"[VersionApplyProfile] Async hash/state recalculation ABORT missing FBX path total={profile.Elapsed.TotalMilliseconds:F1} ms");
            yield break;
        }

        // Invalidate caches for current and backup FBX files
        foreach (string targetPath in paths)
        {
            ContentHash.Forget(targetPath);
        }
        string originalPath = path + FileManagerService.OriginalSuffix;
        bool hasBackup = File.Exists(originalPath);
        if (hasBackup)
        {
            ContentHash.Forget(originalPath);
        }
        MCBLogger.Log($"[VersionApplyProfile] Invalidated FBX hash cache: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms paths={paths.Count}");
        step.Restart();

        // Ensure any imports/updates are finished
        while (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            yield return null;
        }
        MCBLogger.Log($"[VersionApplyProfile] Waited for editor import/update before hashing: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        // Calculate hashes
        var currentHashTask = ContentHash.GetFreshAsync(Path.GetFullPath(path));
        var originalHashTask = hasBackup
            ? ContentHash.GetFreshAsync(Path.GetFullPath(originalPath))
            : null;
        while (!currentHashTask.IsCompleted || (originalHashTask != null && !originalHashTask.IsCompleted))
        {
            if (fbxHashRecalculationGeneration != expectedHashGeneration ||
                (expectedApplyGeneration.HasValue && applyProgressGeneration != expectedApplyGeneration.Value))
            {
                MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT superseded by a newer apply generation.");
                yield break;
            }
            yield return null;
        }
        string currentHash = currentHashTask.Result;
        string originalHash = originalHashTask != null ? originalHashTask.Result : null;
        if (string.IsNullOrWhiteSpace(currentHash) || (hasBackup && string.IsNullOrWhiteSpace(originalHash)))
        {
            MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT stale or unavailable hash result.");
            yield break;
        }
        MCBLogger.Log($"[VersionApplyProfile] Calculated primary FBX/current+backup hashes: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        foreach (string targetPath in paths.Skip(1))
        {
            var targetHashTask = ContentHash.GetFreshAsync(targetPath);
            while (!targetHashTask.IsCompleted)
            {
                if (fbxHashRecalculationGeneration != expectedHashGeneration ||
                    (expectedApplyGeneration.HasValue && applyProgressGeneration != expectedApplyGeneration.Value))
                {
                    MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT superseded by a newer apply generation.");
                    yield break;
                }
                yield return null;
            }
            if (string.IsNullOrWhiteSpace(targetHashTask.Result))
            {
                MCBLogger.Log($"[VersionApplyProfile] Async hash/state recalculation ABORT stale or unavailable hash for '{targetPath}'.");
                yield break;
            }
        }
        MCBLogger.Log($"[VersionApplyProfile] Calculated additional FBX hashes: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
        step.Restart();

        // Update editor state
        if (fbxHashRecalculationGeneration != expectedHashGeneration ||
            (expectedApplyGeneration.HasValue && applyProgressGeneration != expectedApplyGeneration.Value))
        {
            MCBLogger.Log("[VersionApplyProfile] Async hash/state recalculation ABORT superseded before state update.");
            yield break;
        }

        session.currentBaseFbxHash = hasBackup ? originalHash : currentHash;
        UpdateAppliedVersionAndState(currentHash);
        session.Repaint();
        MCBLogger.Log($"[VersionApplyProfile] Async hash/state recalculation DONE: step={step.Elapsed.TotalMilliseconds:F1} ms total={profile.Elapsed.TotalMilliseconds:F1} ms");
    }
    

    // FIX: Centralized and corrected state detection logic. This is the single source of truth.
    public void UpdateAppliedVersionAndState(string currentFileHash = null)
    {
        if (currentFileHash == null)
        {
            string path = GetCurrentFBXPath();
            if (!string.IsNullOrEmpty(path))
            {
                currentFileHash = ContentHash.GetIfCached(path);
            }

            if (string.IsNullOrEmpty(currentFileHash))
            {
                var pendingMarkerVersion = ResolvePersistedAppliedVersion();
                if (pendingMarkerVersion != null && IsAdvancedMeshVersionApplied(pendingMarkerVersion))
                {
                    var previousVersion = session.customBaseTarget.appliedCustomBaseVersion;
                    if (PersistAppliedVersionState(pendingMarkerVersion, true) || !Equals(previousVersion, pendingMarkerVersion))
                        MCBLogger.Log($"[VersionActions] Keeping applied native mesh version from persisted state before hash is ready: {pendingMarkerVersion.version}");
                    return;
                }

                MCBLogger.Log("[VersionActions] UpdateAppliedVersionAndState deferred (hash not cached yet).");
                return;
            }
        }

        // Track applied hash consistently
        session.currentAppliedFbxHash = currentFileHash;

        var candidateVersions = session.GetAllVersions() ?? new System.Collections.Generic.List<CustomBaseVersion>();

        // If we have no candidates yet, don't decide custom state prematurely
        if (string.IsNullOrEmpty(currentFileHash) || (!session.fetchAttempted && candidateVersions.Count == 0))
        {
            MCBLogger.Log("[VersionActions] Deferring state detection (waiting for versions).\n" +
                              $"fetchAttempted={session.fetchAttempted}, candidates={candidateVersions.Count}");
            return;
        }

        if (session?.customBaseTarget == null) return;
        // This runs on every inspector rebuild: the custom base is recorded for undo and marked dirty only when one of its
        // values changes, and the outcome is logged only when it changes.
        var target = session.customBaseTarget;
        var previous = target.appliedCustomBaseVersion;

        var markerVersion = ResolvePersistedAppliedVersion(candidateVersions);
        if (markerVersion != null && IsAdvancedMeshVersionApplied(markerVersion))
        {
            if (PersistAppliedVersionState(markerVersion, true) || !Equals(previous, markerVersion))
                MCBLogger.Log($"[VersionActions] Keeping applied native mesh version from persisted state: {markerVersion.version}");
            return;
        }

        var matchingVersion = FindMatchingAppliedVersion(candidateVersions, currentFileHash);
        bool changed;

        if (matchingVersion != null)
        {
            session.isCustomBase = true;
            session.currentIsCustom = false;
            var links = BuildAppliedVersionBlendshapeLinkCache(matchingVersion);
            var offsets = BuildAppliedVersionAnimationPositionOffsetCache(matchingVersion);
            // The applied version is saved with the custom base: another version, or the same one with new contents, counts.
            changed = JsonUtility.ToJson(previous) != JsonUtility.ToJson(matchingVersion) ||
                      !SameSerializedEntries(target.appliedVersionBlendshapeLinksCache, links) ||
                      !SameSerializedEntries(target.appliedVersionAnimationPositionOffsetsCache, offsets);
            if (changed)
            {
                Undo.RecordObject(target, UpdateStateUndoName);
                SetCache(ref target.appliedVersionBlendshapeLinksCache, links);
                SetCache(ref target.appliedVersionAnimationPositionOffsetsCache, offsets);
                MCBLogger.Log($"[VersionActions] Matched applied version: {matchingVersion.version}");
            }
            target.appliedCustomBaseVersion = matchingVersion;
        }
        else
        {
            session.isCustomBase = false;
            changed = previous != null || target.appliedVersionAnimationPositionOffsetsCache?.Count > 0;
            if (changed)
            {
                Undo.RecordObject(target, UpdateStateUndoName);
                target.appliedVersionAnimationPositionOffsetsCache?.Clear();
            }
            target.appliedCustomBaseVersion = null;

            // Detect user-custom base only when feature is enabled and we have attempted fetching versions
            string fbxPath = GetCurrentFBXPath();
            bool hasBackup = !string.IsNullOrEmpty(fbxPath) && fileManagerService.BackupExists(fbxPath);
            bool canTreatAsCustom = FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) && session.fetchAttempted && candidateVersions.Count > 0;
            if (hasBackup && canTreatAsCustom)
            {
                session.currentIsCustom = true;
                // Persist the custom version entry (copy FBX and avatar) if not already present
                try
                {
                    // Saving decides from the model's bytes, never from the cached hash this state was detected with.
                    UserCustomVersionService.Instance.SaveCurrent(fbxPath, AvatarPaths.Root(session.customBaseTarget), out bool created);
                    if (created)
                    {
                        session.userCustomVersions = UserCustomVersionService.Instance.GetAll();
                    }
                }
                catch (System.Exception ex)
                {
                    MCBLogger.LogWarning($"[MCB] Failed to persist custom base info: {ex.Message}");
                }
            }
            else
            {
                session.currentIsCustom = false;
            }
            
            if (changed)
                MCBLogger.Log("[VersionActions] No matching version hash found. Marking state as non-custom-base.");
        }

        if (changed) EditorUtility.SetDirty(target);
    }

    private void PersistAppliedVersionState(CustomBaseVersion version) => PersistAppliedVersionState(version, false);

    /// <param name="onlyWhenChanged">For state refreshes: the custom base is recorded for undo and marked dirty only
    /// when one of its values changes.</param>
    /// <returns>Whether the custom base was written.</returns>
    private bool PersistAppliedVersionState(CustomBaseVersion version, bool onlyWhenChanged)
    {
        if (session?.customBaseTarget == null || version == null || version == VersionListDrawer.RESET_VERSION)
        {
            ClearAppliedVersionState();
            return true;
        }

        var target = session.customBaseTarget;
        var appliedAsset = session.GetSelectedAsset();
        string name = appliedAsset != null && appliedAsset.id == version.assetId && !string.IsNullOrWhiteSpace(appliedAsset.name)
            ? appliedAsset.name
            : target.appliedCustomBaseAssetId != version.assetId ? "" : target.appliedCustomBaseName;
        string versionString = version.version ?? "";
        string defaultAviVersion = version.defaultAviVersion ?? "";
        string sourceVersionKey = version.sourceVersionKey ?? "";
        string deliveryMode = NativeMeshPayloadService.VersionUsesAdvancedMesh(version)
            ? AdvancedMeshDeliveryMode
            : FbxReplacementDeliveryMode;
        var links = BuildAppliedVersionBlendshapeLinkCache(version);
        var offsets = BuildAppliedVersionAnimationPositionOffsetCache(version);
        var customization = VersionCustomization.Read(version.extraCustomization);

        target.appliedCustomBaseVersion = version;
        session.isCustomBase = true;
        session.currentIsCustom = false;
        if (onlyWhenChanged)
        {
            if (target.appliedCustomBaseName == name &&
                target.appliedCustomBaseAssetId == version.assetId &&
                target.appliedCustomBaseVersionString == versionString &&
                target.appliedCustomBaseDefaultAviVersion == defaultAviVersion &&
                target.appliedCustomBaseSourceVersionKey == sourceVersionKey &&
                target.appliedCustomBaseDeliveryMode == deliveryMode &&
                SameSerializedEntries(target.appliedVersionBlendshapeLinksCache, links) &&
                SameSerializedEntries(target.appliedVersionAnimationPositionOffsetsCache, offsets) &&
                JsonUtility.ToJson(target.appliedCustomization) == JsonUtility.ToJson(customization))
                return false;
            Undo.RecordObject(target, UpdateStateUndoName);
        }

        target.appliedCustomBaseName = name;
        target.appliedCustomBaseAssetId = version.assetId;
        target.appliedCustomBaseVersionString = versionString;
        target.appliedCustomBaseDefaultAviVersion = defaultAviVersion;
        target.appliedCustomBaseSourceVersionKey = sourceVersionKey;
        target.appliedCustomBaseDeliveryMode = deliveryMode;
        SetCache(ref target.appliedVersionBlendshapeLinksCache, links);
        SetCache(ref target.appliedVersionAnimationPositionOffsetsCache, offsets);
        target.appliedCustomization = customization;
        EditorUtility.SetDirty(target);
        session.serializedObject.Update();
        return true;
    }

    private void ClearAppliedVersionState()
    {
        if (session?.customBaseTarget == null) return;

        ModeService.Restore(session.customBaseTarget);
        session.customBaseTarget.appliedCustomization = new VersionCustomization();
        session.customBaseTarget.appliedCustomBaseVersion = null;
        session.customBaseTarget.appliedCustomBaseAssetId = 0;
        session.customBaseTarget.versionOriginalModels.Clear();
        session.customBaseTarget.appliedCustomBaseName = "";
        session.customBaseTarget.appliedCustomBaseVersionString = "";
        session.customBaseTarget.appliedCustomBaseDefaultAviVersion = "";
        session.customBaseTarget.appliedCustomBaseSourceVersionKey = "";
        session.customBaseTarget.appliedCustomBaseDeliveryMode = "";
        session.isCustomBase = false;
        session.currentIsCustom = false;
        SyncAppliedVersionBlendshapeLinkCache(null);
        SyncAppliedVersionAnimationPositionOffsetCache(null);
        EditorUtility.SetDirty(session.customBaseTarget);
    }

    private CustomBaseVersion ResolvePersistedAppliedVersion(IReadOnlyList<CustomBaseVersion> candidateVersions = null)
    {
        if (session?.customBaseTarget == null)
        {
            return null;
        }

        var applied = session.customBaseTarget.appliedCustomBaseVersion;
        string versionString = session.customBaseTarget.appliedCustomBaseVersionString;
        var candidates = new List<CustomBaseVersion>();
        if (candidateVersions != null) candidates.AddRange(candidateVersions.Where(v => v != null));
        var allVersions = session.GetAllVersions();
        if (allVersions != null) candidates.AddRange(allVersions.Where(v => v != null));
        if (session.selectedVersionForAction != null) candidates.Add(session.selectedVersionForAction);
        if (session.recommendedVersion != null) candidates.Add(session.recommendedVersion);

        candidates = candidates
            .Where(v => v != null)
            .GroupBy(v => $"{v.assetId}|{v.version}|{v.defaultAviVersion}|{v.sourceVersionKey}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var inferredAdvanced = InferAdvancedVersionFromGeneratedMeshPaths(candidates, out bool hasGeneratedMeshes);
        if (inferredAdvanced != null)
        {
            return inferredAdvanced;
        }

        // Shared meshes without an unambiguous identity must not revive a stale marker.
        if (hasGeneratedMeshes)
            return null;

        if (applied != null && !string.IsNullOrWhiteSpace(applied.version) && applied != VersionListDrawer.RESET_VERSION)
        {
            return applied;
        }

        if (string.IsNullOrWhiteSpace(versionString))
        {
            return candidates.FirstOrDefault(v =>
                       v != null &&
                       NativeMeshPayloadService.VersionUsesAdvancedMesh(v) &&
                       NativeMeshPayloadService.HasAnyAdvancedMeshApplied(AvatarPaths.Root(session.customBaseTarget), v));
        }

        int assetId = session.customBaseTarget.appliedCustomBaseAssetId;
        string defaultAviVersion = session.customBaseTarget.appliedCustomBaseDefaultAviVersion;
        return candidates.FirstOrDefault(v =>
            v != null &&
            string.Equals(v.version, versionString, StringComparison.Ordinal) &&
            (string.IsNullOrEmpty(session.customBaseTarget.appliedCustomBaseSourceVersionKey) || v.sourceVersionKey == session.customBaseTarget.appliedCustomBaseSourceVersionKey) &&
            (assetId <= 0 || v.assetId == assetId) &&
            (string.IsNullOrWhiteSpace(defaultAviVersion) || string.Equals(v.defaultAviVersion, defaultAviVersion, StringComparison.Ordinal)));
    }

    // Every version row asks for the applied version on each UI rebuild: the inference from the avatar's generated meshes
    // is reused while the meshes, the applied marker and the candidate versions are the same objects it was made from.
    private sealed class GeneratedMeshInference
    {
        public MyCustomBase Target;
        public int[] Meshes;
        public string[] Paths;
        public string Marker;
        public List<CustomBaseVersion> Available;
        public CustomBaseVersion Match;
    }

    private GeneratedMeshInference generatedMeshInference;

    private CustomBaseVersion InferAdvancedVersionFromGeneratedMeshPaths(IReadOnlyList<CustomBaseVersion> candidates, out bool hasGeneratedMeshes)
    {
        hasGeneratedMeshes = false;
        if (session?.customBaseTarget == null)
        {
            return null;
        }

        var target = session.customBaseTarget;
        var root = AvatarPaths.Root(target);
        var meshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Select(renderer => renderer.sharedMesh != null ? renderer.sharedMesh.GetInstanceID() : 0)
            .ToArray();
        var cached = generatedMeshInference;
        bool sameMeshes = cached != null && cached.Target == target && cached.Meshes.SequenceEqual(meshes);
        var paths = sameMeshes
            ? cached.Paths
            : NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(root)
                .Select(renderer => MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        hasGeneratedMeshes = paths.Length > 0;

        var available = new List<CustomBaseVersion>();
        if (hasGeneratedMeshes)
        {
            if (candidates != null)
            {
                available.AddRange(candidates.Where(v => v != null));
            }

            var local = VersionRepository.Scan();
            available.AddRange(local.imported.Where(v => v != null));
            available.AddRange(local.unsubmitted.Where(v => v != null));
            if (!string.IsNullOrEmpty(target.appliedCustomBaseSourceVersionKey))
                available = available.Where(v => v.sourceVersionKey == target.appliedCustomBaseSourceVersionKey).ToList();
        }

        string marker = $"{target.appliedCustomBaseAssetId}|{target.appliedCustomBaseVersionString}|" +
                        $"{target.appliedCustomBaseDefaultAviVersion}|{target.appliedCustomBaseSourceVersionKey}";
        if (!sameMeshes || cached.Marker != marker || !SameObjects(cached.Available, available))
        {
            var inferred = hasGeneratedMeshes
                ? NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(paths, available, target.appliedCustomBaseAssetId,
                    target.appliedCustomBaseVersionString, target.appliedCustomBaseDefaultAviVersion)
                : null;
            if (inferred != null && !inferred.Equals(cached?.Match))
                MCBLogger.Log($"[VersionActions] Inferred native mesh applied version {inferred.version} from generated mesh provenance.");
            cached = generatedMeshInference = new GeneratedMeshInference
            {
                Target = target, Meshes = meshes, Paths = paths, Marker = marker, Available = available, Match = inferred
            };
        }

        var match = cached.Match;
        if (match == null)
        {
            return null;
        }

        if (match.isUnsubmitted)
        {
            if (session.unsubmittedVersions != null &&
                !session.unsubmittedVersions.Any(v => v != null && v.Equals(match)))
            {
                session.unsubmittedVersions.Add(match);
            }
        }
        else if (session.importedVersions != null &&
                 !session.importedVersions.Any(v => v != null && v.Equals(match)))
        {
            session.importedVersions.Add(match);
        }

        return match;
    }

    private static bool SameObjects<T>(List<T> cached, List<T> current) where T : class
    {
        if (cached.Count != current.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(cached[i], current[i])) return false;
        }
        return true;
    }

    private bool IsAdvancedMeshVersionApplied(CustomBaseVersion version)
    {
        if (session?.customBaseTarget == null || version == null)
        {
            return false;
        }

        bool isAdvanced = NativeMeshPayloadService.VersionUsesAdvancedMesh(version) ||
                          string.Equals(
                              session.customBaseTarget.appliedCustomBaseDeliveryMode,
                              AdvancedMeshDeliveryMode,
                              StringComparison.Ordinal);
        return isAdvanced &&
               NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(
                   AvatarPaths.Root(session.customBaseTarget),
                   version).Count > 0;
    }

    // One build of the version section asks which version is applied once per row and per button. Inside an
    // AppliedStateScope the answer (a merge and sort of every version, a walk of the avatar's renderers, maybe a scan of
    // the versions folder) is worked out once: nothing the section builds changes it.
    private int appliedScopeDepth;

    private bool appliedInScopeResolved;

    private CustomBaseVersion appliedInScope;

    private bool? evidenceInScope;

    /// <summary>Until disposed, the applied version and the applied-base evidence are resolved once and reused.</summary>
    public IDisposable AppliedStateScope()
    {
        appliedScopeDepth++;
        return new AppliedScope(this);
    }

    private sealed class AppliedScope : IDisposable
    {
        private VersionActions owner;
        public AppliedScope(VersionActions owner) { this.owner = owner; }

        public void Dispose()
        {
            if (owner == null) return;
            if (--owner.appliedScopeDepth == 0)
            {
                owner.appliedInScopeResolved = false;
                owner.appliedInScope = null;
                owner.evidenceInScope = null;
            }
            owner = null;
        }
    }

    private CustomBaseVersion AppliedVersionForView()
    {
        if (appliedScopeDepth == 0) return ResolvePersistedAppliedVersion();
        if (!appliedInScopeResolved)
        {
            appliedInScope = ResolvePersistedAppliedVersion();
            appliedInScopeResolved = true;
        }
        return appliedInScope;
    }

    public bool IsVersionCurrentlyApplied(CustomBaseVersion version)
    {
        if (version == null || version == VersionListDrawer.RESET_VERSION || session?.customBaseTarget == null)
        {
            return false;
        }

        var applied = AppliedVersionForView();
        return applied != null && version.Equals(applied);
    }

    public bool HasAppliedCustomBaseEvidence()
    {
        if (session?.customBaseTarget == null)
        {
            return false;
        }

        if (appliedScopeDepth > 0 && evidenceInScope.HasValue) return evidenceInScope.Value;
        bool evidence = NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(
                            AvatarPaths.Root(session.customBaseTarget)).Count > 0 ||
                        AppliedVersionForView() != null ||
                        session.isCustomBase ||
                        session.currentIsCustom;
        if (appliedScopeDepth > 0) evidenceInScope = evidence;
        return evidence;
    }

    public bool IsDefaultBaseCurrentlyApplied()
    {
        return !HasAppliedCustomBaseEvidence();
    }

    private void SyncAppliedVersionBlendshapeLinkCache(CustomBaseVersion version)
    {
        if (session?.customBaseTarget == null) return;
        SetCache(ref session.customBaseTarget.appliedVersionBlendshapeLinksCache, BuildAppliedVersionBlendshapeLinkCache(version));
    }

    private void SyncAppliedVersionAnimationPositionOffsetCache(CustomBaseVersion version)
    {
        if (session?.customBaseTarget == null) return;
        SetCache(ref session.customBaseTarget.appliedVersionAnimationPositionOffsetsCache, BuildAppliedVersionAnimationPositionOffsetCache(version));
    }

    private static void SetCache<T>(ref List<T> cache, List<T> entries)
    {
        if (cache == null) cache = new List<T>();
        cache.Clear();
        cache.AddRange(entries);
    }

    // Compared as Unity serializes them: entries rebuilt from the same version are equal to the cached ones.
    private static bool SameSerializedEntries<T>(List<T> cache, List<T> entries)
    {
        if ((cache?.Count ?? 0) != entries.Count) return false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (JsonUtility.ToJson(cache[i]) != JsonUtility.ToJson(entries[i])) return false;
        }
        return true;
    }

    private static List<CreatorBlendshapeEntry> BuildAppliedVersionBlendshapeLinkCache(CustomBaseVersion version)
    {
        var cache = new List<CreatorBlendshapeEntry>();
        if (version?.customBlendshapes == null || version.customBlendshapes.Length == 0) return cache;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null) continue;
            var cached = new CreatorBlendshapeEntry
            {
                name = entry.name,
                defaultValue = entry.defaultValue,
                isSlider = entry.isSlider,
                isSliderDefault = entry.isSliderDefault,
                correctiveBlendshapes = new List<CreatorCorrectiveBlendshapeEntry>()
            };

            if (entry.correctiveBlendshapes != null)
            {
                foreach (var c in entry.correctiveBlendshapes)
                {
                    if (c == null) continue;
                    cached.correctiveBlendshapes.Add(new CreatorCorrectiveBlendshapeEntry
                    {
                        toFixType = c.toFixType,
                        toFix = c.toFix,
                        fixedByType = c.fixedByType,
                        fixedBy = c.fixedBy
                    });
                }
            }

            cache.Add(cached);
        }
        return cache;
    }

    private static List<AnimationPositionOffsetEntry> BuildAppliedVersionAnimationPositionOffsetCache(CustomBaseVersion version)
    {
        var cache = new List<AnimationPositionOffsetEntry>();
        if (version == null) return cache;

        foreach (var offset in AnimationPositionOffsetService.BuildOffsetsForVersion(version))
        {
            if (offset == null || string.IsNullOrWhiteSpace(offset.bonePath)) continue;
            cache.Add(new AnimationPositionOffsetEntry
            {
                sourceFbxPath = offset.sourceFbxPath,
                bonePath = offset.bonePath,
                offset = offset.offset
            });
        }
        return cache;
    }

    internal void SmartSelectVersion()
    {
        var allVersions = session.GetAllVersions() ?? new System.Collections.Generic.List<CustomBaseVersion>();
        CustomBaseVersion versionToSelect = null;

        if (session.selectedVersionForAction != null && allVersions.Contains(session.selectedVersionForAction))
        {
            versionToSelect = session.selectedVersionForAction;
        }
        else if (session.customBaseTarget.appliedCustomBaseVersion != null && allVersions.Contains(session.customBaseTarget.appliedCustomBaseVersion))
        {
            versionToSelect = session.customBaseTarget.appliedCustomBaseVersion;
        }
        else if (session.recommendedVersion != null)
        {
            bool isNewer = session.customBaseTarget.appliedCustomBaseVersion == null ||
                           session.CompareVersions(session.recommendedVersion.version, session.customBaseTarget.appliedCustomBaseVersion.version) > 0;
            bool isDownloaded = MCBUtils.IsVersionDownloaded(session.recommendedVersion);

            if (isNewer && isDownloaded)
            {
                versionToSelect = session.recommendedVersion;
            }
        }

        session.selectedVersionForAction = versionToSelect;
    }

    public string GetCurrentFBXPath()
    {
        return GetCurrentFBXPaths().FirstOrDefault();
    }

    private List<string> GetCurrentFBXPaths()
    {
        // Read from the component itself: the shared serialized view may not have read a change made without it.
        var paths = new List<string>();
        if (session?.customBaseTarget == null || session.customBaseTarget.baseFbxFiles == null)
        {
            return paths;
        }

        foreach (var fbx in session.customBaseTarget.baseFbxFiles)
        {
            string path = fbx != null ? AssetDatabase.GetAssetPath(fbx) : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(MCBUtils.ToUnityPath(path));
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CustomBaseVersion FindMatchingAppliedVersion(IReadOnlyList<CustomBaseVersion> candidateVersions, string currentFileHash)
    {
        if (candidateVersions == null || candidateVersions.Count == 0)
        {
            return null;
        }

        var persistedAdvanced = session?.customBaseTarget?.appliedCustomBaseVersion;
        if (persistedAdvanced != null && NativeMeshPayloadService.VersionUsesAdvancedMesh(persistedAdvanced))
        {
            var matchingPersisted = candidateVersions.FirstOrDefault(v =>
                v != null &&
                v.assetId == persistedAdvanced.assetId &&
                string.Equals(v.version, persistedAdvanced.version, StringComparison.Ordinal) &&
                string.Equals(v.defaultAviVersion, persistedAdvanced.defaultAviVersion, StringComparison.Ordinal));
            if (matchingPersisted != null &&
                NativeMeshPayloadService.IsVersionApplied(AvatarPaths.Root(session.customBaseTarget), matchingPersisted))
            {
                return matchingPersisted;
            }
        }

        var target = session?.customBaseTarget;
        return SelectAppliedVersionByHashes(candidateVersions, currentFileHash, GetCurrentTargetHashes(currentFileHash),
            sourceFile => AvatarPathOverrideService.ResolveLocalTargetPath(target, sourceFile),
            version => target != null &&
                       version.assetId == target.appliedCustomBaseAssetId &&
                       string.Equals(version.version, target.appliedCustomBaseVersionString, StringComparison.Ordinal) &&
                       string.Equals(version.defaultAviVersion, target.appliedCustomBaseDefaultAviVersion, StringComparison.Ordinal) &&
                       string.Equals(version.sourceVersionKey ?? "", target.appliedCustomBaseSourceVersionKey ?? "", StringComparison.Ordinal));
    }

    /// <summary>
    /// A release is identified by every target model it changes. appliedCustomAviHash covers only the first model, so it
    /// cannot identify a release until secondary hashes are known. An existing persisted identity can be retained
    /// while hashing if no known hash contradicts it. It also picks among releases with identical mesh bytes.
    /// </summary>
    internal static CustomBaseVersion SelectAppliedVersionByHashes(
        IReadOnlyList<CustomBaseVersion> candidateVersions,
        string primaryHash,
        IReadOnlyDictionary<string, string> currentHashesByPath,
        Func<ModelFileData, string> resolveTargetPath,
        Func<CustomBaseVersion, bool> isPersistedIdentity)
    {
        var candidates = (candidateVersions ?? Array.Empty<CustomBaseVersion>()).Where(v => v != null).ToList();
        var matches = candidates.Where(v => MatchTargetHashes(v, currentHashesByPath, resolveTargetPath) == true).ToList();
        if (matches.Count == 0)
        {
            matches = candidates.Where(v =>
                !string.IsNullOrEmpty(v.appliedCustomAviHash) &&
                v.appliedCustomAviHash.Equals(primaryHash, StringComparison.OrdinalIgnoreCase) &&
                isPersistedIdentity(v) &&
                MatchTargetHashes(v, currentHashesByPath, resolveTargetPath) != false).ToList();
        }

        return matches.FirstOrDefault(isPersistedIdentity) ?? matches.FirstOrDefault();
    }

    // True when every target hash matches, false when a known target hash differs, null when a target hash is unknown.
    private static bool? MatchTargetHashes(
        CustomBaseVersion version,
        IReadOnlyDictionary<string, string> currentHashesByPath,
        Func<ModelFileData, string> resolveTargetPath)
    {
        if (version?.sourceFiles == null || version.sourceFiles.Length == 0)
        {
            return null;
        }

        bool unknown = false;
        bool comparedAny = false;
        bool changesAny = false;
        foreach (var sourceFile in version.sourceFiles)
        {
            if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.path))
            {
                continue;
            }

            string targetPath = resolveTargetPath(sourceFile);
            if (string.IsNullOrWhiteSpace(targetPath) ||
                currentHashesByPath == null ||
                !currentHashesByPath.TryGetValue(targetPath, out string currentHash))
            {
                unknown = true;
                continue;
            }

            string expectedHash = ResolveExpectedHashForSource(version, sourceFile);
            if (string.IsNullOrWhiteSpace(expectedHash) ||
                !expectedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            comparedAny = true;
            changesAny |= !expectedHash.Equals(sourceFile.hash, StringComparison.OrdinalIgnoreCase);
        }

        // A release whose models all equal their originals cannot be told apart from the untouched base.
        return comparedAny && !unknown && changesAny ? true : (bool?)null;
    }

    private Dictionary<string, string> GetCurrentTargetHashes(string currentFileHash)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = GetCurrentFBXPaths();

        for (int i = 0; i < paths.Count; i++)
        {
            string path = MCBUtils.ToUnityPath(paths[i]);
            string hash = i == 0 ? currentFileHash : null;
            if (string.IsNullOrWhiteSpace(hash))
            {
                hash = ContentHash.GetIfCached(path);
            }

            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(hash))
            {
                hashes[path] = hash;
            }
        }

        return hashes;
    }

    private static string ResolveExpectedHashForSource(CustomBaseVersion version, ModelFileData sourceFile)
    {
        if (NativeMeshPayloadService.VersionUsesAdvancedMesh(version))
        {
            var advancedPatch = version.versionFiles?.FirstOrDefault(file =>
                file != null &&
                string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                NativeMeshPayloadService.IsAdvancedMeshPatchTransform(file.transform) &&
                IsPatchForSourceFile(file, sourceFile));
            return advancedPatch?.outputHash;
        }

        var patchFile = version.versionFiles?.FirstOrDefault(file =>
            file != null &&
            string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
            ModelFileTransforms.IsFbxReplacementTransform(file.transform) &&
            IsPatchForSourceFile(file, sourceFile));

        if (!string.IsNullOrWhiteSpace(patchFile?.outputHash))
        {
            return patchFile.outputHash;
        }

        // Releases published before per-model patch files changed only their first model, whose result is appliedCustomAviHash.
        bool listsPatches = version.versionFiles?.Any(file => file != null && string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase)) == true;
        if (!listsPatches && !string.IsNullOrWhiteSpace(version.appliedCustomAviHash) &&
            ReferenceEquals(sourceFile, version.sourceFiles?.FirstOrDefault(file => file != null && !string.IsNullOrWhiteSpace(file.path))))
        {
            return version.appliedCustomAviHash;
        }

        return sourceFile.hash;
    }

    private ModelFileData ResolveSourceFileForPatch(CustomBaseVersion version, ModelFileData patchFile)
    {
        return version?.sourceFiles?.FirstOrDefault(sourceFile => IsPatchForSourceFile(patchFile, sourceFile));
    }

    private static bool IsPatchForSourceFile(ModelFileData patchFile, ModelFileData sourceFile)
    {
        if (patchFile == null || sourceFile == null)
        {
            return false;
        }

        if (patchFile.sourceModelFileId.HasValue && sourceFile.id == patchFile.sourceModelFileId.Value)
        {
            return true;
        }

        string sourcePath = MCBUtils.ToUnityPath(sourceFile.path);
        string patchSourcePath = null;
        if (patchFile.metadata != null &&
            patchFile.metadata.TryGetValue("sourcePath", out object sourcePathValue))
        {
            patchSourcePath = sourcePathValue?.ToString();
        }

        return !string.IsNullOrWhiteSpace(sourcePath) &&
               !string.IsNullOrWhiteSpace(patchSourcePath) &&
               string.Equals(sourcePath, MCBUtils.ToUnityPath(patchSourcePath), StringComparison.OrdinalIgnoreCase);
    }
}
#endif
