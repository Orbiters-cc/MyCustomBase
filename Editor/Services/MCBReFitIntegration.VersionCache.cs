#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Refitted meshes are kept per custom base version: switching versions saves the fits of the version being left and puts
/// back those saved for the version being applied, so clothing fits every version it was refitted for.
/// </summary>
public static partial class MCBReFitIntegration
{
    private static CustomBaseVersion GetAppliedRefitVersion(MyCustomBase target)
    {
        if (target.appliedCustomBaseVersion != null) return target.appliedCustomBaseVersion;
        return string.IsNullOrEmpty(target.appliedCustomBaseVersionString) ? null : new CustomBaseVersion {
            assetId = target.appliedCustomBaseAssetId, version = target.appliedCustomBaseVersionString,
            defaultAviVersion = target.appliedCustomBaseDefaultAviVersion, sourceVersionKey = target.appliedCustomBaseSourceVersionKey };
    }

    public static string GetVersionRefitFolder(MyCustomBase target, CustomBaseVersion version)
    {
        if (target == null || version == null || version.assetId <= 0 || string.IsNullOrWhiteSpace(version.version)) return null;
        McbInstanceIdentityService.EnsureIdentity(target);
        return "Assets/MCB/refits/" + target.mcbComponentId + "/" + version.assetId + "/" +
            (Version.TryParse(version.version, out var label) ? "v" + label + "-" : "version-") +
            CacheKey(version.version + "|" + version.defaultAviVersion + (string.IsNullOrEmpty(version.sourceVersionKey) ? "" : "|" + version.sourceVersionKey));
    }

    private static string CacheKey(string value)
    {
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
    }

    // Accessories may share a name with a sibling. A renderer is identified by its name path plus its sibling ordinals
    // (RefitRecords.SiblingOrdinals), both when its fit is saved and when it is put back; unique names keep the plain path.
    internal static string SnapshotKey(string rendererPath, IReadOnlyList<int> ordinals) => CacheKey(RefitRecords.PathKey(rendererPath, ordinals));

