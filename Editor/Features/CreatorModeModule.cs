#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using AutocompleteSearchField;

// Handles all UI and logic for the Creator Mode feature.
public class CreatorModeModule
{
    private const double AnimationClipLookupCacheTtlSeconds = 2.0d;
    private static Dictionary<string, List<AnimationClip>> animationClipsByNameCache;
    private static double animationClipsByNameCacheTimestamp;
    private static bool animationClipProjectChangedHooked;

    private readonly MCBEditor editor;
    private readonly NetworkService networkService;
    private readonly FileManagerService fileManagerService;
    private ReorderableList blendshapeList;
    private AutocompleteSearchField.AutocompleteSearchField blendshapeSearchField;
    private readonly Dictionary<string, AutocompleteSearchField.AutocompleteSearchField> correctiveSearchFields
        = new Dictionary<string, AutocompleteSearchField.AutocompleteSearchField>();
    private readonly Dictionary<string, string> pendingCorrectiveValues = new Dictionary<string, string>();
    private readonly HashSet<string> activeCorrectiveFieldKeys = new HashSet<string>(StringComparer.Ordinal);
    private GUIStyle clippedMiniLabelStyle;
    private int cachedCustomFbxMeshId;
    private List<string> cachedCustomFbxBlendshapeNames = new List<string>();

    // UI State
    private bool creatorModeFoldout = true;
    private bool creatorBlendshapeEditorFoldout;
    private int newVersionMajor = 0, newVersionMinor = 1, newVersionPatch = 0;
    private Scope newVersionScope = Scope.BETA;
    private string newChangelog = "";

    private List<CustomBaseVersion> compatibleParentVersions;
    private List<string> parentVersionDisplayOptions;
    private string[] parentVersionDisplayOptionsArray = Array.Empty<string>();
    private int defaultParentIndex;
    private int selectedParentVersionIndex = -1;
    private CustomBaseVersion selectedParentVersionObject = null;
    private CustomBaseVersion previouslySelectedVersion = null;
    private List<CustomBaseVersion> lastServerVersionsRef;
    private int lastServerVersionsCount = -1;
    private string lastAppliedParentVersionKey;
    private const string CustomVeinsKey = "customVeins";
    private const string DynamicNormalBodyKey = "dynamicNormalBody";
    private const string DynamicNormalFlexingKey = "dynamicNormalFlexing";
    private string autoAssignedVeinsTexturePath;
    private CustomBaseVersion lastParentVersionForVeins;
    private bool isRestoringFromVersionState;
    private const float BlendshapeNameColumn = 0.35f;
    private const float BlendshapeValueColumn = 0.16f;
    private const float BlendshapeSliderColumn = 0.10f;
    private const float BlendshapeDefaultColumn = 0.10f;
    
    public CreatorModeModule(MCBEditor editor)
    {
        this.editor = editor;
        this.networkService = new NetworkService();
        this.fileManagerService = new FileManagerService();
        EnsureAnimationClipProjectChangedHook();
        creatorBlendshapeEditorFoldout = EditorPrefs.GetBool("MCB_CreatorMode_BlendshapeEditorFoldout", false);
    }

    public void Initialize()
    {
        blendshapeList = new ReorderableList(editor.serializedObject, editor.customBlendshapesForCreatorProp, true, true, false, true);
        
        blendshapeList.drawHeaderCallback = (Rect rect) =>
        {
            float nameWidth = rect.width * BlendshapeNameColumn;
            float defaultValWidth = rect.width * BlendshapeValueColumn;
            float sliderWidth = rect.width * BlendshapeSliderColumn;
            float isDefaultWidth = rect.width * BlendshapeDefaultColumn;
            float correctiveWidth = rect.width - nameWidth - defaultValWidth - sliderWidth - isDefaultWidth;
            
            EditorGUI.LabelField(new Rect(rect.x, rect.y, nameWidth, rect.height), "Blendshape Name");
            EditorGUI.LabelField(new Rect(rect.x + nameWidth, rect.y, defaultValWidth, rect.height), "Value");
            EditorGUI.LabelField(new Rect(rect.x + nameWidth + defaultValWidth, rect.y, sliderWidth, rect.height), "Slider");
            EditorGUI.LabelField(new Rect(rect.x + nameWidth + defaultValWidth + sliderWidth, rect.y, isDefaultWidth, rect.height), "Default");
            EditorGUI.LabelField(new Rect(rect.x + nameWidth + defaultValWidth + sliderWidth + isDefaultWidth, rect.y, correctiveWidth, rect.height), "Correctives");
        };
        blendshapeList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
        {
            var element = blendshapeList.serializedProperty.GetArrayElementAtIndex(index);
            rect.y += 2;
            float spacing = 5f;
            float nameWidth = rect.width * BlendshapeNameColumn - spacing;
            float defaultValWidth = rect.width * BlendshapeValueColumn - spacing;
            float sliderWidth = rect.width * BlendshapeSliderColumn - spacing;
            float isDefaultWidth = rect.width * BlendshapeDefaultColumn - spacing;
            float correctiveWidth = rect.width - nameWidth - defaultValWidth - sliderWidth - isDefaultWidth - spacing * 4f;
            
            var nameProp = element.FindPropertyRelative("name");
            var defaultValueProp = element.FindPropertyRelative("defaultValue");
            var isSliderProp = element.FindPropertyRelative("isSlider");
            var isSliderDefaultProp = element.FindPropertyRelative("isSliderDefault");
            
            float currentX = rect.x;
            EditorGUI.PropertyField(new Rect(currentX, rect.y, nameWidth, EditorGUIUtility.singleLineHeight), nameProp, GUIContent.none);
            currentX += nameWidth + spacing;
            EditorGUI.PropertyField(new Rect(currentX, rect.y, defaultValWidth, EditorGUIUtility.singleLineHeight), defaultValueProp, GUIContent.none);
            currentX += defaultValWidth + spacing;
            
            isSliderProp.boolValue = EditorGUI.Toggle(new Rect(currentX + (sliderWidth - 16f) / 2f, rect.y, 16f, EditorGUIUtility.singleLineHeight), isSliderProp.boolValue);
            currentX += sliderWidth + spacing;

            using (new EditorGUI.DisabledScope(!isSliderProp.boolValue))
            {
                isSliderDefaultProp.boolValue = EditorGUI.Toggle(new Rect(currentX + (isDefaultWidth - 16f) / 2f, rect.y, 16f, EditorGUIUtility.singleLineHeight), isSliderDefaultProp.boolValue);
                if (!isSliderProp.boolValue) isSliderDefaultProp.boolValue = false;
            }
            currentX += isDefaultWidth + spacing;

            if (GUI.Button(new Rect(currentX, rect.y, correctiveWidth, EditorGUIUtility.singleLineHeight), "add fixes"))
            {
                var correctiveProp = element.FindPropertyRelative("correctiveBlendshapes");
                AddCorrectivePair(correctiveProp);
                editor.serializedObject.ApplyModifiedProperties();
            }
        };
        
        // Initialize the autocomplete search field for blendshapes
        blendshapeSearchField = new AutocompleteSearchField.AutocompleteSearchField();
        blendshapeSearchField.onInputChanged = OnBlendshapeSearchInputChanged;
        blendshapeSearchField.onConfirm = OnBlendshapeSearchConfirm;
    }

