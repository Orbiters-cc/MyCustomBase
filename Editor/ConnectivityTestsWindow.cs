#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class ConnectivityTestsWindow : EditorWindow
{
    private string testUrl = string.Empty;
    private string httpClientStatus = string.Empty;
    private string unityWebRequestStatus = string.Empty;
    private bool httpClientRunning;
    private bool unityWebRequestRunning;
    private bool forceTls12 = true;
    private bool ignoreCertificateErrors;
    private bool diagnosticsRunning;
    private string diagnosticsReport = string.Empty;
    private Vector2 diagnosticsScroll;
#if UNITY_EDITOR_WIN
    private string powerShellStatus = string.Empty;
    private bool powerShellRunning;
#endif

    [MenuItem("Tools/My Custom Base (MCB)/Connectivity Tests")]
    public static void ShowWindow()
    {
        var window = GetWindow<ConnectivityTestsWindow>("Connectivity Tests");
        window.minSize = new Vector2(520f, 380f);
        window.RefreshDefaultUrl();
    }

    private void OnEnable()
    {
        RefreshDefaultUrl();
    }

    private void RefreshDefaultUrl()
    {
        if (!string.IsNullOrEmpty(testUrl))
        {
            return;
        }

        testUrl = ConnectivityDiagnosticsService.BuildConnectivityCheckUrl(AuthenticationService.GetAuth()?.token);
    }

    private ConnectivityDiagnosticsOptions BuildOptions()
    {
        return new ConnectivityDiagnosticsOptions
        {
            ForceTls12 = forceTls12,
            IgnoreCertificateErrors = ignoreCertificateErrors
        };
    }

    private void OnGUI()
    {
        GUILayout.Space(10);
        EditorGUILayout.LabelField("Connectivity Tests", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Any HTTP response counts as reachable. Only transport failures trigger the diagnostics report.", MessageType.Info);
        if (MCBUtils.apiSimulationMode != ApiSimulationMode.Off)
        {
            EditorGUILayout.HelpBox("API simulation is active: " + MCBUtils.apiSimulationMode, MessageType.Warning);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Test URL:", GUILayout.Width(70));
            testUrl = EditorGUILayout.TextField(testUrl);
        }

        if (GUILayout.Button("Use current API target", GUILayout.Height(20f)))
        {
            testUrl = ConnectivityDiagnosticsService.BuildConnectivityCheckUrl(AuthenticationService.GetAuth()?.token);
        }

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Options (diagnostics)", EditorStyles.boldLabel);
            forceTls12 = EditorGUILayout.ToggleLeft("Force TLS 1.2", forceTls12);
            ignoreCertificateErrors = EditorGUILayout.ToggleLeft("Ignore certificate errors (TEST ONLY)", ignoreCertificateErrors);
        }

        GUILayout.Space(6);
        DrawSection("HttpClient", httpClientStatus, httpClientRunning, RunHttpClientTestAsync);
        GUILayout.Space(6);
        DrawSection("UnityWebRequest", unityWebRequestStatus, unityWebRequestRunning, RunUnityWebRequestTestAsync);
#if UNITY_EDITOR_WIN
        GUILayout.Space(6);
        DrawSection("Windows (PowerShell/WinHTTP)", powerShellStatus, powerShellRunning, RunPowerShellTestAsync);
#endif

        GUILayout.Space(8);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Deep Diagnostics", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(diagnosticsRunning))
            {
                if (GUILayout.Button("Run Deep Diagnostics", GUILayout.Height(26f)))
                {
                    _ = RunDeepDiagnosticsAsync();
                }
            }

            diagnosticsScroll = EditorGUILayout.BeginScrollView(diagnosticsScroll, GUILayout.MinHeight(140f));
            var style = new GUIStyle(EditorStyles.textArea) { wordWrap = false };
            EditorGUILayout.TextArea(diagnosticsReport ?? string.Empty, style, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(diagnosticsReport)))
            {
                if (GUILayout.Button("Copy Report to Clipboard"))
                {
                    EditorGUIUtility.systemCopyBuffer = diagnosticsReport;
                }
            }
        }
    }

    private void DrawSection(string title, string status, bool running, Func<Task> onClick)
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(running))
            {
                if (GUILayout.Button("Test", GUILayout.Height(26f)))
                {
                    _ = onClick();
                }
            }

            if (!string.IsNullOrEmpty(status))
            {
                var style = new GUIStyle(EditorStyles.label) { richText = true };
                EditorGUILayout.LabelField(status, style);
            }
        }
    }

    private async Task RunHttpClientTestAsync()
    {
        httpClientRunning = true;
        httpClientStatus = "Running...";
        Repaint();

        try
        {
            var result = await ConnectivityDiagnosticsService.RunHttpClientProbeAsync(testUrl, BuildOptions());
            httpClientStatus = result.ToRichTextStatus();
            if (result.ReachedServer)
            {
                Debug.Log("[ConnectivityTestsWindow/HttpClient] Reached server. Status: " + result.StatusCode + " " + result.ReasonPhrase);
            }
            else
            {
                Debug.LogError("[ConnectivityTestsWindow/HttpClient] Transport error: " + result.Error);
            }
        }
        finally
        {
            httpClientRunning = false;
            Repaint();
        }
    }

    private async Task RunUnityWebRequestTestAsync()
    {
        unityWebRequestRunning = true;
        unityWebRequestStatus = "Running...";
        Repaint();

        try
        {
            var result = await ConnectivityDiagnosticsService.RunUnityWebRequestProbeAsync(testUrl, BuildOptions());
            unityWebRequestStatus = result.ToRichTextStatus();
            if (result.ReachedServer)
            {
                Debug.Log("[ConnectivityTestsWindow/UWR] Reached server. Status: " + result.StatusCode + " " + result.ReasonPhrase);
            }
            else
            {
                Debug.LogError("[ConnectivityTestsWindow/UWR] Error: " + result.Error);
            }
        }
        finally
        {
            unityWebRequestRunning = false;
            Repaint();
        }
    }

#if UNITY_EDITOR_WIN
    private async Task RunPowerShellTestAsync()
    {
        powerShellRunning = true;
        powerShellStatus = "Running...";
        Repaint();

        try
        {
            var result = await ConnectivityDiagnosticsService.RunPowerShellProbeAsync(testUrl, 25);
            powerShellStatus = result.ToRichTextStatus();
            if (result.ReachedServer)
            {
                Debug.Log("[ConnectivityTestsWindow/WinHTTP] Reached server. Status: " + result.StatusCode + " " + result.ReasonPhrase);
            }
            else
            {
                Debug.LogError("[ConnectivityTestsWindow/WinHTTP] Error: " + result.Error);
            }
        }
        finally
        {
            powerShellRunning = false;
            Repaint();
        }
    }
#endif

    private async Task RunDeepDiagnosticsAsync()
    {
        diagnosticsRunning = true;
        diagnosticsReport = "Running diagnostics...\n";
        Repaint();

        try
        {
            diagnosticsReport = await ConnectivityDiagnosticsService.BuildFullReportAsync(testUrl, BuildOptions());
        }
        finally
        {
            diagnosticsRunning = false;
            Repaint();
        }
    }
}
#endif
