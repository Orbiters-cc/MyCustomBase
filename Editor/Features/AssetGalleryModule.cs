#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

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

        return hasFetchedCompatibleAssets || hasFetchedAllAssets || isLoading;
    }

    public void Draw()
    {
        RefreshIfNeeded();

        if (SelectedAsset != null)
        {
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
            DrawGallery(matchingAssets);

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

    private void DrawGallery(List<AvatarDiscoveredAsset> assets)
    {
        float availableWidth = Mathf.Max(220f, EditorGUIUtility.currentViewWidth - 60f);
        int columns = availableWidth > 700f ? 3 : availableWidth > 420f ? 2 : 1;
        float spacing = 10f;
        float cardWidth = Mathf.Floor((availableWidth - (spacing * (columns - 1))) / columns);
        cardWidth = Mathf.Min(cardWidth, 190f);
        float cardHeight = 254f;

        for (int i = 0; i < assets.Count; i += columns)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int column = 0; column < columns; column++)
                {
                    int assetIndex = i + column;
                    if (assetIndex >= assets.Count)
                    {
                        GUILayout.Space(cardWidth);
                        continue;
                    }

                    DrawGalleryCard(assets[assetIndex], cardWidth, cardHeight);
                    if (column < columns - 1)
                    {
                        GUILayout.Space(spacing);
                    }
                }
            }

            GUILayout.Space(8f);
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
