#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Orbiters.Toolkit;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Newtonsoft.Json;
using Object = UnityEngine.Object;

[JsonObject(MemberSerialization.OptIn)]
public class UserInfo
{
    [JsonProperty] public string username;
    [JsonProperty] public string avatarUrl;
    // Only reported for the signed-in member: may they publish versions without XOR protection.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? creatorTrusted;
}

public class UserService
{
    private static Dictionary<int, UserInfo> userCache = new Dictionary<int, UserInfo>();
    private static readonly Dictionary<int, bool> trustedCreators = new Dictionary<int, bool>();

    /// <summary>Whether the member is a trusted creator, once their own user info has loaded.</summary>
    public static bool IsTrustedCreator(int userId) => trustedCreators.TryGetValue(userId, out bool trusted) && trusted;
    private static Dictionary<int, Texture2D> avatarCache = new Dictionary<int, Texture2D>();
    private static HashSet<int> pendingRequests = new HashSet<int>();
    private static HashSet<int> pendingAvatarDownloads = new HashSet<int>();
    private static MCBImageRetryGate<int> failedRequests = new MCBImageRetryGate<int>();
    private static MCBImageRetryGate<int> failedAvatarDownloads = new MCBImageRetryGate<int>();
    private static Dictionary<int, List<Action>> avatarCompletionCallbacks = new Dictionary<int, List<Action>>();
    private static bool repaintQueued;

    private static bool HasLiveAvatar(int userId) => MCBImageCache.TryGetLive(avatarCache, userId, out _);
    
    private static readonly string AVATARS_FOLDER = Path.Combine(MCBUtils.GetMCBDataFolder(), "avatars");
    
    static UserService()
    {
        AssemblyReloadEvents.beforeAssemblyReload += () => MCBImageCache.ReleaseAll(avatarCache);
        // Ensure avatars folder exists
        if (!Directory.Exists(AVATARS_FOLDER))
        {
            Directory.CreateDirectory(AVATARS_FOLDER);
        }
    }
    
    public static void RequestUserInfo(int userId, System.Action onComplete = null)
    {
        // Don't request if already cached
        if (userCache.ContainsKey(userId))
        {
            QueueCompletion(onComplete);
            return;
        }
        
        // Don't request if already pending
        if (pendingRequests.Contains(userId))
        {
            return;
        }
        
        // Don't request if previously failed (unless explicitly cleared)
        if (failedRequests.Contains(userId))
        {
            return;
        }
        
        pendingRequests.Add(userId);
        _ = FetchUserInfoAsync(userId, null, onComplete);
    }
    
    // Overload allowing an explicit auth token when auth.dat is not available yet
    public static void RequestUserInfo(int userId, string authToken, System.Action onComplete)
    {
        // Don't request if already cached
        if (userCache.ContainsKey(userId))
        {
            QueueCompletion(onComplete);
            return;
        }
        
        // Don't request if already pending
        if (pendingRequests.Contains(userId))
        {
            return;
        }
        
        // Don't request if previously failed (unless explicitly cleared)
        if (failedRequests.Contains(userId))
        {
            return;
        }
        
        pendingRequests.Add(userId);
        _ = FetchUserInfoAsync(userId, authToken, onComplete);
    }
    
    private static async Task FetchUserInfoAsync(int userId, string overrideToken, System.Action onComplete)
    {
        string tokenToUse = overrideToken;
        if (string.IsNullOrEmpty(tokenToUse))
        {
            var auth = AuthenticationService.GetAuth();
            if (auth != null) tokenToUse = auth.token;
        }
        
        if (string.IsNullOrEmpty(tokenToUse))
        {
            pendingRequests.Remove(userId);
            failedRequests.Add(userId);
            onComplete?.Invoke();
            return;
        }
        
        string url = $"{MCBUtils.getApiUrl()}/user?u={userId}";
        UserInfo userInfo = null;
        bool fetched = false;
        try
        {
            userInfo = await McbHttp.JsonAsync<UserInfo>(HttpMethod.Get, url, tokenToUse, null, MCBRequestPolicy.Backend("Fetch user info"),
                NetworkService.GetTimeoutSeconds(NetworkRequestType.UserInfo));
            fetched = true;
        }
        catch (OrbitersHttpException ex)
        {
            failedRequests.Add(userId);
            MCBLogger.LogWarning($"[MCB] Failed to fetch user info for ID {userId}: {ex.Message}, url: {NetworkService.SanitizeUrlForLogs(url)}");
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to parse user info for ID {userId}: {ex.Message}");
        }

        pendingRequests.Remove(userId);
        if (fetched && userInfo != null)
        {
            try
            {
                if (userInfo.creatorTrusted.HasValue) trustedCreators[userId] = userInfo.creatorTrusted.Value;
                UpdateUserInfo(userId, userInfo.username, userInfo.avatarUrl);
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[MCB] Failed to parse user info for ID {userId}: {ex.Message}");
            }
        }

        onComplete?.Invoke();
    }
    
