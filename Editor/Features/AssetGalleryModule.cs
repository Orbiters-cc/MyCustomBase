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
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

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
    [JsonProperty] public int? ownerId;
    [JsonProperty] public string ownerUsername;
    [JsonProperty] public string ownerAvatarUrl;
    [JsonProperty] public string thumbnail;
    [JsonProperty] public string mcbBanner;
    [JsonProperty] public CreatorAvatarBaseOption selectedAvatarBase;
    [JsonProperty] public ModelFileData[] sourceFiles;
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
    private string otherAvatarBaseName = "";
    private readonly List<GameObject> targetFbxFiles = new List<GameObject>();
    private bool isLoadingAvatarBases;
    private string avatarBaseLoadError;
    private List<CreatorAvatarBaseOption> avatarBaseOptions = new List<CreatorAvatarBaseOption>();
    private VisualElement galleryRoot;
    private VisualElement selectedAssetActionsRoot;
    private VisualElement commentsRoot;
    private string selectedAvatarBaseFilter;
    private string commentDraft = "";
    private bool isPostingComment;
    private bool isTogglingLike;
    private int editingCommentId;
    private string editingCommentDraft = "";
    private bool isUpdatingComment;
    private int deletingCommentId;
    private VisualElement selectedAssetBannerFrame;
    private Image selectedAssetBannerImage;
    private Label selectedAssetBannerMessage;
    private int selectedAssetBannerAssetId;
    private readonly Dictionary<int, AssetInteractionState> interactionStates = new Dictionary<int, AssetInteractionState>();
    private readonly Dictionary<int, Image> thumbnailImages = new Dictionary<int, Image>();
    private readonly Dictionary<int, Image> ownerAvatarImages = new Dictionary<int, Image>();
    private readonly Dictionary<int, Label> likeCountLabels = new Dictionary<int, Label>();
    private readonly Dictionary<int, Label> commentCountLabels = new Dictionary<int, Label>();
    private readonly Dictionary<int, Texture2D> selectedAssetBannerTextures = new Dictionary<int, Texture2D>();
    private readonly Dictionary<int, string> selectedAssetBannerErrors = new Dictionary<int, string>();
    private readonly HashSet<int> selectedAssetBannerLoads = new HashSet<int>();

    private List<AvatarDiscoveredAsset> compatibleAssets = new List<AvatarDiscoveredAsset>();
    private List<AvatarDiscoveredAsset> allAssets = new List<AvatarDiscoveredAsset>();

    public AvatarDiscoveredAsset SelectedAsset { get; private set; }
    private string SelectedAssetIdPrefKey => $"MCB.SelectedAssetId.{ComputeStableHash(Application.dataPath)}";

    private class AssetInteractionState
    {
        public bool isLoading;
        public bool loadAttempted;
        public string error;
        public int likeCount;
        public int commentCount;
        public bool likedByCurrentUser;
        public int currentUserLikeId;
        public List<InteractionRecord> comments = new List<InteractionRecord>();
    }

    public AssetGalleryModule(MCBEditor editor)
    {
        this.editor = editor;
    }

    public void AttachUIToolkit(VisualElement galleryRoot, VisualElement selectedAssetActionsRoot, VisualElement commentsRoot)
    {
        this.galleryRoot = galleryRoot;
        this.selectedAssetActionsRoot = selectedAssetActionsRoot;
        this.commentsRoot = commentsRoot;
        RefreshUIToolkit();
    }

    public void DetachUIToolkit()
    {
        galleryRoot = null;
        selectedAssetActionsRoot = null;
        commentsRoot = null;
        thumbnailImages.Clear();
        ownerAvatarImages.Clear();
        likeCountLabels.Clear();
        commentCountLabels.Clear();
        selectedAssetBannerFrame = null;
        selectedAssetBannerImage = null;
        selectedAssetBannerMessage = null;
        selectedAssetBannerAssetId = 0;
    }

    public void RefreshUIToolkit()
    {
        BuildGalleryUIToolkit();
        BuildSelectedAssetActionsUIToolkit();
        BuildCommentsUIToolkit();
    }

    public void UpdateDynamicUiContent()
    {
        foreach (var pair in thumbnailImages.ToList())
        {
            var asset = FindKnownAsset(pair.Key);
            if (asset == null || pair.Value == null)
            {
                continue;
            }

            var texture = AvatarAssetDiscoveryService.GetThumbnail(asset);
            if (texture != null)
            {
                pair.Value.image = texture;
            }
        }

        foreach (var pair in ownerAvatarImages.ToList())
        {
            var asset = FindKnownAsset(pair.Key);
            if (asset == null || pair.Value == null || !asset.ownerId.HasValue)
            {
                continue;
            }

            var texture = UserService.GetUserAvatar(asset.ownerId.Value);
            if (texture != null)
            {
                pair.Value.image = texture;
            }
        }

        if (SelectedAsset != null &&
            selectedAssetBannerImage != null &&
            selectedAssetBannerAssetId == SelectedAsset.id &&
            selectedAssetBannerTextures.TryGetValue(SelectedAsset.id, out var bannerTexture) &&
            bannerTexture != null)
        {
            ApplySelectedAssetBannerTexture(bannerTexture);
        }

        foreach (var pair in likeCountLabels.ToList())
        {
            var state = GetInteractionState(pair.Key, false);
            if (state != null && pair.Value != null)
            {
                pair.Value.text = state.likeCount.ToString();
            }
        }

        foreach (var pair in commentCountLabels.ToList())
        {
            var state = GetInteractionState(pair.Key, false);
            if (state != null && pair.Value != null)
            {
                pair.Value.text = state.commentCount.ToString();
            }
        }
    }

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

        if (SelectedAsset != null)
        {
            galleryRoot.AddToClassList("mcb-selected-title-band");
            BuildSelectedAssetBannerUIToolkit(galleryRoot, SelectedAsset);
            BuildSelectedAssetHeaderUIToolkit(galleryRoot, SelectedAsset);
            EnsureInteractionLoad(SelectedAsset.id);
            return;
        }

        galleryRoot.AddToClassList("mcb-surface");
        galleryRoot.AddToClassList("mcb-gallery");

        if (isCreatingCustomBase)
        {
            BuildCreateCustomBaseFormUIToolkit(galleryRoot);
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

    private void BuildSelectedAssetHeaderUIToolkit(VisualElement root, AvatarDiscoveredAsset selectedAsset)
    {
        var state = EnsureInteractionLoad(selectedAsset.id);
        var row = CreateRow();
        row.AddToClassList("mcb-selected-header");

        var backButton = CreateTextButton("< Back to custom bases", () =>
        {
            SelectedAsset = null;
            commentDraft = "";
            editingCommentId = 0;
            editingCommentDraft = "";
            selectedAssetBannerFrame = null;
            selectedAssetBannerImage = null;
            selectedAssetBannerMessage = null;
            selectedAssetBannerAssetId = 0;
            EditorPrefs.DeleteKey(SelectedAssetIdPrefKey);
            editor.RefreshUiToolkitSections();
            editor.Repaint();
        });
        backButton.style.width = 165f;
        row.Add(backButton);

        var title = CreateLabel(selectedAsset.name ?? "Unnamed asset", 15, FontStyle.Bold, Color.white);
        title.AddToClassList("mcb-selected-header__title");
        row.Add(title);

        if (ShouldShowCreateNewVersionButton(selectedAsset))
        {
            var createVersionButton = CreateTextButton("Create New Version", StartCreateNewVersion);
            createVersionButton.AddToClassList("mcb-button--primary");
            createVersionButton.AddToClassList("mcb-selected-header__create-version");
            row.Add(createVersionButton);
        }

        var actions = CreateRow();
        actions.AddToClassList("mcb-selected-header__actions");

        var likeButton = CreateIconButton(MCBInteractionIconKind.Like, state != null && state.likedByCurrentUser ? "Liked" : "Like", () => ToggleSelectedAssetLike());
        likeButton.SetEnabled(!isTogglingLike && state != null && !state.isLoading);
        actions.Add(likeButton);

        var likeCount = CreateLabel(state != null ? state.likeCount.ToString() : "0", 12, FontStyle.Bold, Color.white);
        likeCount.AddToClassList("mcb-selected-header__count");
        actions.Add(likeCount);

        var commentImage = new MCBInteractionIconElement(MCBInteractionIconKind.Comment);
        commentImage.AddToClassList("mcb-selected-header__comment-icon");
        actions.Add(commentImage);

        actions.Add(CreateLabel(state != null ? state.commentCount.ToString() : "0", 12, FontStyle.Bold, Color.white));

        if (state != null && !string.IsNullOrWhiteSpace(state.error))
        {
            var error = CreateLabel(state.error, 11, FontStyle.Normal, new Color(1f, 0.55f, 0.35f));
            error.AddToClassList("mcb-selected-header__error");
            actions.Add(error);
        }

        row.Add(actions);
        root.Add(row);
    }

    private bool ShouldShowCreateNewVersionButton(AvatarDiscoveredAsset selectedAsset)
    {
        if (selectedAsset == null ||
            editor == null ||
            !editor.isAuthenticated ||
            !editor.HasServerAccess ||
            MCBPackageVersionService.RequiresMajorUpdate ||
            editor.isCreatorModeProp == null ||
            editor.isCreatorModeProp.boolValue ||
            !selectedAsset.ownerId.HasValue)
        {
            return false;
        }

        int currentUserId = GetCurrentUserId();
        return currentUserId > 0 && selectedAsset.ownerId.Value == currentUserId;
    }

    private void StartCreateNewVersion()
    {
        if (editor == null || editor.isCreatorModeProp == null)
        {
            return;
        }

        editor.serializedObject.Update();
        editor.isCreatorModeProp.boolValue = true;
        editor.serializedObject.ApplyModifiedProperties();
        if (editor.customBaseTarget != null)
        {
            EditorUtility.SetDirty(editor.customBaseTarget);
        }

        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private void BuildSelectedAssetBannerUIToolkit(VisualElement root, AvatarDiscoveredAsset selectedAsset)
    {
        if (selectedAsset == null || selectedAsset.id <= 0)
        {
            return;
        }

        string bannerUrl = ResolveSelectedAssetBannerUrl(selectedAsset);
        if (string.IsNullOrWhiteSpace(bannerUrl))
        {
            return;
        }

        var frame = new VisualElement();
        frame.AddToClassList("mcb-selected-banner");
        frame.RegisterCallback<GeometryChangedEvent>(_ => UpdateSelectedAssetBannerAspect());
        root.Add(frame);

        selectedAssetBannerFrame = frame;
        selectedAssetBannerAssetId = selectedAsset.id;
        selectedAssetBannerImage = new Image { scaleMode = ScaleMode.ScaleToFit };
        selectedAssetBannerImage.AddToClassList("mcb-selected-banner__image");
        frame.Add(selectedAssetBannerImage);

        selectedAssetBannerMessage = CreateLabel("Loading banner...", 12, FontStyle.Bold, new Color(0.82f, 0.82f, 0.82f));
        selectedAssetBannerMessage.AddToClassList("mcb-selected-banner__message");
        frame.Add(selectedAssetBannerMessage);

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
            selectedAssetBannerFrame.style.height = 96f;
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

    private void BuildCommentsUIToolkit()
    {
        if (commentsRoot == null)
        {
            return;
        }

        commentsRoot.Clear();
        commentsRoot.AddToClassList("mcb-comments");
        if (!ShouldShowSelectedAssetInteractionUi())
        {
            commentsRoot.style.display = DisplayStyle.None;
            return;
        }

        commentsRoot.style.display = DisplayStyle.Flex;

        var state = EnsureInteractionLoad(SelectedAsset.id);
        var titleRow = CreateRow();
        titleRow.AddToClassList("mcb-comments__title-row");
        var title = CreateLabel("Comments", 13, FontStyle.Bold, Color.white);
        title.AddToClassList("mcb-comments__title");
        titleRow.Add(title);
        if (state != null)
        {
            var count = CreateLabel(state.commentCount.ToString(), 12, FontStyle.Bold, new Color(0.82f, 0.82f, 0.82f));
            count.AddToClassList("mcb-comments__count");
            titleRow.Add(count);
        }
        commentsRoot.Add(titleRow);

        if (state != null && state.isLoading)
        {
            commentsRoot.Add(CreateMessageLabel("Loading comments...", new Color(0.70f, 0.78f, 0.86f)));
        }
        else if (state == null || state.comments.Count == 0)
        {
            commentsRoot.Add(CreateMessageLabel("No comments yet.", new Color(0.62f, 0.62f, 0.62f)));
        }
        else
        {
            foreach (var comment in state.comments.OrderBy(comment => comment.createdAt ?? string.Empty))
            {
                commentsRoot.Add(CreateCommentRowUIToolkit(comment));
            }
        }

        Button submit = null;
        var input = new TextField { multiline = true, value = commentDraft };
        input.AddToClassList("mcb-comment-input");
        input.RegisterValueChangedCallback(evt =>
        {
            commentDraft = evt.newValue ?? "";
            submit?.SetEnabled(!isPostingComment && !string.IsNullOrWhiteSpace(commentDraft));
        });
        input.SetEnabled(!isPostingComment);
        commentsRoot.Add(input);

        submit = CreateTextButton(isPostingComment ? "Posting..." : "Post Comment", PostSelectedAssetComment);
        submit.style.marginTop = 8f;
        submit.style.width = 130f;
        submit.SetEnabled(!isPostingComment && !string.IsNullOrWhiteSpace(commentDraft));
        commentsRoot.Add(submit);
    }

    private VisualElement CreateCommentRowUIToolkit(InteractionRecord comment)
    {
        var row = CreateRow();
        row.AddToClassList("mcb-comment");

        var avatar = new VisualElement();
        avatar.AddToClassList("mcb-comment__avatar");

        var avatarImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
        if (comment != null && comment.fromUserId > 0)
        {
            avatarImage.image = UserService.GetUserAvatar(comment.fromUserId);
        }
        avatarImage.AddToClassList("mcb-comment__avatar-image");
        avatar.Add(avatarImage);
        row.Add(avatar);

        var body = new VisualElement();
        body.AddToClassList("mcb-comment__body");
        string userName = comment?.fromUsername;
        if (comment != null && comment.fromUserId > 0)
        {
            var info = UserService.GetUserInfo(comment.fromUserId);
            if (info != null && !string.IsNullOrWhiteSpace(info.username))
            {
                userName = info.username;
            }
        }

        var header = CreateRow();
        header.AddToClassList("mcb-comment__header");

        var author = CreateLabel(string.IsNullOrWhiteSpace(userName) ? "Unknown user" : userName, 12, FontStyle.Bold, Color.white);
        author.AddToClassList("mcb-comment__author");
        header.Add(author);

        bool isOwnComment = comment != null && comment.fromUserId > 0 && comment.fromUserId == GetCurrentUserId();
        if (isOwnComment)
        {
            var actions = CreateRow();
            actions.AddToClassList("mcb-comment__actions");

            var editButton = CreateIconOnlyButton(MCBInteractionIconKind.Edit, "Edit comment", () => BeginEditComment(comment));
            editButton.SetEnabled(!isUpdatingComment && deletingCommentId == 0);
            actions.Add(editButton);

            var deleteButton = CreateIconOnlyButton(MCBInteractionIconKind.Delete, "Delete comment", () => DeleteSelectedAssetComment(comment));
            deleteButton.SetEnabled(!isUpdatingComment && deletingCommentId != comment.id);
            actions.Add(deleteButton);

            header.Add(actions);
        }

        body.Add(header);

        if (comment != null && editingCommentId == comment.id)
        {
            Button saveButton = null;
            var editInput = new TextField { multiline = true, value = editingCommentDraft };
            editInput.AddToClassList("mcb-comment-edit-input");
            editInput.RegisterValueChangedCallback(evt =>
            {
                editingCommentDraft = evt.newValue ?? "";
                saveButton?.SetEnabled(!isUpdatingComment && !string.IsNullOrWhiteSpace(editingCommentDraft));
            });
            editInput.SetEnabled(!isUpdatingComment);
            body.Add(editInput);

            var editActions = CreateRow();
            editActions.AddToClassList("mcb-comment__edit-actions");
            saveButton = CreateTextButton(isUpdatingComment ? "Saving..." : "Save", () => UpdateSelectedAssetComment(comment.id));
            saveButton.SetEnabled(!isUpdatingComment && !string.IsNullOrWhiteSpace(editingCommentDraft));
            editActions.Add(saveButton);

            var cancelButton = CreateTextButton("Cancel", CancelEditComment);
            cancelButton.SetEnabled(!isUpdatingComment);
            editActions.Add(cancelButton);
            body.Add(editActions);
        }
        else
        {
            var content = CreateLabel(comment != null && !string.IsNullOrWhiteSpace(comment.content) ? comment.content : "(empty comment)", 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
            content.AddToClassList("mcb-comment__content");
            body.Add(content);
        }

        row.Add(body);
        return row;
    }

    private void BuildCreateCustomBaseFormUIToolkit(VisualElement root)
    {
        var header = CreateRow();
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 12f;
        var backButton = CreateTextButton("< Back to custom bases", () =>
        {
            isCreatingCustomBase = false;
            createError = null;
            editor.RefreshUiToolkitSections();
        });
        backButton.style.width = 165f;
        header.Add(backButton);
        var title = CreateLabel("Create custom base", 15, FontStyle.Bold, Color.white);
        title.style.marginLeft = 12f;
        header.Add(title);
        root.Add(header);

        var form = new VisualElement();
        form.AddToClassList("mcb-form-card");
        root.Add(form);

        var thumbnailField = new ObjectField("Thumbnail") { objectType = typeof(Texture2D), allowSceneObjects = false, value = createThumbnail };
        thumbnailField.RegisterValueChangedCallback(evt => createThumbnail = evt.newValue as Texture2D);
        form.Add(thumbnailField);

        var bannerField = new ObjectField("Banner") { objectType = typeof(Texture2D), allowSceneObjects = false, value = createBanner };
        bannerField.RegisterValueChangedCallback(evt => createBanner = evt.newValue as Texture2D);
        form.Add(bannerField);

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

    private bool ShouldShowSelectedAssetInteractionUi()
    {
        return editor.isAuthenticated &&
               editor.HasServerAccess &&
               !MCBPackageVersionService.RequiresMajorUpdate &&
               SelectedAsset != null;
    }

    private AssetInteractionState EnsureInteractionLoad(int assetId)
    {
        var state = GetInteractionState(assetId, true);
        if (state == null || state.loadAttempted || state.isLoading || string.IsNullOrWhiteSpace(editor.authToken))
        {
            return state;
        }

        state.loadAttempted = true;
        state.isLoading = true;
        state.error = null;
        int currentUserId = GetCurrentUserId();

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.LoadAssetInteractionsCoroutine(
                assetId,
                editor.authToken,
                currentUserId,
                (response, error) =>
                {
                    state.isLoading = false;
                    state.error = error;
                    if (response != null)
                    {
                        state.likeCount = response.likeCount;
                        state.commentCount = response.commentCount;
                        state.likedByCurrentUser = response.likedByCurrentUser;
                        state.currentUserLikeId = response.currentUserLikeId ?? 0;
                        state.comments = response.interactions
                            .Where(item => item != null && string.Equals(item.type, InteractionTypes.COMMENT, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));

        return state;
    }

    private AssetInteractionState GetInteractionState(int assetId, bool create)
    {
        if (assetId <= 0)
        {
            return null;
        }

        if (!interactionStates.TryGetValue(assetId, out var state) && create)
        {
            state = new AssetInteractionState();
            interactionStates[assetId] = state;
        }

        return state;
    }

    private void ToggleSelectedAssetLike()
    {
        if (SelectedAsset == null || isTogglingLike)
        {
            return;
        }

        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null || state.isLoading)
        {
            return;
        }

        isTogglingLike = true;
        state.error = null;
        editor.RefreshUiToolkitSections();

        if (state.likedByCurrentUser && state.currentUserLikeId > 0)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(
                InteractionService.DeleteInteractionCoroutine(
                    state.currentUserLikeId,
                    editor.authToken,
                    error =>
                    {
                        isTogglingLike = false;
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            state.error = error;
                        }
                        else
                        {
                            state.likedByCurrentUser = false;
                            state.currentUserLikeId = 0;
                            state.likeCount = Mathf.Max(0, state.likeCount - 1);
                        }
                        editor.RefreshUiToolkitSections();
                        editor.Repaint();
                    }));
            return;
        }

        var payload = new CreateInteractionRequest
        {
            toAsset = SelectedAsset.id,
            type = InteractionTypes.LIKE
        };

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.CreateInteractionCoroutine(
                payload,
                editor.authToken,
                (interaction, error) =>
                {
                    isTogglingLike = false;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                    }
                    else
                    {
                        state.likedByCurrentUser = true;
                        state.currentUserLikeId = interaction != null ? interaction.id : 0;
                        state.likeCount += 1;
                    }
                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }

    private void PostSelectedAssetComment()
    {
        if (SelectedAsset == null || isPostingComment || string.IsNullOrWhiteSpace(commentDraft))
        {
            return;
        }

        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null)
        {
            return;
        }

        isPostingComment = true;
        state.error = null;
        string commentText = commentDraft.Trim();
        editor.RefreshUiToolkitSections();

        var payload = new CreateInteractionRequest
        {
            toAsset = SelectedAsset.id,
            type = InteractionTypes.COMMENT,
            content = commentText
        };

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.CreateInteractionCoroutine(
                payload,
                editor.authToken,
                (interaction, error) =>
                {
                    isPostingComment = false;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                    }
                    else
                    {
                        commentDraft = "";
                        if (interaction != null)
                        {
                            state.comments.Add(interaction);
                        }
                        state.commentCount += 1;
                    }
                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }

    private void BeginEditComment(InteractionRecord comment)
    {
        if (comment == null || comment.fromUserId != GetCurrentUserId())
        {
            return;
        }

        editingCommentId = comment.id;
        editingCommentDraft = comment.content ?? "";
        editor.RefreshUiToolkitSections();
    }

    private void CancelEditComment()
    {
        editingCommentId = 0;
        editingCommentDraft = "";
        editor.RefreshUiToolkitSections();
    }

    private void UpdateSelectedAssetComment(int commentId)
    {
        if (SelectedAsset == null ||
            commentId <= 0 ||
            editingCommentId != commentId ||
            isUpdatingComment ||
            string.IsNullOrWhiteSpace(editingCommentDraft))
        {
            return;
        }

        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null)
        {
            return;
        }

        string nextContent = editingCommentDraft.Trim();
        isUpdatingComment = true;
        state.error = null;
        editor.RefreshUiToolkitSections();

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.UpdateInteractionCoroutine(
                commentId,
                nextContent,
                editor.authToken,
                (interaction, error) =>
                {
                    isUpdatingComment = false;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                    }
                    else if (interaction != null)
                    {
                        int index = state.comments.FindIndex(comment => comment != null && comment.id == interaction.id);
                        if (index >= 0)
                        {
                            state.comments[index] = interaction;
                        }

                        editingCommentId = 0;
                        editingCommentDraft = "";
                    }

                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }

    private void DeleteSelectedAssetComment(InteractionRecord comment)
    {
        if (SelectedAsset == null || comment == null || comment.id <= 0 || comment.fromUserId != GetCurrentUserId())
        {
            return;
        }

        if (!EditorUtility.DisplayDialog("Delete Comment", "Delete this comment permanently?", "Delete", "Cancel"))
        {
            return;
        }

        var state = EnsureInteractionLoad(SelectedAsset.id);
        if (state == null)
        {
            return;
        }

        deletingCommentId = comment.id;
        state.error = null;
        editor.RefreshUiToolkitSections();

        EditorCoroutineUtility.StartCoroutineOwnerless(
            InteractionService.DeleteInteractionCoroutine(
                comment.id,
                editor.authToken,
                error =>
                {
                    deletingCommentId = 0;
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        state.error = error;
                    }
                    else
                    {
                        state.comments.RemoveAll(item => item != null && item.id == comment.id);
                        state.commentCount = Mathf.Max(0, state.commentCount - 1);
                        if (editingCommentId == comment.id)
                        {
                            editingCommentId = 0;
                            editingCommentDraft = "";
                        }
                    }

                    editor.RefreshUiToolkitSections();
                    editor.Repaint();
                }));
    }

    private int GetCurrentUserId()
    {
        var auth = AuthenticationService.GetAuth();
        if (auth == null || string.IsNullOrWhiteSpace(auth.user))
        {
            return 0;
        }

        return int.TryParse(auth.user, out var userId) ? userId : 0;
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

    private static VisualElement CreateRow()
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-row");
        return row;
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

    private static Label CreateMessageLabel(string text, Color color)
    {
        var label = CreateLabel(text, 12, FontStyle.Normal, color);
        label.AddToClassList("mcb-message");
        return label;
    }

    private static VisualElement CreateCardShell()
    {
        var card = new VisualElement();
        card.AddToClassList("mcb-card");
        card.pickingMode = PickingMode.Position;
        return card;
    }

    private static Button CreateToolbarButton(string text, Action onClick)
    {
        var button = CreateTextButton(text, onClick);
        button.AddToClassList("mcb-button--toolbar");
        return button;
    }

    private static Button CreateTextButton(string text, Action onClick)
    {
        var button = new Button(onClick) { text = text };
        button.AddToClassList("mcb-button");
        return button;
    }

    private static Button CreateIconButton(MCBInteractionIconKind iconKind, string text, Action onClick)
    {
        var button = CreateTextButton(string.Empty, onClick);
        button.AddToClassList("mcb-button--icon");

        var image = new MCBInteractionIconElement(iconKind);
        image.AddToClassList("mcb-button-icon");
        button.Add(image);

        button.Add(CreateLabel(text, 12, FontStyle.Bold, Color.white));
        return button;
    }

    private static Button CreateIconOnlyButton(MCBInteractionIconKind iconKind, string tooltip, Action onClick)
    {
        var button = CreateTextButton(string.Empty, onClick);
        button.tooltip = tooltip;
        button.AddToClassList("mcb-button--icon-only");

        var image = new MCBInteractionIconElement(iconKind);
        image.AddToClassList("mcb-button-icon");
        button.Add(image);
        return button;
    }

    public void OnAuthenticationChanged()
    {
        if (!editor.isAuthenticated)
        {
            ResetState(clearSelection: true);
            editor.RefreshUiToolkitSections();
            return;
        }

        RefreshIfNeeded(force: true);
        editor.RefreshUiToolkitSections();
    }

    public void OnProjectChanged()
    {
        RefreshIfNeeded(force: true);
        editor.RefreshUiToolkitSections();
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
        targetFbxFiles.Clear();
        foreach (string path in editor.GetDetectedAvatarFbxPaths())
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (fbx != null)
            {
                targetFbxFiles.Add(fbx);
            }
        }
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
        otherAvatarBaseName = "";
        targetFbxFiles.Clear();
        createError = null;
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
        commentDraft = "";
        editingCommentId = 0;
        editingCommentDraft = "";
        isUpdatingComment = false;
        deletingCommentId = 0;
        selectedAssetBannerFrame = null;
        selectedAssetBannerImage = null;
        selectedAssetBannerMessage = null;
        selectedAssetBannerAssetId = 0;
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
