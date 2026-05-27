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
    private bool CanEditSelectedAssetMedia(AvatarDiscoveredAsset selectedAsset)
    {
        if (selectedAsset == null ||
            editor == null ||
            !editor.isAuthenticated ||
            !editor.HasServerAccess ||
            MCBPackageVersionService.RequiresMajorUpdate ||
            !selectedAsset.ownerId.HasValue)
        {
            return false;
        }

        int currentUserId = GetCurrentUserId();
        return currentUserId > 0 && selectedAsset.ownerId.Value == currentUserId;
    }

    private void BuildSelectedAssetBannerUIToolkit(VisualElement root, AvatarDiscoveredAsset selectedAsset)
    {
        if (selectedAsset == null || selectedAsset.id <= 0)
        {
            return;
        }

        bool canEditMedia = CanEditSelectedAssetMedia(selectedAsset);
        var state = EnsureInteractionLoad(selectedAsset.id);
        string bannerUrl = ResolveSelectedAssetBannerUrl(selectedAsset);

        var frame = new VisualElement();
        frame.AddToClassList("mcb-selected-banner");
        frame.RegisterCallback<GeometryChangedEvent>(_ => UpdateSelectedAssetBannerAspect());
        root.Add(frame);

        selectedAssetBannerFrame = frame;
        selectedAssetBannerAssetId = selectedAsset.id;
        selectedAssetBannerImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        selectedAssetBannerImage.AddToClassList("mcb-selected-banner__image");
        frame.Add(selectedAssetBannerImage);

        selectedAssetBannerMessage = CreateLabel("Loading banner...", 12, FontStyle.Bold, new Color(0.82f, 0.82f, 0.82f));
        selectedAssetBannerMessage.AddToClassList("mcb-selected-banner__message");
        frame.Add(selectedAssetBannerMessage);

        frame.Add(CreateSelectedAssetBreadcrumb(selectedAsset));
        frame.Add(CreateSelectedAssetLikeButton(state));

        if (canEditMedia)
        {
            var editButton = CreateTextButton("Edit", () => BeginEditSelectedAssetMedia(selectedAsset));
            editButton.AddToClassList("mcb-selected-banner__edit-button");
            editButton.SetEnabled(!isSavingSelectedAssetMedia && !isGeneratingPhotoshootImage);
            frame.Add(editButton);
        }

        frame.Add(CreateSelectedAssetAuthorBadge(selectedAsset));

        if (state != null && !string.IsNullOrWhiteSpace(state.error))
        {
            var interactionError = CreateLabel(state.error, 11, FontStyle.Normal, new Color(1f, 0.55f, 0.35f));
            interactionError.AddToClassList("mcb-selected-banner__error");
            frame.Add(interactionError);
        }

        if (string.IsNullOrWhiteSpace(bannerUrl))
        {
            selectedAssetBannerImage.style.display = DisplayStyle.None;
            selectedAssetBannerMessage.text = "No banner yet";
            selectedAssetBannerMessage.style.display = DisplayStyle.Flex;
            return;
        }

        if (selectedAssetBannerTextures.TryGetValue(selectedAsset.id, out var cachedTexture) && cachedTexture != null)
        {
            ApplySelectedAssetBannerTexture(cachedTexture);
            return;
        }

        if (selectedAssetBannerErrors.TryGetValue(selectedAsset.id, out var error) && !string.IsNullOrWhiteSpace(error))
        {
            selectedAssetBannerImage.style.display = DisplayStyle.None;
            selectedAssetBannerMessage.text = error;
            selectedAssetBannerMessage.style.display = DisplayStyle.Flex;
            return;
        }

        selectedAssetBannerImage.style.display = DisplayStyle.None;
        selectedAssetBannerMessage.style.display = DisplayStyle.Flex;
        StartSelectedAssetBannerLoad(selectedAsset.id, bannerUrl);
    }

    private void BuildSelectedAssetActionsUIToolkit()
    {
        if (selectedAssetActionsRoot == null)
        {
            return;
        }

        selectedAssetActionsRoot.Clear();
        selectedAssetActionsRoot.style.display = DisplayStyle.None;
    }

    private void StartSelectedAssetBannerLoad(int assetId, string bannerUrl)
    {
        if (assetId <= 0 ||
            string.IsNullOrWhiteSpace(bannerUrl) ||
            selectedAssetBannerLoads.Contains(assetId) ||
            selectedAssetBannerTextures.ContainsKey(assetId))
        {
            return;
        }

        selectedAssetBannerLoads.Add(assetId);
        selectedAssetBannerErrors.Remove(assetId);
        EditorCoroutineUtility.StartCoroutineOwnerless(LoadSelectedAssetBannerCoroutine(assetId, bannerUrl));
    }

    private IEnumerator LoadSelectedAssetBannerCoroutine(int assetId, string bannerUrl)
    {
        using (var request = UnityWebRequestTexture.GetTexture(bannerUrl))
        {
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetImageDownload);
            var policy = MCBManagedRequest.ResourcePolicyForUrl(
                bannerUrl,
                "Load selected asset banner",
                $"asset-banner:{assetId}",
                "Asset banner unavailable");
            yield return MCBManagedRequest.SendUnityWebRequest(request, bannerUrl, policy);

            selectedAssetBannerLoads.Remove(assetId);

            if (request.result != UnityWebRequest.Result.Success)
            {
                selectedAssetBannerErrors[assetId] = $"Banner unavailable ({request.responseCode})";
                if (SelectedAsset != null && SelectedAsset.id == assetId)
                {
                    UpdateSelectedAssetBannerMessage(selectedAssetBannerErrors[assetId]);
                }
                yield break;
            }

            Texture2D texture = null;
            try
            {
                texture = DownloadHandlerTexture.GetContent(request);
            }
            catch (Exception ex)
            {
                selectedAssetBannerErrors[assetId] = $"Banner decode failed: {ex.Message}";
                MCBConnectivityMonitor.ReportManagedException(bannerUrl, ex, MCBRequestPolicy.ExternalResource(
                    "Decode selected asset banner",
                    $"asset-banner:{assetId}",
                    "Asset banner unavailable"));
            }

            if (texture == null)
            {
                if (!selectedAssetBannerErrors.ContainsKey(assetId))
                {
                    selectedAssetBannerErrors[assetId] = "Banner unavailable";
                }

                if (SelectedAsset != null && SelectedAsset.id == assetId)
                {
                    UpdateSelectedAssetBannerMessage(selectedAssetBannerErrors[assetId]);
                }
                yield break;
            }

            texture.name = $"mcb-asset-banner-{assetId}";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            selectedAssetBannerTextures[assetId] = texture;
            selectedAssetBannerErrors.Remove(assetId);

            if (SelectedAsset != null && SelectedAsset.id == assetId)
            {
                ApplySelectedAssetBannerTexture(texture);
                editor.Repaint();
            }
        }
    }

    private void ApplySelectedAssetBannerTexture(Texture2D texture)
    {
        if (selectedAssetBannerImage == null || texture == null)
        {
            return;
        }

        selectedAssetBannerImage.image = texture;
        selectedAssetBannerImage.style.display = DisplayStyle.Flex;
        if (selectedAssetBannerMessage != null)
        {
            selectedAssetBannerMessage.style.display = DisplayStyle.None;
        }
        UpdateSelectedAssetBannerAspect();
    }

    private void UpdateSelectedAssetBannerMessage(string message)
    {
        if (selectedAssetBannerImage != null)
        {
            selectedAssetBannerImage.style.display = DisplayStyle.None;
        }

        if (selectedAssetBannerMessage != null)
        {
            selectedAssetBannerMessage.text = message ?? "Banner unavailable";
            selectedAssetBannerMessage.style.display = DisplayStyle.Flex;
        }

        editor.Repaint();
    }

    private void UpdateSelectedAssetBannerAspect()
    {
        if (selectedAssetBannerFrame == null || selectedAssetBannerImage == null)
        {
            return;
        }

        var texture = selectedAssetBannerImage.image;
        if (texture == null || texture.width <= 0 || texture.height <= 0)
        {
            selectedAssetBannerFrame.style.height = 220f;
            return;
        }

        float width = selectedAssetBannerFrame.resolvedStyle.width;
        if (width <= 0f || float.IsNaN(width))
        {
            return;
        }

        float height = Mathf.Max(72f, width * texture.height / texture.width);
        selectedAssetBannerFrame.style.height = height;
    }

    private VisualElement CreateSelectedAssetBreadcrumb(AvatarDiscoveredAsset selectedAsset)
    {
        var row = CreateRow();
        row.AddToClassList("mcb-selected-banner__breadcrumb");

        var galleryStep = new Button { text = "Gallery" };
        RegisterImmediateClick(galleryStep, ReturnToGallery);
        galleryStep.AddToClassList("mcb-selected-banner__breadcrumb-step");
        row.Add(galleryStep);

        var separator = CreateLabel(">", 12, FontStyle.Normal, Color.white);
        separator.AddToClassList("mcb-selected-banner__breadcrumb-separator");
        row.Add(separator);

        var title = CreateLabel(selectedAsset?.name ?? "Unnamed asset", 12, FontStyle.Bold, Color.white);
        title.AddToClassList("mcb-selected-banner__breadcrumb-current");
        row.Add(title);

        return row;
    }

    private Button CreateSelectedAssetLikeButton(AssetInteractionState state)
    {
        var button = new Button
        {
            tooltip = state != null && state.likedByCurrentUser ? "Unlike asset" : "Like asset"
        };
        RegisterImmediateClick(button, ToggleSelectedAssetLike);
        button.AddToClassList("mcb-selected-banner__like-button");
        button.EnableInClassList("mcb-selected-banner__like-button--liked", state != null && state.likedByCurrentUser);
        button.SetEnabled(state != null && !state.isLoading);

        var icon = new MCBInteractionIconElement(MCBInteractionIconKind.Like);
        icon.AddToClassList("mcb-selected-banner__like-icon");
        button.Add(icon);

        var count = CreateLabel(state != null ? state.likeCount.ToString() : "0", 12, FontStyle.Bold, Color.white);
        count.AddToClassList("mcb-selected-banner__like-count");
        button.Add(count);
        selectedAssetLikeButton = button;
        selectedAssetLikeCountLabel = count;
        return button;
    }

    private VisualElement CreateSelectedAssetAuthorBadge(AvatarDiscoveredAsset selectedAsset)
    {
        var row = CreateRow();
        row.AddToClassList("mcb-selected-banner__author");

        var avatarFrame = new VisualElement();
        avatarFrame.AddToClassList("mcb-selected-banner__author-avatar");
        var avatarImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        avatarImage.AddToClassList("mcb-selected-banner__author-avatar-image");

        if (selectedAsset != null && selectedAsset.ownerId.HasValue && selectedAsset.ownerId.Value > 0)
        {
            UserService.UpdateUserInfo(selectedAsset.ownerId.Value, selectedAsset.ownerUsername, selectedAsset.ownerAvatarUrl);
            avatarImage.image = UserService.GetUserAvatar(selectedAsset.ownerId.Value);
            ownerAvatarImages[selectedAsset.id] = avatarImage;
        }
        avatarFrame.Add(avatarImage);
        row.Add(avatarFrame);

        var by = CreateLabel("by", 11, FontStyle.Normal, Color.white);
        by.AddToClassList("mcb-selected-banner__author-by");
        row.Add(by);

        string ownerName = ResolveSelectedAssetOwnerName(selectedAsset);
        var name = CreateLabel(string.IsNullOrWhiteSpace(ownerName) ? "Unknown author" : ownerName, 11, FontStyle.Bold, Color.white);
        name.AddToClassList("mcb-selected-banner__author-name");
        row.Add(name);
        return row;
    }

    private static string ResolveSelectedAssetOwnerName(AvatarDiscoveredAsset selectedAsset)
    {
        if (selectedAsset == null)
        {
            return null;
        }

        string ownerName = selectedAsset.ownerUsername;
        if (selectedAsset.ownerId.HasValue && selectedAsset.ownerId.Value > 0)
        {
            var info = UserService.GetUserInfo(selectedAsset.ownerId.Value);
            if (info != null && !string.IsNullOrWhiteSpace(info.username))
            {
                ownerName = info.username;
            }
        }

        return ownerName;
    }

    private void ReturnToGallery()
    {
        ResetSelectedAssetMediaEditState(destroyPreviewTexture: true);
        SelectedAsset = null;
        commentDraft = "";
        editingCommentId = 0;
        editingCommentDraft = "";
        selectedAssetBannerFrame = null;
        selectedAssetBannerImage = null;
        selectedAssetBannerMessage = null;
        selectedAssetBannerAssetId = 0;
        selectedAssetLikeButton = null;
        selectedAssetLikeCountLabel = null;
        EditorPrefs.DeleteKey(SelectedAssetIdPrefKey);
        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private static string ResolveSelectedAssetBannerUrl(AvatarDiscoveredAsset selectedAsset)
    {
        if (selectedAsset == null)
        {
            return null;
        }

        string url = selectedAsset.bannerUrl;
        if (string.IsNullOrWhiteSpace(url) && selectedAsset.id > 0)
        {
            url = $"{MCBUtils.getApiUrl("assets")}/{selectedAsset.id}/mcb-banner";
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return AppendSelectedAssetBannerFormat(url);
        }

        if (!Uri.TryCreate(MCBUtils.getApiUrl(string.Empty), UriKind.Absolute, out var apiRoot))
        {
            return AppendSelectedAssetBannerFormat(url);
        }

        return AppendSelectedAssetBannerFormat(new Uri(apiRoot, url.TrimStart('/')).ToString());
    }

    private static string AppendSelectedAssetBannerFormat(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.IndexOf("format=", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return url;
        }

        return $"{url}{(url.Contains("?") ? "&" : "?")}format=png";
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

    private void ApplySelectedAssetMediaUpdate(CreatorAssetCreateResponseAsset updatedAsset)
    {
        if (updatedAsset == null || updatedAsset.id <= 0)
        {
            return;
        }

        Texture2D cachedThumbnail = null;
        Texture2D cachedBanner = null;
        if (editThumbnail != null && !string.IsNullOrWhiteSpace(updatedAsset.thumbnail))
        {
            cachedThumbnail = AvatarAssetDiscoveryService.CacheThumbnail(updatedAsset.id, updatedAsset.thumbnail, editThumbnail);
        }
        if (editBanner != null && !string.IsNullOrWhiteSpace(updatedAsset.mcbBanner))
        {
            cachedBanner = AvatarAssetDiscoveryService.CacheBanner(updatedAsset.id, updatedAsset.mcbBanner, editBanner);
        }

        ApplyAssetMediaFields(SelectedAsset, updatedAsset);
        foreach (var asset in compatibleAssets.Concat(allAssets).Where(asset => asset != null && asset.id == updatedAsset.id))
        {
            ApplyAssetMediaFields(asset, updatedAsset);
        }

        if (cachedThumbnail != null && thumbnailImages.TryGetValue(updatedAsset.id, out var thumbnailImage) && thumbnailImage != null)
        {
            thumbnailImage.image = cachedThumbnail;
        }

        if (cachedBanner != null)
        {
            selectedAssetBannerTextures[updatedAsset.id] = cachedBanner;
        }
        else
        {
            selectedAssetBannerTextures.Remove(updatedAsset.id);
        }
        selectedAssetBannerErrors.Remove(updatedAsset.id);
        selectedAssetBannerLoads.Remove(updatedAsset.id);
    }

    private static void ApplyAssetMediaFields(AvatarDiscoveredAsset asset, CreatorAssetCreateResponseAsset updatedAsset)
    {
        if (asset == null || updatedAsset == null || asset.id != updatedAsset.id)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(updatedAsset.name))
        {
            asset.name = updatedAsset.name;
        }

        if (updatedAsset.ownerId.HasValue)
        {
            asset.ownerId = updatedAsset.ownerId;
        }

        if (!string.IsNullOrWhiteSpace(updatedAsset.ownerUsername))
        {
            asset.ownerUsername = updatedAsset.ownerUsername;
        }

        if (!string.IsNullOrWhiteSpace(updatedAsset.ownerAvatarUrl))
        {
            asset.ownerAvatarUrl = updatedAsset.ownerAvatarUrl;
        }

        if (!string.IsNullOrWhiteSpace(updatedAsset.thumbnail))
        {
            asset.thumbnailUrl = updatedAsset.thumbnail;
        }

        if (!string.IsNullOrWhiteSpace(updatedAsset.mcbBanner))
        {
            asset.bannerUrl = updatedAsset.mcbBanner;
        }
    }

    private void SelectAsset(AvatarDiscoveredAsset asset, bool persist, bool refreshVersions)
    {
        if (asset == null)
        {
            return;
        }

        bool changed = SelectedAsset == null || SelectedAsset.id != asset.id;
        SelectedAsset = asset;
        editor.SyncBaseFbxFilesFromSelectedAsset();

        if (changed)
        {
            commentDraft = "";
            editingCommentId = 0;
            editingCommentDraft = "";
            selectedAssetBannerFrame = null;
            selectedAssetBannerImage = null;
            selectedAssetBannerMessage = null;
            selectedAssetBannerAssetId = 0;
            selectedAssetLikeButton = null;
            selectedAssetLikeCountLabel = null;
        }

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

        editor.RefreshUiToolkitSections();
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

        SelectAsset(asset, persist: true, refreshVersions: false);
        editor.LoadCachedVersionsForCurrentSelection();
    }
}
#endif
