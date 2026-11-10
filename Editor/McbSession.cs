#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor;

/// <summary>
/// One avatar's MCB session: the custom base's version lists, the asset they are for, the version picked for an action,
/// the account, the busy state and the operations on the avatar's files (fetch, download, apply, switch, reset, delete,
/// publish). Every view of the avatar talks to it: each Inspector on it, the creator and compare windows, Mixer's update
/// pills (<see cref="McbBaseUpdates"/>), MCP authoring and the tests. None of them owns it, so an operation runs whether or
/// not an Inspector shows the avatar, and every view sees the same state.
/// </summary>
/// <remarks>
/// Runtime state only: a domain reload ends the operations it describes and starts every session again (from the
/// component, the version folders and the caches). A session whose component is destroyed (its scene closed) is dropped
/// the next time sessions are looked up.
/// </remarks>
public sealed partial class McbSession
{
    private static readonly Dictionary<int, McbSession> ByTarget = new Dictionary<int, McbSession>();

    /// <summary>The session of this custom base, started on first use; null without a component.</summary>
    public static McbSession For(MyCustomBase target)
    {
        if (target == null) return null;
        Prune();
        int id = target.GetInstanceID();
        if (ByTarget.TryGetValue(id, out var session) && session.customBaseTarget == target) return session;
        session = new McbSession(target);
        ByTarget[id] = session;
        session.Start();
        return session;
    }

    /// <summary>The session of this custom base when one runs (no session is started for it).</summary>
    public static McbSession Find(MyCustomBase target)
    {
        if (target == null) return null;
        Prune();
        return ByTarget.TryGetValue(target.GetInstanceID(), out var session) && session.customBaseTarget == target ? session : null;
    }

    /// <summary>Every running session (each custom base some view or operation used since the last domain reload).</summary>
    public static List<McbSession> Live
    {
        get
        {
            Prune();
            return ByTarget.Values.ToList();
        }
    }

    /// <summary>A session that is not registered and starts nothing (no account read, no service events): tests.</summary>
    internal static McbSession Detached(MyCustomBase target) => new McbSession(target);

    private static void Prune()
    {
        if (ByTarget.Count == 0) return;
        foreach (var dead in ByTarget.Where(entry => entry.Value.customBaseTarget == null).ToList())
        {
            dead.Value.Stop();
            ByTarget.Remove(dead.Key);
        }
    }

    // --- Target, its serialized view, and the services the operations use ---
    public readonly MyCustomBase customBaseTarget;
    /// <summary>The one serialized view of the component every view of it edits through (each Inspector used to keep its own).</summary>
    public readonly SerializedObject serializedObject;
    public SerializedProperty specifyCustomBaseFbxProp, baseFbxFilesProp, blendShapeValuesProp, isCreatorModeProp,
                              avatarLogicPrefabProp, customBlendshapesForCreatorProp,
                              modelFileBuildEntriesProp,
                              useAdvancedMeshReplacementForCreatorProp,
                              useHdiffFbxDeltaForCreatorProp,
                              includeCustomVeinsForCreatorProp, customVeinsNormalMapProp,
                              includeDynamicNormalsBodyForCreatorProp, includeDynamicNormalsFlexingForCreatorProp,
                              includeSuggestRealisticForCreatorProp, suggestRealisticMeshPathsForCreatorProp;
    public readonly NetworkService networkService = new NetworkService();
    public readonly FileManagerService fileManagerService = new FileManagerService();
    /// <summary>The operations on the avatar's files: one instance, so its progress and "a switch is running" guard are the avatar's.</summary>
    public readonly VersionActions actions;
    public readonly WarningsModule warningsModule = new WarningsModule();

    // --- Account ---
    public bool isAuthenticated;
    public string authToken;
    public bool HasServerAccess => MCBPackageVersionService.HasServerAccess(authToken);

    // --- Versions ---
    public bool fetchAttempted;
    public string fetchError, submitError;
    public string accessDeniedAssetId;
    public string currentBaseFbxHash;
    public string currentAppliedFbxHash;
    public bool isCustomBase;
    public List<CustomBaseVersion> serverVersions = new List<CustomBaseVersion>();
    public List<CustomBaseVersion> importedVersions = new List<CustomBaseVersion>();
    public List<CustomBaseVersion> unsubmittedVersions = new List<CustomBaseVersion>();
    public CustomBaseVersion recommendedVersion, selectedVersionForAction;
    public List<UserCustomVersionEntry> userCustomVersions = new List<UserCustomVersionEntry>();
    public UserCustomVersionEntry selectedCustomVersionForAction;
    public bool currentIsCustom;

