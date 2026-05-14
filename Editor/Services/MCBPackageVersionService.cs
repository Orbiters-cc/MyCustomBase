#if UNITY_EDITOR
using System;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

[InitializeOnLoad]
public static class MCBPackageVersionService
{
    private const string MinorPopupSessionKey = "MCB.PackageVersion.MinorPopup";
    private const string MockEnabledPrefKey = "MCB.PackageVersion.MockEnabled";
    private const string MockJsonPrefKey = "MCB.PackageVersion.MockJson";
    private static readonly NetworkService networkService = new NetworkService();
    private static int lastKnownMCBCount = -1;
    private static bool isChecking;
    private static string lastRequestedToken;
    private static bool hasPendingForcedCheck;
    private static string pendingAuthToken;
    private static bool hierarchyScanQueued;

    public static PackageVersionStatus CurrentStatus { get; private set; }
    public static event Action StatusChanged;

    static MCBPackageVersionService()
    {
        CurrentStatus = PackageVersionStatus.CreateDefault(ReadCurrentPackageVersion());
        EditorApplication.delayCall += OnInitialDelayCall;
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }

    public static bool RequiresMajorUpdate
    {
        get { return CurrentStatus != null && CurrentStatus.requiresMajorUpdate; }
    }

    public static bool HasServerAccess(string authToken)
    {
        bool connectivityBlocked = MCBConnectivityMonitor.HasCompleted &&
                                   !MCBConnectivityMonitor.CanReachServer &&
                                   !string.IsNullOrEmpty(MCBConnectivityMonitor.FailureReport);
        return !string.IsNullOrEmpty(authToken) && !RequiresMajorUpdate && !connectivityBlocked;
    }

    public static bool IsMockEnabled
    {
        get
        {
            try { return EditorPrefs.GetBool(MockEnabledPrefKey, false); }
            catch { return false; }
        }
        set
        {
            try { EditorPrefs.SetBool(MockEnabledPrefKey, value); } catch { }
        }
    }

    public static string MockResponseJson
    {
        get
        {
            try { return EditorPrefs.GetString(MockJsonPrefKey, string.Empty); }
            catch { return string.Empty; }
        }
        set
        {
            try { EditorPrefs.SetString(MockJsonPrefKey, value ?? string.Empty); } catch { }
        }
    }

