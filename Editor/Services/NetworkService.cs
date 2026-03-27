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
            req.timeout = GetTimeoutSeconds(NetworkRequestType.VersionFetch);
            await req.SendWebRequest();

            // Special handling: access denied for asset (backend may return 203 or 204 with JSON { error, assetId })
            long code = req.responseCode;
            string body = null;
            try { body = req.downloadHandler?.text; } catch { /* ignore */ }

            if (code == 203 || code == 204)
            {
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
                // Try to extract specific error message from JSON body
                if (!string.IsNullOrEmpty(body))
                {
                    try
                    {
                        var errorObj = JsonConvert.DeserializeObject<AccessDeniedPayload>(body);
                        if (!string.IsNullOrEmpty(errorObj.errorMessage)) return (false, null, errorObj.errorMessage);
                        if (!string.IsNullOrEmpty(errorObj.error)) return (false, null, errorObj.error);
                    }
                    catch { /* ignore JSON parse errors */ }
                }

                switch (req.responseCode)
                {
                    case 401:
                        return (false, null, "Unauthorized: If you haven't done anything that could lead to your account being limited, contact blackorbit.");
                    case 404:
                        return (false, null, "Not Found: The requested resource could not be found.");
                    case 500:
                        return (false, null, "Server Error: An error occurred on the server.");
                }
                return (false, null, $"Request failed: {req.error}");
            }

            try
            {
                var response = JsonConvert.DeserializeObject<CustomBaseVersionResponse>(body);
                return (true, response, null);
            }
            catch (Exception e) { return (false, null, $"Failed to parse server response: {e.Message}"); }
        }
    }

    // Minimal payload to read assetId from access denied responses
    private class AccessDeniedPayload { public string error; public string assetId; public string errorMessage; }

    public async Task<(bool success, string error)> DownloadFileAsync(string url, string destinationPath)
    {
        try
        {
            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        string errorMsg = $"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}";

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
                        
                        MCBLogger.LogError($"[NetworkService] {errorMsg}, url = {url}");
                        return (false, errorMsg);
                    }

                    using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        await stream.CopyToAsync(fs);
                    }
                    
                    return (true, null);
                }
            }
        }
        catch (Exception ex)
        {
             MCBLogger.LogError($"[NetworkService] Download exception: {ex.Message}, url = {url}");
             return (false, $"Download exception: {ex.Message}");
        }
    }
    
    // --- NEW: Creator Mode Upload Method ---
    public async Task<(bool success, string serverResponse, string error)> SubmitNewVersionAsync(string url, string authToken, string zipFilePath, string metadataJson)
    {
        byte[] fileBytes = File.ReadAllBytes(zipFilePath);
        string zipFileName = Path.GetFileName(zipFilePath);
        
        WWWForm form = new WWWForm();
        form.AddBinaryData("packageFile", fileBytes, zipFileName, "application/zip");
        form.AddField("metadata", metadataJson);

        using (var req = UnityWebRequest.Post(url, form))
        {
            req.SetRequestHeader("Authorization", $"Bearer {authToken}");
            req.timeout = 300; // 5 minute timeout for uploads

            await req.SendWebRequest();
            
            if (req.result != UnityWebRequest.Result.Success)
            {
                return (false, req.downloadHandler.text, $"Upload failed: [{req.responseCode}] {req.error}");
            }

            return (true, req.downloadHandler.text, null);
        }
    }

    public class CheckConnectionResponse { public string state; }

    public async Task<string> CheckConnectionAsync(string url, string authToken = null)
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
                await req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    long code = req.responseCode;
                    string body = null;
                    try { body = req.downloadHandler?.text; } catch { /* ignore */ }
                    MCBLogger.LogWarning($"[MCB] Connection check failed: [{code}] [url: {url}] {req.error} {(string.IsNullOrEmpty(body) ? string.Empty : "- " + body)}");
                    return "disconnected";
                }

                var text = req.downloadHandler.text;
                CheckConnectionResponse resp = null;
                try { resp = JsonConvert.DeserializeObject<CheckConnectionResponse>(text); }
                catch (Exception jex)
                {
                    MCBLogger.LogWarning($"[MCB] Invalid connection check response JSON: {jex.Message}");
                    return "disconnected";
                }
                if (resp == null || string.IsNullOrEmpty(resp.state))
                {
                    MCBLogger.LogWarning("[MCB] Connection check response missing 'state'.");
                    return "disconnected";
                }
                if (resp.state == "connected" || resp.state == "limited") return resp.state;
                MCBLogger.LogWarning($"[MCB] Connection check returned unexpected state '{resp.state}'. Treating as disconnected.");
                return "disconnected";
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[MCB] Connection check error: {ex.Message}");
                return "disconnected";
            }
        }
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