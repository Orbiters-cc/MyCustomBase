using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// MCB's use of Orbiters Toolkit's shared issue reporter (masking, queueing, sending and the setting are the
/// Toolkit's, tested there), and the account token never reaching MCB's own request logs.
/// </summary>
public sealed class IssueReportingRegressionTests
{
    [Test] public void TheReporterIsMcbsAndReadsOnlyItsLogger()
    {
        Assert.AreEqual("MCB", EditorIssueReporter.Reporter.ToolName);
        var options = EditorIssueReporter.Options();
        Assert.AreEqual("MCB", options.SettingsKey, "The opt-in is kept where it always was.");
        Assert.IsNotNull(options.Feed, "MCB's logger feeds the reporter.");
        Assert.IsEmpty(options.Namespaces, "MCB's code has no namespace: the Console is not read for it.");
        Assert.False(EditorIssueReporter.Reporter.IsOwnLog("UnityEngine.Debug:LogWarning (object)\nMCBLogger:LogWarning (string)\nVersionActions:Infer ()", LogType.Warning));
    }

    [Test] public void TheOptInSaysWhatIsSent()
    {
        StringAssert.Contains("MCB", EditorIssueReporter.ConsentText);
        StringAssert.Contains("stack traces", EditorIssueReporter.ConsentText);
        StringAssert.Contains("every Unity project on this computer", EditorIssueReporter.ConsentText);
        StringAssert.Contains("On until you turn it off", EditorIssueReporter.ConsentText);
    }

    // Sent until the person turns it off, and nothing before the inspector's notice was shown or they chose.
    [Test] public void DiagnosticsAreSentByDefaultButNotBeforeThePersonWasTold()
    {
        var options = EditorIssueReporter.Options();
        Assert.True(options.SharedByDefault);
        const string share = "MCB_ShareIssueLogs", shown = "MCB_DiagnosticsNoticeShown";
        bool hadShare = UnityEditor.EditorPrefs.HasKey(share), wasShare = UnityEditor.EditorPrefs.GetBool(share, false);
        bool hadShown = UnityEditor.EditorPrefs.HasKey(shown), wasShown = UnityEditor.EditorPrefs.GetBool(shown, false);
        try
        {
            UnityEditor.EditorPrefs.DeleteKey(share); UnityEditor.EditorPrefs.DeleteKey(shown);
            Assert.True(EditorIssueReporter.ShareIssueLogs, "On until turned off.");
            Assert.False(EditorIssueReporter.HasChoice);
            Assert.False(options.CanSend(), "Nothing leaves before the notice was shown.");
            Assert.True(DiagnosticsNotice.Needed);
            EditorIssueReporter.NoticeShown = true;
            if (!MCBConnectivityMonitor.HasCompleted || MCBConnectivityMonitor.CanReachServer) Assert.True(options.CanSend());
            Assert.True(DiagnosticsNotice.Needed, "The notice stays until it is answered.");
            EditorIssueReporter.ShareIssueLogs = false;
            Assert.True(EditorIssueReporter.HasChoice);
            Assert.False(EditorIssueReporter.ShareIssueLogs, "The person's choice is kept.");
            Assert.False(DiagnosticsNotice.Needed);
        }
        finally
        {
            if (hadShare) UnityEditor.EditorPrefs.SetBool(share, wasShare); else UnityEditor.EditorPrefs.DeleteKey(share);
            if (hadShown) UnityEditor.EditorPrefs.SetBool(shown, wasShown); else UnityEditor.EditorPrefs.DeleteKey(shown);
            EditorIssueReporter.RefreshListener();
        }
    }

    // Below the level sent nothing is handed over (no stack trace is taken), and nothing while a health check runs: its
    // failures are deliberate.
    [Test] public void TheLoggerHandsOverOnlyTheLevelsSentAndNothingWhileQuiet()
    {
        var got = new List<string>();
        Action<string, string, LogType> listen = (message, stack, type) => { if (message.StartsWith("[IssueTest]", StringComparison.Ordinal)) got.Add(message); };
        int level = MCBLogger.IssueMinLevel;
        MCBLogger.IssueLogged += listen;
        try
        {
            MCBLogger.IssueMinLevel = 2;
            MCBLogger.Log("[IssueTest] info");
            MCBLogger.LogWarning("[IssueTest] warning");
            using (MCBLogger.QuietIssues()) MCBLogger.LogWarning("[IssueTest] deliberate");
            MCBLogger.LogWarning("[IssueTest] after");
            CollectionAssert.AreEqual(new[] { "[IssueTest] warning", "[IssueTest] after" }, got);
        }
        finally
        {
            MCBLogger.IssueLogged -= listen;
            MCBLogger.IssueMinLevel = level;
        }
    }

    [Test] public void RequestUrlsInLogsAndConnectivityReportsNeverContainTheAccountToken()
    {
        const string url = "https://api.orbiters.cc/user?u=4&t=orbit-secret-token";
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var monitor = typeof(MCBConnectivityMonitor);
        string warning = (string)monitor.GetMethod("BuildWarningMessage", flags).Invoke(null, new object[] { "Fetch user info", url, 500L, "Internal Server Error" });
        string details = (string)monitor.GetMethod("BuildFailedRequestDetails", flags).Invoke(null, new object[] { "Fetch user info", url, 0L, "Cannot resolve host" });
        foreach (string text in new[] { NetworkService.SanitizeUrlForLogs(url), warning, details })
        {
            StringAssert.DoesNotContain("orbit-secret-token", text);
            StringAssert.Contains("u=4", text);
        }
    }
}
