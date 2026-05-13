#if UNITY_EDITOR
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class CustomVeinsDrawer
{
    private readonly MCBEditor editor;
    private Texture2D customVeinsTexture;
    private Texture2D okIcon;
    private Texture2D koIcon;
    private MaterialService materialService;
    private Transform cachedRoot;

    public const string CUSTOM_VEINS_PREF_KEY = "MCB_CustomVeins_Enabled";

    public CustomVeinsDrawer(MCBEditor editor)
    {
        this.editor = editor;
        LoadTextures();
    }

    private void LoadTextures()
    {
        customVeinsTexture = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/customVeins.png");
        okIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/ok.png");
        koIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/ko.png");
    }

    public void Draw()
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        
        // Only show if extraCustomization contains "customVeins" 
        if (!editor.isCustomBase ||
            !ExtraCustomizationUtils.HasFlag(appliedVersion?.extraCustomization, "customVeins"))
            return;

        if (!EnsureMaterialService())
        {
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Custom Veins", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical();

        // Horizontal layout for image on left, controls on right
        EditorGUILayout.BeginHorizontal();

        // Left side: Image
        if (customVeinsTexture != null)
        {
            GUILayout.BeginVertical(GUILayout.Width(110));
            GUILayout.FlexibleSpace();
            GUILayout.Label(customVeinsTexture, GUILayout.Width(100), GUILayout.Height(100));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
        }

        // Right side: Checkbox, shader info, and texture preview
        GUILayout.BeginVertical();
        
        // Checkbox
        bool currentEnabled = EditorPrefs.GetBool(CUSTOM_VEINS_PREF_KEY, true);
        EditorGUI.BeginChangeCheck();
        bool newEnabled = EditorGUILayout.Toggle("Custom veins", currentEnabled);
        if (EditorGUI.EndChangeCheck())
        {
            bool success = false;
            if (newEnabled)
            {
                success = ApplyCustomVeins();
            }
            else
            {
                success = RemoveCustomVeins();
                // Removing always succeeds, or we want to update the toggle anyway
                success = true;
            }

            // Only update EditorPrefs if the operation succeeded
            if (success)
            {
                EditorPrefs.SetBool(CUSTOM_VEINS_PREF_KEY, newEnabled);
                currentEnabled = newEnabled;
            }
        }

        // Shader detection and compatibility display
        DrawShaderCompatibility();

        var targetMaterials = GetTargetMaterials();
        bool isLocked = targetMaterials.Any(material => materialService.IsMaterialLocked(material));
        DrawPoiyomiInstallSuggestion(targetMaterials);

        // "Applied on detail normal map" label and texture preview
        DrawVeinsTexturePreview();

        bool veinsApplied = targetMaterials.Count > 0 && targetMaterials.All(material => materialService.HasDetailNormalMap(material));
        bool shouldShowWarning = currentEnabled && !veinsApplied;

        if (shouldShowWarning)
        {
            DrawVeinsMissingWarning(isLocked);
        }
        else
        {
            DrawReapplyButton(currentEnabled, isLocked);
        }
        
        GUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
    }

    private void DrawShaderCompatibility()
    {
        var targetMaterials = GetTargetMaterials();
        
        if (targetMaterials.Count == 0)
        {
            EditorGUILayout.HelpBox("Could not detect materials on the targeted FBX meshes", MessageType.Warning);
            return;
        }

        var shaderNames = targetMaterials
            .Select(material => material?.shader?.name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        bool isSupported = shaderNames.Count > 0 && shaderNames.All(materialService.IsShaderSupported);
        Texture2D icon = isSupported ? okIcon : koIcon;

        // Display "Detected Shader:" label
        EditorGUILayout.LabelField("Detected Shader:", EditorStyles.miniLabel);

        // Display shader name with icon in a fixed-height horizontal layout for proper vertical alignment
        EditorGUILayout.BeginHorizontal(GUILayout.Height(20));
        
        if (icon != null)
        {
            GUILayout.Label(icon, GUILayout.Width(20), GUILayout.Height(20));
            GUILayout.Space(2); // spacing between icon and label
        }
        
        string supportText = isSupported ? "Supported" : "Not Supported";
        string shaderLabel = shaderNames.Count == 0
            ? "Unknown shader"
            : string.Join(", ", shaderNames.Take(3)) + (shaderNames.Count > 3 ? $" and {shaderNames.Count - 3} more" : string.Empty);
        // Create a custom style with top padding to vertically center the text with the 20x20 icon
        GUIStyle centeredLabelStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            padding = new RectOffset(0, 0, 4, 0) // Add 4px top padding to align with icon center
        };
        GUILayout.Label($"{shaderLabel}: {supportText}", centeredLabelStyle, GUILayout.ExpandHeight(false));
        
        EditorGUILayout.EndHorizontal();
    }

    private void DrawVeinsTexturePreview()
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (appliedVersion == null) return;

        // Construct the path to the veins normal map using the utility method
        string versionFolder = MCBUtils.GetVersionDataPath(appliedVersion);
        string veinsNormalPath = System.IO.Path.Combine(versionFolder, "veins normal.png").Replace("\\", "/");

        // Load the texture
        Texture2D veinsTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(veinsNormalPath);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        
        // Display "Applied on detail normal map" label
        EditorGUILayout.LabelField("Applied on detail normal map", EditorStyles.miniLabel);

        // Display mini read-only texture field
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.ObjectField(veinsTexture, typeof(Texture2D), false, GUILayout.Height(16));
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawPoiyomiInstallSuggestion(IReadOnlyList<Material> targetMaterials)
    {
        if (!HasLockedPoiyomiMaterial(targetMaterials))
        {
            return;
        }

        var status = VpmDependencyService.Instance.GetOptionalDependencyStatus("com.poiyomi.toon");
        if (status == null || status.IsInstalled)
        {
            return;
        }

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(
            "This material was locked from a Poiyomi shader, but Poiyomi Toon is not installed in the project.",
            EditorStyles.wordWrappedMiniLabel);

        if (!string.IsNullOrWhiteSpace(status.Reason))
        {
            EditorGUILayout.LabelField(status.Reason, EditorStyles.wordWrappedMiniLabel);
        }

        using (new EditorGUI.DisabledScope(VpmDependencyService.Instance.IsInstalling))
        {
            string label = VpmDependencyService.Instance.IsInstalling ? "Installing Poiyomi Toon..." : "Install Poiyomi Toon";
            if (GUILayout.Button(label, GUILayout.Width(180f)))
            {
                var result = VpmDependencyService.Instance.InstallOptionalDependency("com.poiyomi.toon");
                if (!result.Success)
                {
                    EditorUtility.DisplayDialog("Install Poiyomi Toon Failed", result.ErrorMessage, "Ok");
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Poiyomi Toon Installed",
                        "Poiyomi Toon was installed. Unity may reload assemblies before custom veins can continue.",
                        "Ok");
                }
            }
        }

        EditorGUILayout.EndVertical();
    }

    private bool HasLockedPoiyomiMaterial(IReadOnlyList<Material> materials)
    {
        if (materials == null)
        {
            return false;
        }

        foreach (var material in materials)
        {
            if (material == null || !materialService.IsMaterialLocked(material))
            {
                continue;
            }

            string originalShader = material.GetTag("OriginalShader", false, string.Empty);
            if (!string.IsNullOrEmpty(originalShader) &&
                originalShader.IndexOf("poiyomi", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }


    private bool ApplyCustomVeins()
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (appliedVersion == null)
        {
            MCBLogger.LogError("[CustomVeinsDrawer] No applied version found");
            return false;
        }

        var targetRenderers = GetTargetRenderers();
        var targetMaterials = GetTargetMaterials(targetRenderers);
        if (!EnsureUnlocked(targetMaterials))
        {
            return false;
        }

        // Construct the path to the veins normal map using the utility method
        string versionFolder = MCBUtils.GetVersionDataPath(appliedVersion);
        string veinsNormalPath = System.IO.Path.Combine(versionFolder, "veins normal.png").Replace("\\", "/");

        MCBLogger.Log($"[CustomVeinsDrawer] Applying custom veins from: {veinsNormalPath}");

        bool success = false;
        bool anyFailures = false;
        foreach (var renderer in targetRenderers)
        {
            bool rendererSuccess = materialService.SetDetailNormalMap(renderer, veinsNormalPath);
            if (rendererSuccess)
            {
                materialService.SetDetailNormalOpacity(renderer, 1.0f);
                success = true;
            }
            else
            {
                anyFailures = true;
            }
        }

        if (success)
        {
            MCBLogger.Log("[CustomVeinsDrawer] Custom veins applied successfully");
        }
        if (!success || anyFailures)
        {
            MCBLogger.LogError("[CustomVeinsDrawer] Failed to apply custom veins");
        }
        return success;
    }

    private bool RemoveCustomVeins()
    {
        MCBLogger.Log("[CustomVeinsDrawer] Removing custom veins");

        var targetRenderers = GetTargetRenderers();
        var targetMaterials = GetTargetMaterials(targetRenderers);
        if (!EnsureUnlocked(targetMaterials))
        {
            return false;
        }

        bool success = false;
        foreach (var renderer in targetRenderers)
        {
            success |= materialService.RemoveDetailNormalMap(renderer);
        }

        if (success)
        {
            MCBLogger.Log("[CustomVeinsDrawer] Custom veins removed successfully");
        }
        else
        {
            MCBLogger.LogError("[CustomVeinsDrawer] Failed to remove custom veins");
        }
        return success;
    }

    private void DrawReapplyButton(bool currentEnabled, bool isLocked)
    {
        using (new EditorGUI.DisabledScope(!currentEnabled))
        {
            string label = isLocked ? "Unlock and re-apply" : "Re-apply";
            if (GUILayout.Button(label, GUILayout.Width(140)))
            {
                if (EnsureUnlocked(GetTargetMaterials()))
                {
                    ApplyCustomVeins();
                }
            }
        }
    }

    private void DrawVeinsMissingWarning(bool isLocked)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("the custom veins are not applied anymore.", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4f);

        EditorGUILayout.BeginHorizontal();
        string reapplyLabel = isLocked ? "Unlock and re-apply" : "Re-apply";
        if (GUILayout.Button(reapplyLabel, GUILayout.Width(140)))
        {
            if (EnsureUnlocked(GetTargetMaterials()))
            {
                ApplyCustomVeins();
            }
        }

        if (GUILayout.Button("Disable custom veins", GUILayout.Width(160)))
        {
            EditorPrefs.SetBool(CUSTOM_VEINS_PREF_KEY, false);
            RemoveCustomVeins();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private bool EnsureUnlocked(IReadOnlyList<Material> materials)
    {
        if (materials == null || materials.Count == 0)
        {
            return false;
        }

        foreach (var material in materials)
        {
            if (!EnsureUnlocked(material))
            {
                return false;
            }
        }

        return true;
    }

    private bool EnsureUnlocked(Material material)
    {
        if (material == null || !materialService.IsMaterialLocked(material))
        {
            return true;
        }

        if (materialService.UnlockMaterial(material))
        {
            return true;
        }

        EditorUtility.DisplayDialog(
            "Unlock Failed",
            "Could not unlock the material shader. Please unlock it manually from Poiyomi before trying again.",
            "Ok");
        return false;
    }

    private bool EnsureMaterialService()
    {
        Transform root = editor?.customBaseTarget != null && editor.customBaseTarget.transform != null
            ? editor.customBaseTarget.transform.root
            : null;

        if (root == null)
        {
            return false;
        }

        if (materialService == null || cachedRoot != root)
        {
            materialService = new MaterialService(root);
            cachedRoot = root;
        }

        return true;
    }

    private List<SkinnedMeshRenderer> GetTargetRenderers()
    {
        var targetFbxPaths = GetTargetFbxPaths();
        var appliedVersion = editor?.customBaseTarget != null
            ? editor.customBaseTarget.appliedCustomBaseVersion
            : null;

        if (NativeMeshPayloadService.VersionUsesAdvancedMesh(appliedVersion))
        {
            var advancedRenderers = ResolveAdvancedMeshTargetRenderers(appliedVersion, targetFbxPaths);
            if (advancedRenderers.Count > 0)
            {
                return advancedRenderers;
            }
        }

        return materialService
            .GetSkinnedMeshRenderersForFbxPaths(targetFbxPaths)
            .Where(renderer => renderer?.sharedMaterial != null)
            .ToList();
    }

    private List<SkinnedMeshRenderer> ResolveAdvancedMeshTargetRenderers(CustomBaseVersion appliedVersion, List<string> targetFbxPaths)
    {
        if (cachedRoot == null || appliedVersion == null)
        {
            return new List<SkinnedMeshRenderer>();
        }

        var lookupPaths = targetFbxPaths != null && targetFbxPaths.Count > 0
            ? targetFbxPaths
            : GetVersionSourcePaths(appliedVersion);

        var renderers = ResolveAdvancedMeshRenderers(appliedVersion, lookupPaths);
        if (renderers.Count > 0)
        {
            return renderers;
        }

        // Advanced mesh swaps renderer.sharedMesh away from the FBX asset, so fall back to
        // the version source mapping if the editor FBX object list is stale or path-normalized differently.
        var sourcePaths = GetVersionSourcePaths(appliedVersion);
        return sourcePaths.Count > 0
            ? ResolveAdvancedMeshRenderers(appliedVersion, sourcePaths)
            : renderers;
    }

    private List<SkinnedMeshRenderer> ResolveAdvancedMeshRenderers(CustomBaseVersion appliedVersion, IEnumerable<string> sourcePaths)
    {
        return NativeMeshPayloadService
            .ResolveRenderersForSourcePaths(cachedRoot, appliedVersion, sourcePaths)
            .Where(renderer => renderer?.sharedMaterial != null)
            .GroupBy(renderer => renderer.GetInstanceID())
            .Select(group => group.First())
            .ToList();
    }

    private List<string> GetVersionSourcePaths(CustomBaseVersion appliedVersion)
    {
        if (appliedVersion?.sourceFiles == null)
        {
            return new List<string>();
        }

        return appliedVersion.sourceFiles
            .Select(file => file?.path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(MCBUtils.ToUnityPath)
            .Distinct()
            .ToList();
    }

    private List<Material> GetTargetMaterials(List<SkinnedMeshRenderer> renderers = null)
    {
        renderers = renderers ?? GetTargetRenderers();
        return renderers
            .Select(renderer => renderer.sharedMaterial)
            .Where(material => material != null)
            .GroupBy(material => material.GetInstanceID())
            .Select(group => group.First())
            .ToList();
    }

    private List<string> GetTargetFbxPaths()
    {
        var paths = new List<string>();
        if (editor?.baseFbxFilesProp == null)
        {
            return paths;
        }

        for (int i = 0; i < editor.baseFbxFilesProp.arraySize; i++)
        {
            var fbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            string path = fbx != null ? AssetDatabase.GetAssetPath(fbx) : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(MCBUtils.ToUnityPath(path));
            }
        }

        return paths
            .Distinct()
            .ToList();
    }
}
#endif
