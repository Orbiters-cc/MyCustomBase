#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public class AsyncHashService
{
    private static AsyncHashService _instance;
    public static AsyncHashService Instance
    {
        get
        {
            if (_instance == null)
                _instance = new AsyncHashService();
            return _instance;
        }
    }

    private const int BUFFER_SIZE = 1024 * 1024; // 1MB buffer for reading files
    private readonly object _lockObject = new object();

    // Deduplicate concurrent hash requests per normalized file path
    private readonly System.Collections.Generic.Dictionary<string, Task<string>> _inflightByPath = new System.Collections.Generic.Dictionary<string, Task<string>>();

    private AsyncHashService()
    {
    }

    public async Task<string> CalculateFileHashAsync(string filePath, string taskId = null)
    {
        // Default behavior: visible task
        return await CalculateFileHashAsync(filePath, taskId, false);
    }

    public async Task<string> CalculateFileHashAsync(string filePath, string taskId, bool hideUi)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            MCBLogger.LogError($"[AsyncHashService] File not found: {filePath}");
            return null;
        }

        // Normalize path for consistent cache/inflight keys
        string normalizedPath = Path.GetFullPath(filePath);

        // Check cache first
        string cachedHash = PersistentCache.Instance.GetCachedHash(normalizedPath);
        if (!string.IsNullOrEmpty(cachedHash))
        {
            MCBLogger.Log($"[AsyncHashService] Using cached hash for: {Path.GetFileName(normalizedPath)}");
            return cachedHash;
        }

        // If a hash calculation is already in-flight for this file, await it instead of starting a new task
        Task<string> existing = null;
        lock (_lockObject)
        {
            if (_inflightByPath.TryGetValue(normalizedPath, out existing))
            {
                // fall through and await outside the lock
            }
        }
        if (existing != null)
        {
            return await existing; // join existing computation
        }

        // Generate unique task ID if not provided
        if (string.IsNullOrEmpty(taskId))
        {
            taskId = $"hash_{Path.GetFileName(normalizedPath)}_{Guid.NewGuid().ToString().Substring(0, 8)}";
        }

        var taskManager = AsyncTaskManager.Instance;
        var fileName = Path.GetFileName(normalizedPath);
        
        // Start the task (optionally hidden)
        taskManager.StartTask(taskId, $"Calculating hash for {fileName}", hideUi);

        // Create computation task and register as in-flight
        Task<string> computeTask = Task.Run(() => CalculateHashInternal(normalizedPath, taskId, taskManager));
        lock (_lockObject)
        {
            _inflightByPath[normalizedPath] = computeTask;
        }

        try
        {
            var hash = await computeTask;
            
            // Cache the result
            if (!string.IsNullOrEmpty(hash))
            {
                PersistentCache.Instance.CacheHash(normalizedPath, hash);
            }
            
            taskManager.CompleteTask(taskId);
            return hash;
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[AsyncHashService] Hash calculation failed for {fileName}: {ex.Message}");
            taskManager.CompleteTask(taskId, true, ex.Message);
            return null;
        }
        finally
        {
            lock (_lockObject)
            {
                Task<string> t;
                if (_inflightByPath.TryGetValue(normalizedPath, out t) && t == computeTask)
                {
                    _inflightByPath.Remove(normalizedPath);
                }
            }
        }
    }

    private string CalculateHashInternal(string filePath, string taskId, AsyncTaskManager taskManager)
    {
        var fileInfo = new FileInfo(filePath);
        long totalBytes = fileInfo.Length;
        long processedBytes = 0;

        using (var sha256 = SHA256.Create())
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE))
        {
            byte[] buffer = new byte[BUFFER_SIZE];
            int bytesRead;

            if (totalBytes == 0)
            {
                // Empty file
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                taskManager.UpdateTaskProgress(taskId, 1.0f);
                return BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant();
            }

            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                processedBytes += bytesRead;
                bool isFinal = fileStream.Position >= totalBytes;

                if (isFinal)
                {
                    sha256.TransformFinalBlock(buffer, 0, bytesRead);
                }
                else
                {
                    sha256.TransformBlock(buffer, 0, bytesRead, buffer, 0);
                }

                float progress = totalBytes > 0 ? (float)processedBytes / totalBytes : 1.0f;
                taskManager.UpdateTaskProgress(taskId, progress);

                if (isFinal)
                {
                    break;
                }

                // Yield control periodically to prevent UI blocking
                if ((processedBytes & ((BUFFER_SIZE * 4) - 1)) == 0) // roughly every 4MB
                {
                    System.Threading.Thread.Sleep(1);
                }
            }

            // Ensure UI shows completion
            taskManager.UpdateTaskProgress(taskId, 1.0f);

            var hashBytes = sha256.Hash;
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }

    public async Task<(string currentHash, string originalHash)> CalculateFBXHashesAsync(string fbxPath)
    {
        if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath))
        {
            return (null, null);
        }

        var taskId = $"fbx_hashes_{Guid.NewGuid().ToString().Substring(0, 8)}";
        var taskManager = AsyncTaskManager.Instance;
        var fileName = Path.GetFileName(fbxPath);
        
        taskManager.StartTask(taskId, $"Calculating hashes for {fileName}");

        try
        {
            // Calculate current FBX hash
            taskManager.UpdateTaskProgress(taskId, 0.0f, $"Hashing current {fileName}");
            var currentHashTask = CalculateFileHashAsync(fbxPath, $"{taskId}_current");
            
            // Calculate original FBX hash if backup exists
            string originalPath = fbxPath + FileManagerService.OriginalSuffix;
            Task<string> originalHashTask = null;
            
            if (File.Exists(originalPath))
            {
                taskManager.UpdateTaskProgress(taskId, 0.5f, $"Hashing original {fileName}");
                originalHashTask = CalculateFileHashAsync(originalPath, $"{taskId}_original");
            }

            // Wait for both tasks to complete
            string currentHash = await currentHashTask;
            string originalHash = originalHashTask != null ? await originalHashTask : null;

            taskManager.CompleteTask(taskId);
            return (currentHash, originalHash);
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[AsyncHashService] FBX hash calculation failed: {ex.Message}");
            taskManager.CompleteTask(taskId, true, ex.Message);
            return (null, null);
        }
    }

    public bool IsFileHashCached(string filePath)
    {
        return !string.IsNullOrEmpty(PersistentCache.Instance.GetCachedHash(filePath));
    }

    public void InvalidateHashCache(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            string normalized = Path.GetFullPath(filePath);
            PersistentCache.Instance.InvalidateHash(normalized);
            MCBLogger.Log($"[AsyncHashService] Hash cache invalidated for: {Path.GetFileName(normalized)}");
        }
        catch (Exception ex)
        {
            MCBLogger.LogError($"[AsyncHashService] Failed to invalidate hash cache: {ex.Message}");
        }
    }

    // Convenience method for immediate hash needs (checks cache first)
    public string GetHashIfCached(string filePath)
    {
        return PersistentCache.Instance.GetCachedHash(filePath);
    }

    // Method to start hash calculation without waiting for result
    public void StartHashCalculation(string filePath, Action<string> onCompleted = null)
    {
        var taskId = $"async_hash_{Path.GetFileName(filePath)}_{Guid.NewGuid().ToString().Substring(0, 8)}";
        
        _ = Task.Run(async () =>
        {
            var hash = await CalculateFileHashAsync(filePath, taskId);
            
            if (onCompleted != null && !string.IsNullOrEmpty(hash))
            {
                AsyncTaskManager.Instance.ExecuteOnMainThread(() => onCompleted(hash));
            }
        });
    }

    // Batch hash calculation for multiple files
    public async Task<System.Collections.Generic.Dictionary<string, string>> CalculateMultipleHashesAsync(System.Collections.Generic.List<string> filePaths)
    {
        var results = new System.Collections.Generic.Dictionary<string, string>();
        var tasks = new System.Collections.Generic.List<Task<(string path, string hash)>>();

        foreach (var filePath in filePaths)
        {
            if (File.Exists(filePath))
            {
                var task = Task.Run(async () =>
                {
                    var hash = await CalculateFileHashAsync(filePath);
                    return (filePath, hash);
                });
                tasks.Add(task);
            }
        }

        var completedTasks = await Task.WhenAll(tasks);
        
        foreach (var (path, hash) in completedTasks)
        {
            if (!string.IsNullOrEmpty(hash))
            {
                results[path] = hash;
            }
        }

        return results;
    }

    public void ClearHashCache()
    {
        PersistentCache.Instance.ClearHashCache();
        MCBLogger.Log("[AsyncHashService] Hash cache cleared.");
    }
}
#endif