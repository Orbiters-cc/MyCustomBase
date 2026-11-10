#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// The session's asset, its version lists and their fetch.
public sealed partial class McbSession
{
    /// <summary>The asset whose versions the session lists (the gallery's selection), or null.</summary>
    public AvatarDiscoveredAsset SelectedAsset { get; private set; }

    /// <summary>The model signature the views' asset lists were made for: a view opened later keeps the selection while it is the same.</summary>
    internal string AssetListSignature;

    public AvatarDiscoveredAsset GetSelectedAsset() => SelectedAsset;

    public string GetSelectedAssetDisplayName()
    {
        var selectedAsset = SelectedAsset;
        return selectedAsset != null && !string.IsNullOrWhiteSpace(selectedAsset.name)
            ? selectedAsset.name
            : "Custom Base";
    }

    /// <summary>Shows this asset without touching its versions (a view leaving it, or showing one it already listed).</summary>
    internal void SetSelectedAsset(AvatarDiscoveredAsset asset)
    {
        if (ReferenceEquals(SelectedAsset, asset)) return;
        if (SelectedAsset?.id != asset?.id) EndUserFetch();
        SelectedAsset = asset;
        RequestRefresh(McbViewParts.All);
    }

    /// <summary>
    /// Makes this asset the session's: its versions replace the previous asset's when it is another one, or when
    /// <paramref name="refreshVersions"/> asks for them again (then fetched from the server).
    /// </summary>
    public void Select(AvatarDiscoveredAsset asset, bool refreshVersions)
    {
        if (asset == null) return;
        bool changed = SelectedAsset == null || SelectedAsset.id != asset.id;
        if (changed) EndUserFetch();
        SelectedAsset = asset;
        if (changed || refreshVersions)
        {
            warningsModule.Clear();
            serverVersions.Clear();
            recommendedVersion = null;
            selectedVersionForAction = null;
            fetchAttempted = false;
            if (refreshVersions)
            {
                RefreshAccountAndVersions();
            }
        }

        RequestRefresh(McbViewParts.All);
    }

    public enum AppliedAssetState { Shown, Finding, NotFound }

    private bool findingAppliedAsset;
    private int appliedAssetMissingFor;

    /// <summary>
    /// Makes the asset the custom base applied the session's, with its versions read again (an update asked from another
    /// view applies one of them). The avatar's assets are asked from the server when no view listed them: no Inspector is
    /// needed. <see cref="AppliedAssetState.NotFound"/> when the server does not offer it for the avatar (or signed out).
    /// </summary>
    public AppliedAssetState ShowAppliedAsset()
    {
        int id = customBaseTarget != null ? customBaseTarget.appliedCustomBaseAssetId : 0;
        if (id <= 0 || !isAuthenticated) return AppliedAssetState.NotFound;
        if (SelectedAsset != null && SelectedAsset.id == id) return AppliedAssetState.Shown;
        if (findingAppliedAsset) return AppliedAssetState.Finding;
        if (appliedAssetMissingFor == id) return AppliedAssetState.NotFound;

        findingAppliedAsset = true;
        string token = authToken;
        EditorCoroutineUtility.StartCoroutineOwnerless(AvatarAssetDiscoveryService.DiscoverAssetsCoroutine(
            token, GetDetectedAvatarFbxPaths(), true, (response, error) =>
            {
                findingAppliedAsset = false;
                // Answered for another account, or the avatar applied another asset meanwhile: asked again next time.
                if (customBaseTarget == null || token != authToken || customBaseTarget.appliedCustomBaseAssetId != id) return;
                var asset = response?.assets?.FirstOrDefault(a => a != null && a.id == id);
                if (asset == null)
                {
                    appliedAssetMissingFor = id;
                    return;
                }
                appliedAssetMissingFor = 0;
                if (SelectedAsset == null || SelectedAsset.id != id) Select(asset, refreshVersions: true);
            }, customBaseTarget));
        return AppliedAssetState.Finding;
    }

    /// <summary>Forgets that the applied asset was not found, so the next <see cref="ShowAppliedAsset"/> asks again (a retry).</summary>
    public void LookForAppliedAssetAgain() => appliedAssetMissingFor = 0;

