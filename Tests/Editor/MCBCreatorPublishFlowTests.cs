using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

// Creator publish flows: source binding on creation retries, the shared build/publish guard, resume checks against the
// server's stored settings, version ordering and what the logic package may ship.
public class MCBCreatorPublishFlowTests
{
    private static readonly string HashA = new string('a', 64), HashB = new string('b', 64), HashC = new string('c', 64);

    [Test]
    public void CreationRetryBindsOnlyTheRequestedSceneSources()
    {
        var requested = new[] { Source(0, "Assets/Base/Body.fbx", HashA), Source(0, "Assets/Base/Head.fbx", HashB) };
        // A retry returns every registered original: the scene's, an extra original sharing Head.fbx, and its own body.
        var returned = new[]
        {
            Source(11, "Assets/Base/Body.fbx", HashA, "primary"),
            Source(12, "Assets/Base/Head.fbx", HashB, "primary"),
            Source(13, "Assets/Base/Head.fbx", HashB, "extra"),
            Source(14, "Assets/Base/Body.fbx", HashC, "extra"),
        };
        var selected = CustomBaseSourceSetupTransaction.SelectRequestedSources(returned, requested, "primary");
        Assert.That(selected.Select(f => f.id), Is.EqualTo(new[] { 11, 12 }));

        // Without keys the earliest record wins; the server's canonical path is matched by hash.
        var canonical = new[] { Source(21, "Assets/Canonical/Body.fbx", HashA), Source(22, "Assets/Canonical/Head.fbx", HashB),
            Source(23, "Assets/Canonical/Head.fbx", HashB) };
        Assert.That(CustomBaseSourceSetupTransaction.SelectRequestedSources(canonical, requested).Select(f => f.id), Is.EqualTo(new[] { 21, 22 }));

        Assert.Throws<InvalidOperationException>(() => CustomBaseSourceSetupTransaction.SelectRequestedSources(returned.Take(1), requested));
        Assert.Throws<InvalidOperationException>(() => CustomBaseSourceSetupTransaction.SelectRequestedSources(null, requested));
    }

    [Test]
    public void BuildsAndPublishesShareOneProcessWideGuard()
    {
        Assume.That(VersionOperationGuard.IsBusy, Is.False, "An MCB build or publish is running in this editor.");
        var build = VersionOperationGuard.Acquire("building version 1.2.0");
        VersionOperationGuard.Scope publish = null;
        try
        {
            Assert.That(build.IsHeld, Is.True);
            Assert.That(VersionOperationGuard.TryAcquire("publishing version 1.1.0", out publish, out string busy), Is.False);
            Assert.That(publish, Is.Null);
            StringAssert.Contains("building version 1.2.0", busy);
            var error = Assert.Throws<InvalidOperationException>(() => VersionOperationGuard.Acquire("building version 2.0.0"));
            StringAssert.Contains("Wait for it to finish", error.Message);

            build.Dispose();
            build.Dispose();
            Assert.That(VersionOperationGuard.IsBusy, Is.False);
            Assert.That(VersionOperationGuard.TryAcquire("publishing version 1.1.0", out publish, out busy), Is.True);
            Assert.That(busy, Is.Null);
            // A released scope never releases its successor.
            build.Dispose();
            Assert.That(publish.IsHeld, Is.True);
        }
        finally
        {
            build.Dispose();
            publish?.Dispose();
        }
        Assert.That(VersionOperationGuard.IsBusy, Is.False);
    }

    [Test]
    public void ResumeComparesSettingsTheWayTheServerStoresThem()
    {
        var local = new CustomBaseVersion { assetId = 7, version = "1.0.0", defaultAviVersion = "1", title = "  Fluffy update ", scope = Scope.BETA,
            changelog = null, extraCustomization = null, customBlendshapes = null, dependencies = null,
            originalBaseVersions = new[] { Original("a", "a.bin") } };
        var remote = new CustomBaseVersion { assetId = 7, version = "1.0.0", defaultAviVersion = "1", title = "Fluffy update", scope = Scope.BETA,
            changelog = "Written by the server's AI because none was given.", extraCustomization = new object[0],
            customBlendshapes = new CustomBlendshapeEntry[0], dependencies = new Dictionary<string, string>(),
            originalBaseVersions = new[] { Original("a", "a.bin") } };
        Assert.DoesNotThrow(() => VersionPublisher.ValidateResume(local, remote));

        local.changelog = "  Notes  ";
        remote.changelog = "Notes";
        local.extraCustomization = new object[] { " nativeMesh ", "nativeMesh", JObject.Parse("{\"suggestRealistic\":[\" Body \",\"Body\"],\" \":1}"), new JObject() };
        remote.extraCustomization = new object[] { "nativeMesh", JObject.Parse("{\"suggestRealistic\":[\"Body\"]}") };
        Assert.DoesNotThrow(() => VersionPublisher.ValidateResume(local, remote));

        local.changelog = "Other notes";
        Assert.Throws<InvalidOperationException>(() => VersionPublisher.ValidateResume(local, remote));
        local.changelog = "Notes";
        remote.extraCustomization = new object[] { "nativeMesh", "customVeins" };
        Assert.Throws<InvalidOperationException>(() => VersionPublisher.ValidateResume(local, remote));
    }

