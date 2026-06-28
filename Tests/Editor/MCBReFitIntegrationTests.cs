using NUnit.Framework;
using UnityEngine;

public sealed class MCBReFitIntegrationTests
{
    [Test]
    public void IsGeneratedEditorOnlyRendererIgnoresXRayGizmosObjects()
    {
        var root = new GameObject("Avatar");
        var overlay = new GameObject("__XRayGizmos_WeightPaint_123");
        overlay.transform.SetParent(root.transform, false);

        try
        {
            var renderer = overlay.AddComponent<SkinnedMeshRenderer>();

            Assert.That(MCBReFitIntegration.IsGeneratedEditorOnlyRenderer(renderer), Is.True);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void IsGeneratedEditorOnlyRendererIgnoresHideAndDontSaveRenderers()
    {
        var go = new GameObject("GeneratedOverlay");

        try
        {
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.hideFlags = HideFlags.HideAndDontSave;

            Assert.That(MCBReFitIntegration.IsGeneratedEditorOnlyRenderer(renderer), Is.True);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void IsGeneratedEditorOnlyRendererKeepsRegularAvatarRenderers()
    {
        var go = new GameObject("Accessory");
        var mesh = new Mesh { name = "AccessoryMesh" };

        try
        {
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;

            Assert.That(MCBReFitIntegration.IsGeneratedEditorOnlyRenderer(renderer), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void IsGeneratedEditorOnlyRendererKeepsRegularRendererWithXRayInName()
    {
        var go = new GameObject("XRayHarnessAccessory");
        var mesh = new Mesh { name = "XRayHarnessMesh" };

        try
        {
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;

            Assert.That(MCBReFitIntegration.IsGeneratedEditorOnlyRenderer(renderer), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(go);
        }
    }
}
