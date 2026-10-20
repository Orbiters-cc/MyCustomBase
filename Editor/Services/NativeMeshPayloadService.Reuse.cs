#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEditor;

public static partial class NativeMeshPayloadService
{
    internal static string HashBytes(byte[] bytes)
    {
        using (var sha = MCBHashing.CreateSha256()) return BytesToHex(sha.ComputeHash(bytes));
    }

    internal static string GetPayloadIdentity(ModelFileData patch)
    {
        if (patch?.metadata != null && patch.metadata.TryGetValue("contentHash", out var value))
        {
            string hash = value?.ToString();
            if (hash == null || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid native mesh content identity.");
            return "raw:" + hash;
        }
        return patch?.outputHash;
    }

    internal static ModelFileData[] ExpandRendererParts(List<ModelFileData> files,
        IList<FileManagerService.ModelFilePackageEntry> entries)
    {
        var result = new List<ModelFileData>();
        foreach (var file in files)
        {
            var entry = entries.FirstOrDefault(e => Path.GetFileName(e.binUnityPath) == file.path);
            if (entry?.payloadParts == null) { result.Add(file); continue; }
            foreach (var part in entry.payloadParts)
            {
                var patch = JsonConvert.DeserializeObject<ModelFileData>(JsonConvert.SerializeObject(file));
                var primary = part.variants[0];
                patch.path = primary.path; patch.hash = primary.hash; patch.outputHash = primary.outputHash;
                patch.compression = primary.codec;
                patch.metadata["contentHash"] = part.contentHash;
                patch.metadata["deliveryVariants"] = part.variants;
                patch.metadata["advancedRendererCount"] = 1;
                patch.metadata[PayloadCompressionMetadataKey] = primary.codec;
                result.Add(patch);
            }
        }
        return result.ToArray();
    }

    internal static bool IsSharedMeshForVersion(string path, CustomBaseVersion version)
    {
        if (SharedPayloadKey(path) == null) return false;
        return version == null || (version.versionFiles ?? Array.Empty<ModelFileData>()).Any(p =>
            IsAdvancedMeshPatchTransform(p?.transform) && IsGeneratedPayloadPath(path, version, p));
    }

    // Shared payloads are cached per Unity version; a mesh applied under another editor version is still the same payload.
    internal static bool IsGeneratedPayloadPath(string meshPath, CustomBaseVersion version, ModelFileData patch)
    {
        if (string.IsNullOrEmpty(meshPath)) return false;
        string expected = GetGeneratedPayloadPath(version, patch, GetPayloadIdentity(patch));
        if (meshPath == expected) return true;
        string key = SharedPayloadKey(meshPath);
        return key != null && key == SharedPayloadKey(expected);
    }

    // "<generated>/<assetId>/shared-<unity version>/<content hash>.asset" without its Unity version, else null.
    static string SharedPayloadKey(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(GeneratedFolder + "/", StringComparison.Ordinal)) return null;
        string[] parts = path.Substring(GeneratedFolder.Length + 1).Split('/');
        if (parts.Length != 3 || !parts[1].StartsWith("shared-", StringComparison.Ordinal)) return null;
        return parts[0] + "/" + parts[2];
    }

    internal static CustomBaseVersion ResolveAppliedMeshVersionFromPaths(IEnumerable<string> meshPaths,
        IEnumerable<CustomBaseVersion> candidates, int assetId, string version, string defaultVersion)
    {
        var paths = meshPaths.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return null;
        var matches = (candidates ?? Enumerable.Empty<CustomBaseVersion>())
            .Where(v => v != null && !string.IsNullOrWhiteSpace(v.version))
            .GroupBy(v => new { v.assetId, v.version, v.defaultAviVersion, v.sourceVersionKey })
            .Select(g => g.First())
            .Where(v => paths.All(path => IsSharedMeshForVersion(path, v) ||
                (TryParseGeneratedMeshAssetPath(path, out int owner, out string revision) &&
                 owner == v.assetId && revision == v.version)))
            .ToArray();

        // Identical meshes can belong to different releases with different options.
        // Preserve the explicit installed identity; never choose the newest by mesh alone.
        var persisted = matches.FirstOrDefault(v => v.assetId == assetId && v.version == version &&
            (string.IsNullOrEmpty(defaultVersion) || v.defaultAviVersion == defaultVersion));
        return persisted ?? (matches.Length == 1 ? matches[0] : null);
    }

