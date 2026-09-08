#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

public static partial class MCBReFitIntegration
{
    private static CustomBaseVersion GetAppliedRefitVersion(MyCustomBase target)
    {
        if (target.appliedCustomBaseVersion != null) return target.appliedCustomBaseVersion;
        return string.IsNullOrEmpty(target.appliedCustomBaseVersionString) ? null : new CustomBaseVersion {
            assetId = target.appliedCustomBaseAssetId, version = target.appliedCustomBaseVersionString,
            defaultAviVersion = target.appliedCustomBaseDefaultAviVersion };
    }

    public static string GetVersionRefitFolder(MyCustomBase target, CustomBaseVersion version)
    {
        if (target == null || version == null || version.assetId <= 0 || string.IsNullOrWhiteSpace(version.version)) return null;
        McbInstanceIdentityService.EnsureIdentity(target);
        return "Assets/MCB/refits/" + target.mcbComponentId + "/" + version.assetId + "/" +
            (Version.TryParse(version.version, out var label) ? "v" + label + "-" : "version-") +
            CacheKey(version.version + "|" + version.defaultAviVersion);
    }

    private static string CacheKey(string value)
    {
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
    }

    // Unity object references to meshes are serialized as assets; scene transforms are stored only by path.
    private static RefitAppliedMeshEntry PersistentState(RefitAppliedMeshEntry state)
    {
        var copy = JsonUtility.FromJson<RefitAppliedMeshEntry>(JsonUtility.ToJson(state));
        copy.originalBones.Clear();
        copy.originalRootBone = null;
        foreach (var transform in copy.originalTransformStates) transform.transform = null;
        return copy;
    }