    // --- Busy state (runtime only: a domain reload ends the coroutines running these) ---
    private bool fetching, downloading, deleting, submitting, applying;
    public bool isFetching { get => fetching; set => SetBusy(ref fetching, value); }
    public bool isDownloading { get => downloading; set => SetBusy(ref downloading, value); }
    public bool isDeleting { get => deleting; set => SetBusy(ref deleting, value); }
    public bool isSubmitting { get => submitting; set => SetBusy(ref submitting, value); }
    public bool isApplying { get => applying; set => SetBusy(ref applying, value); }
    /// <summary>A download, delete, build, publish or switch of the avatar runs.</summary>
    public bool IsBusy => downloading || deleting || submitting || applying;

    // --- Change announcements ---
    private readonly McbViewRefresh announce;

    /// <summary>Raised on the frame after a change, with every part of the views that changed since the last time.</summary>
    public event Action<McbViewParts> Changed;
    /// <summary>The account changed (signed in, out, or another one): views drop what belonged to the previous one.</summary>
    public event Action AuthenticationChanged;
    /// <summary>The avatar's models were detected again: views listing assets for them look again.</summary>
    public event Action ModelsDetected;
    /// <summary>An unsubmitted version was deleted: forms that built it forget it.</summary>
    public event Action<CustomBaseVersion> UnsubmittedVersionRemoved;

    private bool started;

    private McbSession(MyCustomBase target)
    {
        customBaseTarget = target;
        announce = new McbViewRefresh(parts => Changed?.Invoke(parts));
        if (target != null)
        {
            serializedObject = new SerializedObject(target);
            FindSerializedProperties();
        }
        actions = new VersionActions(this, networkService, fileManagerService);
    }

    /// <summary>Tells every view of this avatar that these parts changed; they refresh once, on the next editor frame.</summary>
    public void RequestRefresh(McbViewParts parts) => announce.Request(parts);

    /// <summary>Asks every view of this avatar to draw again, with nothing to rebuild.</summary>
    public void Repaint() => announce.Request(McbViewParts.Repaint);

    /// <summary>Announces pending changes now instead of next frame (tests).</summary>
    internal void FlushChanges() => announce.Flush();

    private void SetBusy(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        announce.Request(McbViewParts.Busy);
    }

    private void Start()
    {
        if (started) return;
        started = true;
        EnsureSerializedDefaults();
        LoadImportedVersions();
        LoadUnsubmittedVersions();
        userCustomVersions = UserCustomVersionService.Instance.GetAll();
        HookStaticEvents();
        AsyncVersionService.Instance.OnVersionsUpdated += OnVersionsUpdated;
        AsyncVersionService.Instance.OnVersionFetchError += OnVersionFetchError;
        AuthenticationService.Changed += SyncAuthentication;
        OrbitersEnvironment.Changed += SyncAuthentication;
        MCBConnectivityMonitor.StatusChanged += OnConnectivityChanged;
        MCBPackageVersionService.StatusChanged += OnPackageVersionStatusChanged;
        lastConnectivityBlocked = IsConnectivityBlocked();
        CheckAuthentication();
    }

    private void Stop()
    {
        if (!started) return;
        started = false;
        announce.Cancel();
        AsyncVersionService.Instance.OnVersionsUpdated -= OnVersionsUpdated;
        AsyncVersionService.Instance.OnVersionFetchError -= OnVersionFetchError;
        AuthenticationService.Changed -= SyncAuthentication;
        OrbitersEnvironment.Changed -= SyncAuthentication;
        MCBConnectivityMonitor.StatusChanged -= OnConnectivityChanged;
        MCBPackageVersionService.StatusChanged -= OnPackageVersionStatusChanged;
        EditorApplication.delayCall -= RunScheduledAsyncInitialization;
    }

    private void FindSerializedProperties()
    {
        specifyCustomBaseFbxProp = serializedObject.FindProperty("specifyCustomBaseFbx");
        baseFbxFilesProp = serializedObject.FindProperty("baseFbxFiles");
        blendShapeValuesProp = serializedObject.FindProperty("blendShapeValues");
        isCreatorModeProp = serializedObject.FindProperty("isCreatorMode");
        modelFileBuildEntriesProp = serializedObject.FindProperty("modelFileBuildEntries");
        avatarLogicPrefabProp = serializedObject.FindProperty("avatarLogicPrefab");
        useAdvancedMeshReplacementForCreatorProp = serializedObject.FindProperty("useAdvancedMeshReplacementForCreator");
        useHdiffFbxDeltaForCreatorProp = serializedObject.FindProperty("useHdiffFbxDeltaForCreator");
        customBlendshapesForCreatorProp = serializedObject.FindProperty("customBlendshapesForCreator");
        includeCustomVeinsForCreatorProp = serializedObject.FindProperty("includeCustomVeinsForCreator");
        customVeinsNormalMapProp = serializedObject.FindProperty("customVeinsNormalMap");
        includeDynamicNormalsBodyForCreatorProp = serializedObject.FindProperty("includeDynamicNormalsBodyForCreator");
        includeDynamicNormalsFlexingForCreatorProp = serializedObject.FindProperty("includeDynamicNormalsFlexingForCreator");
        includeSuggestRealisticForCreatorProp = serializedObject.FindProperty("includeSuggestRealisticForCreator");
        suggestRealisticMeshPathsForCreatorProp = serializedObject.FindProperty("suggestRealisticMeshPathsForCreator");
    }

