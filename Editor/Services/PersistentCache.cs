#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>Which contents of a file a hash describes: its length and last write time, read before hashing it.</summary>
public readonly struct CachedFileStamp : IEquatable<CachedFileStamp>
{
    public readonly long length;
    public readonly long lastWriteTimeUtcTicks;

    public CachedFileStamp(long length, long lastWriteTimeUtcTicks)
    {
        this.length = length;
        this.lastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
    }

    public static bool TryRead(string path, out CachedFileStamp stamp)
    {
        stamp = default;
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            stamp = new CachedFileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return false;
        }
    }

    public bool Equals(CachedFileStamp other) => length == other.length && lastWriteTimeUtcTicks == other.lastWriteTimeUtcTicks;
    public override bool Equals(object obj) => obj is CachedFileStamp other && Equals(other);
    public override int GetHashCode() => (length.GetHashCode() * 397) ^ lastWriteTimeUtcTicks.GetHashCode();
}

[Serializable]
public class HashCacheEntry
{
    public string filePath;
    public string hash;
    public long length;
    public long lastWriteTimeUtcTicks;
    public DateTime cacheTime;

    public HashCacheEntry() { }

    public HashCacheEntry(string filePath, string hash, CachedFileStamp hashedStamp)
    {
        this.filePath = filePath;
        this.hash = hash;
        length = hashedStamp.length;
        lastWriteTimeUtcTicks = hashedStamp.lastWriteTimeUtcTicks;
        cacheTime = DateTime.Now;
    }

    /// <summary>The file still has the length and write time it had when it was hashed.</summary>
    public bool IsValid() =>
        lastWriteTimeUtcTicks != 0 &&
        CachedFileStamp.TryRead(filePath, out var current) &&
        current.Equals(new CachedFileStamp(length, lastWriteTimeUtcTicks));
}

[Serializable]
public class VersionCacheEntry
{
    public string sourceVersionKey;
    public string baseFbxHash;
    public int assetId;
    public List<CustomBaseVersion> serverVersions;
    public CustomBaseVersion recommendedVersion;
    public DateTime cacheTime;
    /// <summary>Salted hash of the account's token: invalidates the cache when the account changes. Never the token itself.</summary>
    public string accountKey;

    public VersionCacheEntry() { }

    public VersionCacheEntry(string baseFbxHash, List<CustomBaseVersion> serverVersions, CustomBaseVersion recommendedVersion, string accountKey, int assetId)
    {
        this.baseFbxHash = baseFbxHash;
        this.assetId = assetId;
        this.serverVersions = serverVersions ?? new List<CustomBaseVersion>();
        this.recommendedVersion = recommendedVersion;
        this.cacheTime = DateTime.Now;
        this.accountKey = accountKey;
    }

    /// <summary>
    /// A copy that shares nothing with this entry: the cache never hands out or keeps the lists and versions an inspector
    /// clears or a delivery manifest rewrites.
    /// </summary>
    public VersionCacheEntry Copy()
    {
        var versions = CopyVersions(serverVersions);
        return new VersionCacheEntry
        {
            sourceVersionKey = sourceVersionKey,
            baseFbxHash = baseFbxHash,
            assetId = assetId,
            serverVersions = versions,
            recommendedVersion = recommendedVersion == null ? null : versions.FirstOrDefault(v => v != null && v.Equals(recommendedVersion)) ?? CopyVersion(recommendedVersion),
            cacheTime = cacheTime,
            accountKey = accountKey
        };
    }

    internal static List<CustomBaseVersion> CopyVersions(List<CustomBaseVersion> versions) => versions == null
        ? new List<CustomBaseVersion>()
        : JsonConvert.DeserializeObject<List<CustomBaseVersion>>(JsonConvert.SerializeObject(versions)) ?? new List<CustomBaseVersion>();

    private static CustomBaseVersion CopyVersion(CustomBaseVersion version) =>
        JsonConvert.DeserializeObject<CustomBaseVersion>(JsonConvert.SerializeObject(version));

