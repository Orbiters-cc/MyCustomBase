#if UNITY_EDITOR
using System;
using NUnit.Framework;

public class MCBVersionAvailabilityTests
{
    [Test]
    public void CreatorKeepsImportedParentWhenServerListIsEmpty()
    {
        var saved = new CustomBaseVersion { assetId = 14, version = "0.5.2", defaultAviVersion = "1.0.0", isImported = true };
        var other = new CustomBaseVersion { assetId = 15, version = "0.9.0" };
        var draft = new CustomBaseVersion { assetId = 14, version = "0.5.3", isUnsubmitted = true };
        var parents = VersionRepository.GetCreatorParentVersions(14, null, new[] { saved, other, draft }, saved);
        Assert.That(parents, Is.EqualTo(new[] { saved }));
        Assert.That(VersionRepository.GetCreatorParentVersions(14, null, null, saved), Is.EqualTo(new[] { saved }));
        Assert.That(VersionRepository.GetCreatorParentVersions(15, null, null, saved), Is.Empty);
        Assert.That(VersionRepository.GetCreatorParentVersions(14, null, null, draft), Is.Empty);
    }

    [Test]
    public void CreatorReconcilesRemoteParentWithoutLosingItsIdentity()
    {
        var saved = new CustomBaseVersion { assetId = 14, version = "0.5.2", defaultAviVersion = "1.0.0", isImported = true };
        var remote = new CustomBaseVersion { id = 101, assetId = 14, version = "0.5.2", defaultAviVersion = "1.0.0", scope = Scope.PUBLIC };
        var parents = VersionRepository.GetCreatorParentVersions(14, new[] { remote }, new[] { saved }, saved);
        Assert.That(parents, Has.Count.EqualTo(1));
        Assert.That(parents[0], Is.SameAs(remote));
        Assert.That(parents[0].Equals(saved), Is.True);
    }

    [Test]
    public void PublishedCopyKeepsRemoteIdentityBeforeAndAfterLocalDeletion()
    {
        var published = new CustomBaseVersion { id = 101, assetId = 14, version = "0.5.1", defaultAviVersion = "1.0.0", scope = Scope.PUBLIC };
        var downloaded = new CustomBaseVersion { assetId = 14, version = "0.5.1", defaultAviVersion = "1.0.0", isImported = true };
        var otherAsset = new CustomBaseVersion { assetId = 15, version = "0.5.1", defaultAviVersion = "1.0.0" };
        var remote = new[] { published, otherAsset };
        var beforeDelete = VersionRepository.MergeAvailableVersions(14, remote, new[] { downloaded }, null);
        Assert.That(beforeDelete, Has.Count.EqualTo(1));
        Assert.That(beforeDelete[0], Is.SameAs(published));
        Assert.That(beforeDelete[0].isImported, Is.False);
        Assert.That(beforeDelete[0].id, Is.EqualTo(101), "Remote editing identity must survive a local cache copy.");
        var afterDelete = VersionRepository.MergeAvailableVersions(14, remote, Array.Empty<CustomBaseVersion>(), null);
        Assert.That(afterDelete, Is.EqualTo(beforeDelete));
        var offline = VersionRepository.MergeAvailableVersions(14, null, new[] { downloaded }, null);
        Assert.That(offline[0], Is.SameAs(downloaded));
        var draft = new CustomBaseVersion { assetId = 14, version = "0.5.1", defaultAviVersion = "1.0.0", isUnsubmitted = true };
        Assert.That(VersionRepository.MergeAvailableVersions(14, remote, new[] { downloaded }, new[] { draft })[0], Is.SameAs(draft));
    }
}
#endif
