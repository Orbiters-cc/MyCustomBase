#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// What a version fetch was made for. The service is shared by every inspector: each one only takes the responses to its
/// own selection (asset, source model, original base and account), never one started for another inspector or selection.
/// </summary>
public sealed class VersionFetchRequest
{
    public readonly string fbxPath;
    public readonly int assetId;
    public readonly string sourceVersionKey;
    private readonly string authToken;

    public VersionFetchRequest(string fbxPath, string authToken, int assetId, string sourceVersionKey)
    {
        this.fbxPath = NormalizePath(fbxPath);
        this.authToken = authToken ?? "";
        this.assetId = assetId;
        this.sourceVersionKey = sourceVersionKey ?? "";
    }

    public bool Matches(string currentFbxPath, string currentAuthToken, int currentAssetId, string currentSourceVersionKey) =>
        assetId == currentAssetId &&
        string.Equals(sourceVersionKey, currentSourceVersionKey ?? "", StringComparison.Ordinal) &&
        string.Equals(authToken, currentAuthToken ?? "", StringComparison.Ordinal) &&
        fbxPath != null && string.Equals(fbxPath, NormalizePath(currentFbxPath), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is System.IO.PathTooLongException) { return path; }
    }
}

public class AsyncVersionService
{
    private static AsyncVersionService _instance;
    public static AsyncVersionService Instance
    {
        get
        {
            if (_instance == null)
                _instance = new AsyncVersionService();
            return _instance;
        }
    }

    private readonly NetworkService networkService;
    private readonly AsyncHashService hashService;
    private readonly PersistentCache cache;
    private readonly AsyncTaskManager taskManager;

    // Track in-flight fetches to prevent duplicates per FBX path + token
    private readonly System.Collections.Generic.Dictionary<string, Task> inflightFetches = new System.Collections.Generic.Dictionary<string, Task>();
    private readonly HashSet<string> pendingForcedRefreshes = new HashSet<string>();

    // Events for UI updates, with the request they answer.
    public event Action<VersionFetchRequest, List<CustomBaseVersion>, CustomBaseVersion> OnVersionsUpdated;
    public event Action<VersionFetchRequest, string> OnVersionFetchError;

    private AsyncVersionService()
    {
        networkService = new NetworkService();
        hashService = AsyncHashService.Instance; 
        cache = PersistentCache.Instance;
        taskManager = AsyncTaskManager.Instance;
    }

