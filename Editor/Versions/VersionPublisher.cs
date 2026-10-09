#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Publishes a built version artifact: validates the stored outputs against the
/// manifest (hard gate), warns about source drift (soft gate), re-creates the upload
/// zip from exactly the manifest-listed files, streams it to the server with progress
/// and cancel, and only after a confirmed success marks the artifact as published and
/// runs the post-publish steps (version refetch + Unit Git release checkpoint).
///
/// This is the single upload path: both the creator-form Publish button and the
/// version-list Upload button go through here. Publishing never rebuilds or
/// repackages from live project state â€” if the artifact is not intact, the user is
/// told to rebuild explicitly.
/// </summary>
public static class VersionPublisher
{
    public const long MaxVersionPackageUploadBytes = 600L * 1024L * 1024L;

    private sealed class Progress
    {
        public string message = "Checking built version files…";
        public float value;
    }

    public static IEnumerator PublishCoroutine(MCBEditor editor, NetworkService networkService,
        FileManagerService fileManagerService, CustomBaseVersion version, Action onPublished = null, bool interactive = true,
        string assetName = null)
    {
        if (editor.isSubmitting)
        {
            editor.submitError = "This avatar is already building or publishing a version. Wait for it to finish, then publish again.";
            yield break;
        }
        if (!VersionOperationGuard.TryAcquire("publishing version " + version.version, out var operation, out string busy))
        {
            editor.submitError = busy;
            editor.warningsModule.AddWarning(busy, MessageType.Warning, "Publish not started");
            editor.RefreshUiToolkitSections();
            yield break;
        }
        editor.isSubmitting = true;
        editor.submitError = "";
        editor.warningsModule.Clear();
        editor.RefreshUiToolkitSections();
        var cancellation = new CancellationTokenSource();
        var progress = new Progress();
        Task task = null;
        bool success = false;
        try
        {
            task = PublishBuiltAsync(editor, networkService, fileManagerService, version, interactive, progress, cancellation.Token);
            while (!task.IsCompleted)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Publishing " + version.version, progress.message, progress.value)) cancellation.Cancel();
                yield return null;
            }
            try { task.GetAwaiter().GetResult(); success = true; }
            catch (OperationCanceledException)
            {
                editor.submitError = "Publishing was cancelled. Confirmed source uploads are retained; Upload resumes the remaining sources.";
                editor.warningsModule.AddWarning(editor.submitError, MessageType.Info, "Publishing cancelled");
            }
            catch (Exception ex)
            {
                editor.submitError = OperationErrorReporter.Report(editor,
                    OperationErrorClassifier.Classify(ex, "Publishing the version"), "Upload failed");
            }
        }
        finally
        {
            operation.Dispose();
            cancellation.Cancel();
            cancellation.Dispose();
            EditorUtility.ClearProgressBar();
            editor.isSubmitting = false;
            editor.RefreshUiToolkitSections();
            editor.Repaint();
        }
        if (!success) yield break;
        onPublished?.Invoke();
        new VersionActions(editor, networkService, fileManagerService).StartVersionFetch();
        // The checkpoint names the published asset, which need not be the gallery's selection (creator window, MCP).
        string publishedAssetName = ResolvePublishedAssetName(version, assetName,
            new[] { editor.GetSelectedAsset() }.Concat(Resources.FindObjectsOfTypeAll<MCBEditor>().Select(other => other.GetSelectedAsset())));
        bool checkpoint = UnitGitReleasePublisher.TryPublishReleaseCheckpoint(version, publishedAssetName, out string checkpointMessage);
        if (interactive) EditorUtility.DisplayDialog("Publish successful", "Version " + version.version + " and all its source variants have been uploaded." +
            (checkpoint ? "\n\nA local release checkpoint was created." : ""), "OK");
    }

    private static async Task PublishBuiltAsync(MCBEditor editor, NetworkService network,
        FileManagerService files, CustomBaseVersion shown, bool interactive, Progress progress, CancellationToken cancellation)
    {
        var artifact = VersionRepository.GetArtifact(shown);
        var parts = VersionPublishPlan.Create(artifact);
        ValidateVersionMetadataForUpload(artifact.Metadata);
        using (VersionRepository.BeginOperationOnFolder(artifact.FolderUnityPath))
        {
            var validation = await Task.Run(() => VersionRepository.Validate(artifact, cancellation), cancellation);
            if (!validation.IsPublishable) throw new InvalidDataException(validation.Describe() + " Rebuild this version in the creator form.");
            cancellation.ThrowIfCancellationRequested();
            if (interactive && validation.state == ArtifactState.SourceDrift && !EditorUtility.DisplayDialog("Source files changed since build",
                validation.Describe() + "\n\nThe stored build is intact. Publish it as built, or cancel to rebuild with your current source files.", "Publish as built", "Cancel"))
                throw new OperationCanceledException();

            progress.message = "Checking completed source uploads…";
            var remote = await network.FetchVersionsAsync(OriginalBaseSupportService.Url(shown.assetId, "/versions") + "?allSourceVersions=1", editor.authToken);
            if (!remote.success) throw new IOException(remote.error);
            var existing = remote.response.versions.FirstOrDefault(v => v.version == shown.version);
            var completed = new System.Collections.Generic.HashSet<string>();
            if (existing != null)
            {
                if (parts.Length == 1 && artifact.Metadata.originalBaseVersions == null)
                    throw new InvalidOperationException("This version already exists. Choose a new version number.");
                ValidateResume(artifact.Metadata, existing);
                foreach (var source in existing.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>()) completed.Add(source.key);
            }
            else
            {
                ValidateNewVersionNumber(shown.version, artifact.Metadata.parentVersion,
                    remote.response.versions.Select(v => v.version), editor.CompareVersions);
            }
            bool created = existing != null;
            for (int i = 0; i < parts.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var part = parts[i];
                if (completed.Contains(part.Metadata.sourceVersionKey)) continue;
                string label = part.Metadata.originalBaseVersions?.FirstOrDefault()?.label ?? shown.version;
                string prefix = (i + 1) + "/" + parts.Length + " · " + label;
                progress.message = "Preparing " + prefix;
                string zip = null;
                try
                {
                    zip = await Task.Run(() => files.CreateZipFromManifestOutputs(part, cancellation,
                        p => progress.value = (i + p * .35f) / parts.Length), cancellation);
                    long bytes = new FileInfo(zip).Length;
                    if (bytes > MaxVersionPackageUploadBytes) throw new InvalidOperationException("Version package is too large to upload (" + DiskUtils.FormatBytes(bytes) + "). Maximum allowed size is 600 MB.");
                    cancellation.ThrowIfCancellationRequested();
                    string url = created
                        ? OriginalBaseSupportService.Url(shown.assetId, "/versions/" + Uri.EscapeDataString(shown.version) + "/source-support")
                        : MCBUtils.getApiUrl() + MCBUtils.NEW_VERSION_ENDPOINT;
                    object metadata = created ? (object)new { originalBaseVersions = part.Metadata.originalBaseVersions } : part.Metadata;
                    progress.message = "Uploading " + prefix + " (" + DiskUtils.FormatBytes(bytes) + ")";
                    var response = await network.SubmitNewVersionStreamingAsync(url, editor.authToken, zip,
                        JsonConvert.SerializeObject(metadata, new StringEnumConverter()),
                        (p, sent) => progress.value = (i + .35f + .65f * p) / parts.Length, cancellation);
                    if (response.cancelled) throw new OperationCanceledException();
                    if (!response.success) throw new IOException(response.error + " Confirmed source uploads are retained; retry to resume.");
                    created = true;
                }
                finally { if (zip != null && File.Exists(zip)) File.Delete(zip); }
            }
            VersionRepository.MarkPublished(artifact);
            shown.isUnsubmitted = false;
            editor.LoadUnsubmittedVersions(true);
            editor.LoadImportedVersions(true);
            AvatarAssetDiscoveryService.InvalidateDiscoveryCache();
        }
    }

    private static readonly JsonSerializer ServerJson = JsonSerializer.Create(new JsonSerializerSettings { Converters = { new StringEnumConverter() } });

    /// <summary>
    /// A retry resumes a multi-original publish only when the server's version is this build. Settings are compared the
    /// way the server stores them: trimmed title and changelog (a blank changelog is replaced by an AI-written one),
    /// normalized extra customization, and empty collections for missing ones.
    /// </summary>
    internal static void ValidateResume(CustomBaseVersion local, CustomBaseVersion remote)
    {
        bool changelogMatches = string.IsNullOrWhiteSpace(local.changelog) || ServerText(remote.changelog) == ServerText(local.changelog);
        if (remote.defaultAviVersion != local.defaultAviVersion || ServerText(remote.title) != ServerText(local.title) || remote.scope != local.scope ||
            !changelogMatches || !JToken.DeepEquals(ServerExtraCustomization(remote.extraCustomization), ServerExtraCustomization(local.extraCustomization)) ||
            !JToken.DeepEquals(ServerArray(remote.customBlendshapes), ServerArray(local.customBlendshapes)) ||
            !JToken.DeepEquals(ServerObject(remote.dependencies), ServerObject(local.dependencies)))
            throw new InvalidOperationException("This version number already contains different settings. Choose a new version number.");
        var originals = local.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>();
        foreach (var source in remote.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>())
        {
            var match = originals.FirstOrDefault(v => v.key == source.key);
            if (match == null || !(match.versionFiles ?? Array.Empty<ModelFileData>()).Select(f => f.path + ":" + f.hash).OrderBy(f => f)
                .SequenceEqual((source.versionFiles ?? Array.Empty<ModelFileData>()).Select(f => f.path + ":" + f.hash).OrderBy(f => f)))
                throw new InvalidOperationException("This version number already contains different built files. Choose a new version number.");
        }
        if (remote.originalBaseVersions == null || remote.originalBaseVersions.Length == 0)
            throw new InvalidOperationException("The existing version cannot be verified against this local build. Choose a new version number.");
    }

    private static string ServerText(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JArray ServerArray(object value) => value == null ? new JArray() : (JArray)JToken.FromObject(value, ServerJson);

    private static JObject ServerObject(object value) => value == null ? new JObject() : (JObject)JToken.FromObject(value, ServerJson);

    // The server's normalizeExtraCustomization: trimmed, distinct flags; objects without blank keys or empty values;
    // suggestRealistic as a trimmed, distinct list; anything else dropped.
    internal static JArray ServerExtraCustomization(IEnumerable<object> entries)
    {
        var result = new JArray();
        foreach (var entry in entries ?? Enumerable.Empty<object>())
        {
            var token = entry == null ? null : JToken.FromObject(entry, ServerJson);
            if (token?.Type == JTokenType.String)
            {
                string flag = ((string)token).Trim();
                if (flag.Length > 0 && !result.Any(existing => existing.Type == JTokenType.String && (string)existing == flag)) result.Add(flag);
            }
            else if (token is JObject entryObject)
            {
                var normalized = new JObject();
                foreach (var property in entryObject.Properties())
                {
                    string key = property.Name.Trim();
                    if (key.Length == 0) continue;
                    if (key != "suggestRealistic") { normalized[key] = property.Value; continue; }
                    var meshPaths = ServerStringList(property.Value);
                    if (meshPaths.Count > 0) normalized[key] = meshPaths;
                }
                if (normalized.Count > 0) result.Add(normalized);
            }
        }
        return result;
    }

    private static JArray ServerStringList(JToken value)
    {
        IEnumerable<string> items = Enumerable.Empty<string>();
        if (value is JArray array) items = array.Select(item => item.Type == JTokenType.String ? ((string)item).Trim() : "");
        else if (value.Type == JTokenType.String) items = new[] { ((string)value).Trim() };
        else if (value is JObject map) items = map.Properties()
            .Where(p => p.Value.Type != JTokenType.Null && !(p.Value.Type == JTokenType.Boolean && !(bool)p.Value))
            .Select(p => p.Name.Trim());
        return JArray.FromObject(items.Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The server's rule, applied before building and publishing (window and MCP alike): a new version number is higher
    /// than its parent, or than every published version of the asset when it has no parent.
    /// </summary>
    public static void ValidateNewVersionNumber(string version, string parentVersion, IEnumerable<string> publishedVersions,
        Func<string, string, int> compare)
    {
        if (!string.IsNullOrWhiteSpace(parentVersion))
        {
            if (compare(version, parentVersion) <= 0)
                throw new InvalidOperationException("Version " + version + " must be higher than its parent version " + parentVersion + ".");
            return;
        }
        string latest = null;
        foreach (string published in publishedVersions ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(published) && (latest == null || compare(published, latest) > 0)) latest = published;
        if (latest != null && compare(version, latest) <= 0)
            throw new InvalidOperationException("Version " + version + " must be higher than the latest published version " + latest + ".");
    }

    internal static string ResolvePublishedAssetName(CustomBaseVersion version, string preferred, IEnumerable<AvatarDiscoveredAsset> known)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim();
        var asset = (known ?? Enumerable.Empty<AvatarDiscoveredAsset>())
            .FirstOrDefault(candidate => candidate != null && version != null && candidate.id == version.assetId && !string.IsNullOrWhiteSpace(candidate.name));
        return asset?.name.Trim();
    }

    public static void ValidateVersionMetadataForUpload(CustomBaseVersion metadata)
    {
        if (metadata == null)
        {
            throw new InvalidOperationException("Version metadata is missing.");
        }

        if (metadata.assetId <= 0)
        {
            throw new InvalidOperationException("Version metadata is missing a valid asset id.");
        }

        if (string.IsNullOrWhiteSpace(metadata.version) ||
            !Enum.IsDefined(typeof(Scope), metadata.scope) ||
            string.IsNullOrWhiteSpace(metadata.title) ||
            string.IsNullOrWhiteSpace(metadata.defaultAviVersion))
        {
            throw new InvalidOperationException("Required version metadata is missing (version, scope, title or default AVI version).");
        }
    }
}

/// <summary>
/// One version build or publish at a time in this editor process. The creator window, every MCB editor and MCP jobs
/// all go through it: they share the version folders, the avatars' FBX files and the upload session, while each
/// MCBEditor only knows its own isSubmitting. The holder releases it in a finally; a domain reload ends every running
/// operation and releases it as well.
/// </summary>
public static class VersionOperationGuard
{
    public sealed class Scope : IDisposable
    {
        internal Scope(string operation) { Operation = operation; }
        public string Operation { get; }
        public bool IsHeld => current == this;
        public void Dispose() { if (current == this) current = null; }
    }

    private static Scope current;

    public static bool IsBusy => current != null;

    public static string BusyMessage => current == null ? null
        : "Another MCB operation is " + current.Operation + ". Wait for it to finish, then try again.";

    public static Scope Acquire(string operation)
    {
        if (!TryAcquire(operation, out var scope, out string busy)) throw new InvalidOperationException(busy);
        return scope;
    }

    public static bool TryAcquire(string operation, out Scope scope, out string busy)
    {
        busy = BusyMessage;
        scope = busy == null ? (current = new Scope(operation)) : null;
        return scope != null;
    }

    [InitializeOnLoadMethod]
    private static void ReleaseOnDomainReload() => AssemblyReloadEvents.beforeAssemblyReload += () => current = null;
}
#endif
