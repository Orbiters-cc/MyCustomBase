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

        BuildPhotoshootSectionUIToolkit(root);

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

            var field = new ObjectField($"FBX {index + 1}") { objectType = typeof(GameObject), allowSceneObjects = false, value = targetFbxFiles[index] };
            field.style.flexGrow = 1f;
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
            remove.style.width = 28f;
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

            using (new EditorGUI.DisabledScope(isSubmittingCustomBase))
            {
                createThumbnail = EditorGUILayout.ObjectField("Thumbnail", createThumbnail, typeof(Texture2D), false) as Texture2D;
                createBanner = EditorGUILayout.ObjectField("Banner", createBanner, typeof(Texture2D), false) as Texture2D;

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

        var metadata = new JObject
        {
            ["name"] = createName.Trim(),
            ["description"] = createDescription?.Trim() ?? string.Empty,
            ["jinxxyLink"] = string.IsNullOrWhiteSpace(createJinxxyLink) ? null : createJinxxyLink.Trim(),
            ["gumroadLink"] = string.IsNullOrWhiteSpace(createGumroadLink) ? null : createGumroadLink.Trim()
        };

        metadata["sourceFiles"] = JArray.FromObject(BuildSourceFilePayload(GetValidTargetFbxPaths()));
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

                    compatibleAssets.RemoveAll(asset => asset != null && asset.id == discoveredAsset.id);
                    compatibleAssets.Add(discoveredAsset);
                    hasFetchedCompatibleAssets = true;
                    isCreatingCustomBase = false;
                    ResetCreateForm();
                    SelectAsset(discoveredAsset, persist: true, refreshVersions: true);
                    editor.isCreatorModeProp.boolValue = true;
                    editor.serializedObject.ApplyModifiedProperties();
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
        selectedAvatarBaseIndex = 0;
        otherAvatarBaseName = "";
        targetFbxFiles.Clear();
        createError = null;
        ResetPhotoshootState(destroyPreviewTexture: true);
    }
}
#endif
