#if UNITY_EDITOR
using Orbiters.Toolkit.Editor;
using UnityEditor;

/// <summary>
/// MCB's opt-in diagnostics (off by default): what MCB's own logger reports (<see cref="MCBLogger.IssueLogged"/>, whether
/// or not it also logs to the Console), sent by Orbiters Toolkit's shared reporter, which masks and bounds it. MCB's code
/// has no namespace of its own, so its logger feeds the reporter instead of the Console being read.
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
        // Not while MCB knows the server is unreachable.
        CanSend = () => !MCBConnectivityMonitor.HasCompleted || MCBConnectivityMonitor.CanReachServer
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

    /// <summary>Reads the settings again (another editor may have changed them).</summary>
    public static void RefreshListener()
    {
        Reporter.Refresh();
        MCBLogger.IssueMinLevel = Reporter.MinSeverityLevel;
    }
}
#endif
