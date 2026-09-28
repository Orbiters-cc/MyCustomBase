#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

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