    private static async Task DownloadAvatarAsync(int uploaderId, string avatarUrl)
    {
        try
        {
            if (HasLiveAvatar(uploaderId))
            {
                return;
            }

            string localPath = Path.Combine(AVATARS_FOLDER, $"avatar_{uploaderId}.png");

            // Check if avatar already exists locally
            if (File.Exists(localPath))
            {
                if (LoadLocalAvatar(uploaderId, localPath))
                {
                    failedAvatarDownloads.Remove(uploaderId);
                    return;
                }

                try { File.Delete(localPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            var policy = MCBManagedRequest.ResourcePolicyForUrl(
                avatarUrl,
                $"Download user avatar for {uploaderId}",
                GetAvatarWarningKey(uploaderId),
                "Profile picture unavailable");
            Texture2D texture;
            try
            {
                // A picture: OrbitersHttp sends it once and ends it at its picture limit (8 s), before MCB's 10 s.
                texture = await McbHttp.Run(avatarUrl, policy, NetworkService.GetTimeoutSeconds(NetworkRequestType.AvatarDownload), default,
                    limit => OrbitersHttp.TextureAsync(avatarUrl, null, limit));
            }
            catch (Exception ex) when (ex is OrbitersHttpException || ex is UriFormatException || ex is ArgumentException)
            {
                failedAvatarDownloads.Add(uploaderId);
                MCBLogger.LogWarning($"[MCB] Failed to download avatar for user {uploaderId}: {ex.Message} (url: {NetworkService.SanitizeUrlForLogs(avatarUrl)})");
                return;
            }

            try
            {
                if (texture == null) throw new InvalidDataException("The answer is not a picture.");
                Texture2D processed = MakeCircularAvatar(texture) ?? texture;
                byte[] pngData = processed.EncodeToPNG();
                File.WriteAllBytes(localPath, pngData);

                avatarCache[uploaderId] = MCBImageCache.Retain(processed);
                failedAvatarDownloads.Remove(uploaderId);
                MCBConnectivityMonitor.ClearRequestWarning(GetAvatarWarningKey(uploaderId));
                QueueRepaintAllViews();

                if (!ReferenceEquals(processed, texture))
                {
                    Object.DestroyImmediate(texture);
                }
            }
            catch (Exception ex)
            {
                if (texture != null && !avatarCache.ContainsValue(texture)) Object.DestroyImmediate(texture);
                failedAvatarDownloads.Add(uploaderId);
                MCBConnectivityMonitor.ReportManagedException(avatarUrl, ex, MCBRequestPolicy.ExternalResource(
                    $"Process user avatar for {uploaderId}",
                    GetAvatarWarningKey(uploaderId),
                    "Profile picture unavailable"));
                MCBLogger.LogError($"[MCB] Failed to process avatar for user {uploaderId}: {ex.Message}");
            }
        }
        finally
        {
            pendingAvatarDownloads.Remove(uploaderId);
            CompleteAvatarCallbacks(uploaderId);
        }
    }

    public static void RequestUserAvatar(int uploaderId, Action onComplete)
    {
        if (uploaderId <= 0)
        {
            return;
        }

        if (HasLiveAvatar(uploaderId))
        {
            QueueCompletion(onComplete);
            return;
        }

        if (failedAvatarDownloads.Contains(uploaderId))
        {
            return;
        }

        UserInfo info = GetUserInfo(uploaderId);
        if (info == null || string.IsNullOrWhiteSpace(info.avatarUrl))
        {
            return;
        }

        RegisterAvatarCallback(uploaderId, onComplete);

        string localPath = Path.Combine(AVATARS_FOLDER, $"avatar_{uploaderId}.png");
        if (File.Exists(localPath))
        {
            if (LoadLocalAvatar(uploaderId, localPath))
            {
                CompleteAvatarCallbacks(uploaderId);
                return;
            }

            try { File.Delete(localPath); } catch { }
        }

        if (pendingAvatarDownloads.Add(uploaderId))
        {
            _ = DownloadAvatarAsync(uploaderId, info.avatarUrl);
        }
    }

    
    private static bool LoadLocalAvatar(int uploaderId, string localPath)
    {
        try
        {
            byte[] fileData = File.ReadAllBytes(localPath);
            Texture2D texture = new Texture2D(2, 2);
            if (texture.LoadImage(fileData))
            {
                Texture2D processed = MakeCircularAvatar(texture) ?? texture;
                avatarCache[uploaderId] = MCBImageCache.Retain(processed);
                QueueRepaintAllViews();
                if (!ReferenceEquals(processed, texture))
                {
                    Object.DestroyImmediate(texture);
                }

                return true;
            }

            Object.DestroyImmediate(texture);
            return false;
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to load cached avatar for user {uploaderId}: {ex.Message}");
            return false;
        }
    }

    
    public static UserInfo GetUserInfo(int uploaderId)
    {
        return userCache.ContainsKey(uploaderId) ? userCache[uploaderId] : null;
    }
    
    public static Texture2D GetUserAvatar(int uploaderId)
    {
        // Try to load from disk if not in memory cache
        if (!HasLiveAvatar(uploaderId))
        {
            string localPath = Path.Combine(AVATARS_FOLDER, $"avatar_{uploaderId}.png");
            if (File.Exists(localPath))
            {
                if (!LoadLocalAvatar(uploaderId, localPath))
                {
                    try { File.Delete(localPath); } catch { }
                    RequestUserAvatar(uploaderId, null);
                }
            }
        }
        
        if (MCBImageCache.TryGetLive(avatarCache, uploaderId, out var texture)) return texture;
        RequestUserAvatar(uploaderId, null);
        return null;
    }

    public static string GetUserAvatarLocalPath(int uploaderId)
    {
        string localPath = Path.Combine(AVATARS_FOLDER, $"avatar_{uploaderId}.png");
        return File.Exists(localPath) ? localPath : null;
    }

    public static string GetAvatarWarningKey(int uploaderId)
    {
        return $"user-avatar:{uploaderId}";
    }

    public static bool HasAvatarWarning(int uploaderId)
    {
        return MCBConnectivityMonitor.HasRequestWarning(GetAvatarWarningKey(uploaderId));
    }
    
    public static void UpdateUserInfo(int userId, string username, string avatarUrl)
    {
        try
        {
            avatarUrl = MCBUtils.ResolveImageUrl(avatarUrl);
            UserInfo info = userCache.ContainsKey(userId) ? userCache[userId] : new UserInfo();
            bool changed = !userCache.ContainsKey(userId);
            bool avatarChanged = false;
            if (!string.IsNullOrEmpty(username) && !string.Equals(info.username, username, StringComparison.Ordinal))
            {
                info.username = username;
                changed = true;
            }

            if (!string.IsNullOrEmpty(avatarUrl) && !string.Equals(info.avatarUrl, avatarUrl, StringComparison.Ordinal))
            {
                info.avatarUrl = avatarUrl;
                changed = true;
                avatarChanged = true;
            }

            userCache[userId] = info;

            failedRequests.Remove(userId);
            if (avatarChanged)
            {
                failedAvatarDownloads.Remove(userId);
                if (avatarCache.TryGetValue(userId, out var previousAvatar) && previousAvatar != null)
                {
                    Object.DestroyImmediate(previousAvatar);
                }
                avatarCache.Remove(userId);
                string previousLocalPath = Path.Combine(AVATARS_FOLDER, $"avatar_{userId}.png");
                try { if (File.Exists(previousLocalPath)) File.Delete(previousLocalPath); } catch { }
            }

            if (!string.IsNullOrEmpty(info.avatarUrl) &&
                !HasLiveAvatar(userId) &&
                !pendingAvatarDownloads.Contains(userId) &&
                !failedAvatarDownloads.Contains(userId))
            {
                string localPath = Path.Combine(AVATARS_FOLDER, $"avatar_{userId}.png");
                if (!File.Exists(localPath))
                {
                    pendingAvatarDownloads.Add(userId);
                    _ = DownloadAvatarAsync(userId, info.avatarUrl);
                }
            }

            if (changed)
            {
                QueueRepaintAllViews();
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to update user info for ID {userId}: {ex.Message}");
        }
    }

    public static void UpdateUserInfo(int userId, UserInfo updated)
    {
        if (updated == null) return;
        UpdateUserInfo(userId, updated.username, updated.avatarUrl);
    }

    private static Texture2D MakeCircularAvatar(Texture2D texture)
    {
        if (texture == null)
            return null;

        try
        {
            int size = Mathf.Min(texture.width, texture.height);
            int xOffset = Mathf.Max(0, (texture.width - size) / 2);
            int yOffset = Mathf.Max(0, (texture.height - size) / 2);
            Color[] sourcePixels = texture.GetPixels(xOffset, yOffset, size, size);

            float radius = size * 0.5f;
            float radiusSquared = radius * radius;

            for (int y = 0; y < size; y++)
            {
                float dy = (y + 0.5f) - radius;
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) - radius;
                    int index = y * size + x;
                    if ((dx * dx) + (dy * dy) > radiusSquared)
                    {
                        Color c = sourcePixels[index];
                        c.a = 0f;
                        sourcePixels[index] = c;
                    }
                }
            }

            Texture2D circular = new Texture2D(size, size, TextureFormat.RGBA32, false);
            circular.SetPixels(sourcePixels);
            circular.Apply();
            circular.wrapMode = TextureWrapMode.Clamp;
            circular.filterMode = FilterMode.Bilinear;
            circular.name = texture.name;
            return circular;
        }
        catch (UnityException ex)
        {
            MCBLogger.LogWarning($"[MCB] Avatar texture for circular mask was not readable: {ex.Message}");
            return texture;
        }
    }

    public static bool IsUserInfoAvailable(int uploaderId)
    {
        return userCache.ContainsKey(uploaderId);
    }
    
    // Clear all failed requests (e.g., on component reload or manual refresh all)
    public static void ClearAllFailedRequests()
    {
        failedRequests.Clear();
        failedAvatarDownloads.Clear();
    }

    // Flush all in-memory user-related caches to force fresh fetching on next requests
    public static void FlushUserCache()
    {
        try
        {
            userCache.Clear();
            MCBImageCache.ReleaseAll(avatarCache);
            pendingRequests.Clear();
            pendingAvatarDownloads.Clear();
            failedRequests.Clear();
            failedAvatarDownloads.Clear();
            avatarCompletionCallbacks.Clear();

            // Trigger UI repaint so views reflect cleared state
            QueueRepaintAllViews();

            MCBLogger.Log("[MCB] Flushed user cache (user info, avatars, pending/failed requests)");
        }
        catch (System.Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to flush user cache: {ex.Message}");
        }
    }

    public static void ClearUserCache(int userId)
    {
        try
        {
            if (userCache.ContainsKey(userId)) userCache.Remove(userId);
            if (avatarCache.TryGetValue(userId, out var oldAvatar) && oldAvatar != null) Object.DestroyImmediate(oldAvatar);
            avatarCache.Remove(userId);
            if (pendingRequests.Contains(userId)) pendingRequests.Remove(userId);
            if (pendingAvatarDownloads.Contains(userId)) pendingAvatarDownloads.Remove(userId);
            if (failedRequests.Contains(userId)) failedRequests.Remove(userId);
            if (failedAvatarDownloads.Contains(userId)) failedAvatarDownloads.Remove(userId);
            avatarCompletionCallbacks.Remove(userId);
            
            // Delete local file
            string localPath = Path.Combine(AVATARS_FOLDER, $"avatar_{userId}.png");
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
                MCBLogger.Log($"[MCB] Deleted cached avatar file for user {userId}");
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[MCB] Failed to clear cache for user {userId}: {ex.Message}");
        }
    }

    private static void QueueRepaintAllViews()
    {
        if (repaintQueued)
        {
            return;
        }

        repaintQueued = true;
        EditorApplication.delayCall += () =>
        {
            repaintQueued = false;
            try { InternalEditorUtility.RepaintAllViews(); } catch { }
        };
    }

    private static void RegisterAvatarCallback(int userId, Action callback)
    {
        if (callback == null)
        {
            return;
        }

        if (!avatarCompletionCallbacks.TryGetValue(userId, out var callbacks))
        {
            callbacks = new List<Action>();
            avatarCompletionCallbacks[userId] = callbacks;
        }

        if (!callbacks.Contains(callback))
        {
            callbacks.Add(callback);
        }
    }

    private static void CompleteAvatarCallbacks(int userId)
    {
        if (!avatarCompletionCallbacks.TryGetValue(userId, out var callbacks))
        {
            return;
        }

        avatarCompletionCallbacks.Remove(userId);
        foreach (Action callback in callbacks)
        {
            QueueCompletion(callback);
        }
    }

    private static void QueueCompletion(Action callback)
    {
        if (callback == null)
        {
            return;
        }

        EditorApplication.delayCall += () =>
        {
            try { callback(); }
            catch (Exception ex) { MCBLogger.LogError($"[MCB] Deferred user callback failed: {ex.Message}"); }
        };
    }
    
    public static void PreloadUserInfo(List<CustomBaseVersion> versions)
    {
        HashSet<int> uploaderIds = new HashSet<int>();
        
        foreach (var version in versions)
        {
            if (version.uploaderId > 0 && !IsUserInfoAvailable(version.uploaderId))
            {
                uploaderIds.Add(version.uploaderId);
            }
        }
        
        foreach (int uploaderId in uploaderIds)
        {
            RequestUserInfo(uploaderId);
        }
    }
}
#endif
