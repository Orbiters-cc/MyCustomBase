#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;
using Newtonsoft.Json;
using UnityEngine.UIElements;
using VRC.SDKBase.Editor;

[CustomEditor(typeof(MyCustomBase))]
public class MCBEditor : UnityEditor.Editor
{
    private const string DevModeWarningEnabledPrefKey = "MCB.DevModeWarningEnabled";
    private static readonly TimeSpan LocalVersionCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly string[] UiToolkitStyleSheets =
    {
        "Packages/orbiters.mcb/Editor/Styles/mcb-theme.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-account.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-gallery.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-creator.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-version.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-avatar-options.uss"
    };
    private static List<CustomBaseVersion> cachedImportedVersions;
    private static DateTime cachedImportedVersionsAtUtc = DateTime.MinValue;
    private static List<CustomBaseVersion> cachedUnsubmittedVersions;
    private static DateTime cachedUnsubmittedVersionsAtUtc = DateTime.MinValue;
    private static readonly Dictionary<string, List<string>> SharedDetectedAvatarFbxPathCache =
        new Dictionary<string, List<string>>(StringComparer.Ordinal);

    // --- Target & Serialized Object ---
    public MyCustomBase customBaseTarget;
    public new SerializedObject serializedObject;
    private Texture2D bannerTexture;

    // --- Serialized Properties ---
    public SerializedProperty specifyCustomBaseFbxProp, baseFbxFilesProp, blendShapeValuesProp, isCreatorModeProp,
                               customFbxForCreatorProp, customBaseAvatarForCreatorProp, avatarLogicPrefabProp, customBlendshapesForCreatorProp,
                               modelFileBuildEntriesProp,
                               useAdvancedMeshReplacementForCreatorProp, compressAdvancedMeshPayloadForCreatorProp,
                               includeCustomVeinsForCreatorProp, customVeinsNormalMapProp,
                               includeDynamicNormalsBodyForCreatorProp, includeDynamicNormalsFlexingForCreatorProp,
                               includeSuggestRealisticForCreatorProp, suggestRealisticMeshPathsForCreatorProp;

    // --- Services and Modules ---
    private NetworkService networkService;
    private AuthenticationModule authModule;
    public VersionManagementModule versionModule;
    public CreatorModeModule creatorModule;
    private AssetGalleryModule assetGalleryModule;
    private AdvancedModeModule advancedModule;
    private AccountModule accountModule;
    private DependencyInstallerModule dependencyInstallerModule;
    private AvatarOptionsModule avatarOptionsModule;
    private AdjustMaterialModule adjustMaterialModule;
    public WarningsModule warningsModule;

    private VisualElement uiToolkitRoot;
    private MCBGlowSurfaceElement chromeSurfaceHost;
    private VisualElement headerHost;
    private VisualElement bannerHost;
    private VisualElement accountHost;
    private VisualElement dependencyHost;
    private VisualElement connectivityHost;
    private VisualElement galleryHost;
    private VisualElement selectedAssetActionsHost;
    private VisualElement creatorHost;
    private VisualElement versionHost;
    private VisualElement avatarOptionsHost;
    private VisualElement commentsHost;
    private IMGUIContainer topImGuiContainer;
    private IMGUIContainer middleImGuiContainer;
    private IMGUIContainer postCreatorImGuiContainer;
    private IMGUIContainer bottomBarImGuiContainer;
    private IVisualElementScheduledItem dynamicUiSchedule;
    public const float AssetViewImGuiPaddingLeft = 24f;
    public const float AssetViewImGuiPaddingRight = 24f;
    public const float AssetViewImGuiPaddingTop = 18f;
    public const float AssetViewImGuiPaddingBottom = 22f;
    
    // --- Async Services ---
    private AsyncTaskManager taskManager;
    private AsyncHashService hashService;
    private AsyncVersionService versionService;
    
    // --- SHARED EDITOR STATE ---
    public bool isAuthenticated;
    public string authToken;
    public bool fetchAttempted;
    public bool isFetching, isDownloading, isDeleting, isSubmitting, isApplying;
    public string fetchError, submitError;
    public string accessDeniedAssetId;
    public string currentBaseFbxHash;
    public bool isCustomBase;
    public List<CustomBaseVersion> serverVersions = new List<CustomBaseVersion>();
    public List<CustomBaseVersion> importedVersions = new List<CustomBaseVersion>();
    public List<CustomBaseVersion> unsubmittedVersions = new List<CustomBaseVersion>();
    public CustomBaseVersion recommendedVersion, selectedVersionForAction;
    
    // User custom base tracking
    public List<UserCustomVersionEntry> userCustomVersions = new List<UserCustomVersionEntry>();
    public UserCustomVersionEntry selectedCustomVersionForAction;
    public bool currentIsCustom;
    
    public string uiRenderingError;
    private string lastUiRenderingExceptionSignature;
    private List<string> cachedDetectedAvatarFbxPaths;
    private int cachedDetectedAvatarRootInstanceId;
    private bool detectedAvatarFbxCacheDirty = true;
    private bool delayedDetectionScheduled;
    private bool asyncInitializationScheduled;
    private bool initialBackgroundRefreshStarted;
    private bool lastConnectivityBlocked;
    private bool connectivityRecoveryScheduled;

    // Runtime state for detection
    public string currentAppliedFbxHash;
    public bool customWarningShown;

    public bool HasServerAccess
    {
        get { return MCBPackageVersionService.HasServerAccess(authToken); }
    }

    public static bool IsDevModeWarningEnabled
    {
        get
        {
            try { return EditorPrefs.GetBool(DevModeWarningEnabledPrefKey, true); }
            catch { return true; }
        }
        set
        {
            try { EditorPrefs.SetBool(DevModeWarningEnabledPrefKey, value); } catch { }
        }
    }
    
    private void OnEnable()
    {
        customBaseTarget = (MyCustomBase)target;
        EnsureSerializedDefaults();
        serializedObject = new SerializedObject(customBaseTarget);
        fetchAttempted = false;
        
        // Initialize async services first
        taskManager = AsyncTaskManager.Instance;
        hashService = AsyncHashService.Instance;
        versionService = AsyncVersionService.Instance;
        
        // Ensure ProgressBarManager is initialized early so it subscribes to task events
        var __ensureProgressBars = ProgressBarManager.Instance;
        
        // Subscribe to version service events
        versionService.OnVersionsUpdated += OnVersionsUpdated;
        versionService.OnVersionFetchError += OnVersionFetchError;
        
        bannerTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Path.Combine(MCBUtils.PACKAGE_BASE_FOLDER, "Editor/banner.png")); 
        FindSerializedProperties();
        
        networkService = new NetworkService();
        var fileManagerService = new FileManagerService();
        
        authModule = new AuthenticationModule(this);
        versionModule = new VersionManagementModule(this, networkService, fileManagerService);
        creatorModule = new CreatorModeModule(this);
        assetGalleryModule = new AssetGalleryModule(this);
        advancedModule = new AdvancedModeModule(this);
        accountModule = new AccountModule(this, networkService);
        accountModule.Initialize();
        dependencyInstallerModule = new DependencyInstallerModule(this);
        dependencyInstallerModule.Initialize();
        avatarOptionsModule = new AvatarOptionsModule(this);
        adjustMaterialModule = new AdjustMaterialModule(this);
        warningsModule = new WarningsModule();
        
        // Load local versions first (synchronous, but fast)
        LoadImportedVersions();
        LoadUnsubmittedVersions();
        // Load user custom versions
        userCustomVersions = UserCustomVersionService.Instance.GetAll();
        
