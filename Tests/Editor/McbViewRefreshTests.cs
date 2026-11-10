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
            var first = McbAvatarViewModel.For(target);
            var second = McbAvatarViewModel.For(target);
            Assert.That(second, Is.SameAs(first));

            var announced = new List<McbViewParts>();
            first.Changed += parts => announced.Add(parts);
            first.IsDownloading = true;
            first.IsApplying = true;
            first.IsApplying = true;
            Assert.That(second.IsDownloading && second.IsApplying && second.IsBusy);

            first.FlushChanges();
            Assert.That(announced, Is.EqualTo(new[] { McbViewParts.Busy }));

            first.IsDownloading = false;
            first.IsApplying = false;
            first.FlushChanges();
            Assert.That(second.IsBusy, Is.False);
            Assert.That(announced.Count, Is.EqualTo(2));

            first.IsDeleting = false;
            first.FlushChanges();
            Assert.That(announced.Count, Is.EqualTo(2), "setting a flag to its value announces nothing");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
