using System.Collections.Generic;
using NUnit.Framework;

public sealed class UnitGitReleaseApiBindingTests
{
    [Test]
    public void ApiV2BindingAcceptsCurrentContractAndInvokesMethods()
    {
        Assert.That(
            UnitGitReleaseApiBinding.TryCreateForTypes(
                typeof(CompatibleReleasesApi),
                typeof(ReleaseEntry),
                typeof(ReleaseField),
                new[]
                {
                    UnitGitReleaseApiBinding.CommitFilesCapability,
                    UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability
                },
                out var api,
                out string message),
            Is.True,
            message);

        object entry = api.CreateReleaseEntry();
        api.SetReleaseEntryValue(entry, "tool", "MCB");
        api.SetReleaseEntryValue(entry, "type", "mcb-version");
        api.SetReleaseEntryValue(entry, "name", "Contract Asset");
        api.SetReleaseEntryValue(entry, "version", "1.2.3");
        api.SetReleaseEntryValue(entry, "title", "Contract Test");
        api.SetReleaseEntryValue(entry, "changelog", "Contract test changelog.");
        api.SetReleaseEntryValue(entry, "scope", "PUBLIC");
        api.SetReleaseEntryValue(entry, "date", "2026-06-17T00:00:00Z");
        api.AddReleaseField(entry, "Asset Id", "42");

        var publishResult = api.PublishRelease(entry, "MCB : v1.2.3 - Contract Test", new[] { "Assets/version.json" });
        var commitResult = api.CommitFiles("MCB : Unit Git connector test", "body", new[] { "Assets/file.txt" });

        Assert.That(publishResult.Success, Is.True, publishResult.Message);
        Assert.That(publishResult.CommitHash, Is.EqualTo("publish-hash"));
        Assert.That(publishResult.ReleaseId, Is.EqualTo("release-id"));
        Assert.That(commitResult.Success, Is.True, commitResult.Message);
        Assert.That(commitResult.CommitHash, Is.EqualTo("commit-hash"));
        Assert.That(((ReleaseEntry)entry).fields, Has.Count.EqualTo(1));
    }

    [Test]
    public void ApiV2BindingRejectsWrongApiVersionWithClearMessage()
    {
        bool ok = UnitGitReleaseApiBinding.TryCreateForTypes(
            typeof(V1ReleasesApi),
            typeof(ReleaseEntry),
            typeof(ReleaseField),
            new[] { UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability },
            out _,
            out string message);

        Assert.That(ok, Is.False);
        Assert.That(message, Is.EqualTo("The Unit Git package is installed but incompatible. MCB requires Unit Git release API v2; found v1."));
    }

    [Test]
    public void ApiV2BindingRejectsMissingCapabilityWithClearMessage()
    {
        bool ok = UnitGitReleaseApiBinding.TryCreateForTypes(
            typeof(MissingScopedCapabilityReleasesApi),
            typeof(ReleaseEntry),
            typeof(ReleaseField),
            new[] { UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability },
            out _,
            out string message);

        Assert.That(ok, Is.False);
        Assert.That(message, Is.EqualTo("The Unit Git package is installed but incompatible. Missing release API capability: scoped-release-checkpoint."));
    }

    [Test]
    public void ApiV2BindingRejectsMissingPublishMethodWithClearMessage()
    {
        bool ok = UnitGitReleaseApiBinding.TryCreateForTypes(
            typeof(MissingPublishMethodReleasesApi),
            typeof(ReleaseEntry),
            typeof(ReleaseField),
            new[] { UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability },
            out _,
            out string message);

        Assert.That(ok, Is.False);
        Assert.That(message, Is.EqualTo("The Unit Git package is installed but incompatible. Missing release API method: PublishRelease()."));
    }

