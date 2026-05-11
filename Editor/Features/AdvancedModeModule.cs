#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Threading.Tasks;

public class AdvancedModeModule
{
    private readonly MCBEditor editor;
    private bool advancedModeFoldout = true; // Opened by default when advanced mode is on
    private bool addArmatureToggle = false;
    private GameObject debugPrefabInstance;
    private DynamicNormalsService dynamicNormalsService;
    private readonly BlendShapeLinkTestDrawer blendShapeLinkTestDrawer;
    private bool dynamicNormalsFoldout = false; // Folded by default

    // FBX Hash checker state
    private Object fbxAsset;
    private string fbxHash;
    private string fbxHashError;
    private bool fbxHashBusy = false;
    private bool connectionMockEnabledDraft;
    private string connectionMockJsonDraft;
    private string connectionMockError;
    private bool connectionMockBusy;
    
    public AdvancedModeModule(MCBEditor editor)
    {
        this.editor = editor;
        dynamicNormalsService = new DynamicNormalsService(editor);
        blendShapeLinkTestDrawer = new BlendShapeLinkTestDrawer(editor);
        connectionMockEnabledDraft = MCBPackageVersionService.IsMockEnabled;
        connectionMockJsonDraft = MCBPackageVersionService.MockResponseJson;
        // Ensure issue reporter subscribes according to current settings
        EditorIssueReporter.RefreshListener();
    }

    public void Draw()
    {
        EditorGUILayout.Space();

        if (GUILayout.Button("Advanced Options", GUILayout.Width(140f)))
        {
            MCBAdvancedModeWindow.Open(editor, this);
        }
    }

