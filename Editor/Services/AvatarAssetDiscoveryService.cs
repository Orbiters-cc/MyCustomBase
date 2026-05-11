#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

[JsonObject(MemberSerialization.OptIn)]
public class AvatarAssetBaseInfo
{
    [JsonProperty] public int id;
    [JsonProperty] public string name;
}

[JsonObject(MemberSerialization.OptIn)]
public class AvatarDiscoveredAsset
{
    [JsonProperty] public int id;
    [JsonProperty] public string name;
    [JsonProperty] public int? ownerId;
    [JsonProperty] public string ownerUsername;
    [JsonProperty] public string ownerAvatarUrl;
    [JsonProperty] public string thumbnailUrl;
    [JsonProperty] public string bannerUrl;
    [JsonProperty] public string latestVersion;
    [JsonProperty] public AvatarAssetBaseInfo avatarBase;
    [JsonProperty] public ModelFileData[] sourceFiles;
    [JsonProperty] public bool isCompatible;
    [JsonProperty] public JObject compatibility;
}

[JsonObject(MemberSerialization.OptIn)]
public class AvatarAssetDiscoveryResponse
{
    [JsonProperty] public bool filterOnlyCompatible;
    [JsonProperty] public List<AvatarDiscoveredAsset> assets = new List<AvatarDiscoveredAsset>();
}

internal class AvatarAssetDiscoveryRequest
{
    [JsonProperty] public List<string> paths;
    [JsonProperty] public List<ModelFileData> files;
    [JsonProperty] public bool filterOnlyCompatible;
}

public static class AvatarAssetDiscoveryService
{
    private static readonly string THUMBNAILS_FOLDER = Path.Combine(MCBUtils.GetMCBDataFolder(), "asset-images");
    private static readonly TimeSpan PendingDownloadTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, Texture2D> ThumbnailCache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> PendingThumbnailDownloads = new Dictionary<string, DateTime>(StringComparer.Ordinal);
    private static readonly HashSet<int> LoggedMissingBannerAssets = new HashSet<int>();
    private static readonly HashSet<string> LoggedInsecureImageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedImageDownloads = new HashSet<string>(StringComparer.Ordinal);
    private static bool repaintQueued;

    static AvatarAssetDiscoveryService()
    {
        if (!Directory.Exists(THUMBNAILS_FOLDER))
        {
            Directory.CreateDirectory(THUMBNAILS_FOLDER);
        }

        AssemblyReloadEvents.beforeAssemblyReload += () =>
        {
            EditorApplication.delayCall -= RepaintAllViews;
            PendingThumbnailDownloads.Clear();
            repaintQueued = false;
        };
    }

