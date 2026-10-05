#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEditor;

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
        FileManagerService fileManagerService, CustomBaseVersion version, Action onPublished = null, bool interactive = true)
    {
        if (editor.isSubmitting) yield break;
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
        bool checkpoint = UnitGitReleasePublisher.TryPublishReleaseCheckpoint(version, editor.GetSelectedAsset()?.name, out string checkpointMessage);
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

    internal static void ValidateResume(CustomBaseVersion local, CustomBaseVersion remote)
    {
        bool Same(object a, object b) => Newtonsoft.Json.Linq.JToken.DeepEquals(
            Newtonsoft.Json.Linq.JToken.FromObject(a ?? new object()), Newtonsoft.Json.Linq.JToken.FromObject(b ?? new object()));
        if (remote.defaultAviVersion != local.defaultAviVersion || remote.title != local.title || remote.scope != local.scope ||
            remote.changelog != local.changelog || !Same(remote.extraCustomization, local.extraCustomization) ||
            !Same(remote.customBlendshapes, local.customBlendshapes) || !Same(remote.dependencies, local.dependencies))
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
#endif