    public bool IsValid(string currentBaseFbxHash, string currentAccountKey, int currentAssetId, TimeSpan maxAge)
    {
        return baseFbxHash == currentBaseFbxHash &&
               assetId == currentAssetId &&
               !string.IsNullOrEmpty(accountKey) &&
               accountKey == currentAccountKey &&
               DateTime.Now - cacheTime < maxAge;
    }
}

[Serializable]
public class PersistentCacheData
{
    public Dictionary<string, HashCacheEntry> hashCache = new Dictionary<string, HashCacheEntry>();
    public Dictionary<string, VersionCacheEntry> versionCache = new Dictionary<string, VersionCacheEntry>();
    public DateTime lastCleanup = DateTime.Now;
    /// <summary>Random per-cache salt of <see cref="VersionCacheEntry.accountKey"/>.</summary>
    public string accountKeySalt;
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
    // The latest snapshot waiting for the writer thread, and whether one is running.
    private readonly object writeLock = new object();
    private string pendingJson;
    private bool writing;

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
                cacheData = JsonConvert.DeserializeObject<PersistentCacheData>(json) ?? new PersistentCacheData();
                cacheData.hashCache = cacheData.hashCache ?? new Dictionary<string, HashCacheEntry>();
                cacheData.versionCache = cacheData.versionCache ?? new Dictionary<string, VersionCacheEntry>();
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

