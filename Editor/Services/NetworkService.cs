#if UNITY_EDITOR
using Orbiters.Toolkit.Editor.Net;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net.Http;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public enum NetworkRequestType
{
    UserInfo,
    AvatarDownload,
    AssetImageDownload,
    AssetDiscovery,
    VersionFetch,
    ModelDownload,
    Upload,
    ConnectionCheck
}

public class NetworkService
{
    public static int GetTimeoutSeconds(NetworkRequestType type)
    {
        switch (type)
        {
            case NetworkRequestType.UserInfo: return 2;
            case NetworkRequestType.AvatarDownload: return 10;
            case NetworkRequestType.AssetImageDownload: return 10;
            case NetworkRequestType.AssetDiscovery: return 8;
            case NetworkRequestType.VersionFetch: return 5;
            case NetworkRequestType.ModelDownload: return 180;
            case NetworkRequestType.Upload: return 300;
            case NetworkRequestType.ConnectionCheck: return 3;
            default: return 30;
        }
    }

    public async Task<VersionContentTrust.CreatorTrustSnapshot> FetchCreatorTrustAsync(string url, string authToken)
    {
        try
        {
            using (var request = UnityWebRequest.Get(url))
            {
                MCBRequestHeaders.SetAuthorization(request, authToken);
                request.timeout = GetTimeoutSeconds(NetworkRequestType.VersionFetch);
                request.redirectLimit = 0; // Trust must come from the authenticated API, never a file/CDN redirect.
                await MCBManagedRequest.SendUnityWebRequestAsync(request, url, MCBRequestPolicy.Backend("Verify version creator"));
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200) return null;
                return JsonConvert.DeserializeObject<VersionContentTrust.CreatorTrustSnapshot>(request.downloadHandler.text);
            }
        }
        catch (Exception)
        {
            // Unknown trust requires the same explicit code consent as an untrusted creator. No cached fallback.
            return null;
        }
    }

    public async Task<(bool success, CustomBaseVersionResponse response, string error)> FetchVersionsAsync(string url, string authToken)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            MCBRequestHeaders.SetAuthorization(req, authToken);
            MCBLogger.Log($"[NetworkService] FetchVersionsAsync GET {SanitizeUrlForLogs(url)}");
            req.timeout = GetTimeoutSeconds(NetworkRequestType.VersionFetch);
            await MCBManagedRequest.SendUnityWebRequestAsync(req, url, MCBRequestPolicy.Backend("Fetch versions"));

            // Special handling: access denied for asset (backend may return 203 or 204 with JSON { error, assetId })
            long code = req.responseCode;
            string body = null;
            try { body = req.downloadHandler?.text; } catch { /* ignore */ }

            if (code == 203 || code == 204)
            {
                MCBLogger.LogWarning($"[NetworkService] FetchVersionsAsync access denied: {BuildHttpContext(req, url, body)}");
                try
                {
                    // Try to parse a minimal object with assetId
                    var payload = JsonConvert.DeserializeObject<AccessDeniedPayload>(body ?? "{}");
                    if (payload != null && !string.IsNullOrEmpty(payload.assetId))
                    {
                        // Encode a recognizable error token so callers can react specifically
                        return (false, null, $"ACCESS_DENIED:{payload.assetId}");
                    }
                }
                catch { /* ignore parse error and fall through to generic handling */ }
                // If no assetId, return a generic message
                return (false, null, "You do not seem to own this MCB. Get it from the Orbiters website and try again.");
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                string httpContext = BuildHttpContext(req, url, body);
                MCBLogger.LogError($"[NetworkService] FetchVersionsAsync failed: {httpContext}");

                // Try to extract specific error message from JSON body
                if (!string.IsNullOrEmpty(body))
                {
                    try
                    {
                        var errorObj = JsonConvert.DeserializeObject<AccessDeniedPayload>(body);
                        if (!string.IsNullOrEmpty(errorObj.errorMessage)) return (false, null, $"{errorObj.errorMessage}\n{httpContext}");
                        if (!string.IsNullOrEmpty(errorObj.error)) return (false, null, $"{errorObj.error}\n{httpContext}");
                    }
                    catch { /* ignore JSON parse errors */ }
                }

                switch (req.responseCode)
                {
                    case 401:
                        return (false, null, $"Unauthorized while fetching versions.\n{httpContext}");
                    case 404:
                        return (false, null, $"Version discovery endpoint returned 404.\n{httpContext}");
                    case 500:
                        return (false, null, $"Server error while fetching versions.\n{httpContext}");
                }
                return (false, null, $"Version fetch request failed.\n{httpContext}");
            }

            try
            {
                var response = JsonConvert.DeserializeObject<CustomBaseVersionResponse>(body);
                return (true, response, null);
            }
            catch (Exception e)
            {
                string parseContext = BuildHttpContext(req, url, body);
                MCBLogger.LogError($"[NetworkService] FetchVersionsAsync parse failure: {e.Message}. {parseContext}");
                return (false, null, $"Failed to parse version response: {e.Message}\n{parseContext}");
            }
        }
    }

    // Minimal payload to read assetId from access denied responses
    private class AccessDeniedPayload { public string error; public string assetId; public string errorMessage; }

    private static string BuildHttpContext(UnityWebRequest req, string url, string body)
    {
        string bodySnippet = CreateBodySnippet(body);
        return $"HTTP {(long)req.responseCode} {req.error} | url={SanitizeUrlForLogs(url)}" +
               (string.IsNullOrEmpty(bodySnippet) ? string.Empty : $" | body={bodySnippet}");
    }

    public static string SanitizeUrlForLogs(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        return Regex.Replace(url, @"([?&]t=)([^&]+)", "$1<redacted>", RegexOptions.IgnoreCase);
    }

    private static string CreateBodySnippet(string body, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string compact = Regex.Replace(body, @"\s+", " ").Trim();
        if (compact.Length <= maxLength)
        {
            return compact;
        }

        return compact.Substring(0, maxLength) + "...";
    }

    public async Task<(bool success, string error)> DownloadFileAsync(
        string url,
        string destinationPath,
        Action<float> onProgress = null,
        Action<ulong> onDownloadedBytes = null,
        string authToken = null)
    {
        int timeoutSeconds = GetTimeoutSeconds(NetworkRequestType.ModelDownload);
        bool succeeded = false;
        MCBDownloadTempFiles.Track(destinationPath);
        try
        {
            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var request = await MCBManagedRequest.SendAuthorizedAsync(
                       target => MCBDownloadTempFiles.Attach(destinationPath, new UnityWebRequest(target, UnityWebRequest.kHttpVerbGET)
                       {
                           downloadHandler = new DownloadHandlerFile(destinationPath),
                           timeout = timeoutSeconds
                       }),
                       url, authToken, MCBRequestPolicy.Backend("Download file"),
                       running =>
                       {
                           onProgress?.Invoke(NormalizeDownloadProgress(running.downloadProgress));
                           onDownloadedBytes?.Invoke(running.downloadedBytes);
                       }))
            {
                onProgress?.Invoke(1f);
                onDownloadedBytes?.Invoke(request.downloadedBytes);

                if (request.result == UnityWebRequest.Result.Success)
                {
                    succeeded = true;
                    return (true, null);
                }

                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }

                string errorBody = null;
                try { errorBody = request.downloadHandler?.text; } catch { }

                string errorMsg = $"Download failed: HTTP {request.responseCode} {request.error}";
                if (!string.IsNullOrEmpty(errorBody))
                {
                    try
                    {
                        var errorObj = JsonConvert.DeserializeObject<AccessDeniedPayload>(errorBody);
                        if (!string.IsNullOrEmpty(errorObj.errorMessage)) errorMsg = errorObj.errorMessage;
                        else if (!string.IsNullOrEmpty(errorObj.error)) errorMsg = errorObj.error;
                    }
                    catch { /* ignore JSON parse error */ }
                }

                MCBLogger.LogError($"[NetworkService] {errorMsg}, url = {SanitizeUrlForLogs(url)}");
                return (false, errorMsg);
            }
        }
        catch (Exception ex)
        {
             MCBManagedRequest.ReportException(url, ex, MCBRequestPolicy.Backend("Download file"));
             MCBLogger.LogError($"[NetworkService] Download exception: {ex.Message}, url = {SanitizeUrlForLogs(url)}");
             return (false, $"Download exception: {ex.Message}");
        }
        finally
        {
            MCBDownloadTempFiles.Finish(destinationPath, succeeded);
        }
    }

    public async Task<(bool success, long contentLength, string error)> GetDownloadContentLengthAsync(string url, string authToken = null)
    {
        int timeoutSeconds = GetTimeoutSeconds(NetworkRequestType.ModelDownload);
        try
        {
            using (var request = await MCBManagedRequest.SendAuthorizedAsync(
                       target => new UnityWebRequest(target, "HEAD") { timeout = timeoutSeconds },
                       url, authToken, MCBRequestPolicy.Backend("Download metadata")))
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string errorMsg = $"Download metadata failed: HTTP {request.responseCode} {request.error}";
                    MCBLogger.LogWarning($"[NetworkService] {errorMsg}, url = {SanitizeUrlForLogs(url)}");
                    return (false, -1L, errorMsg);
                }

                string header = request.GetResponseHeader("Content-Length");
                if (!long.TryParse(header, out long length) || length < 0L)
                {
                    return (false, -1L, "Download metadata did not include a valid Content-Length header.");
                }

                return (true, length, null);
            }
        }
        catch (Exception ex)
        {
            MCBManagedRequest.ReportException(url, ex, MCBRequestPolicy.Backend("Download metadata"));
            MCBLogger.LogWarning($"[NetworkService] Download metadata exception: {ex.Message}, url = {SanitizeUrlForLogs(url)}");
            return (false, -1L, $"Download metadata exception: {ex.Message}");
        }
    }

    public async Task<(bool success, byte[] data, string error)> DownloadBytesAsync(
        string url,
        Action<float> onProgress = null,
        Action<ulong> onDownloadedBytes = null,
        string authToken = null)
    {
        int timeoutSeconds = GetTimeoutSeconds(NetworkRequestType.ModelDownload);
        try
        {
            using (var request = await MCBManagedRequest.SendAuthorizedAsync(
                       target => new UnityWebRequest(target, UnityWebRequest.kHttpVerbGET)
                       {
                           downloadHandler = new DownloadHandlerBuffer(),
                           timeout = timeoutSeconds
                       },
                       url, authToken, MCBRequestPolicy.Backend("Download bytes"),
                       running =>
                       {
                           onProgress?.Invoke(NormalizeDownloadProgress(running.downloadProgress));
                           onDownloadedBytes?.Invoke(running.downloadedBytes);
                       }))
            {
                onProgress?.Invoke(1f);
                onDownloadedBytes?.Invoke(request.downloadedBytes);

                if (request.result == UnityWebRequest.Result.Success)
                {
                    return (true, request.downloadHandler.data, null);
                }

                string errorBody = null;
                try { errorBody = request.downloadHandler?.text; } catch { }

                string errorMsg = $"Download failed: HTTP {request.responseCode} {request.error}";
                if (!string.IsNullOrEmpty(errorBody))
                {
                    try
                    {
                        var errorObj = JsonConvert.DeserializeObject<AccessDeniedPayload>(errorBody);
                        if (!string.IsNullOrEmpty(errorObj.errorMessage)) errorMsg = errorObj.errorMessage;
                        else if (!string.IsNullOrEmpty(errorObj.error)) errorMsg = errorObj.error;
                    }
                    catch { /* ignore JSON parse error */ }
                }

                MCBLogger.LogError($"[NetworkService] {errorMsg}, url = {SanitizeUrlForLogs(url)}");
                return (false, null, errorMsg);
            }
        }
        catch (Exception ex)
        {
            MCBManagedRequest.ReportException(url, ex, MCBRequestPolicy.Backend("Download bytes"));
            MCBLogger.LogError($"[NetworkService] Download bytes exception: {ex.Message}, url = {SanitizeUrlForLogs(url)}");
            return (false, null, $"Download exception: {ex.Message}");
        }
    }

    private static float NormalizeDownloadProgress(float progress)
    {
        if (float.IsNaN(progress) || progress < 0f)
        {
            return 0f;
        }

        return Mathf.Clamp01(progress);
    }
    
    // --- Creator Mode Upload: streaming multipart with progress, cancel and stall detection ---

    /// <summary>Seconds without any uploaded-bytes movement before a large upload is
    /// considered stalled and aborted (replaces the old fixed 300 s total timeout that
    /// killed big uploads on slow links and let dead connections hang).</summary>
    private const double UploadStallTimeoutSeconds = 120d;

    /// <summary>
    /// Uploads a version package as multipart/form-data ("metadata" JSON field +
    /// "packageFile" zip part — wire-compatible with the previous implementation), but
    /// streams the body from disk via UploadHandlerFile instead of loading the whole
    /// zip into memory. The complete multipart body is pre-written to a temp file.
    /// </summary>
    public async Task<(bool success, string serverResponse, string error, bool cancelled)> SubmitNewVersionStreamingAsync(
        string url,
        string authToken,
        string zipFilePath,
        string metadataJson,
        Action<float, ulong> onProgress = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        string boundary = "----MCBUpload" + Guid.NewGuid().ToString("N");
        string bodyPath = Path.Combine(Path.GetTempPath(), $"mcb_upload_body_{Guid.NewGuid():N}.tmp");

        try
        {
            // Same wire format as before ("metadata" JSON + "packageFile" zip), written by Orbiters Toolkit's transfer service.
            OrbitersTransfer.WriteMultipart(bodyPath, boundary, new[]
            {
                OrbitersTransfer.Part.Field("metadata", metadataJson),
                OrbitersTransfer.Part.File("packageFile", zipFilePath, Path.GetFileName(zipFilePath), "application/zip"),
            });

            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerFile(bodyPath);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", $"multipart/form-data; boundary={boundary}");
                MCBRequestHeaders.SetAuthorization(req, authToken);
                req.timeout = 0; // stall detection below replaces the fixed timeout

                UnityWebRequestAsyncOperation operation;
                try
                {
                    operation = req.SendWebRequest();
                }
                catch (Exception ex)
                {
                    MCBManagedRequest.ReportException(url, ex, MCBRequestPolicy.Backend("Upload version"));
                    return (false, null, $"Upload failed to start: {ex.Message}", false);
                }

                var end = await OrbitersTransfer.WaitAsync(req, operation, running => onProgress?.Invoke(Mathf.Clamp01(running.uploadProgress), running.uploadedBytes),
                    cancellationToken, UploadStallTimeoutSeconds, upload: true);

                onProgress?.Invoke(1f, req.uploadedBytes);
                MCBConnectivityMonitor.ReportManagedUnityWebRequest(req, url, MCBRequestPolicy.Backend("Upload version"));

                if (end == OrbitersTransfer.End.Cancelled)
                {
                    return (false, null, "The upload was cancelled.", true);
                }

                if (end == OrbitersTransfer.End.Stalled)
                {
                    return (false, null, $"The upload timed out: no data was sent for {UploadStallTimeoutSeconds:0} seconds.", false);
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string body = null;
                    try { body = req.downloadHandler?.text; } catch { }
                    string serverMessage = CreateBodySnippet(body, 500);
                    string error = $"Upload failed: [{req.responseCode}] {req.error}";
                    if (!string.IsNullOrWhiteSpace(serverMessage))
                    {
                        error += $" | {serverMessage}";
                    }
                    return (false, body, error, false);
                }

                return (true, req.downloadHandler.text, null, false);
            }
        }
        finally
        {
            try { if (File.Exists(bodyPath)) File.Delete(bodyPath); } catch { }
        }
    }

    public async Task<(bool success, CustomBaseVersion version, string error)> UpdateCreatorVersionMetadataAsync(string url, string authToken, string title, string changelog)
    {
        var payload = new CreatorVersionMetadataUpdateRequest
        {
            title = title,
            changelog = changelog ?? string.Empty
        };
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));

        using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT))
        {
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            MCBRequestHeaders.SetAuthorization(req, authToken);
            req.timeout = GetTimeoutSeconds(NetworkRequestType.Upload);

            await MCBManagedRequest.SendUnityWebRequestAsync(req, url, MCBRequestPolicy.Backend("Update version metadata"));

            string body = null;
            try { body = req.downloadHandler?.text; } catch { }

            if (req.result != UnityWebRequest.Result.Success)
            {
                string serverMessage = CreateBodySnippet(body, 500);
                string error = $"Update failed: [{req.responseCode}] {req.error}";
                if (!string.IsNullOrWhiteSpace(serverMessage))
                {
                    error += $" | {serverMessage}";
                }
                return (false, null, error);
            }

            try
            {
                var response = JsonConvert.DeserializeObject<CreatorVersionMetadataUpdateResponse>(body);
                if (response?.version == null)
                {
                    return (false, null, "The server did not return the updated version.");
                }

                return (true, response.version, null);
            }
            catch (Exception ex)
            {
                return (false, null, $"Failed to parse update response: {ex.Message}");
            }
        }
    }

    private class CreatorVersionMetadataUpdateRequest
    {
        public string title;
        public string changelog;
    }

    private class CreatorVersionMetadataUpdateResponse
    {
        public CustomBaseVersion version;
    }

    public class CheckConnectionResponse
    {
        public string state;
        public string latestVersion;
        public string updateMessage;
        public string supportStatus;
    }

    public async Task<CheckConnectionResponse> CheckConnectionDetailedAsync(string url, string authToken = null)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            MCBRequestHeaders.SetAuthorization(req, authToken);

            try
            {
                req.timeout = GetTimeoutSeconds(NetworkRequestType.ConnectionCheck);
                await MCBManagedRequest.SendUnityWebRequestAsync(req, url, MCBRequestPolicy.Backend("Check connection"));
                if (req.result != UnityWebRequest.Result.Success)
                {
                    long code = req.responseCode;
                    string body = null;
                    try { body = req.downloadHandler?.text; } catch { /* ignore */ }
                    MCBLogger.LogWarning($"[MCB] Connection check failed: [{code}] [url: {SanitizeUrlForLogs(url)}] {req.error} {(string.IsNullOrEmpty(body) ? string.Empty : "- " + body)}");
                    return new CheckConnectionResponse { state = "disconnected" };
                }

                var text = req.downloadHandler.text;
                CheckConnectionResponse resp = null;
                try { resp = JsonConvert.DeserializeObject<CheckConnectionResponse>(text); }
                catch (Exception jex)
                {
                    MCBLogger.LogWarning($"[MCB] Invalid connection check response JSON: {jex.Message}");
                    return new CheckConnectionResponse { state = "disconnected" };
                }
                if (resp == null || string.IsNullOrEmpty(resp.state))
                {
                    MCBLogger.LogWarning("[MCB] Connection check response missing 'state'.");
                    return new CheckConnectionResponse { state = "disconnected" };
                }
                if (resp.state == "connected" || resp.state == "limited" || resp.state == "disconnected") return resp;
                MCBLogger.LogWarning($"[MCB] Connection check returned unexpected state '{resp.state}'. Treating as disconnected.");
                return new CheckConnectionResponse { state = "disconnected" };
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[MCB] Connection check error: {ex.Message}");
                return new CheckConnectionResponse { state = "disconnected" };
            }
        }
    }

    public async Task<string> CheckConnectionAsync(string url, string authToken = null)
    {
        var response = await CheckConnectionDetailedAsync(url, authToken);
        return response != null && !string.IsNullOrEmpty(response.state) ? response.state : "disconnected";
    }

}