    public void Draw()
    {
        EditorGUILayout.PropertyField(editor.isCreatorModeProp, new GUIContent("Enable Creator Mode"));
        if (!editor.isCreatorModeProp.boolValue) return;

        creatorModeFoldout = EditorGUILayout.Foldout(creatorModeFoldout, "Creator Mode", true, EditorStyles.foldoutHeader);
        if (!creatorModeFoldout) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        using (new EditorGUI.DisabledScope(editor.isSubmitting))
        {
            PopulateParentVersionDropdown();
            
            // Auto-populate fields if an unsubmitted version is selected
            if (editor.selectedVersionForAction != previouslySelectedVersion)
            {
                if (editor.selectedVersionForAction != null && editor.selectedVersionForAction.isUnsubmitted)
                {
                    PopulateFieldsFromVersion(editor.selectedVersionForAction);
                }
                previouslySelectedVersion = editor.selectedVersionForAction;
            }
            
            DrawParentVersionDropdown();
            DrawParentBlendshapeImportControls();
            
            EditorGUILayout.PropertyField(editor.customFbxForCreatorProp, new GUIContent("Custom FBX (Transformed)"));
            EditorGUILayout.PropertyField(editor.customBaseAvatarForCreatorProp, new GUIContent("Custom Base Avatar (Transformed)"));
            EditorGUILayout.PropertyField(editor.avatarLogicPrefabProp, new GUIContent("Avatar Logic Prefab"));
            DrawCustomVeinsSection();
            DrawDynamicNormalsSection();
            
            EditorGUILayout.Space();
            
            // vertical group with helpbox style
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            creatorBlendshapeEditorFoldout = EditorGUILayout.Foldout(
                creatorBlendshapeEditorFoldout,
                "Blendshape And Correctives Editor",
                true);
            EditorPrefs.SetBool("MCB_CreatorMode_BlendshapeEditorFoldout", creatorBlendshapeEditorFoldout);

            if (creatorBlendshapeEditorFoldout)
            {
                blendshapeList.DoLayoutList();

                if (blendshapeSearchField != null)
                {
                    EditorGUILayout.LabelField("Search and Add Blendshapes:", EditorStyles.miniBoldLabel);
                    blendshapeSearchField.OnGUI();
                }

                DrawBlendshapeCorrectivesSection();
            }
            
            EditorGUILayout.EndVertical();  
            
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("New Version Details:", EditorStyles.miniBoldLabel);
            DrawVersionFields();
            
            string newVersionString = $"{newVersionMajor}.{newVersionMinor}.{newVersionPatch}";
            bool isVersionValid = IsNewVersionValid(newVersionString);
            if (!isVersionValid)
            {
                string baseVer = selectedParentVersionObject?.version ?? "the base version";
                EditorGUILayout.HelpBox($"New version must be higher than the selected parent version (v{baseVer}).", MessageType.Warning);
            }

            EditorGUILayout.LabelField("Changelog:", EditorStyles.miniBoldLabel);
            newChangelog = EditorGUILayout.TextArea(newChangelog, GUILayout.Height(80));

            EditorGUILayout.Space();
            bool canSubmit = editor.customFbxForCreatorProp.objectReferenceValue != null &&
                             editor.avatarLogicPrefabProp.objectReferenceValue != null &&
                             editor.customBaseAvatarForCreatorProp.objectReferenceValue != null &&
                             selectedParentVersionObject != null &&
                             isVersionValid;
            if (editor.includeCustomVeinsForCreatorProp.boolValue && editor.customVeinsNormalMapProp.objectReferenceValue == null)
            {
                canSubmit = false;
            }
            
            using (new EditorGUI.DisabledScope(!canSubmit))
            {
                EditorGUILayout.BeginHorizontal();
                
                // Test Button
                if (GUILayout.Button(editor.isSubmitting ? "Building..." : "Test", GUILayout.Height(30)))
                {
                    if (EditorUtility.DisplayDialog("Confirm Test Build", "This will create and apply the new version locally without uploading it. The original FBX will be backed up.", "Build and Test", "Cancel"))
                    {
                        EditorCoroutineUtility.StartCoroutineOwnerless(BuildAndApplyLocalVersionCoroutine());
                    }
                }
                
                // Submit Button
                GUI.backgroundColor = Color.green;
                if (GUILayout.Button(editor.isSubmitting ? "Submitting..." : "Submit New Version", GUILayout.Height(30)))
                {
                    string parentInfo = selectedParentVersionObject != null ? $"Parent: {selectedParentVersionObject.version}\n" : "";
                    if (EditorUtility.DisplayDialog("Confirm Upload", $"This will create and upload the new version files.\n\nVersion: {newVersionString} ({newVersionScope})\n{parentInfo}This action is irreversible.", "Upload", "Cancel"))
                    {
                        EditorCoroutineUtility.StartCoroutineOwnerless(SubmitNewVersionCoroutine());
                    }
                }
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();
            }
        }
        
        if (!string.IsNullOrEmpty(editor.submitError))
        {
            EditorGUILayout.HelpBox("Submission Error: " + editor.submitError, MessageType.Error);
        }
        
        EditorGUILayout.EndVertical();
    }
    

    private void OnBlendshapeSearchInputChanged(string searchString)
    {
        blendshapeSearchField.ClearResults();
        
        if (string.IsNullOrEmpty(searchString))
            return;
        
        var allBlendshapes = GetAllBlendshapeNamesFromCustomFbx();
        var existingBlendshapeNames = editor.customBaseTarget.customBlendshapesForCreator.Select(e => e.name);
        var availableBlendshapes = allBlendshapes.Except(existingBlendshapeNames).OrderBy(s => s);

        // Filter blendshapes that contain the search string (case-insensitive)
        foreach (var shapeName in availableBlendshapes)
        {
            if (shapeName.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                blendshapeSearchField.AddResult(shapeName);
            }
        }
    }

    private void OnBlendshapeSearchConfirm(string selectedBlendshape)
    {
        if (string.IsNullOrEmpty(selectedBlendshape))
            return;

        // Add the selected blendshape to the list
        var prop = editor.customBlendshapesForCreatorProp;
        int index = prop.arraySize;
        prop.arraySize++;
        var element = prop.GetArrayElementAtIndex(index);
        element.FindPropertyRelative("name").stringValue = selectedBlendshape;
        element.FindPropertyRelative("defaultValue").stringValue = "0";
        element.FindPropertyRelative("isSlider").boolValue = false;
        element.FindPropertyRelative("isSliderDefault").boolValue = false;
        element.FindPropertyRelative("correctiveBlendshapes").arraySize = 0;
        editor.serializedObject.ApplyModifiedProperties();

        // Clear the search field
        blendshapeSearchField.searchString = "";
        blendshapeSearchField.ClearResults();
    }

    private void DrawBlendshapeCorrectivesSection()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Blendshape Correctives", EditorStyles.miniBoldLabel);
        activeCorrectiveFieldKeys.Clear();

        var blendshapesProp = editor.customBlendshapesForCreatorProp;
        if (blendshapesProp == null || blendshapesProp.arraySize == 0)
        {
            ClearAllCorrectiveSearchFields();
            EditorGUILayout.HelpBox("Add blendshapes above to create corrective links.", MessageType.None);
            return;
        }

