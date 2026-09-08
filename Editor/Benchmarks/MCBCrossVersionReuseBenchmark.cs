#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using MCBEditorUtils;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Explicit local integration fixture. Uses a preview scene, never saves the user's scene.</summary>
public static class MCBCrossVersionReuseBenchmark
{
    public static string Status { get; private set; }
    static readonly List<object> results = new List<object>();
    static string sourcePath, referencePath, rootFolder;
    static GameObject target;
    static MCBEditor editor;
    static UnityEngine.SceneManagement.Scene preview;
    public static void Build(string source, string reference, string output)
    {
        sourcePath = source; referencePath = reference; rootFolder = output;
        Directory.CreateDirectory(output); results.Clear();
        var baseModel = AssetDatabase.LoadAssetAtPath<GameObject>(source);
        var model = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(reference));
        model.hideFlags = HideFlags.HideAndDontSave;
        string scratch = "Assets/MCB/ReuseBenchmark-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(scratch); AssetDatabase.Refresh();
        Mesh changedHair = null;
        try {
            var paths = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => new ModelFileSmrPathData {
                avatarPath = SmrPathService.GetRelativeTransformPath(model.transform, r.transform),
                fbxMeshPath = SmrPathService.GetRelativeTransformPath(model.transform, r.transform),
                meshName = r.sharedMesh.name, rendererName = r.name }).ToList();
            foreach (string name in new[] { "a", "b", "c" }) {
                string folder = Path.Combine(output, name); Directory.CreateDirectory(folder);
                if (name == "b") {
                    var hair = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name.Contains("Hair"));
                    changedHair = Object.Instantiate(hair.sharedMesh); hair.sharedMesh = changedHair;
                    var deltas = new Vector3[changedHair.vertexCount]; deltas[0] = Vector3.up * .001f;
                    changedHair.AddBlendShapeFrame("ReuseFixtureHair", 100, deltas, null, null);
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                NativeMeshPayloadService.NativeMeshPayloadBuildResult build = null;
                CustomBaseVersion version;
                if (name != "c") {
                    build = NativeMeshPayloadService.WriteEncryptedPayload(source, model, paths, new FileManagerService(),
                        Path.Combine(folder, "mesh.bin"), sourcePoseRoot: baseModel.transform, createDeliveryVariants: true);
                    var primary = build.parts[0].variants[0];
                    var template = new ModelFileData { path = "mesh.bin", role = "PATCH", type = "BIN", sourceModelFileId = 1,
                        transform = NativeMeshPayloadService.TransformName, metadata = new Dictionary<string, object> { { "sourcePath", source } } };
                    version = new CustomBaseVersion { assetId = 999996, version = "reuse-" + name, defaultAviVersion = "1", scope = Scope.PUBLIC,
                        defaultAviHash = new[] { MCBUtils.CalculateFileHash(source) },
                        extraCustomization = new object[] { NativeMeshPayloadService.ExtraCustomizationKey },
                        sourceFiles = new[] { new ModelFileData { id = 1, path = source, type = "FBX", role = "SOURCE", hash = MCBUtils.CalculateFileHash(source), smrPaths = paths } },
                        versionFiles = NativeMeshPayloadService.ExpandRendererParts(new List<ModelFileData> { template },
                            new[] { new FileManagerService.ModelFilePackageEntry { binUnityPath = "mesh.bin", payloadParts = build.parts } }) };
                } else {
                    version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(Path.Combine(output, "a.json")));
                    version.version = "reuse-c";
                    foreach (string file in Directory.GetFiles(Path.Combine(output, "a"), "*.bin")) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                }
                version.customBlendshapes = new[] { new CustomBlendshapeEntry { name = model.GetComponentsInChildren<SkinnedMeshRenderer>(true)[0].sharedMesh.GetBlendShapeName(0), defaultValue = name == "c" ? "37" : "0" } };
                var logic = new GameObject("mcb logic"); new GameObject("Version-" + name).transform.SetParent(logic.transform);
                string prefab = scratch + "/logic.prefab";
                PrefabUtility.SaveAsPrefabAsset(logic, prefab); Object.DestroyImmediate(logic);
                File.Copy(prefab, Path.Combine(folder, "mcb logic.prefab"));
                AssetDatabase.ExportPackage(prefab, Path.GetFullPath(Path.Combine(folder, "mcb logic.unitypackage")), ExportPackageOptions.Default);
                File.WriteAllText(Path.Combine(output, name + ".json"), JsonConvert.SerializeObject(version));
                ZipFile.CreateFromDirectory(folder, Path.Combine(output, name + ".zip"), System.IO.Compression.CompressionLevel.NoCompression, false);
                results.Add(new { stage = "build", version = name, ms = watch.Elapsed.TotalMilliseconds,
                    rendererCount = build?.rendererCount, hashes = version.versionFiles.Select(p => p.metadata["contentHash"]).ToArray() });
            }
            // Creator cache seeding is tested by health checks; the download run starts cold.
            foreach (string name in new[] { "a", "b" }) {
                var version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(Path.Combine(output, name + ".json")));
                foreach (var variant in version.versionFiles.SelectMany(MCBVersionDelivery.GetVariants)) {
                    string path = Path.Combine(MCBUtils.GetMCBDataFolder(), "mesh-blobs-v1", variant.hash + ".bin");
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            Status = "built"; Save();
        } finally { Object.DestroyImmediate(model); if (changedHair != null) Object.DestroyImmediate(changedHair); AssetDatabase.DeleteAsset(scratch); }
    }
    public static void Run(string url)
    {
        Status = "running";
        EditorCoroutineUtility.StartCoroutineOwnerless(Guard(RunRoutine(url)));
    }
    public static void RunExisting(string source, string reference, string folder, string url)
    {
        sourcePath = source; referencePath = reference; rootFolder = folder; results.Clear();
        AssetDatabase.DeleteAsset("Assets/MCB/generated/advancedMeshPayloads/999996");
        foreach (string name in new[] { "a", "b" }) {
            var version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(Path.Combine(folder, name + ".json")));
            foreach (var variant in version.versionFiles.SelectMany(MCBVersionDelivery.GetVariants)) {
                string cache = Path.Combine(MCBUtils.GetMCBDataFolder(), "mesh-blobs-v1", variant.hash + ".bin");
                if (File.Exists(cache)) File.Delete(cache);
            }
        }
        Run(url);
    }
    static IEnumerator Guard(IEnumerator routine)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(routine);
        while (stack.Count > 0) {
            object current = null; bool moved = false;
            try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
            catch (Exception ex) { Status = "FAILED: " + ex; Save(); Cleanup(); yield break; }
            if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
            if (current is IEnumerator nested) stack.Push(nested); else yield return current;
        }
        Status = "passed"; Save(); Cleanup();
    }
    static IEnumerator RunRoutine(string url)
    {
        preview = EditorSceneManager.NewPreviewScene();
        target = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath));
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(target, preview);
        target.name = "MCB Reuse Verification";
        foreach (var renderer in target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m == null ? null : Object.Instantiate(m)).ToArray();
        var component = target.AddComponent<MyCustomBase>();
        component.baseFbxFiles = new List<GameObject> { AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath) };
        component.preserveBlendshapeValuesOnVersionSwitch = false;
        component.preserveBlendshapeValuesOnVersionSwitchInitialized = true;
        editor = (MCBEditor)UnityEditor.Editor.CreateEditor(component);
        editor.isAuthenticated = false; editor.authToken = null;
        var manager = new FileManagerService(); var actions = new VersionActions(editor, new NetworkService(), manager);
        Mesh[] previous = null;
        foreach (string name in new[] { "a", "b", "c" }) {
            Status = "download " + name;
            var version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(Path.Combine(rootFolder, name + ".authorized.json")));
            editor.serverVersions.Add(version); editor.selectedVersionForAction = version;
            string zip = Path.Combine(rootFolder, "download-" + name + ".zip");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var download = MCBMeshDelivery.DownloadAsync(new NetworkService(), url + "/mcb/999996/model?version=" + version.version
                + "&d=" + version.sourceFiles[0].hash + "&t=fixture", version, zip, null, null, recordMeasurements: false);
            while (!download.IsCompleted) yield return null;
            if (!download.Result.success) throw new Exception(download.Result.error);
            results.Add(new { stage = "download", version = name, ms = watch.Elapsed.TotalMilliseconds, metrics = MCBMeshDelivery.LastDownload });
            int expectedDownloads = name == "a" ? 5 : name == "b" ? 1 : 0;
            if (MCBMeshDelivery.LastDownload.downloadedMeshes != expectedDownloads) throw new Exception("Wrong missing-mesh count for " + name);
            string destination = MCBUtils.GetVersionDataPath(version);
            manager.UnzipAndMove(zip, Path.Combine(rootFolder, "extract-" + name), destination);
            MCBVersionDelivery.ApplyLocalDelivery(version); VersionRepository.SaveVersionJson(destination, version);
            AssetDatabase.Refresh();
            while (EditorApplication.isUpdating) yield return null;
            Status = "apply " + name; watch.Restart();
            component.preserveBlendshapeValuesOnVersionSwitch = false;
            component.preserveBlendshapeValuesOnVersionSwitchInitialized = true;
            component.customBlendshapeOverrideNames.Clear(); component.customBlendshapeOverrideValues.Clear();
            editor.serializedObject.Update();
            yield return actions.ApplyOrResetCoroutine(version, false);
            double applyMilliseconds = watch.Elapsed.TotalMilliseconds; watch.Restart();
            var renderers = target.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var meshes = renderers.Select(r => r.sharedMesh).ToArray();
            if (component.appliedCustomBaseVersionString != version.version) throw new Exception("Wrong applied version: " + component.appliedCustomBaseVersionString);
            if (target.transform.Find("mcb logic/Version-" + name) == null) throw new Exception("Non-mesh version logic did not update.");
            if (previous != null && meshes[0] != previous[0]) throw new Exception("Body reference changed during reuse.");
            var reference = AssetDatabase.LoadAssetAtPath<GameObject>(referencePath);
            var expected = Object.Instantiate(reference);
            Mesh expectedHair = null;
            if (name == "b") {
                var hair = expected.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name.Contains("Hair"));
                expectedHair = Object.Instantiate(hair.sharedMesh); hair.sharedMesh = expectedHair;
                var deltas = new Vector3[expectedHair.vertexCount]; deltas[0] = Vector3.up * .001f;
                expectedHair.AddBlendShapeFrame("ReuseFixtureHair", 100, deltas, null, null);
            }
            var comparison = MCBMeshRoundTripVerifier.Compare(expected.transform, target.transform);
            Object.DestroyImmediate(expected); if (expectedHair != null) Object.DestroyImmediate(expectedHair);
            if (comparison.Any(r => !r.Passed))
                throw new Exception("Reference mesh mismatch: " + JsonConvert.SerializeObject(comparison.Where(r => !r.Passed)));
            if (name == "b" && !renderers.Any(r => r.sharedMesh.GetBlendShapeIndex("ReuseFixtureHair") >= 0)) throw new Exception("Changed Hair was not applied.");
            if (name == "c" && Mathf.Abs(renderers[0].GetBlendShapeWeight(0) - 37) > .001f) throw new Exception("New blendshape defaults were skipped for an unchanged mesh.");
            results.Add(new { stage = "apply", version = name, ms = applyMilliseconds, verificationMs = watch.Elapsed.TotalMilliseconds,
                retained = previous == null ? 0 : meshes.Where((m, i) => m == previous[i]).Count(), comparison });
            CapturePreview(Path.Combine(rootFolder, "applied-" + name + ".png"));
            previous = meshes;
            NativeMeshPayloadService.DeleteGeneratedPayloadsForVersion(version);
            if (meshes.Any(m => m == null)) throw new Exception("Version deletion broke a mesh reference.");
        }
        Status = "reset";
        yield return actions.ApplyOrResetCoroutine(null, true);
        var reset = MCBMeshRoundTripVerifier.Compare(AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath).transform, target.transform);
        if (reset.Any(r => !r.Passed)) throw new Exception("Reset did not restore the original base.");
        results.Add(new { stage = "reset", comparison = reset });
    }
    static void Save() => File.WriteAllText(Path.Combine(rootFolder, "result.json"), JsonConvert.SerializeObject(new { Status, results }, Formatting.Indented));
    static void CapturePreview(string path)
    {
        var cameraObject = new GameObject("Fixture Camera"); var lightObject = new GameObject("Fixture Light");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, preview);
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lightObject, preview);
        var camera = cameraObject.AddComponent<Camera>(); camera.scene = preview;
        camera.cameraType = CameraType.Preview; camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(preview);
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.18f, .2f, .24f);
        var body = target.GetComponentsInChildren<SkinnedMeshRenderer>(true)[0];
        var baked = new Mesh(); body.BakeMesh(baked);
        var bounds = new Bounds(body.transform.TransformPoint(baked.bounds.center), Vector3.zero);
        foreach (int x in new[] { -1, 1 }) foreach (int y in new[] { -1, 1 }) foreach (int z in new[] { -1, 1 })
            bounds.Encapsulate(body.transform.TransformPoint(baked.bounds.center + Vector3.Scale(baked.bounds.extents, new Vector3(x, y, z))));
        Object.DestroyImmediate(baked);
        camera.transform.position = bounds.center + new Vector3(-.35f, .12f, 1) * bounds.size.y * 1.65f;
        camera.transform.LookAt(bounds.center); camera.fieldOfView = 35; camera.nearClipPlane = .01f;
        var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.4f;
        light.transform.rotation = Quaternion.Euler(35, -30, 0);
        var rt = new RenderTexture(800, 1000, 24); var image = new Texture2D(800, 1000, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, 800, 1000), 0, 0); image.Apply(); File.WriteAllBytes(path, image.EncodeToPNG()); }
        finally { RenderTexture.active = previous; camera.targetTexture = null; Object.DestroyImmediate(image); rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(lightObject); }
    }
    public static void RenderSavedVersion(string folder, string name)
    {
        var version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(Path.Combine(folder, name + ".authorized.json")));
        preview = EditorSceneManager.NewPreviewScene();
        target = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(version.sourceFiles[0].path));
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(target, preview);
        try {
            foreach (var patch in version.versionFiles)
                NativeMeshPayloadService.ApplyEncryptedPayload(target.transform, version, patch,
                    Path.Combine(MCBUtils.GetVersionDataPath(version), patch.path), version.sourceFiles[0].path, new FileManagerService());
            CapturePreview(Path.Combine(folder, "applied-" + name + ".png"));
        } finally { Cleanup(); }
    }
    static void Cleanup()
    {
        if (editor != null) Object.DestroyImmediate(editor);
        if (target != null) { foreach (var r in target.GetComponentsInChildren<SkinnedMeshRenderer>(true)) foreach (var m in r.sharedMaterials) if (m != null && !AssetDatabase.Contains(m)) Object.DestroyImmediate(m); Object.DestroyImmediate(target); }
        if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
    }
}
#endif
