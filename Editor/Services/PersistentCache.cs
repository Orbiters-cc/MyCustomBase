#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

[Serializable]
public class HashCacheEntry
{
    public string filePath;
    public string hash;
    public long lastWriteTime; // File.GetLastWriteTime().ToBinary()
    public DateTime cacheTime;
    
    public HashCacheEntry(string filePath, string hash, long lastWriteTime)
    {
        this.filePath = filePath;
        this.hash = hash;
        this.lastWriteTime = lastWriteTime;
        this.cacheTime = DateTime.Now;
    }
    
    public bool IsValid()
    {
        if (!File.Exists(filePath))
            return false;
            
        var currentWriteTime = File.GetLastWriteTime(filePath).ToBinary();
        return currentWriteTime == lastWriteTime;
    }
}

[Serializable]
public class VersionCacheEntry
{
    public string baseFbxHash;
    public int assetId;
    public List<CustomBaseVersion> serverVersions;
    public CustomBaseVersion recommendedVersion;
    public DateTime cacheTime;
    public string authToken; // To invalidate cache when user changes
    
    public VersionCacheEntry(string baseFbxHash, List<CustomBaseVersion> serverVersions, CustomBaseVersion recommendedVersion, string authToken, int assetId)
    {
        this.baseFbxHash = baseFbxHash;
        this.assetId = assetId;
        this.serverVersions = serverVersions ?? new List<CustomBaseVersion>();
        this.recommendedVersion = recommendedVersion;
        this.cacheTime = DateTime.Now;
        this.authToken = authToken;
    }
    
    public bool IsValid(string currentBaseFbxHash, string currentAuthToken, int currentAssetId, TimeSpan maxAge)
    {
        return baseFbxHash == currentBaseFbxHash && 
               assetId == currentAssetId &&
               authToken == currentAuthToken &&
               DateTime.Now - cacheTime < maxAge;
    }
}

[Serializable]
public class PersistentCacheData
{
    public Dictionary<string, HashCacheEntry> hashCache = new Dictionary<string, HashCacheEntry>();
    public Dictionary<string, VersionCacheEntry> versionCache = new Dictionary<string, VersionCacheEntry>();
    public DateTime lastCleanup = DateTime.Now;
}

public class PersistentCache
{
    private static PersistentCache _instance;
    public static PersistentCache Instance
    {
        get
        {
            if (_instance == null)
                _instance = new PersistentCache();
            return _instance;
        }
    }

    private const string CACHE_FILE_NAME = "mcb_cache.json";
    private static readonly TimeSpan VERSION_CACHE_MAX_AGE = TimeSpan.FromHours(1); // Cache versions for 1 hour
    private static readonly TimeSpan HASH_CACHE_MAX_AGE = TimeSpan.FromDays(7); // Cache hashes for 7 days
    private static readonly TimeSpan CLEANUP_INTERVAL = TimeSpan.FromDays(1); // Cleanup old entries daily
    
    private PersistentCacheData cacheData;
    private string cacheFilePath; 
    private readonly object cacheLock = new object();

    private PersistentCache()
    {
        cacheFilePath = Path.Combine(GetCacheDirectory(), CACHE_FILE_NAME);
        LoadCache();
        
        // Subscribe to editor update for periodic cleanup
        EditorApplication.update += PeriodicCleanup;
    }