        bool drewOne = false;
        for (int i = 0; i < blendshapesProp.arraySize; i++)
        {
            var blendshapeElement = blendshapesProp.GetArrayElementAtIndex(i);
            var correctiveProp = blendshapeElement.FindPropertyRelative("correctiveBlendshapes");
            if (correctiveProp == null || correctiveProp.arraySize == 0) continue;

            drewOne = true;
            string title = blendshapeElement.FindPropertyRelative("name").stringValue;
            if (string.IsNullOrWhiteSpace(title)) title = "(Unnamed Blendshape)";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            if (GUILayout.Button("Delete", GUILayout.Width(70f)))
            {
                correctiveProp.arraySize = 0;
                editor.serializedObject.ApplyModifiedProperties();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                continue;
            }
            EditorGUILayout.EndHorizontal();

            for (int j = 0; j < correctiveProp.arraySize; j++)
            {
                var item = correctiveProp.GetArrayElementAtIndex(j);
                var toFixTypeProp = item.FindPropertyRelative("toFixType");
                var toFixProp = item.FindPropertyRelative("toFix");
                var fixedByTypeProp = item.FindPropertyRelative("fixedByType");
                var fixedByProp = item.FindPropertyRelative("fixedBy");
                string keyPrefix = $"corrective_{i}_{j}";
                activeCorrectiveFieldKeys.Add(keyPrefix + "_toFix");
                activeCorrectiveFieldKeys.Add(keyPrefix + "_fixing");
                float indentPadding = EditorGUI.indentLevel * 15f;
                float availableWidth = EditorGUIUtility.currentViewWidth - indentPadding - 110f;
                float fieldWidth = Mathf.Clamp((availableWidth - 28f - 4f) * 0.5f, 95f, 260f);

                EditorGUILayout.BeginHorizontal();
                DrawCorrectiveTargetField(
                    toFixTypeProp,
                    toFixProp,
                    keyPrefix + "_toFix",
                    "to fix",
                    true,
                    GUILayout.Width(fieldWidth)
                );
                GUILayout.Space(4f);
                DrawCorrectiveTargetField(
                    fixedByTypeProp,
                    fixedByProp,
                    keyPrefix + "_fixing",
                    "fixing",
                    false,
                    GUILayout.Width(fieldWidth)
                );
                if (GUILayout.Button("-", GUILayout.Width(24f)))
                {
                    correctiveProp.DeleteArrayElementAtIndex(j);
                    editor.serializedObject.ApplyModifiedProperties();
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Add", GUILayout.Width(80f)))
            {
                AddCorrectivePair(correctiveProp);
                editor.serializedObject.ApplyModifiedProperties();
            }

            EditorGUILayout.EndVertical();
        }

        PruneStaleCorrectiveSearchFields();

        if (!drewOne)
        {
            EditorGUILayout.HelpBox("Use 'add corrective blendshapes' in the table to create entries.", MessageType.None);
        }

        EditorGUILayout.HelpBox(
            "The fixing target activation is proportional to the fixed blendshape and the blendshape needing correction.",
            MessageType.Info
        );
    }

    private void DrawCorrectiveTargetField(
        SerializedProperty typeProp,
        SerializedProperty stringProp,
        string fieldKey,
        string labelSuffix,
        bool typeBeforeLabel,
        params GUILayoutOption[] layout
    )
    {
        EditorGUILayout.BeginVertical(layout);
        var clippedMiniLabel = GetClippedMiniLabelStyle();

        CorrectiveActivationType currentType = CorrectiveActivationType.Blendshape;
        if (typeProp != null)
        {
            typeProp.enumValueIndex = Mathf.Clamp(typeProp.enumValueIndex, 0, Enum.GetValues(typeof(CorrectiveActivationType)).Length - 1);
            currentType = (CorrectiveActivationType)typeProp.enumValueIndex;
        }

        EditorGUILayout.BeginHorizontal();
        if (typeBeforeLabel)
        {
            var newType = (CorrectiveActivationType)EditorGUILayout.EnumPopup(currentType, GUILayout.Width(92f));
            EditorGUILayout.LabelField(labelSuffix, clippedMiniLabel);
            currentType = ApplyCorrectiveTypeChange(typeProp, stringProp, fieldKey, currentType, newType);
        }
        else
        {
            EditorGUILayout.LabelField(labelSuffix, clippedMiniLabel, GUILayout.Width(42f));
            var newType = (CorrectiveActivationType)EditorGUILayout.EnumPopup(currentType, GUILayout.Width(92f));
            currentType = ApplyCorrectiveTypeChange(typeProp, stringProp, fieldKey, currentType, newType);
        }
        EditorGUILayout.EndHorizontal();

        if (currentType == CorrectiveActivationType.Blendshape)
        {
            var searchField = GetOrCreateCorrectiveSearchField(fieldKey);
            string currentValue = stringProp != null ? (stringProp.stringValue ?? string.Empty) : string.Empty;
            if (searchField.searchString != currentValue)
            {
                searchField.searchString = currentValue;
            }

            searchField.OnGUI();

            if (pendingCorrectiveValues.TryGetValue(fieldKey, out var pending))
            {
                if (stringProp != null) stringProp.stringValue = pending ?? string.Empty;
                pendingCorrectiveValues.Remove(fieldKey);
            }
        }
        else
        {
            DrawAnimationClipNameField(stringProp);
        }

        EditorGUILayout.EndVertical();
    }

    private CorrectiveActivationType ApplyCorrectiveTypeChange(
        SerializedProperty typeProp,
        SerializedProperty stringProp,
        string fieldKey,
        CorrectiveActivationType currentType,
        CorrectiveActivationType newType)
    {
        if (typeProp == null || newType == currentType) return currentType;

        typeProp.enumValueIndex = (int)newType;
        if (stringProp != null)
        {
            stringProp.stringValue = string.Empty;
        }
        pendingCorrectiveValues.Remove(fieldKey);
        if (correctiveSearchFields.TryGetValue(fieldKey, out var existing))
        {
            existing.searchString = string.Empty;
            existing.ClearResults();
        }
        return newType;
    }

    private static void DrawAnimationClipNameField(SerializedProperty stringProp)
    {
        string currentName = stringProp != null ? (stringProp.stringValue ?? string.Empty) : string.Empty;
        var currentClip = FindAnimationClipsByName(currentName).FirstOrDefault();
        EditorGUILayout.BeginHorizontal();
        string nextName = EditorGUILayout.DelayedTextField(currentName);
        var selectedClip = EditorGUILayout.ObjectField(currentClip, typeof(AnimationClip), false, GUILayout.Width(130f)) as AnimationClip;
        EditorGUILayout.EndHorizontal();

        if (stringProp == null) return;

        if (selectedClip != null)
        {
            stringProp.stringValue = selectedClip != null ? selectedClip.name : string.Empty;
            return;
        }

        if (!string.Equals(nextName, currentName, StringComparison.Ordinal))
        {
            stringProp.stringValue = nextName ?? string.Empty;
        }
    }

    private void DrawParentBlendshapeImportControls()
    {
        if (selectedParentVersionObject == null || selectedParentVersionObject.customBlendshapes == null ||
            selectedParentVersionObject.customBlendshapes.Length == 0)
        {
            return;
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(EditorGUI.indentLevel * 15f);
        if (GUILayout.Button(
                new GUIContent("Import Parent Blendshapes",
                    "Copy parent version blendshapes and correctives into Creator Mode."),
                GUILayout.Width(220f)))
        {
            ImportBlendshapesFromParent(selectedParentVersionObject);
        }

        EditorGUILayout.EndHorizontal();
    }

    private static List<AnimationClip> FindAnimationClipsByName(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return new List<AnimationClip>();
        var lookup = GetAnimationClipsByNameLookupCached();
        return lookup.TryGetValue(clipName, out var clips) && clips != null
            ? clips
            : new List<AnimationClip>();
    }

    private static List<string> CollectAnimationAssetPathsFromFixedBy(IEnumerable<CustomBlendshapeEntry> customBlendshapeEntries)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (customBlendshapeEntries == null) return paths.ToList();

        foreach (var blendshape in customBlendshapeEntries)
        {
            if (blendshape?.correctiveBlendshapes == null) continue;
            foreach (var corrective in blendshape.correctiveBlendshapes)
            {
                if (corrective == null) continue;
                if (corrective.fixedByType != CorrectiveActivationType.Animation) continue;
                if (string.IsNullOrWhiteSpace(corrective.fixedBy)) continue;

                foreach (var clip in FindAnimationClipsByName(corrective.fixedBy))
                {
                    if (clip == null) continue;
                    string assetPath = AssetDatabase.GetAssetPath(clip);
                    if (string.IsNullOrWhiteSpace(assetPath)) continue;
                    paths.Add(assetPath);
                }
            }
        }

        return paths.ToList();
    }

    private AutocompleteSearchField.AutocompleteSearchField GetOrCreateCorrectiveSearchField(string fieldKey)
    {
        if (correctiveSearchFields.TryGetValue(fieldKey, out var existing))
        {
            return existing;
        }

        var field = new AutocompleteSearchField.AutocompleteSearchField();
        field.onInputChanged = input =>
        {
            pendingCorrectiveValues[fieldKey] = input ?? string.Empty;
            field.ClearResults();
            if (string.IsNullOrWhiteSpace(input)) return;

            foreach (var name in GetAllBlendshapeNamesFromCustomFbx())
            {
                if (name.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    field.AddResult(name);
                }
            }
        };
        field.onConfirm = selected =>
        {
            pendingCorrectiveValues[fieldKey] = selected ?? string.Empty;
        };

        correctiveSearchFields[fieldKey] = field;
        return field;
    }

    private GUIStyle GetClippedMiniLabelStyle()
    {
        if (clippedMiniLabelStyle != null) return clippedMiniLabelStyle;
        clippedMiniLabelStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            clipping = TextClipping.Clip,
            wordWrap = false
        };
        return clippedMiniLabelStyle;
    }