        // Initialize modules
        creatorModule.Initialize();
        CheckAuthentication();
        lastConnectivityBlocked = IsConnectivityBlocked();
        MCBConnectivityMonitor.StatusChanged += RepaintFromConnectivityMonitor;
        MCBConnectivityMonitor.EnsureCheckStarted(authToken);
        MCBPackageVersionService.StatusChanged += RepaintFromPackageVersionStatus;
        MCBPackageVersionService.EnsureCheckStarted(authToken);
        
        // Ensure modules are enabled
        versionModule.OnEnable();
        
        ScheduleAsyncInitialization();
        
        // Subscribe to play mode state changes
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.projectChanged += OnProjectChanged;
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }
    
    private void OnDisable()
    {
        // Unsubscribe from play mode state changes
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.delayCall -= RunScheduledAsyncInitialization;
        asyncInitializationScheduled = false;
        accountModule?.DetachUIToolkit();
        dependencyInstallerModule?.DetachUIToolkit();
        dependencyInstallerModule?.Dispose();
        creatorModule?.DetachUIToolkit();
        versionModule?.DetachUIToolkit();
        avatarOptionsModule?.DetachUIToolkit();
        assetGalleryModule?.DetachUIToolkit();
        dynamicUiSchedule?.Pause();
        dynamicUiSchedule = null;
        uiToolkitRoot = null;
        chromeSurfaceHost = null;
        headerHost = null;
        bannerHost = null;
        accountHost = null;
        dependencyHost = null;
        galleryHost = null;
        selectedAssetActionsHost = null;
        creatorHost = null;
        versionHost = null;
        avatarOptionsHost = null;
        commentsHost = null;
        postCreatorImGuiContainer = null;
        bottomBarImGuiContainer = null;
        
        // Unsubscribe from version service events
        if (versionService != null)
        {
            versionService.OnVersionsUpdated -= OnVersionsUpdated;
            versionService.OnVersionFetchError -= OnVersionFetchError;
        }

        MCBConnectivityMonitor.StatusChanged -= RepaintFromConnectivityMonitor;
        MCBPackageVersionService.StatusChanged -= RepaintFromPackageVersionStatus;
        EditorApplication.projectChanged -= OnProjectChanged;
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;
    }

    public override VisualElement CreateInspectorGUI()
    {
        dynamicUiSchedule?.Pause();
        dynamicUiSchedule = null;

        uiToolkitRoot = new VisualElement();
        uiToolkitRoot.AddToClassList("mcb-root");
        LoadUiToolkitStyleSheets(uiToolkitRoot);

        chromeSurfaceHost = new MCBGlowSurfaceElement(
            new Color(0.180f, 0.180f, 0.180f, 1f),
            new Color(0.212f, 0.212f, 0.212f, 1f),
            254f,
            -78f,
            318f);
        chromeSurfaceHost.AddToClassList("mcb-chrome-surface");
        uiToolkitRoot.Add(chromeSurfaceHost);

        headerHost = new VisualElement();
        headerHost.AddToClassList("mcb-header");
        uiToolkitRoot.Add(headerHost);

        bannerHost = new VisualElement();
        bannerHost.AddToClassList("mcb-banner");
        headerHost.Add(bannerHost);

        accountHost = new VisualElement();
        headerHost.Add(accountHost);
        chromeSurfaceHost.SendToBack();

        dependencyHost = new VisualElement();
        uiToolkitRoot.Add(dependencyHost);

        connectivityHost = new VisualElement();
        uiToolkitRoot.Add(connectivityHost);

        topImGuiContainer = new IMGUIContainer(DrawToolkitTopImGui);
        topImGuiContainer.AddToClassList("mcb-imgui-top");
        uiToolkitRoot.Add(topImGuiContainer);

        galleryHost = new VisualElement();
        uiToolkitRoot.Add(galleryHost);

        selectedAssetActionsHost = new VisualElement();
        selectedAssetActionsHost.AddToClassList("mcb-selected-actions-host");
        uiToolkitRoot.Add(selectedAssetActionsHost);

        middleImGuiContainer = new IMGUIContainer(DrawToolkitMiddleImGui);
        middleImGuiContainer.AddToClassList("mcb-imgui-middle");
        uiToolkitRoot.Add(middleImGuiContainer);

        creatorHost = new VisualElement();
        creatorHost.AddToClassList("mcb-creator-host");
        uiToolkitRoot.Add(creatorHost);

        versionHost = new VisualElement();
        versionHost.AddToClassList("mcb-version-host");
        uiToolkitRoot.Add(versionHost);

        avatarOptionsHost = new VisualElement();
        avatarOptionsHost.AddToClassList("mcb-avatar-options-host");
        uiToolkitRoot.Add(avatarOptionsHost);

        postCreatorImGuiContainer = new IMGUIContainer(DrawToolkitPostCreatorImGui);
        postCreatorImGuiContainer.AddToClassList("mcb-imgui-middle");
        uiToolkitRoot.Add(postCreatorImGuiContainer);

        commentsHost = new VisualElement();
        commentsHost.AddToClassList("mcb-comments-host");
        uiToolkitRoot.Add(commentsHost);

        bottomBarImGuiContainer = new IMGUIContainer(DrawToolkitBottomBarImGui);
        bottomBarImGuiContainer.AddToClassList("mcb-imgui-statusbar");
        uiToolkitRoot.Add(bottomBarImGuiContainer);

        accountModule?.AttachUIToolkit(accountHost);
        dependencyInstallerModule?.AttachUIToolkit(dependencyHost);
        assetGalleryModule?.AttachUIToolkit(galleryHost, selectedAssetActionsHost, commentsHost);
        creatorModule?.AttachUIToolkit(creatorHost);
        versionModule?.AttachUIToolkit(versionHost);
        avatarOptionsModule?.AttachUIToolkit(avatarOptionsHost);
        RefreshUiToolkitSections();
        OrderUiToolkitLayers();
        dynamicUiSchedule = uiToolkitRoot.schedule.Execute(() => assetGalleryModule?.UpdateDynamicUiContent()).Every(3000);

        return uiToolkitRoot;
    }

    public void RefreshUiToolkitSections()
    {
        if (uiToolkitRoot == null)
        {
            return;
        }

        try
        {
            DrawVectorBannerUIToolkit();
            accountModule?.RefreshUIToolkit();
            dependencyInstallerModule?.RefreshUIToolkit();
            RefreshConnectivityDiagnosticsUIToolkit();
            assetGalleryModule?.RefreshUIToolkit();
            creatorModule?.RefreshUIToolkit();
            versionModule?.RefreshUIToolkit();
            avatarOptionsModule?.RefreshUIToolkit();
            ApplyDependencyBlockerState();
            OrderUiToolkitLayers();
            chromeSurfaceHost?.WakeForSeconds(20f);
            chromeSurfaceHost?.MarkDirtyRepaint();
            topImGuiContainer?.MarkDirtyRepaint();
            middleImGuiContainer?.MarkDirtyRepaint();
            postCreatorImGuiContainer?.MarkDirtyRepaint();
            bottomBarImGuiContainer?.MarkDirtyRepaint();
        }
        catch (Exception ex)
        {
            RecordUiException(ex);
            bottomBarImGuiContainer?.MarkDirtyRepaint();
        }
    }

    private void OrderUiToolkitLayers()
    {
        chromeSurfaceHost?.SendToBack();
        headerHost?.BringToFront();
        dependencyHost?.BringToFront();
        connectivityHost?.BringToFront();
        topImGuiContainer?.BringToFront();
        galleryHost?.BringToFront();
        selectedAssetActionsHost?.BringToFront();
        middleImGuiContainer?.BringToFront();
        creatorHost?.BringToFront();
        versionHost?.BringToFront();
        avatarOptionsHost?.BringToFront();
        postCreatorImGuiContainer?.BringToFront();
        commentsHost?.BringToFront();
        bottomBarImGuiContainer?.BringToFront();
    }

    private void ApplyDependencyBlockerState()
    {
        bool blocked = dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies;
        DisplayStyle contentDisplay = blocked ? DisplayStyle.None : DisplayStyle.Flex;

        if (topImGuiContainer != null) topImGuiContainer.style.display = contentDisplay;
        if (connectivityHost != null)
        {
            connectivityHost.style.display = blocked || connectivityHost.childCount == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }
        if (galleryHost != null) galleryHost.style.display = contentDisplay;
        if (selectedAssetActionsHost != null) selectedAssetActionsHost.style.display = contentDisplay;
        if (middleImGuiContainer != null) middleImGuiContainer.style.display = contentDisplay;
        if (creatorHost != null && blocked) creatorHost.style.display = DisplayStyle.None;
        if (versionHost != null && blocked) versionHost.style.display = DisplayStyle.None;
        if (avatarOptionsHost != null && blocked) avatarOptionsHost.style.display = DisplayStyle.None;
        if (postCreatorImGuiContainer != null) postCreatorImGuiContainer.style.display = contentDisplay;
        if (commentsHost != null) commentsHost.style.display = contentDisplay;
        if (bottomBarImGuiContainer != null) bottomBarImGuiContainer.style.display = contentDisplay;
    }

    private static void LoadUiToolkitStyleSheets(VisualElement root)
    {
        if (root == null)
        {
            return;
        }

        foreach (var styleSheetPath in UiToolkitStyleSheets)
        {
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(styleSheetPath);
            if (styleSheet != null)
            {
                root.styleSheets.Add(styleSheet);
            }
        }
    }

    private void DrawToolkitTopImGui()
    {
        if (dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies)
        {
            return;
        }

        serializedObject.Update();
        try
        {
            if (!isAuthenticated)
            {
                SafeUiCall(() => authModule.DrawMagicSyncAuth());
            }
        }
        finally
        {
            serializedObject.ApplyModifiedProperties();
        }
    }

    private void DrawToolkitMiddleImGui()
    {
        if (dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies)
        {
            return;
        }

        serializedObject.Update();
        bool useAssetViewPadding = IsSelectedAssetView();
        try
        {
            if (useAssetViewPadding)
            {
                BeginAssetViewImGuiPadding();
            }

            bool hasMajorUpdateLockout = MCBPackageVersionService.RequiresMajorUpdate;
            bool showOfflineSavedVersionsUi = !HasServerAccess && importedVersions != null && importedVersions.Count > 0;

            if (hasMajorUpdateLockout)
            {
                SafeUiCall(DrawMajorUpdateRequiredInfo);
                if (showOfflineSavedVersionsUi)
                {
                    SafeUiCall(DrawOfflineSavedVersionsInfo);
                }
            }
            else if (HasServerAccess)
            {
                if (assetGalleryModule == null || !assetGalleryModule.ShouldShowGalleryOnly())
                {
                    SafeUiCall(() => warningsModule?.Draw());
                }
            }
            else if (showOfflineSavedVersionsUi)
            {
                SafeUiCall(DrawOfflineSavedVersionsInfo);
            }
        }
        finally
        {
            if (useAssetViewPadding)
            {
                EndAssetViewImGuiPadding();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }

    private void DrawToolkitPostCreatorImGui()
    {
        if (dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies)
        {
            return;
        }

        serializedObject.Update();
        bool useAssetViewPadding = IsSelectedAssetView();
        try
        {
            if (useAssetViewPadding)
            {
                BeginAssetViewImGuiPadding();
            }

            bool hasMajorUpdateLockout = MCBPackageVersionService.RequiresMajorUpdate;
            if (!hasMajorUpdateLockout && HasServerAccess)
            {
                if (assetGalleryModule == null || !assetGalleryModule.ShouldShowGalleryOnly())
                {
                    SafeUiCall(() => adjustMaterialModule?.Draw());
                }
            }
        }
        finally
        {
            if (useAssetViewPadding)
            {
                EndAssetViewImGuiPadding();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }

    private bool IsSelectedAssetView()
    {
        return assetGalleryModule != null && assetGalleryModule.SelectedAsset != null;
    }

    public float GetCurrentAssetViewImGuiLeftPadding()
    {
        return IsSelectedAssetView() ? AssetViewImGuiPaddingLeft : 0f;
    }

    public bool ShouldShowGalleryOnly()
    {
        return assetGalleryModule != null && assetGalleryModule.ShouldShowGalleryOnly();
    }

    public void StartCreateNewVersion()
    {
        if (isCreatorModeProp == null)
        {
            return;
        }

        serializedObject.Update();
        isCreatorModeProp.boolValue = true;
        serializedObject.ApplyModifiedProperties();
        if (customBaseTarget != null)
        {
            EditorUtility.SetDirty(customBaseTarget);
        }

        RefreshUiToolkitSections();
        Repaint();
    }

    private static void BeginAssetViewImGuiPadding()
    {
        GUILayout.Space(AssetViewImGuiPaddingTop);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(AssetViewImGuiPaddingLeft);
        EditorGUILayout.BeginVertical();
    }

    private static void EndAssetViewImGuiPadding()
    {
        EditorGUILayout.EndVertical();
        GUILayout.Space(AssetViewImGuiPaddingRight);
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(AssetViewImGuiPaddingBottom);
    }

    private void DrawVectorBannerUIToolkit()
    {
        if (bannerHost == null)
        {
            return;
        }

        bannerHost.Clear();

        var logo = new MCBLogoElement(drawLogo: true, drawGlows: false);
        logo.AddToClassList("mcb-logo");
        bannerHost.Add(logo);

        bannerHost.Add(new MCBLoadingBarElement());
    }

    private void DrawToolkitBottomBarImGui()
    {
        if (dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies)
        {
            return;
        }

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button("Advanced Options", EditorStyles.toolbarButton, GUILayout.Width(126f)))
        {
            OpenAdvancedModeWindow();
        }

        if (GUILayout.Button("Blendshape Links", EditorStyles.toolbarButton, GUILayout.Width(126f)))
        {
            BlendShapeLinksDebugWindow.OpenWindow();
        }

        GUILayout.FlexibleSpace();
        if (!string.IsNullOrEmpty(uiRenderingError))
        {
            GUILayout.Label(new GUIContent("UI warning", uiRenderingError), EditorStyles.miniLabel);
        }
        EditorGUILayout.EndHorizontal();
    }

    public void OpenAdvancedModeWindow()
    {
        MCBAdvancedModeWindow.Open(this, advancedModule);
    }
    
    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        advancedModule?.OnPlayModeStateChanged(state);
        avatarOptionsModule?.OnPlayModeStateChanged(state);
    }

    private void OnProjectChanged()
    {
        InvalidateDetectedAvatarFbxCache(true);
        SmrPathService.InvalidateCache();
        LoadImportedVersions(true);
        if (!ShouldDeferBackgroundNetworkRefresh())
        {
            assetGalleryModule?.OnProjectChanged();
        }

        dependencyInstallerModule?.RefreshUIToolkit();
        ApplyDependencyBlockerState();
        Repaint();
    }

    private void OnHierarchyChanged()
    {
        InvalidateDetectedAvatarFbxCache(true);
        SmrPathService.InvalidateCache();
    }

    internal bool CanStartBackgroundRefreshes
    {
        get { return initialBackgroundRefreshStarted && !ShouldDeferBackgroundNetworkRefresh(); }
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
        RefreshUiToolkitSections();
        Repaint();
    }

    private void StartAsyncInitialization()
    {
        // Skip async initialization if already in progress or if we're submitting/building
        if (isFetching || isSubmitting || ShouldDeferBackgroundNetworkRefresh()) return;

        // Auto-detect FBX immediately (synchronously) to avoid missing detection on first draw
        if (!specifyCustomBaseFbxProp.boolValue)
        {
            DetectAndLoadCached();
            if (baseFbxFilesProp != null && baseFbxFilesProp.arraySize == 0)
            {
                ScheduleDelayedDetectAndLoadCached();
            }
        }
        else
        {
            TryLoadCachedVersionsAndRefetch(false);
        }
    }

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
            if (ShouldDeferBackgroundNetworkRefresh())
            {
                return;
            }

            DetectAndLoadCached();
        };
    }

    private void OnVersionsUpdated(System.Collections.Generic.List<CustomBaseVersion> versions, CustomBaseVersion recommended)
    {
        serverVersions = versions;
        recommendedVersion = recommended;
        fetchError = null; // Clear any previous errors
        accessDeniedAssetId = null; // Clear special access state on success
        
        // Update applied version state
        if (versionModule?.actions != null)
        {
            versionModule.actions.UpdateAppliedVersionAndState();
        }
        
        RefreshUiToolkitSections();
        Repaint();
        MCBLogger.Log($"[MCBEditor] Updated with {versions.Count} server versions");
    }

    public void LoadCachedVersionsForCurrentSelection()
    {
        TryLoadCachedVersionsAndRefetch(false);
    }

    private void TryLoadCachedVersionsAndRefetch(bool forceRefetch)
    {
        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrEmpty(fbxPath))
        {
            return;
        }

        var selectedAsset = GetSelectedAsset();
        if (selectedAsset == null)
        {
            serverVersions.Clear();
            recommendedVersion = null;
            return;
        }

        var cached = versionService.GetCachedVersions(fbxPath, authToken, selectedAsset.id);
        if (HasServerAccess && cached.versions.Count > 0)
        {
            serverVersions = cached.versions;
            recommendedVersion = cached.recommended;
            if (versionModule != null && versionModule.actions != null)
            {
                versionModule.actions.UpdateAppliedVersionAndState();
            }
            RefreshUiToolkitSections();
            Repaint();
            MCBLogger.Log($"[MCBEditor] Loaded {cached.versions.Count} cached versions");
        }

        if (HasServerAccess && (forceRefetch || cached.versions.Count == 0))
        {
            fetchAttempted = true;
            versionService.StartVersionFetchInBackground(fbxPath, authToken, selectedAsset.id, useCache: !forceRefetch);
        }
    }

    private void DetectAndLoadCached()
    {
        if (!SyncBaseFbxFilesFromSelectedAsset())
        {
            AutoDetectBaseFbxViaHierarchy();
        }
        // Immediately update hash state so UI knows an FBX is present
        if (versionModule != null && versionModule.actions != null)
        {
            versionModule.actions.UpdateCurrentBaseFbxHash();
        }
        TryLoadCachedVersionsAndRefetch(false);
        assetGalleryModule?.RefreshIfNeeded(force: true);
    }

    private void OnVersionFetchError(string error)
    {
        fetchError = error;
        RefreshUiToolkitSections();
        Repaint();
        MCBLogger.LogError($"[MCBEditor] Version fetch error: {error}");
    }

    private bool AutoDetectBaseFbxViaHierarchy()
    {
        if (customBaseTarget == null) return false;
        var detectedPaths = GetDetectedAvatarFbxPaths();
        if (detectedPaths.Count == 0)
        {
            return false;
        }

        var detectedAssets = new List<GameObject>();
        for (int i = 0; i < detectedPaths.Count; i++)
        {
            var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(detectedPaths[i]);
            if (fbxAsset == null)
            {
                continue;
            }

            detectedAssets.Add(fbxAsset);
        }

        if (detectedAssets.Count == 0)
        {
            return false;
        }

        if (AreBaseFbxReferencesEqual(detectedAssets))
        {
            return true;
        }

        baseFbxFilesProp.ClearArray();
        for (int i = 0; i < detectedAssets.Count; i++)
        {
            baseFbxFilesProp.InsertArrayElementAtIndex(baseFbxFilesProp.arraySize);
            baseFbxFilesProp.GetArrayElementAtIndex(baseFbxFilesProp.arraySize - 1).objectReferenceValue = detectedAssets[i];
        }

        serializedObject.ApplyModifiedProperties();
        InvalidateDetectedAvatarFbxCache();
        MCBLogger.Log($"[MCBEditor] Auto-detected {baseFbxFilesProp.arraySize} FBX file(s) for the avatar root.");
        Repaint();
        return true;
    }

    private string GetCurrentFBXPath()
    {
        if (baseFbxFilesProp.arraySize > 0)
        {
            var fbx = baseFbxFilesProp.GetArrayElementAtIndex(0).objectReferenceValue as GameObject;
            if (fbx != null) return AssetDatabase.GetAssetPath(fbx);
        }
        return null;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        try
        {
            SafeUiCall(DrawBanner);
            
            // Account module just under the banner
            SafeUiCall(() => accountModule?.Draw());

            if (dependencyInstallerModule != null && dependencyInstallerModule.DrawFallbackIfBlocked())
            {
                return;
            }

            if (!isAuthenticated)
            {
                SafeUiCall(() => authModule.DrawMagicSyncAuth());
            }

            bool hasMajorUpdateLockout = MCBPackageVersionService.RequiresMajorUpdate;
            bool showOfflineSavedVersionsUi = !HasServerAccess && importedVersions != null && importedVersions.Count > 0;

            if (hasMajorUpdateLockout)
            {
                SafeUiCall(DrawMajorUpdateRequiredInfo);
                if (showOfflineSavedVersionsUi)
                {
                    SafeUiCall(DrawOfflineSavedVersionsInfo);
                    SafeUiCall(() => versionModule.Draw());
                    SafeUiCall(() => avatarOptionsModule?.Draw());
                }
            }
            else if (HasServerAccess)
            {
                SafeUiCall(() => assetGalleryModule?.DrawSelectedAssetHeader());
                SafeUiCall(() => assetGalleryModule?.Draw());

                if (assetGalleryModule == null || !assetGalleryModule.ShouldShowGalleryOnly())
                {
                    SafeUiCall(() => warningsModule?.Draw());
                    SafeUiCall(() => creatorModule.Draw());
                    SafeUiCall(() => versionModule.Draw());
                    SafeUiCall(() => avatarOptionsModule?.Draw());
                    SafeUiCall(() => adjustMaterialModule?.Draw());
                }
            }
            else if (showOfflineSavedVersionsUi)
            {
                SafeUiCall(DrawOfflineSavedVersionsInfo);
                SafeUiCall(() => versionModule.Draw());
                SafeUiCall(() => avatarOptionsModule?.Draw());
            }

            // Logout moved to AccountModule
            SafeUiCall(() => advancedModule.Draw());
            DrawUiRenderingError();
        }
        finally
        {
            serializedObject.ApplyModifiedProperties();
        }
    }
    
    private void SafeUiCall(Action drawAction)
    {
        if (drawAction == null) return;

        try
        {
            drawAction.Invoke();
        }
        catch (Exception ex)
        {
            if (ex is ExitGUIException) throw;
            RecordUiException(ex);
        }
    }

    private void DrawUiRenderingError()
    {
        if (string.IsNullOrEmpty(uiRenderingError)) return;

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(uiRenderingError, MessageType.Error);

        if (GUILayout.Button("Dismiss Error Message"))
        {
            uiRenderingError = null;
            lastUiRenderingExceptionSignature = null;
        }
    }

    private void RecordUiException(Exception ex)
    {
        if (ex == null) return;

        string baseMessage = "MCB encountered a problem while drawing the editor UI. Check the Console for details.";
        string reason = $"Reason: {ex.Message}";

        if (string.IsNullOrEmpty(uiRenderingError))
        {
            uiRenderingError = $"{baseMessage}\n{reason}";
        }
        else if (!uiRenderingError.Contains(reason))
        {
            uiRenderingError += $"\n{reason}";
        }

        string signature = ex.ToString();
        if (!string.Equals(lastUiRenderingExceptionSignature, signature, StringComparison.Ordinal))
        {
            MCBLogger.LogException(ex);
            lastUiRenderingExceptionSignature = signature;
        }
    }

    private void DrawBanner()
    {
        Texture2D textureToDraw = null;
        var selectedAsset = GetSelectedAsset();
        if (isAuthenticated && selectedAsset != null)
        {
            try
            {
                textureToDraw = AvatarAssetDiscoveryService.GetBanner(selectedAsset);
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[MCBEditor] Failed to resolve selected asset banner for assetId={selectedAsset.id} name='{selectedAsset.name}': {ex}");
            }

            // Fall back to the package banner while the asset banner is missing or still downloading.
            if (textureToDraw == null)
            {
                textureToDraw = bannerTexture;
            }
        }
        else if (isAuthenticated && assetGalleryModule != null && assetGalleryModule.ShouldShowGalleryOnly())
        {
            textureToDraw = bannerTexture;
        }

        if (textureToDraw == null) return;
        if (textureToDraw.height == 0) return;
        float aspect = (float)textureToDraw.width / textureToDraw.height;
        float desiredWidth = EditorGUIUtility.currentViewWidth;
        Rect rect = GUILayoutUtility.GetRect(desiredWidth, desiredWidth / aspect);
        GUI.DrawTexture(rect, textureToDraw, ScaleMode.StretchToFill);
        GUILayout.Space(5);
    }

    private void RefreshConnectivityDiagnosticsUIToolkit()
    {
        if (connectivityHost == null)
        {
            return;
        }

        connectivityHost.Clear();
        connectivityHost.RemoveFromClassList("mcb-connectivity");

        bool isDevModeEnabled = MCBUtils.isDevEnvironment;
        bool showDevModeWarning = isDevModeEnabled && IsDevModeWarningEnabled;
        ApiSimulationMode simulationMode = MCBUtils.apiSimulationMode;
        bool isFakeNetworkErrorEnabled = simulationMode != ApiSimulationMode.Off;
        bool hasOverrideUi = showDevModeWarning || isFakeNetworkErrorEnabled;
        string failureReport = MCBConnectivityMonitor.FailureReport;
        bool hasFailureReport = !string.IsNullOrEmpty(failureReport);
        List<MCBRequestWarning> requestWarnings = MCBConnectivityMonitor.GetRequestWarnings();
        bool hasRequestWarnings = requestWarnings.Count > 0;

        if (!hasFailureReport && !hasOverrideUi && !hasRequestWarnings)
        {
            connectivityHost.style.display = DisplayStyle.None;
            return;
        }

        connectivityHost.style.display = DisplayStyle.Flex;
        connectivityHost.AddToClassList("mcb-connectivity");

        var panel = new VisualElement();
        panel.AddToClassList("mcb-connectivity__panel");
        if (!hasFailureReport && hasRequestWarnings)
        {
            panel.AddToClassList("mcb-connectivity__panel--warning");
        }
        connectivityHost.Add(panel);

        if (hasFailureReport)
        {
            BuildConnectivityFailureReportUIToolkit(panel, failureReport);
        }

        if (hasRequestWarnings)
        {
            BuildRequestWarningsUIToolkit(panel, requestWarnings);
        }

        if (hasOverrideUi)
        {
            BuildConnectivityOverridesUIToolkit(panel, showDevModeWarning, isFakeNetworkErrorEnabled, simulationMode);
        }
    }

    private void BuildConnectivityFailureReportUIToolkit(VisualElement panel, string failureReport)
    {
        var header = new VisualElement();
        header.AddToClassList("mcb-connectivity__header");

        var icon = new Label("!");
        icon.AddToClassList("mcb-connectivity__icon");
        header.Add(icon);

        var text = new VisualElement();
        text.AddToClassList("mcb-connectivity__header-text");

        var title = new Label("The tool cannot connect to the server");
        title.AddToClassList("mcb-connectivity__title");
        text.Add(title);

        var body = new Label("Copy the data below and send it to @blackorbit on Discord.");
        body.AddToClassList("mcb-connectivity__body");
        text.Add(body);

        header.Add(text);
        panel.Add(header);

        if (MCBConnectivityMonitor.IsBuildingFailureReport)
        {
            panel.Add(CreateConnectivityReportLoadingBar());
        }

        var report = new TextField { multiline = true, value = failureReport ?? string.Empty };
        report.isReadOnly = true;
        report.AddToClassList("mcb-connectivity__report");
        panel.Add(report);

        var actions = new VisualElement();
        actions.AddToClassList("mcb-connectivity__actions");
        actions.Add(CreateConnectivityButton("Copy report", () =>
        {
            EditorGUIUtility.systemCopyBuffer = MCBConnectivityMonitor.FailureReport ?? string.Empty;
        }));
        actions.Add(CreateConnectivityButton("Retry check", () =>
        {
            MCBConnectivityMonitor.Retry(authToken);
            RefreshAccountAndVersions();
            RefreshUiToolkitSections();
        }));
        panel.Add(actions);
    }

    private static VisualElement CreateConnectivityReportLoadingBar()
    {
        var wrapper = new VisualElement();
        wrapper.AddToClassList("mcb-connectivity-report-loading");

        var label = new Label("Creating full connectivity report...");
        label.AddToClassList("mcb-connectivity-report-loading__label");
        wrapper.Add(label);

        var bar = new VisualElement();
        bar.AddToClassList("mcb-loading-bar");
        bar.AddToClassList("mcb-connectivity-report-loading__bar");
        wrapper.Add(bar);

        var track = new VisualElement();
        track.AddToClassList("mcb-loading-bar__track");
        bar.Add(track);

        var fill = new VisualElement();
        fill.AddToClassList("mcb-loading-bar__fill");
        track.Add(fill);

        double startTime = MCBConnectivityMonitor.FailureReportBuildStartedAt;
        if (startTime <= 0d)
        {
            startTime = EditorApplication.timeSinceStartup;
        }
        IVisualElementScheduledItem animation = null;
        animation = wrapper.schedule.Execute(() =>
        {
            double elapsed = EditorApplication.timeSinceStartup - startTime;
            float progress = Mathf.Clamp01((float)elapsed / 8f);
            float eased = Mathf.SmoothStep(0f, 1f, progress);
            fill.style.width = Length.Percent(Mathf.Lerp(10f, 92f, eased));
        }).Every(16);

        wrapper.RegisterCallback<DetachFromPanelEvent>(_ => animation?.Pause());
        return wrapper;
    }

    private void BuildRequestWarningsUIToolkit(VisualElement panel, List<MCBRequestWarning> requestWarnings)
    {
        requestWarnings.Sort((left, right) => right.timestampUtc.CompareTo(left.timestampUtc));

        var section = new VisualElement();
        section.AddToClassList("mcb-connectivity-warnings");
        section.EnableInClassList("mcb-connectivity-warnings--first", panel.childCount == 0);

        var header = new VisualElement();
        header.AddToClassList("mcb-connectivity-warnings__header");

        var icon = new Label("!");
        icon.AddToClassList("mcb-connectivity-warnings__icon");
        header.Add(icon);

        var text = new VisualElement();
        text.AddToClassList("mcb-connectivity-warnings__text");

        var title = new Label("Some resources could not be loaded");
        title.AddToClassList("mcb-connectivity-warnings__title");
        text.Add(title);

        var body = new Label("The server is reachable, but one or more non-critical requests failed.");
        body.AddToClassList("mcb-connectivity-warnings__body");
        text.Add(body);

        header.Add(text);
        section.Add(header);

        foreach (var warning in requestWarnings)
        {
            var item = new VisualElement();
            item.AddToClassList("mcb-connectivity-warning");

            var itemTitle = new Label(string.IsNullOrWhiteSpace(warning.title) ? "Non-critical request failed" : warning.title);
            itemTitle.AddToClassList("mcb-connectivity-warning__title");
            item.Add(itemTitle);

            var itemBody = new Label(warning.message ?? string.Empty);
            itemBody.AddToClassList("mcb-connectivity-warning__body");
            item.Add(itemBody);

            section.Add(item);
        }

        var actions = new VisualElement();
        actions.AddToClassList("mcb-connectivity__actions");
        actions.Add(CreateConnectivityButton("Clear warnings", () =>
        {
            MCBConnectivityMonitor.ClearAllRequestWarnings();
            RefreshUiToolkitSections();
        }));
        section.Add(actions);

        panel.Add(section);
    }

    private void BuildConnectivityOverridesUIToolkit(VisualElement panel, bool isDevModeEnabled, bool isFakeNetworkErrorEnabled, ApiSimulationMode simulationMode)
    {
        var section = new VisualElement();
        section.AddToClassList("mcb-connectivity-overrides");

        var title = new Label("Advanced connectivity overrides are enabled.");
        title.AddToClassList("mcb-connectivity-overrides__title");
        section.Add(title);

        if (isDevModeEnabled)
        {
            section.Add(CreateConnectivityOverrideRow(
                "Dev mode",
                "Requests are using the dev environment endpoints.",
                DisableDevModeOverrideAndRefresh));
        }

        if (isFakeNetworkErrorEnabled)
        {
            section.Add(CreateConnectivityOverrideRow(
                "Fake network error",
                GetConnectivitySimulationLabel(simulationMode),
                DisableFakeNetworkErrorOverrideAndRefresh));
        }

        var refreshButton = CreateConnectivityButton("Refresh with overrides off", RefreshWithOverridesOff);
        refreshButton.AddToClassList("mcb-button--primary");
        refreshButton.AddToClassList("mcb-connectivity-overrides__refresh");
        section.Add(refreshButton);

        panel.Add(section);
    }

    private VisualElement CreateConnectivityOverrideRow(string titleText, string bodyText, Action onTurnOff)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-connectivity-override");

        var text = new VisualElement();
        text.AddToClassList("mcb-connectivity-override__text");

        var title = new Label(titleText);
        title.AddToClassList("mcb-connectivity-override__title");
        text.Add(title);

        var body = new Label(bodyText);
        body.AddToClassList("mcb-connectivity-override__body");
        text.Add(body);

        row.Add(text);

        var button = CreateConnectivityButton("Turn off", onTurnOff);
        button.AddToClassList("mcb-connectivity-override__button");
        row.Add(button);

        return row;
    }

    private static Button CreateConnectivityButton(string text, Action clicked)
    {
        var button = new Button(clicked) { text = text };
        button.AddToClassList("mcb-button");
        return button;
    }

    private static string GetConnectivitySimulationLabel(ApiSimulationMode simulationMode)
    {
        switch (simulationMode)
        {
            case ApiSimulationMode.TransportFailure:
                return "Transport Failure";
            case ApiSimulationMode.SslFailure:
                return "SSL Failure";
            default:
                return "Off";
        }
    }

    private void DrawOfflineSavedVersionsInfo()
    {
        EditorGUILayout.HelpBox("Imported saved versions are available offline. You can apply them or reset to the original avatar base without logging in.", MessageType.Info);
    }

    private void DrawMajorUpdateRequiredInfo()
    {
        var status = MCBPackageVersionService.CurrentStatus;
        if (status == null || !status.requiresMajorUpdate)
        {
            return;
        }

        var titleStyle = new GUIStyle(EditorStyles.boldLabel);
        titleStyle.fontSize = 20;
        titleStyle.alignment = TextAnchor.MiddleCenter;
        titleStyle.normal.textColor = new Color(0.85f, 0.15f, 0.15f);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Update needed !", titleStyle, GUILayout.Height(28f));
        EditorGUILayout.Space(4f);

        string message = string.IsNullOrWhiteSpace(status.updateMessage)
            ? $"A new major version of MCB is available.\n\nCurrent version: {status.currentVersion}\nLatest version: {status.latestVersion}\n\nUpdate the package from VCC before using connected features."
            : status.updateMessage;

        EditorGUILayout.HelpBox(message, MessageType.Warning);
    }
    
    public void CheckAuthentication()
    {
        authToken = AuthenticationService.GetAuth()?.token;
        isAuthenticated = !string.IsNullOrEmpty(authToken);
        if (!isAuthenticated)
        {
            accessDeniedAssetId = null;
            fetchError = null;
        }
        MCBPackageVersionService.EnsureCheckStarted(authToken);
        accountModule?.Refresh();
        assetGalleryModule?.OnAuthenticationChanged();
        RefreshUiToolkitSections();
    }

    public void RefreshAccountAndVersions()
    {
        // Clear failed user info requests to allow retry
        UserService.ClearAllFailedRequests();
        
        try
        {
            accountModule?.Refresh();
        }
        catch (Exception ex)
        {
            RecordUiException(ex);
        }

        SyncBaseFbxFilesFromSelectedAsset();

        string fbxPath = GetCurrentFBXPath();
        var selectedAsset = GetSelectedAsset();
        if (!string.IsNullOrEmpty(fbxPath) && HasServerAccess && selectedAsset != null)
        {
            fetchAttempted = true;
            versionService?.StartVersionFetchInBackground(fbxPath, authToken, selectedAsset.id, useCache: false);
        }
        if (selectedAsset == null)
        {
            assetGalleryModule?.RefreshIfNeeded(force: true);
        }
        RefreshUiToolkitSections();
    }

    public void DisableDevModeOverride()
    {
        if (!MCBUtils.isDevEnvironment) return;
        MCBUtils.isDevEnvironment = false;
        CheckAuthentication();
        Repaint();
    }

    public void DisableFakeNetworkErrorOverride()
    {
        if (MCBUtils.apiSimulationMode == ApiSimulationMode.Off) return;
        MCBUtils.apiSimulationMode = ApiSimulationMode.Off;
        Repaint();
    }

    public void DisableDevModeOverrideAndRefresh()
    {
        DisableDevModeOverride();
        MCBConnectivityMonitor.Retry(authToken);
        RefreshAccountAndVersions();
        Repaint();
    }

    public void DisableFakeNetworkErrorOverrideAndRefresh()
    {
        DisableFakeNetworkErrorOverride();
        MCBConnectivityMonitor.Retry(authToken);
        RefreshAccountAndVersions();
        Repaint();
    }

    public void RefreshWithOverridesOff()
    {
        DisableDevModeOverride();
        DisableFakeNetworkErrorOverride();
        CheckAuthentication();
        MCBConnectivityMonitor.Retry(authToken);
        RefreshAccountAndVersions();
        Repaint();
    }

    private void FindSerializedProperties()
    {
        specifyCustomBaseFbxProp = serializedObject.FindProperty("specifyCustomBaseFbx");
        baseFbxFilesProp = serializedObject.FindProperty("baseFbxFiles");
        blendShapeValuesProp = serializedObject.FindProperty("blendShapeValues");
        isCreatorModeProp = serializedObject.FindProperty("isCreatorMode");
        customFbxForCreatorProp = serializedObject.FindProperty("customFbxForCreator");
        customBaseAvatarForCreatorProp = serializedObject.FindProperty("customBaseAvatarForCreatorProp");
        modelFileBuildEntriesProp = serializedObject.FindProperty("modelFileBuildEntries");
        avatarLogicPrefabProp = serializedObject.FindProperty("avatarLogicPrefab");
        useAdvancedMeshReplacementForCreatorProp = serializedObject.FindProperty("useAdvancedMeshReplacementForCreator");
        compressAdvancedMeshPayloadForCreatorProp = serializedObject.FindProperty("compressAdvancedMeshPayloadForCreator");
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
    }

    public void LoadUnsubmittedVersions(bool forceRefresh = false)
    {
        unsubmittedVersions.Clear();
        if (!forceRefresh && IsLocalVersionCacheFresh(cachedUnsubmittedVersionsAtUtc) && cachedUnsubmittedVersions != null)
        {
            unsubmittedVersions.AddRange(cachedUnsubmittedVersions);
            return;
        }

        string path = MCBUtils.UNSUBMITTED_VERSIONS_FILE;
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<List<CustomBaseVersion>>(json);
                if (loaded != null)
                {
                    foreach (var v in loaded)
                    {
                        v.isUnsubmitted = true; // Set runtime flag
                        unsubmittedVersions.Add(v);
                    }
                }
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[MCB] Failed to load unsubmitted versions from {path}: {ex.Message}");
            }
        }

        cachedUnsubmittedVersions = new List<CustomBaseVersion>(unsubmittedVersions);
        cachedUnsubmittedVersionsAtUtc = DateTime.UtcNow;
    }

    public void LoadImportedVersions(bool forceRefresh = false)
    {
        importedVersions.Clear();
        if (!forceRefresh && IsLocalVersionCacheFresh(cachedImportedVersionsAtUtc) && cachedImportedVersions != null)
        {
            importedVersions.AddRange(cachedImportedVersions);
            return;
        }

        string versionsRoot = Path.GetFullPath(MCBUtils.ASSET_VERSIONS_FOLDER);
        if (!Directory.Exists(versionsRoot))
        {
            cachedImportedVersions = new List<CustomBaseVersion>();
            cachedImportedVersionsAtUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            foreach (string versionJsonPath in Directory.GetFiles(versionsRoot, "version.json", SearchOption.AllDirectories))
            {
                try
                {
                    string json = File.ReadAllText(versionJsonPath);
                    var version = JsonConvert.DeserializeObject<CustomBaseVersion>(json);
                    if (version == null || version.assetId <= 0 || string.IsNullOrWhiteSpace(version.version) || string.IsNullOrWhiteSpace(version.defaultAviVersion))
                    {
                        MCBLogger.LogWarning($"[MCB] Ignoring invalid imported version metadata at {versionJsonPath}");
                        continue;
                    }

                    version.isImported = true;
                    importedVersions.Add(version);
                }
                catch (Exception ex)
                {
                    MCBLogger.LogWarning($"[MCB] Failed to parse imported version metadata at {versionJsonPath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to scan imported versions: {ex.Message}");
        }

        cachedImportedVersions = new List<CustomBaseVersion>(importedVersions);
        cachedImportedVersionsAtUtc = DateTime.UtcNow;
    }

    private static bool IsLocalVersionCacheFresh(DateTime cachedAtUtc)
    {
        return cachedAtUtc != DateTime.MinValue && DateTime.UtcNow - cachedAtUtc < LocalVersionCacheDuration;
    }

    public List<CustomBaseVersion> GetAllVersions()
    {
        if (!HasServerAccess)
        {
            return importedVersions
                .Where(v => v != null)
                .OrderByDescending(v => ParseVersion(v.version))
                .ToList();
        }

        // Merge sources while letting local variants override matching server entries.
        var merged = new Dictionary<string, CustomBaseVersion>(StringComparer.Ordinal);
        int selectedAssetId = GetSelectedAsset()?.id ?? 0;

        foreach (var version in serverVersions)
        {
            if (!BelongsToSelectedAsset(version, selectedAssetId)) continue;
            if (version == null) continue;
            merged[GetVersionKey(version)] = version;
        }

        foreach (var version in importedVersions)
        {
            if (!BelongsToSelectedAsset(version, selectedAssetId)) continue;
            if (version == null) continue;
            merged[GetVersionKey(version)] = version;
        }

        foreach (var version in unsubmittedVersions)
        {
            if (!BelongsToSelectedAsset(version, selectedAssetId)) continue;
            if (version == null) continue;
            merged[GetVersionKey(version)] = version;
        }

        return merged.Values.OrderByDescending(v => ParseVersion(v.version)).ToList();
    }

    private static bool BelongsToSelectedAsset(CustomBaseVersion version, int selectedAssetId)
    {
        return version != null && selectedAssetId > 0 && version.assetId == selectedAssetId;
    }

    private static string GetVersionKey(CustomBaseVersion version)
    {
        return $"{version.assetId}|{version.version}|{version.defaultAviVersion}";
    }
    
    public Version ParseVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return new Version(0,0);
        if (v.Count(c => c == '.') == 0) v += ".0.0";
        if (v.Count(c => c == '.') == 1) v += ".0";
        return Version.TryParse(v, out var ver) ? ver : new Version(0,0);
    }

    public int CompareVersions(string v1, string v2) => ParseVersion(v1).CompareTo(ParseVersion(v2));

    public AvatarDiscoveredAsset GetSelectedAsset()
    {
        return assetGalleryModule != null ? assetGalleryModule.SelectedAsset : null;
    }

    public bool SyncBaseFbxFilesFromSelectedAsset()
    {
        var selectedAsset = GetSelectedAsset();
        if (selectedAsset?.sourceFiles == null || selectedAsset.sourceFiles.Length == 0 || baseFbxFilesProp == null)
        {
            return false;
        }

        var paths = selectedAsset.sourceFiles
            .Where(file => file != null
                           && string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(file.path))
            .Select(file => MCBUtils.ToUnityPath(file.path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
        {
            return false;
        }

        var fbxAssets = new List<GameObject>();
        foreach (string path in paths)
        {
            var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (fbxAsset == null || !(AssetImporter.GetAtPath(path) is ModelImporter))
            {
                MCBLogger.LogWarning($"[MCBEditor] Selected custom base source FBX is not present in this project: {path}");
                continue;
            }

            fbxAssets.Add(fbxAsset);
        }

        if (fbxAssets.Count == 0)
        {
            return false;
        }

        if (AreBaseFbxReferencesEqual(fbxAssets))
        {
            return true;
        }

        baseFbxFilesProp.ClearArray();
        foreach (var fbxAsset in fbxAssets)
        {
            baseFbxFilesProp.InsertArrayElementAtIndex(baseFbxFilesProp.arraySize);
            baseFbxFilesProp.GetArrayElementAtIndex(baseFbxFilesProp.arraySize - 1).objectReferenceValue = fbxAsset;
        }

        serializedObject.ApplyModifiedProperties();
        InvalidateDetectedAvatarFbxCache();
        MCBLogger.Log($"[MCBEditor] Synced {baseFbxFilesProp.arraySize} target FBX file(s) from selected custom base source ModelFiles.");
        return true;
    }

    public string GetSelectedAssetDisplayName()
    {
        var selectedAsset = GetSelectedAsset();
        return selectedAsset != null && !string.IsNullOrWhiteSpace(selectedAsset.name)
            ? selectedAsset.name
            : "Custom Base";
    }

    public List<string> GetDetectedAvatarFbxPaths()
    {
        if (customBaseTarget == null)
        {
            return new List<string>();
        }

        Transform root = customBaseTarget.transform.root;
        int rootInstanceId = root != null ? root.GetInstanceID() : 0;
        string sharedCacheKey = BuildDetectedAvatarFbxCacheKey(rootInstanceId);
        if (!detectedAvatarFbxCacheDirty &&
            cachedDetectedAvatarFbxPaths != null &&
            cachedDetectedAvatarRootInstanceId == rootInstanceId)
        {
            return new List<string>(cachedDetectedAvatarFbxPaths);
        }

        if (!string.IsNullOrEmpty(sharedCacheKey) && SharedDetectedAvatarFbxPathCache.TryGetValue(sharedCacheKey, out var sharedPaths))
        {
            cachedDetectedAvatarRootInstanceId = rootInstanceId;
            cachedDetectedAvatarFbxPaths = new List<string>(sharedPaths);
            detectedAvatarFbxCacheDirty = false;
            return new List<string>(cachedDetectedAvatarFbxPaths);
        }

        var uniquePaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            path = path.Replace("\\", "/");
            if (seen.Add(path))
            {
                uniquePaths.Add(path);
            }
        }

        if (root != null)
        {
            foreach (var smr in MeshFinder.GetAllSkinnedMeshRenderers(root))
            {
                TryAddFbxPathFromObject(smr != null ? smr.sharedMesh : null, TryAddPath);
            }

            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                TryAddFbxPathFromObject(meshFilter != null ? meshFilter.sharedMesh : null, TryAddPath);
            }
        }

        if (baseFbxFilesProp != null)
        {
            for (int i = 0; i < baseFbxFilesProp.arraySize; i++)
            {
                var fbx = baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                if (fbx != null)
                {
                    TryAddPath(AssetDatabase.GetAssetPath(fbx));
                }
            }
        }

        cachedDetectedAvatarRootInstanceId = rootInstanceId;
        cachedDetectedAvatarFbxPaths = uniquePaths;
        detectedAvatarFbxCacheDirty = false;
        if (!string.IsNullOrEmpty(sharedCacheKey))
        {
            SharedDetectedAvatarFbxPathCache[sharedCacheKey] = new List<string>(uniquePaths);
        }

        return new List<string>(uniquePaths);
    }

    private string BuildDetectedAvatarFbxCacheKey(int rootInstanceId)
    {
        if (rootInstanceId == 0)
        {
            return null;
        }

        var basePaths = new List<string>();
        if (baseFbxFilesProp != null)
        {
            for (int i = 0; i < baseFbxFilesProp.arraySize; i++)
            {
                var fbx = baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                if (fbx == null)
                {
                    continue;
                }

                string path = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(fbx));
                if (!string.IsNullOrWhiteSpace(path))
                {
                    basePaths.Add(path);
                }
            }
        }

        basePaths.Sort(StringComparer.OrdinalIgnoreCase);
        return rootInstanceId + "|" + string.Join("|", basePaths);
    }

    private bool AreBaseFbxReferencesEqual(List<GameObject> fbxAssets)
    {
        if (baseFbxFilesProp == null || fbxAssets == null)
        {
            return false;
        }

        if (baseFbxFilesProp.arraySize != fbxAssets.Count)
        {
            return false;
        }

        for (int i = 0; i < fbxAssets.Count; i++)
        {
            var current = baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            if (current != fbxAssets[i])
            {
                return false;
            }
        }

        return true;
    }

    private void InvalidateDetectedAvatarFbxCache(bool clearSharedCache = false)
    {
        int rootInstanceId = 0;
        try
        {
            rootInstanceId = customBaseTarget != null && customBaseTarget.transform != null && customBaseTarget.transform.root != null
                ? customBaseTarget.transform.root.GetInstanceID()
                : 0;
        }
        catch
        {
            rootInstanceId = 0;
        }

        detectedAvatarFbxCacheDirty = true;
        cachedDetectedAvatarFbxPaths = null;
        cachedDetectedAvatarRootInstanceId = 0;

        if (!clearSharedCache)
        {
            return;
        }

        if (rootInstanceId != 0)
        {
            string prefix = rootInstanceId + "|";
            var keysToRemove = SharedDetectedAvatarFbxPathCache.Keys
                .Where(key => key != null && key.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            foreach (string key in keysToRemove)
            {
                SharedDetectedAvatarFbxPathCache.Remove(key);
            }
        }
        else
        {
            SharedDetectedAvatarFbxPathCache.Clear();
        }
    }

    private static void TryAddFbxPathFromObject(UnityEngine.Object obj, Action<string> addPath)
    {
        if (obj == null || addPath == null)
        {
            return;
        }

        string assetPath = AssetDatabase.GetAssetPath(obj);
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        if (!(AssetImporter.GetAtPath(assetPath) is ModelImporter))
        {
            TryAddFbxPathFromNativeMeshPayload(assetPath, addPath);
            return;
        }

        addPath(assetPath);
    }

    private static void TryAddFbxPathFromNativeMeshPayload(string meshAssetPath, Action<string> addPath)
    {
        if (string.IsNullOrWhiteSpace(meshAssetPath) || addPath == null)
        {
            return;
        }

        var payload = AssetDatabase.LoadMainAssetAtPath(meshAssetPath) as NativeMeshPayloadAsset;
        string sourcePath = MCBUtils.ToUnityPath(payload?.sourceFbxPath);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        if (!(AssetImporter.GetAtPath(sourcePath) is ModelImporter))
        {
            MCBLogger.LogWarning($"[MCBEditor] Advanced mesh payload points to a source FBX that is not present in this project: {sourcePath}");
            return;
        }

        addPath(sourcePath);
    }

    private void RepaintFromConnectivityMonitor()
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

        RefreshUiToolkitSections();
        Repaint();
    }

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
            InvalidateDetectedAvatarFbxCache(true);
            DetectAndLoadCached();
            RefreshUiToolkitSections();
            Repaint();
        };
    }

    private void RepaintFromPackageVersionStatus()
    {
        RefreshUiToolkitSections();
        Repaint();
    }
}
#endif
