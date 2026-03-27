#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

[Serializable]
public class TaskProgress
{
    public string id;
    public string description;
    public float progress; // 0.0f to 1.0f
    public bool isCompleted;
    public bool hasError;
    public string errorMessage;
    public bool isCancelled;
    public bool isUiHidden; // if true, ProgressBarManager should not display this task
    
    public TaskProgress(string id, string description)
    {
        this.id = id;
        this.description = description;
        this.progress = 0.0f;
        this.isCompleted = false;
        this.hasError = false;
        this.errorMessage = null;
        this.isCancelled = false;
        this.isUiHidden = false;
    }
}

public class AsyncTaskManager
{
    private static AsyncTaskManager _instance;
    public static AsyncTaskManager Instance
    {
        get
        {
            if (_instance == null)
                _instance = new AsyncTaskManager();
            return _instance;
        }
    }

    private Dictionary<string, TaskProgress> activeTasks = new Dictionary<string, TaskProgress>();
    private Dictionary<string, Task> runningTasks = new Dictionary<string, Task>();
    private Dictionary<string, List<string>> taskDependencies = new Dictionary<string, List<string>>();
    private List<Action> mainThreadCallbacks = new List<Action>();

    public event Action<TaskProgress> OnTaskProgressChanged;
    public event Action<TaskProgress> OnTaskCompleted;
    public event Action<TaskProgress> OnTaskStarted;

    private AsyncTaskManager()
    {
        // Subscribe to editor update to process main thread callbacks
        EditorApplication.update += ProcessMainThreadCallbacks;
    }

    public TaskProgress StartTask(string taskId, string description, List<string> dependencies = null)
    {
        return StartTask(taskId, description, false, dependencies);
    }

    public TaskProgress StartTask(string taskId, string description, bool hideInUi, List<string> dependencies = null)
    {
        if (activeTasks.ContainsKey(taskId))
        {
            MCBLogger.LogWarning($"[AsyncTaskManager] Task '{taskId}' already exists. Returning existing task.");
            return activeTasks[taskId];
        }

        var taskProgress = new TaskProgress(taskId, description);
        taskProgress.isUiHidden = hideInUi;
        activeTasks[taskId] = taskProgress;
        
        if (dependencies != null && dependencies.Count > 0)
        {
            taskDependencies[taskId] = new List<string>(dependencies);
        }

        OnTaskStarted?.Invoke(taskProgress);
        MCBLogger.Log($"[AsyncTaskManager] Started task: {taskId} - {description} (hidden: {hideInUi})");
        
        return taskProgress;
    }

    public void UpdateTaskProgress(string taskId, float progress, string description = null)
    {
        if (!activeTasks.TryGetValue(taskId, out var taskProgress))
        {
            MCBLogger.LogWarning($"[AsyncTaskManager] Task '{taskId}' not found for progress update.");
            return;
        }

        taskProgress.progress = Mathf.Clamp01(progress);
        if (!string.IsNullOrEmpty(description))
            taskProgress.description = description;

        ExecuteOnMainThread(() => OnTaskProgressChanged?.Invoke(taskProgress));
    }

    public void CompleteTask(string taskId, bool hasError = false, string errorMessage = null)
    {
        if (!activeTasks.TryGetValue(taskId, out var taskProgress))
        {
            MCBLogger.LogWarning($"[AsyncTaskManager] Task '{taskId}' not found for completion.");
            return;
        }

        taskProgress.isCompleted = true;
        taskProgress.hasError = hasError;
        taskProgress.errorMessage = errorMessage;
        taskProgress.progress = 1.0f;

        if (runningTasks.ContainsKey(taskId))
        {
            runningTasks.Remove(taskId);
        }

        if (taskDependencies.ContainsKey(taskId))
        {
            taskDependencies.Remove(taskId);
        }

        ExecuteOnMainThread(() => 
        {
            OnTaskCompleted?.Invoke(taskProgress);
            MCBLogger.Log($"[AsyncTaskManager] Completed task: {taskId} - Success: {!hasError}");
        });
    }

    public void CancelTask(string taskId)
    {
        if (!activeTasks.TryGetValue(taskId, out var taskProgress))
        {
            MCBLogger.LogWarning($"[AsyncTaskManager] Task '{taskId}' not found for cancellation.");
            return;
        }

        taskProgress.isCancelled = true;
        taskProgress.isCompleted = true;

        if (runningTasks.ContainsKey(taskId))
        {
            runningTasks.Remove(taskId);
        }

        if (taskDependencies.ContainsKey(taskId))
        {
            taskDependencies.Remove(taskId);
        }

        ExecuteOnMainThread(() => 
        {
            OnTaskCompleted?.Invoke(taskProgress);
            MCBLogger.Log($"[AsyncTaskManager] Cancelled task: {taskId}");
        });
    }