    public void DrawWindowContents()
    {
        EditorGUILayout.Space();

        advancedModeFoldout = EditorGUILayout.Foldout(advancedModeFoldout, "Advanced Settings", true, EditorStyles.foldoutHeader);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        if (advancedModeFoldout)
        {
            EditorGUI.indentLevel++;
                
                // Dev Environment checkbox + Magic Sync per environment
                bool currentDevEnvironment = MCBUtils.isDevEnvironment;
                bool newDevEnvironment = EditorGUILayout.Toggle("Dev Environment", currentDevEnvironment);
                
                if (newDevEnvironment != currentDevEnvironment)
                {
                    MCBUtils.isDevEnvironment = newDevEnvironment;
                    editor.CheckAuthentication(); // Refresh auth token for the selected environment
                }

                bool currentDevModeWarning = MCBEditor.IsDevModeWarningEnabled;
                bool newDevModeWarning = EditorGUILayout.Toggle("enable dev mode warning", currentDevModeWarning);
                if (newDevModeWarning != currentDevModeWarning)
                {
                    MCBEditor.IsDevModeWarningEnabled = newDevModeWarning;
                    editor.Repaint();
                }

                // Magic Sync buttons for each environment
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Magic Sync Prod", GUILayout.Width(140)))
                {
                    AuthenticationService.RegisterAuthForEnv(false).ContinueWith(task =>
                    {
                        UnityEditor.EditorApplication.delayCall += () =>
                        {
                            if (!MCBUtils.isDevEnvironment)
                            {
                                editor.CheckAuthentication();
                            }
                        };
                    });
                }

                if (GUILayout.Button("Magic Sync Dev", GUILayout.Width(140)))
                {
                    AuthenticationService.RegisterAuthForEnv(true).ContinueWith(task =>
                    {
                        UnityEditor.EditorApplication.delayCall += () =>
                        {
                            if (MCBUtils.isDevEnvironment)
                            {
                                editor.CheckAuthentication();
                            }
                        };
                    });
                }
                EditorGUILayout.EndHorizontal();

                // Refresh account state button
                EditorGUILayout.BeginHorizontal();  
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Refresh account state", GUILayout.Width(180)))
                {
                    editor.RefreshAccountAndVersions();
                }
                EditorGUILayout.EndHorizontal();

                BlenderSyncService.DrawAdvancedBlenderConnectorSettings();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Connectivity Simulation", EditorStyles.boldLabel);
                ApiSimulationMode currentSimulationMode = MCBUtils.apiSimulationMode;
                ApiSimulationMode newSimulationMode = (ApiSimulationMode)EditorGUILayout.EnumPopup(
                    new GUIContent("API simulation", "Overrides package API requests for quick failure testing."),
                    currentSimulationMode);
                if (newSimulationMode != currentSimulationMode)
                {
                    MCBUtils.apiSimulationMode = newSimulationMode;
                    MCBConnectivityMonitor.Retry(editor.authToken);
                    editor.RefreshAccountAndVersions();
                }

                EditorGUILayout.HelpBox(
                    "Off uses the real API.\n" +
                    "Transport Failure points requests to https://127.0.0.1:1.\n" +
                    "SSL Failure points requests to https://wrong.host.badssl.com.\n" +
                    "Turn this back to Off before using Magic Sync normally.",
                    MessageType.None);

                DrawConnectionMockControls();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Open connectivity tests", GUILayout.Width(180)))
                {
                    ConnectivityTestsWindow.ShowWindow();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Retry connectivity check", GUILayout.Width(180)))
                {
                    MCBConnectivityMonitor.Retry(editor.authToken);
                }
                EditorGUILayout.EndHorizontal();


                // Flush user cache button
                EditorGUILayout.BeginHorizontal();  
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button("Flush user cache", GUILayout.Width(180)))
                {
                    UserService.FlushUserCache();
                    MCBLogger.Log("[MCB] User cache flush requested from Advanced Settings");
                }
                EditorGUILayout.EndHorizontal();

                // Logging checkbox (false by default via EditorPrefs)
                bool currentLogging = MCBLogger.IsEnabled();
                bool newLogging = EditorGUILayout.Toggle("Log in Console", currentLogging);
                if (newLogging != currentLogging)
                {
                    MCBLogger.SetEnabled(newLogging);
                }

                // Share issue logs (true by default) + minimum severity slider
                bool currentShare = EditorIssueReporter.ShareIssueLogs;
                bool newShare = EditorGUILayout.Toggle(new GUIContent("Share issue logs", "Send warnings/errors/exceptions to the MCB server to help us improve."), currentShare);
                if (newShare != currentShare)
                {
                    EditorIssueReporter.ShareIssueLogs = newShare;
                    EditorIssueReporter.RefreshListener();
                }

                int currentMin = EditorIssueReporter.MinSeverityLevel; // 1..4
                int newMin = EditorGUILayout.IntSlider(new GUIContent("Min error severity", "1=Info, 2=Warning, 3=Error, 4=Exception"), currentMin, 1, 4);
                // Severity chips visualization
                DrawSeverityChips(newMin);
                
                // Test button for info-level logging
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button(new GUIContent("Test INFO log", "Emits an INFO level log to test the logging & reporting pipeline"), GUILayout.Width(120)))
                {
                    MCBLogger.Log("[MCB] Test INFO log from AdvancedModeModule");
                }
                EditorGUILayout.EndHorizontal();

                if (newMin != currentMin)
                {
                    EditorIssueReporter.MinSeverityLevel = newMin;
                }
                
                // Blenshape processing section
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Blenshape processing", EditorStyles.boldLabel);

                if (editor.customBaseTarget != null) {
                    EditorGUI.BeginChangeCheck();
                    bool preserveBlendshapeValues = EditorGUILayout.Toggle(
                        new GUIContent(
                            "Preserve Blendshapes On Version Switch",
                            "Saves current blendshape weights by name before switching version, then restores matching names afterward. New blendshapes use version defaults when available."),
                        editor.customBaseTarget.preserveBlendshapeValuesOnVersionSwitch);
                    if (EditorGUI.EndChangeCheck())
                    {
                        editor.customBaseTarget.preserveBlendshapeValuesOnVersionSwitch = preserveBlendshapeValues;
                        editor.customBaseTarget.preserveBlendshapeValuesOnVersionSwitchInitialized = true;
                        EditorUtility.SetDirty(editor.customBaseTarget);
                    }

                    EditorGUILayout.Space();
                }
                
                // Dynamic Normals section
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Dynamic Normals", EditorStyles.boldLabel);
                
                if (editor.customBaseTarget != null)
                {
                    EditorGUI.BeginChangeCheck();
                    bool useDynamicNormals = EditorGUILayout.Toggle(new GUIContent("Enable Dynamic Normals", "Applies dynamic normals to blendshapes containing 'muscle' or 'flex' on the Body mesh"), editor.customBaseTarget.useDynamicNormals);
                    if (EditorGUI.EndChangeCheck())
                    {
                        editor.customBaseTarget.useDynamicNormals = useDynamicNormals;
                        EditorUtility.SetDirty(editor.customBaseTarget);
                        
                        if (useDynamicNormals)
                        {
                            dynamicNormalsService.Apply();
                        }
                        else
                        {
                            dynamicNormalsService.Remove();
                        }
                    }

                    if (editor.customBaseTarget.useDynamicNormals)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUI.BeginChangeCheck();
                        bool useAPose = EditorGUILayout.Toggle(new GUIContent("Use Leg Separation", "Separates the legs during calculation to prevent clipping artifacts on the inner thighs. Uses pure translation to avoid deforming the knees."), editor.customBaseTarget.useAPoseForDynamicNormals);
                        if (EditorGUI.EndChangeCheck())
                        {
                            editor.customBaseTarget.useAPoseForDynamicNormals = useAPose;
                            EditorUtility.SetDirty(editor.customBaseTarget);
                            
                            // Re-apply to take the new pose into account
                            dynamicNormalsService.Remove();
                            dynamicNormalsService.Apply();
                        }
                        EditorGUI.indentLevel--;
                    }
                    
                    // Flush cache button
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUI.indentLevel * 15);
                    if (GUILayout.Button(new GUIContent("Flush Dynamic Normals Cache", "Clear the cached original mesh references"), GUILayout.Width(220f)))
                    {
                        dynamicNormalsService.FlushOriginalMeshes(); 
                    }
                    EditorGUILayout.EndHorizontal();
                    
