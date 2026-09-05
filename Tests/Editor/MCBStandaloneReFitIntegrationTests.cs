using System;
using NUnit.Framework;
using UnityEngine;

public sealed class MCBStandaloneReFitIntegrationTests
{
    private sealed class FakeRendererState
    {
#pragma warning disable 0414
        private readonly Mesh mesh;
        private readonly Transform snapshotRoot;
        private readonly Transform[] bones;
        private readonly string[] bonePaths;
        private readonly Transform rootBone;
        private readonly string rootBonePath;
        private readonly bool updateWhenOffscreen;
        private readonly Bounds localBounds;
        private readonly string[] blendShapeNames;
        private readonly float[] blendShapeWeights;
        private readonly object[] transformStates = Array.Empty<object>();
#pragma warning restore 0414

        public FakeRendererState(Transform root, SkinnedMeshRenderer renderer)
        {
            mesh = renderer.sharedMesh;
            snapshotRoot = root;
            bones = renderer.bones;
            bonePaths = Array.Empty<string>();
            rootBone = renderer.rootBone;
            rootBonePath = null;
            updateWhenOffscreen = renderer.updateWhenOffscreen;
            localBounds = renderer.localBounds;
            blendShapeNames = Array.Empty<string>();
            blendShapeWeights = Array.Empty<float>();
        }
    }

    [Test]
    public void RegisterStandaloneRefitTracksSynchronizesAndRestoresAsset()
    {
        var root = new GameObject("Avatar");
        var bodyObject = new GameObject("Body");
        var assetObject = new GameObject("Jacket");
        bodyObject.transform.SetParent(root.transform, false);
        assetObject.transform.SetParent(root.transform, false);
        var originalMesh = CreateMeshWithBlendShape("Original", "OriginalShape");
        var bodyMesh = CreateMeshWithBlendShape("Body", "Smile");
        var refitMesh = CreateMeshWithBlendShape("Jacket_ReFit", "refit_Smile");
        int stateChangeCount = 0;
        string changedPath = null;
        Action<MyCustomBase, string> stateChanged = (changedMcb, path) =>
        {
            if (changedMcb == root.GetComponent<MyCustomBase>())
            {
                stateChangeCount++;
                changedPath = path;
            }
        };
        MCBReFitIntegration.RefitStateChanged += stateChanged;

        try
        {
            var mcb = root.AddComponent<MyCustomBase>();
            var body = bodyObject.AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = bodyMesh;
            body.SetBlendShapeWeight(0, 42f);
            var asset = assetObject.AddComponent<SkinnedMeshRenderer>();
            asset.sharedMesh = originalMesh;
            var snapshot = new FakeRendererState(root.transform, asset);
            asset.sharedMesh = refitMesh;

            Assert.That(MCBReFitIntegration.RegisterStandaloneRefit(
                root, asset, snapshot, refitMesh, "Assets/ReFit/Jacket.asset",
                new[] { "Smile" }, new[] { "refit_Smile" }), Is.True);
            Assert.That(MCBReFitIntegration.IsRefitApplied(mcb, asset), Is.True);
            Assert.That(asset.GetBlendShapeWeight(0), Is.EqualTo(42f));
            Assert.That(stateChangeCount, Is.EqualTo(1));
            Assert.That(changedPath, Is.EqualTo("Jacket"));

            MCBReFitIntegration.RestoreAsset(mcb, "Jacket");

            Assert.That(asset.sharedMesh, Is.SameAs(originalMesh));
            Assert.That(mcb.appliedRefits, Is.Empty);
            Assert.That(stateChangeCount, Is.EqualTo(2));
        }
        finally
        {
            MCBReFitIntegration.RefitStateChanged -= stateChanged;
            UnityEngine.Object.DestroyImmediate(originalMesh);
            UnityEngine.Object.DestroyImmediate(bodyMesh);
            UnityEngine.Object.DestroyImmediate(refitMesh);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PrefixedStandaloneBlendshapeIsDetectedWithoutStoredMapping()
    {
        var root = new GameObject("Avatar");
        var assetObject = new GameObject("Jacket");
        assetObject.transform.SetParent(root.transform, false);
        var mesh = CreateMeshWithBlendShape("Jacket", "refit_Smile");

        try
        {
            var mcb = root.AddComponent<MyCustomBase>();
            assetObject.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;

            var names = MCBReFitIntegration.GetBlendShapeNamesWithTransferredReFit(mcb, "Smile");

            Assert.That(names, Does.Contain("refit_Smile"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static Mesh CreateMeshWithBlendShape(string name, string blendShapeName)
    {
        var mesh = new Mesh { name = name };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        mesh.triangles = new[] { 0, 1, 2 };
        var delta = new[] { Vector3.zero, Vector3.zero, Vector3.zero };
        mesh.AddBlendShapeFrame(blendShapeName, 100f, delta, delta, delta);
        return mesh;
    }
}
