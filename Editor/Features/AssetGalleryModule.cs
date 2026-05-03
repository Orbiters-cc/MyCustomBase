#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public class CreatorAvatarBaseOption
{
    [JsonProperty] public int id;
    [JsonProperty] public string name;
}

public class CreatorAvatarBasesResponse
{
    [JsonProperty] public List<CreatorAvatarBaseOption> avatarBases;
}

public class CreatorAssetCreateResponseAsset
{
    [JsonProperty] public int id;
    [JsonProperty] public string name;
    [JsonProperty] public string thumbnail;
    [JsonProperty] public string mcbBanner;
    [JsonProperty] public CreatorAvatarBaseOption selectedAvatarBase;
}

public class CreateCustomBaseAssetResponse
{
    [JsonProperty] public CreatorAssetCreateResponseAsset asset;
}

public class AssetGalleryModule
{
    private readonly MCBEditor editor;

    private Vector2 scrollPosition;
    private string lastAvatarSignature;
    private bool hasFetchedCompatibleAssets;
    private bool hasFetchedAllAssets;
    private bool isLoading;
    private string lastError;
    private bool showNonMatchingAssets;
    private bool isCreatingCustomBase;
    private bool isSubmittingCustomBase;
    private string createError;
    private Vector2 createFormScrollPosition;
    private Texture2D createThumbnail;
    private Texture2D createBanner;
    private string createName = "";
    private string createDescription = "";
    private string createJinxxyLink = "";
    private string createGumroadLink = "";
    private int selectedAvatarBaseIndex;
    private GameObject otherAvatarBaseFbx;
    private bool isLoadingAvatarBases;
    private string avatarBaseLoadError;
    private List<CreatorAvatarBaseOption> avatarBaseOptions = new List<CreatorAvatarBaseOption>();

    private List<AvatarDiscoveredAsset> compatibleAssets = new List<AvatarDiscoveredAsset>();
    private List<AvatarDiscoveredAsset> allAssets = new List<AvatarDiscoveredAsset>();

    public AvatarDiscoveredAsset SelectedAsset { get; private set; }
    private string SelectedAssetIdPrefKey => $"MCB.SelectedAssetId.{ComputeStableHash(Application.dataPath)}";

    public AssetGalleryModule(MCBEditor editor)
    {
        this.editor = editor;
    }

    public void OnAuthenticationChanged()
    {
        if (!editor.isAuthenticated)
        {
            ResetState(clearSelection: true);
            return;
        }

        RefreshIfNeeded(force: true);
    }

    public void OnProjectChanged()
    {
        RefreshIfNeeded(force: true);
    }

    public void RefreshIfNeeded(bool force = false)
    {
        if (!editor.isAuthenticated)
        {
            return;
        }

        var currentPaths = editor.GetDetectedAvatarFbxPaths();
        string currentSignature = AvatarAssetDiscoveryService.BuildAvatarSignature(currentPaths);

        bool signatureChanged = !string.Equals(currentSignature, lastAvatarSignature, StringComparison.Ordinal);
        if (!force && !signatureChanged)
        {
            return;
        }

        lastAvatarSignature = currentSignature;
        ResetState(clearSelection: signatureChanged);
        StartDiscovery(filterOnlyCompatible: true);
    }

    public bool ShouldShowGalleryOnly()
    {
        if (!editor.isAuthenticated || SelectedAsset != null)
        {
            return false;
        }

        return isCreatingCustomBase || hasFetchedCompatibleAssets || hasFetchedAllAssets || isLoading;
    }

    public void Draw()
    {
        RefreshIfNeeded();

        if (SelectedAsset != null)
        {
            return;
        }

        if (isCreatingCustomBase)
        {
            DrawCreateCustomBaseForm();
            return;
        }

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Available Custom Bases", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "The gallery is built from the FBX files referenced by the meshes under this avatar root.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4f);

