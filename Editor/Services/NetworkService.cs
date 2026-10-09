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

            long code = req.responseCode;
            string body = null;
            try { body = req.downloadHandler?.text; } catch { /* ignore */ }

            // A member who does not own the asset gets 403 {"code": "ACCESS_DENIED", "assetId"}: callers show the store link.
            // Other 403s (a Discord role, ...) keep their server message below.
            if (code == 403 && TryReadAccessDenied(body, out string deniedAssetId))
            {
                MCBLogger.LogWarning($"[NetworkService] FetchVersionsAsync access denied: {BuildHttpContext(req, url, body)}");
                return (false, null, AccessDeniedPrefix + deniedAssetId);
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

    /// <summary>Error of a version fetch the member has no access to, followed by the asset id.</summary>
    public const string AccessDeniedPrefix = "ACCESS_DENIED:";

    // Server error answers: {"error"} or {"errorMessage"}; access denials add "code" and "assetId".
    private class AccessDeniedPayload { public string error; public string assetId; public string errorMessage; public string code; }

    internal static bool TryReadAccessDenied(string body, out string assetId)
    {
        assetId = null;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            var payload = JsonConvert.DeserializeObject<AccessDeniedPayload>(body);
            if (payload?.code != "ACCESS_DENIED" || string.IsNullOrWhiteSpace(payload.assetId)) return false;
            assetId = payload.assetId.Trim();
            return true;
        }
        catch (Exception) { return false; }
    }

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

    // --- Version downloads: no total time limit. An attempt ends when no byte arrives for a while; the next one resumes
    // with HTTP Range at the bytes already on disk (signed storage honours it), a bounded number of times. ---

    /// <summary>Seconds without a received byte before a download attempt is abandoned and retried.</summary>
    internal const double DownloadStallSeconds = 60d;
    /// <summary>Attempts per download: the first one and its retries.</summary>
    internal const int DownloadAttempts = 4;
    // The backend signs storage URLs for 300 s: a retry resumes there without asking (and counting at) the backend again.
    private const double SignedUrlReuseSeconds = 240d;
    private const int ErrorBodyLimit = 64 * 1024;

    /// <summary>How one attempt ended: the request and the signed storage hop it may redirect to.</summary>
    internal sealed class TransferAttempt
    {
        public long StatusCode, ContentLength = -1, Total = -1;
        public bool Answered, Redirected, Completed, Stalled, TooLarge, RangeIgnored, RangeMismatch, Encoded;
        public string Error, FinalUrl, Body;
        public byte[] Data;
    }

    public async Task<(bool success, string error)> DownloadFileAsync(
        string url,
        string destinationPath,
        Action<float> onProgress = null,
        Action<ulong> onDownloadedBytes = null,
        string authToken = null)
    {
        bool succeeded = false;
        MCBDownloadTempFiles.Track(destinationPath);
        try
        {
            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (File.Exists(destinationPath)) File.Delete(destinationPath);

            string signedUrl = null, error = null;
            double signedAt = 0;
            long total = -1;
            bool answered = false, resumable = true;
            for (int attempt = 1; ; attempt++)
            {
                if (!resumable) SetFileLength(destinationPath, 0);
                long offset = FileLength(destinationPath);
                bool direct = signedUrl != null && EditorApplication.timeSinceStartup - signedAt < SignedUrlReuseSeconds;
                var result = await SendTransferAsync(direct ? signedUrl : url, direct ? null : authToken, target =>
                {
                    // Every hop writes at the offset: the body of a redirect answered before it is dropped.
                    SetFileLength(destinationPath, offset);
                    return MCBDownloadTempFiles.Attach(destinationPath, new UnityWebRequest(target, UnityWebRequest.kHttpVerbGET)
                    {
                        downloadHandler = new DownloadHandlerFile(destinationPath, offset > 0) { removeFileOnAbort = false }
                    });
                }, offset, 0, running =>
                {
                    long received = offset + (long)running.downloadedBytes, length = ContentLength(running);
                    long expected = length >= 0 ? offset + length : total;
                    onProgress?.Invoke(expected > 0 ? Mathf.Clamp01((float)((double)received / expected)) : NormalizeDownloadProgress(running.downloadProgress));
                    onDownloadedBytes?.Invoke((ulong)received);
                });
                answered |= result.Answered;
                if (result.Redirected)
                {
                    signedUrl = result.FinalUrl;
                    signedAt = EditorApplication.timeSinceStartup;
                }
                if (result.Total > 0) total = result.Total;
                long code = result.StatusCode;
                bool storage = direct || result.Redirected;
                if (result.RangeIgnored || result.RangeMismatch || code == 416 || (offset > 0 && code == 200))
                {
                    if (code == 416 && total > 0 && offset == total) { succeeded = true; break; }
                    // This server cannot resume: start over, at once since nothing was kept.
                    resumable = false;
                    error = "The server could not resume the download.";
                    if (offset > 0) { attempt--; continue; }
                }
                else if (result.Completed && (code == 200 || code == 206))
                {
                    long received = FileLength(destinationPath);
                    if (total <= 0 || result.Encoded || received == total) { succeeded = true; break; }
                    error = $"The download ended early ({received} of {total} bytes).";
                }
                else if (code >= 300)
                {
                    // The error answer was written after the received bytes: keep its message, then drop it.
                    string body = ReadErrorBody(destinationPath, offset);
                    SetFileLength(destinationPath, offset);
                    error = ServerError(code, result.Error, body);
                    if (storage && code < 500) signedUrl = null; // an expired signature: ask the backend for a new one
                    if (!Retryable(code, storage)) break;
                }
                else
                {
                    error = TransferFailure(result);
                }

                if (attempt >= DownloadAttempts) break;
                MCBLogger.LogWarning($"[NetworkService] Download attempt {attempt} failed: {error} Resuming at {FileLength(destinationPath)} bytes, url = {SanitizeUrlForLogs(url)}");
                await Task.Delay(RetryDelay(attempt));
            }

            if (succeeded)
            {
                onProgress?.Invoke(1f);
                onDownloadedBytes?.Invoke((ulong)FileLength(destinationPath));
                return (true, null);
            }

            if (!answered) ReportUnreachable(url, "Download file", error);
            MCBLogger.LogError($"[NetworkService] {error} url = {SanitizeUrlForLogs(url)}");
            return (false, error);
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[NetworkService] Download exception: {ex.Message}, url = {SanitizeUrlForLogs(url)}");
            return (false, $"Download exception: {ex.Message}");
        }
        finally
        {
            if (!succeeded)
            {
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            // A file that could not be deleted stays tracked: it goes before the next reload.
            MCBDownloadTempFiles.Finish(destinationPath, succeeded || File.Exists(destinationPath));
        }
    }

    /// <param name="maxBytes">Larger answers are refused while they download (0: no limit).</param>
    public async Task<(bool success, byte[] data, string error)> DownloadBytesAsync(
        string url,
        Action<float> onProgress = null,
        Action<ulong> onDownloadedBytes = null,
        string authToken = null,
        long maxBytes = 0)
    {
        try
        {
            string error = null;
            bool answered = false;
            for (int attempt = 1; ; attempt++)
            {
                var result = await SendTransferAsync(url, authToken, target => new UnityWebRequest(target, UnityWebRequest.kHttpVerbGET)
                {
                    downloadHandler = new DownloadHandlerBuffer()
                }, 0, maxBytes, running =>
                {
                    onProgress?.Invoke(NormalizeDownloadProgress(running.downloadProgress));
                    onDownloadedBytes?.Invoke(running.downloadedBytes);
                });
                answered |= result.Answered;
                if (result.Completed && (maxBytes <= 0 || (result.Data?.Length ?? 0) <= maxBytes))
                {
                    byte[] data = result.Data ?? Array.Empty<byte>();
                    onProgress?.Invoke(1f);
                    onDownloadedBytes?.Invoke((ulong)data.Length);
                    return (true, data, null);
                }

                bool retry = true;
                if (result.TooLarge || result.Completed)
                {
                    error = $"The server's answer is larger than the {maxBytes / 1024} KB expected.";
                    retry = false;
                }
                else if (result.StatusCode >= 300)
                {
                    error = ServerError(result.StatusCode, result.Error, result.Body);
                    retry = Retryable(result.StatusCode, result.Redirected);
                }
                else
                {
                    error = TransferFailure(result);
                }

                if (!retry || attempt >= DownloadAttempts)
                {
                    if (!answered) ReportUnreachable(url, "Download bytes", error);
                    MCBLogger.LogError($"[NetworkService] {error} url = {SanitizeUrlForLogs(url)}");
                    return (false, null, error);
                }
                MCBLogger.LogWarning($"[NetworkService] Download attempt {attempt} failed: {error} Retrying, url = {SanitizeUrlForLogs(url)}");
                await Task.Delay(RetryDelay(attempt));
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[NetworkService] Download bytes exception: {ex.Message}, url = {SanitizeUrlForLogs(url)}");
            return (false, null, $"Download exception: {ex.Message}");
        }
    }

    /// <summary>
    /// One attempt with stall detection instead of a whole-request time limit: the request (authorized redirects to signed
    /// storage are followed without the token) is aborted once no byte arrived for <see cref="DownloadStallSeconds"/>, when
    /// a resumed (<paramref name="offset"/>) answer restarts the whole file, or when it grows past <paramref name="maxBytes"/>.
    /// </summary>
    private static async Task<TransferAttempt> SendTransferAsync(string url, string authToken, Func<string, UnityWebRequest> create,
        long offset, long maxBytes, Action<UnityWebRequest> progress)
    {
        var attempt = new TransferAttempt();
        UnityWebRequest watched = null;
        ulong seen = 0;
        double movedAt = 0;
        bool aborted = false;
        int hops = 0;
        using (var request = await MCBManagedRequest.SendAuthorizedAsync(target =>
               {
                   hops++;
                   attempt.FinalUrl = target;
                   var hop = create(target);
                   hop.timeout = 0;
                   if (offset > 0) hop.SetRequestHeader("Range", "bytes=" + offset + "-");
                   return hop;
               }, url, authToken, MCBRequestPolicy.Transfer("Download"), running =>
               {
                   double now = EditorApplication.timeSinceStartup;
                   if (running != watched || running.downloadedBytes != seen)
                   {
                       watched = running;
                       seen = running.downloadedBytes;
                       movedAt = now;
                   }
                   if (aborted) return;
                   long code = running.responseCode;
                   bool content = code == 200 || code == 206;
                   if (content && offset > 0 && code == 200) attempt.RangeIgnored = aborted = true;
                   else if (content && maxBytes > 0 && ((long)seen > maxBytes || ContentLength(running) > maxBytes)) attempt.TooLarge = aborted = true;
                   else if (IsStalled(movedAt, now)) attempt.Stalled = aborted = true;
                   if (aborted) running.Abort();
                   else if (content) progress?.Invoke(running);
               }))
        {
            attempt.StatusCode = request.responseCode;
            attempt.Redirected = hops > 1;
            attempt.Answered = request.responseCode > 0 || attempt.Redirected;
            attempt.Error = request.error;
            attempt.Completed = !aborted && request.result == UnityWebRequest.Result.Success;
            attempt.ContentLength = ContentLength(request);
            string encoding = request.GetResponseHeader("Content-Encoding");
            attempt.Encoded = !string.IsNullOrEmpty(encoding) && !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
            if (attempt.StatusCode == 200) attempt.Total = attempt.ContentLength;
            else if (attempt.StatusCode == 206) attempt.RangeMismatch = !TryParseContentRange(request.GetResponseHeader("Content-Range"), offset, out attempt.Total);
            if (request.downloadHandler is DownloadHandlerBuffer buffer)
            {
                if (attempt.Completed) attempt.Data = buffer.data;
                else
                {
                    try { attempt.Body = buffer.text; } catch (Exception) { /* aborted: no body */ }
                }
            }
        }
        return attempt;
    }

    internal static bool IsStalled(double movedAt, double now) => now - movedAt > DownloadStallSeconds;

    /// <summary>"bytes start-end/total": a resumed answer must start exactly at the bytes already on disk.</summary>
    internal static bool TryParseContentRange(string header, long offset, out long total)
    {
        total = -1;
        var match = Regex.Match(header ?? string.Empty, @"^\s*bytes\s+(\d+)-(\d+)/(\d+|\*)\s*$", RegexOptions.IgnoreCase);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out long start) || start != offset) return false;
        if (long.TryParse(match.Groups[3].Value, out long size)) total = size;
        return true;
    }

    /// <summary>The server's {"errorMessage"} or {"error"} (an update request, a model mismatch, ...), else the HTTP status.</summary>
    internal static string ServerError(long code, string requestError, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var payload = JsonConvert.DeserializeObject<AccessDeniedPayload>(body);
                if (!string.IsNullOrEmpty(payload?.errorMessage)) return payload.errorMessage;
                if (!string.IsNullOrEmpty(payload?.error)) return payload.error;
            }
            catch (Exception) { /* not JSON: storage errors are XML */ }
        }
        return $"Download failed: HTTP {code} {requestError}".TrimEnd();
    }

    /// <summary>The text of an error answer a disk download wrote after the <paramref name="offset"/> bytes it had.</summary>
    internal static string ReadErrorBody(string path, long offset)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long length = stream.Length - offset;
                if (length <= 0 || length > ErrorBodyLimit) return null;
                stream.Position = offset;
                var bytes = new byte[length];
                int read = 0, count;
                while (read < bytes.Length && (count = stream.Read(bytes, read, bytes.Length - read)) > 0) read += count;
                return System.Text.Encoding.UTF8.GetString(bytes, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
    }

    // Server hiccups and dropped connections are retried; a storage 4xx is usually an expired signature a new redirect fixes.
    private static bool Retryable(long code, bool storage) => code == 0 || code == 408 || code == 429 || code >= 500 || (storage && code >= 400);

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(1 << Math.Min(attempt - 1, 4));

    private static string TransferFailure(TransferAttempt result) => result.Stalled
        ? $"The download stopped: no data arrived for {DownloadStallSeconds:0} seconds."
        : $"The connection was lost ({(string.IsNullOrEmpty(result.Error) ? "no answer" : result.Error)}).";

    // Every attempt failed before any server answered: MCB is offline, as for other backend requests.
    private static void ReportUnreachable(string url, string context, string error) =>
        MCBManagedRequest.ReportException(url, new IOException(error ?? "No answer."), MCBRequestPolicy.Backend(context));

    private static long ContentLength(UnityWebRequest request) =>
        long.TryParse(request.GetResponseHeader("Content-Length"), out long length) && length >= 0 ? length : -1;

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void SetFileLength(string path, long length)
    {
        using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) stream.SetLength(length);
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
        MCBDownloadTempFiles.Track(bodyPath);

        try
        {
            // Same wire format as before ("metadata" JSON + "packageFile" zip), written by Orbiters Toolkit's transfer service.
            OrbitersTransfer.WriteMultipart(bodyPath, boundary, new[]
            {
                OrbitersTransfer.Part.Field("metadata", metadataJson),
                OrbitersTransfer.Part.File("packageFile", zipFilePath, Path.GetFileName(zipFilePath), "application/zip"),
            });

            using (var req = MCBDownloadTempFiles.Attach(bodyPath, new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)))
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
                // A cancelled or stalled upload does not take MCB offline; one that never reached the server does.
                MCBConnectivityMonitor.ReportManagedUnityWebRequest(req, url, end == OrbitersTransfer.End.Done
                    ? MCBRequestPolicy.Backend("Upload version") : MCBRequestPolicy.Transfer("Upload version"));

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
            // A body that could not be deleted yet stays tracked: it goes before the next reload.
            MCBDownloadTempFiles.Finish(bodyPath, File.Exists(bodyPath));
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
        { ("mcb_dl_", ".zip"), ("mcb_upload_", ".zip"), ("mcb_upload_body_", ".tmp"), ("mcb-rekey-", ".fbx"), ("mcb-logic-", ".unitypackage") };
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
            foreach (string gone in Tracked.Where(entry => entry.Value == null && !File.Exists(entry.Key) && !Directory.Exists(entry.Key)).Select(entry => entry.Key).ToList())
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

    /// <summary>Deletes the file or folder, and the download's staging folder in the temp folder when it is in one.</summary>
    /// <returns>False when something is still in use; nothing is thrown.</returns>
    internal static bool TryDelete(string path)
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
