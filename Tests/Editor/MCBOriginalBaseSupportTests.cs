#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

public class MCBOriginalBaseSupportTests
{
    [Test]
    public void OriginalDraftSurvivesSerializationWithItsNamedFileMappings()
    {
        var window = ScriptableObject.CreateInstance<OriginalBaseSupportWindow>();
        var type = typeof(OriginalBaseSupportWindow); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            type.GetField("asset", flags).SetValue(window, new AvatarDiscoveredAsset { id = 9876124 });
            type.GetField("assetJson", flags).SetValue(window, "{\"id\":9876124}");
            type.GetField("loaded", flags).SetValue(window, true);
            var drafts = (List<OriginalBaseVersionsEditor.Draft>)type.GetField("drafts", flags).GetValue(window);
            var draft = new OriginalBaseVersionsEditor.Draft { label = "Original 2" };
            draft.candidates.Add(("Base.fbx", new string('b', 64))); draft.hashes["Assets/Base.fbx"] = new string('b', 64); drafts.Add(draft);
            type.GetMethod("SaveState", flags).Invoke(window, null);
            string state = (string)type.GetField("stateJson", flags).GetValue(window); drafts.Clear();
            // Simulate hot reload retaining primitives while dropping the collection fields.
            type.GetField("stateJson", flags).SetValue(window, state);
            type.GetMethod("RecoverContext", flags).Invoke(window, null);
            var restored = drafts.Single();
            var original = OriginalBaseVersionsEditor.Build(new[] { restored }, new[] { "Assets/Base.fbx" }).Single();
            Assert.AreEqual("Original 2", original.label); Assert.AreEqual(draft.candidates[0], restored.candidates[0]);
            Assert.AreEqual(draft.hashes["Assets/Base.fbx"], original.sourceFiles[0].hash);
        }
        finally { UnityEngine.Object.DestroyImmediate(window); SessionState.EraseString("MCB.SourceSupport.0.9876124"); }
    }

    [Test]
    public void SupportWizardRequiresReadyOriginalsAndSearchPreservesOtherSelections()
    {
        var window = ScriptableObject.CreateInstance<OriginalBaseSupportWindow>();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var panelType = typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.Panel");
        var panel = (IPanel)panelType.GetMethod("CreateEditorPanel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { window });
        panel.visualTree.Add(window.rootVisualElement);
        void Set(string field, object value) => typeof(OriginalBaseSupportWindow).GetField(field, flags).SetValue(window, value);
        try
        {
            var source = new ModelFileData { path = "Assets/Base.fbx", hash = new string('a', 64) };
            Set("asset", new AvatarDiscoveredAsset { id = 9876123, name = "Example base", sourceFiles = new[] { source } });
            Set("originals", new[] { new OriginalBaseVersionData { label = "Original 1", key = OriginalBaseLibrary.Key(new[] { source }), sourceFiles = new[] { source } } });
            Set("loaded", true);
            window.CreateGUI();
            Assert.IsFalse(window.rootVisualElement.Q<Button>("mcb-support-primary").enabledSelf);
            var drafts = (List<OriginalBaseVersionsEditor.Draft>)typeof(OriginalBaseSupportWindow).GetField("drafts", flags).GetValue(window);
            var draft = new OriginalBaseVersionsEditor.Draft { label = "Original 2" }; draft.candidates.Add(("Base.fbx", new string('b', 64))); draft.hashes[source.path] = new string('b', 64); drafts.Add(draft);
            window.CreateGUI(); Assert.IsTrue(window.rootVisualElement.Q<Button>("mcb-support-primary").enabledSelf);
            Set("step", 1);
            Set("versions", new[] { new CustomBaseVersion { version = "0.5.3", title = "New shapes" }, new CustomBaseVersion { version = "0.5.2", title = "Earlier release" } });
            var selected = (HashSet<string>)typeof(OriginalBaseSupportWindow).GetField("selected", flags).GetValue(window); selected.UnionWith(new[] { "0.5.3", "0.5.2" });
            window.CreateGUI();
            window.rootVisualElement.Q<ToolbarSearchField>().value = "New shapes";
            Assert.IsNotNull(window.rootVisualElement.Q<Toggle>("mcb-support-version-0.5.3"));
            Assert.IsNull(window.rootVisualElement.Q<Toggle>("mcb-support-version-0.5.2"));
            Assert.AreEqual(2, selected.Count);
            window.rootVisualElement.Q<Toggle>("mcb-support-version-0.5.3").value = false;
            CollectionAssert.AreEqual(new[] { "0.5.2" }, selected);
            typeof(OriginalBaseSupportWindow).GetMethod("SelectVersions", flags).Invoke(window, new object[] { false });
            Assert.AreEqual("Register originals", window.rootVisualElement.Q<Button>("mcb-support-primary").text);
        }
        finally { window.rootVisualElement.RemoveFromHierarchy(); ((IDisposable)panel).Dispose(); UnityEngine.Object.DestroyImmediate(window); SessionState.EraseString("MCB.SourceSupport.0.9876123"); }
    }

    static string Hash(byte[] bytes) { using (var sha = MCBHashing.CreateSha256()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    [Test]
    public void RekeyReconstructsIdenticalCustomBytesForTwoOriginalsAndRejectsWrongKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcb-source-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            byte[] a = { 1, 4, 7 }, b = { 9, 8, 7, 6, 5 }, custom = { 10, 22, 31, 44, 55, 61, 71 };
            File.WriteAllBytes(Path.Combine(root, "a.key"), a); File.WriteAllBytes(Path.Combine(root, "b.key"), b);
            byte[] encrypted = MCBXor.Transform(a, custom); File.WriteAllBytes(Path.Combine(root, "mesh.bin"), encrypted);
            var original = new ModelFileData { id = 1, path = "Assets/Base.fbx", hash = Hash(a), role = "SOURCE", type = "FBX" };
            var target = new ModelFileData { id = 2, path = original.path, hash = Hash(b), role = "SOURCE", type = "FBX" };
            var version = new CustomBaseVersion { sourceFiles = new[] { original }, versionFiles = new[] {
                new ModelFileData { path = "mesh.bin", hash = Hash(encrypted), outputHash = Hash(custom), sourceModelFileId = 1,
                    role = "PATCH", type = "BIN", transform = ModelFileTransforms.XorBinToFbx, metadata = new Dictionary<string, object> { { "sourcePath", original.path }, { "sourceHash", original.hash } } } } };
            var variant = OriginalBaseVariantBuilder.Build(root, version, new OriginalBaseVersionData { label = "2.0", sourceFiles = new[] { target } },
                source => Path.Combine(root, source.hash == original.hash ? "a.key" : "b.key"));
            byte[] encoded = File.ReadAllBytes(Path.Combine(root, variant.versionFiles[0].path));
            CollectionAssert.AreEqual(custom, MCBXor.Transform(b, encoded));
            CollectionAssert.AreEqual(encrypted, File.ReadAllBytes(Path.Combine(root, "mesh.bin")));
            Assert.AreEqual(Hash(custom), variant.versionFiles[0].outputHash);
            Assert.AreEqual(2, variant.versionFiles[0].sourceModelFileId);
            Assert.Throws<InvalidDataException>(() => OriginalBaseVariantBuilder.Build(root, version, variant, source => Path.Combine(root, "b.key")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public void RekeyPreservesEveryNativeMeshCodecAndContentIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcb-codec-rekey-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            byte[] a = { 1, 2, 3 }, b = { 5, 6, 7, 8 }, raw = Enumerable.Range(0, 32768).Select(i => (byte)(i % 251)).ToArray();
            File.WriteAllBytes(Path.Combine(root, "a"), a); File.WriteAllBytes(Path.Combine(root, "b"), b);
            var codecs = new List<MCBPayloadVariant>();
            foreach (string codec in new[] { MCBCompression.Zstd, MCBCompression.Lz4 })
            {
                byte[] encoded = MCBCompression.Encode(raw, codec), encrypted = MCBXor.Transform(a, encoded);
                string name = "mesh-" + codec + ".bin"; File.WriteAllBytes(Path.Combine(root, name), encrypted);
                codecs.Add(new MCBPayloadVariant { path = name, codec = codec, hash = Hash(encrypted), outputHash = Hash(encoded), bytes = encrypted.Length, decodedBytes = raw.Length });
            }
            var first = codecs[0];
            var source = new ModelFileData { id=1, path="Assets/Base.fbx", hash=Hash(a) };
            var target = new OriginalBaseVersionData { label="2", sourceFiles=new[] { new ModelFileData { id=2,path=source.path,hash=Hash(b) } } };
            var version = new CustomBaseVersion { sourceFiles=new[]{source},versionFiles=new[]{new ModelFileData { path=first.path,hash=first.hash,outputHash=first.outputHash,
                compression=first.codec,transform=NativeMeshPayloadService.TransformName,sourceModelFileId=1,metadata=new Dictionary<string,object> { {"sourcePath",source.path},{"sourceHash",source.hash},{"contentHash",Hash(raw)},{"deliveryVariants",codecs},{"payloadCompression",first.codec} } } } };
            var result = OriginalBaseVariantBuilder.Build(root,version,target,f=>Path.Combine(root,f.hash==source.hash?"a":"b"));
            Assert.AreEqual(Hash(raw),Convert.ToString(result.versionFiles[0].metadata["contentHash"]));
            foreach(var codec in MCBVersionDelivery.GetVariants(result.versionFiles[0]))
            {
                byte[] data=File.ReadAllBytes(Path.Combine(root,codec.path));
                Assert.AreEqual(codec.hash,Hash(data));
                CollectionAssert.AreEqual(raw,MCBCompression.Decode(MCBXor.Transform(b,data),codec.codec));
            }
            // A downloaded single-codec archive uses the primary filename even for LZ4.
            File.Delete(Path.Combine(root, first.path));
            File.Move(Path.Combine(root, codecs[1].path), Path.Combine(root, first.path));
            var single = OriginalBaseVariantBuilder.Build(root, version, target, f => Path.Combine(root, f.hash == source.hash ? "a" : "b"));
            Assert.AreEqual(MCBCompression.Lz4, single.versionFiles[0].compression);
            Assert.AreEqual(codecs[1].outputHash, single.versionFiles[0].outputHash);
            CollectionAssert.AreEqual(raw, MCBCompression.Decode(MCBXor.Transform(b, File.ReadAllBytes(Path.Combine(root, single.versionFiles[0].path))), MCBCompression.Lz4));
        }
        finally { Directory.Delete(root,true); }
    }

    [Test]
    public void DownloadsAndAvailableVersionsStayBoundToTheirOriginalFiles()
    {
        var first = new CustomBaseVersion { assetId = 123, version = "1.0.0", defaultAviVersion = "base", sourceVersionKey = new string('a', 64) };
        var second = new CustomBaseVersion { assetId = first.assetId, version = first.version, defaultAviVersion = first.defaultAviVersion, sourceVersionKey = new string('b', 64) };
        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(MCBUtils.GetVersionDataPath(first), MCBUtils.GetVersionDataPath(second));
        var visible = VersionRepository.MergeAvailableVersions(123, new[] { second }, new[] { first }, null, second.sourceVersionKey);
        CollectionAssert.AreEqual(new[] { second }, visible);
        Assert.Throws<ArgumentException>(() => MCBUtils.GetVersionDataPath(123, "1.0.0", "base", "../other"));
    }

    [Test]
    public void PhotoshootPosePreservesCustomEyePositionAndScale()
    {
        var root = new GameObject("root"); var clip = new AnimationClip(); var mesh = new Mesh();
        try
        {
            var eye = new GameObject("Eye").transform; eye.SetParent(root.transform); eye.localPosition = new Vector3(0, 1, 2); eye.localScale = Vector3.one * 1.2f;
            var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh; renderer.bones = new[] { eye };
            clip.SetCurve("Eye", typeof(Transform), "localPosition.z", AnimationCurve.Constant(0, 1, 0));
            clip.SetCurve("Eye", typeof(Transform), "localScale.x", AnimationCurve.Constant(0, 1, 1));
            typeof(PhotoshootGenerationService).GetMethod("SampleBodyPose", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { root, clip });
            Assert.AreEqual(new Vector3(0, 1, 2), eye.localPosition); Assert.AreEqual(Vector3.one * 1.2f, eye.localScale);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(clip); UnityEngine.Object.DestroyImmediate(mesh); }
    }
    [Test]
    public void BoundsEncloseSkinnedVerticesWithRotatedRootBone()
    {
        var root = new GameObject("root"); var mesh = new Mesh(); var baked = new Mesh();
        try
        {
            var bone = new GameObject("Hips").transform; bone.SetParent(root.transform); bone.localRotation = Quaternion.Euler(90, 0, 0); bone.localPosition = Vector3.up;
            var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.rootBone = bone; renderer.bones = new[] { bone };
            mesh.vertices = new[] { new Vector3(-.2f, 1.5f, 0), new Vector3(.2f, 1.5f, 0), new Vector3(0, 2, 0) };
            mesh.triangles = new[] { 0, 1, 2 }; mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            mesh.bindposes = new[] { bone.worldToLocalMatrix * root.transform.localToWorldMatrix }; mesh.RecalculateBounds(); renderer.sharedMesh = mesh;
            renderer.localBounds = mesh.bounds;
            SkinnedMeshBoundsService.Refresh(renderer); renderer.BakeMesh(baked);
            foreach (var vertex in baked.vertices) Assert.IsTrue(renderer.bounds.Contains(renderer.transform.TransformPoint(vertex)), "Culling bounds exclude posed geometry.");
            Assert.IsFalse(renderer.updateWhenOffscreen, "The scene fix must not enable perpetual offscreen skinning.");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(baked); }
    }
}
#endif