            if (isLoading)
            {
                EditorGUILayout.HelpBox("Discovering avatar assets for the current avatar...", MessageType.Info);
                return;
            }

            if (!string.IsNullOrEmpty(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Retry"))
                    {
                        StartDiscovery(filterOnlyCompatible: !showNonMatchingAssets);
                    }

                    if (!showNonMatchingAssets && GUILayout.Button("Show Other Non Matching Assets"))
                    {
                        showNonMatchingAssets = true;
                        StartDiscovery(filterOnlyCompatible: false);
                    }
                }

                return;
            }

            var matchingAssets = GetMatchingAssets();
            if (matchingAssets.Count == 0)
            {
                EditorGUILayout.HelpBox("No matching avatar assets were found for this avatar.", MessageType.Info);
                DrawCreateCustomBaseCardRow();

                if (!showNonMatchingAssets)
                {
                    if (GUILayout.Button("Show Other Non Matching Assets"))
                    {
                        showNonMatchingAssets = true;
                        StartDiscovery(filterOnlyCompatible: false);
                    }
                }
                else
                {
                    DrawNonMatchingSection();
                }

                return;
            }

            EditorGUILayout.LabelField("Matching Assets", EditorStyles.boldLabel);
            DrawGallery(matchingAssets, includeCreateCard: true);