    public bool AreTasksDependenciesSatisfied(string taskId)
    {
        if (!taskDependencies.TryGetValue(taskId, out var dependencies))
            return true; // No dependencies

        foreach (var depId in dependencies)
        {
            if (!activeTasks.TryGetValue(depId, out var depTask) || !depTask.isCompleted || depTask.hasError)
                return false;
        }

        return true;
    }

    public TaskProgress GetTaskProgress(string taskId)
    {
        activeTasks.TryGetValue(taskId, out var taskProgress);
        return taskProgress;
    }

    public List<TaskProgress> GetAllActiveTasks()
    {
        var result = new List<TaskProgress>();
        foreach (var task in activeTasks.Values)
        {
            if (!task.isCompleted)
                result.Add(task);
        }
        return result;
    }

    public Task RunTaskAsync<T>(string taskId, Func<IProgress<float>, Task<T>> taskFunc, Action<T> onCompleted = null)
    {
        if (runningTasks.ContainsKey(taskId))
        {
            MCBLogger.LogWarning($"[AsyncTaskManager] Task '{taskId}' is already running.");
            return runningTasks[taskId];
        }

        var progress = new Progress<float>(p => UpdateTaskProgress(taskId, p));
        
        var task = Task.Run(async () =>
        {
            try
            {
                var result = await taskFunc(progress);
                CompleteTask(taskId);
                
                if (onCompleted != null)
                {
                    ExecuteOnMainThread(() => onCompleted(result));
                }
                
                return result;
            }
            catch (Exception ex)
            {
                CompleteTask(taskId, true, ex.Message);
                MCBLogger.LogError($"[AsyncTaskManager] Task '{taskId}' failed: {ex.Message}");
                throw;
            }
        });

        runningTasks[taskId] = task;
        return task;
    }

    public void ExecuteOnMainThread(Action action)
    {
        lock (mainThreadCallbacks)
        {
            mainThreadCallbacks.Add(action);
        }
    }
    
    public Task ExecuteOnMainThreadAsync(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        var tcs = new TaskCompletionSource<bool>();
        ExecuteOnMainThread(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task<T> ExecuteOnMainThreadAsync<T>(Func<T> func)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));

        var tcs = new TaskCompletionSource<T>();
        ExecuteOnMainThread(() =>
        {
            try
            {
                var result = func();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task ExecuteOnMainThreadAsync(Func<Task> func)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));

        var tcs = new TaskCompletionSource<bool>();
        ExecuteOnMainThread(() =>
        {
            Task taskInstance;
            try
            {
                taskInstance = func();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return;
            }

            if (taskInstance == null)
            {
                tcs.TrySetResult(true);
                return;
            }

            taskInstance.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var exception = t.Exception != null && t.Exception.InnerExceptions.Count == 1
                        ? t.Exception.InnerException
                        : t.Exception;
                    tcs.TrySetException(exception ?? new Exception("Main thread task faulted."));
                }
                else if (t.IsCanceled)
                {
                    tcs.TrySetCanceled();
                }
                else
                {
                    tcs.TrySetResult(true);
                }
            }, TaskScheduler.Default);
        });
        return tcs.Task;
    }

    public Task<T> ExecuteOnMainThreadAsync<T>(Func<Task<T>> func)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));

        var tcs = new TaskCompletionSource<T>();
        ExecuteOnMainThread(() =>
        {
            Task<T> taskInstance;
            try
            {
                taskInstance = func();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return;
            }

            if (taskInstance == null)
            {
                tcs.TrySetResult(default(T));
                return;
            }

            taskInstance.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var exception = t.Exception != null && t.Exception.InnerExceptions.Count == 1
                        ? t.Exception.InnerException
                        : t.Exception;
                    tcs.TrySetException(exception ?? new Exception("Main thread task faulted."));
                }
                else if (t.IsCanceled)
                {
                    tcs.TrySetCanceled();
                }
                else
                {
                    tcs.TrySetResult(t.Result);
                }
            }, TaskScheduler.Default);
        });
        return tcs.Task;
    }

    private void ProcessMainThreadCallbacks()
    {
        if (mainThreadCallbacks.Count == 0) return;

        List<Action> callbacksToExecute;
        lock (mainThreadCallbacks)
        {
            callbacksToExecute = new List<Action>(mainThreadCallbacks);
            mainThreadCallbacks.Clear();
        }

        foreach (var callback in callbacksToExecute)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception ex)
            {
                MCBLogger.LogError($"[AsyncTaskManager] Main thread callback failed: {ex.Message}");
            }
        }
    }

    public void CleanupCompletedTasks()
    {
        var keysToRemove = new List<string>();
        foreach (var kvp in activeTasks)
        {
            if (kvp.Value.isCompleted && DateTime.Now.Subtract(TimeSpan.FromMinutes(5)).Ticks > 0) // Keep completed tasks for 5 minutes
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            activeTasks.Remove(key);
            MCBLogger.Log($"[AsyncTaskManager] Cleaned up completed task: {key}");
        }
    }
}
#endif