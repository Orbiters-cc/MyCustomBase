#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;

public static partial class MCBReFitIntegration
{
    // Temporary imports of original base FBX files, for refits of versions that replaced the FBX.
    private const string OriginalImportFolder = "Assets/MCB/refits/_original";

    private sealed class SourceBodyReference
    {
        public GameObject sourceAvatar;
        public SkinnedMeshRenderer renderer;
        public int score;
    }

    /// <summary>
    /// The original base the body was made from (model A of a refit), with its own armature, bindposes and skin weights:
    /// the base FBX asset itself, or, when the applied version replaced that FBX (the original then only exists as its
    /// original-base backup (*.fbx.originalbase), a temporary import of the backup that Dispose removes.
    /// </summary>
    internal static CustomBaseOriginal ResolveOriginal(MyCustomBase target)
    {
        var baseMeshes = BuildBaseMeshMap(target);
        if (baseMeshes.Count == 0)
            throw new InvalidOperationException("ReFit could not find any mesh in the avatar's base FBX file(s). Make sure the MCB component's 'Base Fbx Files' list points at the avatar's base FBX.");
        var targetBody = FindPrimaryBodyRenderer(Root(target), baseMeshes)
                         ?? throw new InvalidOperationException("ReFit could not match any of the avatar's meshes to the base FBX. The avatar's body mesh may have been renamed.");
        var baseMesh = ResolveBaseMesh(baseMeshes, targetBody.sharedMesh.name, targetBody.transform.name)
                       ?? throw new InvalidOperationException($"ReFit could not resolve the original base mesh for body renderer '{targetBody.name}'.");
        var source = ResolveSourceBodyRenderer(target, baseMesh, targetBody.transform.name);
        if (source == null)
            throw new InvalidOperationException($"ReFit could not find a skinned renderer for original mesh '{baseMesh.name}' inside the configured base FBX asset.");

        string fbxPath = AssetDatabase.GetAssetPath(source.sourceAvatar);
        string fullPath = Path.GetFullPath(fbxPath);
        string backup = FileManagerService.GetOriginalBasePath(fullPath);
        if (!File.Exists(backup) || SameContent(fullPath, backup))
            return new CustomBaseOriginal { Avatar = source.sourceAvatar, Body = source.renderer };
        return ImportOriginal(fbxPath, backup, AnimationUtility.CalculateTransformPath(source.renderer.transform, source.sourceAvatar.transform));
    }

    // Users of each temporary import: every ResolveOriginal shares the folder of its backup, deleted with its last user.
    private static readonly Dictionary<string, int> OriginalImportUsers = new Dictionary<string, int>(StringComparer.Ordinal);

    private static CustomBaseOriginal ImportOriginal(string fbxPath, string backup, string bodyPath)
    {
        string hash = MCBUtils.CalculateFileHash(backup);
        if (string.IsNullOrEmpty(hash)) throw new FileNotFoundException("The original base backup could not be read.", backup);
        string folder = OriginalImportFolder + "/" + hash.Substring(0, 16);
        AcquireOriginalImport(folder);
        try { return LoadOriginal(folder, fbxPath, backup, bodyPath); }
        catch
        {
            ReleaseOriginalImport(folder);
            throw;
        }
    }

    internal static void AcquireOriginalImport(string folder) =>
        OriginalImportUsers[folder] = OriginalImportUsers.TryGetValue(folder, out int users) ? users + 1 : 1;

    /// <summary>One user of the temporary import is done: the folder goes with the last one.</summary>
    internal static void ReleaseOriginalImport(string folder)
    {
        if (!OriginalImportUsers.TryGetValue(folder, out int users)) return;
        if (users > 1) { OriginalImportUsers[folder] = users - 1; return; }
        OriginalImportUsers.Remove(folder);
        AssetDatabase.DeleteAsset(folder);
    }

    private static CustomBaseOriginal LoadOriginal(string folder, string fbxPath, string backup, string bodyPath)
    {
        string copy = folder + "/" + Path.GetFileName(fbxPath);
        Directory.CreateDirectory(Path.GetFullPath(folder));
        if (!File.Exists(Path.GetFullPath(copy)))
        {
            File.Copy(backup, Path.GetFullPath(copy));
            // Same import settings (scale, rig, humanoid mapping) as the base FBX, under a new GUID.
            string meta = File.Exists(fbxPath + ".meta") ? File.ReadAllText(fbxPath + ".meta") : null;
            if (meta != null)
                File.WriteAllText(Path.GetFullPath(copy) + ".meta",
                    Regex.Replace(meta, @"(?m)^guid: [0-9a-f]{32}", "guid: " + GUID.Generate()));
        }
        // Import only this model. Refresh scans the whole project and can import unrelated pending
        // assets (or reload scripts) while the caller is waiting for the original body.
        AssetDatabase.ImportAsset(copy, ImportAssetOptions.ForceSynchronousImport);
        var avatar = AssetDatabase.LoadAssetAtPath<GameObject>(copy);
        var body = avatar != null ? avatar.transform.Find(bodyPath)?.GetComponent<SkinnedMeshRenderer>() : null;
        if (body == null)
            throw new InvalidOperationException("The original base backup of " + Path.GetFileName(fbxPath) + " has no body at " + bodyPath + ".");
        return new CustomBaseOriginal { Avatar = avatar, Body = body, Cleanup = () => ReleaseOriginalImport(folder) };
    }

    private static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && MCBUtils.CalculateFileHash(a) == MCBUtils.CalculateFileHash(b);

    private static SourceBodyReference ResolveSourceBodyRenderer(MyCustomBase target, Mesh baseMesh, string targetRendererName)
    {
        SourceBodyReference best = null;
        if (target == null || baseMesh == null) return null;
        foreach (var fbx in target.baseFbxFiles)
        {
            if (fbx == null) continue;
            foreach (var renderer in fbx.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.sharedMesh == null) continue;
                int score = ScoreSourceBodyRenderer(renderer, baseMesh, targetRendererName);
                if (score <= 0) continue;
                if (best == null || score > best.score ||
                    (score == best.score && renderer.sharedMesh.vertexCount > best.renderer.sharedMesh.vertexCount))
                    best = new SourceBodyReference { sourceAvatar = fbx, renderer = renderer, score = score };
            }
        }
        return best;
    }

    private static int ScoreSourceBodyRenderer(SkinnedMeshRenderer renderer, Mesh baseMesh, string targetRendererName)
    {
        int score = 0;
        if (renderer.sharedMesh == baseMesh) score += 10000;
        if (string.Equals(CleanMeshName(renderer.sharedMesh.name), CleanMeshName(baseMesh.name), StringComparison.OrdinalIgnoreCase)) score += 1000;
        if (!string.IsNullOrWhiteSpace(targetRendererName) &&
            string.Equals(renderer.transform.name, targetRendererName, StringComparison.OrdinalIgnoreCase)) score += 100;
        return score;
    }
}
#endif
