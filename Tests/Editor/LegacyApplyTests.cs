using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

public class LegacyApplyTests
{
    private static LegacyMigrationService.Match Match(int asset, string label = "Legacy") =>
        new LegacyMigrationService.Match { assetId = asset, legacyRelease = new LegacyMigrationService.Release { label = label } };

    [Test]
    public void PendingLookupWaitsThenMigratesOnlyTheRequestedAssetsWholeRelease()
    {
        var pending = new TaskCompletionSource<List<LegacyMigrationService.Match>>();
        IList<LegacyMigrationService.Match> migrated = null;
        bool validated = false;
        var routine = LegacyMigrationService.PrepareApply(7, pending.Task, () => validated = true, matches =>
        { Assert.That(validated, Is.True); migrated = matches; });
        Assert.That(routine.MoveNext(), Is.True);
        Assert.That(validated, Is.False);
        Assert.That(migrated, Is.Null);
        var body = Match(7); var tail = Match(7);
        pending.SetResult(new List<LegacyMigrationService.Match> { Match(9), body, tail });
        Assert.That(routine.MoveNext(), Is.False);
        Assert.That(migrated, Is.EqualTo(new[] { body, tail }));
    }

    [Test]
    public void OtherAssetDoesNotMigrate()
    {
        var routine = LegacyMigrationService.PrepareApply(7, Task.FromResult(new List<LegacyMigrationService.Match> { Match(9) }),
            () => { }, _ => Assert.Fail("Other asset must not migrate"));
        Assert.That(routine.MoveNext(), Is.False);
    }

    [Test]
    public void MigrationFailureStopsApplyAndKeepsItsMessage()
    {
        bool applied = false;
        var preparation = LegacyMigrationService.PrepareApply(7, Task.FromResult(new List<LegacyMigrationService.Match> { Match(7) }),
            () => { }, _ => throw new InvalidOperationException("Import the original base model first."));
        var routine = VersionActions.RunPreparedMutation(preparation, () => applied = true);
        Assert.That(Assert.Throws<InvalidOperationException>(() => routine.MoveNext()).Message, Is.EqualTo("Import the original base model first."));
        Assert.That(applied, Is.False);
    }

    [Test]
    public void LookupFailureStopsApply()
    {
        var preparation = LegacyMigrationService.PrepareApply(7, Task.FromException<List<LegacyMigrationService.Match>>(new InvalidOperationException("Lookup failed")),
            () => Assert.Fail("No validation after failed lookup"), _ => Assert.Fail("No migration after failed lookup"));
        Assert.That(Assert.Throws<InvalidOperationException>(() => preparation.MoveNext()).Message, Is.EqualTo("Lookup failed"));
    }

    [Test]
    public void ChangedAvatarStopsBeforeMigration()
    {
        var preparation = LegacyMigrationService.PrepareApply(7, Task.FromResult(new List<LegacyMigrationService.Match> { Match(7) }),
            () => throw new InvalidOperationException("Avatar changed"), _ => Assert.Fail("No stale migration"));
        Assert.Throws<InvalidOperationException>(() => preparation.MoveNext());
    }

    [Test]
    public void MigratedVersionUsesFreshObjectAndOriginalInsteadOfStaleFiles()
    {
        var stale = new CustomBaseVersion { assetId = 7, version = "5.0.0", sourceVersionKey = "legacy" };
        var current = new CustomBaseVersion { assetId = 7, version = "5.0.0", sourceVersionKey = "original" };
        Assert.That(VersionActions.SelectMigratedVersion(stale, new[] { current }, null), Is.SameAs(current));
        Assert.Throws<InvalidOperationException>(() => VersionActions.SelectMigratedVersion(stale,
            new[] { new CustomBaseVersion { assetId = 9, version = "5.0.0" } }, null));
    }

    [Test]
    public void FailedApplyClearsSessionAndLateProgressCannotRestartIt()
    {
        var session = McbSession.Detached(null);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(VersionActions).GetMethod("BeginApplyProgress", flags).Invoke(session.actions, new object[] { "Preparing", 0f });
        Assert.That(session.isApplying, Is.True);
        typeof(VersionActions).GetMethod("FinishApplyProgress", flags).Invoke(session.actions, new object[] { false });
        typeof(VersionActions).GetMethod("ReportApplyProgress", flags).Invoke(session.actions, new object[] { .5f, "Creating Unity mesh 1/1..." });
        Assert.That(session.isApplying, Is.False);
        Assert.That(session.actions.ApplyProgress.IsRunning, Is.False);
        Assert.That(session.actions.ApplyProgress.StepText, Is.Null);
    }

    [Test]
    public void OfflineLocalApplyDoesNotStartLegacyNetworkLookup()
    {
        var root = new UnityEngine.GameObject("Offline fixture");
        try
        {
            var session = McbSession.Detached(root.AddComponent<MyCustomBase>());
            Assert.That(LegacyMigrationService.PlanApply(session, 7, _ => Assert.Fail("No offline migration")).MoveNext(), Is.False);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}
