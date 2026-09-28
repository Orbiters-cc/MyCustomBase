#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Opt-in, bounded reporting of diagnostics emitted by MCB's own logger.</summary>
[InitializeOnLoad]
public static class EditorIssueReporter
{
    private const string SharePrefKey = "MCB_ShareIssueLogs";
    private const string MinSeverityPrefKey = "MCB_MinIssueSeverity";
    public enum SeverityLevel { Info = 1, Warn = 2, Error = 3, Fatal = 4 }
    private static readonly IssueReportBuffer Buffer = new IssueReportBuffer();
    private static volatile bool listening;
    private static volatile int minSeverity = 2;
    private static UnityWebRequest activeRequest;
    private static bool sending;

    static EditorIssueReporter()
    {
        EditorApplication.delayCall += RefreshListener;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
    }

    public static bool ShareIssueLogs
    {
        get => EditorPrefs.GetBool(SharePrefKey, false);
        set { EditorPrefs.SetBool(SharePrefKey, value); RefreshListener(); }
    }

    public static int MinSeverityLevel
    {
        get => Mathf.Clamp(EditorPrefs.GetInt(MinSeverityPrefKey, 2), 1, 4);
        set { EditorPrefs.SetInt(MinSeverityPrefKey, Mathf.Clamp(value, 1, 4)); minSeverity = MinSeverityLevel; }
    }

    public static void RefreshListener()
    {
        Stop();
        minSeverity = MinSeverityLevel;
        if (!ShareIssueLogs) return;
        listening = true;
        MCBLogger.IssueLogged += OnIssue;
        EditorApplication.update += Pump;
    }

    private static void Stop()
    {
        listening = false;
        MCBLogger.IssueLogged -= OnIssue;
        EditorApplication.update -= Pump;
        Buffer.Clear();
        activeRequest?.Abort();
    }

    private static void OnIssue(string message, string stack, LogType type)
    {
        int level = type == LogType.Exception ? 4 : type == LogType.Error || type == LogType.Assert ? 3 : type == LogType.Warning ? 2 : 1;
        if (!listening || level < minSeverity) return;
        // Logger calls may come from worker threads: no Unity APIs or EditorPrefs here.
        Buffer.Enqueue(new IssueReportBuffer.Report
        {
            Message = IssueReportBuffer.Redact(message, 4000),
            Stack = IssueReportBuffer.Redact(stack, 12000),
            Severity = new[] { "INFO", "WARN", "ERROR", "FATAL" }[level - 1],
            ErrorType = type.ToString()
        }, DateTime.UtcNow);
        if (!listening) Buffer.Clear();
    }

    private static void Pump()
    {
        if (!listening || sending || (MCBConnectivityMonitor.HasCompleted && !MCBConnectivityMonitor.CanReachServer)) return;
        if (Buffer.TryTake(DateTime.UtcNow, out var report)) Send(report);
    }

    private static async void Send(IssueReportBuffer.Report report)
    {
        sending = true;
        try
        {
            if (!listening) return;
            var auth = AuthenticationService.GetAuth();
            if (!string.IsNullOrEmpty(auth?.token))
            {
                report.Message = report.Message.Replace(auth.token, "[redacted]");
                report.Stack = report.Stack.Replace(auth.token, "[redacted]");
            }
            string payload = JsonConvert.SerializeObject(new
            {
                message = report.Message, severity = report.Severity, errorType = report.ErrorType,
                tool = "MCB", stackTrace = report.Stack, userAgent = "Unity " + Application.unityVersion,
                browserInfo = JsonConvert.SerializeObject(new { unityVersion = Application.unityVersion, platform = Application.platform.ToString() })
            });
            string endpoint = MCBUtils.getApiUrl(scope: "bugs");
            using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                activeRequest = request;
                request.timeout = 15;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(auth?.token)) request.SetRequestHeader("Authorization", "Bearer " + auth.token);
                await MCBManagedRequest.SendUnityWebRequestAsync(request, endpoint, MCBRequestPolicy.LocalOnly("Send issue report"));
            }
        }
        catch (Exception) { /* Reporting failures must not generate more reports. */ }
        finally { activeRequest = null; sending = false; }
    }
}

internal sealed class IssueReportBuffer
{
    internal sealed class Report { public string Message, Stack, Severity, ErrorType; }
    internal const int QueueLimit = 32, SignatureLimit = 128;
    private readonly Queue<Report> pending = new Queue<Report>();
    private readonly Dictionary<string, DateTime> signatures = new Dictionary<string, DateTime>();
    private readonly object gate = new object();
    private DateTime nextSend;
    internal int PendingCount { get { lock (gate) return pending.Count; } }
    internal int SignatureCount { get { lock (gate) return signatures.Count; } }

    internal bool Enqueue(Report report, DateTime now)
    {
        lock (gate)
        {
            foreach (var key in signatures.Where(p => now - p.Value >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) signatures.Remove(key);
            string signature = report.Severity + "|" + report.Message;
            if (signatures.ContainsKey(signature) || pending.Count >= QueueLimit) return false;
            if (signatures.Count >= SignatureLimit) signatures.Remove(signatures.OrderBy(p => p.Value).First().Key);
            signatures[signature] = now;
            pending.Enqueue(report);
            return true;
        }
    }

    internal bool TryTake(DateTime now, out Report report)
    {
        lock (gate)
        {
            report = null;
            if (pending.Count == 0 || now < nextSend) return false;
            nextSend = now.AddSeconds(10); // Six starts per minute, one in flight.
            report = pending.Dequeue();
            return true;
        }
    }

    internal void Clear() { lock (gate) { pending.Clear(); signatures.Clear(); } }

    internal static string Redact(string input, int limit)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        string text = input.Substring(0, Math.Min(input.Length, limit));
        try
        {
            foreach (string pattern in RedactionPatterns)
                text = Regex.Replace(text, pattern, "[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return text;
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic omitted]"; }
    }

    private static readonly string[] RedactionPatterns =
    {
        @"(?im)\b(?:authorization|cookie|set-cookie)\s*:\s*[^\r\n]+",
        @"https?://[^\s<>""']+",
        @"(?i)\b(?:bearer|basic)\s+[A-Za-z0-9+/=_\-.]+",
        @"(?i)[""']?(?:password|passwd|secret|token|api[_-]?key|authorization|cookie|access[_-]?token|refresh[_-]?token)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;}]+)",
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        @"[A-Za-z]:[\\/][^\r\n""<>|]+|\\\\[^\r\n""<>|]+|(?<![\w:])/(?:[^\s/]+/)+[^\s]*",
        @"\b(?:Assets|Packages)[\\/][^\r\n""<>|]+",
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"
    };
}
#endif