    // A cleanup: the generated payloads and humanoid Avatars under Folder (those Include accepts) that nothing uses any more
    // go to the Recycle Bin. Saved files, open objects (unsaved avatars included) and the version switches Undo can still go
    // back to all keep theirs.
    private sealed class Cleanup
    {
        public string Folder, Label;
        public Func<string, bool> Include;
        // Whose emptied folders go too.
        public CustomBaseVersion Version;
        // The saved files whose references count (tests); the project's scenes, prefabs and assets by default.
        public IEnumerable<string> Roots;
        // Tests: deleted for good, nothing of theirs left in the Recycle Bin.
        public bool Permanent;
        // The asset types searched for in Folder (FindAssets filters): generated payloads and humanoid Avatars by default.
        public string[] Types;
        // What more open objects use (beyond renderers, Animators, open animations and version switch Undo): kept.
        public Func<IEnumerable<string>> InUse;
    }

    private static readonly string[] PayloadTypes = { "t:NativeMeshPayloadAsset", "t:Avatar" };

    private static Cleanup CleanupOf(CustomBaseVersion version)
    {
        string versionFolder = GetGeneratedPayloadVersionFolder(version);
        if (versionFolder == null) return null;
        return new Cleanup
        {
            Folder = MCBUtils.CombineUnityPath(GeneratedFolder, version.assetId.ToString()),
            Label = "of version " + version.version,
            Version = version,
            Include = path =>
            {
                // A humanoid Avatar belongs to the payload it was generated from ("<payload>.humanoid-<key>.asset").
                int humanoid = path.IndexOf(".humanoid-", StringComparison.Ordinal);
                string payloadPath = humanoid >= 0 ? path.Substring(0, humanoid) + ".asset" : path;
                return payloadPath.StartsWith(versionFolder + "/", StringComparison.Ordinal) || IsSharedMeshForVersion(payloadPath, version);
            }
        };
    }

