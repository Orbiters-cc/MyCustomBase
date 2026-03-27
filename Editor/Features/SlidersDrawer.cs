#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class SlidersDrawer
{
    private readonly MCBEditor editor;
    private readonly SelectableChipGroup selectableChipGroup;
    private readonly RepartitionGraph repartitionGraph;
    private readonly Texture2D sideImage;

    private List<string> sliderNames = new List<string>();
    private List<RepartitionGraph.GraphElement> graphData = new List<RepartitionGraph.GraphElement>();
    private HashSet<int> selectedIndices = new HashSet<int>();
    private bool suppressSelectionCallback;
    
    private double lastMenuNameChangeTime;
    private bool hasPendingMenuNameUpdate;
    private bool replayPlaymodeAfterForcedApply;
    private const double DEBOUNCE_DELAY = 4.0; // Seconds

    private const int MAX_PARAMETERS = 256;

    private bool lastKnownGameObjectState = true;

    public SlidersDrawer(MCBEditor editor)
    {
        this.editor = editor;
        
        // Load the image
        sideImage = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/slidersFactoryImageHalf.png");

        repartitionGraph = new RepartitionGraph();
        
        // Initialize with default/empty state
        UpdateSliderData();
        
        var entries = GetSliderEntries();
        HashSet<int> initialSelection = GetInitialSelection(entries);

        selectedIndices = initialSelection;
        selectableChipGroup = new SelectableChipGroup(sliderNames, initialSelection, OnSliderSelectionChanged);
        
        UpdateGraph(initialSelection.Count);
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
        UpdateGraph(selectedIndices.Count);

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

    private void UpdateGraph(int selectedCount)
    {
        GameObject avatarRoot = editor.customBaseTarget.transform.root?.gameObject;
        if (avatarRoot == null) return;

        var usage = VRCFuryService.Instance.GetAvatarParameterUsage(avatarRoot, selectedCount);
        int usedByAvatar = usage.usedByAvatar;
        int usedBySliders = usage.usedBySliders;
        int available = Mathf.Max(0, MAX_PARAMETERS - usage.totalUsedAfterBuild);

        graphData = new List<RepartitionGraph.GraphElement>
        {
            new RepartitionGraph.GraphElement(usedByAvatar, $"Parameters used by the avatar : {usedByAvatar}", "#e0e0e0"),
            new RepartitionGraph.GraphElement(usedBySliders, $"Parameters used by the sliders : {usedBySliders}", "#008000"),
            new RepartitionGraph.GraphElement(available, $"Parameters available : {available}", "#333333")
        };
    }

    public void Draw()
    {
        var entries = GetSliderEntries();
        if (entries.Count == 0) return;

        GameObject avatarRoot = editor.customBaseTarget.transform.root.gameObject;
        Transform slidersTransform = avatarRoot.transform.Find(VRCFuryService.SLIDERS_GAMEOBJECT_NAME);
        bool gameObjectActive = slidersTransform != null && slidersTransform.gameObject.activeSelf;

        // Detect manual change from Hierarchy
        if (slidersTransform != null && gameObjectActive != lastKnownGameObjectState)
        {
            lastKnownGameObjectState = gameObjectActive;
            Undo.RecordObject(editor.customBaseTarget, "Sync Sliders State from GameObject");
            editor.customBaseTarget.useCustomSlidersState = true;
            editor.customBaseTarget.customSlidersState = gameObjectActive;
            EditorUtility.SetDirty(editor.customBaseTarget);
        }

        // If entries changed, refresh names and selection
        var currentNames = entries.Select(e => e.name).ToList();
        if (entries.Count != sliderNames.Count || !sliderNames.SequenceEqual(currentNames))
        {
            UpdateSliderData();
            selectableChipGroup.SetOptions(sliderNames);
            
            HashSet<int> initialSelection = GetInitialSelection(entries);
            selectedIndices = initialSelection;
            UpdateGraph(initialSelection.Count);

            suppressSelectionCallback = true;
            selectableChipGroup.SetSelection(initialSelection);
            suppressSelectionCallback = false;
        }

        float drawerHeight = 224f;
        float imageWidth = 115f;
        Rect compressionSectionBottomRect = new Rect();
        bool showPendingApplyLabel = false;
        double timeRemaining = 0;
        
        // Define styles for transparent text field
        GUIStyle transparentTextFieldStyle = new GUIStyle(GUIStyle.none);
        transparentTextFieldStyle.alignment = TextAnchor.UpperCenter;
        transparentTextFieldStyle.normal.textColor = Color.white;
        transparentTextFieldStyle.focused.textColor = Color.white;
        transparentTextFieldStyle.fontSize = 12;
        transparentTextFieldStyle.fontStyle = FontStyle.Bold;
        transparentTextFieldStyle.wordWrap = true;
        transparentTextFieldStyle.clipping = TextClipping.Overflow;

        EditorGUILayout.BeginHorizontal();
        {
            // Reserve space for the image
            Rect imagePlaceholderRect = EditorGUILayout.GetControlRect(false, drawerHeight, GUILayout.Width(imageWidth));
            
            // Draw the right side content
            GUILayout.BeginVertical(GUILayout.Height(drawerHeight));
            {
                GUILayout.FlexibleSpace();
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Sliders", EditorStyles.boldLabel);
                
                bool currentSlidersActive = editor.customBaseTarget.useCustomSlidersState ? editor.customBaseTarget.customSlidersState : true;
                EditorGUI.BeginChangeCheck();
                bool nextSlidersActive = EditorGUILayout.Toggle(currentSlidersActive, GUILayout.Width(32));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(editor.customBaseTarget, "Toggle Sliders State");
                    editor.customBaseTarget.useCustomSlidersState = true;
                    editor.customBaseTarget.customSlidersState = nextSlidersActive;
                    EditorUtility.SetDirty(editor.customBaseTarget);
                    
                    if (slidersTransform != null)
                    {
                        Undo.RecordObject(slidersTransform.gameObject, "Toggle Sliders GameObject");
                        slidersTransform.gameObject.SetActive(nextSlidersActive);
                        lastKnownGameObjectState = nextSlidersActive;
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                repartitionGraph.Draw(graphData);
                EditorGUILayout.Space(15);

                selectableChipGroup.Draw();
                EditorGUILayout.Space(5);

                GameObject rootObj = editor.customBaseTarget.transform.root.gameObject;
                var usage = VRCFuryService.Instance.GetAvatarParameterUsage(rootObj, selectedIndices.Count);
                
                EditorGUI.BeginDisabledGroup(usage.compressionIsExternal);
                bool toggleVal = usage.compressionEnabled;
                EditorGUI.BeginChangeCheck();
                toggleVal = EditorGUILayout.ToggleLeft("enable VRCFury parameters compression", toggleVal);
                if (EditorGUI.EndChangeCheck())
                {
                    VRCFuryService.Instance.SetCompression(rootObj, toggleVal);
                    UpdateGraph(selectedIndices.Count);
                }
                EditorGUI.EndDisabledGroup();
                compressionSectionBottomRect = GUILayoutUtility.GetLastRect();

                if (usage.compressionEnabled)
                {
                    GUIStyle successStyle = new GUIStyle(EditorStyles.miniLabel);
                    successStyle.normal.textColor = new Color(0.3f, 0.8f, 0.3f);
                    successStyle.fontStyle = FontStyle.Bold;
                    EditorGUILayout.LabelField("Parameter use reduced by VRCFury compression", successStyle);
                    compressionSectionBottomRect = GUILayoutUtility.GetLastRect();
                    
                    if (usage.compressionIsExternal && !string.IsNullOrEmpty(usage.compressionPath))
                    {
                        GUIStyle pathStyle = new GUIStyle(EditorStyles.miniLabel);
                        pathStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                        EditorGUILayout.LabelField($"Already activated in : {usage.compressionPath}", pathStyle);
                        compressionSectionBottomRect = GUILayoutUtility.GetLastRect();
                    }
                }
                
                // Debounce logic for sliders setup (moved here to avoid clipping)
                if (hasPendingMenuNameUpdate)
                {
                    timeRemaining = DEBOUNCE_DELAY - (EditorApplication.timeSinceStartup - lastMenuNameChangeTime);
                    if (timeRemaining <= 0)
                    {
                        hasPendingMenuNameUpdate = false;
                        ApplySlidersToAvatar();
                    }
                    else
                    {
                        showPendingApplyLabel = true;
                        editor.Repaint(); // Force repaint to see the countdown
                    }
                }
                
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndVertical();

            // Debounce logic moved inside the vertical layout

            // Draw overlay elements (Image + Icon + Text) at absolute coordinates
            if (Event.current.type == EventType.Repaint || Event.current.type == EventType.Layout || Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp || Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp)
            {
                // Draw background image
                if (sideImage != null && Event.current.type == EventType.Repaint)
                {
                    Rect imageRect = new Rect(0, imagePlaceholderRect.y, imageWidth, drawerHeight);
                    GUI.DrawTexture(imageRect, sideImage, ScaleMode.StretchToFill);
                }

                Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/vrcSliderIcon.png");
                if (icon != null)
                {
                    float iconSize = 50f;
                    float paddingLeft = 49f;
                    float spacing = 2f;
                    float textWidth = 70f;
                    
                    string currentName = editor.customBaseTarget.slidersMenuName;
                    
                    // Calculate dynamic height based on content
                    float textHeight = transparentTextFieldStyle.CalcHeight(new GUIContent(currentName), textWidth);
                    
                    // Calculate total height of the block to center it
                    float totalBlockHeight = iconSize + spacing + textHeight;
                    float blockStartY = imagePlaceholderRect.y + (drawerHeight - totalBlockHeight) / 2f;
                    
                    Rect iconRect = new Rect(paddingLeft, blockStartY, iconSize, iconSize);

                    // Draw Icon
                    if (Event.current.type == EventType.Repaint)
                    {
                        GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);
                    }
                    
                    // Draw Text Field below icon
                    float textX = paddingLeft + (iconSize / 2f) - (textWidth / 2f);
                    float textY = iconRect.yMax + spacing;
                    Rect textRect = new Rect(textX, textY, textWidth, textHeight);

                    EditorGUI.BeginChangeCheck();
                    string newName = EditorGUI.TextField(textRect, currentName, transparentTextFieldStyle);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(editor.customBaseTarget, "Change Slider Menu Name");
                        editor.customBaseTarget.slidersMenuName = newName;
                        
                        // Start debounce timer
                        lastMenuNameChangeTime = EditorApplication.timeSinceStartup;
                        hasPendingMenuNameUpdate = true;
                    }
                }

                if (showPendingApplyLabel)
                {
                    GUIStyle pendingStyle = new GUIStyle(EditorStyles.miniLabel);
                    pendingStyle.richText = true;
                    Rect pendingRect = new Rect(
                        compressionSectionBottomRect.x,
                        compressionSectionBottomRect.yMax + 5f,
                        compressionSectionBottomRect.width,
                        EditorGUIUtility.singleLineHeight
                    );
                    GUI.Label(pendingRect, $"<color=#888888>Applying with VRCFury in {timeRemaining:F0}s...</color>", pendingStyle);
                }
            }
        }
        EditorGUILayout.EndHorizontal();
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
