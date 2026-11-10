#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCBEditorUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Meshes;

// Which version's payload an avatar holds.
public static partial class NativeMeshPayloadService
{
    public static bool IsVersionApplied(Transform avatarRoot, CustomBaseVersion version)
    {
        if (avatarRoot == null || version == null)
        {
            return false;
        }

        var advancedPatches = GetAdvancedPatchFiles(version).ToList();
        if (advancedPatches.Count == 0)
        {
            return false;
        }
        if (advancedPatches.Any(p => GetPayloadIdentity(p)?.StartsWith("raw:", StringComparison.Ordinal) == true))
        {
            var state = avatarRoot.GetComponentsInChildren<MyCustomBase>(true).FirstOrDefault(m =>
                m.appliedCustomBaseAssetId == version.assetId && m.appliedCustomBaseVersionString == version.version);
            if (state == null) return false;
        }

        foreach (var patch in advancedPatches)
        {
            if (string.IsNullOrWhiteSpace(patch.outputHash))
            {
                return false;
            }

            bool matchedPatch = false;
            foreach (var source in GetSourceFilesForPatch(version, patch))
            {
                foreach (var renderer in ResolveRenderersForSource(avatarRoot, source))
                {
                    string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh));
                    string meshName = renderer.sharedMesh != null ? renderer.sharedMesh.name : null;
                    string shortHash = ShortHash(patch.outputHash);
                    if (IsGeneratedPayloadPath(meshPath, version, patch) ||
                        (!string.IsNullOrWhiteSpace(meshName) &&
                         meshName.IndexOf(shortHash, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        matchedPatch = true;
                        break;
                    }
                }

                if (matchedPatch)
                {
                    break;
                }
            }

            if (!matchedPatch)
            {
                return false;
            }
        }

        return true;
    }

    public static bool HasAnyAdvancedMeshApplied(Transform avatarRoot, CustomBaseVersion version)
    {
        if (avatarRoot == null || version == null)
        {
            return false;
        }

        foreach (var patch in GetAdvancedPatchFiles(version))
        {
            if (string.IsNullOrWhiteSpace(patch.outputHash))
            {
                continue;
            }

            foreach (var source in GetSourceFilesForPatch(version, patch))
            {
                foreach (var renderer in ResolveRenderersForSource(avatarRoot, source))
                {
                    string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh));
                    string meshName = renderer.sharedMesh != null ? renderer.sharedMesh.name : null;
                    string shortHash = ShortHash(patch.outputHash);
                    if (IsGeneratedPayloadPath(meshPath, version, patch) ||
                        (!string.IsNullOrWhiteSpace(meshName) &&
                         meshName.IndexOf(shortHash, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static List<SkinnedMeshRenderer> ResolveAppliedGeneratedMeshRenderers(
        Transform avatarRoot,
        CustomBaseVersion version = null)
    {
        var renderers = new List<SkinnedMeshRenderer>();
        if (avatarRoot == null)
        {
            return renderers;
        }

        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.sharedMesh == null)
            {
                continue;
            }

            string meshPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh));
            if (IsSharedMeshForVersion(meshPath, version)) { renderers.Add(renderer); continue; }
            if (!TryParseGeneratedMeshAssetPath(meshPath, out int assetId, out string versionString))
            {
                continue;
            }

            if (version != null &&
                (version.assetId != assetId ||
                 !string.Equals(version.version, versionString, StringComparison.Ordinal)))
            {
                continue;
            }

            renderers.Add(renderer);
        }

        return renderers;
    }

    public static bool TryGetAppliedGeneratedMeshVersion(
        Transform avatarRoot,
        out int assetId,
        out string versionString)
    {
        assetId = 0;
        versionString = null;
        if (avatarRoot == null)
        {
            return false;
        }

        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            string meshPath = renderer != null && renderer.sharedMesh != null
                ? MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh))
                : null;
            if (IsSharedMeshForVersion(meshPath, null)) {
                var state = avatarRoot.GetComponentsInChildren<MyCustomBase>(true).FirstOrDefault(m => m.appliedCustomBaseAssetId > 0 && !string.IsNullOrEmpty(m.appliedCustomBaseVersionString));
                if (state != null) { assetId = state.appliedCustomBaseAssetId; versionString = state.appliedCustomBaseVersionString; return true; }
            }
            if (TryParseGeneratedMeshAssetPath(meshPath, out assetId, out versionString))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParseGeneratedMeshAssetPath(
        string meshAssetPath,
        out int assetId,
        out string versionString)
    {
        assetId = 0;
        versionString = null;
        string normalized = MCBUtils.ToUnityPath(meshAssetPath);
        string prefix = GeneratedFolder + "/";
        if (string.IsNullOrWhiteSpace(normalized) ||
            !normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = normalized.Substring(prefix.Length).Split('/');
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], out assetId) ||
            assetId <= 0 ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            assetId = 0;
            return false;
        }

        versionString = parts[1];
        return true;
    }

    public static List<SkinnedMeshRenderer> ResolveRenderersForSourcePaths(
        Transform avatarRoot,
        CustomBaseVersion version,
        IEnumerable<string> sourceFbxPaths,
        MyCustomBase target = null)
    {
        var renderers = new List<SkinnedMeshRenderer>();
        var seen = new HashSet<int>();
        if (avatarRoot == null)
        {
            return renderers;
        }

        foreach (string rawPath in sourceFbxPaths ?? Enumerable.Empty<string>())
        {
            string sourcePath = MCBUtils.ToUnityPath(rawPath);
            var source = version?.sourceFiles?.FirstOrDefault(file =>
                file != null &&
                SourceFileMatchesPath(file, sourcePath, target));
            foreach (var renderer in ResolveRenderersForSource(avatarRoot, source))
            {
                if (renderer != null && seen.Add(renderer.GetInstanceID()))
                {
                    renderers.Add(renderer);
                }
            }
        }

        return renderers;
    }

    public static bool VersionUsesAdvancedMesh(CustomBaseVersion version)
    {
        if (version == null)
        {
            return false;
        }

        bool hasExtra = ExtraCustomizationUtils.HasFlag(version.extraCustomization, ExtraCustomizationKey);
        return hasExtra || GetAdvancedPatchFiles(version).Any();
    }

    public static bool IsAdvancedMeshPatchTransform(string transform)
    {
        return string.Equals(transform, TransformName, StringComparison.OrdinalIgnoreCase) || IsPlainPayloadTransform(transform);
    }

    public static bool IsPlainPayloadTransform(string transform)
    {
        return string.Equals(transform, PlainTransformName, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