    public async Task<(List<CustomBaseVersion> versions, CustomBaseVersion recommended, string error)> FetchVersionsAsync(
        string fbxPath, string authToken, int assetId, bool useCache = true, string sourceVersionKey = null)
    {
        var request = new VersionFetchRequest(fbxPath, authToken, assetId, sourceVersionKey);
        // Fast path: if we can resolve the base hash from cache and versions are cached, avoid creating any task
        if (useCache)
        {
            string cachedBaseHash = GetBaseFbxHashIfCached(fbxPath);
            if (!string.IsNullOrEmpty(cachedBaseHash))
            {
                var cachedEntryFast = cache.GetCachedVersions(cachedBaseHash, authToken, assetId, sourceVersionKey);
                // An empty list is never final: a version just published, or access just granted, must appear.
                if (cachedEntryFast != null && cachedEntryFast.serverVersions?.Count > 0 && HasRequiredAssetIds(cachedEntryFast.serverVersions) && MatchesOriginal(cachedEntryFast.serverVersions, sourceVersionKey))
                {
                    MCBLogger.Log($"[AsyncVersionService] Fast cache hit, returning versions without UI task for hash: {cachedBaseHash}");
                    taskManager.ExecuteOnMainThread(() =>
                        OnVersionsUpdated?.Invoke(request, cachedEntryFast.serverVersions, cachedEntryFast.recommendedVersion));
                    return (cachedEntryFast.serverVersions, cachedEntryFast.recommendedVersion, null);
                }
            }
        }

        var taskId = $"fetch_versions_{Guid.NewGuid().ToString().Substring(0, 8)}";
        var fileName = System.IO.Path.GetFileName(fbxPath);
        
        taskManager.StartTask(taskId, $"Fetching versions for {fileName}");

        try
        {
            // Step 1: Get base FBX hash (wait for hash calculation if needed)
            taskManager.UpdateTaskProgress(taskId, 0.1f, "Calculating FBX hash...");
            
            string baseFbxHash = await GetBaseFbxHashAsync(fbxPath);
            if (string.IsNullOrEmpty(baseFbxHash))
            {
                var error = "Could not calculate FBX hash";
                taskManager.CompleteTask(taskId, true, error);
                OnVersionFetchError?.Invoke(request, error);
                return (new List<CustomBaseVersion>(), null, error);
            }

            // Step 2: Check cache if requested
            if (useCache)
            {
                taskManager.UpdateTaskProgress(taskId, 0.3f, "Checking version cache...");
                
                var cachedEntry = cache.GetCachedVersions(baseFbxHash, authToken, assetId, sourceVersionKey);
                if (cachedEntry != null && cachedEntry.serverVersions?.Count > 0 && HasRequiredAssetIds(cachedEntry.serverVersions) && MatchesOriginal(cachedEntry.serverVersions, sourceVersionKey))
                {
                    MCBLogger.Log($"[AsyncVersionService] Using cached versions for hash: {baseFbxHash}");
                    taskManager.CompleteTask(taskId);
                    
                    // Fire event on main thread
                    taskManager.ExecuteOnMainThread(() =>
                        OnVersionsUpdated?.Invoke(request, cachedEntry.serverVersions, cachedEntry.recommendedVersion));
                    
                    return (cachedEntry.serverVersions, cachedEntry.recommendedVersion, null);
                }
            }

            // Step 3: Fetch from server
            taskManager.UpdateTaskProgress(taskId, 0.5f, "Fetching from server...");
            
            string url = $"{MCBUtils.getApiUrl()}{MCBUtils.GetAssetVersionEndpoint(assetId)}?d={baseFbxHash}&sourceKey={sourceVersionKey}";
            var fetchTask = taskManager.ExecuteOnMainThreadAsync(() => networkService.FetchVersionsAsync(url, authToken));

            // Wait for network request with progress updates
            var random = new System.Random();
            while (!fetchTask.IsCompleted)
            {
                await Task.Delay(100); // Check every 100ms
                taskManager.UpdateTaskProgress(taskId, 0.5f + (0.4f * (float)random.NextDouble()), "Waiting for server response...");
            }

            var (success, response, fetchError) = await fetchTask;

            
            if (success && response != null)
            {
                taskManager.UpdateTaskProgress(taskId, 0.9f, "Processing server response...");
                
                var versions = response.versions ?? new List<CustomBaseVersion>();
                if (!HasRequiredAssetIds(versions))
                {
                    var errorMsg = "Version response is missing assetId.";
                    taskManager.CompleteTask(taskId, true, errorMsg);
                    taskManager.ExecuteOnMainThread(() => OnVersionFetchError?.Invoke(request, errorMsg));
                    return (new List<CustomBaseVersion>(), null, errorMsg);
                }
                var recommendedVersion = versions.FirstOrDefault(v => v.version == response.recommendedVersion);

                // Cache the results
                await Task.Run(() => cache.CacheVersions(baseFbxHash, versions, recommendedVersion, authToken, assetId, sourceVersionKey));

                taskManager.CompleteTask(taskId);
                
                // Fire event on main thread
                taskManager.ExecuteOnMainThread(() =>
                    OnVersionsUpdated?.Invoke(request, versions, recommendedVersion));

                return (versions, recommendedVersion, null);
            }
            else
            {
                var errorMsg = fetchError ?? "Unknown server error";
                taskManager.CompleteTask(taskId, true, errorMsg);
                taskManager.ExecuteOnMainThread(() => OnVersionFetchError?.Invoke(request, errorMsg));
                return (new List<CustomBaseVersion>(), null, errorMsg);
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"Version fetch failed: {ex.Message}";
            MCBLogger.LogError($"[AsyncVersionService] {errorMsg}");
            taskManager.CompleteTask(taskId, true, errorMsg);
            taskManager.ExecuteOnMainThread(() => OnVersionFetchError?.Invoke(request, errorMsg));
            return (new List<CustomBaseVersion>(), null, errorMsg);
        }
    }

    // Versions are served for the original: its backup once a version replaced the FBX, else the FBX itself.
    private static string GetBaseFbxHashPath(string fbxPath)
    {
        string originalPath = FileManagerService.GetOriginalBasePath(fbxPath);
        return System.IO.File.Exists(originalPath) ? originalPath : fbxPath;
    }

    private Task<string> GetBaseFbxHashAsync(string fbxPath)
    {
        return hashService.CalculateFileHashAsync(GetBaseFbxHashPath(fbxPath));
    }

    private string GetBaseFbxHashIfCached(string fbxPath)
    {
        if (string.IsNullOrEmpty(fbxPath)) return null;
        return hashService.GetHashIfCached(GetBaseFbxHashPath(fbxPath));
    }

    public void StartVersionFetchInBackground(string fbxPath, string authToken, int assetId, bool useCache = true, string sourceVersionKey = null)
    {
        if (string.IsNullOrEmpty(fbxPath) || string.IsNullOrEmpty(authToken) || assetId <= 0)
            return;
        string key = System.IO.Path.GetFullPath(fbxPath) + "|" + authToken + "|" + assetId + "|" + sourceVersionKey;
        lock (inflightFetches)
        {
            Task running;
            if (inflightFetches.TryGetValue(key, out running) && running != null && !running.IsCompleted)
            {
                // A user reload must still fetch fresh data after the current request finishes.
                if (!useCache) pendingForcedRefreshes.Add(key);
                return;
            }
            var t = FetchVersionsAsync(fbxPath, authToken, assetId, useCache, sourceVersionKey);
            inflightFetches[key] = t;
            t.ContinueWith(_ =>
            {
                bool refresh = false;
                lock (inflightFetches)
                {
                    if (inflightFetches.TryGetValue(key, out var current) && ReferenceEquals(current, t))
                    {
                        inflightFetches.Remove(key);
                        refresh = pendingForcedRefreshes.Remove(key);
                    }
                }
                if (refresh) taskManager.ExecuteOnMainThread(() => StartVersionFetchInBackground(fbxPath, authToken, assetId, false, sourceVersionKey));
            }, TaskScheduler.Default);
        }
    }


    public bool AreVersionsCached(string fbxPath, string authToken, int assetId, string sourceVersionKey = null)
    {
        string cachedHash = GetBaseFbxHashIfCached(fbxPath);
        if (string.IsNullOrEmpty(cachedHash))
            return false;

        var cachedVersions = cache.GetCachedVersions(cachedHash, authToken, assetId, sourceVersionKey);
        return cachedVersions != null && HasRequiredAssetIds(cachedVersions.serverVersions) && MatchesOriginal(cachedVersions.serverVersions, sourceVersionKey);
    }

    public (List<CustomBaseVersion> versions, CustomBaseVersion recommended) GetCachedVersions(string fbxPath, string authToken, int assetId, string sourceVersionKey = null)
    {
        string cachedHash = GetBaseFbxHashIfCached(fbxPath);
        if (string.IsNullOrEmpty(cachedHash))
            return (new List<CustomBaseVersion>(), null);

        var cachedVersions = cache.GetCachedVersions(cachedHash, authToken, assetId, sourceVersionKey);
        if (cachedVersions != null && HasRequiredAssetIds(cachedVersions.serverVersions) && MatchesOriginal(cachedVersions.serverVersions, sourceVersionKey))
        {
            return (cachedVersions.serverVersions, cachedVersions.recommendedVersion);
        }

        return (new List<CustomBaseVersion>(), null);
    }

    public void ClearVersionCache()
    {
        cache.ClearVersionCache();
        MCBLogger.Log("[AsyncVersionService] Version cache cleared.");
    }

    public void ClearAllCache()
    {
        cache.ClearAllCache();
        MCBLogger.Log("[AsyncVersionService] All cache cleared.");
    }

    // Get statistics about cache usage
    public (int hashEntries, int versionEntries) GetCacheStats()
    {
        return cache.GetCacheStats();
    }

    // Force refresh versions (bypass cache)
    public async Task<(List<CustomBaseVersion> versions, CustomBaseVersion recommended, string error)> RefreshVersionsAsync(
        string fbxPath, string authToken, int assetId)
    {
        return await FetchVersionsAsync(fbxPath, authToken, assetId, useCache: false);
    }

    private static bool MatchesOriginal(IEnumerable<CustomBaseVersion> versions, string key) => string.IsNullOrEmpty(key) || versions.All(v => v.originalBaseVersions == null || v.originalBaseVersions.Length == 0 || v.sourceVersionKey == key);

    private static bool HasRequiredAssetIds(IEnumerable<CustomBaseVersion> versions)
    {
        return versions != null && versions.All(version => version != null && version.assetId > 0);
    }

}
#endif
