#if UNITY_EDITOR
using System;
using NUnit.Framework;

public class MCBVersionAvailabilityTests
{
    [Test]
    public void PreviousVersionIsTheChosenParentElseTheHighestPublishedOneBelowForTheSameOriginal()
    {
        string key = new string('a', 64);
        CustomBaseVersion V(string number, string source = null, bool draft = false, string parent = null, int asset = 9) => new CustomBaseVersion
            { assetId = asset, version = number, defaultAviVersion = "1.0.0", sourceVersionKey = source ?? key, isUnsubmitted = draft, parentVersion = parent };
        Comparison<string> compare = (a, b) => new Version(a).CompareTo(new Version(b));
        var v100 = V("1.0.0");
        var v110 = V("1.1.0");
        var v120 = V("1.2.0");
        var history = new[] { v120, V("1.1.5", draft: true), v110, V("1.1.9", new string('b', 64)), V("1.1.8", asset: 3), v100 };
        Assert.That(VersionRepository.PreviousInHistory(v120, history, compare), Is.SameAs(v110));
        Assert.That(VersionRepository.PreviousInHistory(v110, history, compare), Is.SameAs(v100));
        // The first version's history starts at the original base.
        Assert.That(VersionRepository.PreviousInHistory(v100, history, compare), Is.Null);
        Assert.That(VersionRepository.PreviousInHistory(V("1.3.0", parent: "1.0.0"), history, compare), Is.SameAs(v100));
        Assert.That(VersionRepository.PreviousInHistory(V("1.3.0", parent: "0.9.0"), history, compare), Is.SameAs(v120));
        Assert.That(VersionRepository.PreviousInHistory(V("2.0.0", draft: true), history, compare), Is.SameAs(v120));
    }

    [Test]
    public void BundledOriginalIsVisibleForItsOwnSourceAndResolvesSharedArtifactFolder()
    {
        var artifact = new CustomBaseVersion { assetId = 7, version = "5.0.0", defaultAviVersion = "1.0.0",
            sourceVersionKey = new string('a', 64), isUnsubmitted = true,
            originalBaseVersions = new[] { new OriginalBaseVersionData { key = new string('b', 64),
                sourceFiles = new[] { new ModelFileData { path = "Body.fbx", hash = new string('c', 64) } },
                versionFiles = new[] { new ModelFileData { path = "mapped.bin" } } } } };
        var views = System.Linq.Enumerable.ToArray(VersionRepository.LocalSourceViews(artifact));
        var matching = VersionRepository.MergeAvailableVersions(7, null, null, views, new string('b', 64));
        Assert.That(matching, Has.Count.EqualTo(1));
        Assert.That(matching[0].versionFiles[0].path, Is.EqualTo("mapped.bin"));
        Assert.That(MCBUtils.GetVersionDataPath(matching[0]), Is.EqualTo(MCBUtils.GetVersionDataPath(artifact)));
        Assert.That(artifact.sourceVersionKey, Is.EqualTo(new string('a', 64)));
        Assert.That(matching[0].isUnsubmitted, Is.True);
        Assert.That(VersionRepository.MergeAvailableVersions(7, null, null, views), Is.EqualTo(new[] { artifact }));
    }

    [Test]
    public void NonmatchingDiscoveryDoesNotInventASourceKeyFromAllSupportedOriginals()
    {
        var a = new ModelFileData { path = "Body.fbx", hash = new string('a', 64) };
        var b = new ModelFileData { path = "Body.fbx", hash = new string('b', 64) };
        var asset = new AvatarDiscoveredAsset { sourceFiles = new[] { a, b }, sourceVersions = new[] {
            new OriginalBaseVersionData { key = OriginalBaseLibrary.Key(new[] { a }) },
            new OriginalBaseVersionData { key = OriginalBaseLibrary.Key(new[] { b }) } } };
        Assert.That(OriginalBaseLibrary.ActiveKey(asset), Is.Null);
        asset.sourceFiles = new[] { b };
        Assert.That(OriginalBaseLibrary.ActiveKey(asset), Is.EqualTo(OriginalBaseLibrary.Key(new[] { b })));
    }

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

    // A Unity upgrade changes where new shared payloads are cached, not which version an applied one belongs to.
    [Test]
    public void SharedMeshCachedByAnotherUnityVersionIsStillRecognised()
    {
        var current = SharedVersion("0.5.3", 'a', 'b');
        var paths = new[] { OldSharedPath(14, 'a'), OldSharedPath(14, 'b') };
        Assert.That(NativeMeshPayloadService.IsSharedMeshForVersion(paths[0], current), Is.True);
        Assert.That(NativeMeshPayloadService.ResolveAppliedMeshVersionFromPaths(
            paths, new[] { current }, 14, "0.5.3", "1.0.0"), Is.SameAs(current));
        Assert.That(NativeMeshPayloadService.IsSharedMeshForVersion(OldSharedPath(15, 'a'), current), Is.False);
        Assert.That(NativeMeshPayloadService.IsSharedMeshForVersion(OldSharedPath(14, 'c'), current), Is.False);
    }

    private static string OldSharedPath(int assetId, char hash) =>
        "Assets/MCB/generated/advancedMeshPayloads/" + assetId + "/shared-2021.3.0f1/" + new string(hash, 64) + ".asset";

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