    [Test]
    public void MissingUnitGitPackageIsSilentForAutomaticCheckpointNoOp()
    {
        bool ok = UnitGitReleaseApiBinding.TryCreateForTypes(
            null,
            null,
            null,
            new[] { UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability },
            out _,
            out string message);

        Assert.That(ok, Is.False);
        Assert.That(message, Is.EqualTo(UnitGitReleaseApiBinding.MissingPackageMessage));

        bool suppressed = UnitGitReleasePublisher.SuppressOptionalCheckpointMessage(ref message);

        Assert.That(suppressed, Is.True);
        Assert.That(message, Is.Empty);
    }

    [Test]
    public void IncompatibleUnitGitPackageStillReportsCheckpointFailure()
    {
        bool ok = UnitGitReleaseApiBinding.TryCreateForTypes(
            typeof(V1ReleasesApi),
            typeof(ReleaseEntry),
            typeof(ReleaseField),
            new[] { UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability },
            out _,
            out string message);

        Assert.That(ok, Is.False);
        Assert.That(UnitGitReleasePublisher.SuppressOptionalCheckpointMessage(ref message), Is.False);
        Assert.That(message, Is.EqualTo("The Unit Git package is installed but incompatible. MCB requires Unit Git release API v2; found v1."));
    }

    public sealed class ReleaseField
    {
        public string key = string.Empty;
        public string value = string.Empty;

        public ReleaseField()
        {
        }

        public ReleaseField(string key, string value)
        {
            this.key = key;
            this.value = value;
        }
    }

    public sealed class ReleaseEntry
    {
        public string tool = string.Empty;
        public string type = string.Empty;
        public string name = string.Empty;
        public string version = string.Empty;
        public string title = string.Empty;
        public string changelog = string.Empty;
        public string scope = string.Empty;
        public string date = string.Empty;
        public List<ReleaseField> fields = new List<ReleaseField>();
    }

    public sealed class ReleaseResult
    {
        public bool Success;
        public string Message = string.Empty;
        public string CommitHash = string.Empty;
        public string ReleaseId = string.Empty;
    }

    public static class CompatibleReleasesApi
    {
        public const int ApiVersion = 2;

        public static string[] GetCapabilities()
        {
            return new[]
            {
                UnitGitReleaseApiBinding.CommitFilesCapability,
                UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability,
                UnitGitReleaseApiBinding.FullProjectReleaseCheckpointCapability
            };
        }

        public static ReleaseResult CommitFiles(string commitTitle, string trailingParagraph, string[] projectRelativePaths)
        {
            return new ReleaseResult
            {
                Success = true,
                Message = "committed",
                CommitHash = "commit-hash"
            };
        }

        public static ReleaseResult PublishRelease(ReleaseEntry entry, string commitTitle, string[] projectRelativePaths)
        {
            return new ReleaseResult
            {
                Success = true,
                Message = "published",
                CommitHash = "publish-hash",
                ReleaseId = "release-id"
            };
        }
    }

    public static class V1ReleasesApi
    {
        public const int ApiVersion = 1;

        public static string[] GetCapabilities()
        {
            return CompatibleReleasesApi.GetCapabilities();
        }
    }

    public static class MissingScopedCapabilityReleasesApi
    {
        public const int ApiVersion = 2;

        public static string[] GetCapabilities()
        {
            return new[] { UnitGitReleaseApiBinding.CommitFilesCapability };
        }

        public static ReleaseResult CommitFiles(string commitTitle, string trailingParagraph, string[] projectRelativePaths)
        {
            return new ReleaseResult { Success = true };
        }
    }

    public static class MissingPublishMethodReleasesApi
    {
        public const int ApiVersion = 2;

        public static string[] GetCapabilities()
        {
            return new[]
            {
                UnitGitReleaseApiBinding.CommitFilesCapability,
                UnitGitReleaseApiBinding.ScopedReleaseCheckpointCapability
            };
        }

        public static ReleaseResult CommitFiles(string commitTitle, string trailingParagraph, string[] projectRelativePaths)
        {
            return new ReleaseResult { Success = true };
        }
    }
}