    // Other tools (My Avatar) show the applied custom base by name: keep it from the gallery's asset once it is known.
    internal void SyncAppliedCustomBaseName()
    {
        var asset = SelectedAsset;
        if (customBaseTarget == null || asset == null || asset.id <= 0 || asset.id != customBaseTarget.appliedCustomBaseAssetId ||
            string.IsNullOrWhiteSpace(asset.name) || asset.name == customBaseTarget.appliedCustomBaseName) return;
        customBaseTarget.appliedCustomBaseName = asset.name;
        EditorUtility.SetDirty(customBaseTarget);
        MCBReFitIntegration.NotifyCustomBaseChanged(customBaseTarget);
    }

    // --- Local versions: served by the VersionRepository scan, which classifies folders by their build manifest
    // (unsubmitted artifacts vs imported/downloaded versions) and caches results internally. ---
    public void LoadUnsubmittedVersions(bool forceRefresh = false)
    {
        var scan = VersionRepository.Scan(forceRefresh);
        unsubmittedVersions.Clear();
        unsubmittedVersions.AddRange(scan.unsubmitted);
    }

    public void LoadImportedVersions(bool forceRefresh = false)
    {
        var scan = VersionRepository.Scan(forceRefresh);
        importedVersions.Clear();
        importedVersions.AddRange(scan.imported);
    }

    /// <summary>An unsubmitted version was deleted: the lists are read again and the forms that built it forget it.</summary>
    internal void RemoveUnsubmittedVersion(CustomBaseVersion version)
    {
        VersionRepository.InvalidateCache();
        LoadUnsubmittedVersions(true);
        UnsubmittedVersionRemoved?.Invoke(version);
        Repaint();
    }

    public List<CustomBaseVersion> GetAllVersions()
    {
        if (!HasServerAccess)
        {
            return importedVersions
                .Where(v => v != null)
                .OrderByDescending(v => VersionNumber(v.version))
                .ToList();
        }

        int selectedAssetId = SelectedAsset?.id ?? 0;
        return VersionRepository.MergeAvailableVersions(selectedAssetId, serverVersions, importedVersions, unsubmittedVersions, OriginalBaseLibrary.ActiveKey(SelectedAsset))
            .OrderByDescending(v => VersionNumber(v.version)).ToList();
    }

    public Version ParseVersion(string v) => VersionNumber(v);

    /// <summary>A version label as a comparable number ("0.5" reads as 0.5.0; anything unreadable as 0.0).</summary>
    public static Version VersionNumber(string v)
    {
        if (string.IsNullOrEmpty(v)) return new Version(0, 0);
        if (v.Count(c => c == '.') == 0) v += ".0.0";
        if (v.Count(c => c == '.') == 1) v += ".0";
        return Version.TryParse(v, out var ver) ? ver : new Version(0, 0);
    }

    public int CompareVersions(string v1, string v2) => ParseVersion(v1).CompareTo(ParseVersion(v2));

    // --- Server versions. Every fetch goes through AsyncVersionService, which runs one fetch per request identity (model,
    // account, asset, original) and queues a forced one asked for while it runs; every session hears every answer and
    // keeps the ones for its own current request. A fetch the member asked for ("Check for Updates", a publication) also
    // shows as running and picks a version once answered. ---

    // The request of the fetch the member asked for, until its answer (or until the selection or account changes).
    private string userFetchPath, userFetchToken, userFetchKey;
    private int userFetchAssetId;
    private bool userFetchPending;

    /// <summary>
    /// Reads the asset's versions from the server again (not the cache): "Check for Updates", a publication. One running
    /// for the same request makes this one run after it, so the list shows what was just published.
    /// </summary>
    public void RefreshVersions()
    {
        if (!HasServerAccess) return;
        warningsModule.Clear();
        accessDeniedAssetId = null;
        fetchAttempted = true;
        actions.UpdateCurrentBaseFbxHash();

        var selectedAsset = SelectedAsset;
        if (selectedAsset == null)
        {
            MCBLogger.Log("[McbSession] Version fetch skipped because no asset is selected in the gallery.");
            serverVersions = new List<CustomBaseVersion>();
            recommendedVersion = null;
            RequestRefresh(McbViewParts.All);
            return;
        }

        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrEmpty(fbxPath) || string.IsNullOrEmpty(authToken) || selectedAsset.id <= 0)
        {
            MCBLogger.LogWarning("[McbSession] Version fetch aborted because the avatar has no base model.");
            serverVersions = new List<CustomBaseVersion>();
            actions.UpdateAppliedVersionAndState(); // This will clear the applied state
            RequestRefresh(McbViewParts.All);
            return;
        }