    private static List<string> CleanupCandidates(Cleanup cleanup)
    {
        if (!AssetDatabase.IsValidFolder(cleanup.Folder)) return new List<string>();
        var loaded = CollectLoadedGeneratedAssets();
        if (cleanup.InUse != null) loaded.UnionWith(cleanup.InUse().Where(path => !string.IsNullOrEmpty(path)));
        return (cleanup.Types ?? PayloadTypes).SelectMany(type => AssetDatabase.FindAssets(type, new[] { cleanup.Folder }))
            .Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.Ordinal)
            .Where(path => !loaded.Contains(path) && (cleanup.Include == null || cleanup.Include(path))).ToList();
    }

    // Generated assets reference only their own meshes: never read (they reach 100 MB).
    private static Func<string, bool> SkipGenerated(Cleanup cleanup) => path =>
        IsGeneratedAsset(path) || path.StartsWith(cleanup.Folder + "/", StringComparison.Ordinal);

    internal static bool IsGeneratedAsset(string path) =>
        path != null && path.StartsWith(GeneratedFolder + "/", StringComparison.Ordinal);

    // The saved files whose references keep generated assets: the project's scenes, prefabs and assets, generated ones aside
    // (the previews of Blender exports are payloads too: self-contained, and up to hundreds of MB to read).
    internal static IEnumerable<string> ReferenceRoots() => AssetDatabase.GetAllAssetPaths().Where(p =>
        p.StartsWith("Assets/", StringComparison.Ordinal) && !IsGeneratedAsset(p)
        && !p.StartsWith(BlenderExportImport.PreviewsFolder + "/", StringComparison.Ordinal)
        && (p.EndsWith(".unity", StringComparison.Ordinal) || p.EndsWith(".prefab", StringComparison.Ordinal) || p.EndsWith(".asset", StringComparison.Ordinal)));

    // Generated assets open objects (unsaved avatars included) or a version switch Undo/Redo still use.
    static HashSet<string> CollectLoadedGeneratedAssets()
    {
        var loaded = new HashSet<string>(VersionSwitchFileUndo.RetainedAssets(), StringComparer.Ordinal);
        foreach (var renderer in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
            if (renderer.sharedMesh != null) loaded.Add(AssetDatabase.GetAssetPath(renderer.sharedMesh));
        foreach (var animator in Resources.FindObjectsOfTypeAll<Animator>())
            if (animator.avatar != null) loaded.Add(AssetDatabase.GetAssetPath(animator.avatar));
        // Animations open in memory that swap a generated mesh in: their unsaved edits aren't in the files the search reads.
        foreach (var clip in Resources.FindObjectsOfTypeAll<AnimationClip>())
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                    if (key.value is Mesh mesh) loaded.Add(AssetDatabase.GetAssetPath(mesh));
        return loaded;
    }

    // To the Recycle Bin, never deleted for good: a file restored from it (or by Undo) is used again.
    private static GeneratedPayloadStorageInfo Discard(Cleanup cleanup, IEnumerable<string> candidates, HashSet<string> retained)
    {
        long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        var going = candidates.Where(path => !retained.Contains(path) && File.Exists(path))
            .ToDictionary(path => path, path => (bytes: Size(path) + Size(path + ".meta"), files: File.Exists(path + ".meta") ? 2 : 1), StringComparer.Ordinal);
        var failed = new List<string>();
        if (going.Count > 0 && !(cleanup.Permanent ? AssetDatabase.DeleteAssets(going.Keys.ToArray(), failed) : AssetDatabase.MoveAssetsToTrash(going.Keys.ToArray(), failed)))
            MCBLogger.LogWarning("[NativeMeshPayload] Could not move these unused generated meshes to the Recycle Bin: " + string.Join(", ", failed));
        var moved = going.Where(pair => !failed.Contains(pair.Key)).Select(pair => pair.Value).ToList();
        if (cleanup.Version != null)
        {
            string versionFolder = GetGeneratedPayloadVersionFolder(cleanup.Version);
            string fullPath = versionFolder == null ? null : GetGeneratedPayloadFullPath(versionFolder);
            if (fullPath != null && Directory.Exists(fullPath) && !Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories).Any())
                DeleteGeneratedPayloadFolder(versionFolder);
            PruneEmptyGeneratedPayloadAssetFolder(cleanup.Version);
        }
        return new GeneratedPayloadStorageInfo(moved.Sum(item => item.bytes), moved.Count, moved.Sum(item => item.files));
    }

    // At once, on the main thread: health checks and tests (a project whose references are cached already is quick).
    private static GeneratedPayloadStorageInfo DeleteUnreferenced(Cleanup cleanup)
    {
        var candidates = CleanupCandidates(cleanup);
        // Searching the project for references is slow: only when something may go.
        if (candidates.Count == 0) return Discard(cleanup, candidates, null);
        // Everything the project's saved scenes, prefabs and assets use, directly or through other files (a prefab's
        // controller and the animations it plays): what Unity's recursive dependency search finds, read through Orbiters
        // Toolkit's cache of each file's references, so only the files changed since the last search are read.
        var retained = ProjectReferences.Reachable(cleanup.Roots ?? ReferenceRoots(), SkipGenerated(cleanup));
        return Discard(cleanup, candidates, retained);
    }

    private static async Task<GeneratedPayloadStorageInfo> DeleteUnreferencedAsync(Cleanup cleanup, Action<float, string> progress, CancellationToken cancellation)
    {
        var candidates = CleanupCandidates(cleanup);
        if (candidates.Count == 0) return Discard(cleanup, candidates, null);
        var retained = await ProjectReferences.ReachableAsync(cleanup.Roots ?? ReferenceRoots(), SkipGenerated(cleanup), progress, cancellation);
        // The search spanned editor frames: a version switch running now ends first, then what is open decides. Assets made
        // during the search wait for the next cleanup.
        while (VersionOperationGuard.VersionWorkRunning)
        {
            cancellation.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        var stillUnused = new HashSet<string>(CleanupCandidates(cleanup), StringComparer.Ordinal);
        return Discard(cleanup, candidates.Where(stillUnused.Contains), retained);
    }

    private static Task<GeneratedPayloadStorageInfo> cleanupQueue = Task.FromResult<GeneratedPayloadStorageInfo>(null);

    // One cleanup at a time, in the background: the project's references are read a slice per editor tick, with Unity's
    // background progress (cancellable), so nothing waits for it. Null when it failed or was cancelled.
    private static Task<GeneratedPayloadStorageInfo> Queue(Cleanup cleanup)
    {
        var previous = cleanupQueue;
        var next = Run();
        cleanupQueue = next;
        return next;

        async Task<GeneratedPayloadStorageInfo> Run()
        {
            await previous;
            int id = Progress.Start("MCB: unused generated meshes", "Meshes " + cleanup.Label);
            var cancel = new CancellationTokenSource();
            Progress.RegisterCancelCallback(id, () => { cancel.Cancel(); return true; });
            try
            {
                var deleted = await DeleteUnreferencedAsync(cleanup, (value, text) => Progress.Report(id, value, text), cancel.Token);
                if (deleted.HasContent)
                    MCBLogger.Log($"[NativeMeshPayload] Moved {deleted.AssetCount} generated advanced mesh assets {cleanup.Label} nothing uses any more to the Recycle Bin ({deleted.FormattedSize}).");
                Progress.Finish(id);
                return deleted;
            }
            catch (OperationCanceledException)
            {
                Progress.Finish(id, Progress.Status.Canceled);
                return null;
            }
            catch (Exception ex)
            {
                // A background step: reported, never thrown at whoever queued it.
                MCBLogger.LogWarning($"[NativeMeshPayload] Unused generated meshes {cleanup.Label} were kept: {ex.Message}");
                Progress.Finish(id, Progress.Status.Failed);
                return null;
            }
            finally
            {
                cancel.Dispose();
            }
        }
    }

    /// <summary>
    /// Moves the generated assets of <paramref name="version"/> nothing uses any more, its shared payloads included, to the
    /// Recycle Bin in the background. The result is null when it failed or was cancelled.
    /// </summary>
    public static Task<GeneratedPayloadStorageInfo> QueueUnusedPayloadCleanup(CustomBaseVersion version)
    {
        var cleanup = CleanupOf(version);
        return cleanup == null ? Task.FromResult(new GeneratedPayloadStorageInfo(0, 0, 0)) : Queue(cleanup);
    }

    /// <summary>Moves every generated asset nothing uses any more to the Recycle Bin in the background (null when it failed).</summary>
    public static Task<GeneratedPayloadStorageInfo> QueueUnusedPayloadCleanup() =>
        Queue(new Cleanup { Folder = GeneratedFolder, Label = "of every version" });

    /// <summary>
    /// Moves the assets under <paramref name="folder"/> that <paramref name="include"/> accepts (and <paramref name="types"/>
    /// finds) and nothing uses any more to the Recycle Bin, in the background, queued with the other cleanups: what the
    /// saved <paramref name="referenceRoots"/> use, open objects use (<paramref name="inUse"/> adds to them) and version
    /// switch Undo can bring back stays. The result is null when it failed or was cancelled.
    /// </summary>
    internal static Task<GeneratedPayloadStorageInfo> QueueUnusedAssetCleanup(string folder, string label, Func<string, bool> include,
        string[] types, Func<IEnumerable<string>> inUse, IEnumerable<string> referenceRoots) =>
        Queue(new Cleanup { Folder = folder, Label = label, Include = include, Types = types, InUse = inUse, Roots = referenceRoots });

    /// <summary>Tests: <see cref="QueueUnusedAssetCleanup"/> at once, on the main thread, deleted for good.</summary>
    internal static GeneratedPayloadStorageInfo DeleteUnusedAssets(string folder, Func<string, bool> include, string[] types,
        Func<IEnumerable<string>> inUse, IEnumerable<string> referenceRoots) =>
        DeleteUnreferenced(new Cleanup { Folder = folder, Label = "in " + folder, Include = include, Types = types, InUse = inUse, Roots = referenceRoots, Permanent = true });

    /// <summary>The version's generated assets nothing uses any more, at once (<see cref="QueueUnusedPayloadCleanup(CustomBaseVersion)"/> otherwise).</summary>
    internal static GeneratedPayloadStorageInfo DeleteUnreferencedPayloadsOfVersion(CustomBaseVersion version, IEnumerable<string> referenceRoots = null)
    {
        var cleanup = CleanupOf(version);
        if (cleanup == null) return new GeneratedPayloadStorageInfo(0, 0, 0);
        cleanup.Roots = referenceRoots;
        return DeleteUnreferenced(cleanup);
    }

    /// <summary>Tests: the payloads and Avatars under <paramref name="folder"/> that <paramref name="referenceRoots"/> do not use.</summary>
    internal static GeneratedPayloadStorageInfo DeleteUnreferencedGeneratedAssets(string folder, IEnumerable<string> referenceRoots) =>
        DeleteUnreferenced(new Cleanup { Folder = folder, Label = "in " + folder, Roots = referenceRoots, Permanent = true });

    /// <summary>Tests: <see cref="DeleteUnreferencedGeneratedAssets"/> through the background search.</summary>
    internal static Task<GeneratedPayloadStorageInfo> DeleteUnreferencedGeneratedAssetsAsync(string folder, IEnumerable<string> referenceRoots) =>
        DeleteUnreferencedAsync(new Cleanup { Folder = folder, Label = "in " + folder, Roots = referenceRoots, Permanent = true }, null, CancellationToken.None);
}
#endif