    private void EnsureSerializedDefaults()
    {
        if (customBaseTarget == null) return;

        if (!customBaseTarget.preserveBlendshapeValuesOnVersionSwitchInitialized)
        {
            customBaseTarget.preserveBlendshapeValuesOnVersionSwitch = true;
            customBaseTarget.preserveBlendshapeValuesOnVersionSwitchInitialized = true;
            EditorUtility.SetDirty(customBaseTarget);
        }

        if (customBaseTarget.avatarPathOverrides == null)
        {
            customBaseTarget.avatarPathOverrides = new List<AvatarPathOverrideEntry>();
            EditorUtility.SetDirty(customBaseTarget);
        }

        McbInstanceIdentityService.EnsureIdentity(customBaseTarget);
    }

    // --- Account: shared by every Orbiters panel; a sign-out or switch elsewhere must stop this session from sending
    // requests with the previous token. ---
    public void SyncAuthentication() => SyncAuthentication(AuthenticationService.GetAuth());

    // Share the callback's state transition with isolated tests without changing the shared credential store.
    internal void SyncAuthentication(AuthenticationService.AuthData auth)
    {
        if (!string.Equals(auth?.token, authToken, StringComparison.Ordinal)) CheckAuthentication(auth);
    }

    public void CheckAuthentication() => CheckAuthentication(AuthenticationService.GetAuth());

    private void CheckAuthentication(AuthenticationService.AuthData auth)
    {
        authToken = auth?.token;
        isAuthenticated = !string.IsNullOrEmpty(authToken);
        if (!isAuthenticated)
        {
            accessDeniedAssetId = null;
            fetchError = null;
        }
        // A fetch made with the previous account answers nobody now.
        EndUserFetch();
        if (isAuthenticated) MCBPackageVersionService.EnsureCheckStarted(authToken);
        RequestSignedInUserInfo(auth);
        AuthenticationChanged?.Invoke();
        RequestRefresh(McbViewParts.All);
    }

    // The account row is Orbiters Toolkit's; MCB still reads the signed-in creator's details from its user cache (whether
    // Orbiters trusts them decides the protection settings a new custom base offers).
    private void RequestSignedInUserInfo(AuthenticationService.AuthData auth)
    {
        if (string.IsNullOrEmpty(auth?.token) || !int.TryParse(auth.user, out int userId) || UserService.IsUserInfoAvailable(userId)) return;
        UserService.RequestUserInfo(userId, auth.token, () => RequestRefresh(McbViewParts.All));
    }

    // --- Connectivity and package status: shown by every view; a connection that comes back detects and fetches again. ---
    private bool lastConnectivityBlocked;
    private bool connectivityRecoveryScheduled;

    private void OnConnectivityChanged()
    {
        bool currentlyBlocked = IsConnectivityBlocked();
        bool restored = lastConnectivityBlocked && IsConnectivityReachable();
        if (currentlyBlocked)
        {
            lastConnectivityBlocked = true;
        }
        else if (restored)
        {
            lastConnectivityBlocked = false;
            ScheduleConnectivityRecoveryRefresh();
        }

        RequestRefresh(McbViewParts.All);
    }

    private void OnPackageVersionStatusChanged() => RequestRefresh(McbViewParts.All);

    private static bool IsConnectivityBlocked()
    {
        return MCBConnectivityMonitor.HasCompleted &&
               !MCBConnectivityMonitor.CanReachServer &&
               !string.IsNullOrEmpty(MCBConnectivityMonitor.FailureReport);
    }

    private static bool IsConnectivityReachable()
    {
        return MCBConnectivityMonitor.HasCompleted &&
               MCBConnectivityMonitor.CanReachServer &&
               string.IsNullOrEmpty(MCBConnectivityMonitor.FailureReport);
    }

