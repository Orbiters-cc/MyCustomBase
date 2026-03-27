using System;
using UnityEngine;
using Object = UnityEngine.Object;

// Centralized logger for MCB. Allows toggling console output from AdvancedModeModule.
public static class MCBLogger
{
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
        if (!IsEnabled()) return;
        Debug.Log(message);
    }
    
    public static void Log(string message, Object context)
    {
        if (!IsEnabled()) return;
        Debug.Log(message, context);
    }

    public static void LogWarning(string message)
    {
        if (!IsEnabled()) return;
        Debug.LogWarning(message);
    }
    
    public static void LogWarning(string message, Object context)
    {
        if (!IsEnabled()) return;
        Debug.LogWarning(message, context);
    }

    public static void LogError(string message)
    {
        if (!IsEnabled()) return;
        Debug.LogError(message);
    }

    public static void LogError(string message, Object context)
    {
        if (!IsEnabled()) return;
        Debug.LogError(message, context);
    }

    public static void LogException(Exception ex)
    {
        if (!IsEnabled()) return;
        Debug.LogException(ex);
    }
}