    private void PruneStaleCorrectiveSearchFields()
    {
        if (correctiveSearchFields.Count == 0 && pendingCorrectiveValues.Count == 0) return;

        var keysToRemove = new List<string>();
        foreach (var key in correctiveSearchFields.Keys)
        {
            if (!activeCorrectiveFieldKeys.Contains(key))
            {
                keysToRemove.Add(key);
            }
        }

        for (int i = 0; i < keysToRemove.Count; i++)
        {
            correctiveSearchFields.Remove(keysToRemove[i]);
            pendingCorrectiveValues.Remove(keysToRemove[i]);
        }
    }

    private void ClearAllCorrectiveSearchFields()
    {
        if (correctiveSearchFields.Count > 0) correctiveSearchFields.Clear();
        if (pendingCorrectiveValues.Count > 0) pendingCorrectiveValues.Clear();
    }

    private IEnumerable<string> GetAllBlendshapeNamesFromCustomFbx()
    {
        var fbxObject = editor.customFbxForCreatorProp.objectReferenceValue as GameObject;
        if (fbxObject == null)
        {
            cachedCustomFbxMeshId = 0;
            cachedCustomFbxBlendshapeNames.Clear();
            return cachedCustomFbxBlendshapeNames;
        }

        var smr = fbxObject.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr == null || smr.sharedMesh == null)
        {
            cachedCustomFbxMeshId = 0;
            cachedCustomFbxBlendshapeNames.Clear();
            return cachedCustomFbxBlendshapeNames;
        }

        int meshId = smr.sharedMesh.GetInstanceID();
        if (meshId == cachedCustomFbxMeshId)
        {
            return cachedCustomFbxBlendshapeNames;
        }

        var names = new List<string>(smr.sharedMesh.blendShapeCount);
        for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
        {
            string name = smr.sharedMesh.GetBlendShapeName(i);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        names = names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        cachedCustomFbxBlendshapeNames = names;
        cachedCustomFbxMeshId = meshId;
        return cachedCustomFbxBlendshapeNames;
    }

    private static Dictionary<string, List<AnimationClip>> GetAnimationClipsByNameLookupCached()
    {
        EnsureAnimationClipProjectChangedHook();

        double now = EditorApplication.timeSinceStartup;
        if (animationClipsByNameCache != null &&
            (now - animationClipsByNameCacheTimestamp) <= AnimationClipLookupCacheTtlSeconds)
        {
            return animationClipsByNameCache;
        }

        var lookup = new Dictionary<string, List<AnimationClip>>(StringComparer.Ordinal);
        string[] guids = AssetDatabase.FindAssets("t:AnimationClip");
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null || string.IsNullOrWhiteSpace(clip.name)) continue;

            if (!lookup.TryGetValue(clip.name, out var list))
            {
                list = new List<AnimationClip>();
                lookup[clip.name] = list;
            }

