#if UNITY_EDITOR
using System;
using System.IO;
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

    public async Task<(bool success, CustomBaseVersionResponse response, string error)> FetchVersionsAsync(string url)
    {
        using (var req = UnityWebRequest.Get(url))
        {
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
                return (false, null, "You do not seems to own the MCB. Get the MCB from the Orbiters website and try again.");
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

    private static string SanitizeUrlForLogs(string url)
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
        Action<ulong> onDownloadedBytes = null)
    {
        int timeoutSeconds = GetTimeoutSeconds(NetworkRequestType.ModelDownload);
        try
        {
            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
            {
                request.downloadHandler = new DownloadHandlerFile(destinationPath);
                request.timeout = timeoutSeconds;
                UnityWebRequestAsyncOperation operation;
                try
                {
                    operation = request.SendWebRequest();
                }
                catch (Exception ex)
                {
                    MCBConnectivityMonitor.ReportManagedException(url, ex, MCBRequestPolicy.Backend("Download file"));
                    throw;
                }

                while (!operation.isDone)
                {
                    onProgress?.Invoke(NormalizeDownloadProgress(request.downloadProgress));
                    onDownloadedBytes?.Invoke(request.downloadedBytes);
                    await Task.Yield();
                }

                onProgress?.Invoke(1f);
                onDownloadedBytes?.Invoke(request.downloadedBytes);
                MCBConnectivityMonitor.ReportManagedUnityWebRequest(request, url, MCBRequestPolicy.Backend("Download file"));

                if (request.result == UnityWebRequest.Result.Success)
                {
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
    }

    private static float NormalizeDownloadProgress(float progress)
    {
        if (float.IsNaN(progress) || progress < 0f)
        {
            return 0f;
        }

        return Mathf.Clamp01(progress);
    }
    
    // --- NEW: Creator Mode Upload Method ---
    public async Task<(bool success, string serverResponse, string error)> SubmitNewVersionAsync(string url, string authToken, string zipFilePath, string metadataJson)
    {
        byte[] fileBytes = File.ReadAllBytes(zipFilePath);
        string zipFileName = Path.GetFileName(zipFilePath);
        
        WWWForm form = new WWWForm();
        form.AddField("metadata", metadataJson);
        form.AddBinaryData("packageFile", fileBytes, zipFileName, "application/zip");

        using (var req = UnityWebRequest.Post(url, form))
        {
            req.SetRequestHeader("Authorization", $"Bearer {authToken}");
            req.timeout = 300; // 5 minute timeout for uploads

            await MCBManagedRequest.SendUnityWebRequestAsync(req, url, MCBRequestPolicy.Backend("Upload version"));
            
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
                return (false, body, error);
            }
            return (true, req.downloadHandler.text, null);
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
            if (!string.IsNullOrEmpty(authToken))
            {
                req.SetRequestHeader("Authorization", $"Bearer {authToken}");
            }
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
    }

    public async Task<CheckConnectionResponse> CheckConnectionDetailedAsync(string url, string authToken = null)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            if (!string.IsNullOrEmpty(authToken))
            {
                req.SetRequestHeader("Authorization", $"Bearer {authToken}");
            }

            try
            {
                req.timeout = GetTimeoutSeconds(NetworkRequestType.ConnectionCheck);
                await MCBManagedRequest.SendUnityWebRequestAsync(req, url, MCBRequestPolicy.Backend("Check connection"));
                if (req.result != UnityWebRequest.Result.Success)
                {
                    long code = req.responseCode;
                    string body = null;
                    try { body = req.downloadHandler?.text; } catch { /* ignore */ }
                    MCBLogger.LogWarning($"[MCB] Connection check failed: [{code}] [url: {url}] {req.error} {(string.IsNullOrEmpty(body) ? string.Empty : "- " + body)}");
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
