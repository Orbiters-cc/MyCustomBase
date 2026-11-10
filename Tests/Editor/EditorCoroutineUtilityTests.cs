#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class EditorCoroutineUtilityTests
{
    // WaitUntil and WaitWhile hold the coroutine through their public keepWaiting, as they did through their private
    // predicate field.
    [UnityTest]
    public IEnumerator CustomWaitsHoldTheCoroutineUntilTheirConditionChanges()
    {
        bool ready = false, busy = true, passedUntil = false, passedWhile = false;
        IEnumerator Run()
        {
            yield return new WaitUntil(() => ready);
            passedUntil = true;
            yield return new WaitWhile(() => busy);
            passedWhile = true;
        }
        EditorCoroutineUtility.StartCoroutineOwnerless(Run());
        for (int i = 0; i < 5; i++) yield return null;
        Assert.That(passedUntil, Is.False, "WaitUntil waits while its condition is false");
        ready = true;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!passedUntil && DateTime.UtcNow < deadline) yield return null;
        Assert.That(passedUntil, Is.True);
        for (int i = 0; i < 5; i++) yield return null;
        Assert.That(passedWhile, Is.False, "WaitWhile waits while its condition is true");
        busy = false;
        while (!passedWhile && DateTime.UtcNow < deadline) yield return null;
        Assert.That(passedWhile, Is.True);
    }

    // A wait that starts over once it ends, as WaitForSecondsRealtime does: asked through keepWaiting until it ends, once.
    private sealed class Restarting : CustomYieldInstruction
    {
        private int asked;
        public int Ended;
        public override bool keepWaiting
        {
            get
            {
                if (++asked <= 3) return true;
                Ended++;
                asked = 0;
                return false;
            }
        }
    }

    [UnityTest]
    public IEnumerator ACustomWaitIsWaitedOnOnce()
    {
        var wait = new Restarting();
        bool done = false;
        double realtime = -1;
        IEnumerator Run()
        {
            yield return wait;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            yield return new WaitForSecondsRealtime(0.5f);
            realtime = clock.Elapsed.TotalSeconds;
            done = true;
        }
        EditorCoroutineUtility.StartCoroutineOwnerless(Run());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done && DateTime.UtcNow < deadline) yield return null;
        Assert.That(done, Is.True);
        Assert.That(wait.Ended, Is.EqualTo(1), "the wait ended once and the coroutine went on");
        Assert.That(realtime, Is.GreaterThanOrEqualTo(0.45).And.LessThan(0.95), "WaitForSecondsRealtime waited its half second once, not twice");
    }
}
#endif