/// <summary>
/// Version downloads staged in the system temp folder (mcb_dl_*.zip, mcb-mesh-delivery-*, ...). A domain reload ends a
/// running download, or the work that would have used and deleted its file, without any cleanup: the version's data (plain
/// meshes unencrypted) would stay in %TEMP%. The files of this editor's downloads are deleted before the reload (or right
/// after it, while the aborted request still held them), and leftovers of crashed editors are swept on load once a day old.
/// </summary>
[InitializeOnLoad]
public static class MCBDownloadTempFiles
{
    private const string PendingSessionKey = "MCB.DownloadTempFiles.Pending";
    private static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);
    private static readonly (string prefix, string extension)[] StaleFiles =
        { ("mcb_dl_", ".zip"), ("mcb_upload_", ".zip"), ("mcb-rekey-", ".fbx"), ("mcb-logic-", ".unitypackage") };
    private static readonly string[] StagingFolderPrefixes = { "mcb-mesh-delivery-", "mcb-source-support-" };
    // Tracked files and the request writing each (null once finished): another editor's downloads are never touched.
    private static readonly System.Collections.Generic.Dictionary<string, UnityWebRequest> Tracked =
        new System.Collections.Generic.Dictionary<string, UnityWebRequest>(StringComparer.OrdinalIgnoreCase);

    static MCBDownloadTempFiles()
    {
        AssemblyReloadEvents.beforeAssemblyReload += DeleteTracked;
        EditorApplication.delayCall += Sweep;
    }

    /// <summary>Marks <paramref name="path"/> as a file of a download (or download work) running in this editor.</summary>
    public static void Track(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Tracked) Tracked[Path.GetFullPath(path)] = null;
    }

    internal static UnityWebRequest Attach(string path, UnityWebRequest request)
    {
        lock (Tracked) Tracked[Path.GetFullPath(path)] = request;
        return request;
    }

    /// <summary>
    /// The download of <paramref name="path"/> ended. A file in the temp folder stays tracked until a reload, unless its
    /// caller removed it: the reload would also end the work that reads and deletes it.
    /// </summary>
    public static void Finish(string path, bool succeeded)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath = Path.GetFullPath(path);
        lock (Tracked)
        {
            if (succeeded && IsInTemp(fullPath)) Tracked[fullPath] = null;
            else Tracked.Remove(fullPath);
            foreach (string gone in Tracked.Where(entry => entry.Value == null && !File.Exists(entry.Key)).Select(entry => entry.Key).ToList())
                Tracked.Remove(gone);
        }
    }

    private static void DeleteTracked()
    {
        System.Collections.Generic.KeyValuePair<string, UnityWebRequest>[] entries;
        lock (Tracked)
        {
            entries = Tracked.ToArray();
            Tracked.Clear();
        }

        var left = new System.Collections.Generic.List<string>();
        foreach (var entry in entries)
        {
            // Aborting releases the file the native download handler still holds open.
            try
            {
                entry.Value?.Abort();
                entry.Value?.Dispose();
            }
            catch (Exception) { }
            if (!TryDelete(entry.Key)) left.Add(entry.Key);
        }
        SessionState.SetString(PendingSessionKey, string.Join("\n", left));
    }

    private static void Sweep()
    {
        string pending = SessionState.GetString(PendingSessionKey, string.Empty);
        SessionState.EraseString(PendingSessionKey);
        foreach (string path in pending.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) TryDelete(path);

        try
        {
            string temp = Path.GetTempPath();
            DateTime staleBefore = DateTime.UtcNow - StaleAge;
            // One listing of the temp folder, which can be large: every name MCB stages there starts with "mcb".
            foreach (string entry in Directory.EnumerateFileSystemEntries(temp, "mcb*", SearchOption.TopDirectoryOnly).ToList())
            {
                string name = Path.GetFileName(entry);
                bool ours = File.Exists(entry)
                    ? StaleFiles.Any(file => name.StartsWith(file.prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(file.extension, StringComparison.OrdinalIgnoreCase))
                    : StagingFolderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (ours && File.GetLastWriteTimeUtc(entry) < staleBefore) TryDelete(entry);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            MCBLogger.LogWarning($"[NetworkService] Could not sweep stale download files: {ex.Message}");
        }
    }

    // The file or folder, and the download's staging folder in the temp folder when it is in one.
    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
            string staging = StagingFolderOf(path);
            if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string StagingFolderOf(string path)
    {
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (string folder = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (!string.Equals(Path.GetDirectoryName(folder), temp, StringComparison.OrdinalIgnoreCase)) continue;
            string name = Path.GetFileName(folder);
            return StagingFolderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ? folder : null;
        }
        return null;
    }

    private static bool IsInTemp(string fullPath) =>
        fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);
}

public static class EditorAsyncExtensions
{
    public static TaskAwaiter GetAwaiter(this AsyncOperation asyncOp)
    {
        var tcs = new TaskCompletionSource<object>();
        asyncOp.completed += obj => { tcs.SetResult(null); };
        return ((Task)tcs.Task).GetAwaiter();
    }
}
#endif
