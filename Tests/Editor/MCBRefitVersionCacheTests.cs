#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class MCBRefitVersionCacheTests
{
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
            var capture = typeof(MCBReFitIntegration).GetMethod("CaptureRendererState", BindingFlags.Static | BindingFlags.NonPublic);
            var versionB = new CustomBaseVersion { assetId = 999995, version = "0.5.2", defaultAviVersion = "1.0.0" };
            var versionC = new CustomBaseVersion { assetId = 999995, version = "0.5.3", defaultAviVersion = "1.0.0" };
            mcb.appliedCustomBaseVersion = versionB;
            var entry = (RefitAppliedMeshEntry)capture.Invoke(null, new object[] { root.transform, "Jacket", renderer });
            var fitted = UnityEngine.Object.Instantiate(original);
            fitted.vertices = new[] { Vector3.forward, Vector3.right, Vector3.up };
            AssetDatabase.CreateAsset(fitted, fixture + "/fitted.asset");
            renderer.sharedMesh = fitted;
            renderer.localBounds = new Bounds(Vector3.one, Vector3.one * 3);
            renderer.SetBlendShapeWeight(0, 42);
            accessory.transform.localPosition = Vector3.right;
            entry.refitMesh = fitted;
            mcb.appliedRefits.Add(entry);
            var metadataType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Orbiters.ReFit.ReFitGeneratedAssetMetadata")).FirstOrDefault(t => t != null);
            if (metadataType != null)
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
            Assert.That(saved.boneWeights.Select(w => w.weight0), Is.EqualTo(fitted.boneWeights.Select(w => w.weight0)));
            Assert.That(saved.bindposes, Is.EqualTo(fitted.bindposes));

            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            Assert.That(renderer.sharedMesh, Is.SameAs(original));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(17));
            Assert.That(accessory.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionC), Is.Zero);
            Assert.That(MCBReFitIntegration.RestoreVersionFits(mcb, versionB), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh, Is.SameAs(saved));
            Assert.That(renderer.bones[0], Is.SameAs(bone));
            Assert.That(renderer.rootBone, Is.SameAs(bone));
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(42));
            Assert.That(accessory.transform.localPosition, Is.EqualTo(Vector3.right));
            Assert.That(renderer.localBounds, Is.EqualTo(new Bounds(Vector3.one, Vector3.one * 3)));

            MCBReFitIntegration.RestoreOriginalAssetMeshes(mcb);
            mcb.appliedCustomBaseVersion = versionC;
            var entryC = (RefitAppliedMeshEntry)capture.Invoke(null, new object[] { root.transform, "Jacket", renderer });
            renderer.sharedMesh = fitted;
            renderer.SetBlendShapeWeight(0, 80);
            entryC.refitMesh = fitted;
            mcb.appliedRefits.Add(entryC);
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
            MCBReFitIntegration.RestoreAsset(mcb, "Jacket");
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
            if (metadataType != null)
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
