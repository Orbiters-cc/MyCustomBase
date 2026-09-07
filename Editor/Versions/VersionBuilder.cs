#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Orchestrates a version build: staging folder → packaging (FileManagerService) →
/// input/output hashing → manifest (written last) → atomic repository commit.
/// The creator form (CreatorModeModule) collects the build parameters and constructs
/// the CustomBaseVersion metadata (which needs the hashes produced during packaging);
/// this class owns the artifact invariants.
/// </summary>
public static class VersionBuilder
{
    /// <summary>
    /// Builds a version artifact. <paramref name="metadataFactory"/> runs after
    /// packaging (entry hashes are filled in during packaging) and must return the
    /// complete CustomBaseVersion metadata. Never touches the previous artifact on
    /// failure: the staging folder is deleted and the exception rethrown.
    /// </summary>
    public static VersionArtifact Build(
        FileManagerService fileManagerService,
        int assetId,
        string versionString,
        string defaultAviVersion,
        IList<FileManagerService.ModelFilePackageEntry> packageEntries,
        GameObject logicPrefab,
        bool includeCustomVeins,
        Texture2D customVeinsTexture,
        bool includeDynamicNormalsBody,
        bool includeDynamicNormalsFlexing,
        IEnumerable<string> additionalAnimationAssetPaths,
        Func<CustomBaseVersion> metadataFactory,
        string formSignature)
    {
        if (fileManagerService == null) throw new ArgumentNullException(nameof(fileManagerService));
        if (metadataFactory == null) throw new ArgumentNullException(nameof(metadataFactory));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        void Mark(string phase)
        {
            double now = timer.Elapsed.TotalMilliseconds;
            MCBLogger.Log($"[VersionBuildProfile] version={versionString} phase={phase} stepMs={now - last:F1} totalMs={now:F1}");
            last = now;
        }

        var animationPaths = (additionalAnimationAssetPaths ?? Enumerable.Empty<string>()).ToList();

        // Inputs are hashed before packaging so the drift baseline reflects exactly
        // what this build consumed.
        var inputs = CollectInputs(packageEntries, logicPrefab, includeCustomVeins ? customVeinsTexture : null, animationPaths);
        Mark("Hash inputs");

        string staging = VersionRepository.CreateStagingFolder(assetId, versionString, defaultAviVersion);
        try
        {
            fileManagerService.PopulateVersionFolder(
                staging,
                packageEntries,
                logicPrefab,
                includeCustomVeins,
                customVeinsTexture,
                includeDynamicNormalsBody,
                includeDynamicNormalsFlexing,
                animationPaths);
            Mark("Package model and logic");

            var metadata = metadataFactory();
            if (metadata == null)
            {
                throw new InvalidOperationException("Version metadata could not be created.");
            }

            metadata.isUnsubmitted = true;
            Mark("Create metadata");
            var manifest = VersionRepository.CreateManifestFromFolder(staging, metadata, unsubmitted: true, formSignature: formSignature, inputs: inputs);
            Mark("Hash outputs and manifest");
            var result = VersionRepository.CommitStaging(staging, metadata, manifest);
            Mark("Commit local artifact");
            return result;
        }
        catch (Exception)
        {
            VersionRepository.DeleteStaging(staging);
            throw;
        }
        finally
        {
            VersionRepository.ReleaseFolderGuard(assetId, versionString, defaultAviVersion);
        }
    }

    private static List<VersionManifestInput> CollectInputs(
        IEnumerable<FileManagerService.ModelFilePackageEntry> packageEntries,
        GameObject logicPrefab,
        Texture2D veinsTexture,
        IEnumerable<string> animationAssetPaths)
    {
        var inputs = new List<VersionManifestInput>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string kind)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string unityPath = MCBUtils.ToUnityPath(path);
            if (!seen.Add(unityPath)) return;

            string fullPath;
            try { fullPath = Path.GetFullPath(unityPath); }
            catch { return; }
            if (!File.Exists(fullPath)) return;

            inputs.Add(new VersionManifestInput
            {
                path = unityPath,
                hash = MCBUtils.CalculateFileHash(fullPath),
                kind = kind
            });
        }

        foreach (var entry in packageEntries ?? Enumerable.Empty<FileManagerService.ModelFilePackageEntry>())
        {
            if (entry == null) continue;
            Add(entry.sourceFbxPath, "sourceFbx");
            if (entry.customFbx != null) Add(AssetDatabase.GetAssetPath(entry.customFbx), "customFbx");
            Add(entry.externalCustomFbxPath, "customFbx");
            if (entry.customBaseAvatar != null) Add(AssetDatabase.GetAssetPath(entry.customBaseAvatar), "customAvatar");
        }

        if (logicPrefab != null) Add(AssetDatabase.GetAssetPath(logicPrefab), "logicPrefab");
        if (veinsTexture != null) Add(AssetDatabase.GetAssetPath(veinsTexture), "veinsTexture");
        foreach (string animationPath in animationAssetPaths ?? Enumerable.Empty<string>())
        {
            Add(animationPath, "animationClip");
        }

        return inputs;
    }
}
#endif
