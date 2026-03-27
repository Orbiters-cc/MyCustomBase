#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Editor-only issue reporter that can forward Unity console logs to backend /bugs endpoint.
/// Controlled from AdvancedModeModule via EditorPrefs settings.
/// </summary>
public static class EditorIssueReporter
{
    // EditorPrefs keys
    private const string SharePrefKey = "MCB_ShareIssueLogs";
    private const string MinSeverityPrefKey = "MCB_MinIssueSeverity"; // 1..4

    // Defaults
    private const bool DefaultShare = true;
    private const int DefaultMinSeverity = 2; // Warning

    // Severity mapping: 1=INFO, 2=WARN, 3=ERROR, 4=FATAL
    public enum SeverityLevel { Info = 1, Warn = 2, Error = 3, Fatal = 4 }

    private static bool _listening;
    private static readonly Dictionary<string, double> _lastSentAtBySignature = new Dictionary<string, double>();
    private const double DuplicateSuppressWindowSeconds = 10.0; // prevent sending identical message too often

    public static bool ShareIssueLogs
    {
        get
        {
            try { return EditorPrefs.GetBool(SharePrefKey, DefaultShare); }
            catch { return DefaultShare; }
        }
        set
        {
            try { EditorPrefs.SetBool(SharePrefKey, value); } catch { }
        }
    }

    public static int MinSeverityLevel
    {
        get
        {
            try { return Mathf.Clamp(EditorPrefs.GetInt(MinSeverityPrefKey, DefaultMinSeverity), 1, 4); }
            catch { return DefaultMinSeverity; }
        }
        set
        {
            try { EditorPrefs.SetInt(MinSeverityPrefKey, Mathf.Clamp(value, 1, 4)); } catch { }
        }
    }

