#if UNITY_EDITOR
using Orbiters.Toolkit.Editor;
using UnityEditor;

/// <summary>
/// MCB's diagnostics: what MCB's own logger reports (<see cref="MCBLogger.IssueLogged"/>, whether or not it also logs to
/// the Console), sent by Orbiters Toolkit's shared reporter, which masks and bounds it. MCB's code has no namespace of
/// its own, so its logger feeds the reporter instead of the Console being read.
/// Sent until the person turns it off (Enzo, 2026-11-14: opt-in since 1.7.5 brought no report at all, and a buyer's
/// failed install could only be found through Discord). Nothing leaves before MCB's inspector has shown its notice
/// once on this computer (<see cref="NoticeShown"/>, DiagnosticsNotice), unless the person already chose.
/// </summary>
[InitializeOnLoad]
public static class EditorIssueReporter
{
    internal static IssueReporterOptions Options() => new IssueReporterOptions
    {
        ToolName = "MCB",
        SettingsKey = "MCB",
        // Connected only while sharing, from the level sent: MCBLogger takes a stack trace for every log it hands over.
        Feed = (reporter, on) =>
        {
            MCBLogger.IssueLogged -= reporter.Submit;
            MCBLogger.IssueMinLevel = reporter.MinSeverityLevel;
            if (on) MCBLogger.IssueLogged += reporter.Submit;
        },
        Endpoint = () => MCBUtils.getApiUrl(scope: "bugs"),
        SharedByDefault = true,
        // Not while MCB knows the server is unreachable, and not before the person was told (what waits is sent then).
        CanSend = () => (!MCBConnectivityMonitor.HasCompleted || MCBConnectivityMonitor.CanReachServer) && (NoticeShown || HasChoice)
    };

    internal static readonly OrbitersIssueReporter Reporter = OrbitersIssueReporter.Register(Options());

    static EditorIssueReporter()
    {
    }

    public static bool ShareIssueLogs
    {
        get => Reporter.ShareIssueLogs;
        set => Reporter.ShareIssueLogs = value;
    }

    /// <summary>1 Info, 2 Warning, 3 Error, 4 Exception.</summary>
    public static int MinSeverityLevel
    {
        get => Reporter.MinSeverityLevel;
        set
        {
            Reporter.MinSeverityLevel = value;
            MCBLogger.IssueMinLevel = Reporter.MinSeverityLevel;
        }
    }

    /// <summary>What sharing sends, for the settings' tooltip.</summary>
    public static string ConsentText => Reporter.ConsentText;

    /// <summary>Whether the person turned sharing on or off themselves on this computer.</summary>
    public static bool HasChoice => Reporter.HasChoice;

    private const string NoticeShownKey = "MCB_DiagnosticsNoticeShown";

    /// <summary>Whether MCB's inspector showed the notice that says diagnostics are sent, once on this computer.</summary>
    public static bool NoticeShown
    {
        get => EditorPrefs.GetBool(NoticeShownKey, false);
        set { if (value != NoticeShown) EditorPrefs.SetBool(NoticeShownKey, value); }
    }

    /// <summary>Reads the settings again (another editor may have changed them).</summary>
    public static void RefreshListener()
    {
        Reporter.Refresh();
        MCBLogger.IssueMinLevel = Reporter.MinSeverityLevel;
    }
}
#endif
