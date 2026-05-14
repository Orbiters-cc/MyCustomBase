#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public sealed class ConnectivityDiagnosticsOptions
{
    public bool ForceTls12 = true;
    public bool IgnoreCertificateErrors;
}

public sealed class ConnectivityProbeResult
{
    public bool ReachedServer;
    public long StatusCode;
    public string ReasonPhrase;
    public string Error;

    public string ToRichTextStatus()
    {
        return ReachedServer
            ? "<b><color=green>success</color></b>"
            : "<b><color=red>fail</color></b>";
    }

    public string ToSummaryLine(string label)
    {
        if (ReachedServer)
        {
            string codePart = StatusCode > 0 ? $" [{StatusCode}]" : string.Empty;
            string reasonPart = string.IsNullOrEmpty(ReasonPhrase) ? string.Empty : $" {ReasonPhrase}";
            return $"{label}: success{codePart}{reasonPart}";
        }

        return string.IsNullOrEmpty(Error)
            ? $"{label}: fail"
            : $"{label}: fail - {Error}";
    }
}

public enum MCBRequestFailureScope
{
    Backend,
    BackendResource,
    ExternalResource,
    LocalOnly,
    Diagnostics
}

public sealed class MCBRequestPolicy
{
    public string context;
    public MCBRequestFailureScope failureScope;
    public string warningKey;
    public string warningTitle;

    public static MCBRequestPolicy Backend(string context)
    {
        return new MCBRequestPolicy
        {
            context = context,
            failureScope = MCBRequestFailureScope.Backend
        };
    }

    public static MCBRequestPolicy ExternalResource(string context, string warningKey = null, string warningTitle = null)
    {
        return new MCBRequestPolicy
        {
            context = context,
            failureScope = MCBRequestFailureScope.ExternalResource,
            warningKey = warningKey,
            warningTitle = warningTitle
        };
    }

    public static MCBRequestPolicy BackendResource(string context, string warningKey = null, string warningTitle = null)
    {
        return new MCBRequestPolicy
        {
            context = context,
            failureScope = MCBRequestFailureScope.BackendResource,
            warningKey = warningKey,
            warningTitle = warningTitle
        };
    }

    public static MCBRequestPolicy LocalOnly(string context)
    {
        return new MCBRequestPolicy
        {
            context = context,
            failureScope = MCBRequestFailureScope.LocalOnly
        };
    }

    public static MCBRequestPolicy Diagnostics(string context)
    {
        return new MCBRequestPolicy
        {
            context = context,
            failureScope = MCBRequestFailureScope.Diagnostics
        };
    }
}

public sealed class MCBRequestWarning
{
    public string key;
    public string title;
    public string message;
    public string context;
    public string url;
    public long statusCode;
    public string error;
    public DateTime timestampUtc;
}

public static class MCBManagedRequest
{
    public static IEnumerator SendUnityWebRequest(UnityWebRequest request, string requestUrl, MCBRequestPolicy policy)
    {
        if (request == null)
        {
            yield break;
        }

        UnityWebRequestAsyncOperation operation;
        try
        {
            operation = request.SendWebRequest();
        }
        catch (Exception ex)
        {
            MCBConnectivityMonitor.ReportManagedException(requestUrl, ex, policy);
            yield break;
        }

        yield return operation;
        MCBConnectivityMonitor.ReportManagedUnityWebRequest(request, requestUrl, policy);
    }

    public static async Task SendUnityWebRequestAsync(UnityWebRequest request, string requestUrl, MCBRequestPolicy policy)
    {
        if (request == null)
        {
            return;
        }

        try
        {
            await request.SendWebRequest();
        }
        catch (Exception ex)
        {
            MCBConnectivityMonitor.ReportManagedException(requestUrl, ex, policy);
            return;
        }

        MCBConnectivityMonitor.ReportManagedUnityWebRequest(request, requestUrl, policy);
    }

    public static void ReportHttpResponse(HttpResponseMessage response, string requestUrl, MCBRequestPolicy policy)
    {
        MCBConnectivityMonitor.ReportManagedHttpResponse(response, requestUrl, policy);
    }