    public static void SaveVersionFits(MyCustomBase target, CustomBaseVersion version)
    {
        if (target?.appliedRefits == null || target.appliedRefits.Count == 0) return;
        string folder = GetVersionRefitFolder(target, version);
        if (folder == null) return;
        var root = target.transform.root;
        Undo.RecordObject(target, "Save version ReFit");
        foreach (var entry in target.appliedRefits)
        {
            var renderer = root.Find(entry.rendererPath)?.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null || entry.refitMesh == null || renderer.sharedMesh != entry.refitMesh) continue;
            if (entry.originalMesh == null || !EditorUtility.IsPersistent(entry.originalMesh))
                throw new InvalidOperationException("Save the original accessory mesh as an asset before saving its version-specific ReFit.");
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Directory.CreateDirectory(Path.GetFullPath(folder));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            string key = CacheKey(entry.rendererPath);
            string path = folder + "/" + key + ".asset";
            var snapshot = AssetDatabase.LoadAssetAtPath<MCBRefitVersionSnapshot>(path);
            if (snapshot == null)
            {
                snapshot = ScriptableObject.CreateInstance<MCBRefitVersionSnapshot>();
                AssetDatabase.CreateAsset(snapshot, path);
            }
            string meshPath = AssetDatabase.GetAssetPath(entry.refitMesh);
            if (!meshPath.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                var savedMesh = UnityEngine.Object.Instantiate(entry.refitMesh);
                savedMesh.name = entry.refitMesh.name;
                meshPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + key + "-mesh.asset");
                AssetDatabase.CreateAsset(savedMesh, meshPath);
                Undo.RecordObject(renderer, "Save version ReFit");
                renderer.sharedMesh = savedMesh;
                entry.refitMesh = savedMesh;
                entry.refitMeshAssetPath = meshPath;
                EditorUtility.SetDirty(renderer);
            }
            var fittedState = PersistentState(CaptureRendererState(root, entry.rendererPath, renderer));
            var originalState = PersistentState(entry);
            if (snapshot.enabledForVersion && JsonUtility.ToJson(snapshot.fitted) == JsonUtility.ToJson(fittedState) &&
                JsonUtility.ToJson(snapshot.original) == JsonUtility.ToJson(originalState)) continue;
            if (snapshot.fitted == null || snapshot.fitted.originalMesh != fittedState.originalMesh)
            {
                var metadataType = ReFitApi.FindOptionalType("Orbiters.ReFit.ReFitGeneratedAssetMetadata");
                var metadata = metadataType != null ? renderer.GetComponent(metadataType) : null;
                var data = metadata != null ? ReFitApi.GetMemberValue(metadata, "data") : null;
                snapshot.metadataJson = data != null ? JsonUtility.ToJson(data) : null;
            }
            snapshot.enabledForVersion = true;
            snapshot.original = originalState;
            snapshot.fitted = fittedState;
            EditorUtility.SetDirty(snapshot);
            AssetDatabase.SaveAssetIfDirty(snapshot);
            AssetDatabase.SaveAssetIfDirty(entry.refitMesh);
        }
        EditorUtility.SetDirty(target);
    }

    public static int RestoreVersionFits(MyCustomBase target, CustomBaseVersion version)
    {
        string folder = GetVersionRefitFolder(target, version);
        if (folder == null || !AssetDatabase.IsValidFolder(folder)) return 0;
        int restored = 0;
        var root = target.transform.root;
        foreach (string guid in AssetDatabase.FindAssets("t:MCBRefitVersionSnapshot", new[] { folder }))
        {
            var snapshot = AssetDatabase.LoadAssetAtPath<MCBRefitVersionSnapshot>(AssetDatabase.GUIDToAssetPath(guid));
            if (!snapshot.enabledForVersion || snapshot.original == null || snapshot.fitted?.originalMesh == null) continue;
            var renderer = root.Find(snapshot.original.rendererPath)?.GetComponent<SkinnedMeshRenderer>();
            // A replaced or manually edited accessory must never be overwritten by an unrelated saved fit.
            if (renderer == null || (renderer.sharedMesh != snapshot.original.originalMesh &&
                renderer.sharedMesh != snapshot.fitted.originalMesh)) continue;
            var states = snapshot.fitted.originalTransformStates;
            bool canRestore = snapshot.fitted.originalBonePaths.All(p => p != null);
            foreach (string storedPath in snapshot.fitted.originalBonePaths.Concat(new[] { snapshot.fitted.originalRootBonePath }))
            {
                string missing = storedPath;
                while (!string.IsNullOrEmpty(missing) && root.Find(missing) == null)
                {
                    if (!states.Any(s => s.path == missing)) { canRestore = false; break; }
                    int separator = missing.LastIndexOf('/');
                    missing = separator < 0 ? "" : missing.Substring(0, separator);
                }
            }
            if (!canRestore)
            {
                MCBLogger.LogWarning("[MCB] Saved ReFit cannot safely resolve its armature; skipping " + snapshot.original.rendererPath);
                continue;
            }
            RestoreRendererState(root, renderer, snapshot.fitted, "Restore version ReFit");
            var entry = PersistentState(snapshot.original);
            entry.refitMesh = snapshot.fitted.originalMesh;
            entry.refitMeshAssetPath = AssetDatabase.GetAssetPath(entry.refitMesh);
            Undo.RecordObject(target, "Restore version ReFit");
            target.appliedRefits.RemoveAll(e => e != null && e.rendererPath == entry.rendererPath);
            target.appliedRefits.Add(entry);
            var metadataType = ReFitApi.FindOptionalType("Orbiters.ReFit.ReFitGeneratedAssetMetadata");
            if (metadataType != null && !string.IsNullOrEmpty(snapshot.metadataJson))
            {
                var metadata = renderer.GetComponent(metadataType) ?? Undo.AddComponent(renderer.gameObject, metadataType);
                var dataField = metadataType.GetField("data");
                if (dataField != null)
                    dataField.SetValue(metadata, JsonUtility.FromJson(snapshot.metadataJson, dataField.FieldType));
                EditorUtility.SetDirty(metadata);
            }
            SyncTransferredBlendShapeWeightsFromAvatar(entry, target, renderer);
            RefitStateChanged?.Invoke(target, entry.rendererPath);
            restored++;
        }
        if (restored > 0) EditorUtility.SetDirty(target);
        return restored;
    }

    private static void DisableSavedFit(MyCustomBase target, string rendererPath)
    {
        string folder = GetVersionRefitFolder(target, GetAppliedRefitVersion(target));
        if (folder == null) return;
        var snapshot = AssetDatabase.LoadAssetAtPath<MCBRefitVersionSnapshot>(folder + "/" + CacheKey(rendererPath) + ".asset");
        if (snapshot == null) return;
        Undo.RecordObject(snapshot, "Disable version ReFit");
        snapshot.enabledForVersion = false;
        EditorUtility.SetDirty(snapshot);
        AssetDatabase.SaveAssetIfDirty(snapshot);
    }
}
#endif