        string sourceVersionKey = OriginalBaseLibrary.ActiveKey(selectedAsset);
        userFetchPath = fbxPath;
        userFetchToken = authToken;
        userFetchAssetId = selectedAsset.id;
        userFetchKey = sourceVersionKey;
        userFetchPending = true;
        isFetching = true;
        MCBLogger.Log($"[McbSession] Starting version fetch. assetId={selectedAsset.id} | currentFbxPath={fbxPath}");
        AsyncVersionService.Instance.StartVersionFetchInBackground(fbxPath, authToken, selectedAsset.id, useCache: false, sourceVersionKey: sourceVersionKey);
    }

    // The member's fetch answers nobody any more (another asset or account): it no longer shows as running.
    private void EndUserFetch()
    {
        userFetchPending = false;
        isFetching = false;
    }

    // Whether an answer is the one to the fetch the member asked for, even if the selection changed since (it then ends it).
    private bool AnswersUserFetch(VersionFetchRequest request) =>
        userFetchPending && request != null && request.Matches(userFetchPath, userFetchToken, userFetchAssetId, userFetchKey);

    // Every session hears the shared service: keep only the answers to this session's current selection.
    private bool IsCurrentVersionRequest(VersionFetchRequest request)
    {
        var selectedAsset = SelectedAsset;
        return request != null && selectedAsset != null &&
               request.Matches(GetCurrentFBXPath(), authToken, selectedAsset.id, OriginalBaseLibrary.ActiveKey(selectedAsset));
    }

    private void OnVersionsUpdated(VersionFetchRequest request, List<CustomBaseVersion> versions, CustomBaseVersion recommended)
    {
        bool userFetch = AnswersUserFetch(request);
        if (!IsCurrentVersionRequest(request))
        {
            if (userFetch) EndUserFetch();
            return;
        }

        serverVersions = new List<CustomBaseVersion>(versions ?? new List<CustomBaseVersion>());
        recommendedVersion = recommended;
        fetchError = null; // Clear any previous errors
        accessDeniedAssetId = null; // Clear special access state on success

        actions.UpdateAppliedVersionAndState();
        if (userFetch)
        {
            actions.SmartSelectVersion();
            EndUserFetch();
        }

        RequestRefresh(McbViewParts.All);
        MCBLogger.Log($"[McbSession] Updated with {serverVersions.Count} server versions");
    }

    private void OnVersionFetchError(VersionFetchRequest request, string error)
    {
        bool userFetch = AnswersUserFetch(request);
        if (userFetch) EndUserFetch();
        if (!IsCurrentVersionRequest(request))
        {
            return;
        }

        if (error != null && error.StartsWith(NetworkService.AccessDeniedPrefix, StringComparison.Ordinal))
        {
            // No access: the version list shows the store link, never versions cached while the member had access.
            accessDeniedAssetId = error.Substring(NetworkService.AccessDeniedPrefix.Length);
            if (userFetch) warningsModule.Clear(); // Do not show generic error box
            serverVersions = new List<CustomBaseVersion>();
            recommendedVersion = null;
            fetchError = null;
            RequestRefresh(McbViewParts.All);
            return;
        }

        // A transient backend failure keeps the last known version metadata: local imported data and the persisted
        // applied marker remain authoritative while offline. What the member asked for says so where they look.
        if (userFetch) warningsModule.AddWarning(error, MessageType.Error, "Fetch failed");
        else fetchError = error;
        RequestRefresh(McbViewParts.All);
        MCBLogger.LogError($"[McbSession] Version fetch error: {error}");
    }

    public void LoadCachedVersionsForCurrentSelection()
    {
        TryLoadCachedVersionsAndRefetch(false);
    }

    private void TryLoadCachedVersionsAndRefetch(bool forceRefetch)
    {
        string fbxPath = GetCurrentFBXPath();
        if (string.IsNullOrEmpty(fbxPath))
        {
            return;
        }

        var selectedAsset = SelectedAsset;
        if (selectedAsset == null)
        {
            serverVersions = new List<CustomBaseVersion>();
            recommendedVersion = null;
            return;
        }

        var versionService = AsyncVersionService.Instance;
        var cached = versionService.GetCachedVersions(fbxPath, authToken, selectedAsset.id, OriginalBaseLibrary.ActiveKey(selectedAsset));
        if (HasServerAccess && cached.versions.Count > 0)
        {
            serverVersions = cached.versions;
            recommendedVersion = cached.recommended;
            actions.UpdateAppliedVersionAndState();
            RequestRefresh(McbViewParts.All);
        }

        if (HasServerAccess && (forceRefetch || cached.versions.Count == 0))
        {
            fetchAttempted = true;
            versionService.StartVersionFetchInBackground(fbxPath, authToken, selectedAsset.id, useCache: !forceRefetch, sourceVersionKey: OriginalBaseLibrary.ActiveKey(selectedAsset));
        }
    }

    /// <summary>Reads the signed-in member's details and the selected asset's versions again (in the background).</summary>
    public void RefreshAccountAndVersions()
    {
        // Clear failed user info requests to allow retry
        UserService.ClearAllFailedRequests();

        try
        {
            RequestSignedInUserInfo(AuthenticationService.GetAuth());
        }
        catch (Exception ex)
        {
            MCBLogger.LogException(ex);
        }

        SyncBaseFbxFilesFromSelectedAsset();

        string fbxPath = GetCurrentFBXPath();
        var selectedAsset = SelectedAsset;
        if (!string.IsNullOrEmpty(fbxPath) && HasServerAccess && selectedAsset != null)
        {
            fetchAttempted = true;
            AsyncVersionService.Instance.StartVersionFetchInBackground(fbxPath, authToken, selectedAsset.id, useCache: false, sourceVersionKey: OriginalBaseLibrary.ActiveKey(selectedAsset));
        }
        RequestRefresh(McbViewParts.All);
    }

    /// <summary>
    /// Publishes a built version (the creator form's Publish, the version list's Upload, MCP), then lets the other avatars
    /// showing the same asset read their lists again.
    /// </summary>
    public System.Collections.IEnumerator Publish(CustomBaseVersion version, Action onPublished = null, bool interactive = true, string assetName = null)
    {
        yield return VersionPublisher.PublishCoroutine(this, networkService, fileManagerService, version, onPublished, interactive, assetName);
        RefreshSiblingVersionLists();
    }

    /// <summary>A build or publication changed the version folders: the sessions showing the same asset read their lists again.</summary>
    internal void RefreshSiblingVersionLists()
    {
        foreach (var other in Live)
        {
            if (other == this || other.SelectedAsset?.id != SelectedAsset?.id) continue;
            other.LoadUnsubmittedVersions(true);
            other.LoadImportedVersions(true);
            other.serverVersions = serverVersions.ToList();
            other.serializedObject.Update();
            other.RequestRefresh(McbViewParts.All);
        }
        RequestRefresh(McbViewParts.All);
    }

    /// <summary>
    /// Applies a version after the usual confirmation, downloading it first when needed. Returns false when nothing
    /// started (declined, or another download or apply is running).
    /// </summary>
    public bool RequestApply(CustomBaseVersion version)
    {
        if (version == null || isDownloading || isApplying)
        {
            return false;
        }

        bool isDownloaded = MCBUtils.IsVersionDownloaded(version);
        string assetName = GetSelectedAssetDisplayName();

        bool shouldApply = NativeMeshPayloadService.VersionUsesAdvancedMesh(version) ||
                           EditorUtility.DisplayDialog("Confirm Transformation", $"This will modify your base FBX file using {assetName} version '{version.version}'.\nA backup will be created.", "Proceed", "Cancel");
        if (!shouldApply)
        {
            return false;
        }

        selectedVersionForAction = version;
        selectedCustomVersionForAction = null;
        if (isDownloaded)
        {
            actions.StartApplyVersion();
        }
        else
        {
            actions.StartVersionDownload(version, true);
        }

        RequestRefresh(McbViewParts.All);
        return true;
    }
}
#endif