    private void ScheduleConnectivityRecoveryRefresh()
    {
        if (connectivityRecoveryScheduled)
        {
            return;
        }

        connectivityRecoveryScheduled = true;
        EditorApplication.delayCall += () =>
        {
            connectivityRecoveryScheduled = false;
            if (!IsConnectivityReachable() || customBaseTarget == null || ShouldDeferBackgroundNetworkRefresh())
            {
                return;
            }

            MCBLogger.Log("[MCBEditor] Connectivity restored. Refreshing avatar base detection, gallery assets, and versions.");
            UserService.ClearAllFailedRequests();
            InvalidateDetectedAvatarFbxCache();
            DetectAndLoadCached();
            RequestRefresh(McbViewParts.All);
        };
    }

    // --- Background work a view starts when it opens ---
    private bool asyncInitializationScheduled;
    private bool initialBackgroundRefreshStarted;
    private bool delayedDetectionScheduled;

    /// <summary>
    /// A view opened on the avatar: the server checks start, and once Unity is idle the avatar's models are detected and
    /// its cached versions loaded (fetched when none are cached), as each Inspector used to do when it opened.
    /// </summary>
    public void BeginBackgroundRefresh()
    {
        if (customBaseTarget == null) return;
        // Edits made on the component without this view (scripts, Undo) are read again.
        serializedObject.Update();
        MCBConnectivityMonitor.EnsureCheckStarted(authToken);
        MCBPackageVersionService.EnsureCheckStarted(authToken);
        MCBPerformance.EnsureStarted(this);
        ScheduleAsyncInitialization();
    }

    internal bool CanStartBackgroundRefreshes => initialBackgroundRefreshStarted && !ShouldDeferBackgroundNetworkRefresh();

    internal static bool ShouldDeferBackgroundNetworkRefresh()
    {
        return EditorApplication.isCompiling ||
               EditorApplication.isUpdating ||
               BuildPipeline.isBuildingPlayer ||
               IsVrcSdkPanelBusy();
    }

    private static bool IsVrcSdkPanelBusy()
    {
        VRCSdkControlPanel panel = VRCSdkControlPanel.window;
        return panel != null && panel.PanelState != SdkPanelState.Idle;
    }

    private void ScheduleAsyncInitialization()
    {
        if (asyncInitializationScheduled)
        {
            return;
        }

        asyncInitializationScheduled = true;
        EditorApplication.delayCall += RunScheduledAsyncInitialization;
    }

    private void RunScheduledAsyncInitialization()
    {
        asyncInitializationScheduled = false;
        EditorApplication.delayCall -= RunScheduledAsyncInitialization;

        if (customBaseTarget == null)
        {
            return;
        }

        if (ShouldDeferBackgroundNetworkRefresh())
        {
            ScheduleAsyncInitialization();
            return;
        }

        initialBackgroundRefreshStarted = true;
        StartAsyncInitialization();
        RequestRefresh(McbViewParts.All);
    }

    private void StartAsyncInitialization()
    {
        MCBPerformance.EnsureStarted(this);
        // Skip async initialization if already in progress or if we're submitting/building
        if (isFetching || isSubmitting || ShouldDeferBackgroundNetworkRefresh()) return;

        // Auto-detect FBX immediately (synchronously) to avoid missing detection on first draw
        if (!customBaseTarget.specifyCustomBaseFbx)
        {
            DetectAndLoadCached();
            if (customBaseTarget.baseFbxFiles == null || customBaseTarget.baseFbxFiles.Count == 0)
            {
                ScheduleDelayedDetectAndLoadCached();
            }
        }
        else
        {
            TryLoadCachedVersionsAndRefetch(false);
        }
    }

    private void ScheduleDelayedDetectAndLoadCached()
    {
        if (delayedDetectionScheduled)
        {
            return;
        }

        delayedDetectionScheduled = true;
        EditorApplication.delayCall += () =>
        {
            delayedDetectionScheduled = false;
            if (customBaseTarget == null || ShouldDeferBackgroundNetworkRefresh())
            {
                return;
            }

            DetectAndLoadCached();
        };
    }

    private void DetectAndLoadCached()
    {
        if (!SyncBaseFbxFilesFromSelectedAsset())
        {
            AutoDetectBaseFbxViaHierarchy();
        }
        // Immediately update hash state so UI knows an FBX is present
        actions.UpdateCurrentBaseFbxHash();
        TryLoadCachedVersionsAndRefetch(false);
        ModelsDetected?.Invoke();
    }

    // --- Static hooks: hierarchy changes make every session detect its models again. ---
    private static bool staticEventsHooked;

    private static void HookStaticEvents()
    {
        if (staticEventsHooked) return;
        staticEventsHooked = true;
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }

    private static void OnHierarchyChanged()
    {
        SmrPathService.InvalidateCache();
        foreach (var session in ByTarget.Values)
        {
            session.InvalidateDetectedAvatarFbxCache();
        }
    }
}
#endif