                    // Foldout to show active blendshapes
                    var activeBlendshapes = dynamicNormalsService.GetActiveBlendshapes();
                    if (activeBlendshapes.Count > 0)
                    {
                        dynamicNormalsFoldout = EditorGUILayout.Foldout(dynamicNormalsFoldout, $"Active Blendshapes ({activeBlendshapes.Count})", true);
                        if (dynamicNormalsFoldout)
                        {
                            EditorGUI.indentLevel++;
                            foreach (var blendshape in activeBlendshapes)
                            {
                                EditorGUILayout.LabelField("• " + blendshape);
                            }
                            EditorGUI.indentLevel--;
                        }
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("No My Custom Base target found.", MessageType.Info);
                }

                // Version cache controls
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Version Cache", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button(new GUIContent("Flush Version Caches", "Clear cached version metadata and hash entries"), GUILayout.Width(220f)))
                {
                    var versionService = AsyncVersionService.Instance;
                    versionService.ClearAllCache();
                    editor.serverVersions.Clear();
                    editor.recommendedVersion = null;
                    editor.selectedVersionForAction = null;
                    editor.fetchAttempted = false;
                    editor.versionModule?.actions?.UpdateAppliedVersionAndState();
                    editor.Repaint();
                    MCBLogger.Log("[AdvancedMode] Cleared version caches from Advanced Mode module.");
                }
                EditorGUILayout.EndHorizontal();

                
                var cacheStats = AsyncVersionService.Instance.GetCacheStats();
                string cachedRecommended = editor.recommendedVersion != null ? editor.recommendedVersion.version : "None";
                string cachedFbxHash = string.IsNullOrEmpty(editor.currentBaseFbxHash) ? "Unavailable" : editor.currentBaseFbxHash;
                EditorGUILayout.HelpBox(
                    $"Cached versions: {cacheStats.versionEntries} (hash entries: {cacheStats.hashEntries})\nRecommended version: {cachedRecommended}\nCurrent base FBX hash: {cachedFbxHash}",
                    MessageType.None);

