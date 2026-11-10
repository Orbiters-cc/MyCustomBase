using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class McbSessionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void OneSessionPerComponentIsDroppedWithItsComponent()
    {
        var go = new GameObject("McbSessionTest");
        var other = new GameObject("McbSessionTestOther");
        try
        {
            var target = go.AddComponent<MyCustomBase>();
            var second = other.AddComponent<MyCustomBase>();
            var session = McbSession.For(target);
            Assert.That(McbSession.For(second), Is.Not.SameAs(session), "each custom base has its own");
            Assert.That(McbSession.Live.Contains(session), Is.True);
            Assert.That(McbSession.Find(null), Is.Null);
            Assert.That(McbSession.For(null), Is.Null);
            Assert.That(McbSession.Live.Contains(McbSession.Detached(target)), Is.False, "a detached session is never registered");

            Object.DestroyImmediate(go);
            go = null;
            Assert.That(McbSession.Live.Contains(session), Is.False, "a destroyed component's session is dropped");
        }
        finally
        {
            if (go != null) Object.DestroyImmediate(go);
            Object.DestroyImmediate(other);
        }
    }

    // The member's fetch shows as running until its own answer, even when the selection changed meanwhile (the answer
    // then is not shown, but it must not leave "Fetching..." up for good); another request's answer does not end it.
    [Test]
    public void TheMembersFetchEndsWithItsOwnAnswerOnly()
    {
        var session = McbSession.Detached(null);
        void Pending(string path, string token, int asset, string key)
        {
            typeof(McbSession).GetField("userFetchPath", Private).SetValue(session, path);
            typeof(McbSession).GetField("userFetchToken", Private).SetValue(session, token);
            typeof(McbSession).GetField("userFetchAssetId", Private).SetValue(session, asset);
            typeof(McbSession).GetField("userFetchKey", Private).SetValue(session, key);
            typeof(McbSession).GetField("userFetchPending", Private).SetValue(session, true);
            session.isFetching = true;
        }
        var onError = typeof(McbSession).GetMethod("OnVersionFetchError", Private);
        var onVersions = typeof(McbSession).GetMethod("OnVersionsUpdated", Private);

        Pending("Assets/Base.fbx", "token", 7, "");
        onError.Invoke(session, new object[] { new VersionFetchRequest("Assets/Base.fbx", "token", 8, ""), "other asset" });
        Assert.That(session.isFetching, Is.True, "another request's answer");
        onError.Invoke(session, new object[] { new VersionFetchRequest("Assets/Base.fbx", "other token", 7, ""), "other account" });
        Assert.That(session.isFetching, Is.True, "another account's answer");
        onError.Invoke(session, new object[] { new VersionFetchRequest("Assets/Base.fbx", "token", 7, ""), "failed" });
        Assert.That(session.isFetching, Is.False, "its own answer, an error");
        Assert.That(session.warningsModule.Entries, Is.Empty, "not shown: no asset is selected any more");

        Pending("Assets/Base.fbx", "token", 7, "key");
        onVersions.Invoke(session, new object[] { new VersionFetchRequest("Assets/Base.fbx", "token", 7, "key"), new List<CustomBaseVersion>(), null });
        Assert.That(session.isFetching, Is.False, "its own answer, versions");
        Assert.That(session.serverVersions, Is.Empty);

        Pending("Assets/Base.fbx", "token", 7, "");
        session.SetSelectedAsset(new AvatarDiscoveredAsset { id = 9 });
        Assert.That(session.isFetching, Is.False, "another asset shown: the fetch answers nobody");
    }

    [Test]
    public void SelectingAnotherAssetDropsThePreviousAssetsVersions()
    {
        var session = McbSession.Detached(null);
        session.Select(new AvatarDiscoveredAsset { id = 1 }, refreshVersions: false);
        session.serverVersions.Add(new CustomBaseVersion { assetId = 1, version = "1.0.0" });
        session.selectedVersionForAction = session.serverVersions[0];
        session.Select(new AvatarDiscoveredAsset { id = 1 }, refreshVersions: false);
        Assert.That(session.serverVersions.Count, Is.EqualTo(1), "the same asset keeps its versions");
        session.Select(new AvatarDiscoveredAsset { id = 2 }, refreshVersions: false);
        Assert.That(session.serverVersions, Is.Empty);
        Assert.That(session.selectedVersionForAction, Is.Null);
        Assert.That(session.SelectedAsset.id, Is.EqualTo(2));
    }

    // Two Inspectors on one avatar show one selection: a pick in one is announced to the other.
    [Test]
    public void PickingAVersionIsAnnouncedToEveryView()
    {
        var session = McbSession.Detached(null);
        session.FlushChanges();
        var announced = new List<McbViewParts>();
        session.Changed += parts => announced.Add(parts);
        var version = new CustomBaseVersion { assetId = 1, version = "1.0.0" };
        session.selectedVersionForAction = version;
        session.FlushChanges();
        Assert.That(announced, Is.EqualTo(new[] { McbViewParts.Versions }));
        session.selectedVersionForAction = version;
        session.FlushChanges();
        Assert.That(announced.Count, Is.EqualTo(1), "the same pick announces nothing");
        session.selectedCustomVersionForAction = new UserCustomVersionEntry();
        session.FlushChanges();
        Assert.That(announced.Count, Is.EqualTo(2));
    }

    // A creator window keeps its own asset: a version of another asset picked in the Inspector never fills its form.
    [Test]
    public void ACreatorFormIgnoresAnotherAssetsVersion()
    {
        var session = McbSession.Detached(null);
        var form = new CreatorModeModule(session, new AvatarDiscoveredAsset { id = 1 });
        var other = new CustomBaseVersion { assetId = 2, version = "7.8.9", isUnsubmitted = true, title = "B" };
        session.selectedVersionForAction = other;
        Assert.That(form.SelectedVersionOfOwnAsset(), Is.Null);

        var major = typeof(CreatorModeModule).GetField("newVersionMajor", Private);
        int before = (int)major.GetValue(form);
        typeof(CreatorModeModule).GetMethod("PopulateFieldsFromVersion", Private).Invoke(form, new object[] { other });
        Assert.That((int)major.GetValue(form), Is.EqualTo(before), "another asset's version number is not taken");

        var own = new CustomBaseVersion { assetId = 1, version = "1.2.3", isUnsubmitted = true };
        session.selectedVersionForAction = own;
        Assert.That(form.SelectedVersionOfOwnAsset(), Is.SameAs(own));
    }

    // The versions of an asset a view tracks are never the gallery selection's list of another asset.
    [Test]
    public void AnotherAssetsVersionsAreKeptApartFromTheSelection()
    {
        var session = McbSession.Detached(null);
        var a = new AvatarDiscoveredAsset { id = 1 };
        var b = new AvatarDiscoveredAsset { id = 2 };
        session.Select(a, refreshVersions: false);
        session.serverVersions.Add(new CustomBaseVersion { assetId = 1, version = "1.0.0" });
        session.Track(b);
        Assert.That(session.ServerVersionsOf(a), Is.SameAs(session.serverVersions));
        Assert.That(session.ServerVersionsOf(b), Is.Empty, "B's list is not A's");
        session.RefreshVersionsOf(2);
        Assert.That(session.isFetching, Is.False, "refreshing B never shows A's list as fetching");
        Assert.That(session.serverVersions.Count, Is.EqualTo(1));
    }
}