    /// <summary>
    /// Saves the applied fits of the avatar (by any tool) for this version, their meshes copied next to them. A fit made for
    /// another custom base (a refit that finished while the version changed) is never saved as this version's; a tool that
    /// does not know the base (ReFit's wizard) leaves the key empty.
    /// </summary>
    public static void SaveVersionFits(MyCustomBase target, CustomBaseVersion version)
    {
        string folder = GetVersionRefitFolder(target, version);
        if (folder == null) return;
        var root = Root(target);
        string baseKey = BaseKey(version);
        foreach (var record in RefitRecords.All(root))
        {
            var renderer = record.GetComponent<SkinnedMeshRenderer>();
            string rendererPath = renderer != null ? RefitRecords.PathUnder(root, renderer.transform) : null;
            if (!record.Applied || rendererPath == null) continue;
            if (!string.IsNullOrEmpty(record.baseKey) && record.baseKey != baseKey)
            {
                MCBLogger.LogWarning("[MCB] ReFit: " + renderer.name + " was refitted for another custom base (" + (record.baseName ?? record.baseKey) +
                                     "), so it is not saved for this version.");
                continue;
            }
            var ordinals = RefitRecords.SiblingOrdinals(root, renderer.transform);
            if (record.original?.mesh == null || !EditorUtility.IsPersistent(record.original.mesh))
                throw new InvalidOperationException("Save the original accessory mesh as an asset before saving its version-specific ReFit.");
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Directory.CreateDirectory(Path.GetFullPath(folder));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            string key = SnapshotKey(rendererPath, ordinals);
            string path = folder + "/" + key + ".asset";
            var snapshot = AssetDatabase.LoadAssetAtPath<MCBRefitVersionSnapshot>(path);
            if (snapshot == null)
            {
                snapshot = ScriptableObject.CreateInstance<MCBRefitVersionSnapshot>();
                AssetDatabase.CreateAsset(snapshot, path);
            }
            string meshPath = AssetDatabase.GetAssetPath(record.mesh);
            if (!meshPath.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                var savedMesh = UnityEngine.Object.Instantiate(record.mesh);
                savedMesh.name = record.mesh.name;
                meshPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + key + "-mesh.asset");
                AssetDatabase.CreateAsset(savedMesh, meshPath);
                Undo.RecordObject(renderer, "Save version ReFit");
                renderer.sharedMesh = savedMesh;
                EditorUtility.SetDirty(renderer);
                Undo.RecordObject(record, "Save version ReFit");
                record.mesh = savedMesh;
                record.meshPath = meshPath;
                EditorUtility.SetDirty(record);
            }
            var fitted = RefitRecords.Persistent(RefitRecords.Capture(root, renderer));
            var original = RefitRecords.Persistent(record.original);
            string metadata = RefitEngine.Current?.SaveMetadata(renderer);
            if (snapshot.enabledForVersion && snapshot.rendererPath == rendererPath &&
                (snapshot.rendererSiblingOrdinals ?? new List<int>()).SequenceEqual(ordinals) &&
                JsonUtility.ToJson(snapshot.fitted) == JsonUtility.ToJson(fitted) && JsonUtility.ToJson(snapshot.original) == JsonUtility.ToJson(original) &&
                (metadata == null || metadata == snapshot.metadataJson)) continue;
            snapshot.enabledForVersion = true;
            snapshot.rendererPath = rendererPath;
            snapshot.rendererSiblingOrdinals = ordinals;
            snapshot.original = original;
            snapshot.fitted = fitted;
            snapshot.shapes = record.shapes.ToList();
            snapshot.kind = record.kind;
            snapshot.tool = record.tool;
            if (metadata != null) snapshot.metadataJson = metadata;
            EditorUtility.SetDirty(snapshot);
            AssetDatabase.SaveAssetIfDirty(snapshot);
            AssetDatabase.SaveAssetIfDirty(record.mesh);
        }
    }

    /// <summary>Puts back the fits saved for this version on meshes still as they were; returns how many.</summary>
    public static int RestoreVersionFits(MyCustomBase target, CustomBaseVersion version)
    {
        string folder = GetVersionRefitFolder(target, version);
        if (folder == null || !AssetDatabase.IsValidFolder(folder)) return 0;
        int restored = 0;
        var root = Root(target);
        var body = FindPrimaryBodyRenderer(root, BuildBaseMeshMap(target));
        foreach (string guid in AssetDatabase.FindAssets("t:MCBRefitVersionSnapshot", new[] { folder }))
        {
            var snapshot = AssetDatabase.LoadAssetAtPath<MCBRefitVersionSnapshot>(AssetDatabase.GUIDToAssetPath(guid));
            if (snapshot == null || !snapshot.enabledForVersion || snapshot.original == null || snapshot.fitted?.mesh == null ||
                string.IsNullOrEmpty(snapshot.rendererPath)) continue;
            var renderer = RefitRecords.FindUnder(root, snapshot.rendererPath, snapshot.rendererSiblingOrdinals)?.GetComponent<SkinnedMeshRenderer>();
            // A replaced or manually edited accessory must never be overwritten by an unrelated saved fit.
            if (renderer == null || (renderer.sharedMesh != snapshot.original.mesh && renderer.sharedMesh != snapshot.fitted.mesh)) continue;
            if (!RefitRecords.CanResolve(root, snapshot.fitted))
            {
                MCBLogger.LogWarning("[MCB] Saved ReFit cannot safely resolve its armature; skipping " + snapshot.rendererPath);
                continue;
            }
            RefitRecords.Restore(root, renderer, snapshot.fitted, "Restore version ReFit");
            if (!string.IsNullOrEmpty(snapshot.metadataJson)) RefitEngine.Current?.LoadMetadata(renderer, snapshot.metadataJson);
            RefitRecords.Register(renderer, RefitRecords.Persistent(snapshot.original), snapshot.fitted.mesh,
                AssetDatabase.GetAssetPath(snapshot.fitted.mesh), body, snapshot.shapes, snapshot.kind, BaseKey(version),
                DisplayName(target, version), string.IsNullOrEmpty(snapshot.tool) ? ToolName : snapshot.tool);
            restored++;
        }
        return restored;
    }

    private static void DisableSavedFit(MyCustomBase target, Transform renderer)
    {
        var root = Root(target);
        string rendererPath = RefitRecords.PathUnder(root, renderer);
        string folder = rendererPath == null ? null : GetVersionRefitFolder(target, GetAppliedRefitVersion(target));
        if (folder == null) return;
        var snapshot = AssetDatabase.LoadAssetAtPath<MCBRefitVersionSnapshot>(
            folder + "/" + SnapshotKey(rendererPath, RefitRecords.SiblingOrdinals(root, renderer)) + ".asset");
        if (snapshot == null) return;
        Undo.RecordObject(snapshot, "Disable version ReFit");
        snapshot.enabledForVersion = false;
        EditorUtility.SetDirty(snapshot);
        AssetDatabase.SaveAssetIfDirty(snapshot);
    }
}
#endif