    public static void ReportException(string requestUrl, Exception exception, MCBRequestPolicy policy)
    {
        MCBConnectivityMonitor.ReportManagedException(requestUrl, exception, policy);
    }

    public static MCBRequestPolicy ResourcePolicyForUrl(string url, string context, string warningKey = null, string warningTitle = null)
    {
        return IsBackendUrl(url)
            ? MCBRequestPolicy.BackendResource(context, warningKey, warningTitle)
            : MCBRequestPolicy.ExternalResource(context, warningKey, warningTitle);
    }

    public static bool IsBackendUrl(string url)
    {
        return SameOrigin(url, MCBUtils.getApiUrl(string.Empty)) ||
               SameOrigin(url, MCBUtils.getApiUrl("assets"));
    }

    private static bool SameOrigin(string url, string rootUrl)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(rootUrl))
        {
            return false;
        }

        Uri uri;
        Uri root;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
            !Uri.TryCreate(rootUrl, UriKind.Absolute, out root))
        {
            return false;
        }

        return string.Equals(uri.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == root.Port;
    }
}

[InitializeOnLoad]
public static class MCBConnectivityMonitor
{
    private const string SessionKey = "MCB.PackageConnectivity.Started";
    private const string ReachabilityKey = "MCB.PackageConnectivity.Reachable";
    private const string FailureReportKey = "MCB.PackageConnectivity.Report";
    private const string LastUrlKey = "MCB.PackageConnectivity.Url";
    private static int failureReportGeneration;
    private static bool failureDiagnosticsRunning;
    private static double failureDiagnosticsStartedAt;
    private static readonly Dictionary<string, MCBRequestWarning> RequestWarnings = new Dictionary<string, MCBRequestWarning>(StringComparer.Ordinal);

    public static bool HasCompleted { get; private set; }
    public static bool IsRunning { get; private set; }
    public static bool CanReachServer { get; private set; }
    public static bool IsBuildingFailureReport => failureDiagnosticsRunning || IsRunning;
    public static double FailureReportBuildStartedAt => failureDiagnosticsStartedAt;
    public static string FailureReport { get; private set; }
    public static string LastCheckedUrl { get; private set; }

    public static event Action StatusChanged;

    static MCBConnectivityMonitor()
    {
        HasCompleted = SessionState.GetBool(SessionKey, false);
        CanReachServer = SessionState.GetBool(ReachabilityKey, false);
        FailureReport = SessionState.GetString(FailureReportKey, string.Empty);
        LastCheckedUrl = SessionState.GetString(LastUrlKey, string.Empty);
        EditorApplication.delayCall += OnInitialDelayCall;
    }

    private static void OnInitialDelayCall()
    {
        EnsureCheckStarted(AuthenticationService.GetAuth()?.token, force: true);
    }

    public static void EnsureCheckStarted(string authToken = null, bool force = false)
    {
        if (IsRunning)
        {
            return;
        }

        if (HasOfflineIncident())
        {
            return;
        }

        if (!force && HasCompleted && !string.IsNullOrEmpty(LastCheckedUrl))
        {
            return;
        }

        _ = RunCheckAsync(authToken);
    }

    public static void Retry(string authToken = null)
    {
        if (IsRunning)
        {
            return;
        }

        HasCompleted = false;
        CanReachServer = false;
        FailureReport = null;
        failureDiagnosticsRunning = false;
        failureDiagnosticsStartedAt = 0d;
        LastCheckedUrl = null;
        SessionState.SetBool(SessionKey, false);
        SessionState.SetBool(ReachabilityKey, false);
        SessionState.SetString(FailureReportKey, string.Empty);
        SessionState.SetString(LastUrlKey, string.Empty);
        _ = RunCheckAsync(authToken);
    }

