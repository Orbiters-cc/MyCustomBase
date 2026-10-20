using NUnit.Framework;
using UnityEngine;

/// <summary>
/// MCB's use of Orbiters Toolkit's shared issue reporter (masking, queueing, sending and the off-by-default setting are
/// the Toolkit's, tested there), and the account token never reaching MCB's own request logs.
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
