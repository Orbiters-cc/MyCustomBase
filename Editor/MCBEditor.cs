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
    private const float ConnectivityOverrideCardHeight = 86f;
    private const float ConnectivityOverrideCardSpacing = 6f;
    private static readonly string[] UiToolkitStyleSheets =
    {
        "Packages/orbiters.mcb/Editor/Styles/mcb-theme.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-account.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-gallery.uss"
    };

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
    private VisualElement galleryHost;
    private VisualElement selectedAssetActionsHost;
    private VisualElement commentsHost;
    private IMGUIContainer topImGuiContainer;
    private IMGUIContainer middleImGuiContainer;
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
    public bool isFetching, isDownloading, isDeleting, isSubmitting;
    public string fetchError, downloadError, deleteError, submitError;
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
    private Vector2 connectivityReportScroll;
    
    public string uiRenderingError;
    private string lastUiRenderingExceptionSignature;
    private List<string> cachedDetectedAvatarFbxPaths;
    private int cachedDetectedAvatarRootInstanceId;
    private bool detectedAvatarFbxCacheDirty = true;
    private bool delayedDetectionScheduled;

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
        MCBConnectivityMonitor.StatusChanged += RepaintFromConnectivityMonitor;
        MCBConnectivityMonitor.EnsureCheckStarted(authToken);
        MCBPackageVersionService.StatusChanged += RepaintFromPackageVersionStatus;
        MCBPackageVersionService.EnsureCheckStarted(authToken);
        
        // Ensure modules are enabled
        versionModule.OnEnable();
        
        // Start async initialization
        StartAsyncInitialization();
        
        // Subscribe to play mode state changes
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.projectChanged += OnProjectChanged;
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }
    
    private void OnDisable()
    {
        // Unsubscribe from play mode state changes
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        accountModule?.DetachUIToolkit();
        dependencyInstallerModule?.DetachUIToolkit();
        dependencyInstallerModule?.Dispose();
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
        commentsHost = null;
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

        commentsHost = new VisualElement();
        commentsHost.AddToClassList("mcb-comments-host");
        uiToolkitRoot.Add(commentsHost);

        bottomBarImGuiContainer = new IMGUIContainer(DrawToolkitBottomBarImGui);
        bottomBarImGuiContainer.AddToClassList("mcb-imgui-statusbar");
        uiToolkitRoot.Add(bottomBarImGuiContainer);

        accountModule?.AttachUIToolkit(accountHost);
        dependencyInstallerModule?.AttachUIToolkit(dependencyHost);
        assetGalleryModule?.AttachUIToolkit(galleryHost, selectedAssetActionsHost, commentsHost);
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
            assetGalleryModule?.RefreshUIToolkit();
            ApplyDependencyBlockerState();
            OrderUiToolkitLayers();
            chromeSurfaceHost?.WakeForSeconds(20f);
            chromeSurfaceHost?.MarkDirtyRepaint();
            topImGuiContainer?.MarkDirtyRepaint();
            middleImGuiContainer?.MarkDirtyRepaint();
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
        topImGuiContainer?.BringToFront();
        galleryHost?.BringToFront();
        selectedAssetActionsHost?.BringToFront();
        middleImGuiContainer?.BringToFront();
        commentsHost?.BringToFront();
        bottomBarImGuiContainer?.BringToFront();
    }

    private void ApplyDependencyBlockerState()
    {
        bool blocked = dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies;
        DisplayStyle contentDisplay = blocked ? DisplayStyle.None : DisplayStyle.Flex;

        if (topImGuiContainer != null) topImGuiContainer.style.display = contentDisplay;
        if (galleryHost != null) galleryHost.style.display = contentDisplay;
        if (selectedAssetActionsHost != null) selectedAssetActionsHost.style.display = contentDisplay;
        if (middleImGuiContainer != null) middleImGuiContainer.style.display = contentDisplay;
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
            SafeUiCall(DrawConnectivityDiagnosticsPanel);

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
                    SafeUiCall(() => versionModule.Draw());
                    SafeUiCall(() => avatarOptionsModule?.Draw());
                }
            }
            else if (HasServerAccess)
            {
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
        InvalidateDetectedAvatarFbxCache();
        SmrPathService.InvalidateCache();
        LoadImportedVersions();
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
        InvalidateDetectedAvatarFbxCache();
        SmrPathService.InvalidateCache();
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
            TryLoadCachedVersionsAndRefetch();
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
        
        Repaint();
        MCBLogger.Log($"[MCBEditor] Updated with {versions.Count} server versions");
    }

    private void TryLoadCachedVersionsAndRefetch()
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
            Repaint();
            MCBLogger.Log($"[MCBEditor] Loaded {cached.versions.Count} cached versions");
        }

        // Start background version fetch (will update UI when complete)
        if (HasServerAccess)
        {
            fetchAttempted = true;
            versionService.StartVersionFetchInBackground(fbxPath, authToken, selectedAsset.id, useCache: false);
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
        TryLoadCachedVersionsAndRefetch();
        assetGalleryModule?.RefreshIfNeeded(force: true);
    }

    private void OnVersionFetchError(string error)
    {
        fetchError = error;
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
            SafeUiCall(DrawConnectivityDiagnosticsPanel);
            
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

    private void DrawLogoutSectionSafely()
    {
        if (!isAuthenticated) return;

        Color originalColor = GUI.backgroundColor;

        try
        {
            authModule.DrawLogoutButton();
        }
        catch (Exception ex)
        {
            if (ex is ExitGUIException) throw;
 
            GUI.backgroundColor = originalColor;
            RecordUiException(ex);
            DrawFallbackLogoutButton();
        }
        finally
        {
            GUI.backgroundColor = originalColor;
        }
    }

    private void DrawFallbackLogoutButton()
    {
        EditorGUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Logout", GUILayout.Width(100f), GUILayout.Height(25f)))
        {
            if (EditorUtility.DisplayDialog("Confirm Logout", "Are you sure you want to log out?", "Logout", "Cancel"))
            {
                if (AuthenticationService.RemoveAuth())
                {
                    CheckAuthentication();
                    Repaint();
                }
            }
        }

        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(10);
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

    private void DrawConnectivityDiagnosticsPanel()
    {
        bool isDevModeEnabled = MCBUtils.isDevEnvironment;
        bool showDevModeWarning = isDevModeEnabled && IsDevModeWarningEnabled;
        ApiSimulationMode simulationMode = MCBUtils.apiSimulationMode;
        bool isFakeNetworkErrorEnabled = simulationMode != ApiSimulationMode.Off;
        bool hasOverrideUi = showDevModeWarning || isFakeNetworkErrorEnabled;
        bool hasFailureReport = !string.IsNullOrEmpty(MCBConnectivityMonitor.FailureReport);

        if (!hasFailureReport && !hasOverrideUi)
        {
            return;
        }

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            if (hasFailureReport)
            {
                EditorGUILayout.HelpBox("The tool cannot connect to the server, copy the data bellow and send it to @blackorbit on discord", MessageType.Error);
            }

            if (hasOverrideUi)
            {
                EditorGUILayout.LabelField("Some advanced connectivity overrides are enabled.", EditorStyles.boldLabel);
                DrawConnectivityOverrideBoxes(showDevModeWarning, isFakeNetworkErrorEnabled, simulationMode);

                Color oldColor = GUI.backgroundColor;
                GUI.backgroundColor = Color.green;
                if (GUILayout.Button("Refresh with overrides off", GUILayout.Height(32f)))
                {
                    RefreshWithOverridesOff();
                }
                GUI.backgroundColor = oldColor;
            }

            if (hasFailureReport)
            {
                connectivityReportScroll = EditorGUILayout.BeginScrollView(connectivityReportScroll, GUILayout.MinHeight(140f));
                var style = new GUIStyle(EditorStyles.textArea) { wordWrap = false };
                EditorGUILayout.TextArea(MCBConnectivityMonitor.FailureReport, style, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();

                if (GUILayout.Button("copy in the clipboard", GUILayout.Height(24f)))
                {
                    EditorGUIUtility.systemCopyBuffer = MCBConnectivityMonitor.FailureReport;
                }
            }
        }
    }

    private void DrawConnectivityOverrideBoxes(bool isDevModeEnabled, bool isFakeNetworkErrorEnabled, ApiSimulationMode simulationMode)
    {
        int cardCount = 0;
        if (isDevModeEnabled) cardCount++;
        if (isFakeNetworkErrorEnabled) cardCount++;
        if (cardCount <= 0) return;

        Rect rowRect = GUILayoutUtility.GetRect(0f, ConnectivityOverrideCardHeight, GUILayout.ExpandWidth(true));
        float cardWidth = (rowRect.width - (ConnectivityOverrideCardSpacing * (cardCount - 1))) / cardCount;
        float currentX = rowRect.x;

        if (isDevModeEnabled)
        {
            Rect cardRect = new Rect(currentX, rowRect.y, cardWidth, ConnectivityOverrideCardHeight);
            DrawConnectivityOverrideBox(
                cardRect,
                "Dev mode",
                "Requests are using the dev environment endpoints.",
                DisableDevModeOverrideAndRefresh);
            currentX += cardWidth + ConnectivityOverrideCardSpacing;
        }

        if (isFakeNetworkErrorEnabled)
        {
            Rect cardRect = new Rect(currentX, rowRect.y, cardWidth, ConnectivityOverrideCardHeight);
            DrawConnectivityOverrideBox(
                cardRect,
                "Fake network error",
                GetConnectivitySimulationLabel(simulationMode),
                DisableFakeNetworkErrorOverrideAndRefresh);
        }
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

    private void DrawConnectivityOverrideBox(Rect rect, string title, string description, Action onTurnOff)
    {
        GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

        Rect contentRect = new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, rect.height - 16f);
        Rect titleRect = new Rect(contentRect.x, contentRect.y, contentRect.width, 18f);
        GUI.Label(titleRect, title, EditorStyles.boldLabel);

        float buttonHeight = 22f;
        float buttonWidth = Mathf.Min(90f, contentRect.width);
        Rect buttonRect = new Rect(contentRect.x, contentRect.yMax - buttonHeight, buttonWidth, buttonHeight);

        float descriptionY = titleRect.yMax + 4f;
        float descriptionHeight = Mathf.Max(16f, buttonRect.y - descriptionY - 6f);
        Rect descriptionRect = new Rect(contentRect.x, descriptionY, contentRect.width, descriptionHeight);
        GUI.Label(descriptionRect, description, EditorStyles.wordWrappedLabel);

        if (GUI.Button(buttonRect, "Turn off"))
        {
            onTurnOff?.Invoke();
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
        MCBPackageVersionService.EnsureCheckStarted(authToken, true);
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

    public void LoadUnsubmittedVersions()
    {
        unsubmittedVersions.Clear();
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
    }

    public void LoadImportedVersions()
    {
        importedVersions.Clear();

        string versionsRoot = Path.GetFullPath(MCBUtils.ASSET_VERSIONS_FOLDER);
        if (!Directory.Exists(versionsRoot))
        {
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
        if (!detectedAvatarFbxCacheDirty &&
            cachedDetectedAvatarFbxPaths != null &&
            cachedDetectedAvatarRootInstanceId == rootInstanceId)
        {
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
        return new List<string>(uniquePaths);
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

    private void InvalidateDetectedAvatarFbxCache()
    {
        detectedAvatarFbxCacheDirty = true;
        cachedDetectedAvatarFbxPaths = null;
        cachedDetectedAvatarRootInstanceId = 0;
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
            return;
        }

        addPath(assetPath);
    }

    private void RepaintFromConnectivityMonitor()
    {
        RefreshUiToolkitSections();
        Repaint();
    }

    private void RepaintFromPackageVersionStatus()
    {
        RefreshUiToolkitSections();
        Repaint();
    }
}
#endif
