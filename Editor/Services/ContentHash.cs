#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditorInternal;

/// <summary>
/// The SHA-256 (lowercase hex) of files, cached by full path, length and last write time: in memory for the editor
/// session, and in <see cref="PersistentCache"/> for files large enough to be worth it. Every MCB file hash goes through
/// here, so the same 100 MB model or payload is read once per change instead of once per question.
/// <para>
/// A hash is cached only when the file's write time is at least <see cref="RacyWindow"/> older than the moment hashing
/// started: a file written again within the clock tick of its previous write could keep its length and write time, so a
/// hash taken that close to the write is used once and not remembered (the "racy" case git guards against the same way).
/// Writers that know the hash of what they wrote record it with <see cref="Remember"/>.
/// </para>
/// Thread-safe: <see cref="Get"/> may run on worker threads. The persistent store is used off the main thread only once
/// the main thread created it.
/// </summary>
public static class ContentHash
{
    /// <summary>
    /// Smaller files read synchronously are cheaper to hash again than to keep in the persistent store, which saves on every
    /// entry. Hashes taken with <see cref="GetAsync"/> (the models the Inspector shows) are always kept, as before.
    /// </summary>
    const long PersistMinBytes = 4L * 1024 * 1024;
    static readonly long RacyWindow = TimeSpan.FromMilliseconds(50).Ticks;

    struct Known
    {
        public CachedFileStamp stamp;
        public string hash;
    }

    static readonly object gate = new object();
    static readonly Dictionary<string, Known> known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Task<string>> running = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's hash, from the cache when it did not change; null when it does not exist. Throws when it cannot be read.</summary>
    public static string Get(string path)
    {
        string full = FullPath(path);
        if (full == null || !CachedFileStamp.TryRead(full, out var stamp)) return null;
        return TryKnown(full, stamp, out string hash) ? hash : Compute(full, stamp, null, false);
    }

    /// <summary>The cached hash of the file as it is now, without reading it; null when it would have to be hashed.</summary>
    public static string GetIfCached(string path)
    {
        string full = FullPath(path);
        if (full == null || !CachedFileStamp.TryRead(full, out var stamp)) return null;
        return TryKnown(full, stamp, out string hash) ? hash : null;
    }

    /// <summary>
    /// <see cref="Get"/> on a worker thread, joined with any hash of the same file already running. Returns null (and logs)
    /// when the file is missing or cannot be read.
    /// </summary>
    /// <param name="showProgress">Show the hashing in MCB's progress bars, as a task named after the file.</param>
    public static Task<string> GetAsync(string path, bool showProgress = false)
    {
        string full = FullPath(path);
        if (full == null || !CachedFileStamp.TryRead(full, out var stamp))
        {
            MCBLogger.LogError($"[ContentHash] File not found: {path}");
            return Task.FromResult<string>(null);
        }
        if (TryKnown(full, stamp, out string cached)) return Task.FromResult(cached);
        lock (gate)
        {
            if (running.TryGetValue(full, out var existing)) return existing;
            // Created here, on the caller's (main) thread: the task manager hooks the editor loop when it is created.
            var tasks = showProgress ? AsyncTaskManager.Instance : null;
            var task = Task.Run(() => RunLogged(full, stamp, tasks));
            running[full] = task;
            return task;
        }
    }

    static string RunLogged(string full, CachedFileStamp stamp, AsyncTaskManager tasks)
    {
        string taskId = tasks != null ? $"hash_{Path.GetFileName(full)}_{Guid.NewGuid().ToString("N").Substring(0, 8)}" : null;
        if (taskId != null) tasks.StartTask(taskId, $"Calculating hash for {Path.GetFileName(full)}");
        try
        {
            string hash = Compute(full, stamp, taskId == null ? null : (Action<float>)(share => tasks.UpdateTaskProgress(taskId, share)), true);
            if (taskId != null) tasks.CompleteTask(taskId);
            return hash;
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[ContentHash] Hash calculation failed for {Path.GetFileName(full)}: {ex.Message}");
            if (taskId != null) tasks.CompleteTask(taskId, true, ex.Message);
            return null;
        }
        finally
        {
            lock (gate) running.Remove(full);
        }
    }