        bool rewrite = string.IsNullOrEmpty(cacheData.accountKeySalt);
        if (rewrite) cacheData.accountKeySalt = CreateSalt();
        // Entries without an account key were written by versions that stored the token itself (as a field and in the key).
        foreach (var key in cacheData.versionCache.Where(pair => string.IsNullOrEmpty(pair.Value?.accountKey)).Select(pair => pair.Key).ToList())
        {
            cacheData.versionCache.Remove(key);
            rewrite = true;
        }
        if (rewrite && File.Exists(cacheFilePath)) SaveCache();
    }

    private static string CreateSalt()
    {
        var salt = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(salt);
        return BitConverter.ToString(salt).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// The non-secret identity version entries are cached under: an HMAC of the token keyed by this cache's salt, so the
    /// cache file never holds a usable credential.
    /// </summary>
    internal string AccountKey(string authToken)
    {
        if (string.IsNullOrEmpty(authToken)) return null;
        string salt;
        lock (cacheLock) salt = cacheData.accountKeySalt;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(salt ?? "")))
            return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(authToken))).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Snapshots the cache on the calling thread (its entries are private copies, so nothing changes them meanwhile) and
    /// writes the latest snapshot on a worker thread; snapshots taken while one is written are coalesced.
    /// </summary>
    private void SaveCache()
    {
        string json;
        lock (cacheLock)
        {
            json = JsonConvert.SerializeObject(cacheData, Formatting.Indented);
        }

        lock (writeLock)
        {
            pendingJson = json;
            if (writing) return;
            writing = true;
        }
        Task.Run(WritePendingSnapshots);
    }

    private void WritePendingSnapshots()
    {
        while (true)
        {
            string json;
            lock (writeLock)
            {
                json = pendingJson;
                pendingJson = null;
                if (json == null)
                {
                    writing = false;
                    return;
                }
            }

            try
            {
                // One temporary file per editor process: another editor may save the same cache at the same time.
                string tempPath = cacheFilePath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(cacheFilePath))
                {
                    File.Replace(tempPath, cacheFilePath, null);
                }
                else
                {
                    File.Move(tempPath, cacheFilePath);
                }
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

    /// <param name="hashedStamp">The file's stamp read before hashing it: a file that changed since is not cached.</param>
    public void CacheHash(string filePath, string hash, CachedFileStamp hashedStamp)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(hash) || !File.Exists(filePath))
            return;

        string normalizedPath = Path.GetFullPath(filePath);
        if (!CachedFileStamp.TryRead(normalizedPath, out var current) || !current.Equals(hashedStamp))
        {
            MCBLogger.Log($"[PersistentCache] Not caching the hash of {normalizedPath}: the file changed while it was hashed.");
            return;
        }

        lock (cacheLock)
        {
            cacheData.hashCache[normalizedPath] = new HashCacheEntry(normalizedPath, hash, hashedStamp);
        }
MCBLogger.Log($"[PersistentCache] Cached hash for: {normalizedPath}");
        
        SaveCache();
    }

    // Version Cache Methods
    public VersionCacheEntry GetCachedVersions(string baseFbxHash, string authToken, int assetId, string sourceVersionKey = null)
    {
        if (string.IsNullOrEmpty(baseFbxHash))
            return null;

        if (!string.IsNullOrEmpty(authToken))
        {
            string accountKey = AccountKey(authToken);
            string cacheKey = VersionCacheKey(baseFbxHash, accountKey, assetId, sourceVersionKey);

            lock (cacheLock)
            {
                if (cacheData.versionCache.TryGetValue(cacheKey, out var cacheEntry))
                {
                    if (cacheEntry.IsValid(baseFbxHash, accountKey, assetId, VERSION_CACHE_MAX_AGE))
                    {
                        MCBLogger.Log($"[PersistentCache] Version cache hit for hash: {baseFbxHash}");
                        return cacheEntry.Copy();
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
                        entry.assetId != assetId || (entry.sourceVersionKey ?? "") != (sourceVersionKey ?? ""))
                    {
                        continue;
                    }

                    fallbackKey = kvp.Key;
                    fallbackEntry = entry;
                    break;
                }

                if (fallbackEntry != null)
                {
                    if (fallbackEntry.IsValid(baseFbxHash, fallbackEntry.accountKey, assetId, VERSION_CACHE_MAX_AGE))
                    {
                        MCBLogger.Log($"[PersistentCache] Version cache fallback hit without auth token for hash: {baseFbxHash}");
                        return fallbackEntry.Copy();
                    }

                    cacheData.versionCache.Remove(fallbackKey);
                    MCBLogger.Log($"[PersistentCache] Version cache invalidated for hash: {baseFbxHash} (no auth token)");
                }
            }
        }

        return null;
    }

    public void CacheVersions(string baseFbxHash, List<CustomBaseVersion> serverVersions, CustomBaseVersion recommendedVersion, string authToken, int assetId, string sourceVersionKey = null)
    {
        if (string.IsNullOrEmpty(baseFbxHash) || string.IsNullOrEmpty(authToken))
            return;

        string accountKey = AccountKey(authToken);
        string cacheKey = VersionCacheKey(baseFbxHash, accountKey, assetId, sourceVersionKey);
        // The cache keeps its own copies: callers go on using, clearing and rewriting theirs.
        var cacheEntry = new VersionCacheEntry(baseFbxHash, serverVersions, recommendedVersion, accountKey, assetId) { sourceVersionKey = sourceVersionKey }.Copy();
        
        lock (cacheLock)
        {
            cacheData.versionCache[cacheKey] = cacheEntry;
        }
        MCBLogger.Log($"[PersistentCache] Cached {serverVersions?.Count ?? 0} versions for hash: {baseFbxHash}");
        
        SaveCache();
    }

    /// <summary>Forgets the versions cached for this account and selection (its access to the asset was denied).</summary>
    public void RemoveCachedVersions(string baseFbxHash, string authToken, int assetId, string sourceVersionKey = null)
    {
        if (string.IsNullOrEmpty(baseFbxHash) || string.IsNullOrEmpty(authToken))
            return;

        string cacheKey = VersionCacheKey(baseFbxHash, AccountKey(authToken), assetId, sourceVersionKey);
        bool removed;
        lock (cacheLock)
        {
            removed = cacheData.versionCache.Remove(cacheKey);
        }
        if (removed) SaveCache();
    }

    private static string VersionCacheKey(string baseFbxHash, string accountKey, int assetId, string sourceVersionKey) =>
        $"{baseFbxHash}_{accountKey}_{assetId}_{sourceVersionKey}";

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
