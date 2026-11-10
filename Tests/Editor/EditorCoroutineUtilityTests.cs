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
}
#endif