    public static void MarkServerReachable()
    {
        failureReportGeneration++;
        bool changed = !HasCompleted || !CanReachServer || !string.IsNullOrEmpty(FailureReport);

        HasCompleted = true;
        CanReachServer = true;
        FailureReport = null;
        failureDiagnosticsRunning = false;
        failureDiagnosticsStartedAt = 0d;
        SessionState.SetBool(SessionKey, true);
        SessionState.SetBool(ReachabilityKey, true);
        SessionState.SetString(FailureReportKey, string.Empty);

        if (changed)
        {
            StatusChanged?.Invoke();
        }
    }

    public static List<MCBRequestWarning> GetRequestWarnings()
    {
        return new List<MCBRequestWarning>(RequestWarnings.Values);
    }

    public static bool HasRequestWarning(string key)
    {
        return !string.IsNullOrEmpty(key) && RequestWarnings.ContainsKey(key);
    }

    public static void ClearRequestWarning(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (RequestWarnings.Remove(key))
        {
            StatusChanged?.Invoke();
        }
    }

    public static void ClearAllRequestWarnings()
    {
        if (RequestWarnings.Count == 0)
        {
            return;
        }

        RequestWarnings.Clear();
        StatusChanged?.Invoke();
    }

    public static void ReportManagedUnityWebRequest(UnityWebRequest request, string requestUrl, MCBRequestPolicy policy)
    {
        policy = policy ?? MCBRequestPolicy.Backend(null);
        if (policy.failureScope == MCBRequestFailureScope.Diagnostics || policy.failureScope == MCBRequestFailureScope.LocalOnly)
        {
            return;
        }

        bool success = IsUnityWebRequestSuccess(request);
        if (policy.failureScope == MCBRequestFailureScope.Backend)
        {
            if (success || (request != null && !IsConnectivityFailure(request) && request.responseCode > 0))
            {
                MarkServerReachable();
                return;
            }

            string details = BuildFailedRequestDetails(policy.context, requestUrl, request != null ? request.responseCode : 0, request != null ? request.error : null);
            ReportConnectivityFailure(requestUrl, details);
            return;
        }

        if (policy.failureScope == MCBRequestFailureScope.BackendResource)
        {
            if (success)
            {
                MarkServerReachable();
                ClearRequestWarning(BuildWarningKey(policy, requestUrl));
                return;
            }

            if (request != null && !IsConnectivityFailure(request) && request.responseCode > 0)
            {
                MarkServerReachable();
                ReportRequestWarning(policy, requestUrl, request.responseCode, request.error);
                return;
            }

            string details = BuildFailedRequestDetails(policy.context, requestUrl, request != null ? request.responseCode : 0, request != null ? request.error : null);
            ReportConnectivityFailure(requestUrl, details);
            return;
        }

        if (success)
        {
            ClearRequestWarning(BuildWarningKey(policy, requestUrl));
            return;
        }

        ReportRequestWarning(policy, requestUrl, request != null ? request.responseCode : 0, request != null ? request.error : null);
    }

    public static void ReportManagedHttpResponse(HttpResponseMessage response, string requestUrl, MCBRequestPolicy policy)
    {
        policy = policy ?? MCBRequestPolicy.Backend(null);
        if (response == null ||
            policy.failureScope == MCBRequestFailureScope.Diagnostics ||
            policy.failureScope == MCBRequestFailureScope.LocalOnly)
        {
            return;
        }

        long statusCode = (long)response.StatusCode;
        if (policy.failureScope == MCBRequestFailureScope.Backend)
        {
            MarkServerReachable();
            return;
        }

        if (policy.failureScope == MCBRequestFailureScope.BackendResource)
        {
            MarkServerReachable();
            if (response.IsSuccessStatusCode)
            {
                ClearRequestWarning(BuildWarningKey(policy, requestUrl));
            }
            else
            {
                ReportRequestWarning(policy, requestUrl, statusCode, response.ReasonPhrase);
            }
            return;
        }

        if (response.IsSuccessStatusCode)
        {
            ClearRequestWarning(BuildWarningKey(policy, requestUrl));
        }
        else
        {
            ReportRequestWarning(policy, requestUrl, statusCode, response.ReasonPhrase);
        }
    }

