#if UNITY_EDITOR
using System;
using NUnit.Framework;

public class MCBVersionAvailabilityTests
{
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