    public static string BuildAvatarSignature(IEnumerable<string> paths)
    {
        if (paths == null)
        {
            return string.Empty;
        }

        return string.Join("|", paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeUnityPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    public static IEnumerator DiscoverAssetsCoroutine(
        string authToken,
        List<string> paths,
        bool filterOnlyCompatible,
        Action<AvatarAssetDiscoveryResponse, string> onComplete)
    {
        if (string.IsNullOrWhiteSpace(authToken))
        {
            onComplete?.Invoke(null, "Missing authentication token.");
            yield break;
        }

        var normalizedPaths = (paths ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeUnityPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inventoryTask = BuildProjectFileInventoryAsync(normalizedPaths);
        while (!inventoryTask.IsCompleted)
        {
            yield return null;
        }

        if (inventoryTask.IsFaulted)
        {
            string error = inventoryTask.Exception?.GetBaseException().Message ?? "Unknown inventory error.";
            MCBLogger.LogError($"[AvatarAssetDiscovery] Failed to build project file inventory: {error}");
            onComplete?.Invoke(null, $"Failed to prepare avatar asset discovery: {error}");
            yield break;
        }

        var projectFiles = inventoryTask.Result;

        var requestPayload = new AvatarAssetDiscoveryRequest
        {
            paths = normalizedPaths,
            files = projectFiles,
            filterOnlyCompatible = filterOnlyCompatible
        };

        string url = $"{MCBUtils.getApiUrl()}{MCBUtils.AVATAR_ASSET_DISCOVERY_ENDPOINT}?t={authToken}";
        byte[] requestBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(requestPayload));
        string requestSummary = BuildRequestSummary(url, normalizedPaths, filterOnlyCompatible);
        MCBLogger.LogWarning($"[AvatarAssetDiscovery] POST {requestSummary}");

        using (var request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(requestBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery);

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string responseBody = null;
                try { responseBody = request.downloadHandler?.text; } catch { }
                string httpContext = BuildResponseContext(request, url, responseBody);
                MCBLogger.LogError($"[AvatarAssetDiscovery] Request failed. {httpContext} | paths=[{string.Join(", ", normalizedPaths)}]");
                string serverError = ExtractServerError(request);
                string errorMessage = !string.IsNullOrEmpty(serverError)
                    ? $"{serverError}\n{httpContext}"
                    : $"Asset discovery request failed.\n{httpContext}";
                onComplete?.Invoke(null, errorMessage);
                yield break;
            }

            AvatarAssetDiscoveryResponse response = null;
            try
            {
                response = JsonConvert.DeserializeObject<AvatarAssetDiscoveryResponse>(request.downloadHandler.text);
            }
            catch (Exception ex)
            {
                string responseBody = null;
                try { responseBody = request.downloadHandler?.text; } catch { }
                string httpContext = BuildResponseContext(request, url, responseBody);
                MCBLogger.LogError($"[AvatarAssetDiscovery] Parse failure: {ex.Message}. {httpContext}");
                onComplete?.Invoke(null, $"Failed to parse asset discovery response: {ex.Message}\n{httpContext}");
                yield break;
            }

            if (response == null)
            {
                onComplete?.Invoke(null, "Asset discovery returned an empty response.");
                yield break;
            }

            response.assets = response.assets ?? new List<AvatarDiscoveredAsset>();
            PreloadThumbnails(response.assets);
            onComplete?.Invoke(response, null);
        }
    }

    private static async Task<List<ModelFileData>> BuildProjectFileInventoryAsync(IEnumerable<string> paths)
    {
        var tasks = new List<Task<ModelFileData>>();
        foreach (string path in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            tasks.Add(BuildProjectFileDataAsync(path));
        }

        if (tasks.Count == 0)
        {
            return new List<ModelFileData>();
        }

        var results = await Task.WhenAll(tasks);
        return results
            .Where(file => file != null)
            .ToList();
    }

    private static async Task<ModelFileData> BuildProjectFileDataAsync(string path)
    {
        string normalizedPath = NormalizeUnityPath(path);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(normalizedPath);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(fullPath)) return null;

        string hash = await AsyncHashService.Instance.CalculateFileHashAsync(fullPath, null, true);
        if (string.IsNullOrWhiteSpace(hash)) return null;

        return new ModelFileData
        {
            path = normalizedPath,
            hash = hash,
            type = "FBX",
            role = "SOURCE"
        };
    }

    public static void PreloadThumbnails(IEnumerable<AvatarDiscoveredAsset> assets)
    {
        if (assets == null)
        {
            return;
        }

        foreach (var asset in assets)
        {
            if (asset != null && asset.ownerId.HasValue && asset.ownerId.Value > 0)
            {
                UserService.UpdateUserInfo(asset.ownerId.Value, asset.ownerUsername, asset.ownerAvatarUrl);
            }
            GetThumbnail(asset);
        }
    }

    public static Texture2D GetThumbnail(AvatarDiscoveredAsset asset)
    {
        return GetImage(asset != null ? asset.thumbnailUrl : null, "thumb", asset != null ? asset.id : 0);
    }

    public static Texture2D GetBanner(AvatarDiscoveredAsset asset)
    {
        if (asset == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(asset.bannerUrl))
        {
            if (asset.id <= 0)
            {
                if (!LoggedMissingBannerAssets.Contains(asset.id))
                {
                    LoggedMissingBannerAssets.Add(asset.id);
                    MCBLogger.LogWarning($"[AvatarAssetDiscovery] Asset {asset.id} ('{asset.name}') has no bannerUrl in discovery payload.");
                }
                return null;
            }

            return GetImage(BuildAssetImageUrl(asset.id, "mcb-banner"), "banner", asset.id);
        }

        return GetImage(asset.bannerUrl, "banner", asset.id);
    }