                // Recalculate current FBX hash button (as requested)
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                if (GUILayout.Button(new GUIContent("Recalculate current FBX hash", "Force recalculation of the currently applied and original FBX hashes, and refresh applied-version state"), GUILayout.Width(260f)))
                {
                    editor.versionModule?.actions?.StartRecalculateCurrentFbxHash();
                }
                EditorGUILayout.EndHorizontal();

                // FBX Hash Checker
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("FBX Hash Checker", EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();
                fbxAsset = EditorGUILayout.ObjectField(new GUIContent("FBX Asset", "Drop a .fbx asset here"), fbxAsset, typeof(Object), false);
                if (EditorGUI.EndChangeCheck())
                {
                    fbxHash = null;
                    fbxHashError = null;
                }

                using (new EditorGUI.DisabledScope(fbxAsset == null || fbxHashBusy))
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(EditorGUI.indentLevel * 15);
                    if (GUILayout.Button("Check Hash", GUILayout.Width(120)))
                    {
                        fbxHashBusy = true;
                        fbxHash = null;
                        fbxHashError = null;

                        try
                        {
                            string assetPath = AssetDatabase.GetAssetPath(fbxAsset);
                            if (string.IsNullOrEmpty(assetPath))
                            {
                                fbxHashError = "Could not resolve asset path.";
                            }
                            else if (!assetPath.ToLowerInvariant().EndsWith(".fbx"))
                            {
                                fbxHashError = "Selected asset is not a .fbx file.";
                            }
                            else
                            {
                                string absolutePath = GetAbsolutePath(assetPath);
                                if (!File.Exists(absolutePath))
                                {
                                    fbxHashError = "File does not exist on disk: " + absolutePath;
                                }
                                else
                                {
                                    fbxHash = MCBUtils.CalculateFileHash(absolutePath);
                                    if (string.IsNullOrEmpty(fbxHash))
                                    {
                                        fbxHashError = "Failed to compute hash.";
                                    }
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            fbxHashError = "Error: " + ex.Message;
                        }
                        finally
                        {
                            fbxHashBusy = false;
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }

                if (fbxHashBusy)
                {
                    EditorGUILayout.HelpBox("Calculating hash...", MessageType.Info);
                }
                else if (!string.IsNullOrEmpty(fbxHashError))
                {
                    EditorGUILayout.HelpBox(fbxHashError, MessageType.Error);
                }
                else if (!string.IsNullOrEmpty(fbxHash))
                {
                    EditorGUILayout.LabelField("SHA-256:");
                    EditorGUILayout.SelectableLabel(fbxHash, EditorStyles.textField, GUILayout.Height(18));
                }
                
                // Experimental features
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Experimental features", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                if (!FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR) &&
                    FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK))
                {
                    FeatureFlags.SetEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK, false);
                }
                foreach (var (key, label, description) in FeatureFlags.All())
                {
                    bool isBlenderAdvancedMesh = key == FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK;
                    bool dependencyEnabled = !isBlenderAdvancedMesh ||
                                             FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR);

                    EditorGUI.BeginChangeCheck();
                    bool current = FeatureFlags.IsEnabled(key);
                    bool next;
                    using (new EditorGUI.DisabledScope(!dependencyEnabled))
                    {
                        next = EditorGUILayout.ToggleLeft(new GUIContent(label, description), dependencyEnabled && current);
                    }
                    if (EditorGUI.EndChangeCheck())
                    {
                        FeatureFlags.SetEnabled(key, next);
                        // If user disables custom unknown version support, clear selection and warning
                        if (key == FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION && !next)
                        {
                            editor.selectedCustomVersionForAction = null;
                            editor.customWarningShown = false;
                            editor.currentIsCustom = false; // hide current custom UI marks
                            editor.Repaint();
                        }

                        if (key == FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR && !next)
                        {
                            FeatureFlags.SetEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK, false);
                        }
                    }
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.Space();
                blendShapeLinkTestDrawer.Draw();

                
                // Debug Tools section
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Debug Tools", EditorStyles.boldLabel);
                