    public static void ReportManagedException(string requestUrl, Exception exception, MCBRequestPolicy policy)
    {
        if (exception == null)
        {
            return;
        }

        policy = policy ?? MCBRequestPolicy.Backend(null);
        if (policy.failureScope == MCBRequestFailureScope.Diagnostics || policy.failureScope == MCBRequestFailureScope.LocalOnly)
        {
            return;
        }

        if (policy.failureScope == MCBRequestFailureScope.Backend ||
            policy.failureScope == MCBRequestFailureScope.BackendResource)
        {
            string details = BuildFailedRequestDetails(policy.context, requestUrl, 0, exception.Message);
            ReportConnectivityFailure(requestUrl, details);
            return;
        }

        ReportRequestWarning(policy, requestUrl, 0, exception.Message);
    }

    public static bool IsConnectivityFailure(UnityWebRequest request)
    {
        if (request == null)
        {
            return false;
        }

#if UNITY_2020_2_OR_NEWER
        return request.result == UnityWebRequest.Result.ConnectionError || request.responseCode == 0;
#else
        return request.isNetworkError || request.responseCode == 0;
#endif
    }

    private static bool IsUnityWebRequestSuccess(UnityWebRequest request)
    {
        if (request == null)
        {
            return false;
        }

#if UNITY_2020_2_OR_NEWER
        return request.result == UnityWebRequest.Result.Success;
#else
        return !request.isNetworkError && !request.isHttpError;
#endif
    }

    private static void ReportRequestWarning(MCBRequestPolicy policy, string requestUrl, long responseCode, string error)
    {
        string key = BuildWarningKey(policy, requestUrl);
        RequestWarnings[key] = new MCBRequestWarning
        {
            key = key,
            title = string.IsNullOrWhiteSpace(policy.warningTitle) ? "Non-critical request failed" : policy.warningTitle,
            message = BuildWarningMessage(policy.context, requestUrl, responseCode, error),
            context = policy.context,
            url = requestUrl,
            statusCode = responseCode,
            error = error,
            timestampUtc = DateTime.UtcNow
        };
        StatusChanged?.Invoke();
    }

    private static string BuildWarningKey(MCBRequestPolicy policy, string requestUrl)
    {
        if (policy != null && !string.IsNullOrWhiteSpace(policy.warningKey))
        {
            return policy.warningKey;
        }

        string context = policy != null ? policy.context : null;
        return (context ?? "request") + "|" + (requestUrl ?? string.Empty);
    }

