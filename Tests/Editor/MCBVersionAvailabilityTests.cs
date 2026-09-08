#if UNITY_EDITOR
using System;
using NUnit.Framework;

public class MCBVersionAvailabilityTests
{
    [Test]
    public void SharedMeshRecoveryRejectsStaleMarkerAndPartialMatches()
    {
        var current = SharedVersion("0.5.3", 'a', 'b');
        var stale = SharedVersion("0.5.2", 'a', 'c');
        var paths = new[] { SharedPath('a'), SharedPath('b') };
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            paths, new[] { stale, current }, 14, "0.5.2", "1.0.0"), Is.SameAs(current));
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            paths, new[] { stale }, 14, "0.5.2", "1.0.0"), Is.Null);
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            Array.Empty<string>(), new[] { current }, 14, "0.5.3", "1.0.0"), Is.Null);
    }

    [Test]
    public void IdenticalMeshesKeepExplicitVersionAndNeverGuessBetweenReleases()
    {
        var older = SharedVersion("0.5.3", 'a', 'b');
        var newer = SharedVersion("0.5.4", 'a', 'b');
        var paths = new[] { SharedPath('a'), SharedPath('b') };
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            paths, new[] { newer, older }, 14, "0.5.3", "1.0.0"), Is.SameAs(older));
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            paths, new[] { newer, older }, 0, null, null), Is.Null);
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            paths, new[] { older, older }, 0, null, null), Is.SameAs(older));
        var otherAsset = SharedVersion("0.5.3", 'a', 'b');
        otherAsset.assetId = 15;
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            paths, new[] { otherAsset }, 15, "0.5.3", "1.0.0"), Is.Null);
    }

    private static string SharedPath(char hash) =>
        "Assets/MCB/generated/advancedMeshPayloads/14/shared-" + UnityEngine.Application.unityVersion + "/" + new string(hash, 64) + ".asset";

    private static CustomBaseVersion SharedVersion(string version, params char[] hashes) => new CustomBaseVersion
    {
        assetId = 14, version = version, defaultAviVersion = "1.0.0",
        versionFiles = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(hashes, hash => new ModelFileData
        {
            transform = NativeMeshPayloadService.TransformName,
            metadata = new System.Collections.Generic.Dictionary<string, object> { { "contentHash", new string(hash, 64) } }
        }))
    };

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