                bool previousArmatureToggle = addArmatureToggle;
                addArmatureToggle = EditorGUILayout.Toggle("Add armature toggle", addArmatureToggle);
                
                if (addArmatureToggle != previousArmatureToggle)
                {
                    OnArmatureToggleChanged();
                }
                
                EditorGUI.BeginDisabledGroup(!addArmatureToggle);
                if (GUILayout.Button("Create armature now"))
                {
                    CreateArmatureNow();
                }
                EditorGUI.EndDisabledGroup();

                if (editor.isAuthenticated)
                {
                    EditorGUILayout.Space();
                    // File Configuration section
                    if (editor.versionModule?.fileConfigDrawer != null)
                    {
                        editor.versionModule.fileConfigDrawer.Draw();
                    }
                }
                
                EditorGUI.indentLevel--;
            }

        EditorGUILayout.EndVertical();
    }

    private static string GetAbsolutePath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        // Handle Assets/ paths
        if (assetPath.StartsWith("Assets/"))
        {
            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "/Assets".Length);
            return Path.Combine(projectRoot, assetPath).Replace("/", "\\");
        }
        // Handle Packages/ paths (Unity caches packages under project/Library/PackageCache)
        if (assetPath.StartsWith("Packages/"))
        {
            // Try to resolve via AssetDatabase first (returns absolute path for some providers)
            string maybeAbsolute = AssetDatabase.GetAssetPath(AssetDatabase.LoadMainAssetAtPath(assetPath));
            if (!string.IsNullOrEmpty(maybeAbsolute) && (maybeAbsolute.Contains(":\\") || maybeAbsolute.StartsWith("/")))
            {
                return maybeAbsolute;
            }
            // Fallback: construct path under Library/PackageCache
            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "/Assets".Length);
            string candidate = Path.Combine(projectRoot, assetPath).Replace("/", "\\");
            return candidate;
        }
        return assetPath;
    }
    
    private void OnArmatureToggleChanged()
    {
        if (addArmatureToggle)
        {
            // Place the debug prefab in the root of the avatar
            PlaceDebugPrefab();
            // Immediately generate the mesh (disabled) to prevent stripping
            GenerateArmatureMesh(false);
        }
        else
        {
            // Remove the debug prefab
            RemoveDebugPrefab();
        }
    }
    
    private void PlaceDebugPrefab()
    {
        if (editor.customBaseTarget == null) return;
        
        // Remove existing debug prefab if it exists
        RemoveDebugPrefab();
        
        string prefabPath = "Packages/orbiters.mcb/debug/debug.prefab";
        GameObject debugPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        
        if (debugPrefab == null)
        {
            MCBLogger.LogError($"Could not find debug prefab at {prefabPath}");
            return;
        }
        
        debugPrefabInstance = (GameObject)PrefabUtility.InstantiatePrefab(debugPrefab, editor.customBaseTarget.transform);
        debugPrefabInstance.name = "debug";
        
        MCBLogger.Log("Debug prefab placed in avatar root");
    }
    
    private void RemoveDebugPrefab()
    {
        if (debugPrefabInstance != null)
        {
            Object.DestroyImmediate(debugPrefabInstance);
            debugPrefabInstance = null;
            MCBLogger.Log("Debug prefab removed from avatar root");
        }
        else
        {
            // Try to find existing debug object by name
            if (editor.customBaseTarget != null)
            {
                Transform existingDebug = editor.customBaseTarget.transform.Find("debug");
                if (existingDebug != null)
                {
                    Object.DestroyImmediate(existingDebug.gameObject);
                    MCBLogger.Log("Existing debug object removed from avatar root");
                }
            }
        }
    }
    
    private void CreateArmatureNow()
    {
        if (editor.customBaseTarget == null || debugPrefabInstance == null) return;
        
        GenerateArmatureMesh(true); // Enable the mesh
    }
    
    public void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // Ensure reporter is set according to latest preference whenever play mode changes
        EditorIssueReporter.RefreshListener();
        blendShapeLinkTestDrawer?.OnPlayModeStateChanged(state);

        if (state == PlayModeStateChange.ExitingEditMode && addArmatureToggle)
        {
            // Generate the mesh right before leaving edit mode (before VRCFury processing)
            // This ensures the debug prefab has content and won't be stripped
            GenerateArmatureMesh(false); // Keep it disabled for play mode
        }
        else if (state == PlayModeStateChange.EnteredPlayMode && addArmatureToggle)
        {
            // The mesh should already exist, but we can ensure it's still disabled
            EnsureMeshState(false);
        }
    }
    
    private void GenerateArmatureMesh(bool enableMesh)
    {
        if (debugPrefabInstance == null) return;
        
        // Remove existing ArmatureDebugMesh if it exists
        Transform existingMesh = debugPrefabInstance.transform.Find("ArmatureDebugMesh");
        if (existingMesh != null)
        {
            Object.DestroyImmediate(existingMesh.gameObject);
        }
        
        // Generate new mesh using ArmatureMeshGenerator
        ArmatureMeshGenerator.Generate(editor.customBaseTarget.gameObject, ArmatureMeshGenerator.MeshType.Pyramid);
        
        // Find the generated mesh and move it to the debug prefab
        Transform generatedMesh = editor.customBaseTarget.transform.Find("ArmatureDebugMesh");
        if (generatedMesh != null)
        {
            generatedMesh.SetParent(debugPrefabInstance.transform, true);
            
            // Apply the debug material
            string materialPath = "Packages/orbiters.mcb/debug/ArmatureDebugMaterial.mat";
            Material debugMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            
            if (debugMaterial != null)
            {
                SkinnedMeshRenderer smr = generatedMesh.GetComponent<SkinnedMeshRenderer>();
                if (smr != null)
                {
                    smr.sharedMaterial = debugMaterial;
                }
            }
            else
            {
                MCBLogger.LogWarning($"Could not find ArmatureDebugMaterial at {materialPath}");
            }
            
            // Set the enabled state
            generatedMesh.gameObject.SetActive(enableMesh);
            
            MCBLogger.Log($"ArmatureDebugMesh generated and placed in debug prefab (enabled: {enableMesh})");
        }
        else
        {
            MCBLogger.LogError("Failed to generate ArmatureDebugMesh");
        }
    }
    
    private void EnsureMeshState(bool enableMesh)
    {
        if (debugPrefabInstance == null) return;
        
        Transform existingMesh = debugPrefabInstance.transform.Find("ArmatureDebugMesh");
        if (existingMesh != null)
        {
            existingMesh.gameObject.SetActive(enableMesh);
        }
    }

    // Renders four severity chips using shared EditorUIUtils style; chips below the selected threshold are grayed out.
    private void DrawSeverityChips(int minLevel)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(EditorGUI.indentLevel * 15);
        DrawSeverityChip("INFO", new Color(0.20f, 0.60f, 1.00f), 1, minLevel);
        GUILayout.Space(6);
        DrawSeverityChip("WARN", EditorUIUtils.OrangeColor, 2, minLevel);
        GUILayout.Space(6);
        DrawSeverityChip("ERROR", new Color(1.00f, 0.20f, 0.20f), 3, minLevel);
        GUILayout.Space(6);
        DrawSeverityChip("FATAL", Color.black, 4, minLevel);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSeverityChip(string label, Color activeTextColor, int level, int minLevel)
    {
        bool isActive = level >= minLevel;
        // Reuse chip style from VersionListDrawer: dark background with border, colored text
        Color bg = new Color(0.28f, 0.28f, 0.28f);
        Color border = new Color(0.46f, 0.46f, 0.46f);
        Color txt = isActive ? activeTextColor : new Color(0.65f, 0.65f, 0.65f);
        // Slightly dim inactive chips by also lightening the background
        Color bgInactive = new Color(0.32f, 0.32f, 0.32f);

        // Draw using shared util (rounded chip with border)
        EditorUIUtils.DrawChipLabel(label, isActive ? bg : bgInactive, txt, border, width: 70, height: 20, cornerRadius: 8f, borderWidth: 1.0f);
    }

    private void DrawConnectionMockControls()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Check Connection Mock", EditorStyles.boldLabel);

        connectionMockEnabledDraft = EditorGUILayout.Toggle(
            new GUIContent("Enable mock", "When enabled, MCB package version checks use the JSON below instead of the real /unity-wizard/check-connection response."),
            connectionMockEnabledDraft);

        if (string.IsNullOrWhiteSpace(connectionMockJsonDraft))
        {
            connectionMockJsonDraft = MCBPackageVersionService.MockResponseJson;
        }

        EditorGUILayout.LabelField("Mock JSON");
        connectionMockJsonDraft = EditorGUILayout.TextArea(connectionMockJsonDraft ?? string.Empty, GUILayout.MinHeight(110f));

        if (!string.IsNullOrEmpty(connectionMockError))
        {
            EditorGUILayout.HelpBox(connectionMockError, MessageType.Error);
        }

        using (new EditorGUI.DisabledScope(connectionMockBusy))
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15);

            if (GUILayout.Button("Apply", GUILayout.Width(100f)))
            {
                ApplyConnectionMock();
            }

            if (GUILayout.Button(connectionMockBusy ? "Fetching..." : "Fetch", GUILayout.Width(100f)))
            {
                _ = FetchRealConnectionMockAsync();
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.HelpBox(
            "Disabled by default. The text area is initialized from the first real /unity-wizard/check-connection response MCB receives. " +
            "Apply saves the current draft and refreshes the package-version status. Fetch pulls the real server response and replaces the draft JSON.",
            MessageType.None);
    }

    private void ApplyConnectionMock()
    {
        connectionMockError = null;
        if (!MCBPackageVersionService.TryApplyMock(connectionMockJsonDraft, connectionMockEnabledDraft, out var error))
        {
            connectionMockError = error;
            return;
        }

        editor.CheckAuthentication();
        editor.Repaint();
    }

    private async Task FetchRealConnectionMockAsync()
    {
        if (connectionMockBusy)
        {
            return;
        }

        connectionMockBusy = true;
        connectionMockError = null;
        editor.Repaint();

        try
        {
            var result = await MCBPackageVersionService.FetchRealResponseJsonAsync(editor.authToken);
            EditorApplication.delayCall += () =>
            {
                connectionMockBusy = false;
                if (!result.success)
                {
                    connectionMockError = "Fetch failed: " + result.error;
                }
                else
                {
                    connectionMockJsonDraft = result.json;
                    connectionMockError = null;
                    if (!connectionMockEnabledDraft)
                    {
                        editor.CheckAuthentication();
                    }
                }
                editor.Repaint();
            };
        }
        catch (System.Exception ex)
        {
            EditorApplication.delayCall += () =>
            {
                connectionMockBusy = false;
                connectionMockError = "Fetch failed: " + ex.Message;
                editor.Repaint();
            };
        }
    }
}
#endif