            list.Add(clip);
        }

        foreach (var pair in lookup)
        {
            pair.Value.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
        }

        animationClipsByNameCache = lookup;
        animationClipsByNameCacheTimestamp = now;
        return animationClipsByNameCache;
    }

    private static void EnsureAnimationClipProjectChangedHook()
    {
        if (animationClipProjectChangedHooked) return;
        animationClipProjectChangedHooked = true;
        EditorApplication.projectChanged += ClearAnimationClipLookupCache;
    }

    private static void ClearAnimationClipLookupCache()
    {
        animationClipsByNameCache = null;
        animationClipsByNameCacheTimestamp = 0d;
    }

    private static void AddCorrectivePair(
        SerializedProperty correctiveProp,
        CorrectiveActivationType toFixType = CorrectiveActivationType.Blendshape,
        string toFix = "",
        CorrectiveActivationType fixedByType = CorrectiveActivationType.Blendshape,
        string fixedBy = "")
    {
        if (correctiveProp == null) return;
        int i = correctiveProp.arraySize;
        correctiveProp.InsertArrayElementAtIndex(i);
        var row = correctiveProp.GetArrayElementAtIndex(i);
        row.FindPropertyRelative("toFixType").enumValueIndex = (int)toFixType;
        row.FindPropertyRelative("toFix").stringValue = toFix ?? string.Empty;
        row.FindPropertyRelative("fixedByType").enumValueIndex = (int)fixedByType;
        row.FindPropertyRelative("fixedBy").stringValue = fixedBy ?? string.Empty;
    }

    private void DrawParentVersionDropdown()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Parent Version:", GUILayout.Width(100));

        if (parentVersionDisplayOptions == null || parentVersionDisplayOptions.Count == 0)
        {
            EditorGUILayout.Popup(0, Array.Empty<string>());
            EditorGUILayout.EndHorizontal();
            HandleParentVersionChanged(null);
            return;
        }

        int currentParentPopupIndex = (selectedParentVersionIndex == -1) ? defaultParentIndex : selectedParentVersionIndex;

        EditorGUI.BeginChangeCheck();
        int newParentIndex = EditorGUILayout.Popup(currentParentPopupIndex, parentVersionDisplayOptionsArray);
        if (EditorGUI.EndChangeCheck())
        {
            selectedParentVersionIndex = newParentIndex;
            if (selectedParentVersionIndex >= 0 && selectedParentVersionIndex < compatibleParentVersions.Count)
            {
                selectedParentVersionObject = compatibleParentVersions[selectedParentVersionIndex];
                SetDefaultVersionNumbers(selectedParentVersionObject);
            }
            else
            {
                selectedParentVersionObject = null;
                SetDefaultVersionNumbers(null);
            }
        }
        else if (selectedParentVersionIndex == -1 && defaultParentIndex != -1)
        {
            // Initialize on first draw only
            selectedParentVersionIndex = defaultParentIndex;
            selectedParentVersionObject = compatibleParentVersions[defaultParentIndex];
            SetDefaultVersionNumbers(selectedParentVersionObject);
        }
        EditorGUILayout.EndHorizontal();
        HandleParentVersionChanged(selectedParentVersionObject);
    }

    private void DrawCustomVeinsSection()
    {
        var includeProp = editor.includeCustomVeinsForCreatorProp;
        var textureProp = editor.customVeinsNormalMapProp;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUI.BeginChangeCheck();
        bool includeVeins = EditorGUILayout.ToggleLeft(new GUIContent("Include custom veins"), includeProp.boolValue);
        if (EditorGUI.EndChangeCheck())
        {
            includeProp.boolValue = includeVeins;
            if (!includeVeins)
            {
                textureProp.objectReferenceValue = null;
                autoAssignedVeinsTexturePath = null;
            }
        }

        if (includeProp.boolValue)
        {
            DrawVeinsTextureField(textureProp);
            if (textureProp.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("Assign a normal map texture to include custom veins.", MessageType.Warning);
            }
            else
            {
                DrawCustomVeinsImportWarning(textureProp.objectReferenceValue as Texture2D);
            }
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawDynamicNormalsSection()
    {
        var includeBodyProp = editor.includeDynamicNormalsBodyForCreatorProp;
        var includeFlexingProp = editor.includeDynamicNormalsFlexingForCreatorProp;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // Enable dynamic normals for body checkbox
        EditorGUI.BeginChangeCheck();
        bool includeBody = EditorGUILayout.ToggleLeft(new GUIContent("Enable dynamic normals for body"), includeBodyProp.boolValue);
        if (EditorGUI.EndChangeCheck())
        {
            includeBodyProp.boolValue = includeBody;
        }

        // Info bubble under first checkbox
        EditorGUILayout.HelpBox("Don't use if muscles normal is already baked in the mesh.\nWill apply to all blendshapes containing \"muscle\" in the name.", MessageType.Info);

        EditorGUILayout.Space(5);

        // Enable dynamic normals for flexings checkbox
        EditorGUI.BeginChangeCheck();
        bool includeFlexing = EditorGUILayout.ToggleLeft(new GUIContent("Enable dynamic normals for flexings"), includeFlexingProp.boolValue);
        if (EditorGUI.EndChangeCheck())
        {
            includeFlexingProp.boolValue = includeFlexing;
        }

        // Info bubble under second checkbox
        EditorGUILayout.HelpBox("Allows for the flexed muscles to be more visible.\nWill apply to all blendshapes containing \"flex\" in the name.", MessageType.Info);

        EditorGUILayout.EndVertical();
    }

    private void DrawVeinsTextureField(SerializedProperty textureProp)
    {
        UnityEngine.Object currentTexture = textureProp.objectReferenceValue;
        EditorGUI.indentLevel++;
        EditorGUIUtility.SetIconSize(new Vector2(64f, 64f));
        Texture2D newTexture = (Texture2D)EditorGUILayout.ObjectField(new GUIContent("Custom veins", "Normal map applied to the veins detail layer."), currentTexture, typeof(Texture2D), false, GUILayout.Height(64f));
        EditorGUIUtility.SetIconSize(Vector2.zero);
        EditorGUI.indentLevel--;

        if (!Equals(newTexture, currentTexture))
        {
            textureProp.objectReferenceValue = newTexture;
            string selectedPath = AssetDatabase.GetAssetPath(newTexture);
            string parentPath = GetVeinsTexturePathForVersion(selectedParentVersionObject);
            autoAssignedVeinsTexturePath = (!string.IsNullOrEmpty(selectedPath) && selectedPath == parentPath) ? selectedPath : null;
        }
    }

    private void DrawCustomVeinsImportWarning(Texture2D texture)
    {
        if (texture == null) return;

        string assetPath = AssetDatabase.GetAssetPath(texture);
        if (string.IsNullOrWhiteSpace(assetPath)) return;

        if (TryGetCustomVeinsTextureImporter(assetPath, out var importer) && IsValidCustomVeinsNormalMapImport(importer))
        {
            return;
        }

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.HelpBox("This texture is not marked as a normal map", MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Fix Now", GUILayout.Width(90f)))
            {
                FixCustomVeinsNormalMapImport(assetPath);
            }
        }

        EditorGUILayout.EndVertical();
    }

    private static bool TryGetCustomVeinsTextureImporter(string assetPath, out TextureImporter importer)
    {
        importer = null;
        if (string.IsNullOrWhiteSpace(assetPath)) return false;

        importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        return importer != null;
    }

    private static bool IsValidCustomVeinsNormalMapImport(TextureImporter importer)
    {
        if (importer == null) return false;

        return importer.textureType == TextureImporterType.NormalMap &&
               importer.mipmapEnabled &&
               !importer.sRGBTexture &&
               !importer.convertToNormalmap &&
               !importer.isReadable &&
               !importer.streamingMipmaps &&
               importer.textureShape == TextureImporterShape.Texture2D &&
               importer.alphaSource == TextureImporterAlphaSource.FromInput &&
               !importer.alphaIsTransparency;
    }

    private static void FixCustomVeinsNormalMapImport(string assetPath)
    {
        if (!TryGetCustomVeinsTextureImporter(assetPath, out var importer))
        {
            EditorUtility.DisplayDialog("MCB", "Could not find a texture importer for the selected file.", "OK");
            return;
        }

        importer.textureType = TextureImporterType.NormalMap;
        importer.mipmapEnabled = true;
        importer.sRGBTexture = false;
        importer.convertToNormalmap = false;
        importer.isReadable = false;
        importer.streamingMipmaps = false;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = false;
        importer.npotScale = TextureImporterNPOTScale.ToNearest;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Bilinear;
        importer.anisoLevel = 1;

        importer.SaveAndReimport();
    }

    private static string GetVeinsTexturePathForVersion(CustomBaseVersion version)
    {
        if (version == null) return null;
        string folder = MCBUtils.GetVersionDataPath(version);
        if (string.IsNullOrEmpty(folder)) return null;
        return Path.Combine(folder, "veins normal.png").Replace("\\", "/");
    }

    private bool ParentSupportsCustomVeins(CustomBaseVersion version)
    {
        return version?.extraCustomization != null && version.extraCustomization.Contains(CustomVeinsKey);
    }

    private void HandleParentVersionChanged(CustomBaseVersion newParent)
    {
        if (ReferenceEquals(lastParentVersionForVeins, newParent) && !isRestoringFromVersionState)
        {
            return;
        }

        lastParentVersionForVeins = newParent;

        var includeProp = editor.includeCustomVeinsForCreatorProp;
        var textureProp = editor.customVeinsNormalMapProp;

        // Only auto-populate if parent supports custom veins
        if (ParentSupportsCustomVeins(newParent))
        {
            // Auto-check the checkbox if not already checked (but only when not restoring from saved state)
            if (!isRestoringFromVersionState && !includeProp.boolValue)
            {
                includeProp.boolValue = true;
            }

            string parentTexturePath = GetVeinsTexturePathForVersion(newParent);
            if (!string.IsNullOrEmpty(parentTexturePath))
            {
                Texture2D parentTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(parentTexturePath);
                if (parentTexture != null)
                {
                    string currentTexturePath = AssetDatabase.GetAssetPath(textureProp.objectReferenceValue);
                    bool wasAutoAssigned = !string.IsNullOrEmpty(autoAssignedVeinsTexturePath) && autoAssignedVeinsTexturePath == currentTexturePath;

                    // Auto-assign the texture if empty or was previously auto-assigned
                    if (textureProp.objectReferenceValue == null || wasAutoAssigned)
                    {
                        textureProp.objectReferenceValue = parentTexture;
                        autoAssignedVeinsTexturePath = parentTexturePath;
                    }
                }
            }
        }
        // If parent doesn't support custom veins, don't uncheck or clear - just leave it as is
        
        // Auto-populate dynamic normals checkboxes from parent version
        var includeBodyProp = editor.includeDynamicNormalsBodyForCreatorProp;
        var includeFlexingProp = editor.includeDynamicNormalsFlexingForCreatorProp;
        
        if (newParent?.extraCustomization != null && !isRestoringFromVersionState)
        {
            bool parentHasBody = newParent.extraCustomization.Contains(DynamicNormalBodyKey);
            bool parentHasFlexing = newParent.extraCustomization.Contains(DynamicNormalFlexingKey);
            
            // Auto-check the checkboxes if parent has the features
            if (parentHasBody && !includeBodyProp.boolValue)
            {
                includeBodyProp.boolValue = true;
            }
            if (parentHasFlexing && !includeFlexingProp.boolValue)
            {
                includeFlexingProp.boolValue = true;
            }
        }
        
        // Blendshape/corrective import is now explicit via UI button to avoid long UI stalls on toggle.
    }

    private void ImportBlendshapesFromParent(CustomBaseVersion newParent)
    {
        if (newParent == null || newParent.customBlendshapes == null) return;

        var imported = new List<CreatorBlendshapeEntry>(newParent.customBlendshapes.Length);
        foreach (var src in newParent.customBlendshapes)
        {
            if (src == null) continue;
            var dst = new CreatorBlendshapeEntry
            {
                name = src.name,
                defaultValue = src.defaultValue,
                isSlider = src.isSlider,
                isSliderDefault = src.isSliderDefault,
                correctiveBlendshapes = new List<CreatorCorrectiveBlendshapeEntry>()
            };

            if (src.correctiveBlendshapes != null)
            {
                for (int i = 0; i < src.correctiveBlendshapes.Length; i++)
                {
                    var c = src.correctiveBlendshapes[i];
                    if (c == null) continue;
                    dst.correctiveBlendshapes.Add(new CreatorCorrectiveBlendshapeEntry
                    {
                        toFixType = c.toFixType,
                        toFix = c.toFix,
                        fixedByType = c.fixedByType,
                        fixedBy = c.fixedBy
                    });
                }
            }

            imported.Add(dst);
        }

        Undo.RecordObject(editor.customBaseTarget, "Import Parent Blendshapes");
        editor.customBaseTarget.customBlendshapesForCreator = imported;
        EditorUtility.SetDirty(editor.customBaseTarget);
        editor.serializedObject.Update();
        ClearAllCorrectiveSearchFields();
    }

    private static void CopyCorrectivesToSerialized(SerializedProperty blendshapeElement, CorrectiveBlendshapeEntry[] source)
    {
        var correctiveProp = blendshapeElement.FindPropertyRelative("correctiveBlendshapes");
        if (correctiveProp == null) return;

        correctiveProp.arraySize = 0;
        if (source == null || source.Length == 0) return;

        foreach (var corrective in source)
        {
            if (corrective == null) continue;
            AddCorrectivePair(correctiveProp, corrective.toFixType, corrective.toFix, corrective.fixedByType, corrective.fixedBy);
        }
    }

    private void DrawVersionFields()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Version:", GUILayout.Width(50));
        newVersionMajor = EditorGUILayout.IntField(newVersionMajor, GUILayout.Width(40));
        EditorGUILayout.LabelField(".", GUILayout.Width(10));
        newVersionMinor = EditorGUILayout.IntField(newVersionMinor, GUILayout.Width(40));
        EditorGUILayout.LabelField(".", GUILayout.Width(10));
        newVersionPatch = EditorGUILayout.IntField(newVersionPatch, GUILayout.Width(40));
        newVersionScope = (Scope)EditorGUILayout.EnumPopup("", newVersionScope, GUILayout.Width(100));
        EditorGUILayout.EndHorizontal();
    }
    
    private void SetDefaultVersionNumbers(CustomBaseVersion baseVersionObject)
    {
        string currentVersionStr = baseVersionObject?.version;
        
        if (string.IsNullOrEmpty(currentVersionStr))
        {
            newVersionMajor = 0; newVersionMinor = 1; newVersionPatch = 0;
        }
        else
        {
            Version baseVersion = editor.ParseVersion(currentVersionStr);
            newVersionMajor = baseVersion.Major; 
            newVersionMinor = baseVersion.Minor; 
            newVersionPatch = baseVersion.Build + 1;
        }
    }
    
    private void PopulateParentVersionDropdown()
    {
        var applied = editor.customBaseTarget.appliedCustomBaseVersion;
        string appliedKey = applied != null ? (applied.version + "|" + applied.defaultAviVersion) : string.Empty;
        if (ReferenceEquals(lastServerVersionsRef, editor.serverVersions) &&
            lastServerVersionsCount == editor.serverVersions.Count &&
            string.Equals(lastAppliedParentVersionKey, appliedKey, StringComparison.Ordinal) &&
            compatibleParentVersions != null &&
            parentVersionDisplayOptions != null)
        {
            return;
        }

        compatibleParentVersions = editor.serverVersions.OrderByDescending(v => editor.ParseVersion(v.version)).ToList();
        parentVersionDisplayOptions = compatibleParentVersions.Select(v => $"{v.version} ({v.scope})").ToList();
        parentVersionDisplayOptionsArray = parentVersionDisplayOptions.ToArray();
        lastServerVersionsRef = editor.serverVersions;
        lastServerVersionsCount = editor.serverVersions.Count;
        lastAppliedParentVersionKey = appliedKey;

        if (applied != null)
        {
            defaultParentIndex = compatibleParentVersions.FindIndex(v => v.Equals(applied));
            if (defaultParentIndex == -1) defaultParentIndex = 0;
        }
        else { defaultParentIndex = 0; }
        
        if (selectedParentVersionIndex == -1 && defaultParentIndex < compatibleParentVersions.Count && defaultParentIndex >= 0)
        {
            selectedParentVersionObject = compatibleParentVersions[defaultParentIndex];
            SetDefaultVersionNumbers(selectedParentVersionObject);
        }
    }

    private bool IsNewVersionValid(string newVersionString)
    {
        string baseVersionString = selectedParentVersionObject?.version;
        if (string.IsNullOrEmpty(baseVersionString)) return true;
        return editor.CompareVersions(newVersionString, baseVersionString) > 0;
    }

    private (CustomBaseVersion metadata, string zipPath) BuildNewVersion()
    {
        var customFbxGO = editor.customFbxForCreatorProp.objectReferenceValue as GameObject;
        var customFbxPath = AssetDatabase.GetAssetPath(customFbxGO);
        var customBaseAvatar = editor.customBaseAvatarForCreatorProp.objectReferenceValue as Avatar;
        var logicPrefab = editor.avatarLogicPrefabProp.objectReferenceValue as GameObject;
        bool shouldIncludeCustomVeins = editor.includeCustomVeinsForCreatorProp.boolValue;
        var customVeinsTexture = editor.customVeinsNormalMapProp.objectReferenceValue as Texture2D;
        bool shouldIncludeDynamicNormalsBody = editor.includeDynamicNormalsBodyForCreatorProp.boolValue;
        bool shouldIncludeDynamicNormalsFlexing = editor.includeDynamicNormalsFlexingForCreatorProp.boolValue;

        if (shouldIncludeCustomVeins)
        {
            if (customVeinsTexture == null)
                throw new Exception("Custom veins is enabled but no normal map texture is assigned.");
        }

        string currentFbxPath = new VersionActions(editor, networkService, fileManagerService).GetCurrentFBXPath();
        string originalFbxPath = currentFbxPath + FileManagerService.OriginalSuffix;
        
        // If .old backup doesn't exist, use the current FBX (first-time submission case)
        if (!File.Exists(originalFbxPath))
        {
            originalFbxPath = currentFbxPath;
            if (!File.Exists(originalFbxPath))
                throw new Exception("FBX file not found. Cannot proceed with version creation.");
        }

        if (selectedParentVersionObject == null)
            throw new Exception("A Parent Version must be selected.");

        // Convert CreatorBlendshapeEntry list to CustomBlendshapeEntry array.
        var customBlendshapeEntries = editor.customBaseTarget.customBlendshapesForCreator
            .Select(entry =>
            {
                var corrective = (entry.correctiveBlendshapes ?? new List<CreatorCorrectiveBlendshapeEntry>())
                    .Where(c => c != null
                                && !string.IsNullOrWhiteSpace(c.toFix)
                                && !string.IsNullOrWhiteSpace(c.fixedBy))
                    .Select(c => new CorrectiveBlendshapeEntry
                    {
                        toFixType = c.toFixType,
                        toFix = c.toFix,
                        fixedByType = c.fixedByType,
                        fixedBy = c.fixedBy
                    })
                    .ToArray();

                return new CustomBlendshapeEntry
                {
                    name = entry.name,
                    defaultValue = entry.defaultValue,
                    isSlider = entry.isSlider,
                    isSliderDefault = entry.isSliderDefault,
                    correctiveBlendshapes = corrective.Length > 0 ? corrective : null
                };
            })
            .ToArray();

        var fixedByAnimationAssetPaths = CollectAnimationAssetPathsFromFixedBy(customBlendshapeEntries);
        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null || selectedAsset.id <= 0)
        {
            throw new InvalidOperationException("Select a custom base asset before building a version.");
        }
        int assetId = selectedAsset.id;

        string newVersionString = $"{newVersionMajor}.{newVersionMinor}.{newVersionPatch}";
        EditorUtility.DisplayProgressBar("Preparing Build", "Creating version package...", 0.2f);

        string tempZipPath = fileManagerService.CreateVersionPackageForUpload(
            assetId,
            newVersionString,
            selectedParentVersionObject.defaultAviVersion,
            originalFbxPath,
            customFbxGO,
            customBaseAvatar,
            logicPrefab,
            selectedParentVersionObject,
            shouldIncludeCustomVeins,
            customVeinsTexture,
            fixedByAnimationAssetPaths
        );

        EditorUtility.DisplayProgressBar("Preparing Build", "Calculating hashes and dependencies...", 0.5f);
        string binPath = MCBUtils.GetVersionBinPath(new CustomBaseVersion
        {
            assetId = assetId,
            version = newVersionString,
            defaultAviVersion = selectedParentVersionObject.defaultAviVersion
        });

        var extraCustomization = new List<string>();
        if (selectedParentVersionObject.extraCustomization != null)
        {
            extraCustomization.AddRange(selectedParentVersionObject.extraCustomization);
        }

        if (shouldIncludeCustomVeins)
        {
            if (!extraCustomization.Contains(CustomVeinsKey))
            {
                extraCustomization.Add(CustomVeinsKey);
            }
        }
        else
        {
            extraCustomization.RemoveAll(value => value == CustomVeinsKey);
        }

        // Handle dynamic normals for body
        if (shouldIncludeDynamicNormalsBody)
        {
            if (!extraCustomization.Contains(DynamicNormalBodyKey))
            {
                extraCustomization.Add(DynamicNormalBodyKey);
            }
        }
        else
        {
            extraCustomization.RemoveAll(value => value == DynamicNormalBodyKey);
        }

        // Handle dynamic normals for flexing
        if (shouldIncludeDynamicNormalsFlexing)
        {
            if (!extraCustomization.Contains(DynamicNormalFlexingKey))
            {
                extraCustomization.Add(DynamicNormalFlexingKey);
            }
        }
        else
        {
            extraCustomization.RemoveAll(value => value == DynamicNormalFlexingKey);
        }

        string customVeinsAssetPath = shouldIncludeCustomVeins ? AssetDatabase.GetAssetPath(customVeinsTexture) : null;

        var metadata = new CustomBaseVersion {
            assetId = assetId,
            version = newVersionString,
            scope = newVersionScope,
            changelog = newChangelog,
            defaultAviVersion = selectedParentVersionObject.defaultAviVersion,
            parentVersion = selectedParentVersionObject?.version,
            dependencies = fileManagerService.FindPrefabDependencies(logicPrefab),
            customBlendshapes = customBlendshapeEntries,
            extraCustomization = extraCustomization.Count > 0 ? extraCustomization.Distinct().ToArray() : null,
            includeCustomVeins = shouldIncludeCustomVeins ? true : (bool?)null,
            customVeinsTexturePath = customVeinsAssetPath,
            includeDynamicNormalsBody = shouldIncludeDynamicNormalsBody ? true : (bool?)null,
            includeDynamicNormalsFlexing = shouldIncludeDynamicNormalsFlexing ? true : (bool?)null,
            // Local-only data for repopulating fields
            baseFbxHash = fileManagerService.CalculateFileHash(originalFbxPath),
            customFbxPath = customFbxPath,
            customBaseAvatarPath = AssetDatabase.GetAssetPath(customBaseAvatar),
            logicPrefabPath = AssetDatabase.GetAssetPath(logicPrefab),
            // Hashes of created files
            customAviHash = fileManagerService.CalculateFileHash(binPath),
            appliedCustomAviHash = fileManagerService.CalculateFileHash(customFbxPath)
        };

        return (metadata, tempZipPath);
    }

    private void SaveUnsubmittedVersion(CustomBaseVersion versionToSave)
    {
        string path = MCBUtils.UNSUBMITTED_VERSIONS_FILE;
        List<CustomBaseVersion> unsubmitted = new List<CustomBaseVersion>();
        if (File.Exists(path))
        {
            unsubmitted = JsonConvert.DeserializeObject<List<CustomBaseVersion>>(File.ReadAllText(path)) ?? new List<CustomBaseVersion>();
        }

        int existingIndex = unsubmitted.FindIndex(v => v.Equals(versionToSave));
        if (existingIndex != -1) unsubmitted[existingIndex] = versionToSave;
        else unsubmitted.Add(versionToSave);

        MCBUtils.EnsureDirectoryExists(path);
        File.WriteAllText(path, JsonConvert.SerializeObject(unsubmitted, Formatting.Indented, new StringEnumConverter()));
        
        editor.LoadUnsubmittedVersions();
        editor.Repaint();
    }

    public void RemoveUnsubmittedVersion(CustomBaseVersion versionToRemove)
    {
        string path = MCBUtils.UNSUBMITTED_VERSIONS_FILE;
        if (!File.Exists(path)) return;
        
        List<CustomBaseVersion> unsubmitted = JsonConvert.DeserializeObject<List<CustomBaseVersion>>(File.ReadAllText(path)) ?? new List<CustomBaseVersion>();

        unsubmitted.RemoveAll(v => v.Equals(versionToRemove));

        File.WriteAllText(path, JsonConvert.SerializeObject(unsubmitted, Formatting.Indented, new StringEnumConverter()));
        
        editor.LoadUnsubmittedVersions();
        editor.Repaint();
    }

    private void PopulateFieldsFromVersion(CustomBaseVersion ver)
    {
        isRestoringFromVersionState = true;
        try
        {
            var parsedVersion = editor.ParseVersion(ver.version);
            newVersionMajor = parsedVersion.Major;
            newVersionMinor = parsedVersion.Minor;
            newVersionPatch = parsedVersion.Build;
            newVersionScope = ver.scope;
            newChangelog = ver.changelog;

            if (!string.IsNullOrEmpty(ver.parentVersion))
            {
                int parentIdx = compatibleParentVersions.FindIndex(p => p.version == ver.parentVersion);
                if (parentIdx != -1)
                {
                    selectedParentVersionIndex = parentIdx;
                    selectedParentVersionObject = compatibleParentVersions[parentIdx];
                    SetDefaultVersionNumbers(selectedParentVersionObject);
                }
                else
                {
                    selectedParentVersionIndex = -1;
                    selectedParentVersionObject = null;
                    SetDefaultVersionNumbers(null);
                }
            }
            else
            {
                selectedParentVersionIndex = -1;
                selectedParentVersionObject = null;
                SetDefaultVersionNumbers(null);
            }

            editor.customFbxForCreatorProp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ver.customFbxPath);
            editor.customBaseAvatarForCreatorProp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<Avatar>(ver.customBaseAvatarPath);
            editor.avatarLogicPrefabProp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(ver.logicPrefabPath);

            editor.customBlendshapesForCreatorProp.ClearArray();
            if (ver.customBlendshapes != null)
            {
                for (int i = 0; i < ver.customBlendshapes.Length; i++)
                {
                    editor.customBlendshapesForCreatorProp.InsertArrayElementAtIndex(i);
                    var element = editor.customBlendshapesForCreatorProp.GetArrayElementAtIndex(i);
                    element.FindPropertyRelative("name").stringValue = ver.customBlendshapes[i].name;
                    element.FindPropertyRelative("defaultValue").stringValue = ver.customBlendshapes[i].defaultValue;
                    element.FindPropertyRelative("isSlider").boolValue = ver.customBlendshapes[i].isSlider;
                    element.FindPropertyRelative("isSliderDefault").boolValue = ver.customBlendshapes[i].isSliderDefault;
                    CopyCorrectivesToSerialized(element, ver.customBlendshapes[i].correctiveBlendshapes);
                }
            }

            bool includeCustomVeins = ver.includeCustomVeins ?? (ver.extraCustomization != null && ver.extraCustomization.Contains(CustomVeinsKey));
            editor.includeCustomVeinsForCreatorProp.boolValue = includeCustomVeins;

            if (!string.IsNullOrEmpty(ver.customVeinsTexturePath))
            {
                editor.customVeinsNormalMapProp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>(ver.customVeinsTexturePath);
            }
            else
            {
                editor.customVeinsNormalMapProp.objectReferenceValue = null;
            }

            string assignedPath = AssetDatabase.GetAssetPath(editor.customVeinsNormalMapProp.objectReferenceValue);
            string parentPath = GetVeinsTexturePathForVersion(selectedParentVersionObject);
            autoAssignedVeinsTexturePath = (!string.IsNullOrEmpty(assignedPath) && assignedPath == parentPath) ? assignedPath : null;
            lastParentVersionForVeins = selectedParentVersionObject;

            editor.serializedObject.ApplyModifiedProperties();
            editor.Repaint();
        }
        finally
        {
            isRestoringFromVersionState = false;
        }
    }

    private IEnumerator BuildAndApplyLocalVersionCoroutine()
    {
        editor.isSubmitting = true;
        editor.submitError = "";
        editor.warningsModule.Clear();
        editor.Repaint();

        (CustomBaseVersion metadata, string zipPath) buildResult = default;
        Exception buildError = null;

        try
        {
            try { buildResult = BuildNewVersion(); }
            catch (Exception ex) { buildError = ex; }

            if (buildError != null)
            {
                editor.submitError = buildError.Message;
                editor.warningsModule.AddWarning(buildError.Message, MessageType.Error, "Test Build Failed");
                MCBLogger.LogError($"[CreatorMode] Test Build failed: {buildError}");
            }
            else
            {
                SaveUnsubmittedVersion(buildResult.metadata);
                var versionActions = new VersionActions(editor, networkService, fileManagerService);
                
                AssetDatabase.Refresh();
                while(EditorApplication.isCompiling || EditorApplication.isUpdating) { yield return null; }

                // Apply the newly built version
                yield return versionActions.ApplyOrResetCoroutine(buildResult.metadata, false);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(buildResult.zipPath) && File.Exists(buildResult.zipPath))
            {
                File.Delete(buildResult.zipPath);
            }
            
            editor.isSubmitting = false;
            EditorUtility.ClearProgressBar();
            editor.Repaint();
        }

        if (buildError == null)
        {
            EditorUtility.DisplayDialog("Test Build Complete", $"Version {buildResult.metadata.version} has been built and applied locally. You can find it in the version list.", "OK");
        }
    }
    
    public IEnumerator UploadUnsubmittedVersionCoroutine(CustomBaseVersion unsubmittedVersion)
    {
        editor.isSubmitting = true;
        editor.submitError = "";
        editor.warningsModule.Clear();
        editor.Repaint();

        string zipPath = null;
        Exception error = null;
        System.Threading.Tasks.Task<(bool success, string response, string error)> uploadTask = null;
        string uploadUrl = null;

        try
        {
            // Load the assets from the stored paths
            var customFbxGO = AssetDatabase.LoadAssetAtPath<GameObject>(unsubmittedVersion.customFbxPath);
            var customBaseAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(unsubmittedVersion.customBaseAvatarPath);
            var logicPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(unsubmittedVersion.logicPrefabPath);
            var customVeinsTexture = !string.IsNullOrEmpty(unsubmittedVersion.customVeinsTexturePath) 
                ? AssetDatabase.LoadAssetAtPath<Texture2D>(unsubmittedVersion.customVeinsTexturePath) 
                : null;

            string currentFbxPath = new VersionActions(editor, networkService, fileManagerService).GetCurrentFBXPath();
            string originalFbxPath = currentFbxPath + FileManagerService.OriginalSuffix;
            
            // If .old backup doesn't exist, use the current FBX (first-time submission case)
            if (!File.Exists(originalFbxPath))
            {
                originalFbxPath = currentFbxPath;
                if (!File.Exists(originalFbxPath))
                    throw new Exception("FBX file not found. Cannot proceed with version creation.");
            }

            // Get parent version
            var parentVersion = editor.serverVersions.FirstOrDefault(v => v.version == unsubmittedVersion.parentVersion);
            if (parentVersion == null)
                throw new Exception("Parent version not found.");

            var selectedAsset = editor.GetSelectedAsset();
            if (unsubmittedVersion.assetId <= 0 && selectedAsset != null)
            {
                unsubmittedVersion.assetId = selectedAsset.id;
            }

            EditorUtility.DisplayProgressBar("Preparing Upload", "Creating version package...", 0.3f);

            // Re-create the zip from existing files
            zipPath = fileManagerService.CreateVersionPackageForUpload(
                unsubmittedVersion.assetId,
                unsubmittedVersion.version,
                unsubmittedVersion.defaultAviVersion,
                originalFbxPath,
                customFbxGO,
                customBaseAvatar,
                logicPrefab,
                parentVersion,
                unsubmittedVersion.includeCustomVeins ?? false,
                customVeinsTexture,
                CollectAnimationAssetPathsFromFixedBy(unsubmittedVersion.customBlendshapes)
            );

            EditorUtility.DisplayProgressBar("Uploading", "Sending package to server...", 0.7f);
            string metadataJson = JsonConvert.SerializeObject(unsubmittedVersion, new StringEnumConverter());
            uploadUrl = $"{MCBUtils.getApiUrl()}{MCBUtils.NEW_VERSION_ENDPOINT}?t={editor.authToken}";
            uploadTask = networkService.SubmitNewVersionAsync(uploadUrl, editor.authToken, zipPath, metadataJson);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (uploadTask != null)
        {
            while (!uploadTask.IsCompleted) { yield return null; }
        }

        bool uploadSucceeded = false;
        try
        {
            if (error != null) throw error;

            if (uploadTask != null)
            {
                var (success, response, uploadError) = uploadTask.Result;
                if (!success) throw new Exception(uploadError);

                uploadSucceeded = true;
                RemoveUnsubmittedVersion(unsubmittedVersion);
                new VersionActions(editor, networkService, fileManagerService).StartVersionFetch();
            }
        }
        catch (Exception ex)
        {
            editor.submitError = ex.Message;
            editor.warningsModule.AddWarning(ex.Message, MessageType.Error, "Upload Failed");
            MCBLogger.LogError($"[CreatorMode] Upload failed: {ex}, url: {uploadUrl}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath))
                File.Delete(zipPath);
            
            editor.isSubmitting = false;
            editor.Repaint();
        }

        if (uploadSucceeded)
        {
            EditorUtility.DisplayDialog("Upload Successful", $"custom base version {unsubmittedVersion.version} has been uploaded.", "OK");
        }
    }

    private IEnumerator SubmitNewVersionCoroutine()
    {
        editor.isSubmitting = true;
        editor.submitError = "";
        editor.warningsModule.Clear();
        editor.Repaint();

        (CustomBaseVersion metadata, string zipPath) buildResult = default;
        Exception error = null;
        System.Threading.Tasks.Task<(bool success, string response, string error)> uploadTask = null;
        string uploadUrl = null;
            
        try
        {
            buildResult = BuildNewVersion();
            EditorUtility.DisplayProgressBar("Uploading", "Sending package to server...", 0.8f);
            string metadataJson = JsonConvert.SerializeObject(buildResult.metadata, new StringEnumConverter());
            uploadUrl = $"{MCBUtils.getApiUrl()}{MCBUtils.NEW_VERSION_ENDPOINT}?t={editor.authToken}";
            uploadTask = networkService.SubmitNewVersionAsync(uploadUrl, editor.authToken, buildResult.zipPath, metadataJson);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (uploadTask != null)
        {
            while (!uploadTask.IsCompleted) { yield return null; }
        }

        bool submissionSucceeded = false;
        try
        {
            if (error != null) throw error;

            if (uploadTask != null)
            {
                var (success, response, uploadError) = uploadTask.Result;
                if (!success) throw new Exception(uploadError);

                submissionSucceeded = true;
                RemoveUnsubmittedVersion(buildResult.metadata);
                new VersionActions(editor, networkService, fileManagerService).StartVersionFetch();
            }
        }
        catch (Exception ex)
        {
            editor.submitError = ex.Message;
            editor.warningsModule.AddWarning(ex.Message, MessageType.Error, "Submission Failed");
            MCBLogger.LogError($"[CreatorMode] Submission failed: {ex}, url: {uploadUrl}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (!string.IsNullOrEmpty(buildResult.zipPath) && File.Exists(buildResult.zipPath))
                File.Delete(buildResult.zipPath);
            
            editor.isSubmitting = false;
            editor.Repaint();
        }

        if (submissionSucceeded)
        {
            EditorUtility.DisplayDialog("Upload Successful", $"New custom base version {buildResult.metadata.version} has been uploaded.", "OK");
        }
    }
}
#endif


