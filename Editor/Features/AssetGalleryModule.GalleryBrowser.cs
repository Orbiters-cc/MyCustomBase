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
    private void BuildGalleryUIToolkit()
    {
        if (galleryRoot == null)
        {
            return;
        }

        galleryRoot.Clear();
        galleryRoot.RemoveFromClassList("mcb-surface");
        galleryRoot.RemoveFromClassList("mcb-gallery");
        galleryRoot.RemoveFromClassList("mcb-selected-title-band");
        thumbnailImages.Clear();
        ownerAvatarImages.Clear();
        likeCountLabels.Clear();
        commentCountLabels.Clear();

        if (!editor.isAuthenticated || !editor.HasServerAccess || MCBPackageVersionService.RequiresMajorUpdate)
        {
            galleryRoot.style.display = DisplayStyle.None;
            return;
        }

        galleryRoot.style.display = DisplayStyle.Flex;

        editor.serializedObject.Update();
        RefreshIfNeeded();

        if (isEditingSelectedAssetMedia && SelectedAsset != null)
        {
            galleryRoot.AddToClassList("mcb-surface");
            galleryRoot.AddToClassList("mcb-gallery");
            photoshootPanel.BuildSelectedAssetMediaEditUIToolkit(galleryRoot);
            return;
        }

        if (SelectedAsset != null)
        {
            galleryRoot.AddToClassList("mcb-selected-title-band");
            selectedAssetPanel.BuildBannerUIToolkit(galleryRoot, SelectedAsset);
            selectedAssetPanel.BuildHeaderUIToolkit(galleryRoot, SelectedAsset);
            assetInteractionPanel.EnsureInteractionLoad(SelectedAsset.id);
            return;
        }

        galleryRoot.AddToClassList("mcb-surface");
        galleryRoot.AddToClassList("mcb-gallery");

        if (isCreatingCustomBase)
        {
            customBaseCreateForm.BuildUIToolkit(galleryRoot);
            return;
        }

        var matchingAssets = GetMatchingAssets();
        BuildGalleryToolbarUIToolkit(galleryRoot, matchingAssets);

        if (isLoading)
        {
            galleryRoot.Add(CreateMessageLabel("Discovering avatar assets for the current avatar...", new Color(0.70f, 0.78f, 0.86f)));
            return;
        }

        if (!string.IsNullOrWhiteSpace(lastError))
        {
            galleryRoot.Add(CreateMessageLabel(lastError, new Color(1f, 0.64f, 0.28f)));
            var retryRow = CreateRow();
            retryRow.style.marginTop = 10f;
            retryRow.Add(CreateToolbarButton("Retry", () => StartDiscovery(filterOnlyCompatible: !showNonMatchingAssets)));
            if (!showNonMatchingAssets)
            {
                retryRow.Add(CreateToolbarButton("Show Other Non Matching Assets", () =>
                {
                    showNonMatchingAssets = true;
                    StartDiscovery(filterOnlyCompatible: false);
                    editor.RefreshUiToolkitSections();
                }));
            }
            galleryRoot.Add(retryRow);
            return;
        }

        if (!string.IsNullOrEmpty(MCBConnectivityMonitor.FailureReport) ||
            (MCBConnectivityMonitor.HasCompleted && !MCBConnectivityMonitor.CanReachServer))
        {
            return;
        }

        var filteredMatchingAssets = ApplyAvatarBaseFilter(matchingAssets);
        if (filteredMatchingAssets.Count == 0)
        {
            galleryRoot.Add(CreateMessageLabel("No matching avatar assets were found for this avatar.", new Color(0.70f, 0.78f, 0.86f)));
            BuildCardsGridUIToolkit(galleryRoot, filteredMatchingAssets, includeCreateCard: true);
        }
        else
        {
            BuildCardsGridUIToolkit(galleryRoot, filteredMatchingAssets, includeCreateCard: true);
        }

        BuildNonMatchingUIToolkit(galleryRoot);
    }

    private void BuildGalleryToolbarUIToolkit(VisualElement root, List<AvatarDiscoveredAsset> matchingAssets)
    {
        var row = CreateRow();
        row.AddToClassList("mcb-gallery-toolbar");

        var title = CreateLabel("Available Custom Bases", 12, FontStyle.Bold, new Color(0.78f, 0.78f, 0.78f));
        title.AddToClassList("mcb-label--section");
        title.AddToClassList("mcb-gallery-toolbar__title");
        row.Add(title);

        var baseOptions = matchingAssets
            .Select(GetAvatarBaseFilterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (baseOptions.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(selectedAvatarBaseFilter) ||
                !baseOptions.Contains(selectedAvatarBaseFilter, StringComparer.OrdinalIgnoreCase))
            {
                selectedAvatarBaseFilter = baseOptions[0];
            }

            int selectedIndex = Mathf.Max(0, baseOptions.FindIndex(name => string.Equals(name, selectedAvatarBaseFilter, StringComparison.OrdinalIgnoreCase)));
            var dropdown = new DropdownField(baseOptions, selectedIndex);
            dropdown.AddToClassList("mcb-dropdown");
            dropdown.AddToClassList("mcb-gallery-toolbar__dropdown");
            dropdown.RegisterValueChangedCallback(evt =>
            {
                selectedAvatarBaseFilter = evt.newValue;
                editor.RefreshUiToolkitSections();
            });
            row.Add(dropdown);
        }

        root.Add(row);
    }

    private void BuildCardsGridUIToolkit(VisualElement root, List<AvatarDiscoveredAsset> assets, bool includeCreateCard)
    {
        var grid = new VisualElement();
        grid.AddToClassList("mcb-gallery-grid");
        root.Add(grid);

        foreach (var asset in assets)
        {
            if (asset == null)
            {
                continue;
            }

            grid.Add(CreateAssetCardUIToolkit(asset));
        }

        if (includeCreateCard)
        {
            grid.Add(CreateCreateCustomBaseCardUIToolkit());
        }
    }

    private VisualElement CreateAssetCardUIToolkit(AvatarDiscoveredAsset asset)
    {
        EnsureInteractionLoad(asset.id);

        var card = CreateCardShell();
        card.RegisterCallback<ClickEvent>(evt =>
        {
            SelectAsset(asset, persist: true, refreshVersions: true);
            editor.RefreshUiToolkitSections();
            evt.StopPropagation();
        });

        var imageFrame = new VisualElement();
        imageFrame.AddToClassList("mcb-card__media");

        var thumbnail = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        thumbnail.AddToClassList("mcb-card__thumbnail");
        thumbnail.image = AvatarAssetDiscoveryService.GetThumbnail(asset);
        imageFrame.Add(thumbnail);
        thumbnailImages[asset.id] = thumbnail;
        card.Add(imageFrame);

        var body = new VisualElement();
        body.AddToClassList("mcb-card__body");
        card.Add(body);

        var titleMetrics = CreateRow();
        titleMetrics.AddToClassList("mcb-card__title-row");
        var title = CreateLabel(asset.name ?? "Unnamed asset", 13, FontStyle.Bold, Color.white);
        title.AddToClassList("mcb-card__title");
        titleMetrics.Add(title);

        var metrics = new VisualElement();
        metrics.AddToClassList("mcb-card__metrics");
        var state = GetInteractionState(asset.id, false);
        metrics.Add(CreateMetricRow(asset.id, MCBInteractionIconKind.Like, state != null ? state.likeCount : 0, true));
        metrics.Add(CreateMetricRow(asset.id, MCBInteractionIconKind.Comment, state != null ? state.commentCount : 0, false));
        titleMetrics.Add(metrics);
        body.Add(titleMetrics);

        var spacer = new VisualElement();
        spacer.AddToClassList("mcb-card__spacer");
        body.Add(spacer);

        var footer = CreateOwnerFooterUIToolkit(asset);
        body.Add(footer);
        return card;
    }

    private VisualElement CreateCreateCustomBaseCardUIToolkit()
    {
        var card = CreateCardShell();
        card.RegisterCallback<ClickEvent>(evt =>
        {
            OpenCreateCustomBaseForm();
            editor.RefreshUiToolkitSections();
            evt.StopPropagation();
        });

        var imageFrame = new VisualElement();
        imageFrame.AddToClassList("mcb-card__media");
        imageFrame.AddToClassList("mcb-card__media--create");
        var plus = CreateLabel("+", 52, FontStyle.Bold, Color.white);
        plus.AddToClassList("mcb-create-card__plus");
        imageFrame.Add(plus);
        card.Add(imageFrame);

        var body = new VisualElement();
        body.AddToClassList("mcb-create-card__body");
        var label = CreateLabel("Create Custom base", 13, FontStyle.Bold, Color.white);
        label.AddToClassList("mcb-create-card__label");
        body.Add(label);
        card.Add(body);
        return card;
    }

    private VisualElement CreateOwnerFooterUIToolkit(AvatarDiscoveredAsset asset)
    {
        var footer = CreateRow();
        footer.AddToClassList("mcb-card__owner");

        var avatarFrame = new VisualElement();
        avatarFrame.AddToClassList("mcb-card__avatar");

        var avatarImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        avatarImage.AddToClassList("mcb-card__avatar-image");
        if (asset.ownerId.HasValue)
        {
            avatarImage.image = UserService.GetUserAvatar(asset.ownerId.Value);
            ownerAvatarImages[asset.id] = avatarImage;
        }
        avatarFrame.Add(avatarImage);
        footer.Add(avatarFrame);

        string ownerName = asset.ownerUsername;
        if (asset.ownerId.HasValue && asset.ownerId.Value > 0)
        {
            var info = UserService.GetUserInfo(asset.ownerId.Value);
            if (info != null && !string.IsNullOrWhiteSpace(info.username))
            {
                ownerName = info.username;
            }
        }

        var name = CreateLabel(string.IsNullOrWhiteSpace(ownerName) ? "Unknown author" : ownerName, 12, FontStyle.Bold, Color.white);
        name.AddToClassList("mcb-card__owner-name");
        footer.Add(name);
        return footer;
    }

    private VisualElement CreateMetricRow(int assetId, MCBInteractionIconKind iconKind, int count, bool isLike)
    {
        var row = CreateRow();
        row.AddToClassList("mcb-card__metric-row");

        var label = CreateLabel(count.ToString(), 11, FontStyle.Normal, Color.white);
        label.AddToClassList("mcb-card__metric-label");
        row.Add(label);
        if (isLike)
        {
            likeCountLabels[assetId] = label;
        }
        else
        {
            commentCountLabels[assetId] = label;
        }

        var image = new MCBInteractionIconElement(iconKind);
        image.AddToClassList("mcb-card__metric-icon");
        row.Add(image);

        return row;
    }

    private void BuildNonMatchingUIToolkit(VisualElement root)
    {
        var row = CreateRow();
        row.AddToClassList("mcb-nonmatching-row");
        if (!showNonMatchingAssets)
        {
            row.Add(CreateToolbarButton("Show Other Non Matching Assets", () =>
            {
                showNonMatchingAssets = true;
                if (!hasFetchedAllAssets)
                {
                    StartDiscovery(filterOnlyCompatible: false);
                }
                editor.RefreshUiToolkitSections();
            }));
            root.Add(row);
            return;
        }

        row.Add(CreateToolbarButton("Hide Non Matching Assets", () =>
        {
            showNonMatchingAssets = false;
            editor.RefreshUiToolkitSections();
        }));
        root.Add(row);

        if (!hasFetchedAllAssets && isLoading)
        {
            root.Add(CreateMessageLabel("Loading non matching assets...", new Color(0.70f, 0.78f, 0.86f)));
            return;
        }

        var nonMatchingAssets = GetNonMatchingAssets();
        var title = CreateLabel("Non Matching Assets", 13, FontStyle.Bold, Color.white);
        title.style.marginTop = 12f;
        root.Add(title);
        if (nonMatchingAssets.Count == 0)
        {
            root.Add(CreateMessageLabel("No non matching avatar assets were found for this account.", new Color(0.62f, 0.62f, 0.62f)));
            return;
        }

        BuildCardsGridUIToolkit(root, nonMatchingAssets, includeCreateCard: false);
    }

    private AvatarDiscoveredAsset FindKnownAsset(int assetId)
    {
        if (SelectedAsset != null && SelectedAsset.id == assetId)
        {
            return SelectedAsset;
        }

        var asset = compatibleAssets.FirstOrDefault(item => item != null && item.id == assetId);
        if (asset != null)
        {
            return asset;
        }

        return allAssets.FirstOrDefault(item => item != null && item.id == assetId);
    }

    private List<AvatarDiscoveredAsset> ApplyAvatarBaseFilter(List<AvatarDiscoveredAsset> assets)
    {
        if (assets == null)
        {
            return new List<AvatarDiscoveredAsset>();
        }

        if (string.IsNullOrWhiteSpace(selectedAvatarBaseFilter))
        {
            return assets;
        }

        return assets
            .Where(asset => string.Equals(GetAvatarBaseFilterName(asset), selectedAvatarBaseFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string GetAvatarBaseFilterName(AvatarDiscoveredAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset?.avatarBase?.name))
        {
            return asset.avatarBase.name;
        }

        return "Detected Base";
    }

    public void RefreshIfNeeded(bool force = false)
    {
        if (!editor.isAuthenticated)
        {
            return;
        }

        if (MCBEditor.ShouldDeferBackgroundNetworkRefresh())
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
            if (!string.IsNullOrEmpty(MCBConnectivityMonitor.FailureReport) ||
                (MCBConnectivityMonitor.HasCompleted && !MCBConnectivityMonitor.CanReachServer))
            {
                return;
            }

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

    private void StartDiscovery(bool filterOnlyCompatible)
    {
        if (isLoading || !editor.isAuthenticated || MCBEditor.ShouldDeferBackgroundNetworkRefresh())
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

                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }
}
#endif
