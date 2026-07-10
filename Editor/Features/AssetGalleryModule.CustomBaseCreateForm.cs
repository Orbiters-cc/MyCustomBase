#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

public partial class AssetGalleryModule
{
    private void BuildCreateCustomBaseFormUIToolkit(VisualElement root)
    {
        var header = CreateRow();
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 12f;
        var backButton = CreateTextButton("< Back to custom bases", () =>
        {
            isCreatingCustomBase = false;
            createError = null;
            ResetPhotoshootState(destroyPreviewTexture: true);
            editor.RefreshUiToolkitSections();
        });
        backButton.style.width = 165f;
        header.Add(backButton);
        var title = CreateLabel("Create custom base", 15, FontStyle.Bold, Color.white);
        title.style.marginLeft = 12f;
        header.Add(title);
        root.Add(header);

        BuildCreateSceneModeSectionUIToolkit(root);
        if (createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized)
        {
            BuildPhotoshootSectionUIToolkit(root);
        }

        var form = new VisualElement();
        form.AddToClassList("mcb-form-card");
        root.Add(form);

        var nameField = new TextField("Name") { value = createName };
        nameField.RegisterValueChangedCallback(evt =>
        {
            createName = Regex.Replace(evt.newValue ?? string.Empty, @"[^a-zA-Z0-9 ]", string.Empty);
            if (nameField.value != createName)
            {
                nameField.SetValueWithoutNotify(createName);
            }
        });
        form.Add(nameField);

        var descriptionField = new TextField("Description") { multiline = true, value = createDescription };
        descriptionField.style.minHeight = 70f;
        descriptionField.RegisterValueChangedCallback(evt => createDescription = evt.newValue ?? "");
        form.Add(descriptionField);

        var jinxxyField = new TextField("Jinxxy Link") { value = createJinxxyLink };
        jinxxyField.RegisterValueChangedCallback(evt => createJinxxyLink = evt.newValue ?? "");
        form.Add(jinxxyField);

        var gumroadField = new TextField("Gumroad Link") { value = createGumroadLink };
        gumroadField.RegisterValueChangedCallback(evt => createGumroadLink = evt.newValue ?? "");
        form.Add(gumroadField);

        BuildAvatarBaseFormFieldsUIToolkit(form);

        if (createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized)
        {
            BuildOriginalSourceKeySectionUIToolkit(form);
        }

        if (!string.IsNullOrWhiteSpace(createError))
        {
            form.Add(CreateMessageLabel(createError, new Color(1f, 0.55f, 0.35f)));
        }

        var nextButton = CreateTextButton(isSubmittingCustomBase ? "Creating..." : "Next", () =>
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(CreateCustomBaseAssetCoroutine());
            editor.RefreshUiToolkitSections();
        });
        nextButton.style.marginTop = 12f;
        nextButton.style.height = 32f;
        nextButton.SetEnabled(!isSubmittingCustomBase && IsCreateFormValid());
        form.Add(nextButton);
    }

    private void BuildAvatarBaseFormFieldsUIToolkit(VisualElement form)
    {
        if (isLoadingAvatarBases)
        {
            form.Add(CreateMessageLabel("Loading base avatars...", new Color(0.70f, 0.78f, 0.86f)));
            return;
        }

        if (!string.IsNullOrWhiteSpace(avatarBaseLoadError))
        {
            form.Add(CreateMessageLabel(avatarBaseLoadError, new Color(1f, 0.64f, 0.28f)));
            form.Add(CreateTextButton("Retry Avatar Bases", StartAvatarBaseLoad));
        }

        var options = avatarBaseOptions
            .Select(option => option.name)
            .Concat(new[] { "Other" })
            .ToList();

        selectedAvatarBaseIndex = Mathf.Clamp(selectedAvatarBaseIndex, 0, Mathf.Max(0, options.Count - 1));
        var dropdown = new DropdownField("Base Avatar", options, selectedAvatarBaseIndex);
        dropdown.AddToClassList("mcb-dropdown");
        dropdown.RegisterValueChangedCallback(evt =>
        {
            selectedAvatarBaseIndex = Mathf.Max(0, options.IndexOf(evt.newValue));
            editor.RefreshUiToolkitSections();
        });
        form.Add(dropdown);

        if (IsOtherAvatarBaseSelected())
        {
            var otherField = new TextField("Avatar Base Name") { value = otherAvatarBaseName };
            otherField.RegisterValueChangedCallback(evt => otherAvatarBaseName = evt.newValue ?? "");
            form.Add(otherField);
        }

        var fbxTitle = CreateLabel("Target FBX Files", 12, FontStyle.Bold, Color.white);
        fbxTitle.style.marginTop = 10f;
        form.Add(fbxTitle);

        if (targetFbxFiles.Count == 0)
        {
            targetFbxFiles.Add(null);
        }

        for (int i = 0; i < targetFbxFiles.Count; i++)
        {
            int index = i;
            var row = CreateRow();
            row.style.alignItems = Align.Center;
            row.style.minWidth = 0f;

            var field = new ObjectField($"FBX {index + 1}") { objectType = typeof(GameObject), allowSceneObjects = false, value = targetFbxFiles[index] };
            field.labelElement.style.width = 56f;
            field.labelElement.style.minWidth = 56f;
            field.style.flexBasis = 0f;
            field.style.flexGrow = 1f;
            field.style.flexShrink = 1f;
            field.style.minWidth = 0f;
            field.RegisterValueChangedCallback(evt =>
            {
                targetFbxFiles[index] = evt.newValue as GameObject;
                editor.RefreshUiToolkitSections();
            });
            row.Add(field);
            var remove = CreateTextButton("-", () =>
            {
                targetFbxFiles.RemoveAt(index);
                editor.RefreshUiToolkitSections();
            });
            remove.tooltip = "Remove target FBX";
            remove.style.width = 28f;
            remove.style.minWidth = 28f;
            remove.style.maxWidth = 28f;
            remove.style.flexShrink = 0f;
            remove.style.marginLeft = 8f;
            row.Add(remove);
            form.Add(row);

            string path = targetFbxFiles[index] != null ? AssetDatabase.GetAssetPath(targetFbxFiles[index]) : null;
            if (targetFbxFiles[index] != null && !IsValidFbxPath(path))
            {
                form.Add(CreateMessageLabel("Target files must be FBX model assets.", new Color(1f, 0.64f, 0.28f)));
            }
        }

        var addButton = CreateTextButton("Add Target FBX", () =>
        {
            targetFbxFiles.Add(null);
            editor.RefreshUiToolkitSections();
        });
        addButton.style.width = 140f;
        addButton.style.marginTop = 6f;
        form.Add(addButton);
    }

    private void BuildCreateSceneModeSectionUIToolkit(VisualElement root)
    {
        var panel = new VisualElement();
        panel.AddToClassList("mcb-form-card");
        panel.style.marginBottom = 10f;
        root.Add(panel);

        panel.Add(CreateLabel("What is in the scene right now?", 12, FontStyle.Bold, Color.white));
        var options = new List<string>
        {
            "Already customized base",
            "Original/default base"
        };
        int selectedIndex = createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized ? 0 : 1;
        var dropdown = new DropdownField(options, selectedIndex);
        dropdown.AddToClassList("mcb-dropdown");
        dropdown.RegisterValueChangedCallback(evt =>
        {
            createSceneMode = evt.newValue == options[0]
                ? CreateCustomBaseSceneMode.AlreadyCustomized
                : CreateCustomBaseSceneMode.DefaultBase;
            createError = null;
            editor.RefreshUiToolkitSections();
        });
        panel.Add(dropdown);

        string helpText = createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized
            ? "This scene is the custom base you want to share. Import the original/default FBX files so MCB can create the encryption keys."
            : "You will customize the selected default base after creating the asset. Photoshoot is skipped because the scene is not the final custom base yet.";
        panel.Add(CreateMessageLabel(helpText, new Color(0.70f, 0.78f, 0.86f)));
    }

    private void BuildOriginalSourceKeySectionUIToolkit(VisualElement form)
    {
        SyncOriginalSourceKeyMappings();

        var title = CreateLabel("Original/default FBX files", 12, FontStyle.Bold, Color.white);
        title.style.marginTop = 10f;
        form.Add(title);
        form.Add(CreateMessageLabel("These files are used as encryption keys. MCB extracts only FBX entries from .unitypackage files and writes them next to each target as .originalbase.", new Color(0.70f, 0.78f, 0.86f)));

        var importRow = CreateRow();
        importRow.style.marginTop = 6f;
        importRow.Add(CreateTextButton("Add .unitypackage", BrowseOriginalUnityPackage));
        importRow.Add(CreateTextButton("Add FBX", BrowseOriginalFbx));
        importRow.Add(CreateTextButton("Clear sources", () =>
        {
            originalSourceKeyCandidates.Clear();
            originalSourceKeyMappings.Clear();
            editor.RefreshUiToolkitSections();
        }));
        form.Add(importRow);

        if (originalSourceKeyCandidates.Count == 0)
        {
            form.Add(CreateMessageLabel("Add the original/default source package or FBX files before creating this custom base.", new Color(1f, 0.64f, 0.28f)));
            return;
        }

        for (int i = 0; i < originalSourceKeyMappings.Count; i++)
        {
            var mapping = originalSourceKeyMappings[i];
            var targetLabel = CreateLabel(mapping.localTargetPath ?? $"Target {i + 1}", 11, FontStyle.Bold, new Color(0.86f, 0.90f, 0.95f));
            targetLabel.style.marginTop = 8f;
            form.Add(targetLabel);

            var candidateOptions = new[] { "Select original FBX..." }
                .Concat(originalSourceKeyCandidates
                .Select((candidate, index) => $"{index + 1}. {candidate.displayName}")
                .ToList())
                .ToList();
            mapping.selectedCandidateIndex = Mathf.Clamp(mapping.selectedCandidateIndex, -1, originalSourceKeyCandidates.Count - 1);
            int dropdownIndex = mapping.selectedCandidateIndex >= 0 ? mapping.selectedCandidateIndex + 1 : 0;
            var candidateDropdown = new DropdownField("Original FBX", candidateOptions, dropdownIndex);
            candidateDropdown.AddToClassList("mcb-dropdown");
            candidateDropdown.RegisterValueChangedCallback(evt =>
            {
                int selected = candidateOptions.IndexOf(evt.newValue) - 1;
                SelectOriginalSourceCandidate(mapping, selected);
                editor.RefreshUiToolkitSections();
            });
            form.Add(candidateDropdown);

            var referenceField = new TextField("Reference path") { value = mapping.referenceSourcePath ?? "" };
            referenceField.RegisterValueChangedCallback(evt => mapping.referenceSourcePath = AvatarPathOverrideService.NormalizeUnityPath(evt.newValue));
            form.Add(referenceField);
        }
    }

    private void OpenCreateCustomBaseForm()
    {
        isCreatingCustomBase = true;
        createError = null;
        selectedAvatarBaseIndex = 0;
        targetFbxFiles.Clear();
        foreach (string path in editor.GetDetectedAvatarFbxPaths())
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (fbx != null)
            {
                targetFbxFiles.Add(fbx);
            }
        }
        ResetPhotoshootState(destroyPreviewTexture: true);
        LoadAvatarBasesIfNeeded();
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private void DrawCreateCustomBaseForm()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var backStyle = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    normal = { textColor = new Color(0.75f, 0.85f, 1f) },
                    alignment = TextAnchor.MiddleLeft
                };

                if (GUILayout.Button("\u2190 Back to custom bases", backStyle, GUILayout.Width(150f), GUILayout.Height(22f)))
                {
                    isCreatingCustomBase = false;
                    createError = null;
                    ResetPhotoshootState(destroyPreviewTexture: true);
                    editor.Repaint();
                    GUIUtility.ExitGUI();
                }

                GUILayout.Space(10f);
                EditorGUILayout.LabelField("Create custom base", EditorStyles.boldLabel);
            }

            EditorGUILayout.Space(8f);
            createFormScrollPosition = EditorGUILayout.BeginScrollView(createFormScrollPosition);

            DrawCreateSceneModeIMGUI();
            EditorGUILayout.Space(6f);

            using (new EditorGUI.DisabledScope(isSubmittingCustomBase))
            {
                if (createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized)
                {
                    createThumbnail = EditorGUILayout.ObjectField("Thumbnail", createThumbnail, typeof(Texture2D), false) as Texture2D;
                    createBanner = EditorGUILayout.ObjectField("Banner", createBanner, typeof(Texture2D), false) as Texture2D;
                }

                EditorGUI.BeginChangeCheck();
                string nextName = EditorGUILayout.TextField("Name", createName);
                if (EditorGUI.EndChangeCheck())
                {
                    createName = Regex.Replace(nextName ?? string.Empty, @"[^a-zA-Z0-9 ]", string.Empty);
                }

                EditorGUILayout.LabelField("Description");
                createDescription = EditorGUILayout.TextArea(createDescription, GUILayout.MinHeight(70f));
                createJinxxyLink = EditorGUILayout.TextField("Jinxxy Link", createJinxxyLink);
                createGumroadLink = EditorGUILayout.TextField("Gumroad Link", createGumroadLink);

                DrawAvatarBaseDropdown();
                if (createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized)
                {
                    DrawOriginalSourceKeySectionIMGUI();
                }
            }

            if (!string.IsNullOrWhiteSpace(createError))
            {
                EditorGUILayout.HelpBox(createError, MessageType.Error);
            }

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(isSubmittingCustomBase || !IsCreateFormValid()))
            {
                if (GUILayout.Button(isSubmittingCustomBase ? "Creating..." : "Next", GUILayout.Height(32f)))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(CreateCustomBaseAssetCoroutine());
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawCreateSceneModeIMGUI()
    {
        EditorGUILayout.LabelField("What is in the scene right now?", EditorStyles.miniBoldLabel);
        string[] options =
        {
            "Already customized base",
            "Original/default base"
        };
        int selectedIndex = createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized ? 0 : 1;
        EditorGUI.BeginChangeCheck();
        selectedIndex = EditorGUILayout.Popup("Scene content", selectedIndex, options);
        if (EditorGUI.EndChangeCheck())
        {
            createSceneMode = selectedIndex == 0
                ? CreateCustomBaseSceneMode.AlreadyCustomized
                : CreateCustomBaseSceneMode.DefaultBase;
            createError = null;
        }

        string helpText = createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized
            ? "This scene is the custom base you want to share. Import the original/default FBX files so MCB can create the encryption keys."
            : "You will customize the selected default base after creating the asset. Photoshoot is skipped because the scene is not the final custom base yet.";
        EditorGUILayout.HelpBox(helpText, MessageType.Info);
    }

    private void DrawOriginalSourceKeySectionIMGUI()
    {
        SyncOriginalSourceKeyMappings();

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Original/default FBX files", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox("These files are used as encryption keys. MCB extracts only FBX entries from .unitypackage files and writes them next to each target as .originalbase.", MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add .unitypackage"))
            {
                BrowseOriginalUnityPackage();
            }

            if (GUILayout.Button("Add FBX"))
            {
                BrowseOriginalFbx();
            }

            if (GUILayout.Button("Clear sources"))
            {
                originalSourceKeyCandidates.Clear();
                originalSourceKeyMappings.Clear();
                editor.RefreshUiToolkitSections();
                editor.Repaint();
            }
        }

        if (originalSourceKeyCandidates.Count == 0)
        {
            EditorGUILayout.HelpBox("Add the original/default source package or FBX files before creating this custom base.", MessageType.Warning);
            return;
        }

        for (int i = 0; i < originalSourceKeyMappings.Count; i++)
        {
            var mapping = originalSourceKeyMappings[i];
            EditorGUILayout.LabelField(mapping.localTargetPath ?? $"Target {i + 1}", EditorStyles.boldLabel);

            var candidateOptions = new[] { "Select original FBX..." }
                .Concat(originalSourceKeyCandidates.Select((candidate, index) => $"{index + 1}. {candidate.displayName}"))
                .ToArray();
            mapping.selectedCandidateIndex = Mathf.Clamp(mapping.selectedCandidateIndex, -1, originalSourceKeyCandidates.Count - 1);
            int dropdownIndex = mapping.selectedCandidateIndex >= 0 ? mapping.selectedCandidateIndex + 1 : 0;
            EditorGUI.BeginChangeCheck();
            dropdownIndex = EditorGUILayout.Popup("Original FBX", dropdownIndex, candidateOptions);
            if (EditorGUI.EndChangeCheck())
            {
                SelectOriginalSourceCandidate(mapping, dropdownIndex - 1);
            }

            EditorGUI.BeginChangeCheck();
            string referenceSourcePath = EditorGUILayout.TextField("Reference path", mapping.referenceSourcePath ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                mapping.referenceSourcePath = AvatarPathOverrideService.NormalizeUnityPath(referenceSourcePath);
            }
        }
    }

    private void DrawAvatarBaseDropdown()
    {
        if (isLoadingAvatarBases)
        {
            EditorGUILayout.Popup("Base Avatar", 0, new[] { "Loading..." });
            return;
        }

        if (!string.IsNullOrWhiteSpace(avatarBaseLoadError))
        {
            EditorGUILayout.HelpBox(avatarBaseLoadError, MessageType.Warning);
            if (GUILayout.Button("Retry Avatar Bases"))
            {
                StartAvatarBaseLoad();
            }
        }

        var options = avatarBaseOptions
            .Select(option => option.name)
            .Concat(new[] { "Other" })
            .ToArray();

        selectedAvatarBaseIndex = Mathf.Clamp(selectedAvatarBaseIndex, 0, Mathf.Max(0, options.Length - 1));
        selectedAvatarBaseIndex = EditorGUILayout.Popup("Base Avatar", selectedAvatarBaseIndex, options);

        if (IsOtherAvatarBaseSelected())
        {
            otherAvatarBaseName = EditorGUILayout.TextField("Avatar Base Name", otherAvatarBaseName);
        }

        DrawTargetFbxFiles();
    }

    private void DrawTargetFbxFiles()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Target FBX Files", EditorStyles.miniBoldLabel);

        if (targetFbxFiles.Count == 0)
        {
            targetFbxFiles.Add(null);
        }

        for (int i = 0; i < targetFbxFiles.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                targetFbxFiles[i] = EditorGUILayout.ObjectField($"FBX {i + 1}", targetFbxFiles[i], typeof(GameObject), false) as GameObject;
                if (GUILayout.Button("-", GUILayout.Width(24f)))
                {
                    targetFbxFiles.RemoveAt(i);
                    i--;
                    continue;
                }
            }

            string path = targetFbxFiles[i] != null ? AssetDatabase.GetAssetPath(targetFbxFiles[i]) : null;
            if (targetFbxFiles[i] != null && !IsValidFbxPath(path))
            {
                EditorGUILayout.HelpBox("Target files must be FBX model assets.", MessageType.Warning);
            }
            else if (targetFbxFiles[i] != null)
            {
                DrawSmrPathsForTargetFbx(path);
            }
        }

        if (GUILayout.Button("Add Target FBX", GUILayout.Width(140f)))
        {
            targetFbxFiles.Add(null);
        }
    }

    private void DrawSmrPathsForTargetFbx(string targetPath)
    {
        if (editor?.customBaseTarget == null || string.IsNullOrWhiteSpace(targetPath)) return;

        var entries = SmrPathService.CollectSmrPathsForFbx(editor.customBaseTarget.transform.root, targetPath);
        if (entries.Count == 0)
        {
            EditorGUILayout.HelpBox("No avatar SkinnedMeshRenderer currently uses this FBX.", MessageType.Warning);
            return;
        }

        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField("Avatar SMR Paths", EditorStyles.miniBoldLabel);
        foreach (var entry in entries)
        {
            string label = string.IsNullOrWhiteSpace(entry.meshName)
                ? entry.avatarPath
                : $"{entry.avatarPath}  ->  {entry.meshName}";
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
        }
        EditorGUI.indentLevel--;
    }

    private bool IsCreateFormValid()
    {
        if (string.IsNullOrWhiteSpace(createName) || !Regex.IsMatch(createName, @"^[a-zA-Z0-9 ]+$"))
        {
            return false;
        }

        if (GetValidTargetFbxPaths().Count == 0)
        {
            return false;
        }

        if (createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized &&
            !AreOriginalSourceKeyMappingsValid())
        {
            return false;
        }

        if (!IsOtherAvatarBaseSelected())
        {
            return selectedAvatarBaseIndex >= 0 && selectedAvatarBaseIndex < avatarBaseOptions.Count;
        }

        return !string.IsNullOrWhiteSpace(otherAvatarBaseName);
    }

    private bool IsOtherAvatarBaseSelected()
    {
        return selectedAvatarBaseIndex >= avatarBaseOptions.Count;
    }

    private static bool IsValidFbxPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
               AssetImporter.GetAtPath(path) is ModelImporter;
    }

    private List<string> GetValidTargetFbxPaths()
    {
        return targetFbxFiles
            .Where(fbx => fbx != null)
            .Select(AssetDatabase.GetAssetPath)
            .Where(IsValidFbxPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void BrowseOriginalUnityPackage()
    {
        string path = EditorUtility.OpenFilePanel("Select original/default avatar unitypackage", "", "unitypackage");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var extracted = UnityPackageFbxSourceExtractor.ExtractFbxEntries(path);
            if (extracted.Count == 0)
            {
                createError = "No FBX files were found in the selected .unitypackage.";
                editor.RefreshUiToolkitSections();
                return;
            }

            foreach (var entry in extracted)
            {
                AddOriginalSourceCandidate(new OriginalSourceKeyCandidate
                {
                    displayName = $"{entry.publishedSourcePath} ({Path.GetFileName(path)})",
                    publishedSourcePath = entry.publishedSourcePath,
                    externalPath = entry.tempPath,
                    hash = entry.hash,
                    importKind = AvatarPathOverrideService.SourceImportKindUnityPackage,
                    packagePath = path
                });
            }

            createError = null;
            SyncOriginalSourceKeyMappings();
        }
        catch (Exception ex)
        {
            createError = $"Failed to read .unitypackage: {ex.Message}";
        }

        editor.RefreshUiToolkitSections();
    }

    private void BrowseOriginalFbx()
    {
        string path = EditorUtility.OpenFilePanel("Select original/default FBX", "", "fbx");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            string hash = MCBUtils.CalculateFileHash(path);
            if (string.IsNullOrWhiteSpace(hash))
            {
                createError = "Could not hash the selected FBX.";
                editor.RefreshUiToolkitSections();
                return;
            }

            AddOriginalSourceCandidate(new OriginalSourceKeyCandidate
            {
                displayName = Path.GetFileName(path),
                publishedSourcePath = InferProjectUnityPath(path),
                externalPath = path,
                hash = hash,
                importKind = AvatarPathOverrideService.SourceImportKindRawFbx,
                packagePath = ""
            });
            createError = null;
            SyncOriginalSourceKeyMappings();
        }
        catch (Exception ex)
        {
            createError = $"Failed to read FBX: {ex.Message}";
        }

        editor.RefreshUiToolkitSections();
    }

    private void AddOriginalSourceCandidate(OriginalSourceKeyCandidate candidate)
    {
        if (candidate == null ||
            string.IsNullOrWhiteSpace(candidate.externalPath) ||
            string.IsNullOrWhiteSpace(candidate.hash))
        {
            return;
        }

        if (originalSourceKeyCandidates.Any(existing =>
                existing != null &&
                string.Equals(existing.hash, candidate.hash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.publishedSourcePath, candidate.publishedSourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        originalSourceKeyCandidates.Add(candidate);
    }

    private void SyncOriginalSourceKeyMappings()
    {
        var targetPaths = GetValidTargetFbxPaths();
        originalSourceKeyMappings.RemoveAll(mapping =>
            mapping == null ||
            string.IsNullOrWhiteSpace(mapping.localTargetPath) ||
            !targetPaths.Contains(mapping.localTargetPath, StringComparer.OrdinalIgnoreCase));

        foreach (string targetPath in targetPaths)
        {
            var mapping = originalSourceKeyMappings.FirstOrDefault(entry =>
                entry != null &&
                string.Equals(entry.localTargetPath, targetPath, StringComparison.OrdinalIgnoreCase));
            if (mapping == null)
            {
                mapping = new OriginalSourceKeyMapping { localTargetPath = targetPath };
                originalSourceKeyMappings.Add(mapping);
            }

            AutoSelectOriginalSourceCandidate(mapping, targetPaths.Count);
        }
    }

    private void AutoSelectOriginalSourceCandidate(OriginalSourceKeyMapping mapping, int targetCount)
    {
        if (mapping == null || mapping.selectedCandidateIndex >= 0 || originalSourceKeyCandidates.Count == 0)
        {
            return;
        }

        string targetPath = AvatarPathOverrideService.NormalizeUnityPath(mapping.localTargetPath);
        int exactPathIndex = originalSourceKeyCandidates.FindIndex(candidate =>
            string.Equals(AvatarPathOverrideService.NormalizeUnityPath(candidate.publishedSourcePath), targetPath, StringComparison.OrdinalIgnoreCase));
        if (exactPathIndex >= 0)
        {
            SelectOriginalSourceCandidate(mapping, exactPathIndex);
            return;
        }

        string targetFileName = Path.GetFileName(targetPath);
        var fileNameMatches = originalSourceKeyCandidates
            .Select((candidate, index) => new { candidate, index })
            .Where(item =>
                string.Equals(Path.GetFileName(item.candidate.publishedSourcePath), targetFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(item.candidate.externalPath), targetFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fileNameMatches.Count == 1)
        {
            SelectOriginalSourceCandidate(mapping, fileNameMatches[0].index);
            return;
        }

        if (targetCount == 1 && originalSourceKeyCandidates.Count == 1)
        {
            SelectOriginalSourceCandidate(mapping, 0);
        }
    }

    private void SelectOriginalSourceCandidate(OriginalSourceKeyMapping mapping, int candidateIndex)
    {
        mapping.selectedCandidateIndex = candidateIndex;
        var candidate = candidateIndex >= 0 && candidateIndex < originalSourceKeyCandidates.Count
            ? originalSourceKeyCandidates[candidateIndex]
            : null;
        if (!string.IsNullOrWhiteSpace(candidate?.publishedSourcePath))
        {
            mapping.referenceSourcePath = AvatarPathOverrideService.NormalizeUnityPath(candidate.publishedSourcePath);
        }
        else if (string.IsNullOrWhiteSpace(mapping.referenceSourcePath))
        {
            mapping.referenceSourcePath = "";
        }
    }

    private bool AreOriginalSourceKeyMappingsValid()
    {
        SyncOriginalSourceKeyMappings();
        if (originalSourceKeyMappings.Count == 0 || originalSourceKeyCandidates.Count == 0)
        {
            return false;
        }

        return originalSourceKeyMappings.All(mapping =>
            mapping != null &&
            mapping.selectedCandidateIndex >= 0 &&
            mapping.selectedCandidateIndex < originalSourceKeyCandidates.Count &&
            IsValidReferenceSourcePath(mapping.referenceSourcePath) &&
            File.Exists(originalSourceKeyCandidates[mapping.selectedCandidateIndex].externalPath));
    }

    private static string InferProjectUnityPath(string absoluteOrUnityPath)
    {
        string normalized = AvatarPathOverrideService.NormalizeUnityPath(absoluteOrUnityPath);
        return IsValidReferenceSourcePath(normalized) ? normalized : "";
    }

    private static bool IsValidReferenceSourcePath(string path)
    {
        string normalized = AvatarPathOverrideService.NormalizeUnityPath(path);
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
               (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));
    }

    private void LoadAvatarBasesIfNeeded()
    {
        if ((avatarBaseOptions != null && avatarBaseOptions.Count > 0) || isLoadingAvatarBases)
        {
            return;
        }

        StartAvatarBaseLoad();
    }

    private void StartAvatarBaseLoad()
    {
        EditorCoroutineUtility.StartCoroutineOwnerless(LoadAvatarBasesCoroutine());
    }

    private IEnumerator LoadAvatarBasesCoroutine()
    {
        isLoadingAvatarBases = true;
        avatarBaseLoadError = null;
        editor.Repaint();

        string url = $"{MCBUtils.getApiUrl()}/avatar-bases?t={editor.authToken}";
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery);
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Load avatar bases"));

            if (request.result != UnityWebRequest.Result.Success)
            {
                avatarBaseLoadError = $"Failed to load base avatars: HTTP {request.responseCode} {request.error}";
            }
            else
            {
                try
                {
                    var response = JsonConvert.DeserializeObject<CreatorAvatarBasesResponse>(request.downloadHandler.text);
                    avatarBaseOptions = response?.avatarBases?
                        .Where(option => option != null && option.id > 0)
                        .OrderBy(option => option.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new List<CreatorAvatarBaseOption>();
                }
                catch (Exception ex)
                {
                    avatarBaseLoadError = $"Failed to parse base avatars: {ex.Message}";
                }
            }
        }

        isLoadingAvatarBases = false;
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private IEnumerator CreateCustomBaseAssetCoroutine()
    {
        if (isSubmittingCustomBase || !IsCreateFormValid())
        {
            yield break;
        }

        isSubmittingCustomBase = true;
        createError = null;
        editor.Repaint();

        List<ModelFileData> sourceFilePayload;
        try
        {
            sourceFilePayload = createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized
                ? InstallOriginalBaseKeysAndBuildSourcePayload()
                : BuildSourceFilePayload(GetValidTargetFbxPaths());
        }
        catch (Exception ex)
        {
            createError = ex.Message;
            isSubmittingCustomBase = false;
            editor.RefreshUiToolkitSections();
            editor.Repaint();
            yield break;
        }

        var metadata = new JObject
        {
            ["name"] = createName.Trim(),
            ["description"] = createDescription?.Trim() ?? string.Empty,
            ["jinxxyLink"] = string.IsNullOrWhiteSpace(createJinxxyLink) ? null : createJinxxyLink.Trim(),
            ["gumroadLink"] = string.IsNullOrWhiteSpace(createGumroadLink) ? null : createGumroadLink.Trim(),
            ["mcbCreateSceneMode"] = createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized
                ? "already-customized"
                : "default-base"
        };

        metadata["sourceFiles"] = JArray.FromObject(sourceFilePayload);
        if (IsOtherAvatarBaseSelected())
        {
            metadata["otherAvatarBaseName"] = otherAvatarBaseName.Trim();
        }
        else
        {
            metadata["avatarBaseId"] = avatarBaseOptions[selectedAvatarBaseIndex].id;
        }

        var form = new WWWForm();
        form.AddField("metadata", metadata.ToString(Formatting.None));
        AddImageToForm(form, "thumbnail", createThumbnail);
        AddImageToForm(form, "banner", createBanner);

        string url = $"{MCBUtils.getApiUrl()}/assets/custom-base?t={editor.authToken}";
        using (var request = UnityWebRequest.Post(url, form))
        {
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.Upload);
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Create custom base"));

            if (request.result != UnityWebRequest.Result.Success)
            {
                createError = ExtractErrorMessage(request.downloadHandler?.text) ?? $"Failed to create custom base: HTTP {request.responseCode} {request.error}";
            }
            else
            {
                try
                {
                    var response = JsonConvert.DeserializeObject<CreateCustomBaseAssetResponse>(request.downloadHandler.text);
                    if (response?.asset == null || response.asset.id <= 0)
                    {
                        throw new InvalidOperationException("The server did not return a valid asset.");
                    }

                    var discoveredAsset = new AvatarDiscoveredAsset
                    {
                        id = response.asset.id,
                        name = response.asset.name,
                        ownerId = response.asset.ownerId ?? GetCurrentUserId(),
                        ownerUsername = response.asset.ownerUsername,
                        ownerAvatarUrl = response.asset.ownerAvatarUrl,
                        thumbnailUrl = response.asset.thumbnail,
                        bannerUrl = response.asset.mcbBanner,
                        avatarBase = response.asset.selectedAvatarBase != null
                            ? new AvatarAssetBaseInfo { id = response.asset.selectedAvatarBase.id, name = response.asset.selectedAvatarBase.name }
                            : null,
                        sourceFiles = response.asset.sourceFiles,
                        isCompatible = true
                    };

                    if (createThumbnail != null && !string.IsNullOrWhiteSpace(discoveredAsset.thumbnailUrl))
                    {
                        AvatarAssetDiscoveryService.CacheThumbnail(discoveredAsset.id, discoveredAsset.thumbnailUrl, createThumbnail);
                    }
                    if (createBanner != null && !string.IsNullOrWhiteSpace(discoveredAsset.bannerUrl))
                    {
                        var cachedBanner = AvatarAssetDiscoveryService.CacheBanner(discoveredAsset.id, discoveredAsset.bannerUrl, createBanner);
                        if (cachedBanner != null)
                        {
                            selectedAssetBannerTextures[discoveredAsset.id] = cachedBanner;
                        }
                    }

                    compatibleAssets.RemoveAll(asset => asset != null && asset.id == discoveredAsset.id);
                    compatibleAssets.Add(discoveredAsset);
                    hasFetchedCompatibleAssets = true;
                    isCreatingCustomBase = false;
                    bool seedAlreadyCustomizedCreatorEntries = createSceneMode == CreateCustomBaseSceneMode.AlreadyCustomized;
                    AvatarPathOverrideService.SyncSourceModelFileIds(editor.customBaseTarget, discoveredAsset.sourceFiles);
                    SelectAsset(discoveredAsset, persist: true, refreshVersions: true);
                    editor.isCreatorModeProp.boolValue = true;
                    if (seedAlreadyCustomizedCreatorEntries)
                    {
                        SeedCreatorBuildEntriesForAlreadyCustomizedBase();
                    }
                    editor.serializedObject.ApplyModifiedProperties();
                    ResetCreateForm();
                }
                catch (Exception ex)
                {
                    createError = $"Failed to parse created custom base: {ex.Message}";
                }
            }
        }

        isSubmittingCustomBase = false;
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private List<ModelFileData> BuildSourceFilePayload(IEnumerable<string> unityPaths)
    {
        var files = new List<ModelFileData>();
        var pathList = (unityPaths ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace("\\", "/"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var smrPathsByFbx = editor?.customBaseTarget != null
            ? SmrPathService.CollectSmrPathsByFbx(editor.customBaseTarget.transform.root, pathList)
            : new Dictionary<string, List<ModelFileSmrPathData>>(StringComparer.OrdinalIgnoreCase);
        foreach (string unityPath in pathList)
        {
            if (string.IsNullOrWhiteSpace(unityPath)) continue;

            string normalizedPath = unityPath.Replace("\\", "/");
            string fullPath = Path.GetFullPath(normalizedPath);
            if (!File.Exists(fullPath)) continue;

            string hash = MCBUtils.CalculateFileHash(fullPath);
            if (string.IsNullOrWhiteSpace(hash)) continue;

            var metas = new List<Dictionary<string, string>>();
            string metaPath = normalizedPath + ".meta";
            if (File.Exists(Path.GetFullPath(metaPath)))
            {
                metas.Add(new Dictionary<string, string>
                {
                    { "file", Path.GetFileName(normalizedPath) },
                    { "meta", File.ReadAllText(Path.GetFullPath(metaPath)) }
                });
            }

            files.Add(new ModelFileData
            {
                path = normalizedPath,
                hash = hash,
                type = "FBX",
                role = "SOURCE",
                metas = metas,
                smrPaths = smrPathsByFbx.TryGetValue(normalizedPath, out var smrEntries)
                    ? smrEntries
                    : new List<ModelFileSmrPathData>()
            });
        }

        return files;
    }

    private List<ModelFileData> InstallOriginalBaseKeysAndBuildSourcePayload()
    {
        if (!AreOriginalSourceKeyMappingsValid())
        {
            throw new InvalidOperationException("Map each target FBX to its original/default source FBX before creating this custom base.");
        }

        var files = new List<ModelFileData>();
        var fileManager = new FileManagerService();
        var localTargetPaths = originalSourceKeyMappings
            .Select(mapping => mapping.localTargetPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var smrPathsByFbx = editor?.customBaseTarget != null
            ? SmrPathService.CollectSmrPathsByFbx(editor.customBaseTarget.transform.root, localTargetPaths)
            : new Dictionary<string, List<ModelFileSmrPathData>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in originalSourceKeyMappings)
        {
            if (mapping == null) continue;
            var candidate = originalSourceKeyCandidates[mapping.selectedCandidateIndex];
            string localTargetPath = AvatarPathOverrideService.NormalizeUnityPath(mapping.localTargetPath);
            string referenceSourcePath = AvatarPathOverrideService.NormalizeUnityPath(mapping.referenceSourcePath);
            if (string.IsNullOrWhiteSpace(localTargetPath) || string.IsNullOrWhiteSpace(referenceSourcePath))
            {
                throw new InvalidOperationException("Original FBX mapping is missing a target or reference path.");
            }

            if (!File.Exists(Path.GetFullPath(localTargetPath)))
            {
                throw new FileNotFoundException("Target FBX file not found.", localTargetPath);
            }

            if (!File.Exists(candidate.externalPath))
            {
                throw new FileNotFoundException("Original/default source FBX file not found.", candidate.externalPath);
            }

            string backupToken = ResolveOrCreatePreMcbBackupToken(fileManager, referenceSourcePath, candidate.hash, localTargetPath);
            string originalBasePath = FileManagerService.GetOriginalBasePath(localTargetPath);
            File.Copy(candidate.externalPath, originalBasePath, true);

            var overrideEntry = AvatarPathOverrideService.UpsertOverride(
                editor.customBaseTarget,
                0,
                referenceSourcePath,
                candidate.hash,
                localTargetPath,
                backupToken,
                candidate.importKind,
                candidate.packagePath);

            var metas = CollectModelImporterMetaForCreatePayload(localTargetPath);
            var sourceFile = new ModelFileData
            {
                path = referenceSourcePath,
                hash = candidate.hash,
                type = "FBX",
                role = "SOURCE",
                metas = metas,
                smrPaths = smrPathsByFbx.TryGetValue(localTargetPath, out var smrEntries)
                    ? smrEntries
                    : new List<ModelFileSmrPathData>()
            };
            AvatarPathOverrideService.ApplyOverrideMetadata(sourceFile, overrideEntry, editor.customBaseTarget);
            files.Add(sourceFile);
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        return files;
    }

    private string ResolveOrCreatePreMcbBackupToken(
        FileManagerService fileManager,
        string referenceSourcePath,
        string referenceHash,
        string localTargetPath)
    {
        var existing = AvatarPathOverrideService.FindOverride(editor.customBaseTarget, referenceSourcePath, 0, referenceHash);
        if (!string.IsNullOrWhiteSpace(existing?.preMcbBackupToken))
        {
            string existingBackup = FileManagerService.GetPreMcbBackupPath(localTargetPath, existing.preMcbBackupToken);
            if (!string.IsNullOrWhiteSpace(existingBackup) && File.Exists(existingBackup))
            {
                return existing.preMcbBackupToken;
            }
        }

        return fileManager.CreatePreMcbBackup(localTargetPath);
    }

    private static List<Dictionary<string, string>> CollectModelImporterMetaForCreatePayload(string unityPath)
    {
        var metas = new List<Dictionary<string, string>>();
        string normalizedPath = AvatarPathOverrideService.NormalizeUnityPath(unityPath);
        if (string.IsNullOrWhiteSpace(normalizedPath)) return metas;

        string metaPath = normalizedPath + ".meta";
        if (File.Exists(Path.GetFullPath(metaPath)))
        {
            metas.Add(new Dictionary<string, string>
            {
                { "file", Path.GetFileName(normalizedPath) },
                { "meta", File.ReadAllText(Path.GetFullPath(metaPath)) }
            });
        }

        return metas;
    }

    private void SeedCreatorBuildEntriesForAlreadyCustomizedBase()
    {
        var validTargets = targetFbxFiles
            .Where(fbx => fbx != null)
            .Where(fbx => IsValidFbxPath(AssetDatabase.GetAssetPath(fbx)))
            .Distinct()
            .ToList();
        if (validTargets.Count == 0 || editor?.modelFileBuildEntriesProp == null || editor.baseFbxFilesProp == null)
        {
            return;
        }

        editor.serializedObject.Update();
        editor.baseFbxFilesProp.ClearArray();
        foreach (var fbx in validTargets)
        {
            editor.baseFbxFilesProp.InsertArrayElementAtIndex(editor.baseFbxFilesProp.arraySize);
            editor.baseFbxFilesProp.GetArrayElementAtIndex(editor.baseFbxFilesProp.arraySize - 1).objectReferenceValue = fbx;
        }

        while (editor.modelFileBuildEntriesProp.arraySize < validTargets.Count)
        {
            editor.modelFileBuildEntriesProp.InsertArrayElementAtIndex(editor.modelFileBuildEntriesProp.arraySize);
        }

        while (editor.modelFileBuildEntriesProp.arraySize > validTargets.Count)
        {
            editor.modelFileBuildEntriesProp.DeleteArrayElementAtIndex(editor.modelFileBuildEntriesProp.arraySize - 1);
        }

        for (int i = 0; i < validTargets.Count; i++)
        {
            var entryProp = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i);
            entryProp.FindPropertyRelative("customFbx").objectReferenceValue = validTargets[i];
            var externalPathProp = entryProp.FindPropertyRelative("externalCustomFbxPath");
            if (externalPathProp != null)
            {
                externalPathProp.stringValue = "";
            }
        }

        editor.serializedObject.ApplyModifiedProperties();
    }

    private static void AddImageToForm(WWWForm form, string fieldName, Texture2D texture)
    {
        if (texture == null)
        {
            return; 
        }

        string assetPath = AssetDatabase.GetAssetPath(texture);
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            byte[] pngBytes = ImageConversion.EncodeToPNG(texture);
            if (pngBytes == null || pngBytes.Length == 0)
            {
                return;
            }

            form.AddBinaryData(fieldName, pngBytes, $"{fieldName}.png", "image/png");
            return;
        }

        string fullPath = Path.GetFullPath(assetPath);
        if (!File.Exists(fullPath))
        {
            return;
        }

        string extension = Path.GetExtension(fullPath).ToLowerInvariant();
        string mimeType = extension == ".jpg" || extension == ".jpeg" ? "image/jpeg" : "image/png";
        form.AddBinaryData(fieldName, File.ReadAllBytes(fullPath), Path.GetFileName(fullPath), mimeType);
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var payload = JsonConvert.DeserializeObject<JObject>(body);
            return payload?.Value<string>("error") ?? payload?.Value<string>("message");
        }
        catch
        {
            return null;
        }
    }

    private void ResetCreateForm()
    {
        ClearTextureField(ref createThumbnail);
        ClearTextureField(ref createBanner);
        createName = "";
        createDescription = "";
        createJinxxyLink = "";
        createGumroadLink = "";
        createSceneMode = CreateCustomBaseSceneMode.AlreadyCustomized;
        selectedAvatarBaseIndex = 0;
        otherAvatarBaseName = "";
        targetFbxFiles.Clear();
        originalSourceKeyCandidates.Clear();
        originalSourceKeyMappings.Clear();
        createError = null;
        ResetPhotoshootState(destroyPreviewTexture: true);
    }
}
#endif
