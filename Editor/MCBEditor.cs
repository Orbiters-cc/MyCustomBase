#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
    private static readonly string[] UiToolkitStyleSheets =
    {
        "Packages/orbiters.mcb/Editor/Styles/mcb-account.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-gallery.uss",
        "Packages/orbiters.toolkit/Editor/UI/gallery-card.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-creator.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-sections.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-version.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-avatar-options.uss"
    };

    // --- Target ---
    public MyCustomBase customBaseTarget;
    [NonSerialized] private McbSession session;
    /// <summary>The avatar's session: its versions, selection, account, busy state and operations. This Inspector is one view of it.</summary>
    public McbSession Session => session ?? (session = McbSession.For(customBaseTarget));

    // The session's state, as this view's modules read it.
    public new SerializedObject serializedObject => Session.serializedObject;
    public SerializedProperty specifyCustomBaseFbxProp => Session.specifyCustomBaseFbxProp;
    public SerializedProperty baseFbxFilesProp => Session.baseFbxFilesProp;
    public SerializedProperty blendShapeValuesProp => Session.blendShapeValuesProp;
    public SerializedProperty isCreatorModeProp => Session.isCreatorModeProp;
    public SerializedProperty avatarLogicPrefabProp => Session.avatarLogicPrefabProp;
    public SerializedProperty customBlendshapesForCreatorProp => Session.customBlendshapesForCreatorProp;
    public SerializedProperty modelFileBuildEntriesProp => Session.modelFileBuildEntriesProp;
    public SerializedProperty useAdvancedMeshReplacementForCreatorProp => Session.useAdvancedMeshReplacementForCreatorProp;
    public SerializedProperty useHdiffFbxDeltaForCreatorProp => Session.useHdiffFbxDeltaForCreatorProp;
    public SerializedProperty includeCustomVeinsForCreatorProp => Session.includeCustomVeinsForCreatorProp;
    public SerializedProperty customVeinsNormalMapProp => Session.customVeinsNormalMapProp;
    public SerializedProperty includeDynamicNormalsBodyForCreatorProp => Session.includeDynamicNormalsBodyForCreatorProp;
    public SerializedProperty includeDynamicNormalsFlexingForCreatorProp => Session.includeDynamicNormalsFlexingForCreatorProp;
    public SerializedProperty includeSuggestRealisticForCreatorProp => Session.includeSuggestRealisticForCreatorProp;
    public SerializedProperty suggestRealisticMeshPathsForCreatorProp => Session.suggestRealisticMeshPathsForCreatorProp;
    public WarningsModule warningsModule => Session.warningsModule;
    public bool isAuthenticated { get => Session.isAuthenticated; set => Session.isAuthenticated = value; }
    public string authToken { get => Session.authToken; set => Session.authToken = value; }
    public bool fetchAttempted { get => Session.fetchAttempted; set => Session.fetchAttempted = value; }
    public bool isFetching { get => Session.isFetching; set => Session.isFetching = value; }
    public bool isDownloading { get => Session.isDownloading; set => Session.isDownloading = value; }
    public bool isDeleting { get => Session.isDeleting; set => Session.isDeleting = value; }
    public bool isSubmitting { get => Session.isSubmitting; set => Session.isSubmitting = value; }
    public bool isApplying { get => Session.isApplying; set => Session.isApplying = value; }
    public string fetchError { get => Session.fetchError; set => Session.fetchError = value; }
    public string submitError { get => Session.submitError; set => Session.submitError = value; }
    public string accessDeniedAssetId { get => Session.accessDeniedAssetId; set => Session.accessDeniedAssetId = value; }
    public string currentBaseFbxHash { get => Session.currentBaseFbxHash; set => Session.currentBaseFbxHash = value; }
    public string currentAppliedFbxHash { get => Session.currentAppliedFbxHash; set => Session.currentAppliedFbxHash = value; }
    public bool isCustomBase { get => Session.isCustomBase; set => Session.isCustomBase = value; }
    public List<CustomBaseVersion> serverVersions { get => Session.serverVersions; set => Session.serverVersions = value; }
    public List<CustomBaseVersion> importedVersions { get => Session.importedVersions; set => Session.importedVersions = value; }
    public List<CustomBaseVersion> unsubmittedVersions { get => Session.unsubmittedVersions; set => Session.unsubmittedVersions = value; }
    public CustomBaseVersion recommendedVersion { get => Session.recommendedVersion; set => Session.recommendedVersion = value; }
    public CustomBaseVersion selectedVersionForAction { get => Session.selectedVersionForAction; set => Session.selectedVersionForAction = value; }
    public List<UserCustomVersionEntry> userCustomVersions { get => Session.userCustomVersions; set => Session.userCustomVersions = value; }
    public UserCustomVersionEntry selectedCustomVersionForAction { get => Session.selectedCustomVersionForAction; set => Session.selectedCustomVersionForAction = value; }
    public bool currentIsCustom { get => Session.currentIsCustom; set => Session.currentIsCustom = value; }
    public bool HasServerAccess => Session.HasServerAccess;

    // --- Modules (this view's sections) ---
    public VersionManagementModule versionModule;
    private AssetGalleryModule assetGalleryModule;
    private AdvancedModeModule advancedModule;
    private DependencyInstallerModule dependencyInstallerModule;
    private AvatarOptionsModule avatarOptionsModule;
    private AdjustMaterialModule adjustMaterialModule;

    private VisualElement uiToolkitRoot;
    private OrbitersGlowSurfaceElement chromeSurfaceHost;
    private VisualElement headerHost;
    private VisualElement bannerHost;
    private VisualElement dependencyHost;
    private VisualElement connectivityHost;
    private VisualElement statusHost;
    private VisualElement galleryHost;
    private VisualElement versionHost;
    private VisualElement avatarOptionsHost;
    private VisualElement adjustMaterialHost;
    private VisualElement commentsHost;
    private IMGUIContainer bottomBarImGuiContainer;
    private IVisualElementScheduledItem dynamicUiSchedule;
    public const float AssetViewImGuiPaddingLeft = 24f;
    public const float AssetViewImGuiPaddingRight = 24f;
    public const float AssetViewImGuiPaddingTop = 18f;
    public const float AssetViewImGuiPaddingBottom = 22f;

    [NonSerialized] private McbViewRefresh viewRefresh;
    public string uiRenderingError;
    private string lastUiRenderingExceptionSignature;
    private bool projectRefreshPending;

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
        customBaseTarget = target as MyCustomBase;
        if (customBaseTarget == null)
        {
            // An Inspector whose component is gone has nothing to show.
            EditorApplication.delayCall += () => { if (this != null) DestroyImmediate(this); };
            return;
        }
        session = McbSession.For(customBaseTarget);
        viewRefresh = new McbViewRefresh(RefreshSections);
        session.Changed += OnSessionChanged;
        session.AuthenticationChanged += OnAuthenticationChanged;
        session.ModelsDetected += OnModelsDetected;
        session.warningsModule.Changed += OnWarningsChanged;

        // Ensure ProgressBarManager is initialized early so it subscribes to task events
        var __ensureProgressBars = ProgressBarManager.Instance;

        versionModule = new VersionManagementModule(this);
        assetGalleryModule = new AssetGalleryModule(this);
        advancedModule = new AdvancedModeModule(this);
        dependencyInstallerModule = new DependencyInstallerModule(this);
        dependencyInstallerModule.Initialize();
        avatarOptionsModule = new AvatarOptionsModule(this);
        adjustMaterialModule = new AdjustMaterialModule(this);

        // Ensure modules are enabled
        versionModule.OnEnable();

        session.BeginBackgroundRefresh();

        // Subscribe to play mode state changes
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        LiveEditors.Add(this);
        HookProjectChanges();

        PlayPendingComponentAddedMeshEffect();
    }

    private void OnDisable()
    {
        // Unsubscribe from play mode state changes
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        if (session != null)
        {
            session.Changed -= OnSessionChanged;
            session.AuthenticationChanged -= OnAuthenticationChanged;
            session.ModelsDetected -= OnModelsDetected;
            session.warningsModule.Changed -= OnWarningsChanged;
        }
        viewRefresh?.Cancel();
        dependencyInstallerModule?.DetachUIToolkit();
        dependencyInstallerModule?.Dispose();
        versionModule?.DetachUIToolkit();
        avatarOptionsModule?.DetachUIToolkit();
        assetGalleryModule?.DetachUIToolkit();
        dynamicUiSchedule?.Pause();
        dynamicUiSchedule = null;
        uiToolkitRoot = null;
        chromeSurfaceHost = null;
        headerHost = null;
        bannerHost = null;
        dependencyHost = null;
        connectivityHost = null;
        statusHost = null;
        galleryHost = null;
        versionHost = null;
        avatarOptionsHost = null;
        adjustMaterialHost = null;
        commentsHost = null;
        bottomBarImGuiContainer = null;
        LiveEditors.Remove(this);
    }

    public override VisualElement CreateInspectorGUI()
    {
        dynamicUiSchedule?.Pause();
        dynamicUiSchedule = null;

        var shell = new OrbitersInspectorShell();
        uiToolkitRoot = shell;
        LoadUiToolkitStyleSheets(uiToolkitRoot);
        chromeSurfaceHost = shell.Glow;
        headerHost = shell.Header;
        // MCB is in beta: the same pill as My Avatar's features, in the header's corner.
        var stage = new Orbiters.Toolkit.Editor.StageBadge(Orbiters.Toolkit.Editor.FeatureStage.Beta);
        stage.AddToClassList("mcb-header__stage");
        shell.Header.Add(stage);
        bannerHost = shell.Banner;
        // The shared Orbiters account row (sign-in buttons when signed out), as in My Avatar.
        shell.Account.Add(new OrbitersAccountElement("mcb/check-connection", _ => SyncAuthentication(),
            "Connect your Orbiters account to publish and download your bases.", confirmLogout: true));

        dependencyHost = new VisualElement();
        uiToolkitRoot.Add(dependencyHost);

        connectivityHost = new VisualElement();
        uiToolkitRoot.Add(connectivityHost);

        galleryHost = new VisualElement();
        uiToolkitRoot.Add(galleryHost);

        statusHost = new VisualElement();
        statusHost.AddToClassList("mcb-status-host");
        uiToolkitRoot.Add(statusHost);

        versionHost = new VisualElement();
        versionHost.AddToClassList("mcb-version-host");
        uiToolkitRoot.Add(versionHost);

        avatarOptionsHost = new VisualElement();
        avatarOptionsHost.AddToClassList("mcb-avatar-options-host");
        uiToolkitRoot.Add(avatarOptionsHost);

        adjustMaterialHost = new VisualElement();
        adjustMaterialHost.AddToClassList("mcb-avatar-options");
        adjustMaterialHost.AddToClassList("mcb-adjust-material-host");
        uiToolkitRoot.Add(adjustMaterialHost);

        commentsHost = new VisualElement();
        commentsHost.AddToClassList("mcb-comments-host");
        uiToolkitRoot.Add(commentsHost);

        bottomBarImGuiContainer = new IMGUIContainer(DrawToolkitBottomBarImGui);
        bottomBarImGuiContainer.AddToClassList("mcb-imgui-statusbar");
        uiToolkitRoot.Add(bottomBarImGuiContainer);

        dependencyInstallerModule?.AttachUIToolkit(dependencyHost);
        assetGalleryModule?.AttachUIToolkit(galleryHost, commentsHost);
        versionModule?.AttachUIToolkit(versionHost);
        avatarOptionsModule?.AttachUIToolkit(avatarOptionsHost);
        // The modules only take their hosts: everything is built once, here (each used to build itself on attach, then again).
        RequestRefresh(McbViewParts.All);
        viewRefresh?.Flush();
        OrderUiToolkitLayers();
        dynamicUiSchedule = uiToolkitRoot.schedule.Execute(() => assetGalleryModule?.UpdateDynamicUiContent()).Every(3000);
        // Shown again (an inactive docked tab selected): catch up with the project changes made while hidden.
        uiToolkitRoot.RegisterCallback<AttachToPanelEvent>(_ => { if (projectRefreshPending) uiToolkitRoot?.schedule.Execute(RefreshPendingEditors); });

        return uiToolkitRoot;
    }

    /// <summary>Rebuilds these sections of this view on the next editor frame, together with whatever else was asked for by then.</summary>
    public void RequestRefresh(McbViewParts parts)
    {
        if (viewRefresh != null) viewRefresh.Request(parts);
        else RefreshSections(parts);
    }

    // The session changed (here or in another view of the avatar), already coalesced to once a frame: refresh what it says.
    private void OnSessionChanged(McbViewParts parts)
    {
        // A download, delete or switch starting or ending changes the version section's buttons.
        if ((parts & McbViewParts.Busy) != 0) parts |= McbViewParts.Versions;
        parts &= ~(McbViewParts.Busy | McbViewParts.Repaint);
        if (parts != McbViewParts.None && viewRefresh != null)
        {
            viewRefresh.Request(parts);
            viewRefresh.Flush();
        }
        Repaint();
    }

    private void OnAuthenticationChanged() => assetGalleryModule?.OnAuthenticationChanged();

    private void OnModelsDetected() => assetGalleryModule?.RefreshIfNeeded(force: true);

    private void RefreshSections(McbViewParts parts)
    {
        bool Has(McbViewParts part) => (parts & part) != 0;

        Session.SyncAppliedCustomBaseName();
        if (uiToolkitRoot == null)
        {
            return;
        }

        // Each section on its own: one that fails is reported (in the status bar and the Console) without hiding the
        // sections after it.
        void Section(Action refresh)
        {
            try { refresh(); }
            catch (Exception ex) { RecordUiException(ex); }
        }

        if (Has(McbViewParts.Banner)) Section(DrawVectorBannerUIToolkit);
        if (Has(McbViewParts.Dependencies)) Section(() => dependencyInstallerModule?.RefreshUIToolkit());
        if (Has(McbViewParts.Connectivity)) Section(RefreshConnectivityDiagnosticsUIToolkit);
        if (Has(McbViewParts.Gallery)) Section(() => assetGalleryModule?.RefreshUIToolkit());
        if (Has(McbViewParts.Versions)) Section(() =>
        {
            // The full version DTO is not Unity-serialized. Rehydrate it from local/server
            // metadata before both the action button and avatar options read the state.
            if (customBaseTarget != null &&
                (customBaseTarget.appliedCustomBaseVersion == null || !isCustomBase) &&
                !string.IsNullOrEmpty(customBaseTarget.appliedCustomBaseVersionString))
                versionModule?.actions.UpdateAppliedVersionAndState();
            versionModule?.RefreshUIToolkit();
        });
        if (Has(McbViewParts.AvatarOptions)) Section(() => avatarOptionsModule?.RefreshUIToolkit());
        if (Has(McbViewParts.Status)) Section(RefreshStatusMessagesUIToolkit);
        if (Has(McbViewParts.AdjustMaterial)) Section(RefreshAdjustMaterialUIToolkit);
        Section(ApplyDependencyBlockerState);
        Section(OrderUiToolkitLayers);
        Section(() =>
        {
            chromeSurfaceHost?.WakeForSeconds(20f);
            chromeSurfaceHost?.MarkDirtyRepaint();
        });
        bottomBarImGuiContainer?.MarkDirtyRepaint();
    }

    private void OrderUiToolkitLayers()
    {
        chromeSurfaceHost?.SendToBack();
        headerHost?.BringToFront();
        dependencyHost?.BringToFront();
        connectivityHost?.BringToFront();
        galleryHost?.BringToFront();
        statusHost?.BringToFront();
        versionHost?.BringToFront();
        avatarOptionsHost?.BringToFront();
        adjustMaterialHost?.BringToFront();
        commentsHost?.BringToFront();
        bottomBarImGuiContainer?.BringToFront();
    }

    private void ApplyDependencyBlockerState()
    {
        bool blocked = dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies;
        DisplayStyle contentDisplay = blocked ? DisplayStyle.None : DisplayStyle.Flex;

        if (connectivityHost != null)
        {
            connectivityHost.style.display = blocked || connectivityHost.childCount == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }
        if (galleryHost != null) galleryHost.style.display = contentDisplay;
        if (statusHost != null && blocked) statusHost.style.display = DisplayStyle.None;
        if (versionHost != null && blocked) versionHost.style.display = DisplayStyle.None;
        if (avatarOptionsHost != null && blocked) avatarOptionsHost.style.display = DisplayStyle.None;
        if (adjustMaterialHost != null && blocked) adjustMaterialHost.style.display = DisplayStyle.None;
        if (commentsHost != null) commentsHost.style.display = contentDisplay;
        if (bottomBarImGuiContainer != null) bottomBarImGuiContainer.style.display = contentDisplay;
    }

    private void RefreshStatusMessagesUIToolkit()
    {
        if (statusHost == null)
        {
            return;
        }

        statusHost.Clear();
        uiToolkitRoot?.RemoveFromClassList("mcb-has-status");
        statusHost.AddToClassList("mcb-status-host");

        if (dependencyInstallerModule != null && dependencyInstallerModule.HasBlockingRequiredDependencies)
        {
            statusHost.style.display = DisplayStyle.None;
            return;
        }

        bool hasContent = false;
        bool hasMajorUpdateLockout = MCBPackageVersionService.RequiresMajorUpdate;
        bool showOfflineSavedVersionsUi = !HasServerAccess && importedVersions != null && importedVersions.Count > 0;

        var packageStatus = MCBPackageVersionService.CurrentStatus;
        if (packageStatus != null && packageStatus.isDeprecated)
        {
            statusHost.Add(CreateStatusMessage("MCB update recommended", string.IsNullOrWhiteSpace(packageStatus.updateMessage)
                ? "This MCB release is deprecated. Update My Custom Base in VCC." : packageStatus.updateMessage, HelpBoxMessageType.Warning));
            hasContent = true;
        }

        if (hasMajorUpdateLockout)
        {
            var status = MCBPackageVersionService.CurrentStatus;
            if (status != null && status.requiresMajorUpdate)
            {
                string message = string.IsNullOrWhiteSpace(status.updateMessage)
                    ? $"A new major version of MCB is available.\n\nCurrent version: {status.currentVersion}\nLatest version: {status.latestVersion}\n\nUpdate the package from VCC before using connected features."
                    : status.updateMessage;

                statusHost.Add(CreateStatusMessage("Update needed", message, HelpBoxMessageType.Warning));
                hasContent = true;
            }

            if (showOfflineSavedVersionsUi)
            {
                statusHost.Add(CreateStatusMessage(null, GetOfflineSavedVersionsMessage(), HelpBoxMessageType.Info));
                hasContent = true;
            }
        }
        else if (HasServerAccess)
        {
            if (assetGalleryModule == null || !assetGalleryModule.ShouldShowGalleryOnly())
            {
                hasContent |= warningsModule != null && warningsModule.BuildUIToolkit(statusHost);
            }
        }
        else if (showOfflineSavedVersionsUi)
        {
            statusHost.Add(CreateStatusMessage(null, GetOfflineSavedVersionsMessage(), HelpBoxMessageType.Info));
            hasContent = true;
        }

        statusHost.style.display = hasContent ? DisplayStyle.Flex : DisplayStyle.None;
        uiToolkitRoot?.EnableInClassList("mcb-has-status", hasContent);
    }

    private void RefreshAdjustMaterialUIToolkit()
    {
        if (adjustMaterialHost == null)
        {
            return;
        }

        adjustMaterialHost.Clear();
        adjustMaterialHost.AddToClassList("mcb-avatar-options");
        adjustMaterialHost.AddToClassList("mcb-adjust-material-host");

        bool hasMajorUpdateLockout = MCBPackageVersionService.RequiresMajorUpdate;
        bool canShowAdjustMaterial =
            dependencyInstallerModule != null &&
            !dependencyInstallerModule.HasBlockingRequiredDependencies &&
            !hasMajorUpdateLockout &&
            HasServerAccess &&
            (assetGalleryModule == null || !assetGalleryModule.ShouldShowGalleryOnly());

        if (!canShowAdjustMaterial)
        {
            adjustMaterialHost.style.display = DisplayStyle.None;
            return;
        }

        bool hasContent = false;
        serializedObject.Update();
        try
        {
            hasContent = adjustMaterialModule != null && adjustMaterialModule.BuildUIToolkit(adjustMaterialHost);
        }
        finally
        {
            serializedObject.ApplyModifiedProperties();
        }

        adjustMaterialHost.style.display = hasContent ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void OnWarningsChanged()
    {
        RefreshStatusMessagesUIToolkit();
        Repaint();
    }

    private static string GetOfflineSavedVersionsMessage()
    {
        return "Imported saved versions are available offline. You can apply them or reset to the original avatar base without logging in.";
    }

    private static VisualElement CreateStatusMessage(string title, string message, HelpBoxMessageType messageType)
    {
        var box = new VisualElement();
        box.AddToClassList("mcb-avatar-helpbox");
        box.AddToClassList("mcb-status-message");
        box.AddToClassList(GetStatusMessageClass(messageType));

        var icon = AvatarOptionsModule.CreateOptionLabel(GetStatusMessageIcon(messageType), 14, FontStyle.Bold, Color.white);
        icon.AddToClassList("mcb-avatar-helpbox__icon");
        box.Add(icon);

        var content = new VisualElement();
        content.AddToClassList("mcb-avatar-helpbox__content");

        if (!string.IsNullOrWhiteSpace(title))
        {
            var titleLabel = AvatarOptionsModule.CreateOptionLabel(title, 13, FontStyle.Bold, Color.white);
            titleLabel.AddToClassList("mcb-status-message__title");
            content.Add(titleLabel);
        }

        var body = AvatarOptionsModule.CreateOptionLabel(message, 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
        body.AddToClassList("mcb-avatar-helpbox__text");
        content.Add(body);

        box.Add(content);
        return box;
    }

    private static string GetStatusMessageClass(HelpBoxMessageType messageType)
    {
        switch (messageType)
        {
            case HelpBoxMessageType.Error:
                return "mcb-avatar-helpbox--error";
            case HelpBoxMessageType.Warning:
                return "mcb-avatar-helpbox--warning";
            case HelpBoxMessageType.Info:
                return "mcb-avatar-helpbox--info";
            default:
                return "mcb-avatar-helpbox--none";
        }
    }

    private static string GetStatusMessageIcon(HelpBoxMessageType messageType)
    {
        switch (messageType)
        {
            case HelpBoxMessageType.Error:
                return "x";
            case HelpBoxMessageType.Warning:
                return "!";
            case HelpBoxMessageType.Info:
                return "i";
            default:
                return string.Empty;
        }
    }

    internal static void LoadUiToolkitStyleSheets(VisualElement root)
    {
        if (root == null)
        {
            return;
        }

        foreach (var styleSheetPath in UiToolkitStyleSheets)
        {
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(styleSheetPath);
            if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
            {
                root.styleSheets.Add(styleSheet);
            }
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

        RequestRefresh(McbViewParts.All);
        new CreatorModeModule(Session, GetSelectedAsset()).OpenWindow();
        Repaint();
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

        if (GUILayout.Button(new GUIContent("About", "MCB's version, license and the third-party software it ships with."), EditorStyles.toolbarButton, GUILayout.Width(70f)))
        {
            MCBAboutWindow.Open();
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

    // --- Project and hierarchy changes. Each inspector (inactive docked tabs too) used to rescan the versions folder and
    // refresh its gallery on every asset refresh, which an avatar build does dozens of times. The shared work now runs once
    // for all of them, after the refreshes settle and never while Unity compiles, imports, builds or enters play mode;
    // inspectors not shown catch up when they are shown again. ---
    private const double ProjectChangeSettleSeconds = 0.75d;
    private static readonly List<MCBEditor> LiveEditors = new List<MCBEditor>();
    private static bool projectChangeHooked, projectChangePending, versionsChangedPending;
    private static double projectChangedAt;

    private static void HookProjectChanges()
    {
        if (projectChangeHooked) return;
        projectChangeHooked = true;
        EditorApplication.projectChanged += ScheduleProjectChange;
    }

    /// <summary>Asset paths an import changed: imported versions are only scanned again when their folder changed.</summary>
    internal static void NoteChangedAssets(params string[][] pathGroups)
    {
        bool versionsChanged = pathGroups.Any(paths => paths != null && paths.Any(IsInVersionsFolder));
        if (!versionsChanged) return;
        // Without an inspector the next one to open scans again (sessions read their lists again when one opens).
        if (LiveEditors.Count == 0) VersionRepository.InvalidateCache();
        else
        {
            versionsChangedPending = true;
            ScheduleProjectChange();
        }
    }

    internal static bool IsInVersionsFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string folder = MCBUtils.ASSET_VERSIONS_FOLDER;
        path = path.Replace('\\', '/').TrimEnd('/');
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ||
               folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void ScheduleProjectChange()
    {
        projectChangedAt = EditorApplication.timeSinceStartup;
        if (projectChangePending) return;
        projectChangePending = true;
        EditorApplication.update += RunSettledProjectChange;
    }

    private static void RunSettledProjectChange()
    {
        if (EditorApplication.timeSinceStartup - projectChangedAt < ProjectChangeSettleSeconds || McbSession.ShouldDeferBackgroundNetworkRefresh() ||
            (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying))
        {
            return;
        }

        EditorApplication.update -= RunSettledProjectChange;
        projectChangePending = false;
        bool versionsChanged = versionsChangedPending;
        versionsChangedPending = false;
        LiveEditors.RemoveAll(editor => editor == null);
        if (LiveEditors.Count == 0)
        {
            if (versionsChanged) VersionRepository.InvalidateCache();
            return;
        }

        SmrPathService.InvalidateCache();
        if (versionsChanged) VersionRepository.Scan(true);
        // Each avatar's session once, however many Inspectors show it.
        foreach (var session in McbSession.Live)
        {
            session.InvalidateDetectedAvatarFbxCache();
            if (versionsChanged) session.LoadImportedVersions();
        }
        foreach (var editor in LiveEditors.ToArray())
        {
            editor.projectRefreshPending = true;
        }
        RefreshPendingEditors();
    }

    // Shown inspectors refresh now; busy ones once their download, apply or build ends (the gallery must not reset its
    // selection meanwhile); hidden ones when they are shown again.
    private static void RefreshPendingEditors()
    {
        EditorApplication.update -= RefreshPendingEditors;
        bool waiting = false;
        foreach (var editor in LiveEditors.ToArray())
        {
            if (editor == null || !editor.projectRefreshPending || !editor.IsShown) continue;
            if (editor.isDownloading || editor.isApplying || editor.isSubmitting || editor.isDeleting || McbSession.ShouldDeferBackgroundNetworkRefresh()) waiting = true;
            else editor.RefreshAfterProjectChange();
        }
        if (waiting) EditorApplication.update += RefreshPendingEditors;
    }

    // An inspector's UI is in a panel while it is shown (not in an inactive docked tab).
    private bool IsShown => uiToolkitRoot?.panel != null;

    private void RefreshAfterProjectChange()
    {
        projectRefreshPending = false;
        assetGalleryModule?.OnProjectChanged();
        dependencyInstallerModule?.RefreshUIToolkit();
        ApplyDependencyBlockerState();
        Repaint();
    }

    // The legacy full-IMGUI inspector path (OnInspectorGUI) was removed: Unity never
    // called it because CreateInspectorGUI() above unconditionally returns the
    // UI Toolkit root. The UI Toolkit layout with its IMGUIContainer sections is the
    // only render path.

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
            RequestRefresh(McbViewParts.All);
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
            RequestRefresh(McbViewParts.All);
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

    // The session's operations, as this view's modules call them.
    internal bool CanStartBackgroundRefreshes => Session.CanStartBackgroundRefreshes;
    public void LoadCachedVersionsForCurrentSelection() => Session.LoadCachedVersionsForCurrentSelection();
    internal void RedetectBaseFbx() => Session.RedetectBaseFbx();
    internal string GetCurrentFBXPath() => Session.GetCurrentFBXPath();
    public void SyncAuthentication() => Session.SyncAuthentication();
    public void CheckAuthentication() => Session.CheckAuthentication();
    public void LoadUnsubmittedVersions(bool forceRefresh = false) => Session.LoadUnsubmittedVersions(forceRefresh);
    public void LoadImportedVersions(bool forceRefresh = false) => Session.LoadImportedVersions(forceRefresh);
    public List<CustomBaseVersion> GetAllVersions() => Session.GetAllVersions();
    public Version ParseVersion(string v) => McbSession.VersionNumber(v);
    public int CompareVersions(string v1, string v2) => Session.CompareVersions(v1, v2);
    public AvatarDiscoveredAsset GetSelectedAsset() => Session.SelectedAsset;
    public bool SyncBaseFbxFilesFromSelectedAsset() => Session.SyncBaseFbxFilesFromSelectedAsset();
    public string GetSelectedAssetDisplayName() => Session.GetSelectedAssetDisplayName();
    public List<string> GetDetectedAvatarFbxPaths() => Session.GetDetectedAvatarFbxPaths();
    internal void InvalidateDetectedAvatarFbxCache() => Session.InvalidateDetectedAvatarFbxCache();

    public void RefreshAccountAndVersions()
    {
        Session.RefreshAccountAndVersions();
        if (Session.SelectedAsset == null)
        {
            assetGalleryModule?.RefreshIfNeeded(force: true);
        }
    }

    public void ReloadVersionsAndBanners()
    {
        AvatarAssetDiscoveryService.ReloadImages();
        RefreshAccountAndVersions();
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

    private void PlayPendingComponentAddedMeshEffect()
    {
        if (customBaseTarget == null || customBaseTarget.pendingSceneMeshDotEffectLoops <= 0)
        {
            return;
        }

        int loops = Mathf.Clamp(customBaseTarget.pendingSceneMeshDotEffectLoops, 1, 8);
        customBaseTarget.pendingSceneMeshDotEffectLoops = 0;
        EditorUtility.SetDirty(customBaseTarget);
        SceneMeshDotEffectService.PlayAvatarSweep(customBaseTarget.transform != null ? AvatarPaths.Root(customBaseTarget) : null, loops);
    }

}
/// <summary>Reports which assets an import changed to the MCB inspectors (only the versions folder matters to them).</summary>
internal sealed class MCBProjectChangePostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths) =>
        MCBEditor.NoteChangedAssets(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
}
#endif
