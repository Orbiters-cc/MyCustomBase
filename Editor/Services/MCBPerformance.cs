#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static partial class MCBPerformance
{
    const string PreferencePrefix = "MCB.Performance.";
    static MCBEditor owner;
    static MCBPerformanceProfile profile;
    static Task<MCBPerformanceProfile> calibration;
    static CancellationTokenSource cancellation;
    static readonly ConcurrentQueue<object> reports = new ConcurrentQueue<object>();
    static Task reportTask;
    static string apiUrl, token, preferenceKey, calibrationKey, environment, clientId, unityVersion, platform;
    static int cores, memoryGB;
    static double nextAttempt, nextTick;
    public static string Status { get; private set; } = "Waiting for MCB";
    public static bool Enabled { get => EditorPrefs.GetBool(PreferencePrefix + "Enabled", true); set { EditorPrefs.SetBool(PreferencePrefix + "Enabled", value); if (!value) PauseForeground(); } }
    public static bool ShareMeasurements { get => EditorPrefs.GetBool(PreferencePrefix + "Share", true); set { EditorPrefs.SetBool(PreferencePrefix + "Share", value); if (!value) while (reports.TryDequeue(out _)) { } } }

    static MCBPerformance()
    {
        EditorApplication.update += Tick;
        AssemblyReloadEvents.beforeAssemblyReload += PauseForeground;
        EditorApplication.quitting += PauseForeground;
    }

    public static void EnsureStarted(MCBEditor editor)
    {
        if (editor == null || string.IsNullOrWhiteSpace(editor.authToken) || Application.isBatchMode) return;
        owner = editor;
        string url = MCBUtils.getApiUrl().TrimEnd('/');
        token = editor.authToken;
        if (apiUrl == url && profile != null) return;
        PauseForeground(); apiUrl = url;
        while (reports.TryDequeue(out _)) { }
        unityVersion = Application.unityVersion; platform = Application.platform.ToString();
        cores = SystemInfo.processorCount; memoryGB = (SystemInfo.systemMemorySize + 1023) / 1024;
        environment = "mcc1-lz4-1.9.2-zstd-1.5.7-block4m-level9|" + unityVersion + "|" + platform + "|" + SystemInfo.processorType + "|" + cores + "|" + memoryGB;
        preferenceKey = PreferencePrefix + VersionManifest.ComputeStringHash(apiUrl + "|" + environment);
        try { profile = JsonConvert.DeserializeObject<MCBPerformanceProfile>(EditorPrefs.GetString(preferenceKey, "")); }
        catch { profile = null; }
        if (profile == null || profile.schema != MCBPerformanceProfile.Schema || profile.environment != environment)
            profile = new MCBPerformanceProfile { environment = environment };
        clientId = EditorPrefs.GetString(PreferencePrefix + "ClientId", "");
        if (!Guid.TryParse(clientId, out _)) { clientId = Guid.NewGuid().ToString(); EditorPrefs.SetString(PreferencePrefix + "ClientId", clientId); }
        nextAttempt = EditorApplication.timeSinceStartup + 2;
    }

    public static void PauseForeground()
    {
        cancellation?.Cancel();
        nextAttempt = EditorApplication.timeSinceStartup + 10;
    }

    public static MCBDeliveryDecision Choose(MCBDeliveryVariant[] variants)
    {
        bool current = Enabled && !Expired(profile?.cpuMeasuredUtc, 14) && !Expired(profile?.networkMeasuredUtc, 1);
        return MCBPerformanceModel.Choose(variants, current ? profile : null, MCBCompression.IsSupported);
    }

    public static void RecordDownload(MCBDeliveryDecision decision, long bytes, double milliseconds, bool success)
    {
        if (decision?.codec == null) return;
        if (success && profile != null && bytes > 0 && milliseconds > 0) {
            profile.network = new List<MCBTimingSample> { new MCBTimingSample { bytes = bytes, milliseconds = milliseconds } };
            profile.networkMeasuredUtc = DateTime.UtcNow.ToString("O");
            SaveProfile();
        }
        if (ShareMeasurements) reports.Enqueue(new { kind = "decision", schema = 1, clientId, unity = unityVersion,
            platform, cores, memoryGB, codec = decision.codec, calibrated = decision.calibrated,
            candidates = decision.candidates, bytes, milliseconds, success });
    }

    static void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now < nextTick) return;
        nextTick = now + 0.5;
        bool busy = owner == null || !owner.isAuthenticated || owner.isApplying || owner.isDownloading || owner.isSubmitting
            || EditorApplication.isPlayingOrWillChangePlaymode || MCBEditor.ShouldDeferBackgroundNetworkRefresh();
        if (busy) { cancellation?.Cancel(); return; }
        if (!Enabled) cancellation?.Cancel();
        if (calibration != null && calibration.IsCompleted) {
            if (calibration.Status == TaskStatus.RanToCompletion && calibrationKey == preferenceKey) {
                var result = calibration.Result;
                // A foreground download can complete while a cancelled probe is unwinding.
                if (string.CompareOrdinal(profile?.networkMeasuredUtc, result.networkMeasuredUtc) > 0) {
                    result.network = profile.network; result.networkMeasuredUtc = profile.networkMeasuredUtc;
                }
                profile = result; SaveProfile();
                Status = Expired(profile.cpuMeasuredUtc, 14) || Expired(profile.networkMeasuredUtc, 1)
                    ? "Calibration deferred; choosing the smallest download" : "Ready";
            }
            else { _ = calibration.Exception; Status = "Calibration deferred"; }
            calibration = null; cancellation?.Dispose(); cancellation = null;
        }
        if (Enabled && calibration == null && now >= nextAttempt && (Expired(profile?.cpuMeasuredUtc, 14) || Expired(profile?.networkMeasuredUtc, 1))) {
            nextAttempt = now + 15 * 60;
            cancellation = new CancellationTokenSource();
            calibrationKey = preferenceKey;
            var copy = JsonConvert.DeserializeObject<MCBPerformanceProfile>(JsonConvert.SerializeObject(profile));
            calibration = CalibrateAsync(copy, cancellation.Token);
        }
        if (calibration == null && (reportTask == null || reportTask.IsCompleted) && ShareMeasurements && reports.TryDequeue(out object report))
            reportTask = SendReportAsync(report);
        while (reports.Count > 20) reports.TryDequeue(out _);
    }

    static void SaveProfile()
    {
        if (profile != null && preferenceKey != null) EditorPrefs.SetString(preferenceKey, JsonConvert.SerializeObject(profile));
    }
    static bool Expired(string utc, int days) => !DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date)
        || DateTime.UtcNow - date.ToUniversalTime() > TimeSpan.FromDays(days);

    public static void Recalibrate()
    {
        PauseForeground();
        if (profile != null) { profile.cpuMeasuredUtc = null; profile.networkMeasuredUtc = null; SaveProfile(); }
        nextAttempt = EditorApplication.timeSinceStartup + 1;
    }
}
#endif