    private static string BuildWarningMessage(string context, string requestUrl, long responseCode, string error)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.Append(context);
        }
        else
        {
            sb.Append("A non-critical network request failed");
        }

        if (responseCode > 0 || !string.IsNullOrWhiteSpace(error))
        {
            sb.Append(": ");
            if (responseCode > 0)
            {
                sb.Append("HTTP ").Append(responseCode);
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                if (responseCode > 0)
                {
                    sb.Append(" ");
                }
                sb.Append(error);
            }
        }

        if (!string.IsNullOrWhiteSpace(requestUrl))
        {
            sb.Append("\nURL: ").Append(requestUrl);
        }

        return sb.ToString();
    }

    private static void ReportConnectivityFailure(string requestUrl, string details)
    {
        string diagnosticsUrl = ConnectivityDiagnosticsService.BuildConnectivityCheckUrl(AuthenticationService.GetAuth()?.token);
        if (string.IsNullOrWhiteSpace(diagnosticsUrl))
        {
            diagnosticsUrl = requestUrl;
        }

        if (HasOfflineIncident())
        {
            return;
        }

        if (IsRunning || failureDiagnosticsRunning)
        {
            HasCompleted = true;
            CanReachServer = false;
            LastCheckedUrl = diagnosticsUrl;
            FailureReport = string.IsNullOrWhiteSpace(details)
                ? "A network request failed before reaching the MCB server."
                : details;
            SessionState.SetBool(SessionKey, true);
            SessionState.SetBool(ReachabilityKey, false);
            SessionState.SetString(LastUrlKey, LastCheckedUrl ?? string.Empty);
            SessionState.SetString(FailureReportKey, FailureReport ?? string.Empty);
            StatusChanged?.Invoke();
            return;
        }

        int generation = ++failureReportGeneration;
        failureDiagnosticsRunning = true;
        failureDiagnosticsStartedAt = EditorApplication.timeSinceStartup;
        HasCompleted = true;
        CanReachServer = false;
        LastCheckedUrl = diagnosticsUrl;
        FailureReport = string.IsNullOrWhiteSpace(details)
            ? "A network request failed before reaching the MCB server."
            : details;
        SessionState.SetBool(SessionKey, true);
        SessionState.SetBool(ReachabilityKey, false);
        SessionState.SetString(LastUrlKey, LastCheckedUrl ?? string.Empty);
        SessionState.SetString(FailureReportKey, FailureReport ?? string.Empty);
        StatusChanged?.Invoke();

        _ = BuildFailureReportAsync(generation, diagnosticsUrl, details);
    }

    private static async Task BuildFailureReportAsync(int generation, string diagnosticsUrl, string details)
    {
        try
        {
            string report = await ConnectivityDiagnosticsService.BuildFullReportAsync(diagnosticsUrl, new ConnectivityDiagnosticsOptions());
            if (generation != failureReportGeneration || CanReachServer)
            {
                return;
            }

            failureDiagnosticsRunning = false;
            failureDiagnosticsStartedAt = 0d;
            FailureReport = string.IsNullOrWhiteSpace(details)
                ? report
                : details + "\n\n" + report;
            SessionState.SetString(FailureReportKey, FailureReport ?? string.Empty);
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            if (generation != failureReportGeneration || CanReachServer)
            {
                return;
            }

            failureDiagnosticsRunning = false;
            failureDiagnosticsStartedAt = 0d;
            FailureReport = (string.IsNullOrWhiteSpace(details) ? string.Empty : details + "\n\n") +
                            "Connectivity diagnostics failed unexpectedly.\n" + ex;
            SessionState.SetString(FailureReportKey, FailureReport);
            StatusChanged?.Invoke();
        }
        finally
        {
            if (generation == failureReportGeneration)
            {
                failureDiagnosticsRunning = false;
                failureDiagnosticsStartedAt = 0d;
            }
        }
    }

    private static string BuildFailedRequestDetails(string context, string requestUrl, long responseCode, string error)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A network request failed before reaching the MCB server.");
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine("Request: " + context);
        }
        if (!string.IsNullOrWhiteSpace(requestUrl))
        {
            sb.AppendLine("Failed URL: " + requestUrl);
        }
        sb.AppendLine("HTTP: " + responseCode + " " + (error ?? string.Empty));
        return sb.ToString().TrimEnd();
    }

    private static async Task RunCheckAsync(string authToken)
    {
        int generation = ++failureReportGeneration;
        IsRunning = true;
        failureDiagnosticsRunning = false;
        failureDiagnosticsStartedAt = EditorApplication.timeSinceStartup;
        CanReachServer = false;
        FailureReport = null;
        LastCheckedUrl = ConnectivityDiagnosticsService.BuildConnectivityCheckUrl(authToken);
        SessionState.SetString(LastUrlKey, LastCheckedUrl ?? string.Empty);
        StatusChanged?.Invoke();

        try
        {
            var options = new ConnectivityDiagnosticsOptions();
            var result = await ConnectivityDiagnosticsService.RunUnityWebRequestProbeAsync(LastCheckedUrl, options);
            CanReachServer = result.ReachedServer;

            if (!result.ReachedServer)
            {
                FailureReport = await ConnectivityDiagnosticsService.BuildFullReportAsync(LastCheckedUrl, options);
                if (generation != failureReportGeneration)
                {
                    return;
                }
            }
            else
            {
                FailureReport = null;
            }

            HasCompleted = true;
            SessionState.SetBool(SessionKey, true);
            SessionState.SetBool(ReachabilityKey, CanReachServer);
            SessionState.SetString(FailureReportKey, FailureReport ?? string.Empty);
        }
        catch (Exception ex)
        {
            FailureReport = "Connectivity monitor failed unexpectedly.\n" + ex;
            HasCompleted = true;
            SessionState.SetBool(SessionKey, true);
            SessionState.SetBool(ReachabilityKey, false);
            SessionState.SetString(FailureReportKey, FailureReport);
        }
        finally
        {
            IsRunning = false;
            failureDiagnosticsRunning = false;
            failureDiagnosticsStartedAt = 0d;
            StatusChanged?.Invoke();
        }
    }

    private static bool HasOfflineIncident()
    {
        return HasCompleted &&
               !CanReachServer &&
               !string.IsNullOrEmpty(FailureReport);
    }
}

