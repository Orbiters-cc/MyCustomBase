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
}