    /// <summary>
    /// Reads and hashes the file even when a hash is cached (after MCB rewrote it, or to check the bytes on disk), and
    /// caches the result. Null when it is missing, cannot be read, or changed while it was hashed.
    /// </summary>
    public static string GetFresh(string path)
    {
        string full = FullPath(path);
        if (full == null || !CachedFileStamp.TryRead(full, out var before)) return null;
        try
        {
            long started = DateTime.UtcNow.Ticks;
            string hash = MCBHashing.HashFile(full);
            if (!CachedFileStamp.TryRead(full, out var after) || !after.Equals(before))
            {
                MCBLogger.LogWarning($"[ContentHash] Discarded stale hash because '{Path.GetFileName(full)}' changed during calculation.");
                return null;
            }
            Store(full, before, hash, started, true);
            return hash;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            MCBLogger.LogError($"[ContentHash] Fresh hash calculation failed for '{Path.GetFileName(full)}': {ex.Message}");
            return null;
        }
    }

    /// <summary><see cref="GetFresh"/> on a worker thread.</summary>
    public static Task<string> GetFreshAsync(string path) => Task.Run(() => GetFresh(path));

    /// <summary>
    /// Whether the file holds <paramref name="expectedHash"/> (any case). A known <paramref name="expectedLength"/> answers a
    /// mismatch without reading the file.
    /// </summary>
    public static bool Verify(string path, string expectedHash, long expectedLength = -1)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return false;
        string full = FullPath(path);
        if (full == null || !CachedFileStamp.TryRead(full, out var stamp)) return false;
        if (expectedLength >= 0 && stamp.length != expectedLength) return false;
        string hash = TryKnown(full, stamp, out string cached) ? cached : Compute(full, stamp, null, false);
        return string.Equals(hash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether both files exist and hold the same bytes (compared by length, then by hash).</summary>
    public static bool SameContent(string first, string second)
    {
        string a = FullPath(first), b = FullPath(second);
        if (a == null || b == null || !CachedFileStamp.TryRead(a, out var stampA) || !CachedFileStamp.TryRead(b, out var stampB) ||
            stampA.length != stampB.length) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        string hashA = Get(a), hashB = Get(b);
        return hashA != null && string.Equals(hashA, hashB, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records the hash of a file the caller just wrote and hashed while writing (or verified), so the next question is
    /// answered without reading it. Kept for this editor session only.
    /// </summary>
    public static void Remember(string path, string hash)
    {
        string full = FullPath(path);
        if (full == null || string.IsNullOrWhiteSpace(hash) || !CachedFileStamp.TryRead(full, out var stamp)) return;
        lock (gate) known[full] = new Known { stamp = stamp, hash = hash.Trim().ToLowerInvariant() };
    }

    /// <summary>Forgets the cached hash of a file, in memory and in the persistent store.</summary>
    public static void Forget(string path)
    {
        string full = FullPath(path);
        if (full == null) return;
        lock (gate) known.Remove(full);
        Store()?.InvalidateHash(full);
    }

    static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
    }

    static bool TryKnown(string full, CachedFileStamp stamp, out string hash)
    {
        lock (gate)
        {
            if (known.TryGetValue(full, out var entry) && entry.stamp.Equals(stamp))
            {
                hash = entry.hash;
                return true;
            }
        }
        hash = Store()?.GetCachedHash(full);
        if (string.IsNullOrEmpty(hash)) return false;
        hash = hash.ToLowerInvariant();
        lock (gate) known[full] = new Known { stamp = stamp, hash = hash };
        return true;
    }

    // Hashes the file, again when it changed meanwhile (at most twice more); the last result is returned uncached.
    static string Compute(string full, CachedFileStamp stamp, Action<float> progress, bool persist)
    {
        string hash = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long started = DateTime.UtcNow.Ticks;
            hash = MCBHashing.HashFile(full, progress);
            if (!CachedFileStamp.TryRead(full, out var after)) return hash;
            if (after.Equals(stamp))
            {
                Store(full, stamp, hash, started, persist);
                return hash;
            }
            stamp = after;
        }
        return hash;
    }

    static void Store(string full, CachedFileStamp stamp, string hash, long hashStartedUtcTicks, bool persist)
    {
        if (hashStartedUtcTicks - stamp.lastWriteTimeUtcTicks < RacyWindow) return;
        lock (gate) known[full] = new Known { stamp = stamp, hash = hash };
        if (persist || stamp.length >= PersistMinBytes) Store()?.CacheHash(full, hash, stamp);
    }

    // PersistentCache reads editor folders when it is created, so only the main thread may create it.
    static PersistentCache Store() =>
        PersistentCache.Existing ?? (InternalEditorUtility.CurrentThreadIsMainThread() ? PersistentCache.Instance : null);

    /// <summary>Tests: forgets every hash kept in memory.</summary>
    internal static void ClearMemory()
    {
        lock (gate) known.Clear();
    }
}
#endif
