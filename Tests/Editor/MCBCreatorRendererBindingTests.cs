#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class MCBCreatorRendererBindingTests
{
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
