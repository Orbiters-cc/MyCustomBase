#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CreatorModeModule
{
    private const int MaxToolkitSuggestionCount = 8;

    private VisualElement creatorRoot;
    private string blendshapeSearchText = string.Empty;
    private bool hasAttemptedToolkitSubmit;
    private bool wasToolkitCreatorModeVisible;

    private struct CreatorSubmissionValidation
    {
        public string versionString;
        public bool isVersionValid;
        public string versionWarning;
        public bool hasRequiredVersionMetadata;
        public string metadataWarning;
        public bool canAttemptSubmit;
        public bool canSubmit;
    }

    public void AttachUIToolkit(VisualElement root)
    {
        creatorRoot = root;
        RefreshUIToolkit();
    }

    public void DetachUIToolkit()
    {
        creatorRoot = null;
    }

    public void RefreshUIToolkit()
    {
        if (creatorRoot == null)
        {
            return;
        }

        editor.serializedObject.Update();
        try
        {
            BuildCreatorModeUIToolkit();
        }
        finally
        {
            editor.serializedObject.ApplyModifiedProperties();
        }
    }

    private void BuildCreatorModeUIToolkit()
    {
        creatorRoot.Clear();
        creatorRoot.AddToClassList("mcb-creator");
        creatorRoot.EnableInClassList("mcb-creator--asset-view", editor.GetSelectedAsset() != null);

        if (!editor.isAuthenticated ||
            !editor.HasServerAccess ||
            MCBPackageVersionService.RequiresMajorUpdate ||
            editor.ShouldShowGalleryOnly())
        {
            creatorRoot.style.display = DisplayStyle.None;
            wasToolkitCreatorModeVisible = false;
            return;
        }

        creatorRoot.style.display = DisplayStyle.Flex;

        if (!IsSelectedAssetOwnedByCurrentUser())
        {
            creatorRoot.style.display = DisplayStyle.None;
            wasToolkitCreatorModeVisible = false;
            return;
        }

        if (!editor.isCreatorModeProp.boolValue)
        {
            creatorRoot.style.display = DisplayStyle.None;
            wasToolkitCreatorModeVisible = false;
            return;
        }

        if (!wasToolkitCreatorModeVisible)
        {
            creatorBlendshapeEditorFoldout = false;
            hasAttemptedToolkitSubmit = false;
            wasToolkitCreatorModeVisible = true;
        }

        creatorModeFoldout = true;

        PopulateParentVersionDropdown();
        if (editor.selectedVersionForAction != previouslySelectedVersion)
        {
            if (editor.selectedVersionForAction != null && editor.selectedVersionForAction.isUnsubmitted)
            {
                PopulateFieldsFromVersion(editor.selectedVersionForAction);
            }

            previouslySelectedVersion = editor.selectedVersionForAction;
        }

        var content = CreateCreatorPanel();
        content.AddToClassList("mcb-creator__content");
        content.SetEnabled(!editor.isSubmitting);
        creatorRoot.Add(content);

        BuildCreatorFormHeaderUIToolkit(content);
        BlenderSyncService.BuildCreatorModeSectionUIToolkit(content, editor, RefreshEditorUi);
        BuildParentVersionDropdownUIToolkit(content);
        BuildModelFileBuildEntriesUIToolkit(content);
        content.Add(CreateIconObjectField(
            "Avatar Logic Prefab",
            editor.avatarLogicPrefabProp,
            typeof(GameObject),
            false,
            new MCBCreatorPrefabIconElement()));
        BuildCustomVeinsSectionUIToolkit(content);
        BuildDynamicNormalsSectionUIToolkit(content);
        BuildSuggestRealisticSectionUIToolkit(content);
        BuildBlendshapeEditorUIToolkit(content);
        BuildAdvancedMeshReplacementToggleUIToolkit(content);
        BuildVersionSubmissionSectionUIToolkit(content);

        if (!string.IsNullOrEmpty(editor.submitError))
        {
            content.Add(CreateHelpBox("Submission Error: " + editor.submitError, HelpBoxMessageType.Error));
        }
    }

    private void BuildCreatorFormHeaderUIToolkit(VisualElement root)
    {
        var row = CreateCreatorRow();
        row.AddToClassList("mcb-creator-form-header");

        var title = CreateStrongLabel("Create New Version");
        title.AddToClassList("mcb-creator-form-header__title");
        row.Add(title);

        var cancelButton = CreateTextButton("Cancel", null, () =>
        {
            ApplyCreatorChange(() =>
            {
                hasAttemptedToolkitSubmit = false;
                creatorBlendshapeEditorFoldout = false;
                wasToolkitCreatorModeVisible = false;
                editor.isCreatorModeProp.boolValue = false;
            }, true, true);
        });
        cancelButton.AddToClassList("mcb-creator-form-header__cancel");
        row.Add(cancelButton);

        root.Add(row);
    }

    private void BuildParentVersionDropdownUIToolkit(VisualElement root)
    {
        var row = CreateCreatorRow();
        row.AddToClassList("mcb-creator__field-row");

        if (parentVersionDisplayOptions == null || parentVersionDisplayOptions.Count == 0)
        {
            var dropdown = new DropdownField("Parent Version:", new List<string> { "First version" }, 0);
            dropdown.AddToClassList("mcb-dropdown");
            dropdown.SetEnabled(false);
            row.Add(dropdown);
            root.Add(row);
            HandleParentVersionChanged(null);
            return;
        }

        int currentParentPopupIndex = selectedParentVersionIndex == -1 ? defaultParentIndex : selectedParentVersionIndex;
        currentParentPopupIndex = Mathf.Clamp(currentParentPopupIndex, 0, parentVersionDisplayOptions.Count - 1);

        if (selectedParentVersionIndex == -1 && defaultParentIndex != -1)
        {
            selectedParentVersionIndex = defaultParentIndex;
            selectedParentVersionObject = compatibleParentVersions[defaultParentIndex];
            SetDefaultVersionNumbers(selectedParentVersionObject);
        }

        var parentDropdown = new DropdownField("Parent Version:", parentVersionDisplayOptions, currentParentPopupIndex);
        parentDropdown.AddToClassList("mcb-dropdown");
        parentDropdown.RegisterValueChangedCallback(evt =>
        {
            int newParentIndex = parentVersionDisplayOptions.IndexOf(evt.newValue);
            ApplyCreatorChange(() =>
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

                HandleParentVersionChanged(selectedParentVersionObject);
            });
        });
        row.Add(parentDropdown);
        root.Add(row);
        HandleParentVersionChanged(selectedParentVersionObject);
    }

    private Button CreateParentBlendshapeImportButtonUIToolkit()
    {
        if (selectedParentVersionObject == null ||
            selectedParentVersionObject.customBlendshapes == null ||
            selectedParentVersionObject.customBlendshapes.Length == 0)
        {
            return null;
        }

        var importButton = CreateTextButton(
            "Import Parent Blendshapes",
            "Copy parent version blendshapes and correctives into Creator Mode.",
            () => ApplyCreatorChange(() => ImportBlendshapesFromParent(selectedParentVersionObject)));
        importButton.AddToClassList("mcb-creator-blendshapes__import-button");
        return importButton;
    }

    private void BuildModelFileBuildEntriesUIToolkit(VisualElement root)
    {
        SyncModelFileBuildEntryCount();

        root.Add(CreateSectionLabel("Target Model Files"));
        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            int index = i;
            var targetFbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(index).objectReferenceValue as GameObject;
            string targetLabel = targetFbx != null ? AssetDatabase.GetAssetPath(targetFbx) : $"Target FBX {index + 1}";
            var entry = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(index);
            var customFbxProp = entry.FindPropertyRelative("customFbx");
            var externalCustomFbxProp = entry.FindPropertyRelative("externalCustomFbxPath");
            var avatarProp = entry.FindPropertyRelative("customBaseAvatar");

            var card = CreateCreatorPanel();
            card.AddToClassList("mcb-creator-target");
            card.Add(CreateStrongLabel(targetLabel));

            var customFbxRow = CreateCreatorRow();
            customFbxRow.AddToClassList("mcb-creator__field-row");
            customFbxRow.AddToClassList("mcb-creator-target__row");
            var customFbxField = CreateObjectField("Custom FBX (Transformed)", customFbxProp, typeof(GameObject), false);
            customFbxRow.Add(customFbxField);
            var applyFbxButton = CreateTextButton("Apply", null, () =>
            {
                editor.serializedObject.ApplyModifiedProperties();
                ApplyCustomFbxForModelEntry(targetFbx, customFbxProp.objectReferenceValue as GameObject);
                RefreshEditorUi();
            });
            applyFbxButton.AddToClassList("mcb-creator__compact-button");
            applyFbxButton.SetEnabled(targetFbx != null && customFbxProp.objectReferenceValue != null);
            customFbxRow.Add(applyFbxButton);
            card.Add(customFbxRow);

            if (externalCustomFbxProp != null && !string.IsNullOrWhiteSpace(externalCustomFbxProp.stringValue))
            {
                var externalRow = CreateCreatorRow();
                externalRow.AddToClassList("mcb-creator__field-row");
                externalRow.AddToClassList("mcb-creator-target__row");
                var externalField = new TextField("External Blender FBX") { value = externalCustomFbxProp.stringValue };
                externalField.AddToClassList("mcb-creator__text-field");
                externalField.isReadOnly = true;
                externalRow.Add(externalField);

                var clearExternal = CreateTextButton("Clear", null, () =>
                {
                    ApplyCreatorChange(() => externalCustomFbxProp.stringValue = string.Empty);
                });
                clearExternal.AddToClassList("mcb-creator__compact-button");
                externalRow.Add(clearExternal);
                card.Add(externalRow);
            }

            var avatarRow = CreateCreatorRow();
            avatarRow.AddToClassList("mcb-creator__field-row");
            avatarRow.AddToClassList("mcb-creator-target__row");
            var avatarField = CreateObjectField("Custom Base Avatar (Transformed)", avatarProp, typeof(Avatar), false);
            avatarRow.Add(avatarField);
            var avatar = avatarProp.objectReferenceValue as Avatar;
            var generateButton = CreateTextButton(avatar == null ? "Generate" : "Update", null, () =>
            {
                editor.serializedObject.ApplyModifiedProperties();
                GenerateAvatarForModelEntry(targetFbx, customFbxProp.objectReferenceValue as GameObject, avatarProp);
                RefreshEditorUi();
            });
            generateButton.AddToClassList("mcb-creator__compact-button");
            generateButton.SetEnabled(targetFbx != null && customFbxProp.objectReferenceValue != null);
            avatarRow.Add(generateButton);

            var applyAvatarButton = CreateTextButton("Apply", null, () =>
            {
                editor.serializedObject.ApplyModifiedProperties();
                ApplyCustomAvatarForModelEntry(targetFbx, customFbxProp.objectReferenceValue as GameObject, avatarProp.objectReferenceValue as Avatar);
                RefreshEditorUi();
            });
            applyAvatarButton.AddToClassList("mcb-creator__compact-button");
            applyAvatarButton.SetEnabled(targetFbx != null && customFbxProp.objectReferenceValue != null && avatar != null);
            avatarRow.Add(applyAvatarButton);
            card.Add(avatarRow);

            root.Add(card);
        }
    }

    private void BuildCustomVeinsSectionUIToolkit(VisualElement root)
    {
        var includeProp = editor.includeCustomVeinsForCreatorProp;
        var textureProp = editor.customVeinsNormalMapProp;

        var card = CreateCreatorPanel();
        card.AddToClassList("mcb-creator-option");
        card.Add(CreateToggle("Include custom veins", includeProp.boolValue, evt =>
        {
            ApplyCreatorChange(() =>
            {
                includeProp.boolValue = evt.newValue;
                if (!evt.newValue)
                {
                    textureProp.objectReferenceValue = null;
                    autoAssignedVeinsTexturePath = null;
                }
            });
        }));

        if (includeProp.boolValue)
        {
            BuildVeinsTextureFieldUIToolkit(card, textureProp);

            if (textureProp.objectReferenceValue == null)
            {
                card.Add(CreateHelpBox("Assign a normal map texture to include custom veins.", HelpBoxMessageType.Warning));
            }
            else
            {
                BuildCustomVeinsImportWarningUIToolkit(card, textureProp.objectReferenceValue as Texture2D);
            }
        }

        root.Add(card);
    }

    private void BuildVeinsTextureFieldUIToolkit(VisualElement root, SerializedProperty textureProp)
    {
        var row = CreateCreatorRow();
        row.AddToClassList("mcb-creator__texture-row");

        var preview = new VisualElement();
        preview.AddToClassList("mcb-creator__texture-preview");
        var texture = textureProp.objectReferenceValue as Texture2D;
        if (texture != null)
        {
            var image = new Image { image = texture, scaleMode = ScaleMode.ScaleToFit };
            image.AddToClassList("mcb-creator__texture-preview-image");
            preview.Add(image);
        }
        row.Add(preview);

        var textureField = CreateObjectField(
            "Custom veins",
            "Normal map applied to the veins detail layer.",
            textureProp,
            typeof(Texture2D),
            false,
            evt =>
            {
                Texture2D newTexture = evt.newValue as Texture2D;
                textureProp.objectReferenceValue = newTexture;
                string selectedPath = AssetDatabase.GetAssetPath(newTexture);
                string parentPath = GetVeinsTexturePathForVersion(selectedParentVersionObject);
                autoAssignedVeinsTexturePath = !string.IsNullOrEmpty(selectedPath) && selectedPath == parentPath ? selectedPath : null;
            });
        textureField.AddToClassList("mcb-creator__texture-field");
        row.Add(textureField);

        root.Add(row);
    }

    private void BuildCustomVeinsImportWarningUIToolkit(VisualElement root, Texture2D texture)
    {
        if (texture == null)
        {
            return;
        }

        string assetPath = AssetDatabase.GetAssetPath(texture);
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        if (TryGetCustomVeinsTextureImporter(assetPath, out var importer) && IsValidCustomVeinsNormalMapImport(importer))
        {
            return;
        }

        var warning = CreateCreatorPanel();
        warning.AddToClassList("mcb-creator__nested-warning");
        warning.Add(CreateHelpBox("This texture is not marked as a normal map", HelpBoxMessageType.Warning));

        var row = CreateCreatorRow();
        row.AddToClassList("mcb-creator__actions-row");
        var fixButton = CreateTextButton("Fix Now", null, () =>
        {
            FixCustomVeinsNormalMapImport(assetPath);
            RefreshEditorUi();
        });
        fixButton.AddToClassList("mcb-creator__compact-button");
        row.Add(fixButton);
        warning.Add(row);
        root.Add(warning);
    }

    private void BuildDynamicNormalsSectionUIToolkit(VisualElement root)
    {
        var includeBodyProp = editor.includeDynamicNormalsBodyForCreatorProp;
        var includeFlexingProp = editor.includeDynamicNormalsFlexingForCreatorProp;

        var card = CreateCreatorPanel();
        card.AddToClassList("mcb-creator-option");
        card.Add(CreateToggle("Enable dynamic normals for body", includeBodyProp.boolValue, evt =>
        {
            ApplyCreatorChange(() => includeBodyProp.boolValue = evt.newValue);
        }));
        card.Add(CreateHelpBox(
            "Don't use if muscles normal is already baked in the mesh.\nWill apply to all blendshapes containing \"muscle\" in the name.",
            HelpBoxMessageType.Info));

        card.Add(CreateToggle("Enable dynamic normals for flexings", includeFlexingProp.boolValue, evt =>
        {
            ApplyCreatorChange(() => includeFlexingProp.boolValue = evt.newValue);
        }));
        card.Add(CreateHelpBox(
            "Allows for the flexed muscles to be more visible.\nWill apply to all blendshapes containing \"flex\" in the name.",
            HelpBoxMessageType.Info));
        root.Add(card);
    }

    private void BuildSuggestRealisticSectionUIToolkit(VisualElement root)
    {
        var includeProp = editor.includeSuggestRealisticForCreatorProp;
        var pathsProp = editor.suggestRealisticMeshPathsForCreatorProp;
        if (includeProp == null || pathsProp == null)
        {
            return;
        }

        var card = CreateCreatorPanel();
        card.AddToClassList("mcb-creator-option");
        card.AddToClassList("mcb-creator-suggest");

        var suggestToggleRow = CreateCreatorRow();
        suggestToggleRow.AddToClassList("mcb-creator-suggest__toggle-row");
        var suggestToggle = CreateCheckboxButton(
            "Warn users when selected Poiyomi mesh materials use Texture Ramp lighting and offer to switch them to Realistic.",
            includeProp.boolValue,
            true,
            value =>
            {
                ApplyCreatorChange(() =>
                {
                    includeProp.boolValue = value;
                    if (!value)
                    {
                        pathsProp.ClearArray();
                    }
                });
            });
        suggestToggle.AddToClassList("mcb-creator-suggest__checkbox");
        suggestToggleRow.Add(suggestToggle);
        var suggestLabel = CreateStrongLabel("Suggest realistic materials");
        suggestLabel.AddToClassList("mcb-creator-suggest__label");
        suggestToggleRow.Add(suggestLabel);
        card.Add(suggestToggleRow);

        if (includeProp.boolValue)
        {
            var options = GetSuggestRealisticMeshOptions();
            if (options.Count == 0)
            {
                card.Add(CreateHelpBox("No skinned mesh renderers were found in the targeted FBXs on this avatar.", HelpBoxMessageType.Warning));
            }
            else
            {
                var meshTable = new VisualElement();
                meshTable.AddToClassList("mcb-creator-target-meshes");

                var actions = CreateCreatorRow();
                actions.AddToClassList("mcb-creator-target-meshes__header");
                actions.Add(CreateSectionLabel("Target meshes"));
                var selectAll = CreateTextButton("All", null, () =>
                {
                    ApplyCreatorChange(() => SetSerializedStringList(pathsProp, options.Select(option => option.avatarPath)));
                });
                selectAll.AddToClassList("mcb-creator-target-meshes__action");
                actions.Add(selectAll);
                var selectNone = CreateTextButton("None", null, () =>
                {
                    ApplyCreatorChange(() => pathsProp.ClearArray());
                });
                selectNone.AddToClassList("mcb-creator-target-meshes__action");
                actions.Add(selectNone);
                meshTable.Add(actions);

                for (int i = 0; i < options.Count; i++)
                {
                    var option = options[i];
                    bool selected = SerializedStringListContains(pathsProp, option.avatarPath);
                    var row = CreateCreatorRow();
                    row.AddToClassList("mcb-creator-target-meshes__row");
                    row.EnableInClassList("mcb-creator-target-meshes__row--first", i == 0);
                    row.EnableInClassList("mcb-creator-target-meshes__row--last", i == options.Count - 1);
                    row.EnableInClassList("mcb-creator-target-meshes__row--divider", i < options.Count - 1);

                    var label = CreateLabel(option.Label, 12, FontStyle.Normal, Color.white);
                    label.tooltip = option.Tooltip;
                    label.AddToClassList("mcb-creator-target-meshes__name");
                    row.Add(label);

                    var toggle = CreateCheckboxButton(option.Tooltip, selected, true, value =>
                    {
                        ApplyCreatorChange(() => SetSerializedStringListContains(pathsProp, option.avatarPath, value));
                    });
                    toggle.AddToClassList("mcb-creator-target-meshes__select");
                    row.Add(toggle);
                    meshTable.Add(row);
                }

                card.Add(meshTable);

                if (GetSerializedStringList(pathsProp).Count == 0)
                {
                    card.Add(CreateHelpBox("Select at least one mesh for the suggestRealistic guardrail.", HelpBoxMessageType.Warning));
                }
            }
        }

        root.Add(card);
    }

    private void BuildBlendshapeEditorUIToolkit(VisualElement root)
    {
        var card = CreateCreatorPanel();
        card.AddToClassList("mcb-creator-blendshapes");
        card.EnableInClassList("mcb-creator-blendshapes--expanded", creatorBlendshapeEditorFoldout);
        card.EnableInClassList("mcb-creator-blendshapes--collapsed", !creatorBlendshapeEditorFoldout);

        var foldoutHeader = CreateCreatorRow();
        foldoutHeader.AddToClassList("mcb-creator-blendshapes__foldout-header");

        var toggleArea = CreateCreatorRow();
        toggleArea.AddToClassList("mcb-creator-blendshapes__toggle-area");
        toggleArea.Add(new MCBCreatorDisclosureIconElement(creatorBlendshapeEditorFoldout));
        var titleLabel = CreateStrongLabel("Blendshape And Correctives Editor");
        titleLabel.AddToClassList("mcb-creator-blendshapes__title");
        toggleArea.Add(titleLabel);
        var countChip = CreateMutedLabel($"{GetExposedBlendshapeCount()} exposed");
        countChip.AddToClassList("mcb-creator-blendshapes__count-chip");
        toggleArea.Add(countChip);
        toggleArea.RegisterCallback<ClickEvent>(_ =>
        {
            creatorBlendshapeEditorFoldout = !creatorBlendshapeEditorFoldout;
            RefreshCreatorUi();
        });
        foldoutHeader.Add(toggleArea);

        var importButton = CreateParentBlendshapeImportButtonUIToolkit();
        if (importButton != null)
        {
            foldoutHeader.Add(importButton);
        }

        card.Add(foldoutHeader);

        if (creatorBlendshapeEditorFoldout)
        {
            BuildBlendshapeTableUIToolkit(card);
            BuildBlendshapeSearchUIToolkit(card);
            BuildBlendshapeCorrectivesSectionUIToolkit(card);
        }

        root.Add(card);
    }

    private int GetExposedBlendshapeCount()
    {
        var blendshapesProp = editor.customBlendshapesForCreatorProp;
        if (blendshapesProp == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < blendshapesProp.arraySize; i++)
        {
            var element = blendshapesProp.GetArrayElementAtIndex(i);
            var isSliderProp = element.FindPropertyRelative("isSlider");
            if (isSliderProp != null && isSliderProp.boolValue)
            {
                count++;
            }
        }

        return count;
    }

    private void BuildBlendshapeTableUIToolkit(VisualElement root)
    {
        var table = new VisualElement();
        table.AddToClassList("mcb-creator-table");

        var header = CreateCreatorRow();
        header.AddToClassList("mcb-creator-table__header");
        var reorderSpacer = new VisualElement();
        reorderSpacer.AddToClassList("mcb-creator-table__reorder");
        header.Add(reorderSpacer);
        header.Add(CreateTableHeaderLabel("Blendshape Name", "mcb-creator-table__name"));
        header.Add(CreateTableHeaderLabel("Value", "mcb-creator-table__value"));
        var sliderHeader = CreateTableHeaderLabel("Slider", "mcb-creator-table__toggle");
        sliderHeader.AddToClassList("mcb-creator-table__slider-toggle");
        header.Add(sliderHeader);
        var defaultHeader = CreateTableHeaderLabel("Default", "mcb-creator-table__toggle");
        defaultHeader.AddToClassList("mcb-creator-table__default-toggle");
        header.Add(defaultHeader);
        header.Add(CreateTableHeaderLabel("Correctives", "mcb-creator-table__correctives"));
        header.Add(CreateTableHeaderLabel("", "mcb-creator-table__delete"));
        table.Add(header);

        var blendshapesProp = editor.customBlendshapesForCreatorProp;
        for (int i = 0; i < blendshapesProp.arraySize; i++)
        {
            int index = i;
            var element = blendshapesProp.GetArrayElementAtIndex(index);
            var nameProp = element.FindPropertyRelative("name");
            var defaultValueProp = element.FindPropertyRelative("defaultValue");
            var isSliderProp = element.FindPropertyRelative("isSlider");
            var isSliderDefaultProp = element.FindPropertyRelative("isSliderDefault");
            var correctiveProp = element.FindPropertyRelative("correctiveBlendshapes");

            var row = CreateCreatorRow();
            row.AddToClassList("mcb-creator-table__row");
            row.EnableInClassList("mcb-creator-table__row--first", index == 0);
            row.EnableInClassList("mcb-creator-table__row--last", index == blendshapesProp.arraySize - 1);
            row.EnableInClassList("mcb-creator-table__row--divider", index < blendshapesProp.arraySize - 1);

            var reorder = new VisualElement();
            reorder.AddToClassList("mcb-creator-table__reorder");
            reorder.AddToClassList("mcb-creator-table__reorder-cell");
            var upButton = CreateIconButton(
                "Move this blendshape up.",
                new MCBCreatorChevronIconElement(true),
                () => ApplyCreatorChange(() => blendshapesProp.MoveArrayElement(index, index - 1)));
            upButton.AddToClassList("mcb-creator-table__icon-button");
            upButton.AddToClassList("mcb-creator-table__reorder-button");
            upButton.AddToClassList("mcb-creator-table__reorder-button--up");
            upButton.SetEnabled(index > 0);
            reorder.Add(upButton);

            var downButton = CreateIconButton(
                "Move this blendshape down.",
                new MCBCreatorChevronIconElement(false),
                () => ApplyCreatorChange(() => blendshapesProp.MoveArrayElement(index, index + 1)));
            downButton.AddToClassList("mcb-creator-table__icon-button");
            downButton.AddToClassList("mcb-creator-table__reorder-button");
            downButton.AddToClassList("mcb-creator-table__reorder-button--down");
            downButton.SetEnabled(index < blendshapesProp.arraySize - 1);
            reorder.Add(downButton);
            row.Add(reorder);

            var nameLabel = CreateLabel(nameProp.stringValue, 12, FontStyle.Normal, Color.white);
            nameLabel.AddToClassList("mcb-creator-table__name");
            nameLabel.AddToClassList("mcb-creator-table__name-label");
            row.Add(nameLabel);

            var valueField = CreateTextField(null, defaultValueProp.stringValue, evt =>
            {
                ApplyCreatorChange(() => defaultValueProp.stringValue = evt.newValue ?? string.Empty, false);
            });
            valueField.AddToClassList("mcb-creator-table__value");
            row.Add(valueField);

            var sliderToggle = CreateCheckboxButton("Use this blendshape as a slider.", isSliderProp.boolValue, true, value =>
            {
                ApplyCreatorChange(() =>
                {
                    isSliderProp.boolValue = value;
                    if (!value)
                    {
                        isSliderDefaultProp.boolValue = false;
                    }
                });
            });
            sliderToggle.AddToClassList("mcb-creator-table__toggle");
            sliderToggle.AddToClassList("mcb-creator-table__slider-toggle");
            row.Add(sliderToggle);

            var defaultToggle = CreateCheckboxButton("Enable this slider by default.", isSliderDefaultProp.boolValue, isSliderProp.boolValue, value =>
            {
                ApplyCreatorChange(() => isSliderDefaultProp.boolValue = value);
            });
            defaultToggle.AddToClassList("mcb-creator-table__toggle");
            defaultToggle.AddToClassList("mcb-creator-table__default-toggle");
            row.Add(defaultToggle);

            var addFixesButton = CreateTextButton("add fixes", null, () =>
            {
                ApplyCreatorChange(() => AddCorrectivePair(correctiveProp));
            });
            addFixesButton.AddToClassList("mcb-creator-table__correctives");
            addFixesButton.AddToClassList("mcb-creator-table__mini-button");
            row.Add(addFixesButton);

            var deleteButton = CreateIconButton(
                "Delete this blendshape.",
                new MCBInteractionIconElement(MCBInteractionIconKind.Delete),
                () => ApplyCreatorChange(() => blendshapesProp.DeleteArrayElementAtIndex(index)));
            deleteButton.AddToClassList("mcb-creator-table__delete");
            deleteButton.AddToClassList("mcb-creator-table__icon-button");
            deleteButton.AddToClassList("mcb-creator-table__delete-button");
            row.Add(deleteButton);

            table.Add(row);
        }

        root.Add(table);
    }

    private void BuildBlendshapeSearchUIToolkit(VisualElement root)
    {
        root.Add(CreateSectionLabel("Search and Add Blendshapes:"));
        var suggestionRow = CreateCreatorRow();
        suggestionRow.AddToClassList("mcb-creator__suggestions");

        Action rebuildSuggestions = null;
        rebuildSuggestions = () =>
        {
            suggestionRow.Clear();
            var suggestions = GetBlendshapeSuggestions(blendshapeSearchText).ToList();
            suggestionRow.style.display = suggestions.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (string suggestion in suggestions)
            {
                string selected = suggestion;
                var button = CreateTextButton(selected, null, () =>
                {
                    blendshapeSearchText = string.Empty;
                    OnBlendshapeSearchConfirm(selected);
                    RefreshCreatorUi();
                });
                button.AddToClassList("mcb-creator__suggestion");
                suggestionRow.Add(button);
            }
        };

        var search = CreateTextField(null, blendshapeSearchText, evt =>
        {
            blendshapeSearchText = evt.newValue ?? string.Empty;
            rebuildSuggestions();
        });
        search.isDelayed = false;
        search.RegisterCallback<KeyUpEvent>(_ =>
        {
            blendshapeSearchText = search.value ?? string.Empty;
            rebuildSuggestions();
        });
        search.AddToClassList("mcb-creator__search-field");
        root.Add(search);
        root.Add(suggestionRow);
        rebuildSuggestions();
    }

    private IEnumerable<string> GetBlendshapeSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Enumerable.Empty<string>();
        }

        var existingBlendshapeNames = editor.customBaseTarget.customBlendshapesForCreator.Select(e => e.name);
        return GetAllBlendshapeNamesFromCustomFbx()
            .Except(existingBlendshapeNames)
            .Where(shapeName => shapeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(shapeName => shapeName, StringComparer.Ordinal)
            .Take(MaxToolkitSuggestionCount);
    }

    private void BuildBlendshapeCorrectivesSectionUIToolkit(VisualElement root)
    {
        root.Add(CreateSectionLabel("Blendshape Correctives"));

        var blendshapesProp = editor.customBlendshapesForCreatorProp;
        if (blendshapesProp == null || blendshapesProp.arraySize == 0)
        {
            ClearAllCorrectiveSearchFields();
            root.Add(CreateHelpBox("Add blendshapes above to create corrective links.", HelpBoxMessageType.None));
            return;
        }

        bool drewOne = false;
        for (int i = 0; i < blendshapesProp.arraySize; i++)
        {
            int blendshapeIndex = i;
            var blendshapeElement = blendshapesProp.GetArrayElementAtIndex(blendshapeIndex);
            var correctiveProp = blendshapeElement.FindPropertyRelative("correctiveBlendshapes");
            if (correctiveProp == null || correctiveProp.arraySize == 0)
            {
                continue;
            }

            drewOne = true;
            string title = blendshapeElement.FindPropertyRelative("name").stringValue;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "(Unnamed Blendshape)";
            }

            var card = CreateCreatorPanel();
            card.AddToClassList("mcb-creator-corrective");
            var titleRow = CreateCreatorRow();
            titleRow.AddToClassList("mcb-creator-corrective__title-row");
            var titleLabel = CreateStrongLabel(title);
            titleLabel.AddToClassList("mcb-creator-corrective__title");
            titleRow.Add(titleLabel);
            var deleteButton = CreateTextButton("Delete", null, () =>
            {
                ApplyCreatorChange(() => correctiveProp.arraySize = 0);
            });
            deleteButton.AddToClassList("mcb-creator__compact-button");
            deleteButton.AddToClassList("mcb-creator-corrective__compact-button");
            deleteButton.AddToClassList("mcb-creator-corrective__delete-all");
            titleRow.Add(deleteButton);
            card.Add(titleRow);

            for (int j = 0; j < correctiveProp.arraySize; j++)
            {
                int correctiveIndex = j;
                var item = correctiveProp.GetArrayElementAtIndex(correctiveIndex);
                string keyPrefix = $"corrective_{blendshapeIndex}_{correctiveIndex}";

                var row = CreateCreatorRow();
                row.AddToClassList("mcb-creator-corrective__row");
                row.EnableInClassList("mcb-creator-corrective__row--first", correctiveIndex == 0);
                row.EnableInClassList("mcb-creator-corrective__row--last", correctiveIndex == correctiveProp.arraySize - 1);
                row.EnableInClassList("mcb-creator-corrective__row--divider", correctiveIndex < correctiveProp.arraySize - 1);
                row.Add(BuildCorrectiveTargetFieldUIToolkit(
                    item.FindPropertyRelative("toFixType"),
                    item.FindPropertyRelative("toFix"),
                    keyPrefix + "_toFix",
                    "to fix",
                    true));
                row.Add(BuildCorrectiveTargetFieldUIToolkit(
                    item.FindPropertyRelative("fixedByType"),
                    item.FindPropertyRelative("fixedBy"),
                    keyPrefix + "_fixing",
                    "fixing",
                    false));

                var removeButton = CreateIconButton(
                    "Delete this corrective link.",
                    new MCBInteractionIconElement(MCBInteractionIconKind.Delete),
                    () => ApplyCreatorChange(() => correctiveProp.DeleteArrayElementAtIndex(correctiveIndex)));
                removeButton.AddToClassList("mcb-creator-corrective__remove");
                removeButton.AddToClassList("mcb-creator-table__icon-button");
                row.Add(removeButton);
                card.Add(row);
            }

            var addButton = CreateTextButton("Add", null, () =>
            {
                ApplyCreatorChange(() => AddCorrectivePair(correctiveProp));
            });
            addButton.AddToClassList("mcb-creator__compact-button");
            addButton.AddToClassList("mcb-creator-corrective__compact-button");
            card.Add(addButton);
            root.Add(card);
        }

        if (!drewOne)
        {
            root.Add(CreateHelpBox("Use 'add corrective blendshapes' in the table to create entries.", HelpBoxMessageType.None));
        }

        root.Add(CreateHelpBox(
            "The fixing target activation is proportional to the fixed blendshape and the blendshape needing correction.",
            HelpBoxMessageType.Info));
    }

    private VisualElement BuildCorrectiveTargetFieldUIToolkit(
        SerializedProperty typeProp,
        SerializedProperty stringProp,
        string fieldKey,
        string labelSuffix,
        bool typeBeforeLabel)
    {
        var column = new VisualElement();
        column.AddToClassList("mcb-creator-corrective__target");
        column.EnableInClassList("mcb-creator-corrective__target--to-fix", typeBeforeLabel);
        column.EnableInClassList("mcb-creator-corrective__target--fixing", !typeBeforeLabel);

        CorrectiveActivationType currentType = CorrectiveActivationType.Blendshape;
        if (typeProp != null)
        {
            typeProp.enumValueIndex = Mathf.Clamp(typeProp.enumValueIndex, 0, Enum.GetValues(typeof(CorrectiveActivationType)).Length - 1);
            currentType = (CorrectiveActivationType)typeProp.enumValueIndex;
        }

        var header = CreateCreatorRow();
        header.AddToClassList("mcb-creator-corrective__target-header");
        var typeField = new EnumField(currentType);
        typeField.AddToClassList("mcb-creator-corrective__type");
        typeField.RegisterValueChangedCallback(evt =>
        {
            ApplyCreatorChange(() =>
            {
                ApplyCorrectiveTypeChange(typeProp, stringProp, fieldKey, currentType, (CorrectiveActivationType)evt.newValue);
            });
        });
        var label = CreateMutedLabel(labelSuffix);

        if (typeBeforeLabel)
        {
            header.Add(typeField);
            header.Add(label);
        }
        else
        {
            header.Add(label);
            header.Add(typeField);
        }
        column.Add(header);

        if (currentType == CorrectiveActivationType.Blendshape)
        {
            BuildCorrectiveBlendshapeNameFieldUIToolkit(column, stringProp);
        }
        else
        {
            BuildAnimationClipNameFieldUIToolkit(column, stringProp);
        }

        return column;
    }

    private void BuildCorrectiveBlendshapeNameFieldUIToolkit(VisualElement root, SerializedProperty stringProp)
    {
        string currentValue = stringProp != null ? stringProp.stringValue ?? string.Empty : string.Empty;
        var suggestionRow = CreateCreatorRow();
        suggestionRow.AddToClassList("mcb-creator__suggestions");
        TextField field = null;

        Action rebuildSuggestions = null;
        rebuildSuggestions = () =>
        {
            suggestionRow.Clear();
            string query = field != null ? field.value ?? string.Empty : currentValue;
            var suggestions = GetAllBlendshapeNamesFromCustomFbx()
                .Where(name => !string.IsNullOrWhiteSpace(query) && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Take(MaxToolkitSuggestionCount)
                .ToList();
            suggestionRow.style.display = suggestions.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (string suggestion in suggestions)
            {
                string selected = suggestion;
                var button = CreateTextButton(selected, null, () =>
                {
                    if (field != null)
                    {
                        field.SetValueWithoutNotify(selected);
                    }

                    ApplyCreatorChange(() =>
                    {
                        if (stringProp != null)
                        {
                            stringProp.stringValue = selected;
                        }
                    }, false);
                    rebuildSuggestions();
                });
                button.AddToClassList("mcb-creator__suggestion");
                suggestionRow.Add(button);
            }
        };

        field = CreateTextField(null, currentValue, evt =>
        {
            ApplyCreatorChange(() =>
            {
                if (stringProp != null)
                {
                    stringProp.stringValue = evt.newValue ?? string.Empty;
                }
            }, false);
            rebuildSuggestions();
        });
        root.Add(field);
        root.Add(suggestionRow);
        rebuildSuggestions();
    }

    private void BuildAnimationClipNameFieldUIToolkit(VisualElement root, SerializedProperty stringProp)
    {
        string currentName = stringProp != null ? stringProp.stringValue ?? string.Empty : string.Empty;
        var currentClip = FindAnimationClipsByName(currentName).FirstOrDefault();
        var row = CreateCreatorRow();
        row.AddToClassList("mcb-creator__field-row");
        var nameField = CreateTextField(null, currentName, evt =>
        {
            ApplyCreatorChange(() =>
            {
                if (stringProp != null)
                {
                    stringProp.stringValue = evt.newValue ?? string.Empty;
                }
            }, false);
        });
        row.Add(nameField);

        var clipField = new ObjectField
        {
            objectType = typeof(AnimationClip),
            allowSceneObjects = false,
            value = currentClip
        };
        clipField.AddToClassList("mcb-creator__object-field");
        clipField.RegisterValueChangedCallback(evt =>
        {
            ApplyCreatorChange(() =>
            {
                if (stringProp != null)
                {
                    var selectedClip = evt.newValue as AnimationClip;
                    stringProp.stringValue = selectedClip != null ? selectedClip.name : string.Empty;
                }
            });
        });
        row.Add(clipField);
        root.Add(row);
    }

    private void BuildAdvancedMeshReplacementToggleUIToolkit(VisualElement root)
    {
        if (!FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR) ||
            editor.useAdvancedMeshReplacementForCreatorProp == null)
        {
            return;
        }

        var card = CreateCreatorPanel();
        card.AddToClassList("mcb-creator-option");
        var advancedToggle = CreateToggle(
            "Use Advanced Mesh Replacement",
            "Submit model edits as an encrypted native Unity mesh payload instead of an encrypted replacement FBX.",
            editor.useAdvancedMeshReplacementForCreatorProp.boolValue,
            evt =>
            {
                ApplyCreatorChange(() => editor.useAdvancedMeshReplacementForCreatorProp.boolValue = evt.newValue);
            });
        var advancedText = new VisualElement();
        advancedText.AddToClassList("mcb-creator-advanced");
        advancedText.Add(advancedToggle);
        var advancedDescription = CreateMutedLabel("Improves the user experience by dividing apply time by two, but takes a bit more time to upload.");
        advancedDescription.AddToClassList("mcb-creator-advanced__description");
        advancedText.Add(advancedDescription);
        var advancedRow = CreateIconFieldRow(new MCBCreatorChipIconElement(), advancedText);
        advancedRow.AddToClassList("mcb-creator-advanced-field");
        card.Add(advancedRow);

        if (editor.useAdvancedMeshReplacementForCreatorProp.boolValue)
        {
            if (editor.compressAdvancedMeshPayloadForCreatorProp != null)
            {
                card.Add(CreateToggle(
                    "GZip Native Mesh Payload",
                    "Compress the native mesh payload before XOR encryption. This reduces upload size but adds build and first-cache decode cost. Leave disabled for fastest apply.",
                    editor.compressAdvancedMeshPayloadForCreatorProp.boolValue,
                    evt =>
                    {
                        ApplyCreatorChange(() => editor.compressAdvancedMeshPayloadForCreatorProp.boolValue = evt.newValue, false);
                    }));
            }

            card.Add(CreateHelpBox(
                "The uploaded file is still only an XOR .bin encrypted with the original base FBX. Users will apply the native mesh payload from version metadata, not from their local experimental flags.",
                HelpBoxMessageType.Info));
        }

        root.Add(card);
    }

    private void BuildVersionSubmissionSectionUIToolkit(VisualElement root)
    {
        root.Add(CreateSectionLabel("New Version Details:"));

        MCBCreatorHelpBoxElement versionWarningBox = null;
        MCBCreatorHelpBoxElement metadataWarningBox = null;
        Button testButton = null;
        Button submitButton = null;

        Action refreshValidation = () =>
        {
            UpdateSubmissionValidation(versionWarningBox, metadataWarningBox, testButton, submitButton);
        };

        var versionRow = CreateCreatorRow();
        versionRow.AddToClassList("mcb-creator-version");
        versionRow.Add(CreateMutedLabel("Version:"));
        versionRow.Add(CreateIntegerField(newVersionMajor, evt =>
        {
            newVersionMajor = evt.newValue;
            refreshValidation();
        }));
        versionRow.Add(CreateMutedLabel("."));
        versionRow.Add(CreateIntegerField(newVersionMinor, evt =>
        {
            newVersionMinor = evt.newValue;
            refreshValidation();
        }));
        versionRow.Add(CreateMutedLabel("."));
        versionRow.Add(CreateIntegerField(newVersionPatch, evt =>
        {
            newVersionPatch = evt.newValue;
            refreshValidation();
        }));

        var scopeField = new EnumField(newVersionScope);
        scopeField.AddToClassList("mcb-creator-version__scope");
        scopeField.RegisterValueChangedCallback(evt =>
        {
            newVersionScope = (Scope)evt.newValue;
            refreshValidation();
        });
        versionRow.Add(scopeField);
        root.Add(versionRow);

        versionWarningBox = CreateHelpBox(string.Empty, HelpBoxMessageType.Warning);
        root.Add(versionWarningBox);

        root.Add(CreateSectionLabel("Version Title:"));
        var titleField = new TextField { value = newVersionTitle ?? string.Empty };
        titleField.AddToClassList("mcb-creator__version-title");
        titleField.RegisterValueChangedCallback(evt =>
        {
            newVersionTitle = evt.newValue ?? string.Empty;
            refreshValidation();
        });
        root.Add(titleField);

        root.Add(CreateSectionLabel("Changelog (optional):"));
        var changelog = new TextField { multiline = true, value = newChangelog ?? string.Empty };
        changelog.AddToClassList("mcb-creator__changelog");
        changelog.RegisterValueChangedCallback(evt =>
        {
            newChangelog = evt.newValue ?? string.Empty;
            refreshValidation();
        });
        root.Add(changelog);

        metadataWarningBox = CreateHelpBox(string.Empty, HelpBoxMessageType.Warning);
        root.Add(metadataWarningBox);

        var buttonRow = CreateCreatorRow();
        buttonRow.AddToClassList("mcb-creator-submit");
        testButton = CreateTextButton(editor.isSubmitting ? "Building..." : "Test", null, () =>
        {
            string version = $"{newVersionMajor}.{newVersionMinor}.{newVersionPatch}";
            if (EditorUtility.DisplayDialog("Confirm Test Build", "This will create and apply the new version locally without uploading it. The original FBX will be backed up.", "Build and Test", "Cancel"))
            {
                EditorCoroutineUtility.StartCoroutineOwnerless(BuildAndApplyLocalVersionCoroutine());
                RefreshEditorUi();
            }
        });
        testButton.AddToClassList("mcb-creator-submit__button");
        buttonRow.Add(testButton);

        submitButton = CreateTextButton(editor.isSubmitting ? "Submitting..." : "Submit New Version", null, () =>
        {
            hasAttemptedToolkitSubmit = true;
            refreshValidation();
            CreatorSubmissionValidation validation = GetCreatorSubmissionValidation();
            if (!validation.canSubmit)
            {
                return;
            }

            string newVersionString = $"{newVersionMajor}.{newVersionMinor}.{newVersionPatch}";
            string parentInfo = selectedParentVersionObject != null ? $"Parent: {selectedParentVersionObject.version}\n" : "";
            if (EditorUtility.DisplayDialog("Confirm Upload", $"This will create and upload the new version files.\n\nVersion: {newVersionString} ({newVersionScope})\n{parentInfo}This action is irreversible.", "Upload", "Cancel"))
            {
                EditorCoroutineUtility.StartCoroutineOwnerless(SubmitNewVersionCoroutine());
                RefreshEditorUi();
            }
        });
        submitButton.AddToClassList("mcb-button--primary");
        submitButton.AddToClassList("mcb-creator-submit__button");
        buttonRow.Add(submitButton);
        root.Add(buttonRow);

        refreshValidation();
    }

    private void UpdateSubmissionValidation(MCBCreatorHelpBoxElement versionWarningBox, MCBCreatorHelpBoxElement metadataWarningBox, Button testButton, Button submitButton)
    {
        CreatorSubmissionValidation validation = GetCreatorSubmissionValidation();
        SetHelpBoxMessage(versionWarningBox, validation.versionWarning, HelpBoxMessageType.Warning, !validation.isVersionValid);
        bool showMetadataWarning = !validation.hasRequiredVersionMetadata && hasAttemptedToolkitSubmit;
        SetHelpBoxMessage(metadataWarningBox, validation.metadataWarning, HelpBoxMessageType.Warning, showMetadataWarning);

        testButton?.SetEnabled(!editor.isSubmitting && validation.canSubmit);
        submitButton?.SetEnabled(!editor.isSubmitting && validation.canAttemptSubmit);
    }

    private CreatorSubmissionValidation GetCreatorSubmissionValidation()
    {
        string newVersionString = $"{newVersionMajor}.{newVersionMinor}.{newVersionPatch}";
        bool isVersionValid = IsNewVersionValid(newVersionString);
        string baseVer = selectedParentVersionObject?.version ?? "the base version";
        bool requiresParentVersion = RequiresParentVersion();
        string defaultAviVersion = requiresParentVersion ? selectedParentVersionObject?.defaultAviVersion : InitialDefaultAviVersion;
        bool hasRequiredVersionMetadata = HasRequiredNewVersionMetadata(newVersionString, newVersionScope, newVersionTitle, defaultAviVersion, out string metadataValidationMessage);

        bool hasCustomVeinsPayload = editor.includeCustomVeinsForCreatorProp.boolValue &&
                                     editor.customVeinsNormalMapProp.objectReferenceValue != null;
        bool hasDynamicNormalsPayload = editor.includeDynamicNormalsBodyForCreatorProp.boolValue ||
                                        editor.includeDynamicNormalsFlexingForCreatorProp.boolValue;
        bool hasSuggestRealisticPayload = HasSuggestRealisticPayload();
        bool hasVersionPayload = HasModelFileBuildEntryPayload() ||
                                 editor.avatarLogicPrefabProp.objectReferenceValue != null ||
                                 hasCustomVeinsPayload ||
                                 hasDynamicNormalsPayload ||
                                 hasSuggestRealisticPayload ||
                                 editor.customBlendshapesForCreatorProp.arraySize > 0;
        bool canAttemptSubmit = AreModelFileBuildEntriesValid() &&
                                hasVersionPayload &&
                                (!requiresParentVersion || selectedParentVersionObject != null) &&
                                isVersionValid &&
                                hasRequiredVersionMetadata;
        bool canSubmit = canAttemptSubmit && hasRequiredVersionMetadata;

        if (editor.includeCustomVeinsForCreatorProp.boolValue && editor.customVeinsNormalMapProp.objectReferenceValue == null)
        {
            canAttemptSubmit = false;
            canSubmit = false;
        }

        if (editor.includeSuggestRealisticForCreatorProp != null &&
            editor.includeSuggestRealisticForCreatorProp.boolValue &&
            GetSerializedStringList(editor.suggestRealisticMeshPathsForCreatorProp).Count == 0)
        {
            canAttemptSubmit = false;
            canSubmit = false;
        }

        return new CreatorSubmissionValidation
        {
            versionString = newVersionString,
            isVersionValid = isVersionValid,
            versionWarning = $"New version must be higher than the selected parent version (v{baseVer}).",
            hasRequiredVersionMetadata = hasRequiredVersionMetadata,
            metadataWarning = metadataValidationMessage,
            canAttemptSubmit = canAttemptSubmit,
            canSubmit = canSubmit
        };
    }

    private bool IsSelectedAssetOwnedByCurrentUser()
    {
        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null || !selectedAsset.ownerId.HasValue)
        {
            return false;
        }

        int currentUserId = GetCurrentUserId();
        return currentUserId > 0 && selectedAsset.ownerId.Value == currentUserId;
    }

    private static int GetCurrentUserId()
    {
        var auth = AuthenticationService.GetAuth();
        if (auth == null || string.IsNullOrWhiteSpace(auth.user))
        {
            return 0;
        }

        return int.TryParse(auth.user, out var userId) ? userId : 0;
    }

    private void ApplyCreatorChange(Action change, bool refresh = true, bool refreshAll = false)
    {
        if (change == null)
        {
            return;
        }

        editor.serializedObject.Update();
        change.Invoke();
        editor.serializedObject.ApplyModifiedProperties();
        if (editor.customBaseTarget != null)
        {
            EditorUtility.SetDirty(editor.customBaseTarget);
        }

        if (refresh)
        {
            if (refreshAll)
            {
                RefreshEditorUi();
            }
            else
            {
                RefreshCreatorUi();
            }
        }
        else
        {
            editor.Repaint();
        }
    }

    private void RefreshCreatorUi()
    {
        RefreshUIToolkit();
        editor.Repaint();
    }

    private void RefreshEditorUi()
    {
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private ObjectField CreateObjectField(string label, SerializedProperty property, Type objectType, bool allowSceneObjects)
    {
        return CreateObjectField(label, null, property, objectType, allowSceneObjects, evt =>
        {
            property.objectReferenceValue = evt.newValue;
        });
    }

    private ObjectField CreateObjectField(
        string label,
        string tooltip,
        SerializedProperty property,
        Type objectType,
        bool allowSceneObjects,
        Action<ChangeEvent<UnityEngine.Object>> applyValue)
    {
        var field = new ObjectField(label)
        {
            objectType = objectType,
            allowSceneObjects = allowSceneObjects,
            value = property != null ? property.objectReferenceValue : null
        };
        field.tooltip = tooltip;
        field.AddToClassList("mcb-creator__object-field");
        field.RegisterValueChangedCallback(evt =>
        {
            ApplyCreatorChange(() => applyValue?.Invoke(evt));
        });
        return field;
    }

    private TextField CreateTextField(string label, string value, EventCallback<ChangeEvent<string>> onChanged)
    {
        var field = string.IsNullOrEmpty(label) ? new TextField() : new TextField(label);
        field.value = value ?? string.Empty;
        field.AddToClassList("mcb-creator__text-field");
        field.RegisterValueChangedCallback(onChanged);
        return field;
    }

    private IntegerField CreateIntegerField(int value, EventCallback<ChangeEvent<int>> onChanged)
    {
        var field = new IntegerField { value = value };
        field.AddToClassList("mcb-creator-version__number");
        field.RegisterValueChangedCallback(onChanged);
        return field;
    }

    private Toggle CreateToggle(string label, bool value, EventCallback<ChangeEvent<bool>> onChanged)
    {
        return CreateToggle(label, null, value, onChanged);
    }

    private Toggle CreateToggle(string label, string tooltip, bool value, EventCallback<ChangeEvent<bool>> onChanged)
    {
        var toggle = new Toggle(label ?? string.Empty) { value = value };
        toggle.tooltip = tooltip;
        toggle.AddToClassList("mcb-creator-toggle");
        toggle.RegisterValueChangedCallback(onChanged);
        return toggle;
    }

    private static VisualElement CreateCreatorPanel()
    {
        var panel = new VisualElement();
        panel.AddToClassList("mcb-form-card");
        panel.AddToClassList("mcb-creator-panel");
        return panel;
    }

    private static VisualElement CreateCreatorRow()
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-row");
        return row;
    }

    private static Label CreateSectionLabel(string text)
    {
        var label = CreateLabel(text, 12, FontStyle.Bold, new Color(0.78f, 0.78f, 0.78f));
        label.AddToClassList("mcb-label--section");
        label.AddToClassList("mcb-creator__section-label");
        return label;
    }

    private static Label CreateStrongLabel(string text)
    {
        var label = CreateLabel(text, 12, FontStyle.Bold, Color.white);
        label.AddToClassList("mcb-label--strong");
        return label;
    }

    private static Label CreateMutedLabel(string text)
    {
        var label = CreateLabel(text, 12, FontStyle.Normal, new Color(0.64f, 0.64f, 0.64f));
        label.AddToClassList("mcb-label--muted");
        return label;
    }

    private static Label CreateTableHeaderLabel(string text, string className)
    {
        var label = CreateLabel(text, 11, FontStyle.Bold, new Color(0.76f, 0.76f, 0.76f));
        label.AddToClassList(className);
        return label;
    }

    private static Label CreateLabel(string text, int fontSize, FontStyle fontStyle, Color color)
    {
        var label = new Label(text ?? string.Empty);
        label.AddToClassList("mcb-label");
        label.style.fontSize = fontSize;
        label.style.unityFontStyleAndWeight = fontStyle;
        label.style.color = color;
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        return label;
    }

    private VisualElement CreateIconObjectField(
        string label,
        SerializedProperty property,
        Type objectType,
        bool allowSceneObjects,
        VisualElement icon)
    {
        return CreateIconFieldRow(icon, CreateObjectField(label, property, objectType, allowSceneObjects));
    }

    private static VisualElement CreateIconFieldRow(VisualElement icon, VisualElement field)
    {
        var row = CreateCreatorRow();
        row.AddToClassList("mcb-creator-icon-field");
        if (icon != null)
        {
            icon.AddToClassList("mcb-creator-icon-field__icon");
            row.Add(icon);
        }

        if (field != null)
        {
            field.AddToClassList("mcb-creator-icon-field__field");
            row.Add(field);
        }

        return row;
    }

    private static Button CreateTextButton(string text, string tooltip, Action onClick)
    {
        var button = new Button(onClick) { text = text ?? string.Empty, tooltip = tooltip };
        button.AddToClassList("mcb-button");
        return button;
    }

    private static Button CreateIconButton(string tooltip, VisualElement icon, Action onClick)
    {
        var button = new Button(onClick) { tooltip = tooltip };
        button.AddToClassList("mcb-button");
        button.AddToClassList("mcb-button--icon-only");
        if (icon != null)
        {
            icon.AddToClassList("mcb-button-icon");
            button.Add(icon);
        }

        return button;
    }

    private static Button CreateCheckboxButton(string tooltip, bool value, bool enabled, Action<bool> onChanged)
    {
        var button = new Button(() => onChanged?.Invoke(!value)) { tooltip = tooltip };
        button.AddToClassList("mcb-creator-checkbox");
        button.EnableInClassList("mcb-creator-checkbox--checked", value);
        button.SetEnabled(enabled);
        if (value)
        {
            var icon = new MCBCreatorCheckIconElement();
            icon.AddToClassList("mcb-button-icon");
            button.Add(icon);
        }

        return button;
    }

    private static MCBCreatorHelpBoxElement CreateHelpBox(string message, HelpBoxMessageType messageType)
    {
        return new MCBCreatorHelpBoxElement(message, messageType);
    }

    private static void SetHelpBoxMessage(MCBCreatorHelpBoxElement helpBox, string message, HelpBoxMessageType messageType, bool visible)
    {
        if (helpBox == null)
        {
            return;
        }

        helpBox.SetMessage(message, messageType);
        helpBox.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private sealed class MCBCreatorHelpBoxElement : VisualElement
    {
        private readonly Label iconLabel;
        private readonly Label messageLabel;

        public MCBCreatorHelpBoxElement(string message, HelpBoxMessageType messageType)
        {
            AddToClassList("mcb-creator-helpbox");
            iconLabel = new Label();
            iconLabel.AddToClassList("mcb-creator-helpbox__icon");
            Add(iconLabel);

            messageLabel = new Label();
            messageLabel.AddToClassList("mcb-creator-helpbox__text");
            Add(messageLabel);

            SetMessage(message, messageType);
        }

        public void SetMessage(string message, HelpBoxMessageType messageType)
        {
            messageLabel.text = message ?? string.Empty;
            RemoveFromClassList("mcb-creator-helpbox--info");
            RemoveFromClassList("mcb-creator-helpbox--warning");
            RemoveFromClassList("mcb-creator-helpbox--error");
            RemoveFromClassList("mcb-creator-helpbox--none");

            switch (messageType)
            {
                case HelpBoxMessageType.Info:
                    AddToClassList("mcb-creator-helpbox--info");
                    iconLabel.text = "i";
                    break;
                case HelpBoxMessageType.Warning:
                    AddToClassList("mcb-creator-helpbox--warning");
                    iconLabel.text = "!";
                    break;
                case HelpBoxMessageType.Error:
                    AddToClassList("mcb-creator-helpbox--error");
                    iconLabel.text = "!";
                    break;
                default:
                    AddToClassList("mcb-creator-helpbox--none");
                    iconLabel.text = string.Empty;
                    break;
            }
        }
    }

    private sealed class MCBCreatorChevronIconElement : VisualElement
    {
        private readonly bool pointsUp;

        public MCBCreatorChevronIconElement(bool pointsUp)
        {
            this.pointsUp = pointsUp;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += DrawIcon;
        }

        private void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var painter = context.painter2D;
            painter.strokeColor = Color.white;
            painter.lineWidth = 2f;

            Vector2 first = ScalePoint(rect, pointsUp ? new Vector2(8f, 14f) : new Vector2(8f, 10f));
            Vector2 middle = ScalePoint(rect, pointsUp ? new Vector2(12f, 10f) : new Vector2(12f, 14f));
            Vector2 last = ScalePoint(rect, pointsUp ? new Vector2(16f, 14f) : new Vector2(16f, 10f));

            painter.BeginPath();
            painter.MoveTo(first);
            painter.LineTo(middle);
            painter.LineTo(last);
            painter.Stroke();
        }
    }

    private sealed class MCBCreatorDisclosureIconElement : VisualElement
    {
        private readonly bool expanded;

        public MCBCreatorDisclosureIconElement(bool expanded)
        {
            this.expanded = expanded;
            pickingMode = PickingMode.Ignore;
            AddToClassList("mcb-creator-disclosure-icon");
            generateVisualContent += DrawIcon;
        }

        private void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var painter = context.painter2D;
            painter.fillColor = Color.white;

            Vector2 first = ScalePoint(rect, expanded ? new Vector2(6f, 8f) : new Vector2(8f, 6f));
            Vector2 second = ScalePoint(rect, expanded ? new Vector2(18f, 8f) : new Vector2(8f, 18f));
            Vector2 third = ScalePoint(rect, expanded ? new Vector2(12f, 16f) : new Vector2(16f, 12f));

            painter.BeginPath();
            painter.MoveTo(first);
            painter.LineTo(second);
            painter.LineTo(third);
            painter.ClosePath();
            painter.Fill(FillRule.NonZero);
        }
    }

    private sealed class MCBCreatorCheckIconElement : VisualElement
    {
        public MCBCreatorCheckIconElement()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += DrawIcon;
        }

        private void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var painter = context.painter2D;
            painter.fillColor = Color.white;

            Vector2[] points =
            {
                new Vector2(4f, 13.0711f),
                new Vector2(5.41421f, 11.6569f),
                new Vector2(9.65686f, 15.8995f),
                new Vector2(19.5564f, 6f),
                new Vector2(20.9706f, 7.41421f),
                new Vector2(9.65686f, 18.7279f)
            };

            painter.BeginPath();
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 point = ScalePoint(rect, points[i]);
                if (i == 0)
                {
                    painter.MoveTo(point);
                }
                else
                {
                    painter.LineTo(point);
                }
            }

            painter.ClosePath();
            painter.Fill(FillRule.NonZero);
        }
    }

    private sealed class MCBCreatorPrefabIconElement : VisualElement
    {
        public MCBCreatorPrefabIconElement()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += DrawIcon;
        }

        private void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var painter = context.painter2D;
            painter.fillColor = new Color(0.486f, 0.996f, 0.961f);
            Vector2[] shell =
            {
                new Vector2(12f, 2.4f),
                new Vector2(21f, 7.5f),
                new Vector2(21f, 16.1f),
                new Vector2(12f, 21.6f),
                new Vector2(3f, 16.1f),
                new Vector2(3f, 7.5f)
            };
            DrawPolygon(painter, rect, shell);
        }
    }

    private sealed class MCBCreatorChipIconElement : VisualElement
    {
        public MCBCreatorChipIconElement()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += DrawIcon;
        }

        private void DrawIcon(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var painter = context.painter2D;
            painter.strokeColor = new Color(0.486f, 0.996f, 0.961f);
            painter.lineWidth = 2f;

            DrawStrokeRect(painter, rect, 5.25f, 5.25f, 13.5f, 13.5f);
            DrawStrokeRect(painter, rect, 8.25f, 8.25f, 7.5f, 7.5f);

            float[] pinPositions = { 7.5f, 10.5f, 13.5f, 16.5f };
            foreach (float x in pinPositions)
            {
                DrawLine(painter, rect, new Vector2(x, 2.25f), new Vector2(x, 5.25f));
                DrawLine(painter, rect, new Vector2(x, 18.75f), new Vector2(x, 21.75f));
            }

            foreach (float y in pinPositions)
            {
                DrawLine(painter, rect, new Vector2(2.25f, y), new Vector2(5.25f, y));
                DrawLine(painter, rect, new Vector2(18.75f, y), new Vector2(21.75f, y));
            }
        }
    }

    private static void DrawRect(Painter2D painter, Rect rect, float x, float y, float width, float height)
    {
        Vector2[] points =
        {
            new Vector2(x, y),
            new Vector2(x + width, y),
            new Vector2(x + width, y + height),
            new Vector2(x, y + height)
        };
        DrawPolygon(painter, rect, points);
    }

    private static void DrawStrokeRect(Painter2D painter, Rect rect, float x, float y, float width, float height)
    {
        painter.BeginPath();
        painter.MoveTo(ScalePoint(rect, new Vector2(x, y)));
        painter.LineTo(ScalePoint(rect, new Vector2(x + width, y)));
        painter.LineTo(ScalePoint(rect, new Vector2(x + width, y + height)));
        painter.LineTo(ScalePoint(rect, new Vector2(x, y + height)));
        painter.ClosePath();
        painter.Stroke();
    }

    private static void DrawLine(Painter2D painter, Rect rect, Vector2 start, Vector2 end)
    {
        painter.BeginPath();
        painter.MoveTo(ScalePoint(rect, start));
        painter.LineTo(ScalePoint(rect, end));
        painter.Stroke();
    }

    private static void DrawPolygon(Painter2D painter, Rect rect, Vector2[] points)
    {
        painter.BeginPath();
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 point = ScalePoint(rect, points[i]);
            if (i == 0)
            {
                painter.MoveTo(point);
            }
            else
            {
                painter.LineTo(point);
            }
        }

        painter.ClosePath();
        painter.Fill(FillRule.NonZero);
    }

    private static Vector2 ScalePoint(Rect rect, Vector2 point)
    {
        const float viewSize = 24f;
        float scale = Mathf.Min(rect.width / viewSize, rect.height / viewSize);
        float offsetX = rect.x + (rect.width - viewSize * scale) * 0.5f;
        float offsetY = rect.y + (rect.height - viewSize * scale) * 0.5f;
        return new Vector2(offsetX + point.x * scale, offsetY + point.y * scale);
    }
}
#endif
