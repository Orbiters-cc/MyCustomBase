#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditorInternal;

/// <summary>
/// The SHA-256 (lowercase hex) of files, at two levels that cannot be mixed up:
/// <list type="bullet">
/// <item><b>Advisory</b> (<see cref="Advisory"/>, <see cref="GetIfCached"/>, <see cref="GetAsync"/>, <see cref="GetFreshAsync"/>,
/// <see cref="Remember"/>): cached by full path, length and last write time, in memory and in <see cref="PersistentCache"/>.
/// Length and write time do not prove the bytes are unchanged (a same-length replacement can keep its timestamp), so an
/// advisory hash only decides what to show or offer: Inspector state, applied badges, version lists, server lookups.</item>
/// <item><b>Proof</b> (<see cref="Proof"/>, <see cref="Verify"/>, <see cref="SameContent"/>): always computed from the bytes,
/// for anything that writes, overwrites, deletes, patches, restores or accepts bytes as verified. A read during which the
/// file changed or vanished fails (it is never presented as a hash of the file). Inside one <see cref="Operation"/> a file
/// is read once while its stamp stays the same, and files the operation wrote from verified bytes are recorded; nothing
/// proven in one operation is trusted by another.</item>
/// </list>
/// Thread-safe. The persistent store is used off the main thread only once the main thread created it.
/// </summary>
public static class ContentHash
{
    /// <summary>
    /// Smaller files read synchronously are cheaper to hash again than to keep in the persistent store, which saves on every
    /// entry. Hashes taken with <see cref="GetAsync"/> (the models the Inspector shows) are always kept, as before.
    /// </summary>
    const long PersistMinBytes = 4L * 1024 * 1024;
    // An advisory hash taken this close to the file's last write is not cached: a second write in the same clock tick
    // could keep its length and write time.
    static readonly long RacyWindow = TimeSpan.FromMilliseconds(50).Ticks;
    const int ReadAttempts = 3;

    struct Known
    {
        public CachedFileStamp stamp;
        public string hash;
    }

    static readonly object gate = new object();
    static readonly Dictionary<string, Known> known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Task<string>> running = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ advisory

    /// <summary>
    /// Advisory: the file's hash, from the cache when its length and write time did not change; null when it does not
    /// exist or kept changing while it was read. Never use it to authorise a write or to accept bytes.
    /// </summary>
    public static string Advisory(string path)
    {
        string full = FullPath(path);
        if (full == null || !Stamp(full, out var stamp)) return null;
        if (TryKnown(full, stamp, out string hash)) return hash;
        try { return ReadStable(full, null, true); }
        catch (IOException) { return null; }
    }

    /// <summary>Advisory: the cached hash of the file as it is now, without reading it; null when it would have to be hashed.</summary>
    public static string GetIfCached(string path)
    {
        string full = FullPath(path);
        if (full == null || !Stamp(full, out var stamp)) return null;
        return TryKnown(full, stamp, out string hash) ? hash : null;
    }

