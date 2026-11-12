using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class PoseLedgerTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void Dispose() { foreach (var o in owned) if (o != null) Object.DestroyImmediate(o); owned.Clear(); }

    private GameObject Own(GameObject go) { owned.Add(go); return go; }

    private static Transform Child(Transform parent, string name, Vector3 position)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); t.localPosition = position; return t;
    }

    // Model: Armature/Hips/{Leg, Tail}. Avatar: the same, posed by the person (Tail turned, Hips raised).
    private (GameObject model, GameObject avatar, MyCustomBase owner) Rig()
    {
        var model = Own(new GameObject("Model"));
        var mh = Child(Child(model.transform, "Armature", Vector3.zero), "Hips", Vector3.up);
        Child(mh, "Leg", Vector3.left); Child(mh, "Tail", Vector3.back);
        var avatar = Own(new GameObject("Avatar"));
        var owner = avatar.AddComponent<MyCustomBase>();
        var hips = Child(Child(avatar.transform, "Armature", Vector3.zero), "Hips", Vector3.up + new Vector3(0, .03f, 0));
        Child(hips, "Leg", Vector3.left);
        Child(hips, "Tail", Vector3.back).localRotation = Quaternion.Euler(10, 0, 0);
        return (model, avatar, owner);
    }

    [Test] public void InstallKeepsThePersonsPoseAndGoingBackIsExact()
    {
        var (model, avatar, owner) = Rig();
        var hips = avatar.transform.Find("Armature/Hips"); var tail = hips.Find("Tail"); var leg = hips.Find("Leg");
        var before = avatar.GetComponentsInChildren<Transform>().ToDictionary(t => t, t => (t.parent, t.GetSiblingIndex(), t.localPosition, t.localRotation, t.localScale));
        var snapshot = VersionPoseLedgerService.Capture(avatar.transform, new[] { model });
        // The install: every bone to the model rest, then the version moves the leg under a new bone and lengthens it.
        hips.localPosition = Vector3.up; tail.localRotation = Quaternion.identity;
        var top = new GameObject("Top").transform; top.SetParent(hips, false); leg.SetParent(top, false); leg.localPosition = Vector3.left * 2;
        VersionPoseLedgerService.CarryPose(snapshot);
        Assert.That(tail.localRotation, Is.EqualTo(Quaternion.Euler(10, 0, 0)), "a bone the version left at rest ends at the person's pose");
        Assert.That(hips.localPosition, Is.EqualTo(Vector3.up + new Vector3(0, .03f, 0)));
        Assert.That(leg.localPosition, Is.EqualTo(Vector3.left * 2), "an unposed bone the version changed keeps the version's pose");
        VersionPoseLedgerService.Record(owner, snapshot);
        Assert.That(owner.poseLedger.recorded, Is.True);
        // The person turns the tail further while the version is on.
        tail.localRotation = tail.localRotation * Quaternion.Euler(0, 5, 0);
        var pending = VersionPoseLedgerService.BeginRestore(owner, avatar.transform);
        // The reset machinery: the version's bone goes, every bone to the model rest.
        leg.SetParent(hips, false); Object.DestroyImmediate(top.gameObject); leg.localPosition = Vector3.left; hips.localPosition = Vector3.up; tail.localRotation = Quaternion.identity;
        VersionPoseLedgerService.FinishRestore(owner, pending);
        foreach (var pair in before.Where(p => p.Key != tail))
        {
            Assert.That(pair.Key.parent, Is.SameAs(pair.Value.Item1), pair.Key.name);
            Assert.That(pair.Key.GetSiblingIndex(), Is.EqualTo(pair.Value.Item2), pair.Key.name);
            Assert.That(pair.Key.localPosition, Is.EqualTo(pair.Value.Item3), pair.Key.name);
            Assert.That(pair.Key.localRotation, Is.EqualTo(pair.Value.Item4), pair.Key.name);
        }
        Assert.That(Quaternion.Angle(tail.localRotation, Quaternion.Euler(10, 0, 0) * Quaternion.Euler(0, 5, 0)), Is.LessThan(0.01f), "the edit made while installed is kept");
        Assert.That(owner.poseLedger.recorded, Is.False);
    }

    [Test] public void SnapshotRestoresWhatAFailedSwitchLeft()
    {
        var (model, avatar, owner) = Rig();
        var hips = avatar.transform.Find("Armature/Hips");
        var start = VersionPoseLedgerService.CaptureState(avatar.transform);
        var p = hips.localPosition;
        hips.localPosition = Vector3.zero; hips.SetSiblingIndex(0);
        VersionPoseLedgerService.RestoreSnapshot(start);
        Assert.That(hips.localPosition, Is.EqualTo(p));
    }
}
