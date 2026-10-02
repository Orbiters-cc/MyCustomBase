#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class MCBRefitVersionCacheTests
{
    [Test]
    public void SameNamedAccessoriesKeepTheirOwnSavedFits()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Avatar");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        string fixture = "Assets/MCB-RefitDuplicateTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", fixture.Substring("Assets/".Length));
        string cacheRoot = null;
        try
        {
            var mcb = root.AddComponent<MyCustomBase>();
            var bone = new GameObject("Bone").transform;
            bone.SetParent(root.transform, false);
            var version = new CustomBaseVersion { assetId = 999994, version = "0.5.2", defaultAviVersion = "1.0.0" };
            mcb.appliedCustomBaseVersion = version;
            var renderers = new List<SkinnedMeshRenderer>();
            var originals = new List<Mesh>();
            for (int i = 0; i < 2; i++)
            {
                var accessory = new GameObject("Jacket");
                accessory.transform.SetParent(root.transform, false);
                var renderer = accessory.AddComponent<SkinnedMeshRenderer>();
                var original = new Mesh { name = "Original" + i, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
                original.bindposes = new[] { Matrix4x4.identity };
                original.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
                AssetDatabase.CreateAsset(original, fixture + "/original" + i + ".asset");
                renderer.sharedMesh = original;
                renderer.bones = new[] { bone };
                renderer.rootBone = bone;
                var originalState = RefitRecords.Capture(root.transform, renderer);
                var fitted = UnityEngine.Object.Instantiate(original);
                fitted.vertices = new[] { Vector3.forward * (i + 1), Vector3.right, Vector3.up };
                AssetDatabase.CreateAsset(fitted, fixture + "/fitted" + i + ".asset");
                renderer.sharedMesh = fitted;
                RefitRecords.Register(renderer, originalState, fitted, fixture + "/fitted" + i + ".asset", null, new List<RefitShape>(),
                    OrbitersRefit.FitKind.Fitted, "b", "B", "MCB");
                renderers.Add(renderer);
                originals.Add(original);
            }

            MCBReFitIntegration.SaveVersionFits(mcb, version);
            cacheRoot = "Assets/MCB/refits/" + mcb.mcbComponentId;
            var saved = renderers.Select(renderer => renderer.sharedMesh).ToArray();
            Assert.That(saved[0], Is.Not.SameAs(saved[1]));
            Assert.That(AssetDatabase.FindAssets("t:MCBRefitVersionSnapshot", new[] { MCBReFitIntegration.GetVersionRefitFolder(mcb, version) }),
                Has.Length.EqualTo(2), "Each accessory has its own saved fit.");

            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(renderers.Select(renderer => renderer.sharedMesh), Is.EqualTo(originals));
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, version), Is.EqualTo(2));
            Assert.That(renderers.Select(renderer => renderer.sharedMesh), Is.EqualTo(saved), "Each fit returns to its own accessory.");

            MCBReFitIntegration.RestoreAsset(mcb, renderers[1]);
            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, version), Is.EqualTo(1));
            Assert.That(renderers[0].sharedMesh, Is.SameAs(saved[0]));
            Assert.That(renderers[1].sharedMesh, Is.SameAs(originals[1]), "Only the removed accessory's fit stops being restored.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            if (cacheRoot != null) AssetDatabase.DeleteAsset(cacheRoot);
            AssetDatabase.DeleteAsset(fixture);
        }
    }

    [Test]
    public void SameNamedAccessoriesComeBackOnTheirOwnBonesInPlace()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Avatar");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        string fixture = "Assets/MCB-RefitDuplicateBonesTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", fixture.Substring("Assets/".Length));
        string cacheRoot = null;
        try
        {
            var mcb = root.AddComponent<MyCustomBase>();
            var version = new CustomBaseVersion { assetId = 999993, version = "0.5.2", defaultAviVersion = "1.0.0" };
            mcb.appliedCustomBaseVersion = version;
            var renderers = new List<SkinnedMeshRenderer>();
            var bones = new List<Transform>();
            var originals = new List<Mesh>();
            // Two accessories called "Jacket", each skinned to its own "Bone", at their own places.
            for (int i = 0; i < 2; i++)
            {
                var accessory = new GameObject("Jacket");
                accessory.transform.SetParent(root.transform, false);
                accessory.transform.localPosition = Vector3.right * (i + 1);
                var bone = new GameObject("Bone").transform;
                bone.SetParent(accessory.transform, false);
                bone.localPosition = Vector3.up * (i + 1);
                var renderer = accessory.AddComponent<SkinnedMeshRenderer>();
                var original = new Mesh { name = "Original" + i, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
                original.bindposes = new[] { Matrix4x4.identity };
                original.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
                AssetDatabase.CreateAsset(original, fixture + "/original" + i + ".asset");
                renderer.sharedMesh = original;
                renderer.bones = new[] { bone };
                renderer.rootBone = bone;
                var originalState = RefitRecords.Capture(root.transform, renderer);
                var fitted = UnityEngine.Object.Instantiate(original);
                fitted.vertices = new[] { Vector3.forward * (i + 1), Vector3.right, Vector3.up };
                AssetDatabase.CreateAsset(fitted, fixture + "/fitted" + i + ".asset");
                renderer.sharedMesh = fitted;
                RefitRecords.Register(renderer, originalState, fitted, fixture + "/fitted" + i + ".asset", null, new List<RefitShape>(),
                    OrbitersRefit.FitKind.Fitted, "b", "B", "MCB");
                renderers.Add(renderer);
                bones.Add(bone);
                originals.Add(original);
            }
            var order = root.GetComponentsInChildren<Transform>(true);
            void AssertInPlace(string when)
            {
                for (int i = 0; i < 2; i++)
                {
                    Assert.That(renderers[i].bones, Is.EqualTo(new[] { bones[i] }), when + ": each jacket keeps its own bone.");
                    Assert.That(renderers[i].rootBone, Is.SameAs(bones[i]), when);
                    Assert.That(renderers[i].transform.localPosition, Is.EqualTo(Vector3.right * (i + 1)), when + ": no jacket is moved.");
                    Assert.That(bones[i].localPosition, Is.EqualTo(Vector3.up * (i + 1)), when);
                }
                Assert.That(root.GetComponentsInChildren<Transform>(true), Is.EqualTo(order), when + ": nothing changes places.");
            }

            MCBReFitIntegration.SaveVersionFits(mcb, version);
            cacheRoot = "Assets/MCB/refits/" + mcb.mcbComponentId;
            var saved = renderers.Select(renderer => renderer.sharedMesh).ToArray();
            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(renderers.Select(renderer => renderer.sharedMesh), Is.EqualTo(originals));
            AssertInPlace("Reset");

            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, version), Is.EqualTo(2));
            Assert.That(renderers.Select(renderer => renderer.sharedMesh), Is.EqualTo(saved));
            AssertInPlace("Version fits restored");

            // The restored records keep their originals by path only: resetting again must still tell the jackets apart.
            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(renderers.Select(renderer => renderer.sharedMesh), Is.EqualTo(originals));
            AssertInPlace("Reset from saved originals");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            if (cacheRoot != null) AssetDatabase.DeleteAsset(cacheRoot);
            AssetDatabase.DeleteAsset(fixture);
        }
    }

    [Test]
    public void SavedFitsRoundTripAcrossBaseAndTwoVersionsAndRespectManualRemoval()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Avatar");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        string fixture = "Assets/MCB-RefitTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", fixture.Substring("Assets/".Length));
        string cacheRoot = null;
        try
        {
            var mcb = root.AddComponent<MyCustomBase>();
            var bone = new GameObject("Bone").transform;
            bone.SetParent(root.transform, false);
            var accessory = new GameObject("Jacket");
            accessory.transform.SetParent(root.transform, false);
            var renderer = accessory.AddComponent<SkinnedMeshRenderer>();
            var original = new Mesh { name = "Original", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            original.bindposes = new[] { Matrix4x4.identity };
            original.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            original.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
            AssetDatabase.CreateAsset(original, fixture + "/original.asset");
            renderer.sharedMesh = original;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;
            renderer.SetBlendShapeWeight(0, 17);
            var versionB = new CustomBaseVersion { assetId = 999995, version = "0.5.2", defaultAviVersion = "1.0.0" };
            var versionC = new CustomBaseVersion { assetId = 999995, version = "0.5.3", defaultAviVersion = "1.0.0" };
            mcb.appliedCustomBaseVersion = versionB;
            var originalState = RefitRecords.Capture(root.transform, renderer);
            var fitted = UnityEngine.Object.Instantiate(original);
            fitted.vertices = new[] { Vector3.forward, Vector3.right, Vector3.up };
            AssetDatabase.CreateAsset(fitted, fixture + "/fitted.asset");
            renderer.sharedMesh = fitted;
            renderer.localBounds = new Bounds(Vector3.one, Vector3.one * 3);
            renderer.SetBlendShapeWeight(0, 42);
            accessory.transform.localPosition = Vector3.right;
            RefitRecords.Register(renderer, originalState, fitted, fixture + "/fitted.asset", null, new List<RefitShape>(),
                OrbitersRefit.FitKind.Fitted, "b", "B", "MCB");
            var metadataType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Orbiters.ReFit.ReFitGeneratedAssetMetadata")).FirstOrDefault(t => t != null);
            if (metadataType != null && RefitEngine.Available)
            {
                var metadata = accessory.AddComponent(metadataType);
                var dataField = metadataType.GetField("data");
                var data = Activator.CreateInstance(dataField.FieldType);
                dataField.FieldType.GetField("targetIdentity").SetValue(data, "saved-binding");
                dataField.SetValue(metadata, data);
            }
            MCBReFitIntegration.SaveVersionFits(mcb, versionB);
            cacheRoot = "Assets/MCB/refits/" + mcb.mcbComponentId;
            var saved = renderer.sharedMesh;
            Assert.That(AssetDatabase.GetAssetPath(saved), Does.StartWith(MCBReFitIntegration.GetVersionRefitFolder(mcb, versionB)));
            Assert.That(accessory.GetComponent<OrbitersRefit>().mesh, Is.SameAs(saved), "The record follows the saved copy.");
            Assert.That(saved.boneWeights.Select(w => w.weight0), Is.EqualTo(fitted.boneWeights.Select(w => w.weight0)));
            Assert.That(saved.bindposes, Is.EqualTo(fitted.bindposes));

            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(renderer.sharedMesh, Is.SameAs(original));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(accessory.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(accessory.GetComponent<OrbitersRefit>(), Is.Null);
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionC), Is.Zero);
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionB), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh, Is.SameAs(saved));
            Assert.That(renderer.bones[0], Is.SameAs(bone));
            Assert.That(renderer.rootBone, Is.SameAs(bone));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(42));
            Assert.That(accessory.transform.localPosition, Is.EqualTo(Vector3.right));
            Assert.That(renderer.localBounds, Is.EqualTo(new Bounds(Vector3.one, Vector3.one * 3)));
            Assert.That(RefitRecords.IsApplied(renderer), Is.True);
            Assert.That(accessory.GetComponent<OrbitersRefit>().original.mesh, Is.SameAs(original));

            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            mcb.appliedCustomBaseVersion = versionC;
            var originalC = RefitRecords.Capture(root.transform, renderer);
            renderer.sharedMesh = fitted;
            renderer.SetBlendShapeWeight(0, 80);
            RefitRecords.Register(renderer, originalC, fitted, fixture + "/fitted.asset", null, new List<RefitShape>(),
                OrbitersRefit.FitKind.Fitted, "c", "C", "My Avatar");
            MCBReFitIntegration.SaveVersionFits(mcb, versionC);
            var savedC = renderer.sharedMesh;
            Assert.That(savedC, Is.Not.SameAs(saved));
            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionB), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh, Is.SameAs(saved));
            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionC), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh, Is.SameAs(savedC));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(80));
            Assert.That(accessory.GetComponent<OrbitersRefit>().tool, Is.EqualTo("My Avatar"), "Fits of every tool are kept per version.");
            MCBReFitIntegration.RestoreAsset(mcb, renderer);
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionC), Is.Zero);
            renderer.sharedMesh = fitted; // User replaced this accessory: do not overwrite it.
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionB), Is.Zero);

            // Reload persisted snapshots, then bind them onto a new scene hierarchy with the same saved identity.
            string id = mcb.mcbComponentId;
            UnityEngine.Object.DestroyImmediate(root);
            root = new GameObject("Avatar Reloaded");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            mcb = root.AddComponent<MyCustomBase>();
            mcb.mcbComponentId = id;
            var newBone = new GameObject("Bone").transform;
            newBone.SetParent(root.transform, false);
            accessory = new GameObject("Jacket");
            accessory.transform.SetParent(root.transform, false);
            renderer = accessory.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = original;
            renderer.bones = new[] { newBone };
            renderer.rootBone = newBone;
            foreach (var guid in AssetDatabase.FindAssets("t:MCBRefitVersionSnapshot", new[] { cacheRoot }))
                Resources.UnloadAsset(AssetDatabase.LoadAssetAtPath<MCBRefitVersionSnapshot>(AssetDatabase.GUIDToAssetPath(guid)));
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionB), Is.EqualTo(1));
            Assert.That(renderer.bones[0], Is.SameAs(newBone));
            Assert.That(renderer.rootBone, Is.SameAs(newBone));
            if (metadataType != null && RefitEngine.Available)
            {
                var metadata = renderer.GetComponent(metadataType);
                Assert.That(metadata, Is.Not.Null);
                Assert.That(metadata.gameObject, Is.SameAs(accessory));
                var data = metadataType.GetField("data").GetValue(metadata);
                Assert.That(data.GetType().GetField("targetIdentity").GetValue(data), Is.EqualTo("saved-binding"));
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            if (cacheRoot != null) AssetDatabase.DeleteAsset(cacheRoot);
            AssetDatabase.DeleteAsset(fixture);
        }
    }
}
#endif
