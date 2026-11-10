#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Orbiters.Toolkit.Editor.Vpm;

public partial class CustomVeinsDrawer
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
        string veinsNormalPath = MCBUtils.GetVersionVeinsTexturePath(appliedVersion);

        MCBLogger.Log($"[CustomVeinsDrawer] Applying custom veins from: {veinsNormalPath}");

        bool success = false;
        bool anyFailures = false;
        foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetRenderers))
        {
            bool rendererSuccess = materialService.SetDetailNormalMap(renderer, veinsNormalPath, false);
            if (rendererSuccess)
            {
                materialService.SetDetailNormalOpacity(renderer, 1.0f, false);
                success = true;
            }
            else
            {
                anyFailures = true;
            }
        }
        materialService.SaveTouchedMaterialsSoon();

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
        foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetRenderers))
        {
            success |= materialService.RemoveDetailNormalMap(renderer, false);
        }
        // The original base may order its materials differently: any material still carrying version veins.
        success |= materialService.RemoveVersionVeins();
        materialService.SaveTouchedMaterialsSoon();

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
            ? AvatarPaths.Root(editor.customBaseTarget)
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
        renderers = sourcePaths.Count > 0
            ? ResolveAdvancedMeshRenderers(appliedVersion, sourcePaths)
            : renderers;
        if (renderers.Count > 0)
        {
            return renderers;
        }

        // Imported native-mesh versions can legitimately omit smrPaths. In that case the
        // generated mesh asset folder is the durable source of renderer provenance.
        return NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(cachedRoot, appliedVersion)
            .Where(renderer => renderer?.sharedMaterial != null)
            .ToList();
    }

    private List<SkinnedMeshRenderer> ResolveAdvancedMeshRenderers(CustomBaseVersion appliedVersion, IEnumerable<string> sourcePaths)
    {
        return NativeMeshPayloadService
            .ResolveRenderersForSourcePaths(cachedRoot, appliedVersion, sourcePaths, editor?.customBaseTarget)
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

    public bool BuildUIToolkit(VisualElement root)
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (!editor.isCustomBase ||
            !ExtraCustomizationUtils.HasFlag(appliedVersion?.extraCustomization, "customVeins"))
        {
            return false;
        }

        if (!EnsureMaterialService())
        {
            return false;
        }

        var targetMaterials = GetTargetMaterials();
        bool isLocked = targetMaterials.Any(material => materialService.IsMaterialLocked(material));
        bool currentEnabled = EditorPrefs.GetBool(CUSTOM_VEINS_PREF_KEY, true);

        var card = AvatarOptionsModule.CreateOptionCard("mcb-avatar-veins");
        card.Add(AvatarOptionsModule.CreateOptionTitle("Custom Veins"));

        var layout = new VisualElement();
        layout.AddToClassList("mcb-avatar-veins__layout");
        card.Add(layout);

        if (customVeinsTexture != null)
        {
            var imageColumn = new VisualElement();
            imageColumn.AddToClassList("mcb-avatar-veins__image-column");

            var image = new Image { image = customVeinsTexture, scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("mcb-avatar-veins__image");
            imageColumn.Add(image);
            layout.Add(imageColumn);
        }

        var controls = new VisualElement();
        controls.AddToClassList("mcb-avatar-veins__controls");
        layout.Add(controls);

        var toggle = new Toggle("Custom veins") { value = currentEnabled };
        toggle.AddToClassList("mcb-avatar-toggle");
        toggle.RegisterValueChangedCallback(evt =>
        {
            // The switch already shows the new state; the materials follow on the next frame, and a failure reverts it.
            bool enable = evt.newValue;
            toggle.schedule.Execute(() =>
            {
                bool success;
                if (enable)
                {
                    success = ApplyCustomVeins();
                }
                else
                {
                    RemoveCustomVeins();
                    success = true;
                }

                if (success)
                {
                    EditorPrefs.SetBool(CUSTOM_VEINS_PREF_KEY, enable);
                }
                else
                {
                    toggle.SetValueWithoutNotify(currentEnabled);
                }

                AvatarOptionsModule.RefreshEditorUi(editor);
            });
        });
        controls.Add(toggle);

        BuildShaderCompatibilityUIToolkit(controls, targetMaterials);
        BuildPoiyomiInstallSuggestionUIToolkit(controls, targetMaterials);
        BuildVeinsTexturePreviewUIToolkit(controls, appliedVersion);

        bool canVerifyVeinsState = targetMaterials.Count > 0;
        bool veinsApplied = canVerifyVeinsState && targetMaterials.All(material => materialService.HasDetailNormalMap(material));
        bool shouldShowWarning = currentEnabled && canVerifyVeinsState && !veinsApplied;
        if (shouldShowWarning)
        {
            BuildVeinsMissingWarningUIToolkit(controls, isLocked);
        }
        else
        {
            BuildReapplyButtonUIToolkit(controls, currentEnabled, isLocked);
        }

        root.Add(card);
        return true;
    }

    private void BuildShaderCompatibilityUIToolkit(VisualElement root, IReadOnlyList<Material> targetMaterials)
    {
        if (targetMaterials.Count == 0)
        {
            root.Add(AvatarOptionsModule.CreateOptionHelpBox(
                "Could not verify targeted FBX material status. The custom veins may still be applied in the scene.",
                HelpBoxMessageType.Info));
            return;
        }

        var shaderNames = targetMaterials
            .Select(material => material?.shader?.name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();

        bool isSupported = shaderNames.Count > 0 && shaderNames.All(materialService.IsShaderSupported);
        Texture2D icon = isSupported ? okIcon : koIcon;

        var label = AvatarOptionsModule.CreateOptionLabel("Detected Shader:", 11, FontStyle.Normal, new Color(0.7f, 0.7f, 0.7f));
        label.AddToClassList("mcb-avatar-veins__shader-title");
        root.Add(label);

        var row = new VisualElement();
        row.AddToClassList("mcb-avatar-veins__shader-row");

        if (icon != null)
        {
            var iconImage = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
            iconImage.AddToClassList("mcb-avatar-veins__shader-icon");
            row.Add(iconImage);
        }

        string supportText = isSupported ? "Supported" : "Not Supported";
        string shaderLabel = shaderNames.Count == 0
            ? "Unknown shader"
            : string.Join(", ", shaderNames.Take(3)) + (shaderNames.Count > 3 ? $" and {shaderNames.Count - 3} more" : string.Empty);
        row.Add(AvatarOptionsModule.CreateOptionLabel($"{shaderLabel}: {supportText}", 11, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f)));
        root.Add(row);
    }

    private void BuildVeinsTexturePreviewUIToolkit(VisualElement root, CustomBaseVersion appliedVersion)
    {
        if (appliedVersion == null)
        {
            return;
        }

        string veinsNormalPath = MCBUtils.GetVersionVeinsTexturePath(appliedVersion);
        Texture2D veinsTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(veinsNormalPath);

        var preview = new VisualElement();
        preview.AddToClassList("mcb-avatar-veins__preview");
        preview.Add(AvatarOptionsModule.CreateOptionLabel("Applied on detail normal map", 11, FontStyle.Normal, new Color(0.7f, 0.7f, 0.7f)));

        var field = new ObjectField
        {
            objectType = typeof(Texture2D),
            value = veinsTexture,
            allowSceneObjects = false
        };
        field.AddToClassList("mcb-avatar-veins__texture-field");
        field.SetEnabled(false);
        preview.Add(field);
        root.Add(preview);
    }

    private void BuildPoiyomiInstallSuggestionUIToolkit(VisualElement root, IReadOnlyList<Material> targetMaterials)
    {
        if (!HasLockedPoiyomiMaterial(targetMaterials))
        {
            return;
        }

        var prompt = new DependencyPrompt(McbDependencies.Vpm, McbDependencies.PoiyomiPackageId,
            "This material was locked from a Poiyomi shader, but Poiyomi Toon is not installed in the project.");
        if (prompt.IsInstalled)
        {
            return;
        }

        prompt.Installed += () => AvatarOptionsModule.RefreshEditorUi(editor);
        root.Add(prompt);
    }

    private void BuildReapplyButtonUIToolkit(VisualElement root, bool currentEnabled, bool isLocked)
    {
        string label = isLocked ? "Unlock and re-apply" : "Re-apply";
        var button = AvatarOptionsModule.CreateOptionButton(label, () =>
        {
            if (EnsureUnlocked(GetTargetMaterials()))
            {
                ApplyCustomVeins();
            }

            AvatarOptionsModule.RefreshEditorUi(editor);
        });
        button.AddToClassList("mcb-avatar-veins__reapply-button");
        button.SetEnabled(currentEnabled);
        root.Add(button);
    }

    private void BuildVeinsMissingWarningUIToolkit(VisualElement root, bool isLocked)
    {
        var box = new VisualElement();
        box.AddToClassList("mcb-avatar-helpbox");
        box.AddToClassList("mcb-avatar-helpbox--warning");

        var icon = AvatarOptionsModule.CreateOptionLabel("!", 14, FontStyle.Bold, Color.white);
        icon.AddToClassList("mcb-avatar-helpbox__icon");
        box.Add(icon);

        var content = new VisualElement();
        content.AddToClassList("mcb-avatar-helpbox__content");
        content.Add(AvatarOptionsModule.CreateOptionLabel(
            "the custom veins are not applied anymore.",
            12,
            FontStyle.Normal,
            new Color(0.82f, 0.82f, 0.82f)));

        var actions = new VisualElement();
        actions.AddToClassList("mcb-avatar-helpbox__actions");

        string reapplyLabel = isLocked ? "Unlock and re-apply" : "Re-apply";
        actions.Add(AvatarOptionsModule.CreateOptionButton(reapplyLabel, () =>
        {
            if (EnsureUnlocked(GetTargetMaterials()))
            {
                ApplyCustomVeins();
            }

            AvatarOptionsModule.RefreshEditorUi(editor);
        }));

        actions.Add(AvatarOptionsModule.CreateOptionButton("Disable custom veins", () =>
        {
            EditorPrefs.SetBool(CUSTOM_VEINS_PREF_KEY, false);
            RemoveCustomVeins();
            AvatarOptionsModule.RefreshEditorUi(editor);
        }));

        content.Add(actions);
        box.Add(content);
        root.Add(box);
    }
}
#endif