    public static void EnsureCheckStarted(string authToken, bool force = false)
    {
        if (isChecking)
        {
            if (force)
            {
                hasPendingForcedCheck = true;
                pendingAuthToken = authToken;
            }
            return;
        }

        if (!force &&
            CurrentStatus != null &&
            CurrentStatus.hasChecked &&
            string.Equals(lastRequestedToken, authToken ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        lastRequestedToken = authToken ?? string.Empty;
        _ = RunCheckAsync(authToken);
    }

    private static void OnInitialDelayCall()
    {
        lastKnownMCBCount = CountLoadedMCBComponents();
        if (lastKnownMCBCount > 0)
        {
            EnsureCheckStarted(AuthenticationService.GetAuth()?.token, true);
        }
    }

    private static void OnHierarchyChanged()
    {
        if (hierarchyScanQueued)
        {
            return;
        }

        hierarchyScanQueued = true;
        EditorApplication.delayCall += ProcessQueuedHierarchyChanged;
    }

    private static void ProcessQueuedHierarchyChanged()
    {
        hierarchyScanQueued = false;
        int currentCount = CountLoadedMCBComponents();
        bool addedComponent = lastKnownMCBCount >= 0 && currentCount > lastKnownMCBCount;
        lastKnownMCBCount = currentCount;

        if (currentCount <= 0)
        {
            return;
        }

        if (addedComponent)
        {
            EnsureCheckStarted(AuthenticationService.GetAuth()?.token, true);
        }
    }

    private static int CountLoadedMCBComponents()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll<MyCustomBase>();
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var component = all[i];
                if (component == null) continue;
                if (EditorUtility.IsPersistent(component)) continue;
                count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static async System.Threading.Tasks.Task RunCheckAsync(string authToken)
    {
        isChecking = true;
        PackageVersionStatus statusToPublish = PackageVersionStatus.CreateDefault(ReadCurrentPackageVersion());
        try
        {
            string currentVersion = ReadCurrentPackageVersion();
            NetworkService.CheckConnectionResponse response;

            if (IsMockEnabled && TryDeserializeResponse(MockResponseJson, out var mockedResponse))
            {
                response = mockedResponse;
            }
            else
            {
                response = await FetchRealResponseAsync(authToken);
                InitializeMockJsonIfNeeded(response);
            }

            statusToPublish = BuildStatus(currentVersion, response);
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[MCB] Package version check failed: {ex.Message}");
            statusToPublish = PackageVersionStatus.CreateDefault(ReadCurrentPackageVersion());
        }
        finally
        {
            EditorApplication.delayCall += () =>
            {
                CurrentStatus = statusToPublish;
                isChecking = false;
                MaybeShowMinorUpdatePopup(statusToPublish);
                StatusChanged?.Invoke();

                if (hasPendingForcedCheck)
                {
                    hasPendingForcedCheck = false;
                    string tokenToRetry = pendingAuthToken;
                    pendingAuthToken = null;
                    EnsureCheckStarted(tokenToRetry, true);
                }
            };
        }
    }

    private static PackageVersionStatus BuildStatus(string currentVersion, NetworkService.CheckConnectionResponse response)
    {
        var status = PackageVersionStatus.CreateDefault(currentVersion);
        if (response == null)
        {
            return status;
        }

        status.connectionState = string.IsNullOrEmpty(response.state) ? "disconnected" : response.state;
        status.latestVersion = response.latestVersion;
        status.updateMessage = response.updateMessage;
        status.hasChecked = true;

        if (!IsConnectedState(status.connectionState))
        {
            return status;
        }

        Version current = ParseVersion(currentVersion);
        Version latest = ParseVersion(status.latestVersion);
        if (current == null || latest == null)
        {
            return status;
        }

        status.hasMinorUpdate = latest.Major == current.Major && latest.Minor > current.Minor;
        status.requiresMajorUpdate = latest.Major > current.Major;
        return status;
    }

    private static bool IsConnectedState(string state)
    {
        return string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase)
               || string.Equals(state, "limited", StringComparison.OrdinalIgnoreCase);
    }

    private static void MaybeShowMinorUpdatePopup(PackageVersionStatus status)
    {
        if (status == null || !status.hasMinorUpdate || status.requiresMajorUpdate)
        {
            return;
        }

        string signature = $"{status.currentVersion}->{status.latestVersion}";
        if (string.Equals(SessionState.GetString(MinorPopupSessionKey, string.Empty), signature, StringComparison.Ordinal))
        {
            return;
        }

        SessionState.SetString(MinorPopupSessionKey, signature);
        EditorUtility.DisplayDialog(
            "MCB Update Available",
            $"A newer MCB package is available.\n\nCurrent version: {status.currentVersion}\nLatest version: {status.latestVersion}\n\nGo to VCC and update the \"MCB\" package.",
            "OK");
    }

    private static string ReadCurrentPackageVersion()
    {
        string packageJsonPath = GetPackageJsonPath();
        if (string.IsNullOrEmpty(packageJsonPath) || !File.Exists(packageJsonPath))
        {
            return "0.0.0";
        }

        try
        {
            var payload = JsonConvert.DeserializeObject<PackageJsonVersionPayload>(File.ReadAllText(packageJsonPath));
            return string.IsNullOrEmpty(payload?.version) ? "0.0.0" : payload.version;
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[MCB] Failed to read package version from package.json: {ex.Message}");
            return "0.0.0";
        }
    }

    private static string GetPackageJsonPath()
    {
        try
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(MyCustomBase).Assembly);
            if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
            {
                return Path.Combine(packageInfo.resolvedPath, "package.json");
            }
        }
        catch
        {
        }

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        if (string.IsNullOrEmpty(projectRoot))
        {
            return null;
        }