    /// <summary>
    /// Advisory: <see cref="Advisory"/> on a worker thread, joined with any hash of the same file already running. Returns
    /// null (and logs) when the file is missing or cannot be read.
    /// </summary>
    /// <param name="showProgress">Show the hashing in MCB's progress bars, as a task named after the file.</param>
    public static Task<string> GetAsync(string path, bool showProgress = false)
    {
        string full = FullPath(path);
        if (full == null || !Stamp(full, out var stamp))
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
            var task = Task.Run(() => RunLogged(full, tasks));
            running[full] = task;
            return task;
        }
    }

    static string RunLogged(string full, AsyncTaskManager tasks)
    {
        string taskId = tasks != null ? $"hash_{Path.GetFileName(full)}_{Guid.NewGuid().ToString("N").Substring(0, 8)}" : null;
        if (taskId != null) tasks.StartTask(taskId, $"Calculating hash for {Path.GetFileName(full)}");
        try
        {
            string hash = ReadStable(full, taskId == null ? null : (Action<float>)(share => tasks.UpdateTaskProgress(taskId, share)), true);
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
    /// Advisory: reads and hashes the file even when a hash is cached (after MCB rewrote it) and caches the result. Null
    /// when it is missing, cannot be read, or changed while it was hashed.
    /// </summary>
    public static Task<string> GetFreshAsync(string path) => Task.Run(() =>
    {
        string full = FullPath(path);
        if (full == null) return null;
        try { return ReadStable(full, null, true, attempts: 1); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            MCBLogger.LogWarning($"[ContentHash] Discarded the hash of '{Path.GetFileName(full)}': {ex.Message}");
            return null;
        }
    });

    /// <summary>
    /// Advisory only: records the hash of a file the caller just wrote, so the Inspector can show it without reading the
    /// file. Proofs never use it.
    /// </summary>
    public static void Remember(string path, string hash)
    {
        string full = FullPath(path);
        if (full == null || string.IsNullOrWhiteSpace(hash) || !Stamp(full, out var stamp)) return;
        lock (gate) known[full] = new Known { stamp = stamp, hash = hash.Trim().ToLowerInvariant() };
    }

    /// <summary>Forgets the advisory hash of a file, in memory and in the persistent store.</summary>
    public static void Forget(string path)
    {
        string full = FullPath(path);
        if (full == null) return;
        lock (gate) known.Remove(full);
        Store()?.InvalidateHash(full);
    }

    // ------------------------------------------------------------------ proof

    /// <summary>
    /// Proof: the hash of the file's bytes as read now (or earlier in <paramref name="operation"/>, while its length and
    /// write time stayed the same); null when it does not exist. Throws <see cref="IOException"/> when the file changed or
    /// vanished while it was read.
    /// </summary>
    public static string Proof(string path, Operation operation = null)
    {
        string full = FullPath(path);
        if (full == null || !Stamp(full, out var stamp)) return null;
        if (operation != null && operation.TryGet(full, stamp, out string proven)) return proven;
        string hash = ReadStable(full, null, false, operation: operation);
        return hash;
    }

    /// <summary>
    /// Proof: whether the file's bytes hash to <paramref name="expectedHash"/> (any case). A known
    /// <paramref name="expectedLength"/> rejects a different length without reading; a match is always read from the bytes.
    /// False when the file is missing or changed while it was read.
    /// </summary>
    public static bool Verify(string path, string expectedHash, long expectedLength = -1, Operation operation = null)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return false;
        string full = FullPath(path);
        if (full == null || !Stamp(full, out var stamp)) return false;
        if (expectedLength >= 0 && stamp.length != expectedLength) return false;
        string hash;
        try { hash = Proof(full, operation); }
        catch (IOException) { return false; }
        return string.Equals(hash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Proof: whether both files exist and hold the same bytes (different lengths answer without reading).</summary>
    public static bool SameContent(string first, string second, Operation operation = null)
    {
        string a = FullPath(first), b = FullPath(second);
        if (a == null || b == null || !Stamp(a, out var stampA) || !Stamp(b, out var stampB) || stampA.length != stampB.length) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            string hashA = Proof(a, operation), hashB = Proof(b, operation);
            return hashA != null && string.Equals(hashA, hashB, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// One apply, switch, reset, build or import: inside it a file's proof is read from the bytes once and reused while its
    /// length and write time stay the same, and files the operation itself wrote from verified bytes are recorded. Create
    /// it when the operation starts and dispose it when it ends; never keep one across user interaction (it holds no state
    /// across a domain reload). Thread-safe, so an operation's worker threads may share it.
    /// </summary>
    public sealed class Operation : IDisposable
    {
        readonly Dictionary<string, Known> proven = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
        bool disposed;

        /// <summary>
        /// The caller's operation, or a new one for this call alone. Returns what the callee must dispose: null when it
        /// joined the caller's (<c>using var owned = Operation.Join(operation, out var op);</c>).
        /// </summary>
        public static Operation Join(Operation given, out Operation operation)
        {
            if (given != null) { operation = given; return null; }
            return operation = new Operation();
        }

        internal bool TryGet(string full, CachedFileStamp stamp, out string hash)
        {
            lock (proven)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Operation));
                if (proven.TryGetValue(full, out var entry) && entry.stamp.Equals(stamp)) { hash = entry.hash; return true; }
            }
            hash = null;
            return false;
        }

        internal void Put(string full, CachedFileStamp stamp, string hash)
        {
            lock (proven)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Operation));
                proven[full] = new Known { stamp = stamp, hash = hash };
            }
        }

        /// <summary>
        /// The operation wrote <paramref name="path"/> from bytes whose hash it computed or verified itself (a staged copy
        /// checked before it was moved into place, a stream hashed as it was written).
        /// </summary>
        public void Record(string path, string hash)
        {
            string full = FullPath(path);
            if (full == null || string.IsNullOrWhiteSpace(hash) || !Stamp(full, out var stamp)) return;
            Put(full, stamp, hash.Trim().ToLowerInvariant());
        }

        public void Dispose()
        {
            lock (proven) { disposed = true; proven.Clear(); }
        }
    }

    // ------------------------------------------------------------------ shared

    static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
    }

    // The file's stamp; a missing file loses its advisory entry.
    static bool Stamp(string full, out CachedFileStamp stamp)
    {
        if (CachedFileStamp.TryRead(full, out stamp)) return true;
        bool had;
        lock (gate) had = known.Remove(full);
        if (had) PersistentCache.Existing?.InvalidateHash(full);
        return false;
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

    /// <summary>
    /// Hashes the bytes, checking the stamp before and after: a file that changed is read again (up to
    /// <paramref name="attempts"/> reads), one that vanished or never settled throws, and its advisory entry is dropped.
    /// </summary>
    static string ReadStable(string full, Action<float> progress, bool persist, int attempts = ReadAttempts, Operation operation = null)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (!Stamp(full, out var before)) throw new FileNotFoundException("The file vanished before it was read.", full);
            long started = DateTime.UtcNow.Ticks;
            string hash = MCBHashing.HashFile(full, progress);
            if (!Stamp(full, out var after)) throw new FileNotFoundException("The file vanished while it was read.", full);
            if (!after.Equals(before)) continue;
            operation?.Put(full, before, hash);
            StoreAdvisory(full, before, hash, started, persist);
            return hash;
        }
        Forget(full);
        throw new IOException($"'{Path.GetFileName(full)}' kept changing while it was read.");
    }

    // A hash read from the bytes is also a good advisory answer, unless the read was too close to the last write.
    static void StoreAdvisory(string full, CachedFileStamp stamp, string hash, long hashStartedUtcTicks, bool persist)
    {
        if (hashStartedUtcTicks - stamp.lastWriteTimeUtcTicks < RacyWindow) return;
        lock (gate) known[full] = new Known { stamp = stamp, hash = hash };
        if (persist || stamp.length >= PersistMinBytes) Store()?.CacheHash(full, hash, stamp);
    }

    // PersistentCache reads editor folders when it is created, so only the main thread may create it.
    static PersistentCache Store() =>
        PersistentCache.Existing ?? (InternalEditorUtility.CurrentThreadIsMainThread() ? PersistentCache.Instance : null);

    /// <summary>Tests: forgets every advisory hash kept in memory.</summary>
    internal static void ClearMemory()
    {
        lock (gate) known.Clear();
    }
}
#endif