    private string GetCacheDirectory()
    {
        string cacheDir = Path.Combine(MCBUtils.GetMCBDataFolder(), "cache");
        MCBUtils.EnsureDirectoryExists(cacheDir, false);
        return cacheDir;
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(cacheFilePath))
            {
                string json = File.ReadAllText(cacheFilePath);
                cacheData = JsonConvert.DeserializeObject<PersistentCacheData>(json);
                MCBLogger.Log($"[PersistentCache] Loaded cache with {cacheData.hashCache.Count} hash entries and {cacheData.versionCache.Count} version entries.");
            }
            else
            {
                cacheData = new PersistentCacheData();
                MCBLogger.Log("[PersistentCache] Created new cache data.");
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[PersistentCache] Failed to load cache: {ex.Message}");
            cacheData = new PersistentCacheData();
        }
    }

    private void SaveCache()
    {
        lock (cacheLock)
        {
            try
            {
                string json = JsonConvert.SerializeObject(cacheData, Formatting.Indented);
                string tempPath = cacheFilePath + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(cacheFilePath))
                {
                    File.Replace(tempPath, cacheFilePath, null);
                }
                else
                {
                    File.Move(tempPath, cacheFilePath);
                }

                MCBLogger.Log($"[PersistentCache] Saved cache with {cacheData.hashCache.Count} hash entries and {cacheData.versionCache.Count} version entries.");
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[PersistentCache] Failed to save cache: {ex.Message}");
            }
        }
    }

    // Hash Cache Methods
    public string GetCachedHash(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        string normalizedPath = Path.GetFullPath(filePath);
        
        lock (cacheLock)
        {
            if (cacheData.hashCache.TryGetValue(normalizedPath, out var cacheEntry))
            {
                if (cacheEntry.IsValid() && DateTime.Now - cacheEntry.cacheTime < HASH_CACHE_MAX_AGE)
                {
                    MCBLogger.Log($"[PersistentCache] Hash cache hit for: {normalizedPath}");
                    return cacheEntry.hash;
                }

                cacheData.hashCache.Remove(normalizedPath);
                MCBLogger.Log($"[PersistentCache] Hash cache invalidated for: {normalizedPath}");
            }
        }

        return null;
    }

    public void InvalidateHash(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        string normalizedPath = Path.GetFullPath(filePath);
        bool removed;
        lock (cacheLock)
        {
            removed = cacheData.hashCache.Remove(normalizedPath);
        }

        if (removed)
        {
            SaveCache();
            MCBLogger.Log($"[PersistentCache] Manually invalidated hash for: {normalizedPath}");
        }
    }

    public void CacheHash(string filePath, string hash)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(hash) || !File.Exists(filePath))
            return;

        string normalizedPath = Path.GetFullPath(filePath);
        long lastWriteTime = File.GetLastWriteTime(normalizedPath).ToBinary();
        
        lock (cacheLock)
        {
            cacheData.hashCache[normalizedPath] = new HashCacheEntry(normalizedPath, hash, lastWriteTime);
        }
        MCBLogger.Log($"[PersistentCache] Cached hash for: {normalizedPath}");
        
        SaveCache();
    }

    // Version Cache Methods
    public VersionCacheEntry GetCachedVersions(string baseFbxHash, string authToken, int assetId)
    {
        if (string.IsNullOrEmpty(baseFbxHash))
            return null;

        if (!string.IsNullOrEmpty(authToken))
        {
            string cacheKey = $"{baseFbxHash}_{authToken}_{assetId}";
            
            lock (cacheLock)
            {
                if (cacheData.versionCache.TryGetValue(cacheKey, out var cacheEntry))
                {
                    if (cacheEntry.IsValid(baseFbxHash, authToken, assetId, VERSION_CACHE_MAX_AGE))
                    {
                        MCBLogger.Log($"[PersistentCache] Version cache hit for hash: {baseFbxHash}");
                        return cacheEntry;
                    }

                    cacheData.versionCache.Remove(cacheKey);
                    MCBLogger.Log($"[PersistentCache] Version cache invalidated for hash: {baseFbxHash}");
                }
            }

        }
        else
        {
            // No auth token currently available (e.g., user not authenticated yet). Try best effort lookup.
            string fallbackKey = null;
            VersionCacheEntry fallbackEntry = null;

            lock (cacheLock)
            {
                foreach (var kvp in cacheData.versionCache)
                {
                    var entry = kvp.Value;
                    if (entry == null)
                    {
                        continue;
                    }

                    if (!string.Equals(entry.baseFbxHash, baseFbxHash, StringComparison.Ordinal) ||
                        entry.assetId != assetId)
                    {
                        continue;
                    }

                    fallbackKey = kvp.Key;
                    fallbackEntry = entry;
                    break;
                }

                if (fallbackEntry != null)
                {
                    if (fallbackEntry.IsValid(baseFbxHash, fallbackEntry.authToken, assetId, VERSION_CACHE_MAX_AGE))
                    {
                        MCBLogger.Log($"[PersistentCache] Version cache fallback hit without auth token for hash: {baseFbxHash}");
                        return fallbackEntry;
                    }

                    cacheData.versionCache.Remove(fallbackKey);
                    MCBLogger.Log($"[PersistentCache] Version cache invalidated for hash: {baseFbxHash} (no auth token)");
                }
            }
        }

        return null;
    }

    public void CacheVersions(string baseFbxHash, List<CustomBaseVersion> serverVersions, CustomBaseVersion recommendedVersion, string authToken, int assetId)
    {
        if (string.IsNullOrEmpty(baseFbxHash) || string.IsNullOrEmpty(authToken))
            return;

        string cacheKey = $"{baseFbxHash}_{authToken}_{assetId}";
        var cacheEntry = new VersionCacheEntry(baseFbxHash, serverVersions, recommendedVersion, authToken, assetId);
        
        lock (cacheLock)
        {
            cacheData.versionCache[cacheKey] = cacheEntry;
        }
        MCBLogger.Log($"[PersistentCache] Cached {serverVersions?.Count ?? 0} versions for hash: {baseFbxHash}");
        
        SaveCache();
    }

    // Cleanup Methods
    private void PeriodicCleanup()
    {
        if (DateTime.Now - cacheData.lastCleanup > CLEANUP_INTERVAL)
        {
            CleanupExpiredEntries();
            cacheData.lastCleanup = DateTime.Now;
            SaveCache();
        }
    }

    public void CleanupExpiredEntries()
    {
        int removedHashEntries = 0;
        int removedVersionEntries = 0;

        lock (cacheLock)
        {
            var expiredHashKeys = new List<string>();
            foreach (var kvp in cacheData.hashCache)
            {
                if (!kvp.Value.IsValid() || DateTime.Now - kvp.Value.cacheTime > HASH_CACHE_MAX_AGE)
                {
                    expiredHashKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredHashKeys)
            {
                cacheData.hashCache.Remove(key);
                removedHashEntries++;
            }

            var expiredVersionKeys = new List<string>();
            foreach (var kvp in cacheData.versionCache)
            {
                if (DateTime.Now - kvp.Value.cacheTime > VERSION_CACHE_MAX_AGE)
                {
                    expiredVersionKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredVersionKeys)
            {
                cacheData.versionCache.Remove(key);
                removedVersionEntries++;
            }
        }

        if (removedHashEntries > 0 || removedVersionEntries > 0)
        {
            MCBLogger.Log($"[PersistentCache] Cleaned up {removedHashEntries} hash entries and {removedVersionEntries} version entries.");
        }
    }

    public void ClearAllCache()
    {
        lock (cacheLock)
        {
            cacheData.hashCache.Clear();
            cacheData.versionCache.Clear();
        }
        SaveCache();
        MCBLogger.Log("[PersistentCache] Cleared all cache data.");
    }

    public void ClearHashCache()
    {
        lock (cacheLock)
        {
            cacheData.hashCache.Clear();
        }
        SaveCache();
        MCBLogger.Log("[PersistentCache] Cleared hash cache.");
    }

    public void ClearVersionCache()
    {
        lock (cacheLock)
        {
            cacheData.versionCache.Clear();
        }
        SaveCache();
        MCBLogger.Log("[PersistentCache] Cleared version cache.");
    }

    // Statistics
    public (int hashEntries, int versionEntries) GetCacheStats()
    {
        lock (cacheLock)
        {
            return (cacheData.hashCache.Count, cacheData.versionCache.Count);
        }
    }
}
#endif