    public static string GetBannerLocalPath(AvatarDiscoveredAsset asset)
    {
        if (asset == null)
        {
            return null;
        }

        string url = asset.bannerUrl;
        if (string.IsNullOrWhiteSpace(url) && asset.id > 0)
        {
            url = BuildAssetImageUrl(asset.id, "mcb-banner");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = ExpandImageUrl(url);
        url = PrepareUnityImageUrl(url, "banner");
        url = NormalizeImageUrl(url, "banner", asset.id);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string localPath = GetImageLocalPath("banner", asset.id, url);
        return File.Exists(localPath) ? localPath : null;
    }

    public static void PreloadBanner(AvatarDiscoveredAsset asset)
    {
        GetBanner(asset);
    }

    private static Texture2D GetImage(string url, string kind, int assetId)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = ExpandImageUrl(url);
        url = PrepareUnityImageUrl(url, kind);
        url = NormalizeImageUrl(url, kind, assetId);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string cacheKey = GetImageCacheKey(kind, assetId, url);
        if (ThumbnailCache.TryGetValue(cacheKey, out var cachedTexture))
        {
            return cachedTexture;
        }

        if (FailedImageDownloads.Contains(cacheKey))
        {
            return null;
        }

        ClearStalePendingDownload(cacheKey, kind, assetId, url);

        string localPath = GetImageLocalPath(kind, assetId, url);
        if (File.Exists(localPath))
        {
            var localTexture = LoadTextureFromDisk(localPath);
            if (localTexture != null)
            {
                ThumbnailCache[cacheKey] = localTexture;
                if (string.Equals(kind, "banner", StringComparison.Ordinal))
                {
                    MCBLogger.Log($"[AvatarAssetDiscovery] Loaded cached banner for assetId={assetId} from {localPath}");
                }
                return localTexture;
            }
        }

        DateTime pendingSince;
        if (!PendingThumbnailDownloads.TryGetValue(cacheKey, out pendingSince))
        {
            PendingThumbnailDownloads[cacheKey] = DateTime.UtcNow;
            EditorCoroutineUtility.StartCoroutineOwnerless(DownloadImageCoroutine(url, cacheKey, localPath, kind, assetId));
        }

        return null;
    }

    private static IEnumerator DownloadImageCoroutine(string url, string cacheKey, string localPath, string kind, int assetId)
    {
        Task<(byte[] bytes, string error)> downloadTask = DownloadImageBytesAsync(url);
        while (!downloadTask.IsCompleted)
        {
            yield return null;
        }

        try
        {
            if (downloadTask.IsFaulted)
            {
                FailedImageDownloads.Add(cacheKey);
                MCBLogger.LogWarning($"[AvatarAssetDiscovery] Failed to download {kind} for assetId={assetId}: {downloadTask.Exception?.GetBaseException().Message} (url: {SanitizeUrlForLogs(url)})");
                yield break;
            }

            var result = downloadTask.Result;
            if (!string.IsNullOrEmpty(result.error))
            {
                FailedImageDownloads.Add(cacheKey);
                MCBLogger.LogWarning($"[AvatarAssetDiscovery] Failed to download {kind} for assetId={assetId}: {result.error} (url: {SanitizeUrlForLogs(url)})");
                yield break;
            }

            Texture2D texture = null;
            try
            {
                if (result.bytes == null || result.bytes.Length == 0)
                {
                    FailedImageDownloads.Add(cacheKey);
                    MCBLogger.LogWarning($"[AvatarAssetDiscovery] Downloaded {kind} request for assetId={assetId} but image content was empty.");
                    yield break;
                }

                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(result.bytes))
                {
                    FailedImageDownloads.Add(cacheKey);
                    Object.DestroyImmediate(texture);
                    MCBLogger.LogWarning($"[AvatarAssetDiscovery] Downloaded {kind} request for assetId={assetId} but Unity could not decode the image bytes.");
                    yield break;
                }

                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                byte[] pngData = texture.EncodeToPNG();
                File.WriteAllBytes(localPath, pngData);
                FailedImageDownloads.Remove(cacheKey);
                ThumbnailCache[cacheKey] = texture;
                if (string.Equals(kind, "banner", StringComparison.Ordinal))
                {
                    MCBLogger.Log($"[AvatarAssetDiscovery] Cached banner for assetId={assetId} at {localPath}");
                }
            }
            catch (Exception ex)
            {
                FailedImageDownloads.Add(cacheKey);
                MCBLogger.LogError($"[AvatarAssetDiscovery] Failed to cache {kind} for assetId={assetId}: {ex.Message}");
                if (texture != null && !ThumbnailCache.ContainsKey(cacheKey))
                {
                    Object.DestroyImmediate(texture);
                }
            }
        }
        finally
        {
            PendingThumbnailDownloads.Remove(cacheKey);
        }

