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

public partial class AssetGalleryModule
{
    private readonly MCBEditor editor;
    private readonly GalleryBrowser galleryBrowser;
    private readonly SelectedAssetPanel selectedAssetPanel;
    private readonly AssetInteractionPanel assetInteractionPanel;
    private readonly CustomBaseCreateForm customBaseCreateForm;
    private readonly PhotoshootPanel photoshootPanel;

    private Vector2 scrollPosition;
    private string lastAvatarSignature;
    private string loadingAvatarSignature;
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
    private bool isEditingSelectedAssetMedia;
    private bool isSavingSelectedAssetMedia;
    private string selectedAssetMediaEditError;
    private Texture2D editThumbnail;
    private Texture2D editBanner;
    private PhotoshootGenerationService.Catalog photoshootCatalog;
    private PhotoshootGenerationService.LivePreviewSession photoshootPreviewSession;
    private int photoshootBodyPoseIndex;
    private int photoshootBackgroundIndex;
    private int photoshootLightPresetIndex;
    private float photoshootZoom = 1.35f;
    private Vector2 photoshootPlacement = Vector2.zero;
    private float photoshootRotationDegrees;
    private readonly HashSet<string> photoshootSelectedFaceBlendshapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> photoshootPoseIconCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> photoshootLightIconCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private Image photoshootStagePreviewImage;
    private Image photoshootThumbnailPreviewImage;
    private readonly List<Button> photoshootLightOptionButtons = new List<Button>();
    private readonly List<Button> photoshootPoseOptionButtons = new List<Button>();
    private readonly List<Button> photoshootBackgroundOptionButtons = new List<Button>();
    private readonly Dictionary<string, Button> photoshootExpressionChipButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
    private string photoshootStatus;
    private string photoshootError;
    private bool isGeneratingPhotoshootImage;
    private int photoshootStateVersion;
    private int photoshootLivePreviewRefreshTicket;
    private const float PhotoshootMinZoom = 0.65f;
    private const float PhotoshootMaxZoom = 7.5f;
    private const float PhotoshootMinRotationDegrees = -90f;
    private const float PhotoshootMaxRotationDegrees = 90f;
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
    private int editingCommentId;
    private string editingCommentDraft = "";
    private bool isUpdatingComment;
    private int deletingCommentId;
    private VisualElement selectedAssetBannerFrame;
    private Image selectedAssetBannerImage;
    private Label selectedAssetBannerMessage;
    private int selectedAssetBannerAssetId;
    private Button selectedAssetLikeButton;
    private Label selectedAssetLikeCountLabel;
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
        public bool desiredLikedByCurrentUser;
        public bool pendingLikeSync;
        public bool isLikeSyncRunning;
        public List<InteractionRecord> comments = new List<InteractionRecord>();
    }

    private sealed class GalleryBrowser
    {
        private readonly AssetGalleryModule module;

        public GalleryBrowser(AssetGalleryModule module)
        {
            this.module = module;
        }

        public void BuildUIToolkit()
        {
            module.BuildGalleryUIToolkit();
        }
    }

    private sealed class SelectedAssetPanel
    {
        private readonly AssetGalleryModule module;

        public SelectedAssetPanel(AssetGalleryModule module)
        {
            this.module = module;
        }

        public void BuildBannerUIToolkit(VisualElement root, AvatarDiscoveredAsset selectedAsset)
        {
            module.BuildSelectedAssetBannerUIToolkit(root, selectedAsset);
        }

        public void BuildActionsUIToolkit()
        {
            module.BuildSelectedAssetActionsUIToolkit();
        }
    }

    private sealed class AssetInteractionPanel
    {
        private readonly AssetGalleryModule module;

        public AssetInteractionPanel(AssetGalleryModule module)
        {
            this.module = module;
        }

        public void BuildCommentsUIToolkit()
        {
            module.BuildCommentsUIToolkit();
        }

        public AssetInteractionState EnsureInteractionLoad(int assetId)
        {
            return module.EnsureInteractionLoad(assetId);
        }
    }

    private sealed class CustomBaseCreateForm
    {
        private readonly AssetGalleryModule module;

        public CustomBaseCreateForm(AssetGalleryModule module)
        {
            this.module = module;
        }

        public void BuildUIToolkit(VisualElement root)
        {
            module.BuildCreateCustomBaseFormUIToolkit(root);
        }
    }

    private sealed class PhotoshootPanel
    {
        private readonly AssetGalleryModule module;

        public PhotoshootPanel(AssetGalleryModule module)
        {
            this.module = module;
        }

        public void BuildSelectedAssetMediaEditUIToolkit(VisualElement root)
        {
            module.BuildSelectedAssetMediaEditUIToolkit(root);
        }
    }

    public AssetGalleryModule(MCBEditor editor)
    {
        this.editor = editor;
        galleryBrowser = new GalleryBrowser(this);
        selectedAssetPanel = new SelectedAssetPanel(this);
        assetInteractionPanel = new AssetInteractionPanel(this);
        customBaseCreateForm = new CustomBaseCreateForm(this);
        photoshootPanel = new PhotoshootPanel(this);
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
        selectedAssetLikeButton = null;
        selectedAssetLikeCountLabel = null;
        ResetSelectedAssetMediaEditState(destroyPreviewTexture: true);
        ReleasePhotoshootPreviewTexture();
    }

    public void RefreshUIToolkit()
    {
        galleryBrowser.BuildUIToolkit();
        selectedAssetPanel.BuildActionsUIToolkit();
        assetInteractionPanel.BuildCommentsUIToolkit();
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

        if (SelectedAsset != null)
        {
            UpdateSelectedAssetLikeVisual(SelectedAsset.id, GetInteractionState(SelectedAsset.id, false));
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

    private static VisualElement CreateRow()
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-row");
        return row;
    }

    private static Label CreateLabel(string text, int fontSize, FontStyle fontStyle, Color color)
    {
        var label = new Label(CreateDisplayText(text));
        label.AddToClassList("mcb-label");
        label.style.fontSize = fontSize;
        label.style.unityFontStyleAndWeight = fontStyle;
        label.style.color = color;
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        return label;
    }

    private static string CreateDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder builder = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (builder == null)
                {
                    builder = new StringBuilder(text.Length);
                    builder.Append(text, 0, i);
                }

                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                if (builder == null)
                {
                    builder = new StringBuilder(text.Length);
                    builder.Append(text, 0, i);
                }
                continue;
            }

            builder?.Append(c);
        }

        return builder != null ? builder.ToString().Trim() : text;
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
        var button = new Button { text = text };
        button.AddToClassList("mcb-button");
        RegisterImmediateClick(button, onClick);
        return button;
    }

    private static void RegisterImmediateClick(Button button, Action onClick)
    {
        if (button == null || onClick == null)
        {
            return;
        }

        bool suppressNextClicked = false;
        long lastImmediateTicks = 0L;

        void activateImmediate(EventBase evt)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!button.enabledInHierarchy ||
                now - lastImmediateTicks < TimeSpan.TicksPerMillisecond * 25L)
            {
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                return;
            }

            lastImmediateTicks = now;
            suppressNextClicked = true;
            button.schedule.Execute(() => suppressNextClicked = false).StartingIn(1000);
            evt.StopImmediatePropagation();
            evt.PreventDefault();
            onClick();
        }

        button.clicked += () =>
        {
            if (suppressNextClicked)
            {
                suppressNextClicked = false;
                return;
            }

            onClick();
        };

        button.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0)
            {
                return;
            }

            activateImmediate(evt);
        }, TrickleDown.TrickleDown);
        button.RegisterCallback<MouseDownEvent>(evt =>
        {
            if (evt.button != 0)
            {
                return;
            }

            activateImmediate(evt);
        }, TrickleDown.TrickleDown);
        button.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode != KeyCode.Return &&
                evt.keyCode != KeyCode.KeypadEnter &&
                evt.keyCode != KeyCode.Space)
            {
                return;
            }

            activateImmediate(evt);
        }, TrickleDown.TrickleDown);
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

    private void ResetState(bool clearSelection)
    {
        compatibleAssets.Clear();
        allAssets.Clear();
        hasFetchedCompatibleAssets = false;
        hasFetchedAllAssets = false;
        isLoading = false;
        loadingAvatarSignature = null;
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
        selectedAssetLikeButton = null;
        selectedAssetLikeCountLabel = null;
        ResetSelectedAssetMediaEditState(destroyPreviewTexture: true);
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
