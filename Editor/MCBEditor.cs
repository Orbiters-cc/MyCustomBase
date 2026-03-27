#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;
using Newtonsoft.Json;

[CustomEditor(typeof(MyCustomBase))]
public class MCBEditor : UnityEditor.Editor
{
    // --- Target & Serialized Object ---
    public MyCustomBase customBaseTarget;
    public new SerializedObject serializedObject;
    private Texture2D bannerTexture;

    // --- Serialized Properties ---
    public SerializedProperty specifyCustomBaseFbxProp, baseFbxFilesProp, blendShapeValuesProp, isCreatorModeProp,
                              customFbxForCreatorProp, customBaseAvatarForCreatorProp, avatarLogicPrefabProp, customBlendshapesForCreatorProp,
                              includeCustomVeinsForCreatorProp, customVeinsNormalMapProp,
                              includeDynamicNormalsBodyForCreatorProp, includeDynamicNormalsFlexingForCreatorProp;

    // --- Services and Modules ---
    private NetworkService networkService;
    private AuthenticationModule authModule;
    public VersionManagementModule versionModule;
    public CreatorModeModule creatorModule;
    private AdvancedModeModule advancedModule;
    private AccountModule accountModule;
    private AvatarOptionsModule avatarOptionsModule;
    private AdjustMaterialModule adjustMaterialModule;
    public WarningsModule warningsModule;
    
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

    // Runtime state for detection
    public string currentAppliedFbxHash;
    public bool customWarningShown;
    
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
        advancedModule = new AdvancedModeModule(this);
        accountModule = new AccountModule(this, networkService);
        accountModule.Initialize();
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
        
        // Ensure modules are enabled
        versionModule.OnEnable();
        
        // Start async initialization
        StartAsyncInitialization();
        