        QueueRepaintAllViews();
    }

    private static async Task<(byte[] bytes, string error)> DownloadImageBytesAsync(string url)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetImageDownload));
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Orbiters-MCB-UnityEditor/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    }

                    return (await response.Content.ReadAsByteArrayAsync(), null);
                }
            }
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static string ExpandImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        if (!Uri.TryCreate(MCBUtils.getApiUrl(string.Empty), UriKind.Absolute, out var apiRoot))
        {
            return url;
        }

        return new Uri(apiRoot, url.TrimStart('/')).ToString();
    }

    private static string BuildAssetImageUrl(int assetId, string imageName)
    {
        return $"{MCBUtils.getApiUrl("assets")}/{assetId}/{imageName}";
    }

    private static string PrepareUnityImageUrl(string url, string kind)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.IndexOf("format=", StringComparison.OrdinalIgnoreCase) >= 0 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        bool shouldRequestPng =
            string.Equals(kind, "thumb", StringComparison.Ordinal) &&
            uri.AbsolutePath.IndexOf("/files/serve/", StringComparison.OrdinalIgnoreCase) >= 0;

        shouldRequestPng |=
            string.Equals(kind, "banner", StringComparison.Ordinal) &&
            uri.AbsolutePath.EndsWith("/mcb-banner", StringComparison.OrdinalIgnoreCase);

        if (!shouldRequestPng)
        {
            return url;
        }

        return $"{url}{(url.Contains("?") ? "&" : "?")}format=png";
    }

    private static string NormalizeImageUrl(string url, string kind, int assetId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        bool isLocalhost =
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);

        if (isLocalhost)
        {
            if (MCBUtils.isDevEnvironment)
            {
                return url;
            }

            if (LoggedInsecureImageUrls.Add(url))
            {
                MCBLogger.LogWarning($"[AvatarAssetDiscovery] Skipping insecure local {kind} image for assetId={assetId}: {SanitizeUrlForLogs(url)}");
            }
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1
        };
        return builder.Uri.ToString();
    }

    private static void ClearStalePendingDownload(string cacheKey, string kind, int assetId, string url)
    {
        DateTime pendingSince;
        if (!PendingThumbnailDownloads.TryGetValue(cacheKey, out pendingSince))
        {
            return;
        }

        if (DateTime.UtcNow - pendingSince <= PendingDownloadTimeout)
        {
            return;
        }

        PendingThumbnailDownloads.Remove(cacheKey);
        MCBLogger.LogWarning($"[AvatarAssetDiscovery] Cleared stale pending {kind} download for assetId={assetId} url={SanitizeUrlForLogs(url)}");
    }

    private static Texture2D LoadTextureFromDisk(string localPath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(localPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (texture.LoadImage(bytes))
            {
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                return texture;
            }

            Object.DestroyImmediate(texture);
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[MCB] Failed to load cached asset thumbnail '{localPath}': {ex.Message}");
        }

        return null;
    }

    private static string GetImageCacheKey(string kind, int assetId, string url)
    {
        return $"{kind}:{assetId}:{ComputeStableHash(url)}";
    }

    private static string GetImageLocalPath(string kind, int assetId, string url)
    {
        return Path.Combine(THUMBNAILS_FOLDER, $"{kind}_{assetId}_{ComputeStableHash(url)}.png");
    }

    private static string ComputeStableHash(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "none";
        }

        using (var sha1 = SHA1.Create())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            byte[] hash = sha1.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }

    private static string NormalizeUnityPath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? path : path.Replace("\\", "/");
    }

    private static string ExtractServerError(UnityWebRequest request)
    {
        string body = null;
        try
        {
            body = request.downloadHandler?.text;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var payload = JsonConvert.DeserializeObject<JObject>(body);
            if (payload == null)
            {
                return null;
            }

            return payload.Value<string>("error") ?? payload.Value<string>("message");
        }
        catch
        {
            return null;
        }
    }

    private static string BuildRequestSummary(string url, List<string> normalizedPaths, bool filterOnlyCompatible)
    {
        return $"url={url} | filterOnlyCompatible={filterOnlyCompatible} | paths=[{string.Join(", ", normalizedPaths ?? new List<string>())}]";
    }

    private static string BuildResponseContext(UnityWebRequest request, string url, string body)
    {
        return $"HTTP {(long)request.responseCode} {request.error} | url={(url)}" +
               (string.IsNullOrWhiteSpace(body) ? string.Empty : $" | body={CreateBodySnippet(body)}");
    }

    private static string SanitizeUrlForLogs(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        return System.Text.RegularExpressions.Regex.Replace(url, @"([?&]t=)([^&]+)", "$1<redacted>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string CreateBodySnippet(string body, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string compact = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
        if (compact.Length <= maxLength)
        {
            return compact;
        }

        return compact.Substring(0, maxLength) + "...";
    }

    private static void RepaintAllViews()
    {
        repaintQueued = false;
        try
        {
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
        catch
        {
        }
    }

    private static void QueueRepaintAllViews()
    {
        if (repaintQueued)
        {
            return;
        }

        repaintQueued = true;
        EditorApplication.delayCall += RepaintAllViews;
    }
}
#endif
