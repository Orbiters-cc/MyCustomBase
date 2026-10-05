#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public partial class SlidersDrawer
{
    private Label toolkitPendingApplyLabel;
    private Texture2D toolkitSliderIcon;

    public bool BuildUIToolkit(VisualElement root)
    {
        var entries = PrepareSliderStateForUIToolkit();
        if (entries.Count == 0)
        {
            return false;
        }

        GameObject avatarRoot = editor.customBaseTarget.transform.root?.gameObject;
        if (avatarRoot == null)
        {
            return false;
        }

        Transform slidersTransform = avatarRoot.transform.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
        bool currentSlidersActive = editor.customBaseTarget.useCustomSlidersState
            ? editor.customBaseTarget.customSlidersState
            : true;

        var card = AvatarOptionsModule.CreateOptionCard("mcb-avatar-sliders");
        var layout = new VisualElement();
        layout.AddToClassList("mcb-avatar-sliders__layout");
        card.Add(layout);

        layout.Add(CreateSliderImageUIToolkit());

        var content = new VisualElement();
        content.AddToClassList("mcb-avatar-sliders__content");
        layout.Add(content);

        var titleRow = new VisualElement();
        titleRow.AddToClassList("mcb-avatar-sliders__title-row");
        titleRow.Add(AvatarOptionsModule.CreateOptionTitle("Sliders"));

        var activeToggle = new Toggle { value = currentSlidersActive };
        activeToggle.AddToClassList("mcb-avatar-toggle");
        activeToggle.AddToClassList("mcb-avatar-sliders__active-toggle");
        activeToggle.RegisterValueChangedCallback(evt =>
        {
            Undo.RecordObject(editor.customBaseTarget, "Toggle Sliders State");
            editor.customBaseTarget.useCustomSlidersState = true;
            editor.customBaseTarget.customSlidersState = evt.newValue;
            EditorUtility.SetDirty(editor.customBaseTarget);

            if (slidersTransform != null)
            {
                Undo.RecordObject(slidersTransform.gameObject, "Toggle Sliders GameObject");
                slidersTransform.gameObject.SetActive(evt.newValue);
                lastKnownGameObjectState = evt.newValue;
            }

            AvatarOptionsModule.RefreshEditorUi(editor);
        });
        titleRow.Add(activeToggle);
        content.Add(titleRow);

        // Their parameter cost shows in the avatar budget card.
        content.Add(BuildSliderChipGroupUIToolkit());

        toolkitPendingApplyLabel = AvatarOptionsModule.CreateOptionLabel(string.Empty, 11, FontStyle.Normal, new Color(0.55f, 0.55f, 0.55f));
        toolkitPendingApplyLabel.AddToClassList("mcb-avatar-sliders__pending");
        content.Add(toolkitPendingApplyLabel);
        UpdatePendingApplyLabelText();

        root.Add(card);
        return true;
    }

    internal bool NeedsUIToolkitStateRefresh()
    {
        var entries = GetSliderEntries();
        if (entries.Count == 0)
        {
            return false;
        }

        var currentNames = entries.Select(e => e.name).ToList();
        if (entries.Count != sliderNames.Count || !sliderNames.SequenceEqual(currentNames))
        {
            return true;
        }

        Transform root = editor.customBaseTarget != null && editor.customBaseTarget.transform != null
            ? editor.customBaseTarget.transform.root
            : null;
        Transform slidersTransform = root != null ? root.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME) : null;
        if (slidersTransform == null)
        {
            return false;
        }

        return slidersTransform.gameObject.activeSelf != lastKnownGameObjectState;
    }

    internal void TickUIToolkit()
    {
        if (!hasPendingMenuNameUpdate)
        {
            UpdatePendingApplyLabelText();
            return;
        }

        double timeRemaining = GetPendingApplySeconds();
        if (timeRemaining <= 0)
        {
            hasPendingMenuNameUpdate = false;
            UpdatePendingApplyLabelText();
            ApplySlidersToAvatar();
            AvatarOptionsModule.RefreshEditorUi(editor);
            return;
        }

        UpdatePendingApplyLabelText();
        editor.Repaint();
    }

    private List<CustomBlendshapeEntry> PrepareSliderStateForUIToolkit()
    {
        var entries = GetSliderEntries();
        if (entries.Count == 0)
        {
            return entries;
        }

        GameObject avatarRoot = editor.customBaseTarget.transform.root?.gameObject;
        if (avatarRoot != null)
        {
            Transform slidersTransform = avatarRoot.transform.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
            bool gameObjectActive = slidersTransform != null && slidersTransform.gameObject.activeSelf;
            if (slidersTransform != null && gameObjectActive != lastKnownGameObjectState)
            {
                lastKnownGameObjectState = gameObjectActive;
                Undo.RecordObject(editor.customBaseTarget, "Sync Sliders State from GameObject");
                editor.customBaseTarget.useCustomSlidersState = true;
                editor.customBaseTarget.customSlidersState = gameObjectActive;
                EditorUtility.SetDirty(editor.customBaseTarget);
            }
        }

        var currentNames = entries.Select(e => e.name).ToList();
        if (entries.Count != sliderNames.Count || !sliderNames.SequenceEqual(currentNames))
        {
            UpdateSliderData();
            selectableChipGroup.SetOptions(sliderNames);

            HashSet<int> initialSelection = GetInitialSelection(entries);
            selectedIndices = initialSelection;

            suppressSelectionCallback = true;
            selectableChipGroup.SetSelection(initialSelection);
            suppressSelectionCallback = false;
        }

        return entries;
    }

    private VisualElement CreateSliderImageUIToolkit()
    {
        var imagePanel = new VisualElement();
        imagePanel.AddToClassList("mcb-avatar-sliders__image-panel");

        if (sideImage != null)
        {
            var image = new Image { image = sideImage, scaleMode = ScaleMode.StretchToFill };
            image.AddToClassList("mcb-avatar-sliders__side-image");
            imagePanel.Add(image);
        }

        var overlay = new VisualElement();
        overlay.AddToClassList("mcb-avatar-sliders__image-overlay");

        var icon = GetToolkitSliderIcon();
        if (icon != null)
        {
            var iconImage = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
            iconImage.AddToClassList("mcb-avatar-sliders__menu-icon");
            overlay.Add(iconImage);
        }

        var menuNameField = new TextField
        {
            value = editor.customBaseTarget.slidersMenuName ?? string.Empty,
            multiline = true
        };
        menuNameField.AddToClassList("mcb-avatar-sliders__menu-name");
        menuNameField.RegisterValueChangedCallback(evt =>
        {
            Undo.RecordObject(editor.customBaseTarget, "Change Slider Menu Name");
            editor.customBaseTarget.slidersMenuName = evt.newValue;
            EditorUtility.SetDirty(editor.customBaseTarget);
            RequestApplyDebounced();
            UpdatePendingApplyLabelText();
            editor.Repaint();
        });
        overlay.Add(menuNameField);

        imagePanel.Add(overlay);
        return imagePanel;
    }

    private Texture2D GetToolkitSliderIcon()
    {
        if (toolkitSliderIcon == null)
        {
            toolkitSliderIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/vrcSliderIcon.png");
        }

        return toolkitSliderIcon;
    }

    /// <summary>Parameter options for budgets: the sliders the user selected replace those the sliders object has now.</summary>
    internal Orbiters.Toolkit.Editor.VRChat.Parameters.AvatarParameterBudget.Options ParameterOptions() =>
        GetSliderEntries().Count == 0 ? null : VRCFuryService.SliderParameterOptions(selectedIndices.Count);

    private VisualElement BuildSliderChipGroupUIToolkit()
    {
        var group = new VisualElement();
        group.AddToClassList("mcb-avatar-chip-group");

        for (int i = 0; i < sliderNames.Count; i++)
        {
            int index = i;
            bool selected = selectedIndices.Contains(index);
            var chip = new Button(() => ToggleSliderSelectionUIToolkit(index));
            chip.AddToClassList("mcb-avatar-chip");
            chip.EnableInClassList("mcb-avatar-chip--selected", selected);

            var marker = new VisualElement();
            marker.AddToClassList("mcb-avatar-chip__marker");
            chip.Add(marker);

            var label = AvatarOptionsModule.CreateOptionLabel(sliderNames[i], 12, FontStyle.Normal, Color.white);
            label.AddToClassList("mcb-avatar-chip__label");
            chip.Add(label);

            group.Add(chip);
        }

        return group;
    }

    private void ToggleSliderSelectionUIToolkit(int index)
    {
        var selection = new HashSet<int>(selectedIndices);
        if (selection.Contains(index))
        {
            selection.Remove(index);
        }
        else
        {
            selection.Add(index);
        }

        OnSliderSelectionChanged(selection);
        AvatarOptionsModule.RefreshEditorUi(editor);
    }

    private double GetPendingApplySeconds()
    {
        return DEBOUNCE_DELAY - (EditorApplication.timeSinceStartup - lastMenuNameChangeTime);
    }

    private void UpdatePendingApplyLabelText()
    {
        if (toolkitPendingApplyLabel == null)
        {
            return;
        }

        if (!hasPendingMenuNameUpdate)
        {
            toolkitPendingApplyLabel.text = string.Empty;
            toolkitPendingApplyLabel.style.display = DisplayStyle.None;
            return;
        }

        double timeRemaining = System.Math.Max(0, GetPendingApplySeconds());
        toolkitPendingApplyLabel.text = $"Applying with VRCFury in {timeRemaining:F0}s...";
        toolkitPendingApplyLabel.style.display = DisplayStyle.Flex;
    }
}
#endif
