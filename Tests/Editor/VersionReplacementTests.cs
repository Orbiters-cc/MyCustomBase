using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

// A corrected build replaces a published version under its number (VersionPublisher replace, the server's revision).
public class VersionReplacementTests
{
    private static MyCustomBase Installed(int revision)
    {
        var target = UnityEditor.EditorUtility.CreateGameObjectWithHideFlags("Corrected build test", HideFlags.HideAndDontSave, typeof(MyCustomBase)).GetComponent<MyCustomBase>();
        target.appliedCustomBaseAssetId = 2;
        target.appliedCustomBaseVersionString = "1.0.8";
        target.appliedCustomBaseDefaultAviVersion = "1.5";
        target.appliedCustomBaseSourceVersionKey = "key";
        target.appliedCustomBaseRevision = revision;
        return target;
    }

    private static CustomBaseVersion Published(string version, int revision) =>
        new CustomBaseVersion { assetId = 2, version = version, defaultAviVersion = "1.5", sourceVersionKey = "key", revision = revision };

    [Test]
    public void ACorrectedBuildIsOfferedOverTheBuildInstalledBeforeIt()
    {
        var target = Installed(0);
        try
        {
            // Installed before revisions existed: revision 1, so the server's first build is what the avatar has.
            Assert.False(VersionRevisions.IsCorrectedBuildOf(Published("1.0.8", 0), target));
            Assert.False(VersionRevisions.IsCorrectedBuildOf(Published("1.0.8", 1), target));
            Assert.True(VersionRevisions.IsCorrectedBuildOf(Published("1.0.8", 2), target));
            target.appliedCustomBaseRevision = 2;
            Assert.False(VersionRevisions.IsCorrectedBuildOf(Published("1.0.8", 2), target));
            // Another number, another original or a local build is never a corrected build of the installed version.
            Assert.False(VersionRevisions.IsCorrectedBuildOf(Published("1.0.9", 3), target));
            var otherOriginal = Published("1.0.8", 3); otherOriginal.sourceVersionKey = "other";
            Assert.False(VersionRevisions.IsCorrectedBuildOf(otherOriginal, target));
            var local = Published("1.0.8", 3); local.isUnsubmitted = true;
            Assert.False(VersionRevisions.IsCorrectedBuildOf(local, target));
        }
        finally { UnityEngine.Object.DestroyImmediate(target.gameObject); }
    }

    [Test]
    public void ADownloadedFolderOfAnEarlierBuildIsNotTheVersion()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-revision-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var first = Published("1.0.8", 0);
            first.versionFiles = new[] { new ModelFileData { path = "mesh.bin" } };
            VersionRepository.SaveVersionJson(folder, first);
            File.WriteAllText(Path.Combine(folder, "mesh.bin"), "payload");
            VersionRepository.CreateManifestFromFolder(folder, first, false, null, null).Save(folder);
            Assert.True(VersionStorage.IsComplete(folder, Published("1.0.8", 1)), "revision 1 is the build written before revisions");
            var corrected = Published("1.0.8", 2);
            corrected.versionFiles = first.versionFiles;
            Assert.False(VersionStorage.IsComplete(folder, corrected), "the corrected build is downloaded again");
            // The version.json written with a download records the server's revision.
            VersionRepository.SaveVersionJson(folder, corrected);
            Assert.True(VersionStorage.IsComplete(folder, corrected));
            Assert.That(File.ReadAllText(Path.Combine(folder, "version.json")), Does.Contain("\"revision\": 2"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public void OnlyABuildWithTheLatestPublishedNumberReplacesIt()
    {
        var published = new[] { Published("1.0.7", 1), Published("1.0.8", 1) };
        var build = Published("1.0.8", 0); build.isUnsubmitted = true;
        Assert.That(VersionPublisher.ReplaceableBy(build, published)?.version, Is.EqualTo("1.0.8"));
        var older = Published("1.0.7", 0); older.isUnsubmitted = true;
        Assert.Null(VersionPublisher.ReplaceableBy(older, published));
        var newer = Published("1.0.9", 0); newer.isUnsubmitted = true;
        Assert.Null(VersionPublisher.ReplaceableBy(newer, published));
        // A published version is not a build to publish.
        Assert.Null(VersionPublisher.ReplaceableBy(Published("1.0.8", 1), published));

        Func<string, string, int> compare = (a, b) => Version.Parse(a).CompareTo(Version.Parse(b));
        Assert.DoesNotThrow(() => VersionPublisher.ValidateReplacement("1.0.8", new[] { "1.0.7", "1.0.8" }, compare));
        var error = Assert.Throws<InvalidOperationException>(() => VersionPublisher.ValidateReplacement("1.0.7", new[] { "1.0.7", "1.0.8" }, compare));
        StringAssert.Contains("1.0.8 is newer than 1.0.7", error.Message);
    }
}
