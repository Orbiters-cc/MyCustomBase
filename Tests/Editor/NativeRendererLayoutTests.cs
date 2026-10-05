using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class NativeRendererLayoutTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void Clean() { foreach (var item in owned) if (item != null) Object.DestroyImmediate(item); owned.Clear(); }

    [Test]
    public void OneMeshSlotsFollowTheirNamesAndRestoreTheOriginalOrder()
    {
        var root = Own(new GameObject("Avatar"));
        var owner = root.AddComponent<MyCustomBase>();
        var m = Materials(5);
        var body = Child(root, "Body", m);
        var layout = Layout(("Body", new[] { "MiscMatt", "BodyMatt.001", "EyeMatt", "EyeLensMatt" }), ("Feathers", new[] { "FeathersMatt" }));

        NativeRendererLayoutService.Apply(owner, layout, new Dictionary<SkinnedMeshRenderer, string[]> {
            [body] = new[] { "FeathersMatt", "BodyMatt", "MiscMatt", "EyeMatt", "EyeLensMatt" } });
        Assert.That(body.sharedMaterials, Is.EqualTo(new[] { m[2], m[1], m[3], m[4] }));
        Assert.That(root.transform.Find("Feathers").GetComponent<SkinnedMeshRenderer>().sharedMaterials, Is.EqualTo(new[] { m[0] }));

        owner.appliedCustomization = new VersionCustomization { rendererLayout = layout };
        NativeRendererLayoutService.Restore(owner);
        Assert.That(root.transform.Find("Feathers"), Is.Null);
        Assert.That(body.sharedMaterials, Is.EqualTo(m));
        Assert.That(owner.nativeGeneratedRenderers, Is.Empty);
        Assert.That(owner.nativeRendererOriginalStates, Is.Empty);
    }

    [Test]
    public void AllPiecesHidesOnlyListedOriginalsAndKeepsClothing()
    {
        var root = Own(new GameObject("Avatar"));
        var owner = root.AddComponent<MyCustomBase>();
        var m = Materials(2);
        var body = Child(root, "Body", new[] { m[0] });
        var claws = Child(root, "Claws", new[] { m[1] });
        var clothing = Child(root, "Claws Jacket", new[] { m[1] });
        var layout = Layout(("Body", new[] { "MiscMatt", "BodyMatt" }));
        layout.hide.Add("Claws");
        NativeRendererLayoutService.Apply(owner, layout, new Dictionary<SkinnedMeshRenderer, string[]> {
            [body] = new[] { "BodyMatt" }, [claws] = new[] { "MiscMatt" } });
        Assert.That(body.sharedMaterials, Is.EqualTo(new[] { m[1], m[0] }));
        Assert.That(claws.enabled, Is.False);
        Assert.That(claws.gameObject.activeSelf && clothing.enabled, Is.True);

        owner.appliedCustomization = new VersionCustomization { rendererLayout = layout };
        NativeRendererLayoutService.Restore(owner);
        Assert.That(claws.enabled, Is.True);
        Assert.That(body.sharedMaterials, Is.EqualTo(new[] { m[0] }));
    }

    [Test]
    public void MaterialsTheUserChangedOnCustomSlotsReturnToTheSameOriginalSlots()
    {
        var root = Own(new GameObject("Avatar"));
        var owner = root.AddComponent<MyCustomBase>();
        var m = Materials(3);
        var body = Child(root, "Body", new[] { m[0], m[1] });
        var layout = Layout(("Body", new[] { "BodyMatt", "FeathersMatt", "MiscMatt" }));
        NativeRendererLayoutService.Apply(owner, layout, new Dictionary<SkinnedMeshRenderer, string[]> {
            [body] = new[] { "FeathersMatt", "BodyMatt" } });
        Assert.That(body.sharedMaterials[2], Is.Null, "A slot no original declares stays unassigned.");
        var edited = body.sharedMaterials; edited[0] = m[2]; body.sharedMaterials = edited;

        owner.appliedCustomization = new VersionCustomization { rendererLayout = layout };
        NativeRendererLayoutService.Restore(owner);
        Assert.That(body.sharedMaterials, Is.EqualTo(new[] { m[0], m[2] }));
    }

    [Test]
    public void ARendererOutsideTheOriginalModelBlocksTheLayoutBeforeAnyChange()
    {
        var root = Own(new GameObject("Avatar"));
        var owner = root.AddComponent<MyCustomBase>();
        var m = Materials(1);
        var body = Child(root, "Body", m);
        var clothing = Child(root, "Clothing", m);
        Assert.Throws<InvalidOperationException>(() => NativeRendererLayoutService.Apply(owner,
            Layout(("NewMesh", new[] { "BodyMatt" }), ("Clothing", new[] { "BodyMatt" })),
            new Dictionary<SkinnedMeshRenderer, string[]> { [body] = new[] { "BodyMatt" } }));
        Assert.That(root.transform.Find("NewMesh"), Is.Null);
        Assert.That(owner.nativeRendererOriginalStates, Is.Empty);
        Assert.That(clothing.sharedMaterials, Is.EqualTo(m));
    }

    [Test]
    public void BlenderDuplicateSuffixesCompareAsTheirSlot()
    {
        Assert.That(MaterialSlotNames.Normalize("BodyMatt.001"), Is.EqualTo("BodyMatt"));
        Assert.That(MaterialSlotNames.Normalize(" EyeLensMatt "), Is.EqualTo("EyeLensMatt"));
        Assert.That(MaterialSlotNames.Normalize("Matt.v2"), Is.EqualTo("Matt.v2"));
    }

    [Test]
    public void LayoutValidationRejectsAmbiguousMappings()
    {
        var layout = Layout(("Body", new[] { "BodyMatt" }), ("Body", new[] { "MiscMatt" }));
        Assert.Throws<ArgumentException>(() => layout.Validate());
        layout = Layout(("Body", new[] { "" }));
        Assert.Throws<ArgumentException>(() => layout.Validate());
        layout = Layout(("Body", new[] { "BodyMatt" })); layout.hide.Add("Body");
        Assert.Throws<ArgumentException>(() => layout.Validate());
        layout = Layout(("Body", new[] { "BodyMatt" })); layout.fallbacks.Add(new SlotMaterial { slot = "BodyMatt", material = "not-a-guid" });
        Assert.Throws<ArgumentException>(() => layout.Validate());
        var round = VersionCustomization.Read(Write(new VersionCustomization { rendererLayout = Layout(("Body", new[] { "BodyMatt" })) }));
        Assert.That(round.rendererLayout.renderers.Single().slots, Is.EqualTo(new[] { "BodyMatt" }));
    }

    private static List<object> Write(VersionCustomization customization)
    {
        var entries = new List<object>(); customization.Write(entries); return entries;
    }

    private static RendererLayoutConfiguration Layout(params (string path, string[] slots)[] renderers) => new RendererLayoutConfiguration {
        renderers = renderers.Select(r => new RendererSlotNames { path = r.path, slots = r.slots.ToList() }).ToList() };

    private Material[] Materials(int count) => Enumerable.Range(0, count)
        .Select(i => Own(new Material(Shader.Find("Hidden/InternalErrorShader")) { name = "m" + i })).ToArray();

    private static SkinnedMeshRenderer Child(GameObject root, string name, Material[] materials)
    {
        var go = new GameObject(name); go.transform.SetParent(root.transform);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMaterials = materials;
        return renderer;
    }

    private T Own<T>(T item) where T : Object { owned.Add(item); return item; }
}