        string lowerCasePath = Path.Combine(projectRoot, "Packages", "orbiters.mcb", "package.json");
        if (File.Exists(lowerCasePath))
        {
            return lowerCasePath;
        }

        string displayCasePath = Path.Combine(projectRoot, "Packages", "MCB", "package.json");
        if (File.Exists(displayCasePath))
        {
            return displayCasePath;
        }

        return lowerCasePath;
    }

    private static Version ParseVersion(string versionText)
    {
        if (string.IsNullOrEmpty(versionText))
        {
            return null;
        }

        int dots = 0;
        for (int i = 0; i < versionText.Length; i++)
        {
            if (versionText[i] == '.') dots++;
        }

        string normalized = versionText;
        if (dots == 0) normalized += ".0.0";
        else if (dots == 1) normalized += ".0";

        Version parsed;
        return Version.TryParse(normalized, out parsed) ? parsed : null;
    }

    private sealed class PackageJsonVersionPayload
    {
        public string version;
    }

    public static bool TryApplyMock(string json, bool enabled, out string error)
    {
        error = null;
        if (enabled)
        {
            if (!TryDeserializeResponse(json, out _))
            {
                error = "The mocked JSON is invalid.";
                return false;
            }
        }

        MockResponseJson = json ?? string.Empty;
        IsMockEnabled = enabled;
        return true;
    }

    public static async System.Threading.Tasks.Task<(bool success, string json, string error)> FetchRealResponseJsonAsync(string authToken)
    {
        try
        {
            var response = await FetchRealResponseAsync(authToken);
            string serialized = SerializeResponse(response);
            if (!string.IsNullOrEmpty(serialized))
            {
                InitializeMockJsonIfNeeded(response);
            }
            return (true, serialized, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static async System.Threading.Tasks.Task<NetworkService.CheckConnectionResponse> FetchRealResponseAsync(string authToken)
    {
        string url = MCBUtils.getApiUrl() + MCBUtils.CHECK_CONNECTION_ENDPOINT;
        if (!string.IsNullOrEmpty(authToken))
        {
            url += "?t=" + Uri.EscapeDataString(authToken);
        }

        return await networkService.CheckConnectionDetailedAsync(url, authToken);
    }

    private static bool TryDeserializeResponse(string json, out NetworkService.CheckConnectionResponse response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            response = JsonConvert.DeserializeObject<NetworkService.CheckConnectionResponse>(json);
            return response != null && !string.IsNullOrWhiteSpace(response.state);
        }
        catch
        {
            return false;
        }
    }

    private static string SerializeResponse(NetworkService.CheckConnectionResponse response)
    {
        if (response == null)
        {
            return string.Empty;
        }

        try
        {
            return JsonConvert.SerializeObject(response, Formatting.Indented);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void InitializeMockJsonIfNeeded(NetworkService.CheckConnectionResponse response)
    {
        if (!string.IsNullOrWhiteSpace(MockResponseJson))
        {
            return;
        }

        string serialized = SerializeResponse(response);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return;
        }

        MockResponseJson = serialized;
    }
}

public sealed class PackageVersionStatus
{
    public bool hasChecked;
    public string connectionState;
    public string currentVersion;
    public string latestVersion;
    public string updateMessage;
    public bool hasMinorUpdate;
    public bool requiresMajorUpdate;

    public static PackageVersionStatus CreateDefault(string currentVersion)
    {
        return new PackageVersionStatus
        {
            hasChecked = false,
            connectionState = "disconnected",
            currentVersion = string.IsNullOrEmpty(currentVersion) ? "0.0.0" : currentVersion,
            latestVersion = null,
            updateMessage = null,
            hasMinorUpdate = false,
            requiresMajorUpdate = false
        };
    }
}
#endif
