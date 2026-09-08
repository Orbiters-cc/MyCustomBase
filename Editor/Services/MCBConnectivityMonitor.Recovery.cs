#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEditor;

public static partial class MCBConnectivityMonitor
{
    private static double nextRecoveryAt;
    private static int recoveryAttempts;
    private static bool recoveryRunning;

    internal static double RecoveryDelay(int attempts) => Math.Min(60, 2 * Math.Pow(2, Math.Min(5, Math.Max(0, attempts))));

    private static void PollRecovery()
    {
        if (!HasOfflineIncident() || recoveryRunning || IsRunning || EditorApplication.isCompiling || EditorApplication.isPlaying)
            return;
        double now = EditorApplication.timeSinceStartup;
        if (nextRecoveryAt == 0) nextRecoveryAt = now + RecoveryDelay(recoveryAttempts);
        if (now < nextRecoveryAt) return;
        nextRecoveryAt = now + RecoveryDelay(++recoveryAttempts);
        _ = ProbeRecoveryAsync();
    }

    private static async Task ProbeRecoveryAsync()
    {
        recoveryRunning = true;
        int generation = failureReportGeneration;
        string token = AuthenticationService.GetAuth()?.token;
        string url = ConnectivityDiagnosticsService.BuildConnectivityCheckUrl(token);
        try
        {
            // Only a lightweight probe: retain the offline report and local versions while retrying.
            var result = await ConnectivityDiagnosticsService.RunUnityWebRequestProbeAsync(url, new ConnectivityDiagnosticsOptions());
            if (generation != failureReportGeneration || url != ConnectivityDiagnosticsService.BuildConnectivityCheckUrl(AuthenticationService.GetAuth()?.token)) return;
            if (!result.ReachedServer) return;
            MarkServerReachable();
            MCBPackageVersionService.EnsureCheckStarted(token, true);
        }
        catch { /* Next bounded probe retries without rebuilding the diagnostic report. */ }
        finally { recoveryRunning = false; }
    }
}
#endif
