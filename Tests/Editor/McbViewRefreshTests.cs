using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class McbViewRefreshTests
{
    [Test]
    public void RequestsBeforeAFrameRefreshOnceWithEveryPartAsked()
    {
        var refreshed = new List<McbViewParts>();
        var refresh = new McbViewRefresh(parts => refreshed.Add(parts));

        refresh.Request(McbViewParts.Gallery);
        refresh.Request(McbViewParts.Versions);
        refresh.Request(McbViewParts.Gallery);
        Assert.That(refreshed, Is.Empty, "a request waits for the next frame");

        refresh.Flush();
        Assert.That(refreshed, Is.EqualTo(new[] { McbViewParts.Gallery | McbViewParts.Versions }));

        refresh.Flush();
        Assert.That(refreshed.Count, Is.EqualTo(1), "nothing pending, nothing refreshed");
    }

    [Test]
    public void ARequestMadeWhileRefreshingWaitsForTheNextFrame()
    {
        var refreshed = new List<McbViewParts>();
        McbViewRefresh refresh = null;
        refresh = new McbViewRefresh(parts =>
        {
            refreshed.Add(parts);
            if (refreshed.Count == 1) refresh.Request(McbViewParts.Status);
        });

        refresh.Request(McbViewParts.All);
        refresh.Flush();
        Assert.That(refresh.Pending, Is.EqualTo(McbViewParts.Status));
        refresh.Flush();
        Assert.That(refreshed, Is.EqualTo(new[] { McbViewParts.All, McbViewParts.Status }));
    }

    [Test]
    public void CancelForgetsWhatWasPending()
    {
        int calls = 0;
        var refresh = new McbViewRefresh(_ => calls++);
        refresh.Request(McbViewParts.Creator);
        refresh.Cancel();
        refresh.Flush();
        Assert.That(calls, Is.Zero);
        Assert.That(refresh.Pending, Is.EqualTo(McbViewParts.None));
    }

    [Test]
    public void BusyStateIsSharedByEveryViewOfOneAvatarAndAnnouncedOnce()
    {
        var go = new GameObject("McbViewModelTest");
        try
        {
            var target = go.AddComponent<MyCustomBase>();
            var first = McbSession.For(target);
            var second = McbSession.For(target);
            Assert.That(second, Is.SameAs(first));
            Assert.That(McbSession.Find(target), Is.SameAs(first));

            var announced = new List<McbViewParts>();
            first.FlushChanges();
            first.Changed += parts => announced.Add(parts);
            first.isDownloading = true;
            first.isApplying = true;
            first.isApplying = true;
            Assert.That(second.isDownloading && second.isApplying && second.IsBusy);

            first.FlushChanges();
            Assert.That(announced, Is.EqualTo(new[] { McbViewParts.Busy }));

            first.isDownloading = false;
            first.isApplying = false;
            first.FlushChanges();
            Assert.That(second.IsBusy, Is.False);
            Assert.That(announced.Count, Is.EqualTo(2));

            first.isDeleting = false;
            first.FlushChanges();
            Assert.That(announced.Count, Is.EqualTo(2), "setting a flag to its value announces nothing");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}

public sealed class VersionActionsAppliedStateScopeTests
{
    private static int Depth(VersionActions actions) =>
        (int)typeof(VersionActions).GetField("appliedScopeDepth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(actions);

    [Test]
    public void NestedScopesEndWithTheOutermostAndDisposingTwiceIsHarmless()
    {
        var actions = new VersionActions(null, null, null);
        var outer = actions.AppliedStateScope();
        var inner = actions.AppliedStateScope();
        Assert.That(Depth(actions), Is.EqualTo(2));
        inner.Dispose();
        inner.Dispose();
        Assert.That(Depth(actions), Is.EqualTo(1));
        outer.Dispose();
        Assert.That(Depth(actions), Is.Zero);
        // Without an avatar nothing is applied, inside a scope or not.
        using (actions.AppliedStateScope())
        {
            Assert.That(actions.IsVersionCurrentlyApplied(new CustomBaseVersion { version = "1.0.0" }), Is.False);
            Assert.That(actions.HasAppliedCustomBaseEvidence(), Is.False);
        }
    }

    // A real resolution through the avatar's applied marker: inside a scope the first answer is kept (the section is built
    // from one state), outside it every question resolves again.
    [Test]
    public void AScopeKeepsTheResolvedAnswerAndTheNextQuestionAfterItResolvesAgain()
    {
        var go = new GameObject("McbAppliedScopeTest");
        try
        {
            var target = go.AddComponent<MyCustomBase>();
            // A detached session: a started one reads the user's account and listens to the services.
            var inspector = McbSession.Detached(target);
            inspector.serverVersions = new List<CustomBaseVersion>();
            inspector.importedVersions = new List<CustomBaseVersion>();
            inspector.unsubmittedVersions = new List<CustomBaseVersion>();
            var first = new CustomBaseVersion { assetId = 7, version = "1.0.0" };
            var second = new CustomBaseVersion { assetId = 7, version = "1.1.0" };
            target.appliedCustomBaseVersion = first;
            var actions = new VersionActions(inspector, null, null);

            Assert.That(actions.IsVersionCurrentlyApplied(first), Is.True, "resolved from the applied marker");
            using (actions.AppliedStateScope())
            {
                Assert.That(actions.IsVersionCurrentlyApplied(first), Is.True);
                Assert.That(actions.HasAppliedCustomBaseEvidence(), Is.True);
                target.appliedCustomBaseVersion = second;
                Assert.That(actions.IsVersionCurrentlyApplied(first), Is.True, "kept for the rest of the build");
                Assert.That(actions.IsVersionCurrentlyApplied(second), Is.False);
                target.appliedCustomBaseVersion = null;
                Assert.That(actions.HasAppliedCustomBaseEvidence(), Is.True, "kept for the rest of the build");
                target.appliedCustomBaseVersion = second;
            }

            Assert.That(actions.IsVersionCurrentlyApplied(second), Is.True, "resolved again after the scope");
            Assert.That(actions.IsVersionCurrentlyApplied(first), Is.False);
            target.appliedCustomBaseVersion = null;
            Assert.That(actions.HasAppliedCustomBaseEvidence(), Is.False);
            using (actions.AppliedStateScope())
                Assert.That(actions.IsVersionCurrentlyApplied(second), Is.False, "a new scope starts from the state now");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