public static class ConnectivityDiagnosticsService
{
    public static string BuildConnectivityCheckUrl(string authToken = null)
    {
        string url = MCBUtils.getApiUrl() + MCBUtils.CHECK_CONNECTION_ENDPOINT;
        return string.IsNullOrEmpty(authToken)
            ? url
            : url + "?t=" + Uri.EscapeDataString(authToken);
    }

    public static async Task<ConnectivityProbeResult> RunHttpClientProbeAsync(string url, ConnectivityDiagnosticsOptions options)
    {
        try
        {
            ApplySecurityProtocols(options);

            using (var handler = new HttpClientHandler())
            {
                try
                {
                    var proxy = WebRequest.DefaultWebProxy;
                    if (proxy != null)
                    {
                        proxy.Credentials = CredentialCache.DefaultCredentials;
                        handler.Proxy = proxy;
                        handler.UseProxy = true;
                    }

                    handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    handler.PreAuthenticate = true;
#if !UNITY_WEBGL
                    handler.UseDefaultCredentials = true;
#endif

                    if (options != null && options.IgnoreCertificateErrors)
                    {
#if UNITY_2020_2_OR_NEWER || UNITY_2019_4_OR_NEWER
                        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
#endif
                    }
                }
                catch
                {
                }

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (var client = new HttpClient(handler))
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    return new ConnectivityProbeResult
                    {
                        ReachedServer = true,
                        StatusCode = (long)response.StatusCode,
                        ReasonPhrase = response.ReasonPhrase
                    };
                }
            }
        }
        catch (Exception ex)
        {
            return new ConnectivityProbeResult
            {
                ReachedServer = false,
                Error = ex.Message
            };
        }
    }

    public static async Task<ConnectivityProbeResult> RunUnityWebRequestProbeAsync(string url, ConnectivityDiagnosticsOptions options)
    {
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = 5;

            if (options != null && options.IgnoreCertificateErrors)
            {
                request.certificateHandler = new ConnectivityBypassCertificateHandler();
                request.disposeCertificateHandlerOnDispose = true;
            }

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                await Task.Yield();
            }

#if UNITY_2020_2_OR_NEWER
            bool reached = request.result == UnityWebRequest.Result.Success || request.result == UnityWebRequest.Result.ProtocolError;
#else
            bool reached = !request.isNetworkError;
