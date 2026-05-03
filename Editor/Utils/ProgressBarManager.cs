#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class ProgressBarManager
{
    private static ProgressBarManager _instance;
    public static ProgressBarManager Instance
    {
        get
        {
            if (_instance == null)
                _instance = new ProgressBarManager();
            return _instance;
        }
    }

    private readonly Dictionary<string, TaskProgress> visibleTasks = new Dictionary<string, TaskProgress>();
    private readonly Dictionary<string, double> taskStartTimes = new Dictionary<string, double>();
    private readonly Dictionary<string, double> scheduledRemovalTimes = new Dictionary<string, double>();
    private readonly AsyncTaskManager taskManager;

    private const double LINGER_SECONDS = 1.5;         // how long a completed task stays visible
    private const double MIN_DISPLAY_SECONDS = 0.25;   // hide tasks that complete faster than this
    private const double STALE_SECONDS = 5.0;          // consider tasks stale if stuck at ~0% for this long

    private ProgressBarManager() 
    {
        taskManager = AsyncTaskManager.Instance; 
        
        // Subscribe to task events
        taskManager.OnTaskStarted += OnTaskStarted;
        taskManager.OnTaskProgressChanged += OnTaskProgressChanged;
        taskManager.OnTaskCompleted += OnTaskCompleted;

        // Drive cleanup from editor update to avoid Task.Delay issues in editor
        EditorApplication.update += Tick;
    }

    private void OnTaskStarted(TaskProgress task)
    {
        if (task == null || task.isUiHidden) return;
        if (!visibleTasks.ContainsKey(task.id))
        {
            visibleTasks[task.id] = task;
            taskStartTimes[task.id] = EditorApplication.timeSinceStartup;
            // Request UI repaint
            EditorApplication.delayCall += () => EditorWindow.focusedWindow?.Repaint();
        }
    }

    private void OnTaskProgressChanged(TaskProgress task)
    {
        if (task == null || task.isUiHidden) return;
        if (visibleTasks.ContainsKey(task.id))
        {
            visibleTasks[task.id] = task;
            // Request UI repaint
            EditorApplication.delayCall += () => EditorWindow.focusedWindow?.Repaint();
        }
    }

    private void OnTaskCompleted(TaskProgress task)
    {
        if (task == null || task.isUiHidden) return;
        if (visibleTasks.ContainsKey(task.id))
        {
            double now = EditorApplication.timeSinceStartup;
            double start;
            taskStartTimes.TryGetValue(task.id, out start);
            double elapsed = now - start;

            // If completed very quickly, remove immediately
            if (elapsed < MIN_DISPLAY_SECONDS)
            {
                visibleTasks.Remove(task.id);
                taskStartTimes.Remove(task.id);
                scheduledRemovalTimes.Remove(task.id);
                EditorWindow.focusedWindow?.Repaint();
                return;
            }

            // Otherwise, schedule removal after a short linger period
            scheduledRemovalTimes[task.id] = now + LINGER_SECONDS;
        }
    }

    private void Tick()
    {
        if (visibleTasks.Count == 0 && scheduledRemovalTimes.Count == 0) return;

        double now = EditorApplication.timeSinceStartup;
        var toRemove = new List<string>();

        // Handle scheduled removals
        foreach (var kvp in scheduledRemovalTimes)
        {
            if (now >= kvp.Value)
            {
                toRemove.Add(kvp.Key);
            }
        }

        // Auto-cleanup tasks that are stuck at ~0% for too long (likely abandoned due to selection changes)
        foreach (var kv in visibleTasks)
        {
            var task = kv.Value;
            if (task == null || task.isCompleted || task.isCancelled) continue;

            double startedAt;
            if (!taskStartTimes.TryGetValue(kv.Key, out startedAt)) continue;

            if (task.progress <= 0.001f && (now - startedAt) > STALE_SECONDS)
            {
                // Cancel at the task manager level to ensure consistent cleanup
                try { taskManager.CancelTask(kv.Key); } catch {}
                toRemove.Add(kv.Key);
            }
        }

        bool removedAny = false;
        foreach (var id in toRemove)
        {
            removedAny = visibleTasks.Remove(id) || removedAny;
            taskStartTimes.Remove(id);
            scheduledRemovalTimes.Remove(id);
        }

        if (removedAny)
        {
            var win = EditorWindow.focusedWindow;
            if (win != null) win.Repaint();
        }
    }

    /// <summary>
    /// Draws all active progress bars in the UI. Call this from your EditorWindow's OnGUI method.
    /// </summary>
    public void DrawProgressBars()
    {
        if (visibleTasks.Count == 0) return;

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Active Operations:", EditorStyles.boldLabel);

        var tasksToRemove = new List<string>();
        
        foreach (var kvp in visibleTasks)
        {
            var task = kvp.Value;
            if (task == null)
            {
                tasksToRemove.Add(kvp.Key);
                continue;
            }


            Color progressColor = task.hasError ? Color.red : 
                                 task.isCompleted ? Color.green : 
                                 new Color(0.2f, 0.8f, 0.2f);

            string label = task.description;
            if (task.hasError)
            {
                label += " (Error)";
            }
            else if (task.isCompleted)
            {
                label += " (Complete)";
            }
            else if (task.isCancelled)
            {
                label += " (Cancelled)";
            }

            EditorUIUtils.DrawProgressBar(label, task.progress, 300, 18, null, progressColor);
            
            // Show error message if there's an error
            if (task.hasError && !string.IsNullOrEmpty(task.errorMessage))
            {
                EditorGUILayout.HelpBox(task.errorMessage, MessageType.Error);
            }
        }

        // Clean up any null or skipped tasks
        foreach (var taskId in tasksToRemove)
        {
            visibleTasks.Remove(taskId);
            taskStartTimes.Remove(taskId);
            scheduledRemovalTimes.Remove(taskId);
        }

        EditorGUILayout.Space(5);
    }

    /// <summary>
    /// Returns true if there are any active (non-completed) tasks
    /// </summary>
    public bool HasActiveTasks()
    {
        foreach (var task in visibleTasks.Values)
        {
            if (task != null && !task.isCompleted && !task.isCancelled)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gets the count of active tasks
    /// </summary>
    public int GetActiveTaskCount()
    {
        int count = 0;
        foreach (var task in visibleTasks.Values)
        {
            if (task != null && !task.isCompleted && !task.isCancelled)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Forces a cleanup of completed tasks
    /// </summary>
    public void CleanupCompletedTasks()
    {
        var tasksToRemove = new List<string>();
        foreach (var kvp in visibleTasks)
        {
            var task = kvp.Value;
            if (task == null || (task.isCompleted && !task.hasError))
            {
                tasksToRemove.Add(kvp.Key);
            }
        }

        foreach (var taskId in tasksToRemove)
        {
            visibleTasks.Remove(taskId);
            taskStartTimes.Remove(taskId);
            scheduledRemovalTimes.Remove(taskId);
        }
    }
}
#endif