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

public partial class VersionActions
{
    private const float BlendshapeWeightEpsilon = 0.001f;
    private const string AdvancedMeshDeliveryMode = "UNITY_NATIVE_MESH_ASSET";
    private const string FbxReplacementDeliveryMode = "FBX_REPLACEMENT";
    private const string UpdateStateUndoName = "Update MCB State";
    private const double ApplyProgressIntroSeconds = 0.08d;
    private const double ApplyProgressCompletionHoldSeconds = 0.6d;
    private const float DownloadApplyProgressStart = 0.02f;
    private const float DownloadApplyProgressComplete = 0.66f;
    private const float DownloadApplySyntheticProgressPerSecond = 0.10f;
    private const float DownloadApplySyntheticProgressMax = DownloadApplyProgressComplete - 0.04f;
    private const float DownloadApplyExtractionComplete = 0.68f;
    private const float DownloadApplyImportComplete = 0.70f;
    private const long InMemoryVersionPackageHardCapBytes = 10L * 1024L * 1024L * 1024L;
    private const long InMemoryVersionPackageEstimatedMultiplier = 5L;

    private sealed class ApplyTimingProfile
    {
        private readonly bool enabled;
        private readonly string operation;
        private readonly System.Diagnostics.Stopwatch total = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Diagnostics.Stopwatch step = System.Diagnostics.Stopwatch.StartNew();

        public ApplyTimingProfile(CustomBaseVersion version, bool isReset, bool usesAdvancedMesh)
        {
            enabled = usesAdvancedMesh;
            operation = $"{(isReset ? "reset" : "apply")} version={(version != null ? version.version : "null")}";

            if (enabled)
            {
                MCBLogger.Log($"[VersionApplyProfile] START {operation}");
            }
        }

        public void Mark(string label)
        {
            if (!enabled)
            {
                return;
            }

            MCBLogger.Log($"[VersionApplyProfile] {label}: step={step.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms");
            step.Restart();
        }

        public void Done(string label = "DONE")
        {
            if (!enabled)
            {
                return;
            }

            MCBLogger.Log($"[VersionApplyProfile] {label} {operation}: total={total.Elapsed.TotalMilliseconds:F1} ms");
        }
    }

    private readonly McbSession session;
    private readonly NetworkService networkService;
    private readonly FileManagerService fileManagerService;
    private VersionTransitionRollbackSnapshot activeTransitionRollback;
    private static int fbxHashRecalculationGeneration;
    private int applyProgressGeneration;
    private float applyProgressBase;
    private float applyProgressScale = 1f;
    private readonly Dictionary<string, NativeMeshPayloadService.NativeMeshPayloadPreparationPreload> advancedMeshPreparationPreloads =
        new Dictionary<string, NativeMeshPayloadService.NativeMeshPayloadPreparationPreload>(StringComparer.OrdinalIgnoreCase);
    public VersionApplyProgressState ApplyProgress { get; } = new VersionApplyProgressState();
    /// <summary>How far the running version download is (0 to 1).</summary>
    public float DownloadProgress { get; private set; }

    // A value in a query string, escaped (a version or key with a '&' or a space must not change the request).
    private static string Query(string value) => Uri.EscapeDataString(value ?? string.Empty);

    public VersionActions(McbSession session, NetworkService network, FileManagerService files)
    {
        this.session = session;
        this.networkService = network;
        this.fileManagerService = files;
    }
    
    // Coroutine Starters
    public void StartVersionDownload(CustomBaseVersion ver, bool apply)
    {
        if (apply && RefitBlocksSwitch()) return;
        if (apply && !session.isDownloading)
        {
            ResetApplyProgressRange();
            StartApplyProgress("Starting download...", 0f);
        }

        EditorCoroutineUtility.StartCoroutineOwnerless(MCBWork.Guard(DownloadWithMigrationCoroutine(ver, apply),
            ex => HandleApplyOrResetException(ex, ver, false)));
    }
    public void StartVersionDelete(CustomBaseVersion ver) => EditorCoroutineUtility.StartCoroutineOwnerless(DeleteVersionCoroutine(ver));
    public void StartApplyVersion() => StartApplyOrResetWithIntro(session.selectedVersionForAction, false, "Starting version switch...");
    public void StartReset() => StartApplyOrResetWithIntro(null, true, "Starting reset...");
    public void StartRecalculateCurrentFbxHash()
    {
        int hashGeneration = ++fbxHashRecalculationGeneration;
        EditorCoroutineUtility.StartCoroutineOwnerless(RecalculateCurrentFbxHashCoroutine(hashGeneration, null));
    }
    public void StartApplyCustomVersion()
    {
        if (session.isApplying || ApplyProgress.IsRunning || RefitBlocksSwitch()) return;
        EditorCoroutineUtility.StartCoroutineOwnerless(ApplyCustomVersionCoroutine(session.selectedCustomVersionForAction));
    }
    public void ConfigureApplyProgressColor(Color color) => ApplyProgress.SetFillColor(color);

    public bool CanResetToDefaultBaseWithoutFbxImport()
    {
        if (session?.customBaseTarget == null || fileManagerService == null)
        {
            return false;
        }

        var versionForAssets = ResolvePersistedAppliedVersion() ?? session.selectedVersionForAction;
        if (!NativeMeshPayloadService.VersionUsesAdvancedMesh(versionForAssets))
        {
            return false;
        }

        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrWhiteSpace(fbxPath))
        {
            return false;
        }

        var pathsToRestore = GetResetAffectedFbxPaths(versionForAssets, fbxPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && fileManagerService.BackupExists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return pathsToRestore.Count > 0 && pathsToRestore.All(fileManagerService.FbxMatchesBackupAtPath);
    }

    internal IEnumerator ApplyOrResetCoroutine(CustomBaseVersion version, bool isReset)
    {
        yield return MCBWork.Guard(ApplyResolvedVersionCoroutine(version, isReset),
            ex => HandleApplyOrResetException(ex, version, isReset));
    }

    private IEnumerator ApplyResolvedVersionCoroutine(CustomBaseVersion version, bool isReset)
    {
        BeginApplyProgress(isReset ? "Preparing reset..." : "Checking avatar...", 0f);
        if (!isReset)
        {
            bool migrated = false;
            yield return ResolveLegacyVersionCoroutine(version, resolved => { version = resolved; migrated = true; });
            if (migrated && !MCBUtils.IsVersionDownloaded(version))
            {
                yield return DownloadVersionCoroutine(version, true);
                yield break;
            }
        }
        session.serializedObject.Update();
        yield return RunPreparedMutation(ThenShowApplying(PrepareVersionSwitchCoroutine(version, isReset), isReset),
            () => ApplyOrResetFollowingAnimations(version, isReset));
    }

    private IEnumerator DownloadWithMigrationCoroutine(CustomBaseVersion version, bool apply)
    {
        if (apply) yield return ResolveLegacyVersionCoroutine(version, resolved => version = resolved);
        yield return DownloadVersionCoroutine(version, apply);
    }