#endif

            return new ConnectivityProbeResult
            {
                ReachedServer = reached,
                StatusCode = request.responseCode,
                ReasonPhrase = reached ? request.error : null,
                Error = reached ? null : request.error
            };
        }
    }

    public static async Task<ConnectivityProbeResult> RunPowerShellProbeAsync(string url, int timeoutSeconds)
    {
#if UNITY_EDITOR_WIN
        try
        {
            var result = await TryInvokeWebRequestViaPowerShell(url, timeoutSeconds);
            return new ConnectivityProbeResult
            {
                ReachedServer = result.ok,
                StatusCode = result.statusCode,
                ReasonPhrase = result.reasonPhrase,
                Error = result.error
            };
        }
        catch (Exception ex)
        {
            return new ConnectivityProbeResult
            {
                ReachedServer = false,
                Error = ex.Message
            };
        }
#else
        await Task.Yield();
        return new ConnectivityProbeResult
        {
            ReachedServer = false,
            Error = "PowerShell diagnostics are only available on Windows Editor."
        };
#endif
    }

    public static async Task<string> BuildFullReportAsync(string url, ConnectivityDiagnosticsOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp: " + DateTime.UtcNow.ToString("O"));
        sb.AppendLine("Target URL: " + url);
        sb.AppendLine();

        var httpResult = await RunHttpClientProbeAsync(url, options);
        var uwrResult = await RunUnityWebRequestProbeAsync(url, options);

        sb.AppendLine("=== Quick Tests ===");
        sb.AppendLine(httpResult.ToSummaryLine("HttpClient"));
        sb.AppendLine(uwrResult.ToSummaryLine("UnityWebRequest"));
#if UNITY_EDITOR_WIN
        var psResult = await RunPowerShellProbeAsync(url, 5);
        sb.AppendLine(psResult.ToSummaryLine("PowerShell/WinHTTP"));
#endif
        sb.AppendLine();
        sb.Append(await RunDeepDiagnosticsReportAsync(url, options));

        return sb.ToString();
    }

    public static async Task<string> RunDeepDiagnosticsReportAsync(string url, ConnectivityDiagnosticsOptions options)
    {
        var sb = new StringBuilder();
        Uri uri;

        try
        {
            uri = new Uri(url);
        }
        catch (Exception ex)
        {
            return "Invalid URL: " + ex.Message + "\n";
        }

        sb.AppendLine("=== Environment ===");
        sb.AppendLine("Unity: " + Application.unityVersion);
        sb.AppendLine("OS: " + SystemInfo.operatingSystem);
        sb.AppendLine("CLR: " + Environment.Version);
        sb.AppendLine("SecurityProtocol: " + ServicePointManager.SecurityProtocol);
        sb.AppendLine();

        sb.AppendLine("=== Proxy ===");
        try
        {
            var defaultProxy = WebRequest.DefaultWebProxy;
            sb.AppendLine("DefaultWebProxy: " + (defaultProxy != null ? "present" : "null"));
            if (defaultProxy != null)
            {
                Uri proxyUri = null;
                bool bypass = false;
                try { proxyUri = defaultProxy.GetProxy(uri); } catch { }
                try { bypass = defaultProxy.IsBypassed(uri); } catch { }
                sb.AppendLine("Proxy for URL: " + proxyUri);
                sb.AppendLine("IsBypassed: " + bypass);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Proxy check error: " + ex.Message);
        }
        sb.AppendLine();

        sb.AppendLine("=== DNS ===");
        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            if (addresses != null && addresses.Length > 0)
            {
                foreach (var address in addresses)
                {
                    sb.AppendLine(uri.Host + " -> " + address);
                }
            }
            else
            {
                sb.AppendLine("No addresses returned");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("DNS error: " + ex.Message);
        }
        sb.AppendLine();

        int targetPort = uri.IsDefaultPort
            ? (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ? 80 : 443)
            : uri.Port;

        sb.AppendLine("=== TCP :" + targetPort + " ===");
        try
        {
            using (var tcp = new TcpClient())
            {
                var connectTask = tcp.ConnectAsync(uri.Host, targetPort);
                var timeoutTask = Task.Delay(3000);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                sb.AppendLine(completedTask == connectTask && tcp.Connected ? "TCP connect: OK" : "TCP connect: TIMEOUT/FAIL");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("TCP error: " + ex.Message);
        }
        sb.AppendLine();

        if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("=== TLS Handshake (SslStream) ===");
            try
            {
                using (var tcp = new TcpClient())
                {
                    await tcp.ConnectAsync(uri.Host, targetPort);
                    using (var stream = tcp.GetStream())
                    using (var ssl = new SslStream(stream, false, (_, _, _, errors) => (options != null && options.IgnoreCertificateErrors) || errors == SslPolicyErrors.None))
                    {
                        var protocols = BuildSslProtocols(options);
                        try
                        {
                            await ssl.AuthenticateAsClientAsync(uri.Host, null, protocols, false);
                            sb.AppendLine("TLS handshake: OK (Protocol: " + protocols + ")");
                            try { sb.AppendLine("Cert: " + ssl.RemoteCertificate?.Subject); } catch { }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("TLS handshake error (" + protocols + "): " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("TLS setup error: " + ex.Message);
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("=== TLS Handshake (SslStream) ===");
            sb.AppendLine("Skipped for non-HTTPS target.");
            sb.AppendLine();
        }

        sb.AppendLine("=== Plain HTTP (neverssl.com) ===");
        try
        {
            using (var request = UnityWebRequest.Get("http://neverssl.com/"))
            {
                request.timeout = 5;
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

#if UNITY_2020_2_OR_NEWER
                bool reached = request.result == UnityWebRequest.Result.Success || request.result == UnityWebRequest.Result.ProtocolError;
#else
                bool reached = !request.isNetworkError;
#endif
                sb.AppendLine(reached ? "HTTP check: OK" : "HTTP check: FAIL (" + request.error + ")");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("HTTP check error: " + ex.Message);
        }

        return sb.ToString();
    }

    private static void ApplySecurityProtocols(ConnectivityDiagnosticsOptions options)
    {
        if (options == null)
        {
            return;
        }

        if (options.ForceTls12)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

    }

    private static System.Security.Authentication.SslProtocols BuildSslProtocols(ConnectivityDiagnosticsOptions options)
    {
        System.Security.Authentication.SslProtocols protocols = 0;

        if (options != null)
        {
            if (options.ForceTls12)
            {
                protocols |= System.Security.Authentication.SslProtocols.Tls12;
            }

        }

        return protocols == 0 ? System.Security.Authentication.SslProtocols.None : protocols;
    }

#if UNITY_EDITOR_WIN
    private static async Task<(bool ok, long statusCode, string reasonPhrase, string error)> TryInvokeWebRequestViaPowerShell(string url, int timeoutSeconds)
    {
        string tempScript = Path.Combine(Path.GetTempPath(), "upw_" + Guid.NewGuid().ToString("N") + ".ps1");

        try
        {
            string escapedUrl = url.Replace("'", "''");
            string script = "$ErrorActionPreference='Stop';" +
                            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
                            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12;" +
                            "try {" +
                            "  $r = Invoke-WebRequest -UseBasicParsing -Uri '" + escapedUrl + "' -TimeoutSec " + timeoutSeconds + ";" +
                            "  Write-Output ('HTTP_STATUS=' + [int]$r.StatusCode);" +
                            "  Write-Output ('HTTP_REASON=' + $r.StatusDescription);" +
                            "} catch [System.Net.WebException] {" +
                            "  if ($_.Exception.Response) {" +
                            "    $resp = $_.Exception.Response;" +
                            "    Write-Output ('HTTP_STATUS=' + [int]$resp.StatusCode);" +
                            "    Write-Output ('HTTP_REASON=' + $resp.StatusDescription);" +
                            "    exit 0;" +
                            "  }" +
                            "  throw" +
                            "}";
            File.WriteAllText(tempScript, script, Encoding.UTF8);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tempScript + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var finishedTask = Task.Run(() => process.WaitForExit(timeoutSeconds * 1000 + 5000));
                await Task.WhenAll(stdoutTask, stderrTask, finishedTask);

                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { }
                    return (false, 0, null, "PowerShell timed out");
                }

                if (process.ExitCode != 0)
                {
                    return (false, 0, null, "PowerShell exit " + process.ExitCode + ": " + (stderrTask.Result ?? string.Empty).Trim());
                }

                long statusCode = 0;
                string reasonPhrase = null;
                using (var reader = new StringReader(stdoutTask.Result ?? string.Empty))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("HTTP_STATUS=", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(line.Substring("HTTP_STATUS=".Length), out statusCode);
                        }
                        else if (line.StartsWith("HTTP_REASON=", StringComparison.OrdinalIgnoreCase))
                        {
                            reasonPhrase = line.Substring("HTTP_REASON=".Length);
                        }
                    }
                }

                return (true, statusCode, reasonPhrase, null);
            }
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }
#endif
}

internal sealed class ConnectivityBypassCertificateHandler : CertificateHandler
{
    protected override bool ValidateCertificate(byte[] certificateData)
    {
        return true;
    }
}
#endif
