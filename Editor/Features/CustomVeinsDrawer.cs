#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

public class CustomVeinsDrawer
{
    private readonly MCBEditor editor;
    private Texture2D customVeinsTexture;
    private Texture2D okIcon;
    private Texture2D koIcon;
    private MaterialService materialService;

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
        if (!editor.isCustomBase || appliedVersion?.extraCustomization == null || 
            !appliedVersion.extraCustomization.Contains("customVeins"))
            return;

        // Initialize MaterialService with avatar root
        if (materialService == null)
        {
            materialService = new MaterialService(editor.customBaseTarget.transform.root);
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

        // "Applied on detail normal map" label and texture preview
        DrawVeinsTexturePreview();

        materialService.TryGetMaterial("Body", out var bodyMaterial);
        bool isLocked = bodyMaterial != null && materialService.IsMaterialLocked(bodyMaterial);
        bool veinsApplied = bodyMaterial != null && materialService.HasDetailNormalMap(bodyMaterial);
        bool shouldShowWarning = currentEnabled && !veinsApplied;

        if (shouldShowWarning)
        {
            DrawVeinsMissingWarning(bodyMaterial, isLocked);
        }
        else
        {
            DrawReapplyButton(currentEnabled, bodyMaterial, isLocked);
        }
        
        GUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();
    }

    private void DrawShaderCompatibility()
    {
        string shaderName = materialService.GetShader("Body");
        
        if (string.IsNullOrEmpty(shaderName))
        {
            EditorGUILayout.HelpBox("Could not detect shader on Body material", MessageType.Warning);
            return;
        }

        bool isSupported = materialService.IsShaderSupported(shaderName);
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
        // Create a custom style with top padding to vertically center the text with the 20x20 icon
        GUIStyle centeredLabelStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            padding = new RectOffset(0, 0, 4, 0) // Add 4px top padding to align with icon center
        };
        GUILayout.Label($"{shaderName}: {supportText}", centeredLabelStyle, GUILayout.ExpandHeight(false));
        
        EditorGUILayout.EndHorizontal();
    }

    private void DrawVeinsTexturePreview()
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (appliedVersion == null) return;

        // Construct the path to the veins normal map using the utility method
        string versionFolder = MCBUtils.GetVersionDataPath(appliedVersion.version, appliedVersion.defaultAviVersion);
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


    private bool ApplyCustomVeins()
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (appliedVersion == null)
        {
            MCBLogger.LogError("[CustomVeinsDrawer] No applied version found");
            return false;
        }

        materialService.TryGetMaterial("Body", out var bodyMaterial);
        if (!EnsureUnlocked(bodyMaterial))
        {
            return false;
        }

        // Construct the path to the veins normal map using the utility method
        string versionFolder = MCBUtils.GetVersionDataPath(appliedVersion.version, appliedVersion.defaultAviVersion);
        string veinsNormalPath = System.IO.Path.Combine(versionFolder, "veins normal.png").Replace("\\", "/");

        MCBLogger.Log($"[CustomVeinsDrawer] Applying custom veins from: {veinsNormalPath}");

        // Apply the detail normal map
        bool success = materialService.SetDetailNormalMap("Body", veinsNormalPath);
        if (success)
        {
            // Set opacity to 1.0
            materialService.SetDetailNormalOpacity("Body", 1.0f);
            MCBLogger.Log("[CustomVeinsDrawer] Custom veins applied successfully");
        }
        else
        {
            MCBLogger.LogError("[CustomVeinsDrawer] Failed to apply custom veins");
        }
        return success;
    }

    private bool RemoveCustomVeins()
    {
        MCBLogger.Log("[CustomVeinsDrawer] Removing custom veins");

        materialService.TryGetMaterial("Body", out var bodyMaterial);
        if (!EnsureUnlocked(bodyMaterial))
        {
            return false;
        }

        bool success = materialService.RemoveDetailNormalMap("Body");
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

    private void DrawReapplyButton(bool currentEnabled, Material bodyMaterial, bool isLocked)
    {
        using (new EditorGUI.DisabledScope(!currentEnabled))
        {
            string label = isLocked ? "Unlock and re-apply" : "Re-apply";
            if (GUILayout.Button(label, GUILayout.Width(140)))
            {
                if (EnsureUnlocked(bodyMaterial))
                {
                    ApplyCustomVeins();
                }
            }
        }
    }

    private void DrawVeinsMissingWarning(Material bodyMaterial, bool isLocked)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("the custom veins are not applied anymore.", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4f);

        EditorGUILayout.BeginHorizontal();
        string reapplyLabel = isLocked ? "Unlock and re-apply" : "Re-apply";
        if (GUILayout.Button(reapplyLabel, GUILayout.Width(140)))
        {
            if (EnsureUnlocked(bodyMaterial))
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
}
#endif
