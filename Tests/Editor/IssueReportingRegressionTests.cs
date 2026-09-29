using System;
using NUnit.Framework;

public sealed class IssueReportingRegressionTests
{
    [TestCase("Cookie: session=one; second=two", "two")]
    [TestCase("token=secret-value", "secret-value")]
    [TestCase("Authorization: Bearer abc.def-123", "abc.def-123")]
    [TestCase("{\"password\":\"private value\"}", "private value")]
    [TestCase("Request https://example.test/model?token=private", "example.test")]
    [TestCase("C:\\Users\\Creator\\Private Project\\Assets\\avatar.fbx", "Creator")]
    [TestCase("/home/creator/private/avatar.fbx", "creator")]
    [TestCase("Assets/Private Avatar/body.fbx", "Private Avatar")]
    [TestCase("creator@example.test", "creator@example.test")]
    public void SensitiveFieldsAreRedacted(string text, string secret) => StringAssert.DoesNotContain(secret, IssueReportBuffer.Redact(text, 4000));

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

    [Test] public void ReportingDefaultsToOff()
    {
        const string key = "MCB_ShareIssueLogs";
        bool existed = UnityEditor.EditorPrefs.HasKey(key);
        bool old = UnityEditor.EditorPrefs.GetBool(key);
        try
        {
            UnityEditor.EditorPrefs.DeleteKey(key);
            Assert.False(EditorIssueReporter.ShareIssueLogs);
        }
        finally { if (existed) UnityEditor.EditorPrefs.SetBool(key, old); }
    }

    [Test] public void BurstHasBoundedMemoryAndRate()
    {
        var buffer = new IssueReportBuffer();
        var now = new DateTime(2026, 1, 1);
        for (int i = 0; i < 10000; i++) buffer.Enqueue(new IssueReportBuffer.Report { Message = "warning " + i, Severity = "WARN" }, now);
        Assert.AreEqual(IssueReportBuffer.QueueLimit, buffer.PendingCount);
        Assert.LessOrEqual(buffer.SignatureCount, IssueReportBuffer.SignatureLimit);
        Assert.True(buffer.TryTake(now, out _));
        Assert.False(buffer.TryTake(now.AddSeconds(9), out _));
        Assert.True(buffer.TryTake(now.AddSeconds(10), out _));
        buffer.Clear();
        Assert.AreEqual(0, buffer.PendingCount);
        Assert.AreEqual(0, buffer.SignatureCount);
    }

    [Test] public void DuplicateSuppressionExpires()
    {
        var buffer = new IssueReportBuffer();
        var report = new IssueReportBuffer.Report { Message = "warning", Severity = "WARN" };
        var now = new DateTime(2026, 1, 1);
        Assert.True(buffer.Enqueue(report, now));
        Assert.False(buffer.Enqueue(report, now.AddSeconds(1)));
        Assert.True(buffer.Enqueue(report, now.AddMinutes(2)));
    }
}
