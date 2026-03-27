#if UNITY_EDITOR
using System;
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
    public bool AllowLegacyTls;
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

[InitializeOnLoad]
public static class MCBConnectivityMonitor
{
    private const string SessionKey = "MCB.PackageConnectivity.Started";
    private const string ReachabilityKey = "MCB.PackageConnectivity.Reachable";
    private const string FailureReportKey = "MCB.PackageConnectivity.Report";
    private const string LastUrlKey = "MCB.PackageConnectivity.Url";

    public static bool HasCompleted { get; private set; }
    public static bool IsRunning { get; private set; }
    public static bool CanReachServer { get; private set; }
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
        EnsureCheckStarted(AuthenticationService.GetAuth()?.token);
    }

    public static void EnsureCheckStarted(string authToken = null)
    {
        if (IsRunning)
        {
            return;
        }

        if (HasCompleted && !string.IsNullOrEmpty(LastCheckedUrl))
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
        LastCheckedUrl = null;
        SessionState.SetBool(SessionKey, false);
        SessionState.SetBool(ReachabilityKey, false);
        SessionState.SetString(FailureReportKey, string.Empty);
        SessionState.SetString(LastUrlKey, string.Empty);
        _ = RunCheckAsync(authToken);
    }

    private static async Task RunCheckAsync(string authToken)
    {
        IsRunning = true;
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
            StatusChanged?.Invoke();
        }
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

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
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
            request.timeout = 25;

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
        var psResult = await RunPowerShellProbeAsync(url, 25);
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

        sb.AppendLine("=== TCP :443 ===");
        try
        {
            using (var tcp = new TcpClient())
            {
                var connectTask = tcp.ConnectAsync(uri.Host, 443);
                var timeoutTask = Task.Delay(8000);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                sb.AppendLine(completedTask == connectTask && tcp.Connected ? "TCP connect: OK" : "TCP connect: TIMEOUT/FAIL");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("TCP error: " + ex.Message);
        }
        sb.AppendLine();

        sb.AppendLine("=== TLS Handshake (SslStream) ===");
        try
        {
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(uri.Host, 443);
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

        sb.AppendLine("=== Plain HTTP (neverssl.com) ===");
        try
        {
            using (var request = UnityWebRequest.Get("http://neverssl.com/"))
            {
                request.timeout = 10;
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

        if (options.AllowLegacyTls)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls | SecurityProtocolType.Tls11; } catch { }
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

            if (options.AllowLegacyTls)
            {
                protocols |= System.Security.Authentication.SslProtocols.Tls11 | System.Security.Authentication.SslProtocols.Tls;
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
                            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11 -bor [Net.SecurityProtocolType]::Tls;" +
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