    public static void RefreshListener()
    {
        bool shouldListen = ShareIssueLogs;
        if (shouldListen && !_listening)
        {
            Application.logMessageReceived -= OnLogMessageReceived; // ensure not double
            Application.logMessageReceived += OnLogMessageReceived;
            _listening = true;
        }
        else if (!shouldListen && _listening)
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            _listening = false;
        }
    }

    private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        if (!ShareIssueLogs) return;

        var (level, severityText) = MapSeverity(type);
        if ((int)level < MinSeverityLevel) return;

        // Build payload
        string message = condition ?? string.Empty;
        if (message.Length > 10000) message = message.Substring(0, 10000);
        string trimmedStack = stackTrace ?? string.Empty;
        if (trimmedStack.Length > 50000) trimmedStack = trimmedStack.Substring(0, 50000);

        string errorType = ExtractErrorType(condition, type);
        string tool = "MCB";
        string url = EditorSceneManager.GetActiveScene().path;
        string userAgent = SystemInfo.operatingSystem + " | Unity " + Application.unityVersion + " | " + SystemInfo.deviceModel;

        // browserInfo as JSON string
        var browserInfoObj = new Dictionary<string, object>
        {
            {"unityVersion", Application.unityVersion},
            {"platform", Application.platform.ToString()},
            {"deviceModel", SystemInfo.deviceModel},
            {"deviceType", SystemInfo.deviceType.ToString()},
            {"graphicsDeviceName", SystemInfo.graphicsDeviceName},
            {"graphicsDeviceType", SystemInfo.graphicsDeviceType.ToString()},
            {"graphicsMemorySizeMB", SystemInfo.graphicsMemorySize},
            {"systemMemorySizeMB", SystemInfo.systemMemorySize},
            {"projectPath", Application.dataPath}
        };
        string browserInfoJson = MiniJson.Serialize(browserInfoObj);

        // Deduplication by signature
        string signature = severityText + "|" + errorType + "|" + FirstLine(message);
        double now = EditorApplication.timeSinceStartup;
        if (_lastSentAtBySignature.TryGetValue(signature, out var last) && now - last < DuplicateSuppressWindowSeconds)
        {
            return; // suppress duplicates for a short window
        }
        _lastSentAtBySignature[signature] = now;

        // Fire and forget async send
        EditorApplication.delayCall += () => SendIssueAsync(message, severityText, errorType, tool, url, trimmedStack, userAgent, browserInfoJson);
    }

    private static (SeverityLevel, string) MapSeverity(LogType type)
    {
        switch (type)
        {
            case LogType.Error:
            case LogType.Assert:
                return (SeverityLevel.Error, "ERROR");
            case LogType.Warning:
                return (SeverityLevel.Warn, "WARN");
            case LogType.Exception:
                return (SeverityLevel.Fatal, "FATAL");
            default:
                return (SeverityLevel.Info, "INFO");
        }
    }

    private static string ExtractErrorType(string condition, LogType type)
    {
        if (type == LogType.Exception && !string.IsNullOrEmpty(condition))
        {
            // Usually formatted as "ExceptionType: message" – extract the type left of colon
            int idx = condition.IndexOf(':');
            if (idx > 0)
            {
                return condition.Substring(0, idx).Trim();
            }
        }
        return type.ToString();
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        int idx = s.IndexOf('\n');
        return idx >= 0 ? s.Substring(0, idx) : s;
    }

    private static async void SendIssueAsync(string message, string severity, string errorType, string tool, string url, string stackTrace, string userAgent, string browserInfoJson)
    {
        try
        {
            // Build JSON body manually to avoid dependency on Newtonsoft in this assembly
            var sb = new StringBuilder();
            sb.Append('{');
            AppendJsonField(sb, "message", message, true);
            AppendJsonField(sb, "severity", severity);
            AppendJsonField(sb, "errorType", errorType);
            AppendJsonField(sb, "tool", tool);
            AppendJsonField(sb, "url", url);
            AppendJsonField(sb, "stackTrace", stackTrace);
            AppendJsonField(sb, "userAgent", userAgent);
            // browserInfo is expected to be JSON; send as string that server can parse
            AppendJsonField(sb, "browserInfo", browserInfoJson);
            sb.Append('}');
            byte[] bodyRaw = Encoding.UTF8.GetBytes(sb.ToString());

            string endpoint = MCBUtils.getApiUrl(scope: "bugs");
            using (var req = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");

                // Optional Authorization
                var auth = AuthenticationService.GetAuth();
                if (auth != null && !string.IsNullOrEmpty(auth.token))
                {
                    req.SetRequestHeader("Authorization", $"Bearer {auth.token}");
                }

                await req.SendWebRequest();
                // Swallow errors; this is best-effort reporting
                if (req.result != UnityWebRequest.Result.Success)
                {
                    // Optionally, log locally but do not recurse into reporter (disabled by signature throttling)
                    Debug.Log($"[MCB IssueReporter] Failed to send issue: {req.responseCode} {req.error}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.Log($"[MCB IssueReporter] Exception while sending issue: {ex.Message}");
        }
    }

    private static void AppendJsonField(StringBuilder sb, string key, string value, bool first = false)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(EscapeJson(key)).Append('"').Append(':');
        sb.Append('"').Append(EscapeJson(value ?? string.Empty)).Append('"');
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}

/// <summary>
/// Minimal JSON serializer for simple dictionaries. Avoids requiring Newtonsoft in Editor assembly.
/// </summary>
internal static class MiniJson
{
    public static string Serialize(Dictionary<string, object> dict)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var kv in dict)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(EditorIssueReporter_Escape(kv.Key)).Append('"').Append(':');
            AppendValue(sb, kv.Value);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, object val)
    {
        switch (val)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                sb.Append('"').Append(EditorIssueReporter_Escape(s)).Append('"');
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int i:
                sb.Append(i);
                break;
            case long l:
                sb.Append(l);
                break;
            case float f:
                sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append('"').Append(EditorIssueReporter_Escape(val.ToString())).Append('"');
                break;
        }
    }

    private static string EditorIssueReporter_Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
#endif
