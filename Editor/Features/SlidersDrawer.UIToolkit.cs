#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public partial class SlidersDrawer
{
    private readonly MCBEditor editor;
    private readonly Texture2D sideImage;

    private List<string> sliderNames = new List<string>();
    private HashSet<int> selectedIndices = new HashSet<int>();
    
    private double lastMenuNameChangeTime;
    private bool hasPendingMenuNameUpdate;
    private bool replayPlaymodeAfterForcedApply;
    private const double DEBOUNCE_DELAY = 4.0; // Seconds

    private bool lastKnownGameObjectState = true;

    public SlidersDrawer(MCBEditor editor)
    {
        this.editor = editor;
        
        // Load the image
        sideImage = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/slidersFactoryImageHalf.png");

        // Initialize with default/empty state
        UpdateSliderData();
        
        var entries = GetSliderEntries();
        HashSet<int> initialSelection = GetInitialSelection(entries);

        selectedIndices = initialSelection;
    }

    public void RequestApplyDebounced()
    {
        lastMenuNameChangeTime = EditorApplication.timeSinceStartup;
        hasPendingMenuNameUpdate = true;
    }

    public void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            replayPlaymodeAfterForcedApply = false;
            return;
        }

        if (state != PlayModeStateChange.ExitingEditMode) return;
        if (!hasPendingMenuNameUpdate) return;

        // Cancel this play transition, apply pending setup in Edit Mode,
        // then resume Play Mode on the next tick.
        if (!replayPlaymodeAfterForcedApply)
        {
            replayPlaymodeAfterForcedApply = true;
            EditorApplication.isPlaying = false;
        }

        hasPendingMenuNameUpdate = false;
        ApplySlidersToAvatar(immediate: true);

        EditorApplication.delayCall += ResumePlaymodeAfterForcedApply;
    }

    private void ResumePlaymodeAfterForcedApply()
    {
        if (!replayPlaymodeAfterForcedApply) return;

        replayPlaymodeAfterForcedApply = false;

        if (!EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = true;
        }
    }

    private List<CustomBlendshapeEntry> GetSliderEntries()
    {
        if (editor.customBaseTarget.appliedCustomBaseVersion?.customBlendshapes == null)
            return new List<CustomBlendshapeEntry>();

        return editor.customBaseTarget.appliedCustomBaseVersion.customBlendshapes
            .Where(e => e.isSlider)
            .ToList();
    }

    private void UpdateSliderData()
    {
        var entries = GetSliderEntries();
        sliderNames = entries.Select(e => e.name).ToList();
    }

    private HashSet<int> GetInitialSelection(List<CustomBlendshapeEntry> entries)
    {
        if (editor.customBaseTarget.useCustomSliderSelection)
        {
            var savedNames = new HashSet<string>(editor.customBaseTarget.customSliderSelectionNames ?? new List<string>());
            var restored = new HashSet<int>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (savedNames.Contains(entries[i].name)) restored.Add(i);
            }
            return restored;
        }

        var defaults = new HashSet<int>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].isSliderDefault) defaults.Add(i);
        }
        return defaults;
    }

    private HashSet<int> GetDefaultSelection(List<CustomBlendshapeEntry> entries)
    {
        var defaults = new HashSet<int>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].isSliderDefault) defaults.Add(i);
        }
        return defaults;
    }

    private void OnSliderSelectionChanged(HashSet<int> selection)
    {
        selectedIndices = new HashSet<int>(selection);

        PersistSliderSelectionOverride();
        RequestApplyDebounced();
    }

    private void PersistSliderSelectionOverride()
    {
        var entries = GetSliderEntries();
        var defaultSelection = GetDefaultSelection(entries);
        bool matchesDefault = defaultSelection.SetEquals(selectedIndices);

        Undo.RecordObject(editor.customBaseTarget, "Change Slider Selection");
        if (matchesDefault)
        {
            editor.customBaseTarget.useCustomSliderSelection = false;
            editor.customBaseTarget.customSliderSelectionNames.Clear();
        }
        else
        {
            editor.customBaseTarget.useCustomSliderSelection = true;
            editor.customBaseTarget.customSliderSelectionNames = selectedIndices
                .Where(index => index >= 0 && index < entries.Count)
                .Select(index => entries[index].name)
                .ToList();
        }

        EditorUtility.SetDirty(editor.customBaseTarget);
    }

    private void ApplySlidersToAvatar(bool immediate = false)
    {
        var allEntries = GetSliderEntries();
        var selectedEntries = selectedIndices.Select(index => allEntries[index]).ToList();
        
        GameObject avatarRoot = editor.customBaseTarget.transform.root.gameObject;
        string menuName = editor.customBaseTarget.slidersMenuName;

        System.Action applyAction = () => {
            VRCFuryService.Instance.ApplySliders(avatarRoot, menuName, selectedEntries);
            
            // Ensure the active state matches user preference or default
            var slidersTransform = avatarRoot.transform.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
            if (slidersTransform != null)
            {
                bool desiredState = editor.customBaseTarget.useCustomSlidersState ? editor.customBaseTarget.customSlidersState : true;
                slidersTransform.gameObject.SetActive(desiredState);
                lastKnownGameObjectState = desiredState;
            }
        };

        if (immediate)
        {
            applyAction();
            return;
        }

        // Use the TaskQueue to avoid blocking the UI
        VRCFuryTaskQueue.Enqueue(applyAction);
    }

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

        // Their parameter cost shows in the Toolkit's avatar budget, over the Scene view.
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
            selectedIndices = GetInitialSelection(entries);
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