        // Subscribe to play mode state changes
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.projectChanged += OnProjectChanged;
    }
    
    private void OnDisable()
    {
        // Unsubscribe from play mode state changes
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        
        // Unsubscribe from version service events
        if (versionService != null)
        {
            versionService.OnVersionsUpdated -= OnVersionsUpdated;
            versionService.OnVersionFetchError -= OnVersionFetchError;
        }

        MCBConnectivityMonitor.StatusChanged -= RepaintFromConnectivityMonitor;
        EditorApplication.projectChanged -= OnProjectChanged;
    }
    
    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        advancedModule?.OnPlayModeStateChanged(state);
        avatarOptionsModule?.OnPlayModeStateChanged(state);
    }

    private void OnProjectChanged()
    {
        LoadImportedVersions();
        Repaint();
    }

    private async void StartAsyncInitialization()
    {
        // Skip async initialization if already in progress or if we're submitting/building
        if (isFetching || isSubmitting) return;

        // Auto-detect FBX immediately (synchronously) to avoid missing detection on first draw
        if (!specifyCustomBaseFbxProp.boolValue)
        {
            DetectAndLoadCached();
            // Also schedule another attempt on next editor tick to catch late-loaded assets
            EditorApplication.delayCall += DetectAndLoadCached;
        }
        else
        {
            TryLoadCachedVersionsAndRefetch();
        }
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

        var cached = versionService.GetCachedVersions(fbxPath, authToken);
        if (cached.versions.Count > 0)
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
        if (isAuthenticated)
        {
            fetchAttempted = true;
            versionService.StartVersionFetchInBackground(fbxPath, authToken, useCache: false);
        }
    }

    private void DetectAndLoadCached()
    {
        AutoDetectBaseFbxViaHierarchy();
        // Immediately update hash state so UI knows an FBX is present
        if (versionModule != null && versionModule.actions != null)
        {
            versionModule.actions.UpdateCurrentBaseFbxHash();
        }
        TryLoadCachedVersionsAndRefetch();
    }

    private void OnVersionFetchError(string error)
    {
        fetchError = error;
        Repaint();
        MCBLogger.LogError($"[MCBEditor] Version fetch error: {error}");
    }

    private void AutoDetectBaseFbxViaHierarchy()
    {
        if (customBaseTarget == null) return;
        var root = customBaseTarget.transform.root;
        var bodySmr = MeshFinder.FindMeshPrioritizingRoot(root, "Body");
        
        if (bodySmr?.sharedMesh == null) return;
        
        string meshPath = AssetDatabase.GetAssetPath(bodySmr.sharedMesh);
        if (string.IsNullOrEmpty(meshPath)) return;
        
        var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
        if (fbxAsset != null && AssetImporter.GetAtPath(meshPath) is ModelImporter)
        {
            baseFbxFilesProp.ClearArray();
            baseFbxFilesProp.InsertArrayElementAtIndex(0);
            baseFbxFilesProp.GetArrayElementAtIndex(0).objectReferenceValue = fbxAsset;
            serializedObject.ApplyModifiedProperties();
            
            MCBLogger.Log($"[MCBEditor] Auto-detected FBX: {System.IO.Path.GetFileName(meshPath)}");
            Repaint();

            // After detection, immediately try to load cached versions and start a background refresh
            TryLoadCachedVersionsAndRefetch();
        }
    }

    private string GetCurrentFBXPath()
    {
        if (baseFbxFilesProp.arraySize > 0)
        {
            var fbx = baseFbxFilesProp.GetArrayElementAtIndex(0).objectReferenceValue as GameObject;
            if (fbx != null) return AssetDatabase.GetAssetPath(fbx);
        }
        
        // Fallback to default Winterpaw if none assigned (maintain consistency with VersionActions)
        string defaultPath = "Assets/MasculineCanine/FX/MasculineCanine.v1.5.fbx";
        if (File.Exists(defaultPath)) return defaultPath;
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
            
            // Draw progress bars for active tasks at the top
            SafeUiCall(() => ProgressBarManager.Instance.DrawProgressBars());

            if (!isAuthenticated)
            {
                SafeUiCall(() => authModule.DrawMagicSyncAuth());
            }

            bool showOfflineSavedVersionsUi = !isAuthenticated && importedVersions != null && importedVersions.Count > 0;

            if (isAuthenticated)
            {
                SafeUiCall(() => warningsModule?.Draw());
                SafeUiCall(() => creatorModule.Draw());
                SafeUiCall(() => versionModule.Draw());
                SafeUiCall(() => avatarOptionsModule?.Draw());
                SafeUiCall(() => adjustMaterialModule?.Draw());
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
                    isAuthenticated = false;
                    authToken = null;
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
        if (bannerTexture == null) return;
        float aspect = (float)bannerTexture.width / bannerTexture.height;
        float desiredWidth = EditorGUIUtility.currentViewWidth;
        Rect rect = GUILayoutUtility.GetRect(desiredWidth, desiredWidth / aspect);
        GUI.DrawTexture(rect, bannerTexture, ScaleMode.StretchToFill);
        GUILayout.Space(5);
    }

    private void DrawConnectivityDiagnosticsPanel()
    {
        if (string.IsNullOrEmpty(MCBConnectivityMonitor.FailureReport))
        {
            return;
        }

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.HelpBox("The tool cannot connect to the server, copy the data bellow and send it to @blackorbit on discord", MessageType.Error);

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

    private void DrawOfflineSavedVersionsInfo()
    {
        EditorGUILayout.HelpBox("Imported saved versions are available offline. You can apply them or reset to the original Winterpaw without logging in.", MessageType.Info);
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
        accountModule?.Refresh();
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

        string fbxPath = GetCurrentFBXPath();
        if (!string.IsNullOrEmpty(fbxPath) && isAuthenticated)
        {
            versionService?.StartVersionFetchInBackground(fbxPath, authToken, useCache: false);
        }
    }

    private void FindSerializedProperties()
    {
        specifyCustomBaseFbxProp = serializedObject.FindProperty("specifyCustomBaseFbx");
        baseFbxFilesProp = serializedObject.FindProperty("baseFbxFiles");
        blendShapeValuesProp = serializedObject.FindProperty("blendShapeValues");
        isCreatorModeProp = serializedObject.FindProperty("isCreatorMode");
        customFbxForCreatorProp = serializedObject.FindProperty("customFbxForCreator");
        customBaseAvatarForCreatorProp = serializedObject.FindProperty("customBaseAvatarForCreatorProp");
        avatarLogicPrefabProp = serializedObject.FindProperty("avatarLogicPrefab");
        customBlendshapesForCreatorProp = serializedObject.FindProperty("customBlendshapesForCreator");
        includeCustomVeinsForCreatorProp = serializedObject.FindProperty("includeCustomVeinsForCreator");
        customVeinsNormalMapProp = serializedObject.FindProperty("customVeinsNormalMap");
        includeDynamicNormalsBodyForCreatorProp = serializedObject.FindProperty("includeDynamicNormalsBodyForCreator");
        includeDynamicNormalsFlexingForCreatorProp = serializedObject.FindProperty("includeDynamicNormalsFlexingForCreator");
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

        string versionsRoot = Path.Combine(Application.dataPath, "MCB", "versions");
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
                    if (version == null || string.IsNullOrWhiteSpace(version.version) || string.IsNullOrWhiteSpace(version.defaultAviVersion))
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
        if (!isAuthenticated)
        {
            return importedVersions
                .Where(v => v != null)
                .OrderByDescending(v => ParseVersion(v.version))
                .ToList();
        }

        // Merge sources while letting local variants override matching server entries.
        var merged = new Dictionary<string, CustomBaseVersion>(StringComparer.Ordinal);

        foreach (var version in serverVersions)
        {
            if (version == null) continue;
            merged[GetVersionKey(version)] = version;
        }

        foreach (var version in importedVersions)
        {
            if (version == null) continue;
            merged[GetVersionKey(version)] = version;
        }

        foreach (var version in unsubmittedVersions)
        {
            if (version == null) continue;
            merged[GetVersionKey(version)] = version;
        }

        return merged.Values.OrderByDescending(v => ParseVersion(v.version)).ToList();
    }

    private static string GetVersionKey(CustomBaseVersion version)
    {
        return $"{version.version}|{version.defaultAviVersion}";
    }
    
    public Version ParseVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return new Version(0,0);
        if (v.Count(c => c == '.') == 0) v += ".0.0";
        if (v.Count(c => c == '.') == 1) v += ".0";
        return Version.TryParse(v, out var ver) ? ver : new Version(0,0);
    }

    public int CompareVersions(string v1, string v2) => ParseVersion(v1).CompareTo(ParseVersion(v2));

    private void RepaintFromConnectivityMonitor()
    {
        Repaint();
    }
}
#endif
