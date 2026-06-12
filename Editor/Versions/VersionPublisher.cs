#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
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
/// repackages from live project state — if the artifact is not intact, the user is
/// told to rebuild explicitly.
/// </summary>
public static class VersionPublisher
{
    public const long MaxVersionPackageUploadBytes = 600L * 1024L * 1024L;

    public static IEnumerator PublishCoroutine(
        MCBEditor editor,
        NetworkService networkService,
        FileManagerService fileManagerService,
        CustomBaseVersion version,
        Action onPublished = null)
    {
        if (editor.isSubmitting)
        {
            EditorUtility.DisplayDialog("Publish", "Another build or publish operation is already running. Wait for it to finish and try again.", "OK");
            yield break;
        }

        editor.isSubmitting = true;
        editor.submitError = "";
        editor.warningsModule.Clear();
        editor.Repaint();

        string zipPath = null;
        Exception setupError = null;
        Task<(bool success, string serverResponse, string error, bool cancelled)> uploadTask = null;
        var cancellation = new CancellationTokenSource();
        IDisposable operationGuard = null;
        VersionArtifact artifact = null;
        string uploadUrl = null;
        float uploadProgress = 0f;
        ulong uploadedBytes = 0;
        long packageBytes = 0;
        bool userCancelledBeforeUpload = false;

        try
        {
            artifact = VersionRepository.GetArtifact(version);
            var validation = VersionRepository.Validate(artifact);

            if (!validation.IsPublishable)
            {
                string reason = validation.Describe();
                string message = $"Version {version.version} cannot be published as built: {reason}\n\n" +
                                 "Select the version in the creator form and click 'Build Version' to rebuild it, then publish again. " +
                                 "Publishing never silently repackages from your current project files.";
                editor.submitError = OperationErrorReporter.Report(editor, new OperationError
                {
                    Category = OperationErrorCategory.Integrity,
                    UserMessage = message,
                    Detail = reason
                }, "Publish blocked");
                EditorUtility.DisplayDialog("Publish blocked", message, "OK");
                yield break;
            }

            if (validation.state == ArtifactState.SourceDrift)
            {
                bool publishAsBuilt = EditorUtility.DisplayDialog(
                    "Source files changed since build",
                    $"{validation.Describe()}\n\nThe stored build is still intact and will be uploaded exactly as built. " +
                    "If you want the latest source changes included, cancel and click 'Build Version' again first.",
                    "Publish as built",
                    "Cancel");
                if (!publishAsBuilt)
                {
                    userCancelledBeforeUpload = true;
                    yield break;
                }
            }

            var metadata = artifact.Metadata;
            var selectedAsset = editor.GetSelectedAsset();
            if (metadata.assetId <= 0 && selectedAsset != null)
            {
                metadata.assetId = selectedAsset.id;
            }

            ValidateVersionMetadataForUpload(metadata);

            operationGuard = VersionRepository.BeginOperation(metadata);

            EditorUtility.DisplayProgressBar("Preparing Upload", "Packaging built version files...", 0.05f);
            zipPath = fileManagerService.CreateZipFromManifestOutputs(artifact);

            packageBytes = new FileInfo(zipPath).Length;
            if (packageBytes > MaxVersionPackageUploadBytes)
            {
                throw new InvalidOperationException(
                    $"Version package is too large to upload ({DiskUtils.FormatBytes(packageBytes)}). Maximum allowed size is {DiskUtils.FormatBytes(MaxVersionPackageUploadBytes)}.");
            }

            MCBLogger.Log($"[VersionPublisher] Version package created: {DiskUtils.FormatBytes(packageBytes)} at {zipPath} ({artifact.Manifest.outputs.Count} manifest outputs).");

            string metadataJson = JsonConvert.SerializeObject(metadata, new StringEnumConverter());
            MCBLogger.Log($"[VersionPublisher] Uploading version metadata assetId={metadata.assetId}, version={metadata.version}, scope={metadata.scope}, defaultAviVersion={metadata.defaultAviVersion}, changelogLength={(metadata.changelog ?? string.Empty).Length}");
            uploadUrl = $"{MCBUtils.getApiUrl()}{MCBUtils.NEW_VERSION_ENDPOINT}?t={editor.authToken}";
            uploadTask = networkService.SubmitNewVersionStreamingAsync(
                uploadUrl,
                editor.authToken,
                zipPath,
                metadataJson,
                (progress, bytes) => { uploadProgress = progress; uploadedBytes = bytes; },
                cancellation.Token);
        }
        catch (Exception ex)
        {
            setupError = ex;
        }
        finally
        {
            if (uploadTask == null)
            {
                // Setup failed or was blocked before the upload started: clean up here,
                // because the shared cleanup below only runs after the upload loop.
                if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath)) File.Delete(zipPath);
                operationGuard?.Dispose();
                cancellation.Dispose();
                EditorUtility.ClearProgressBar();
                editor.isSubmitting = false;
                editor.Repaint();
            }
        }

        if (uploadTask == null)
        {
            if (setupError != null)
            {
                editor.submitError = OperationErrorReporter.Report(
                    editor,
                    OperationErrorClassifier.Classify(setupError, "Publishing the version"),
                    "Upload Failed");
                editor.RefreshUiToolkitSections();
            }
            else if (!userCancelledBeforeUpload)
            {
                editor.RefreshUiToolkitSections();
            }

            yield break;
        }

        // Upload loop with progress + cancel (outside try/catch: coroutine constraint).
        while (!uploadTask.IsCompleted)
        {
            string stepText = uploadProgress > 0.001f
                ? $"Sending package... {UnityEngine.Mathf.RoundToInt(uploadProgress * 100f)}% of {DiskUtils.FormatBytes(packageBytes)}"
                : $"Sending package to server ({DiskUtils.FormatBytes(packageBytes)})...";
            if (EditorUtility.DisplayCancelableProgressBar("Uploading", stepText, 0.1f + 0.85f * uploadProgress))
            {
                cancellation.Cancel();
            }

            yield return null;
        }

        bool uploadSucceeded = false;
        try
        {
            var (success, response, uploadError, cancelled) = uploadTask.Result;
            if (cancelled)
            {
                editor.submitError = "The upload was cancelled. The version is still available as an unsubmitted build.";
                MCBLogger.Log($"[VersionPublisher] Upload cancelled by user ({uploadedBytes} bytes sent), url: {NetworkService.SanitizeUrlForLogs(uploadUrl)}");
            }
            else if (!success)
            {
                throw new Exception(uploadError);
            }
            else
            {
                uploadSucceeded = true;
                VersionRepository.MarkPublished(artifact);
                editor.LoadUnsubmittedVersions(true);
                editor.LoadImportedVersions(true);
                onPublished?.Invoke();
                new VersionActions(editor, networkService, fileManagerService).StartVersionFetch();
            }
        }
        catch (Exception ex)
        {
            editor.submitError = OperationErrorReporter.Report(
                editor,
                OperationErrorClassifier.Classify(ex, "Publishing the version"),
                "Upload Failed");
            MCBLogger.LogError($"[VersionPublisher] Upload failed: {ex}, url: {NetworkService.SanitizeUrlForLogs(uploadUrl)}");
        }
        finally
        {
            cancellation.Dispose();
            EditorUtility.ClearProgressBar();
            if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath)) File.Delete(zipPath);
            operationGuard?.Dispose();
            editor.isSubmitting = false;
            editor.Repaint();
        }

        if (uploadSucceeded)
        {
            // Post-publish steps. Failures here are reported but never fail the publish.
            bool checkpointCreated = UnitGitReleasePublisher.TryPublishReleaseCheckpoint(
                version,
                editor.GetSelectedAsset()?.name,
                out string checkpointMessage);

            string checkpointInfo = checkpointCreated
                ? "\n\nA Unit Git release checkpoint commit was created."
                : (string.IsNullOrWhiteSpace(checkpointMessage)
                    ? string.Empty
                    : $"\n\nNo Unit Git release checkpoint was created: {checkpointMessage}");
            EditorUtility.DisplayDialog("Publish Successful", $"Custom base version {version.version} has been uploaded.{checkpointInfo}", "OK");
        }

        editor.RefreshUiToolkitSections();
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
