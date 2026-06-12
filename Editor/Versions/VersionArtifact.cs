#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

/// <summary>
/// Data layer of the version pipeline: the artifact manifest (the identity of a built
/// version), the immutable artifact handle, and artifact validation states.
///
/// A version folder is a valid, publishable artifact when it contains a
/// <c>manifest.json</c> whose <c>outputs</c> (every upload-bound file, including .meta
/// files) all exist on disk with matching hashes. The upload package is re-created at
/// publish time from exactly the manifest-listed outputs, so what is uploaded is
/// content-identical to what was built — never re-derived from live project state.
///
/// <c>inputs</c> record the source files (FBX, prefab, textures...) the build consumed.
/// They are only used to *warn* about source drift at publish time; they never gate or
/// silently trigger a rebuild.
/// </summary>
[Serializable]
public class VersionManifestFile
{
    public string path;   // relative path inside the version folder (forward slashes)
    public string hash;   // SHA-256 hex (same convention as MCBUtils.CalculateFileHash)
    public long bytes;
}

[Serializable]
public class VersionManifestInput
{
    public string path;   // project-relative Unity path of the source file
    public string hash;   // SHA-256 hex
    public string kind;   // sourceFbx | customFbx | customAvatar | logicPrefab | veinsTexture | animationClip
}

[Serializable]
public class VersionManifest
{
    public const string FileName = "manifest.json";
    public const int CurrentSchema = 1;

    public int schema = CurrentSchema;
    public string builderVersion;
    public string createdUtc;
    public int assetId;
    public string version;
    public string defaultAviVersion;
    /// <summary>SHA-256 of the canonical creator-form snapshot that produced this build.
    /// Publish from the creator form is gated on this matching the current form.</summary>
    public string formSignature;
    /// <summary>True while the artifact is built but not yet published. Carried here
    /// because CustomBaseVersion.isUnsubmitted is [JsonIgnore] (wire format).</summary>
    public bool unsubmitted;
    /// <summary>Exact upload zip entry set. version.json and manifest.json are local-only
    /// and never listed here (and therefore never uploaded).</summary>
    public List<VersionManifestFile> outputs = new List<VersionManifestFile>();
    /// <summary>Source files consumed by the build; drift detection only.</summary>
    public List<VersionManifestInput> inputs = new List<VersionManifestInput>();

    public static string GetManifestFullPath(string versionFolderUnityPath)
    {
        return Path.Combine(Path.GetFullPath(versionFolderUnityPath), FileName);
    }

    public static VersionManifest Load(string versionFolderUnityPath)
    {
        try
        {
            string fullPath = GetManifestFullPath(versionFolderUnityPath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<VersionManifest>(File.ReadAllText(fullPath));
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[VersionManifest] Failed to read manifest in '{versionFolderUnityPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes the manifest into the version folder. This must always be the LAST file
    /// written during a build: a folder without a manifest is by definition an
    /// incomplete (or imported/legacy) artifact.
    /// </summary>
    public void Save(string versionFolderUnityPath)
    {
        string fullPath = GetManifestFullPath(versionFolderUnityPath);
        File.WriteAllText(fullPath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    /// <summary>SHA-256 hex of an arbitrary string (used for the form signature).</summary>
    public static string ComputeStringHash(string value)
    {
        using (var sha256 = SHA256.Create())
        {
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var builder = new StringBuilder(hashBytes.Length * 2);
            foreach (byte b in hashBytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}

/// <summary>
/// Publishability state of a version folder.
/// Valid and SourceDrift are publishable (drift requires user confirmation);
/// everything else requires an explicit rebuild.
/// </summary>
public enum ArtifactState
{
    Valid,
    SourceDrift,
    OutputsMissing,
    OutputsModified,
    NoManifest,
    Corrupt
}

public class ArtifactValidationResult
{
    public ArtifactState state;
    public List<string> missingOutputs = new List<string>();
    public List<string> modifiedOutputs = new List<string>();
    public List<string> driftedInputs = new List<string>();

    public bool IsPublishable => state == ArtifactState.Valid || state == ArtifactState.SourceDrift;

    public string Describe()
    {
        switch (state)
        {
            case ArtifactState.Valid:
                return "Artifact is valid.";
            case ArtifactState.SourceDrift:
                return "Source files changed since the build: " + string.Join(", ", driftedInputs.Take(5));
            case ArtifactState.OutputsMissing:
                return "Built files are missing: " + string.Join(", ", missingOutputs.Take(5));
            case ArtifactState.OutputsModified:
                return "Built files were modified after the build: " + string.Join(", ", modifiedOutputs.Take(5));
            case ArtifactState.NoManifest:
                return "No build manifest (imported or pre-refactor version folder).";
            default:
                return "Artifact is corrupt.";
        }
    }
}

/// <summary>
/// Immutable handle to a version folder. All read-only consumers should resolve files
/// through this handle (or the MCBUtils.GetVersion*Path helpers) instead of doing their
/// own path math.
/// </summary>
public class VersionArtifact
{
    public string FolderUnityPath { get; }
    public CustomBaseVersion Metadata { get; }
    /// <summary>Null for imported/downloaded versions (they are not publishable artifacts).</summary>
    public VersionManifest Manifest { get; }

    public VersionArtifact(string folderUnityPath, CustomBaseVersion metadata, VersionManifest manifest)
    {
        FolderUnityPath = folderUnityPath;
        Metadata = metadata;
        Manifest = manifest;
    }

    public bool FolderExists => !string.IsNullOrEmpty(FolderUnityPath) && Directory.Exists(Path.GetFullPath(FolderUnityPath));

    public string ResolveFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        return MCBUtils.CombineUnityPath(FolderUnityPath, relativePath);
    }
}
#endif
