#if UNITY_EDITOR
using System;
using NUnit.Framework;

public class MCBPackagePolicyTests
{
    [TestCase("unsupported", true, false)]
    [TestCase("deprecated", false, true)]
    [TestCase("supported", false, false)]
    public void ExplicitPolicyAppliesEvenWithinSamePatch(string policy, bool blocked, bool deprecated)
    {
        var result = MCBPackageVersionService.BuildStatus("1.5.2", new NetworkService.CheckConnectionResponse {
            state = "connected", latestVersion = "1.5.2", supportStatus = policy, updateMessage = "Update in VCC." });
        Assert.That(result.requiresMajorUpdate, Is.EqualTo(blocked));
        Assert.That(result.isDeprecated, Is.EqualTo(deprecated));
        Assert.That(result.updateMessage, Is.EqualTo("Update in VCC."));
    }

    [Test] public void ANewMajorReleaseDoesNotImplicitlyBlockSupportedVersions()
    {
        var result = MCBPackageVersionService.BuildStatus("1.5.2", new NetworkService.CheckConnectionResponse {
            state = "connected", latestVersion = "2.0.0", supportStatus = "supported" });
        Assert.That(result.requiresMajorUpdate, Is.False);
    }

    [Test] public void RecoveryBackoffIsBounded()
    {
        Assert.That(MCBConnectivityMonitor.RecoveryDelay(0), Is.EqualTo(2));
        Assert.That(MCBConnectivityMonitor.RecoveryDelay(1), Is.EqualTo(4));
        Assert.That(MCBConnectivityMonitor.RecoveryDelay(30), Is.EqualTo(60));
    }

    [Test] public void BundledCodecsWorkWithoutAnyExecutableSearchPath()
    {
        string previous = Environment.GetEnvironmentVariable("PATH");
        try {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var input = new byte[256 * 1024]; new Random(419).NextBytes(input);
            int supported = 0;
            foreach (string codec in new[] { MCBCompression.Lz4, MCBCompression.Zstd }) {
                if (!MCBCompression.IsSupported(codec)) continue;
                supported++;
                var encoded = MCBCompression.Encode(input, codec);
                CollectionAssert.AreEqual(input, MCBCompression.Decode(encoded, codec));
            }
            Assert.That(supported, Is.GreaterThan(0), "Release must contain a native codec for this Editor platform.");
        } finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }
}
#endif