    // A legacy migration changes the original and therefore the offered files. Never carry the old version object
    // across that boundary, even when its version number happens to be the same.
    private IEnumerator ResolveLegacyVersionCoroutine(CustomBaseVersion requested, Action<CustomBaseVersion> resolved)
    {
        if (requested == null) throw new ArgumentNullException(nameof(requested));
        string token = session.authToken;
        var target = session.customBaseTarget;
        // A manual migration, or a retry after discovery failed, also needs a fresh offer before its first apply.
        bool migrated = target != null && target.appliedCustomBaseAssetId == 0
            && target.legacyOriginal?.migrated == true && target.legacyOriginal.assetId == requested.assetId
            && session.HasServerAccess;
        yield return LegacyMigrationService.PrepareApply(session, requested.assetId, () => migrated = true, requested);
        if (!migrated) yield break;
        AvatarAssetDiscoveryResponse offered = null;
        string error = null;
        yield return AvatarAssetDiscoveryService.DiscoverAssetsCoroutine(session.authToken, session.GetDetectedAvatarFbxPaths(), true,
            (response, failure) => { offered = response; error = failure; }, session.customBaseTarget);
        if (target == null || session.authToken != token)
            throw new InvalidOperationException("The avatar or signed-in account changed while refreshing migrated versions; apply again.");
        var asset = offered?.assets?.FirstOrDefault(a => a.id == requested.assetId)
            ?? throw new InvalidOperationException(error ?? "This asset is not offered for the migrated avatar's original base.");
        session.Select(asset, refreshVersions: false);
        session.RedetectBaseFbx();
        var fetch = AsyncVersionService.Instance.FetchVersionsAsync(session.GetCurrentFBXPath(), session.authToken,
            asset.id, useCache: false, sourceVersionKey: OriginalBaseLibrary.ActiveKey(asset));
        while (!fetch.IsCompleted) yield return null;
        var (versions, recommended, fetchError) = fetch.GetAwaiter().GetResult();
        if (target == null || session.authToken != token)
            throw new InvalidOperationException("The avatar or signed-in account changed while refreshing migrated versions; apply again.");
        var version = SelectMigratedVersion(requested, versions, fetchError);
        session.serverVersions = versions;
        session.recommendedVersion = recommended;
        session.selectedVersionForAction = version;
        session.RequestRefresh(McbViewParts.All);
        resolved(version);
    }

    internal static CustomBaseVersion SelectMigratedVersion(CustomBaseVersion requested, IEnumerable<CustomBaseVersion> offered, string error)
    {
        return offered?.FirstOrDefault(v => v.assetId == requested.assetId && v.version == requested.version)
            ?? throw new InvalidOperationException(error ?? $"Version {requested.version} is not offered for the migrated avatar's original base.");
    }

    private void HandleApplyOrResetException(Exception ex, CustomBaseVersion version, bool isReset)
    {
        string operation = isReset ? "Reset" : "Apply";
        string versionLabel = version != null ? version.version : "null";
        string message = $"{operation} failed: {ex.GetBaseException().Message}";
        MCBLogger.LogError($"[VersionActions] {operation} failed unexpectedly. version={versionLabel} reset={isReset}: {ex}");
        RollbackActiveTransition();
        session.warningsModule?.AddWarning(message, MessageType.Error, $"{operation} failed");
        session.isDownloading = false;
        advancedMeshPreparationPreloads.Clear();
        FinishApplyProgress(false);
        session.RequestRefresh(McbViewParts.All);
        session.Repaint();
    }

