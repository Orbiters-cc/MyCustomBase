#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CustomVeinsDrawer
{
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
            bool success;
            if (evt.newValue)
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
                EditorPrefs.SetBool(CUSTOM_VEINS_PREF_KEY, evt.newValue);
            }
            else
            {
                toggle.SetValueWithoutNotify(currentEnabled);
            }

            AvatarOptionsModule.RefreshEditorUi(editor);
        });
        controls.Add(toggle);

        BuildShaderCompatibilityUIToolkit(controls, targetMaterials);
        BuildPoiyomiInstallSuggestionUIToolkit(controls, targetMaterials);
        BuildVeinsTexturePreviewUIToolkit(controls, appliedVersion);

        bool veinsApplied = targetMaterials.Count > 0 && targetMaterials.All(material => materialService.HasDetailNormalMap(material));
        bool shouldShowWarning = currentEnabled && !veinsApplied;
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
                "Could not detect materials on the targeted FBX meshes",
                HelpBoxMessageType.Warning));
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

        string versionFolder = MCBUtils.GetVersionDataPath(appliedVersion);
        string veinsNormalPath = System.IO.Path.Combine(versionFolder, "veins normal.png").Replace("\\", "/");
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

        var status = VpmDependencyService.Instance.GetOptionalDependencyStatus("com.poiyomi.toon");
        if (status == null || status.IsInstalled)
        {
            return;
        }

        var box = new VisualElement();
        box.AddToClassList("mcb-avatar-helpbox");
        box.AddToClassList("mcb-avatar-helpbox--warning");

        var icon = AvatarOptionsModule.CreateOptionLabel("!", 14, FontStyle.Bold, Color.white);
        icon.AddToClassList("mcb-avatar-helpbox__icon");
        box.Add(icon);

        var content = new VisualElement();
        content.AddToClassList("mcb-avatar-helpbox__content");
        content.Add(AvatarOptionsModule.CreateOptionLabel(
            "This material was locked from a Poiyomi shader, but Poiyomi Toon is not installed in the project.",
            12,
            FontStyle.Normal,
            new Color(0.82f, 0.82f, 0.82f)));

        if (!string.IsNullOrWhiteSpace(status.Reason))
        {
            var reason = AvatarOptionsModule.CreateOptionLabel(status.Reason, 11, FontStyle.Normal, new Color(0.62f, 0.62f, 0.62f));
            reason.AddToClassList("mcb-avatar-helpbox__secondary");
            content.Add(reason);
        }

        string label = VpmDependencyService.Instance.IsInstalling ? "Installing Poiyomi Toon..." : "Install Poiyomi Toon";
        var installButton = AvatarOptionsModule.CreateOptionButton(label, () =>
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

            AvatarOptionsModule.RefreshEditorUi(editor);
        });
        installButton.AddToClassList("mcb-avatar-veins__poiyomi-button");
        installButton.SetEnabled(!VpmDependencyService.Instance.IsInstalling);
        content.Add(installButton);

        box.Add(content);
        root.Add(box);
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