    [Test]
    public void NewVersionsMustBeHigherThanTheParentOrTheLatestVersion()
    {
        Func<string, string, int> compare = (a, b) => Version.Parse(a).CompareTo(Version.Parse(b));
        Assert.DoesNotThrow(() => VersionPublisher.ValidateNewVersionNumber("1.10.0", "1.9.0", null, compare));
        Assert.Throws<InvalidOperationException>(() => VersionPublisher.ValidateNewVersionNumber("1.9.0", "1.9.0", null, compare));
        // A branch from an older parent stays possible.
        Assert.DoesNotThrow(() => VersionPublisher.ValidateNewVersionNumber("1.0.1", "1.0.0", new[] { "2.0.0" }, compare));
        var error = Assert.Throws<InvalidOperationException>(() => VersionPublisher.ValidateNewVersionNumber("1.9.5", null, new[] { "1.9.0", "1.10.0" }, compare));
        StringAssert.Contains("1.10.0", error.Message);
        Assert.DoesNotThrow(() => VersionPublisher.ValidateNewVersionNumber("1.11.0", null, new[] { "1.9.0", "1.10.0" }, compare));
        Assert.DoesNotThrow(() => VersionPublisher.ValidateNewVersionNumber("0.1.0", null, Array.Empty<string>(), compare));
    }

    [Test]
    public void ReleaseCheckpointsAreNamedAfterThePublishedAsset()
    {
        var version = new CustomBaseVersion { assetId = 9 };
        var known = new[] { new AvatarDiscoveredAsset { id = 3, name = "Gallery selection" }, null, new AvatarDiscoveredAsset { id = 9, name = " Published " } };
        Assert.That(VersionPublisher.ResolvePublishedAssetName(version, null, known), Is.EqualTo("Published"));
        Assert.That(VersionPublisher.ResolvePublishedAssetName(version, " Draft asset ", known), Is.EqualTo("Draft asset"));
        Assert.That(VersionPublisher.ResolvePublishedAssetName(version, null, known.Take(1)), Is.Null);
    }

    [Test]
    public void LogicPackageLeavesOutTheOriginalBaseAndPackages()
    {
        var modelDependencies = new Dictionary<string, string[]>
        {
            ["Assets/Avatars/Base/FBX/Rig/Base.fbx"] = new[] { "Assets/Avatars/Base/FBX/Rig/Base.fbx", "Assets/Avatars/Base/Materials/Body.mat",
                "Assets/Avatars/Base/Textures/Body.png", "Assets/_Shaders/Toon.shader" },
        };
        var dependencies = new[]
        {
            "Assets/Creator/Logic.prefab", "Assets/Creator/Toggle.anim", "Assets/Creator/Hat.mat",
            "Assets/Avatars/Base/Materials/Blue.mat", "Assets/Avatars/Base/Textures/Blue.png", "Assets/Avatars/Base/FBX/Rig/Base.fbx",
            "Assets/_Shaders/Toon.shader", "Assets/Avatars/Other/Jacket.fbx", "Assets/Creator/Custom/Body.fbx",
            "Packages/com.vrcfury.vrcfury/Runtime/VRCFury.cs",
        };
        var kept = FileManagerService.SelectLogicPackageAssets(dependencies, "Assets/Creator/Logic.prefab",
            new[] { "Assets/Avatars/Base/FBX/Rig/Base.fbx.originalbase" }, new[] { "Assets/Creator/Custom/Body.fbx" },
            path => modelDependencies.TryGetValue(path, out var used) ? used : Array.Empty<string>(), out var excluded);
        Assert.That(kept, Is.EquivalentTo(new[] { "Assets/Creator/Logic.prefab", "Assets/Creator/Toggle.anim", "Assets/Creator/Hat.mat",
            "Assets/Avatars/Other/Jacket.fbx" }));
        Assert.That(excluded, Is.EquivalentTo(new[] { "Assets/Avatars/Base/Materials/Blue.mat", "Assets/Avatars/Base/Textures/Blue.png",
            "Assets/Avatars/Base/FBX/Rig/Base.fbx", "Assets/_Shaders/Toon.shader", "Assets/Creator/Custom/Body.fbx" }));

        // Logic kept inside the base's folder: only the model's own folder and the files it uses are left out.
        Assert.That(FileManagerService.OriginalBaseFolder("Assets/Avatars/Base/FBX/Rig/Base.fbx", modelDependencies["Assets/Avatars/Base/FBX/Rig/Base.fbx"],
            "Assets/Avatars/Base/Logic/Logic.prefab"), Is.EqualTo("Assets/Avatars/Base/FBX/Rig"));
        Assert.That(FileManagerService.OriginalBaseFolder("Assets/Avatars/Base/FBX/Rig/Base.fbx", null, "Assets/Avatars/Base/FBX/Rig/Logic.prefab"), Is.Null);
        // A model directly in Assets/ never turns the whole project into the base's folder.
        Assert.That(FileManagerService.OriginalBaseFolder("Assets/Base.fbx", new[] { "Assets/Materials/Body.mat" }, null), Is.Null);
    }

    private static ModelFileData Source(int id, string path, string hash, string sourceVersionKey = null) => new ModelFileData
    {
        id = id, path = path, hash = hash, type = "FBX", role = "SOURCE",
        metadata = sourceVersionKey == null ? null : new Dictionary<string, object> { ["sourceVersionKey"] = sourceVersionKey },
    };

    private static OriginalBaseVersionData Original(string key, string patch) => new OriginalBaseVersionData
    {
        key = key, label = key,
        sourceFiles = new[] { new ModelFileData { path = "Assets/Base.fbx", hash = HashA } },
        versionFiles = new[] { new ModelFileData { path = patch, hash = HashB, role = "PATCH" } },
    };
}