    private IEnumerator PrepareVersionSwitchCoroutine(CustomBaseVersion version, bool isReset)
    {
        int generation = applyProgressGeneration;
        var requestedTarget = session.customBaseTarget;
        string requestedToken = session.authToken;
        if (!isReset)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            MCBVersionDelivery.ApplyLocalDelivery(version);
            var logic = fileManagerService.PrepareLogicPrefabCoroutine(MCBUtils.GetLogicPackagePath(version));
            while (logic.MoveNext()) yield return logic.Current;
            foreach (var patch in version.versionFiles ?? Array.Empty<ModelFileData>())
            {
                if (patch == null || !string.Equals(patch.role, "PATCH", StringComparison.OrdinalIgnoreCase) ||
                    !NativeMeshPayloadService.IsAdvancedMeshPatchTransform(patch.transform)) continue;
                string targetPath = ResolveTargetFbxPath(version, patch, GetCurrentFBXPath());
                string originalPath = ResolvePayloadKeyPath(version, patch, targetPath, "native mesh preparation");
                var prepare = NativeMeshPayloadService.MaterializeEncryptedPayloadAssetCoroutine(
                    version, patch, ResolveVersionPatchPath(version, patch), originalPath, fileManagerService,
                    (progress, label) => { if (generation == applyProgressGeneration) ReportApplyProgress(Mathf.Lerp(0.05f, 0.30f, progress), label); },
                    _ => { }, GetAdvancedMeshPreparationPreload(version, patch));
                while (prepare.MoveNext()) yield return prepare.Current;
            }
        }
        while (EditorApplication.isCompiling || EditorApplication.isUpdating) yield return null;
        if (requestedTarget == null || session.customBaseTarget != requestedTarget || session.authToken != requestedToken)
            throw new InvalidOperationException("The avatar or signed-in account changed while preparing the version switch.");

    }

    // The Action boundary prevents a mutation phase from silently becoming a yielding coroutine.
    // The switch itself runs in one editor frame (one Undo step) and the Inspector cannot repaint meanwhile: say so first.
    private IEnumerator ThenShowApplying(IEnumerator preparation, bool isReset)
    {
        while (preparation.MoveNext()) yield return preparation.Current;
        BeginApplyProgress(isReset ? "Resetting... Unity may pause for a moment" : "Applying version... Unity may pause for a moment", 0f);
        yield return null;
    }

    internal static IEnumerator RunPreparedMutation(IEnumerator preparation, Action mutate)
    {
        while (preparation.MoveNext()) yield return preparation.Current;
        mutate();
    }

    /// <summary>
    /// A version (or a reset) can move the avatar's bones: Ultirex V5 puts TopFut between the knee and the ankle. The
    /// person's own animations that reached the moved bones by their former paths (a toe curl layer) follow them, as
    /// Toolkit's AnimationPaths fixes any rename or move: shared clips are copied for this avatar, package and model clips
    /// are left alone. MCB's own logic clips already use the new paths. Only after a switch that finished.
    /// </summary>
    private void ApplyOrResetFollowingAnimations(CustomBaseVersion version, bool isReset)
    {
        var target = session.customBaseTarget;
        var root = target != null ? AvatarPaths.Root(target) : null;
        // The whole switch is one Undo step: the install or reset, the objects kept on the body, the release from before
        // MCB, the animation paths and the pose ledger.
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(isReset ? "Go back to the original base" : "Install MCB version");
        // Undo puts back the whole avatar as it was, including what changes without being recorded (renderer bounds the
        // skinning recomputes, objects a constraint moves while the version's logic comes and goes).
        if (root != null)
            Undo.RegisterCompleteObjectUndo(root.GetComponentsInChildren<Transform>(true).SelectMany(t => new UnityEngine.Object[] { t, t.gameObject })
                .Concat(root.GetComponentsInChildren<SkinnedMeshRenderer>(true)).ToArray(), isReset ? "Go back to the original base" : "Install MCB version");
        try
        {
            // Nothing installed: going back has no version to remove (a migrated body still comes back).
            if (isReset && target != null && target.appliedCustomBaseAssetId == 0 && string.IsNullOrEmpty(target.appliedCustomBaseVersionString)
                && NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(root).Count == 0)
            {
                string restored = LegacyMigrationService.RestoreOriginal(root);
                if (restored != null) MCBLogger.Log($"[MCB] The avatar is back on {restored}, as before its migration.");
                session.RequestRefresh(McbViewParts.All);
                return;
            }
            // A version installed with a pose ledger: going back (or the first half of a reinstall or switch) puts back
            // exactly what the person had. A fresh install records the avatar first.
            var start = VersionPoseLedgerService.CaptureState(root);
            poseRestore = target != null && target.appliedCustomBaseAssetId != 0 ? VersionPoseLedgerService.BeginRestore(target, root) : null;
            poseSnapshot = !isReset && poseRestore == null && target != null
                ? VersionPoseLedgerService.Capture(root, VersionPoseLedgerService.ReferenceModels(target, root)) : null;
            if (!ApplyOrResetWithLedger(version, isReset, target, root))
                // Failed and rolled back: what Undo does not cover comes back from the record of the start.
                VersionPoseLedgerService.RestoreSnapshot(start);
        }
        finally
        {
            poseSnapshot = null;
            poseRestore = null;
            placements = null;
            Undo.CollapseUndoOperations(undoGroup);
            Undo.IncrementCurrentGroup();
        }
    }

    // The pose ledger of the switch running now (see VersionPoseLedgerService); null outside it.
    private VersionPoseLedgerService.Snapshot poseSnapshot;
    private VersionPoseLedgerService.PendingRestore poseRestore;

    /// <summary>
    /// Inside an install over an installed version, once the previous version is removed: the avatar goes back to what
    /// the person had, and the new install starts from a fresh record of it (nothing accumulates over reinstalls).
    /// </summary>
    private void RestorePoseLedgerBeforeInstall(Transform root)
    {
        var target = session.customBaseTarget;
        if (poseRestore == null || target == null) return;
        VersionPoseLedgerService.FinishRestore(target, poseRestore);
        poseRestore = null;
        poseSnapshot = VersionPoseLedgerService.Capture(root, VersionPoseLedgerService.ReferenceModels(target, root));
        // Objects on the armature keep their place relative to the person's body, as on a first install.
        if (placements != null) placements = AccessoryPlacements.Capture(root);
    }

    private AccessoryPlacements placements;

    /// <returns>False when the switch did not happen (it was rolled back).</returns>
    private bool ApplyOrResetWithLedger(CustomBaseVersion version, bool isReset, MyCustomBase target, Transform root)
    {
        Orbiters.Toolkit.Editor.Animations.PathSnapshot before = null;
        try { if (root != null) before = Orbiters.Toolkit.Editor.Animations.AnimationPaths.Snapshot(root.gameObject); }
        catch (Exception ex) { MCBLogger.LogWarning("[MCB] Could not record the avatar's animation paths before the switch: " + ex.Message); }
        // Going back with a ledger puts every object exactly back: objects on the armature need no separate keeping.
        bool ledgerReset = isReset && poseRestore != null;
        placements = root != null && !ledgerReset ? AccessoryPlacements.Capture(root) : null;
        ApplyOrResetCore(version, isReset);
        if (target == null) return true;
        bool switched = isReset
            ? target.appliedCustomBaseAssetId == 0
            : version != null && target.appliedCustomBaseAssetId == version.assetId && target.appliedCustomBaseVersionString == version.version;
        if (!switched) return false;
        if (!isReset && poseSnapshot != null)
        {
            int carried = VersionPoseLedgerService.CarryPose(poseSnapshot);
            if (carried > 0) MCBLogger.Log($"[MCB] {carried} bone(s) kept the avatar's own pose on the version.");
        }
        try
        {
            int kept = placements?.Restore(root, BaseBoneNames(target, root)) ?? 0;
            if (kept > 0) MCBLogger.Log($"[MCB] {kept} object(s) on the armature (hair, props, contacts) kept their place on the body the "
                + (isReset ? "reset" : "version") + " moved the bones of.");
            int rebound = placements?.Rebind(root, renderer => IsBaseRenderer(target, renderer)) ?? 0;
            if (rebound > 0) MCBLogger.Log($"[MCB] {rebound} mesh(es) skinned to those bones (clothing, hoodies) kept their place: copies in {AccessoryPlacements.ReboundFolder}.");
        }
        catch (Exception ex) { MCBLogger.LogWarning("[MCB] Objects on the armature could not keep their place: " + ex.Message); }
        // An avatar migrated from a release made before MCB: that release is the person's original base, not the model the
        // migration put under it.
        if (isReset)
            try
            {
                string restored = LegacyMigrationService.RestoreOriginal(root);
                if (restored != null)
                {
                    session.serializedObject.Update();
                    UpdateCurrentBaseFbxHash();
                    session.RequestRefresh(McbViewParts.All);
                    MCBLogger.Log($"[MCB] The avatar is back on {restored}, as before its migration.");
                }
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning("[MCB] The release from before MCB could not be restored: " + ex);
                session.warningsModule?.AddWarning("The avatar is on its original base model, but what it had before its migration could not be restored: "
                    + ex.Message, MessageType.Warning, "Reset");
            }
        // After the renderers and assets: poses, parents and order as the person had them.
        if (ledgerReset)
        {
            VersionPoseLedgerService.FinishRestore(target, poseRestore);
            poseRestore = null;
        }
        if (before != null)
            try
            {
                var plan = Orbiters.Toolkit.Editor.Animations.AnimationPaths.Plan(root.gameObject, before, null, broken: false);
                if (plan.Fixable > 0)
                {
                    var applied = Orbiters.Toolkit.Editor.Animations.AnimationPaths.Apply(plan, everywhere: false,
                        undoName: isReset ? "Animations follow the original bones" : "Animations follow the version's bones");
                    MCBLogger.Log($"[MCB] {applied.Paths} animation path(s) in {applied.Assets} asset(s) now follow the bones the "
                        + (isReset ? "reset" : "version") + $" moved ({applied.Copies} shared asset(s) copied for this avatar).");
                    if (applied.Skipped.Count > 0)
                        session.warningsModule.AddWarning("Some animations still use the former bone paths and were left as they are: "
                            + string.Join("; ", applied.Skipped), MessageType.Warning, "Animation paths");
                }
            }
            catch (Exception ex) { MCBLogger.LogWarning("[MCB] Animations could not follow the moved bones: " + ex.Message); }
        if (!isReset && poseSnapshot != null) VersionPoseLedgerService.Record(target, poseSnapshot);
        else if (!isReset) MCBLogger.LogWarning("[MCB] The avatar's pose could not be recorded for this install: going back uses the model's rest pose.");
        return true;
    }

    /// <summary>The armature's own bones after a switch: the base models' transforms and the base renderers' bones, by name.</summary>
    private static HashSet<string> BaseBoneNames(MyCustomBase target, Transform root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // Only the first base model is the base: the list also names the accessories' models found on the avatar.
        var model = target.baseFbxFiles?.FirstOrDefault(m => m != null);
        if (model != null) foreach (var t in model.GetComponentsInChildren<Transform>(true)) names.Add(t.name);
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (IsBaseRenderer(target, renderer)) foreach (var bone in renderer.bones) if (bone != null) names.Add(bone.name);
        return names;
    }

    /// <summary>A base renderer: its mesh from the first base model, or a version's own mesh (MCB's folder, not the rebound copies).</summary>
    private static bool IsBaseRenderer(MyCustomBase target, SkinnedMeshRenderer renderer)
    {
        if (renderer == null || renderer.sharedMesh == null) return false;
        string meshPath = AssetDatabase.GetAssetPath(renderer.sharedMesh);
        var model = target.baseFbxFiles?.FirstOrDefault(m => m != null);
        if (model != null && string.Equals(meshPath, AssetDatabase.GetAssetPath(model), StringComparison.OrdinalIgnoreCase)) return true;
        return meshPath.StartsWith("Assets/MCB/", StringComparison.OrdinalIgnoreCase)
               && !meshPath.StartsWith(AccessoryPlacements.ReboundFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Objects of the person's own on the armature (a hair's root under Head, horns, props, contacts). A version can move a
    /// bone's pivot while the body around it stays (Ultirex V5 raises Head and moves its child bones back down): such an
    /// object, left at its offset from the bone, would move with the pivot. After the switch it moves as the bone's own
    /// child bones did instead (not at all when the body stayed), keeping its rotation. Objects MCB itself moved, and
    /// objects under a bone the version reshaped around (its child bones moved differently) are left as they are.
    /// </summary>
    private sealed class AccessoryPlacements
    {
        private sealed class Entry { public Transform transform, parent; public Vector3 local, position; public Quaternion localRotation, rotation; }
        private readonly List<Entry> entries = new List<Entry>();
        // Every armature transform's place on the avatar before the switch (children of the bones it then compares).
        private readonly Dictionary<Transform, Vector3> placesBefore = new Dictionary<Transform, Vector3>();
        // Each transform's matrix on the avatar before the switch, and the bones whose pivot alone moved with how the body
        // around them moved (found by Restore).
        private readonly Dictionary<Transform, Matrix4x4> matricesBefore = new Dictionary<Transform, Matrix4x4>();
        private readonly Dictionary<Transform, Vector3> pivots = new Dictionary<Transform, Vector3>();
        public const string ReboundFolder = "Assets/MCB/rebound";

        public static AccessoryPlacements Capture(Transform root)
        {
            var result = new AccessoryPlacements();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || t.parent == null) continue;
                result.placesBefore[t] = root.InverseTransformPoint(t.position);
                result.matricesBefore[t] = root.worldToLocalMatrix * t.localToWorldMatrix;
                result.entries.Add(new Entry { transform = t, parent = t.parent, local = t.localPosition, localRotation = t.localRotation,
                    position = root.InverseTransformPoint(t.position), rotation = Quaternion.Inverse(root.rotation) * t.rotation });
            }
            return result;
        }

        public int Restore(Transform root, HashSet<string> baseBones)
        {
            int moved = 0;
            foreach (var group in entries.Where(e => e.transform != null && e.parent != null && e.transform.parent == e.parent && baseBones.Contains(e.parent.name))
                         .GroupBy(e => e.parent))
            {
                var bone = group.Key;
                if (!placesBefore.TryGetValue(bone, out var boneBefore)) continue;
                var boneShift = root.InverseTransformPoint(bone.position) - boneBefore;
                // How the body around the bone moved: its own child bones that were there before.
                // Only a pivot moved on its own: the bone's own child bones (two or more) all shifted alike, within 2 mm, and
                // the bone by more than a millimetre apart from them. Child bones that moved differently mean the version
                // reshaped that part (a longer arm, a bent ear): its objects keep following the bone.
                var shifts = group.Where(e => baseBones.Contains(e.transform.name)).Select(e => root.InverseTransformPoint(e.transform.position) - e.position).ToList();
                if (shifts.Count < 2) continue;
                var bodyShift = shifts.Aggregate(Vector3.zero, (sum, v) => sum + v) / shifts.Count;
                if (shifts.Any(v => (v - bodyShift).sqrMagnitude > 4e-6f)) continue;
                if ((boneShift - bodyShift).sqrMagnitude < 1e-6f) continue;
                pivots[bone] = bodyShift;
                foreach (var e in group.Where(e => !baseBones.Contains(e.transform.name)))
                {
                    // Objects the switch itself placed keep MCB's place.
                    if ((e.transform.localPosition - e.local).sqrMagnitude > 1e-10f || Quaternion.Angle(e.transform.localRotation, e.localRotation) > 0.01f) continue;
                    Undo.RecordObject(e.transform, "Keep objects on the body");
                    e.transform.position = root.TransformPoint(e.position + bodyShift);
                    e.transform.rotation = root.rotation * e.rotation;
                    EditorUtility.SetDirty(e.transform);
                    moved++;
                }
            }
            return moved;
        }

        /// <summary>
        /// Meshes of the person's own skinned straight to a bone whose pivot alone moved (a hoodie on Head) would rise with
        /// the pivot: their bind poses for those bones are rewritten so each vertex lands where it was, moved as the body
        /// around the bone moved. The mesh is a copy in <see cref="ReboundFolder"/> (a model's mesh cannot be changed);
        /// the renderer points at it in the switch's Undo step. Base renderers are left to the version. After Restore.
        /// </summary>
        public int Rebind(Transform root, Func<SkinnedMeshRenderer, bool> isBase)
        {
            if (pivots.Count == 0) return 0;
            int rebound = 0;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || isBase(renderer)) continue;
                var bones = renderer.bones;
                var poses = mesh.bindposes;
                if (poses.Length != bones.Length) continue;
                bool changed = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    var bone = bones[i];
                    if (bone == null || !pivots.TryGetValue(bone, out var shift) || !matricesBefore.TryGetValue(bone, out var before)) continue;
                    // On the avatar: after = bone now * pose', wanted = shift * bone before * pose.
                    var now = root.worldToLocalMatrix * bone.localToWorldMatrix;
                    poses[i] = now.inverse * Matrix4x4.Translate(shift) * before * poses[i];
                    changed = true;
                }
                if (!changed) continue;
                var copy = UnityEngine.Object.Instantiate(mesh);
                copy.name = mesh.name;
                copy.bindposes = poses;
                string folder = ReboundFolder + "/" + Safe(root.name);
                EnsureFolder(folder);
                string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(renderer.name) + ".asset");
                AssetDatabase.CreateAsset(copy, path);
                Undo.RecordObject(renderer, "Keep meshes on the body");
                renderer.sharedMesh = copy;
                EditorUtility.SetDirty(renderer);
                rebound++;
            }
            if (rebound > 0) AssetDatabase.SaveAssets();
            return rebound;
        }

        private static string Safe(string name) => string.Concat((name ?? "Avatar").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = MCBUtils.ToUnityPath(Path.GetDirectoryName(folder));
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    // The proofs of the switch running now (null outside its synchronous stretch). Never kept across a yield: the files
    // may change between frames. Inside it, a file's hash is read from its bytes once and reused while its length and
    // write time stay the same (ContentHash.Operation), instead of once per check.
    private ContentHash.Operation fileOperation;
    // Scopes opened after an import inside the switch, disposed with it.
    private readonly List<ContentHash.Operation> renewedFileOperations = new List<ContentHash.Operation>();

    /// <summary>
    /// After an import or a reimport inside the switch: Unity and other tools' postprocessors may have rewritten a file the
    /// scope already proved, so what the switch decides from here on is proven again, in a new scope.
    /// </summary>
    private void RenewFileOperationAfterImport()
    {
        if (fileOperation == null) return;
        var fresh = new ContentHash.Operation();
        renewedFileOperations.Add(fresh);
        fileOperation = fresh;
    }

    private void ApplyOrResetCore(CustomBaseVersion version, bool isReset, bool finalize = true)
    {
        // One synchronous stretch (a reset nested in a custom version's install joins that one).
        var outer = fileOperation;
        int renewedBefore = renewedFileOperations.Count;
        using (ContentHash.Operation.Join(outer, out var operation))
        {
            fileOperation = operation;
            try
            {
                ApplyOrResetCoreInOperation(version, isReset, finalize);
            }
            finally
            {
                for (int i = renewedFileOperations.Count - 1; i >= renewedBefore; i--)
                {
                    renewedFileOperations[i].Dispose();
                    renewedFileOperations.RemoveAt(i);
                }
                fileOperation = outer;
            }
        }
    }

    private void ApplyOrResetCoreInOperation(CustomBaseVersion version, bool isReset, bool finalize)
    {
        // The intro spans frames: a refit may have started meanwhile.
        if (RefitBlocksSwitch())
        {
            FinishApplyProgress(false);
            return;
        }
        MCBPerformance.PauseForeground();
        session.serializedObject.Update();
        if (!isReset) MCBVersionDelivery.ApplyLocalDelivery(version);
        float startingProgress = applyProgressBase > 0f
            ? 0f
            : (ApplyProgress.IsRunning ? Mathf.Max(ApplyProgress.Progress, 0.18f) : 0f);
        BeginApplyProgress(isReset ? "Preparing reset..." : "Preparing version switch...", startingProgress);
        var root = AvatarPaths.Root(session.customBaseTarget);
        bool preserveBlendshapeValues = session.customBaseTarget != null && session.customBaseTarget.preserveBlendshapeValuesOnVersionSwitch;
        var blendshapeSnapshot = preserveBlendshapeValues ? CaptureBlendshapeState(root) : null;
        var previousVersion = ResolvePersistedAppliedVersion();
        var versionForAssets = isReset ? (previousVersion ?? session.selectedVersionForAction) : version;
        bool previousUsesAdvancedMesh = NativeMeshPayloadService.VersionUsesAdvancedMesh(previousVersion);
        bool versionUsesAdvancedMesh = NativeMeshPayloadService.VersionUsesAdvancedMesh(versionForAssets);
        var transitionVersions = GetDistinctTransitionVersions(previousVersion, versionForAssets);
        bool isAdvancedTransition = previousUsesAdvancedMesh || versionUsesAdvancedMesh;
        var profile = new ApplyTimingProfile(versionForAssets, isReset, versionUsesAdvancedMesh);
        var dynamicNormalsService = new DynamicNormalsService(session);
        profile.Mark("Setup target, version state, and blendshape snapshot");

        MCBLogger.Log($"[VersionActions] ApplyOrReset start (reset={isReset}, version={(version != null ? version.version : "null")})");

        // Leaving the original state records the avatar's own models; while a version is applied they are what
        // transitions and the reset restore, whatever the version or the selected asset name as sources.
        var target = session.customBaseTarget;
        if (previousVersion == null && target.appliedCustomBaseAssetId == 0)
        {
            if (!isReset)
            {
                Undo.RecordObject(target, "Record original models");
                target.versionOriginalModels = target.baseFbxFiles.Where(model => model != null).ToList();
                RememberOriginalBaseBlendshapes(root, blendshapeSnapshot ?? CaptureBlendshapeState(root));
            }
        }
        else if (target.versionOriginalModels.Count > 0 && !target.baseFbxFiles.SequenceEqual(target.versionOriginalModels))
        {
            session.serializedObject.ApplyModifiedProperties();
            FileManagerService.SetCreatorSourceFiles(target, target.versionOriginalModels);
            session.serializedObject.Update();
        }

        string fbxPath = GetCurrentFBXPath();
        profile.Mark("Resolved current FBX path");
        if (string.IsNullOrEmpty(fbxPath))
        {
            profile.Done("ABORT missing FBX path");
            FinishApplyProgress(false);
            return;
        }

        if (activeTransitionRollback != null)
            throw new InvalidOperationException("Another version transition is still active.");
        activeTransitionRollback = VersionTransitionRollbackSnapshot.Capture(
            GetTransitionAffectedFbxPaths(transitionVersions, fbxPath), session.customBaseTarget, session);

        ModeService.Restore(session.customBaseTarget);
        // The person's values without the version's modes: the reset gives them back by name once the original meshes are on.
        var resetSnapshot = isReset ? CaptureBlendshapeState(root) : null;
        // The original base has none of the custom base's blendshapes: remember their values for its next install.
        if (isReset) RememberVersionBlendshapes(root, previousVersion);
        NativeRendererLayoutService.Restore(session.customBaseTarget);
        NativeMeshPayloadService.RestoreOriginalBoneParents(session.customBaseTarget);
        MCBReFitIntegration.SaveVersionFits(session.customBaseTarget, previousVersion);
        MCBReFitIntegration.RestoreOriginalAssetMeshes(session.customBaseTarget);
        session.serializedObject.Update();

        var previousAffectedFbxPaths = GetResetAffectedFbxPaths(previousVersion ?? versionForAssets, fbxPath);
        if (!isAdvancedTransition)
        {
            dynamicNormalsService.Remove(previousAffectedFbxPaths);
        }
        fileManagerService.RemoveExistingLogic(root);
        ReportApplyProgress(0.05f, "Removing existing MCB objects...");
        profile.Mark(isAdvancedTransition
            ? "Deferred DynamicNormals cleanup to transactional full FBX-state restoration and removed existing MCB logic"
            : "Removed current DynamicNormals objects and existing MCB logic");
        
        bool success = false;
        Exception operationException = null;
        var restoredPreviousModels = new List<string>();
        if (isReset)
        {
            try
            {
                ReportApplyProgress(0.10f, "Restoring base mesh state...");
                if (versionUsesAdvancedMesh)
                {
                    MCBLogger.Log("[VersionActions] Advanced mesh reset will restore renderer meshes from the imported default FBX asset and skip FBX reimport when the source file already matches its backup.");
                }

                RestoreBackupsForVersion(versionForAssets, fbxPath, !versionUsesAdvancedMesh);
                if (versionUsesAdvancedMesh)
                {
                    int restoredTransforms = NativeMeshPayloadService.RestoreOriginalAuthoringPoseFromFbx(root, new[] { fbxPath });
                    if (restoredTransforms == 0)
                    {
                        throw new InvalidOperationException("Advanced mesh reset could not safely restore the canonical FBX armature pose.");
                    }
                    var missingRenderers = new List<string>();
                    int restoredRenderers = NativeMeshPayloadService.RestoreOriginalMeshesFromFbx(
                        root,
                        versionForAssets,
                        GetResetAffectedFbxPaths(versionForAssets, fbxPath),
                        session.customBaseTarget,
                        missingRenderers: missingRenderers);
                    if (restoredRenderers == 0 && missingRenderers.Count == 0)
                    {
                        throw new InvalidOperationException("Advanced mesh reset could not safely restore any renderer from the original FBX.");
                    }
                    WarnSkippedRenderers(missingRenderers);
                    NativeMeshPayloadService.RemoveGeneratedBones(session.customBaseTarget);
                    // After the skeleton is final: the Animator caches the bones it binds.
                    ApplyDefaultAvatarToRootForReset(root, versionForAssets);
                }

            }
            catch (Exception e)
            {
                operationException = e;
            }
        }
        else
        {
            try
            {
                if (version == null) throw new ArgumentNullException(nameof(version), "A version must be provided to apply.");
                if (isAdvancedTransition)
                {
                    ReportApplyProgress(0.10f, "Restoring original FBX state...");
                    // Shared meshes the next version reuses may stay, unless it maps renderers by material slot names:
                    // its layout must start from the original renderers.
                    bool keepShared = VersionCustomization.Read(version.extraCustomization).rendererLayout.IsEmpty;
                    RestoreOriginalFbxStateForTransition(root, transitionVersions, fbxPath,
                        previousVersion != null && !previousUsesAdvancedMesh, keepShared ? version : null);
                    NativeMeshPayloadService.RemoveGeneratedBones(session.customBaseTarget);
                    RestorePoseLedgerBeforeInstall(root);
                    profile.Mark("Restored original FBX renderer and armature state for advanced mesh transition");
                }
                else if (previousVersion != null)
                {
                    ReportApplyProgress(0.10f, "Restoring models of the previous version...");
                    restoredPreviousModels = RestoreModelsLeftByPreviousVersion(root, previousVersion, version, fbxPath);
                    RestorePoseLedgerBeforeInstall(root);
                    profile.Mark($"Restored {restoredPreviousModels.Count} models the previous version changed");
                }
                // The models this version gives its own Avatar: their import settings are kept for the reset.
                foreach (string avatarModelPath in GetCustomAvatarFbxPaths(version))
                    AvatarDefinitionGenerationService.BackupOriginalImportSettings(avatarModelPath);
                NativeRendererLayoutService.Apply(session.customBaseTarget, version, GetCurrentFBXPaths());
                ApplyVersionModelFilePatches(version, fbxPath);
            }
            catch (Exception e)
            {
                operationException = e;
            }
        }

        if (operationException == null)
        {
            try
            {
                success = true;
                profile.Mark(isReset ? "Restored base/native meshes" : "Applied model file patches");
                if (!isReset && versionUsesAdvancedMesh)
                {
                    advancedMeshPreparationPreloads.Clear();
                }
            }
            catch (Exception e)
            {
                operationException = e;
            }
        }

        if (operationException != null)
        {
            profile.Mark("Model patch/reset failed");
            MCBLogger.LogError($"[MCB] Operation failed: {operationException.Message}");
            session.warningsModule?.AddWarning(operationException.Message, MessageType.Error, isReset ? "Reset failed" : "Apply failed");
            if (activeTransitionRollback != null)
            {
                RollbackActiveTransition();
            }
            advancedMeshPreparationPreloads.Clear();
            FinishApplyProgress(false);
        }
        
        if (success)
        {
            var affectedFbxPaths = isReset
                ? GetResetAffectedFbxPaths(versionForAssets, fbxPath)
                : GetAffectedFbxPaths(version, fbxPath).Union(restoredPreviousModels, StringComparer.OrdinalIgnoreCase).ToList();
            var fbxImportPaths = isReset
                ? (versionUsesAdvancedMesh ? new List<string>() : affectedFbxPaths)
                : GetFbxImportPaths(version, fbxPath);
            ReportApplyProgress(versionUsesAdvancedMesh ? 0.72f : 0.30f, versionUsesAdvancedMesh ? "Advanced mesh data prepared..." : "Preparing FBX imports...");
            profile.Mark("Resolved affected FBX/import path lists");
            int importIndex = 0;
            foreach (string affectedPath in fbxImportPaths)
            {
                importIndex++;
                ReportApplyProgress(Mathf.Lerp(0.30f, 0.55f, fbxImportPaths.Count == 0 ? 1f : importIndex / (float)fbxImportPaths.Count), $"Importing FBX {importIndex}/{fbxImportPaths.Count}...");
                MCBLogger.Log($"[VersionActions] Importing modified FBX at {affectedPath}");
                AssetDatabase.ImportAsset(affectedPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                RenewFileOperationAfterImport();
            }
            MCBLogger.Log(fbxImportPaths.Count > 0
                ? "[VersionActions] FBX import completed."
                : "[VersionActions] No FBX import required for this version operation.");
            
            // Model imports above are synchronous; preparation finished any asynchronous package import.
            MCBLogger.Log("[VersionActions] Synchronous model imports completed.");
            profile.Mark("FBX import and pending editor update wait");
            //  --- END CRITICAL FIX ---
            
            if (isReset)
            {
                if (versionUsesAdvancedMesh)
                {
                    ReportApplyProgress(0.78f, "Applying default avatar...");
                    if (NativeMeshPayloadService.RestoreOriginalAuthoringPoseFromFbx(root, new[] { fbxPath }) == 0)
                        throw new InvalidOperationException("Advanced mesh reset could not safely finalize the canonical FBX armature pose.");
                    ApplyDefaultAvatarToRootForReset(root, versionForAssets);
                }
                else
                {
                    ReportApplyProgress(0.60f, "Restoring original import settings...");
                    RestoreImportersForReset(root, versionForAssets, fbxPath);
                }
                MCBLogger.Log("[VersionActions] Default avatar import completed.");
                profile.Mark("Default avatar import/reset wait");
                if (versionUsesAdvancedMesh)
                {
                    profile.Mark("Restored native mesh reset authoring pose");
                    MCBLogger.Log("[VersionActions] Restored source authoring pose after advanced mesh reset.");
                }
            }
            else if (versionForAssets != null && versionForAssets != VersionListDrawer.RESET_VERSION)
            {
                ReportApplyProgress(versionUsesAdvancedMesh ? 0.78f : 0.60f, "Applying avatar definition...");
                ApplyAvatarImportsForVersion(root, versionForAssets, isReset);
                profile.Mark("Applied avatar import settings for version");
                MCBLogger.Log("[VersionActions] Avatar import completed.");
                profile.Mark("Avatar import/update wait");
                if (versionUsesAdvancedMesh)
                {
                    ReportApplyProgress(0.82f, "Restoring advanced mesh authoring pose...");
                    ApplyAdvancedMeshAuthoringPose(versionForAssets, fbxPath);
                    profile.Mark("Restored advanced mesh authoring pose");
                    MCBLogger.Log("[VersionActions] Restored payload authoring pose after advanced mesh apply.");
                }

                if (!isReset)
                {
                    string packagePath = MCBUtils.GetLogicPackagePath(versionForAssets);
                    fileManagerService.InstantiatePreparedLogicPrefab(packagePath, root);
                    profile.Mark("Imported/instantiated logic prefab");
                }
            }
            // Check feature flags
            bool hasCustomVeins = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "customVeins");
            bool hasDynamicNormalBody = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "dynamicNormalBody");
            bool hasDynamicNormalFlexing = !isReset && ExtraCustomizationUtils.HasFlag(version?.extraCustomization, "dynamicNormalFlexing");
            var normalSelection = VersionCustomization.Read(version?.extraCustomization).dynamicNormalBlendshapes;
            bool shouldApplyDynamicNormals = (!isReset && normalSelection.Count > 0 || hasDynamicNormalBody || hasDynamicNormalFlexing) && session.customBaseTarget.useDynamicNormals;
            
            // Apply or remove dynamic normals based on version feature flags
            // Complete these synchronous mutations inside the version transaction.
            if (!shouldApplyDynamicNormals && !versionUsesAdvancedMesh)
            {
                MCBLogger.Log("[VersionActions] Removing dynamic normals.");
                dynamicNormalsService.Remove(affectedFbxPaths);
                MCBLogger.Log("[VersionActions] Dynamic normals removal completed.");
                profile.Mark("Removed DynamicNormals and waited for editor update");
            }

            if (!versionUsesAdvancedMesh)
            {
                RefreshTargetMeshesFromFBXs(root, affectedFbxPaths, versionForAssets);
                profile.Mark("Refreshed target meshes from imported FBXs");
            }
            
            if (shouldApplyDynamicNormals)
            {
                if (versionUsesAdvancedMesh)
                {
                    MCBLogger.Log("[VersionActions] Dynamic normals are expected to be pre-baked in the advanced native mesh payload; skipping user-side regeneration.");
                    profile.Mark("Skipped user-side DynamicNormals regeneration for advanced native mesh");
                }
                else
                {
                    bool applyBody = hasDynamicNormalBody;
                    bool applyFlex = hasDynamicNormalFlexing;
                    MCBLogger.Log("[VersionActions] Applying dynamic normals.");
                    if (normalSelection.Count > 0) dynamicNormalsService.Apply(normalSelection);
                    else dynamicNormalsService.Apply(applyBody, applyFlex);
                    MCBLogger.Log("[VersionActions] Dynamic normals application completed.");
                    profile.Mark("Applied DynamicNormals and waited for editor update");
                }
            }
            
            // Apply or remove custom veins based on version feature flag
            var materialService = new MaterialService(root);
            var targetMaterialRenderers = versionUsesAdvancedMesh
                ? NativeMeshPayloadService.ResolveRenderersForSourcePaths(root, versionForAssets, affectedFbxPaths, session.customBaseTarget)
                : materialService.GetSkinnedMeshRenderersForFbxPaths(affectedFbxPaths);
            // An unencrypted version names no source renderers: its targets are the renderers carrying its meshes.
            if (targetMaterialRenderers.Count == 0 && versionUsesAdvancedMesh && !isReset)
                targetMaterialRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => renderer.sharedMesh != null
                    && NativeMeshPayloadService.IsSharedMeshForVersion(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)), versionForAssets)).ToList();
            ReportApplyProgress(0.90f, "Resolving materials...");
            profile.Mark($"Resolved material target renderers ({targetMaterialRenderers.Count})");
            
            bool materialStateChanged = false;
            if (hasCustomVeins)
            {
                // Apply custom veins
                string veinsNormalPath = MCBUtils.GetVersionVeinsTexturePath(version);
                string veinsNormalAbsolutePath = Path.GetFullPath(veinsNormalPath);

                if (File.Exists(veinsNormalAbsolutePath))
                {
                    bool veinsApplied = false;
                    foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetMaterialRenderers))
                    {
                        if (materialService.SetDetailNormalMap(renderer, veinsNormalPath, false))
                        {
                            materialService.SetDetailNormalOpacity(renderer, 1.0f, false);
                            veinsApplied = true;
                            materialStateChanged = true;
                        }
                    }

                    if (veinsApplied)
                    {
                        // Sync the toggle state with the applied state
                        EditorPrefs.SetBool(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY, true);
                        MCBLogger.Log($"[VersionActions] Custom veins applied from version {version.version}");
                    }
                }
                else
                {
                    MCBLogger.LogWarning($"[VersionActions] Custom veins file not found at: {veinsNormalPath}");
                }
            }
            else
            {
                // Remove custom veins when switching to a version without the feature or resetting
                foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetMaterialRenderers))
                {
                    if (materialService.RemoveDetailNormalMap(renderer, false))
                    {
                        materialStateChanged = true;
                    }
                }
                if (materialService.RemoveVersionVeins()) materialStateChanged = true;
                // Sync the toggle state - set to false when removing veins
                EditorPrefs.SetBool(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY, false);
                MCBLogger.Log("[VersionActions] Custom veins removed");
            }
            if (materialStateChanged)
            {
                materialService.SaveTouchedMaterials();
            }
            ReportApplyProgress(0.93f, hasCustomVeins ? "Applied custom veins..." : "Updated material state...");
            profile.Mark(hasCustomVeins ? "Applied custom veins materials" : "Removed custom veins materials");
            
            if (!isReset && version != null)
            {
                int restoredRefits = MCBReFitIntegration.RestoreVersionFits(session.customBaseTarget, version);
                session.serializedObject.Update();
                profile.Mark($"Restored {restoredRefits} saved accessory ReFits");
            }

            // Restore blendshape values by name after all mesh swaps are complete.
            if (!isReset && version != null)
            {
                ReportApplyProgress(0.95f, "Applying blendshapes and sliders...");
                if (preserveBlendshapeValues)
                {
                    var remembered = RememberedBlendshapes(version);
                    // Coming from the original base, a same-named original shape must not replace the custom base's value.
                    // Other remembered shapes only fill the shapes the avatar does not have yet (below).
                    var sliders = new HashSet<string>((version.customBlendshapes ?? new CustomBlendshapeEntry[0]).Where(e => e != null && !string.IsNullOrEmpty(e.name)).Select(e => e.name), StringComparer.Ordinal);
                    if (previousVersion == null)
                        foreach (var weights in blendshapeSnapshot.Values)
                            foreach (string name in remembered.Keys.Where(sliders.Contains)) weights.Remove(name);
                    var defaults = BuildBlendshapeDefaultLookup(version, remembered);
                    // Remembered shapes the version does not expose as sliders too (a migrated legacy release's "EarsSmall").
                    if (previousVersion == null)
                        foreach (var pair in remembered) defaults[pair.Key] = pair.Value;
                    RestoreBlendshapeState(root, blendshapeSnapshot, defaults);
                    SyncReFitTransferredBlendshapeWeights(root, version);
                    SyncBlendshapeOverridesFromCurrentWeights(root, version);
                    MCBLogger.Log($"[VersionActions] Restored blendshape values by name (saved renderers: {blendshapeSnapshot.Count}, overrides: {session.customBaseTarget.customBlendshapeOverrideNames.Count})");
                }
                else
                {
                    ApplyVersionBlendshapeValues(root, version);
                MCBLogger.Log("[VersionActions] Blendshape preservation on version switch is disabled. Applied version defaults/overrides.");
                }

                // Handle "mcb sliders" GameObject state and deletion
                var slidersTransform = root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
                var hasSliders = version.customBlendshapes != null && version.customBlendshapes.Any(e => e.isSlider);

                if (!hasSliders)
                {
                    if (slidersTransform != null)
                    {
                        MCBLogger.Log("[VersionActions] Version has no sliders. Deleting sliders GameObject.");
                        Undo.DestroyObjectImmediate(slidersTransform.gameObject);
                    }
                }
                else
                {
                    // Ensure sliders are applied/updated for the new version
                    var allSliderEntries = version.customBlendshapes.Where(e => e.isSlider).ToList();
                    List<CustomBlendshapeEntry> selectedSliders;

                    if (session.customBaseTarget.useCustomSliderSelection)
                    {
                        var savedNames = new HashSet<string>(session.customBaseTarget.customSliderSelectionNames ?? new List<string>());
                        selectedSliders = allSliderEntries.Where(e => savedNames.Contains(e.name)).ToList();
                    }
                    else
                    {
                        selectedSliders = allSliderEntries.Where(e => e.isSliderDefault).ToList();
                    }

                    MCBLogger.Log($"[VersionActions] Applying sliders for version {version.version}. Count: {selectedSliders.Count}");
                    VRCFuryService.Instance.ApplySliders(root.gameObject, session.customBaseTarget.slidersMenuName, selectedSliders);

                    // Ensure the active state is correct (ApplySliders handles creation state, but we enforce it here for existing ones too)
                    slidersTransform = root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
                    if (slidersTransform != null)
                    {
                        bool desiredState = session.customBaseTarget.useCustomSlidersState ? session.customBaseTarget.customSlidersState : true;
                        if (slidersTransform.gameObject.activeSelf != desiredState)
                        {
                            MCBLogger.Log($"[VersionActions] Setting sliders GameObject active state to: {desiredState}");
                            Undo.RecordObject(slidersTransform.gameObject, "Set Sliders Active State");
                            slidersTransform.gameObject.SetActive(desiredState);
                        }
                    }
                }
            }
            else if (isReset)
            {
                ReportApplyProgress(0.95f, "Clearing custom version controls...");
                RestoreOriginalBaseBlendshapes(root, resetSnapshot, previousVersion, preserveBlendshapeValues);
                // Clear custom overrides when resetting
                session.customBaseTarget.customBlendshapeOverrideNames.Clear();
                session.customBaseTarget.customBlendshapeOverrideValues.Clear();
                session.customBaseTarget.useCustomSliderSelection = false;
                session.customBaseTarget.customSliderSelectionNames.Clear();
                session.customBaseTarget.useCustomSlidersState = false;
                session.customBaseTarget.customSlidersState = true;
                SyncAppliedVersionBlendshapeLinkCache(null);
                SyncAppliedVersionAnimationPositionOffsetCache(null);

                // Delete sliders GameObject on reset
                var slidersTransform = root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
                if (slidersTransform != null)
                {
                    MCBLogger.Log("[VersionActions] Reset requested. Deleting sliders GameObject.");
                    Undo.DestroyObjectImmediate(slidersTransform.gameObject);
                }
            }
            profile.Mark("Applied blendshape defaults/preservation and slider state");

            if (isReset)
            {
                // Back on the original base: a later install starts from the version's default modes.
                int resetAssetId = session.customBaseTarget.appliedCustomBaseAssetId;
                session.customBaseTarget.modeChoices.RemoveAll(choice => choice.assetId == resetAssetId);
                ClearAppliedVersionState();
            }
            else
            {
                try
                {
                    PersistAppliedVersionState(version);
                    ModeService.Install(session.customBaseTarget, version);
                }
                catch (Exception ex)
                {
                    success = false;
                    MCBLogger.LogError("[VersionActions] Mode setup failed; restoring the previous version. " + ex.Message);
                }
            }
            ReportApplyProgress(0.98f, "Saving applied version state...");
            profile.Mark("Updated persisted applied-version state");
        }

        if (!success)
        {
            RollbackActiveTransition();
            session.Repaint();
            FinishApplyProgress(false);
            profile.Done("FAILED");
            return;
        }
        
        CommitActiveTransition();
        if (previousVersion != null && (isReset || !string.Equals($"{previousVersion.assetId}:{previousVersion.version}", $"{version?.assetId}:{version?.version}", StringComparison.Ordinal)))
        {
            // In the background: finding what the project still uses can read every scene and prefab once (it reports
            // and logs what it moved to the Recycle Bin).
            _ = NativeMeshPayloadService.QueueUnusedPayloadCleanup(previousVersion);
        }
        if (!finalize) return; // A saved-custom transition will commit or restore its encompassing snapshot.
        MCBLogger.Log("[VersionActions] ApplyOrResetCoroutine completed. Updating hash and applied state.");
        // Force a recalculation of current/default FBX hashes after every switch. Advanced
        // transitions can restore .originalbase bytes even though the visible mesh is a native
        // payload, and the persisted advanced marker keeps version detection authoritative.
        int hashGeneration = ++fbxHashRecalculationGeneration;
        profile.Mark(isAdvancedTransition
            ? "Started asynchronous FBX hash/state recalculation after advanced transition"
            : "Started asynchronous FBX hash/state recalculation");

        EditorUtility.SetDirty(session.customBaseTarget);
        if (versionUsesAdvancedMesh)
        {
            MCBLogger.Log("[VersionActions] Skipping synchronous project auto-save for native mesh apply. The scene remains dirty so Unity can save it normally.");
            profile.Mark("Skipped synchronous auto-save for advanced native mesh");
        }
        else
        {
            AutoSaveProjectAfterVersionSwitch();
            profile.Mark("Auto-saved assets and scenes");
        }
        session.Repaint();
        FinishApplyProgress(true);
        // Completion advances the visual generation; it must not cancel its own refresh.
        EditorCoroutineUtility.StartCoroutineOwnerless(
            RecalculateCurrentFbxHashCoroutine(hashGeneration, applyProgressGeneration));
        profile.Done();
    }

    // A refit saves and restores fits per version: switching while one runs would mix two custom bases.
    private bool RefitBlocksSwitch()
    {
        if (session == null || !MCBReFitIntegration.IsRefitRunning(session.customBaseTarget)) return false;
        session.warningsModule.AddWarning(MCBReFitIntegration.RefitRunningMessage, MessageType.Warning, "Refit running");
        session.Repaint();
        return true;
    }

    private static string GetRelativeTransformPath(Transform root, Transform target)
    {
        if (root == null || target == null) return string.Empty;
        if (target == root) return string.Empty;

        var segments = new List<string>();
        var current = target;
        while (current != null && current != root)
        {
            segments.Add(current.name);
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private void AutoSaveProjectAfterVersionSwitch()
    {
        try
        {
            AssetDatabase.SaveAssets();
            bool savedScenes = EditorSceneManager.SaveOpenScenes();
            if (!savedScenes)
            {
                MCBLogger.LogWarning("[VersionActions] Auto-save completed for assets, but one or more open scenes could not be saved.");
            }
            else
            {
                MCBLogger.Log("[VersionActions] Auto-saved assets and open scenes after version change.");
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[VersionActions] Auto-save after version change failed: {ex.Message}");
            session.warningsModule.AddWarning("The version switch succeeded, but MCB could not auto-save the project. Save the project manually to persist the scene state.", MessageType.Warning, "Auto-save failed");
        }
    }

    private IEnumerator ApplyCustomVersionCoroutine(UserCustomVersionEntry entry)
    {
        if (entry == null || !FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION)) yield break;
        if (string.IsNullOrWhiteSpace(entry.backupFbxPath) || !File.Exists(entry.backupFbxPath))
        {
            session.warningsModule?.AddWarning("The saved custom FBX no longer exists. The current version was kept.", MessageType.Error, "Custom version unavailable");
            yield break;
        }
        int generation = applyProgressGeneration;
        var requestedTarget = session.customBaseTarget;
        while (EditorApplication.isCompiling || EditorApplication.isUpdating) yield return null;
        if (requestedTarget == null || session.customBaseTarget != requestedTarget) yield break;

        var root = AvatarPaths.Root(requestedTarget);
        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrEmpty(fbxPath)) yield break;
        var previous = ResolvePersistedAppliedVersion();
        bool ReleaseApplied() => ResolvePersistedAppliedVersion() != null ||
                                 NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(root).Count > 0;
        VersionTransitionRollbackSnapshot snapshot = null;
        try
        {
            if (activeTransitionRollback != null) throw new InvalidOperationException("Another version transition is still active.");
            snapshot = VersionTransitionRollbackSnapshot.Capture(
                GetTransitionAffectedFbxPaths(GetDistinctTransitionVersions(previous), fbxPath), requestedTarget, session);
            StartApplyProgress("Applying saved custom version...", 0f);
            // Reset and custom installation are one synchronous transaction: failure must restore the release as well.
            if (ReleaseApplied())
            {
                ApplyOrResetCore(null, true, finalize: false);
                if (ReleaseApplied()) throw new InvalidOperationException("The applied version could not be reset.");
            }
            fileManagerService.RemoveExistingLogic(root);
            if (!fileManagerService.BackupExists(fbxPath)) fileManagerService.CreateBackup(fbxPath);
            File.Copy(Path.GetFullPath(MCBUtils.ToUnityPath(entry.backupFbxPath)), Path.GetFullPath(fbxPath), true);
            fileManagerService.RecordWrittenFbx(fbxPath);
            AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var fbxGameObject = session.baseFbxFilesProp.arraySize > 0
                ? session.baseFbxFilesProp.GetArrayElementAtIndex(0).objectReferenceValue as GameObject : null;
            if (!string.IsNullOrEmpty(entry.appliedAvatarAsset))
            {
                if (fbxGameObject != null) AvatarDefinitionGenerationService.BackupOriginalImportSettings(AssetDatabase.GetAssetPath(fbxGameObject));
                fileManagerService.ApplyAvatarToModel(root, fbxGameObject, entry.appliedAvatarAsset);
            }
            ClearAppliedVersionState();
            session.currentIsCustom = true;
            snapshot.Commit();
            FinishApplyProgress(true);
            StartRecalculateCurrentFbxHash();
        }
        catch (Exception ex)
        {
            RollbackActiveTransition();
            try { snapshot?.Rollback(); }
            catch (Exception rollback)
            {
                ClearAppliedVersionState();
                session.warningsModule?.AddWarning(rollback.Message, MessageType.Error, "Custom version rollback failed");
                MCBLogger.LogError($"[MCB] Custom version rollback failed: {rollback.Message}");
            }
            FinishApplyProgress(false);
            session.warningsModule?.AddWarning(ex.Message, MessageType.Error, "Custom version not applied");
            MCBLogger.LogError($"[MCB] Failed to apply custom version: {ex.Message}");
        }
        session.RequestRefresh(McbViewParts.All);
        session.Repaint();
    }

}
#endif
