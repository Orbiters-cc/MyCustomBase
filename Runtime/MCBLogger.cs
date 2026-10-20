using System;
using UnityEngine;
using Object = UnityEngine.Object;

// Centralized logger for MCB. Allows toggling console output from AdvancedModeModule.
public static class MCBLogger
{
#if UNITY_EDITOR
    public static event Action<string, string, LogType> IssueLogged;

    /// <summary>
    /// The least severe log <see cref="IssueLogged"/> gets (1 Info, 2 Warning, 3 Error, 4 Exception, as Orbiters' issue
    /// reporter counts): the stack trace, which reads debug symbols, is only taken for those.
    /// </summary>
    public static volatile int IssueMinLevel = 1;

    private static int quiet;

    /// <summary>
    /// Until disposed, nothing goes to <see cref="IssueLogged"/> (from any thread): the health checks log failures on
    /// purpose, which are not issues.
    /// </summary>
    public static IDisposable QuietIssues()
    {
        System.Threading.Interlocked.Increment(ref quiet);
        return new QuietScope();
    }

    private sealed class QuietScope : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) == 0) System.Threading.Interlocked.Decrement(ref quiet);
        }
    }

    private static int LevelOf(LogType type) =>
        type == LogType.Exception ? 4 : type == LogType.Error || type == LogType.Assert ? 3 : type == LogType.Warning ? 2 : 1;

    private static void Report(string message, LogType type, string stack = null)
    {
        var listener = IssueLogged;
        if (listener == null || System.Threading.Volatile.Read(ref quiet) > 0 || LevelOf(type) < IssueMinLevel) return;
        listener(message, stack ?? new System.Diagnostics.StackTrace(2, true).ToString(), type);
    }
#endif
    private const string EditorPrefKey = "MCB_LogInConsole";
    private static bool _initialized;
    private static bool _enabled; // runtime fallback when EditorPrefs not available

    private static void EnsureInitialized()
    {
        if (_initialized) return;
#if UNITY_EDITOR
        try
        {
            _enabled = UnityEditor.EditorPrefs.GetBool(EditorPrefKey, false);
        }
        catch
        {
            _enabled = false;
        }
#else
        _enabled = false; // default off in player builds unless changed at runtime
#endif
        _initialized = true;
    } 

    public static bool IsEnabled()
    {
        EnsureInitialized();
        return _enabled;
    }

    public static void SetEnabled(bool value)
    {
        _enabled = value;
        _initialized = true;
#if UNITY_EDITOR
        try { UnityEditor.EditorPrefs.SetBool(EditorPrefKey, value); } catch { }
#endif
    }

    public static void Log(string message)
    {
#if UNITY_EDITOR
        Report(message, LogType.Log);
#endif
        if (!IsEnabled()) return;
        Debug.Log(message);
    }
    
    public static void Log(string message, Object context)
    {
#if UNITY_EDITOR
        Report(message, LogType.Log);
#endif
        if (!IsEnabled()) return;
        Debug.Log(message, context);
    }

    public static void LogWarning(string message)
    {
#if UNITY_EDITOR
        Report(message, LogType.Warning);
#endif
        if (!IsEnabled()) return;
        Debug.LogWarning(message);
    }
    
    public static void LogWarning(string message, Object context)
    {
#if UNITY_EDITOR
        Report(message, LogType.Warning);
#endif
        if (!IsEnabled()) return;
        Debug.LogWarning(message, context);
    }

    public static void LogError(string message)
    {
#if UNITY_EDITOR
        Report(message, LogType.Error);
#endif
        if (!IsEnabled()) return;
        Debug.LogError(message);
    }

    public static void LogError(string message, Object context)
    {
#if UNITY_EDITOR
        Report(message, LogType.Error);
#endif
        if (!IsEnabled()) return;
        Debug.LogError(message, context);
    }

    public static void LogException(Exception ex)
    {
#if UNITY_EDITOR
        Report(ex?.Message, LogType.Exception, ex?.StackTrace);
#endif
        if (!IsEnabled()) return;
        Debug.LogException(ex);
    }
}
