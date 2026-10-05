#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public partial class SlidersDrawer
{
    private readonly MCBEditor editor;
    private readonly SelectableChipGroup selectableChipGroup;
    private readonly Texture2D sideImage;

    private List<string> sliderNames = new List<string>();
    private HashSet<int> selectedIndices = new HashSet<int>();
    private bool suppressSelectionCallback;
    
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
        selectableChipGroup = new SelectableChipGroup(sliderNames, initialSelection, OnSliderSelectionChanged);
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

        if (suppressSelectionCallback) return;

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
}
#endif