            EditorGUILayout.Space(6f);
            DrawNonMatchingSection();
        }
    }

    public void DrawSelectedAssetHeader()
    {
        var selectedAsset = SelectedAsset;
        if (selectedAsset == null)
        {
            return;
        }

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
                    SelectedAsset = null;
                    EditorPrefs.DeleteKey(SelectedAssetIdPrefKey);
                    editor.Repaint();
                    GUIUtility.ExitGUI();
                }

                GUILayout.Space(10f);
                EditorGUILayout.LabelField(selectedAsset.name ?? "Unnamed asset", EditorStyles.boldLabel);
            }
        }
    }

    private void DrawGallery(List<AvatarDiscoveredAsset> assets, bool includeCreateCard = false)
    {
        float availableWidth = Mathf.Max(220f, EditorGUIUtility.currentViewWidth - 60f);
        int columns = availableWidth > 700f ? 3 : availableWidth > 420f ? 2 : 1;
        float spacing = 10f;
        float cardWidth = Mathf.Floor((availableWidth - (spacing * (columns - 1))) / columns);
        cardWidth = Mathf.Min(cardWidth, 190f);
        float cardHeight = 254f;
        int totalCount = assets.Count + (includeCreateCard ? 1 : 0);

        for (int i = 0; i < totalCount; i += columns)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int column = 0; column < columns; column++)
                {
                    int assetIndex = i + column;
                    if (assetIndex >= totalCount)
                    {
                        GUILayout.Space(cardWidth);
                        continue;
                    }

                    if (includeCreateCard && assetIndex == assets.Count)
                    {
                        DrawCreateCustomBaseCard(cardWidth, cardHeight);
                    }
                    else
                    {
                        DrawGalleryCard(assets[assetIndex], cardWidth, cardHeight);
                    }
                    if (column < columns - 1)
                    {
                        GUILayout.Space(spacing);
                    }
                }
            }

            GUILayout.Space(8f);
        }
    }

    private void DrawCreateCustomBaseCardRow()
    {
        float availableWidth = Mathf.Max(220f, EditorGUIUtility.currentViewWidth - 60f);
        float cardWidth = Mathf.Min(190f, availableWidth);
        DrawCreateCustomBaseCard(cardWidth, 254f);
        GUILayout.Space(8f);
    }

    private void DrawCreateCustomBaseCard(float width, float height)
    {
        Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
        bool hovered = rect.Contains(Event.current.mousePosition);
        Color backgroundColor = hovered ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.18f, 0.18f, 0.18f);
        EditorGUI.DrawRect(rect, backgroundColor);
        Handles.color = new Color(0.32f, 0.32f, 0.32f);
        Handles.DrawAAPolyLine(1.5f,
            new Vector3(rect.xMin, rect.yMin),
            new Vector3(rect.xMax, rect.yMin),
            new Vector3(rect.xMax, rect.yMax),
            new Vector3(rect.xMin, rect.yMax),
            new Vector3(rect.xMin, rect.yMin));

        float imageHeight = Mathf.Min(rect.width, 150f);
        Rect imageRect = new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, imageHeight);
        EditorGUI.DrawRect(imageRect, new Color(0.1f, 0.1f, 0.1f));
        GUI.Label(imageRect, "+", new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 52,
            normal = { textColor = new Color(0.82f, 0.92f, 1f) }
        });

        Rect textRect = new Rect(rect.x + 10f, imageRect.yMax + 16f, rect.width - 20f, 36f);
        GUI.Label(textRect, "Create custom base", new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.UpperCenter,
            wordWrap = true
        });

        if (Event.current.type == EventType.Repaint && hovered)
        {
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        }

        if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            OpenCreateCustomBaseForm();
        }
    }

    private void DrawGalleryCard(AvatarDiscoveredAsset asset, float width, float height)
    {
        Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
        bool hovered = rect.Contains(Event.current.mousePosition);
        Color backgroundColor = hovered ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.18f, 0.18f, 0.18f);
        EditorGUI.DrawRect(rect, backgroundColor);
        Handles.color = new Color(0.32f, 0.32f, 0.32f);
        Handles.DrawAAPolyLine(1.5f,
            new Vector3(rect.xMin, rect.yMin),
            new Vector3(rect.xMax, rect.yMin),
            new Vector3(rect.xMax, rect.yMax),
            new Vector3(rect.xMin, rect.yMax),
            new Vector3(rect.xMin, rect.yMin));

        float imageHeight = Mathf.Min(rect.width, 150f);
        Rect imageRect = new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, imageHeight);
        var thumbnail = AvatarAssetDiscoveryService.GetThumbnail(asset);
        if (thumbnail != null)
        {
            GUI.DrawTexture(imageRect, thumbnail, ScaleMode.ScaleAndCrop, true);
        }
        else
        {
            EditorGUI.DrawRect(imageRect, new Color(0.1f, 0.1f, 0.1f));
            GUI.Label(imageRect, "Thumbnail", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
        }

        float textTop = imageRect.yMax + 8f;
        Rect titleRect = new Rect(rect.x + 10f, textTop, rect.width - 20f, 18f);
        GUI.Label(titleRect, asset.name ?? "Unnamed asset", new GUIStyle(EditorStyles.boldLabel) { clipping = TextClipping.Clip });

        string versionLabel = string.IsNullOrWhiteSpace(asset.latestVersion) ? "No version" : $"Latest v{asset.latestVersion}";
        Rect versionRect = new Rect(rect.x + 10f, textTop + 20f, rect.width - 20f, 16f);
        GUI.Label(versionRect, versionLabel, EditorStyles.miniLabel);

        DrawOwnerFooter(asset, rect);

        if (Event.current.type == EventType.Repaint && hovered)
        {
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        }

        if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            SelectAsset(asset, persist: true, refreshVersions: true);
        }
    }

    private void DrawOwnerFooter(AvatarDiscoveredAsset asset, Rect cardRect)
    {
        float footerY = cardRect.yMax - 34f;
        Rect avatarRect = new Rect(cardRect.x + 10f, footerY, 24f, 24f);
        Rect nameRect = new Rect(avatarRect.xMax + 6f, footerY + 3f, cardRect.width - 50f, 18f);

        Texture2D ownerAvatar = null;
        string ownerName = asset.ownerUsername;
        if (asset.ownerId.HasValue && asset.ownerId.Value > 0)
        {
            ownerAvatar = UserService.GetUserAvatar(asset.ownerId.Value);
            var info = UserService.GetUserInfo(asset.ownerId.Value);
            if (info != null && !string.IsNullOrWhiteSpace(info.username))
            {
                ownerName = info.username;
            }
        }

        if (ownerAvatar != null)
        {
            EditorUIUtils.DrawCircularAvatar(avatarRect, ownerAvatar, new Color(0.24f, 0.24f, 0.24f), new Color(0.45f, 0.45f, 0.45f));
        }
        else
        {
            EditorGUI.DrawRect(avatarRect, new Color(0.24f, 0.24f, 0.24f));
            GUI.Label(avatarRect, "?", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
        }

        GUI.Label(nameRect, string.IsNullOrWhiteSpace(ownerName) ? "Unknown author" : ownerName, EditorStyles.miniLabel);
    }

    private void DrawNonMatchingSection()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (!showNonMatchingAssets)
            {
                if (GUILayout.Button("Show Other Non Matching Assets", GUILayout.Height(24f)))
                {
                    showNonMatchingAssets = true;
                    if (!hasFetchedAllAssets)
                    {
                        StartDiscovery(filterOnlyCompatible: false);
                    }
                }
            }
            else if (GUILayout.Button("Hide Non Matching Assets", GUILayout.Height(24f)))
            {
                showNonMatchingAssets = false;
            }

            GUILayout.FlexibleSpace();
        }

        if (!showNonMatchingAssets)
        {
            return;
        }

        if (!hasFetchedAllAssets && isLoading)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox("Loading non matching assets...", MessageType.Info);
            return;
        }

        var nonMatchingAssets = GetNonMatchingAssets();
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Non Matching Assets", EditorStyles.boldLabel);

        if (nonMatchingAssets.Count == 0)
        {
            EditorGUILayout.HelpBox("No non matching avatar assets were found for this account.", MessageType.Info);
            return;
        }

        DrawGallery(nonMatchingAssets);
    }

    private List<AvatarDiscoveredAsset> GetMatchingAssets()
    {
        return compatibleAssets
            .Where(a => a != null)
            .OrderBy(a => a.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<AvatarDiscoveredAsset> GetNonMatchingAssets()
    {
        if (allAssets == null || allAssets.Count == 0)
        {
            return new List<AvatarDiscoveredAsset>();
        }

        return allAssets
            .Where(a => a != null && !a.isCompatible)
            .OrderBy(a => a.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OpenCreateCustomBaseForm()
    {
        isCreatingCustomBase = true;
        createError = null;
        selectedAvatarBaseIndex = 0;
        LoadAvatarBasesIfNeeded();
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
            otherAvatarBaseFbx = EditorGUILayout.ObjectField("Base Avatar FBX", otherAvatarBaseFbx, typeof(GameObject), false) as GameObject;
            string path = otherAvatarBaseFbx != null ? AssetDatabase.GetAssetPath(otherAvatarBaseFbx) : null;
            if (otherAvatarBaseFbx != null && !IsValidFbxPath(path))
            {
                EditorGUILayout.HelpBox("The base avatar object must be an FBX asset.", MessageType.Warning);
            }
        }
    }

    private bool IsCreateFormValid()
    {
        if (string.IsNullOrWhiteSpace(createName) || !Regex.IsMatch(createName, @"^[a-zA-Z0-9 ]+$"))
        {
            return false;
        }

        if (!IsOtherAvatarBaseSelected())
        {
            return selectedAvatarBaseIndex >= 0 && selectedAvatarBaseIndex < avatarBaseOptions.Count;
        }

        return otherAvatarBaseFbx != null && IsValidFbxPath(AssetDatabase.GetAssetPath(otherAvatarBaseFbx));
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
            yield return request.SendWebRequest();

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

        if (IsOtherAvatarBaseSelected())
        {
            metadata["otherAvatarBasePath"] = AssetDatabase.GetAssetPath(otherAvatarBaseFbx);
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
            yield return request.SendWebRequest();

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
                        thumbnailUrl = response.asset.thumbnail,
                        bannerUrl = response.asset.mcbBanner,
                        avatarBase = response.asset.selectedAvatarBase != null
                            ? new AvatarAssetBaseInfo { id = response.asset.selectedAvatarBase.id, name = response.asset.selectedAvatarBase.name }
                            : null,
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
        editor.Repaint();
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
        createThumbnail = null;
        createBanner = null;
        createName = "";
        createDescription = "";
        createJinxxyLink = "";
        createGumroadLink = "";
        selectedAvatarBaseIndex = 0;
        otherAvatarBaseFbx = null;
        createError = null;
    }

    private void StartDiscovery(bool filterOnlyCompatible)
    {
        if (isLoading || !editor.isAuthenticated)
        {
            return;
        }

        var paths = editor.GetDetectedAvatarFbxPaths();
        if (paths.Count == 0)
        {
            compatibleAssets.Clear();
            allAssets.Clear();
            hasFetchedCompatibleAssets = filterOnlyCompatible;
            hasFetchedAllAssets = !filterOnlyCompatible;
            lastError = null;
            return;
        }

        isLoading = true;
        lastError = null;

        EditorCoroutineUtility.StartCoroutineOwnerless(
            AvatarAssetDiscoveryService.DiscoverAssetsCoroutine(
                editor.authToken,
                paths,
                filterOnlyCompatible,
                (response, error) =>
                {
                    isLoading = false;
                    lastError = error;

                    if (response != null)
                    {
                        if (filterOnlyCompatible)
                        {
                            compatibleAssets = response.assets ?? new List<AvatarDiscoveredAsset>();
                            hasFetchedCompatibleAssets = true;
                        }
                        else
                        {
                            allAssets = response.assets ?? new List<AvatarDiscoveredAsset>();
                            hasFetchedAllAssets = true;
                        }

                        TryRestoreSelectedAsset();
                    }

                    editor.Repaint();
                }));
    }

    private void SelectAsset(AvatarDiscoveredAsset asset, bool persist, bool refreshVersions)
    {
        if (asset == null)
        {
            return;
        }

        bool changed = SelectedAsset == null || SelectedAsset.id != asset.id;
        SelectedAsset = asset;

        if (persist)
        {
            EditorPrefs.SetInt(SelectedAssetIdPrefKey, asset.id);
        }

        if (changed || refreshVersions)
        {
            editor.warningsModule?.Clear();
            editor.serverVersions.Clear();
            editor.recommendedVersion = null;
            editor.selectedVersionForAction = null;
            editor.fetchAttempted = false;
            if (refreshVersions)
            {
                editor.RefreshAccountAndVersions();
            }
        }

        AvatarAssetDiscoveryService.PreloadBanner(asset);
    }

    private void TryRestoreSelectedAsset()
    {
        if (SelectedAsset != null || !EditorPrefs.HasKey(SelectedAssetIdPrefKey))
        {
            return;
        }

        int assetId = EditorPrefs.GetInt(SelectedAssetIdPrefKey, 0);
        if (assetId <= 0)
        {
            return;
        }

        var asset = compatibleAssets
            .Concat(allAssets)
            .FirstOrDefault(a => a != null && a.id == assetId);

        if (asset == null)
        {
            return;
        }

        SelectAsset(asset, persist: true, refreshVersions: true);
    }

    private void ResetState(bool clearSelection)
    {
        compatibleAssets.Clear();
        allAssets.Clear();
        hasFetchedCompatibleAssets = false;
        hasFetchedAllAssets = false;
        isLoading = false;
        lastError = null;
        showNonMatchingAssets = false;
        isCreatingCustomBase = false;
        isSubmittingCustomBase = false;
        createError = null;
        if (clearSelection)
        {
            SelectedAsset = null;
        }
    }

    private static string ComputeStableHash(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "none";
        }

        using (var sha1 = SHA1.Create())
        {
            byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
#endif
