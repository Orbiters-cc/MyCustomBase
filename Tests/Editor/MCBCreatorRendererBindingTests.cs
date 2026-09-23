#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class MCBCreatorRendererBindingTests
{
    [Test]
    public void SourceReorderingKeepsCustomFilesAndAvatarWithTheirOriginalModel()
    {
        var root = new GameObject("creator");
        var body = new GameObject("bodySource");
        var hair = new GameObject("hairSource");
        var newSource = new GameObject("newSource");
        try
        {
            var target = root.AddComponent<MyCustomBase>();
            var bodyEntry = new CreatorModelFileBuildEntry { externalCustomFbxPath = "body.fbx" };
            var hairEntry = new CreatorModelFileBuildEntry { externalCustomFbxPath = "hair.fbx" };
            target.baseFbxFiles.AddRange(new[] { body, hair });
            target.modelFileBuildEntries.AddRange(new[] { bodyEntry, hairEntry });
            FileManagerService.SetCreatorSourceFiles(target, new[] { hair, newSource, body });
            Assert.AreSame(hairEntry, target.modelFileBuildEntries[0]);
            Assert.IsTrue(string.IsNullOrEmpty(target.modelFileBuildEntries[1].externalCustomFbxPath));
            Assert.AreSame(bodyEntry, target.modelFileBuildEntries[2]);
            FileManagerService.SetCreatorSourceFiles(target, new[] { body });
            Assert.AreEqual(1, target.modelFileBuildEntries.Count);
            Assert.AreSame(bodyEntry, target.modelFileBuildEntries[0]);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(body);
            UnityEngine.Object.DestroyImmediate(hair);
            UnityEngine.Object.DestroyImmediate(newSource);
        }
    }

    [Test]
    public void EmptyModelEntriesDoNotRequireSourceFilesOrCreatePatches()
    {
        string folder = "Assets/MCBEmptyVersionTest-" + Guid.NewGuid().ToString("N");
        try
        {
            new FileManagerService().PopulateVersionFolder(folder, new[] {
                new FileManagerService.ModelFilePackageEntry(),
                new FileManagerService.ModelFilePackageEntry { sourceFbxPath = "missing-but-unchanged.fbx" }
            }, null, false, null, false, false);
            Assert.IsEmpty(System.IO.Directory.GetFiles(folder, "*.bin", System.IO.SearchOption.AllDirectories));
        }
        finally { AssetDatabase.DeleteAsset(folder); }
    }

    [Test]
    public void BlenderTargetUsesCreatorAssetPathAndRejectsUnknownTarget()
    {
        string folder = "Assets/MCBTargetIndexTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring(7));
        var root = new GameObject("creator");
        var model = new GameObject("source");
        try
        {
            var body = PrefabUtility.SaveAsPrefabAsset(model, folder + "/Body.prefab");
            var hair = PrefabUtility.SaveAsPrefabAsset(model, folder + "/Hair.prefab");
            var target = root.AddComponent<MyCustomBase>();
            target.baseFbxFiles.AddRange(new[] { hair, body });
            var method = typeof(BlenderSyncService).GetMethod("GetCreatorTargetIndex", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.AreEqual(1, method.Invoke(null, new object[] { target, folder + "/Body.prefab" }));
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { target, folder + "/Unknown.prefab" }));
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(model);
            AssetDatabase.DeleteAsset(folder);
        }
    }

    [Test]
    public void MixedGeneratedBodyAndFbxHairKeepCompleteModelBindings()
    {
        string folder = "Assets/MCBRendererBindingTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        string path = folder + "/Body.asset";
        var source = new GameObject("source");
        var target = new GameObject("target");
        Mesh generated = null;
        try
        {
            var body = new Mesh { name = "Body" }; AssetDatabase.CreateAsset(body, path);
            var hair = new Mesh { name = "Hair" }; AssetDatabase.AddObjectToAsset(hair, path);
            foreach (var mesh in new[] { body, hair })
            {
                var original = new GameObject(mesh.name); original.transform.SetParent(source.transform);
                original.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var applied = new GameObject(mesh.name); applied.transform.SetParent(target.transform);
                applied.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            }
            generated = UnityEngine.Object.Instantiate(body); generated.name = "Body (DynamicNormals)";
            target.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh = generated;
            var outfit = new GameObject("Outfit"); outfit.transform.SetParent(target.transform);
            var unrelated = new GameObject("Body"); unrelated.transform.SetParent(outfit.transform);
            unrelated.AddComponent<SkinnedMeshRenderer>().sharedMesh = generated;
            SmrPathService.InvalidateCache();
            Assert.That(SmrPathService.CollectSmrPathsForFbx(target.transform, path).Count, Is.EqualTo(1));
            var bindings = SmrPathService.CollectModelRendererBindings(target.transform, path, source);
            Assert.That(bindings.Select(b => b.avatarPath), Is.EquivalentTo(new[] { "Body", "Hair" }));
            Assert.That(bindings.First(b => b.avatarPath == "Body").meshName, Is.EqualTo("Body"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(target);
            if (generated != null) UnityEngine.Object.DestroyImmediate(generated);
            AssetDatabase.DeleteAsset(folder); SmrPathService.InvalidateCache();
        }
    }
}
#endif
