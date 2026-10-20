#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The files imports leave behind: every import keeps its own copies (so Undo can go back to the previous one), and once
/// nothing uses a copy any more it only takes space (the previews alone reach hundreds of MB). Once per editor session, when
/// the editor has settled, the copies nothing uses go: imported exports and their Avatars, advanced-mesh previews (both to
/// the Recycle Bin, through the asset database) and advanced-mesh export copies. Only MCB's own file names in MCB's own
/// folders are touched. A copy stays while a saved scene, prefab or asset uses it, while an open object does (the creator's
/// entries among them, unsaved changes included), and when this session's imports used it or it was written since the
/// editor started (Undo can still go back to it). The advanced-mesh copies go to the Recycle Bin too (Windows' shell).
/// Toolkit's GeneratedAssets does not fit here: these copies are partly outside the asset database, its Undo restore
/// does not follow Animators or MCB's component, and it would copy files of hundreds of MB into Library first.
/// </summary>
internal static partial class BlenderExportImport
{
    private const string CleanedKey = "MCB.BlenderExport.CleanedThisSession";
    private const string RetainedKey = "MCB.BlenderExport.RetainedThisSession";
    // The editor's own start (the first scene opening, packages resolving) goes first.
    private const double CleanupDelaySeconds = 30;

    private static readonly Regex ImportFileName = new Regex(@"^\d{2}_.+(\.fbx|Avatar\.asset)$", RegexOptions.IgnoreCase);
    private static readonly Regex PreviewFileName = new Regex(@"^[0-9a-f]{32}(\.humanoid-[0-9a-f]{16})?\.asset$", RegexOptions.IgnoreCase);
    private static readonly Regex AdvancedCopyName = new Regex(@"^\d{2}_.+\.fbx$", RegexOptions.IgnoreCase);

    /// <summary>An imported export or its generated Avatar, by its file name ("NN_….fbx", "NN_…Avatar.asset").</summary>
    internal static bool IsImportFile(string path) => ImportFileName.IsMatch(Path.GetFileName(path ?? ""));

    /// <summary>A preview payload or its humanoid Avatar, by its file name ("&lt;hash&gt;.asset", "&lt;hash&gt;.humanoid-&lt;key&gt;.asset").</summary>
    internal static bool IsPreviewFile(string path) => PreviewFileName.IsMatch(Path.GetFileName(path ?? ""));

    /// <summary>A copy of an advanced-mesh export, by its file name ("NN_….fbx").</summary>
    internal static bool IsAdvancedExportCopy(string path) => AdvancedCopyName.IsMatch(Path.GetFileName(path ?? ""));

    private static bool IsBlenderGenerated(string unityPath) => unityPath != null &&
        (unityPath.StartsWith(ExportsFolder + "/", StringComparison.Ordinal) || unityPath.StartsWith(PreviewsFolder + "/", StringComparison.Ordinal));

    /// <summary>The saved files whose references keep imports: the project's scenes, prefabs and assets, generated ones aside.</summary>
    internal static IEnumerable<string> ReferenceRoots() => NativeMeshPayloadService.ReferenceRoots().Where(path => !IsBlenderGenerated(path));

    // ---- Kept for Undo ---------------------------------------------------------------------------------------------

    /// <summary>Keeps these files (Unity paths, or full paths of advanced-mesh copies) for the rest of the editor session.</summary>
    internal static void RetainForSession(IEnumerable<string> paths)
    {
        var retained = RetainedThisSession();
        int before = retained.Count;
        foreach (string path in paths ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(path)) retained.Add(NormalizeKept(path));
        if (retained.Count != before) SessionState.SetString(RetainedKey, string.Join("\n", retained));
    }

    internal static HashSet<string> RetainedThisSession() =>
        new HashSet<string>(SessionState.GetString(RetainedKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    // Advanced-mesh copies are named by their full path (as the creator's entries name them), assets by their Unity path.
    private static string NormalizeKept(string path)
    {
        string unityPath = MCBUtils.ToUnityPath(path);
        if (unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !unityPath.Contains("~/")) return unityPath;
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    /// <summary>
    /// The files a custom base uses now: what its fields name (the creator's entries, their external FBX paths among them),
    /// its renderers' meshes and its Animator's Avatar.
    /// </summary>
    internal static HashSet<string> FilesUsedBy(MyCustomBase customBase)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (customBase == null) return used;
        var serialized = new SerializedObject(customBase);
        var property = serialized.GetIterator();
        // Strings are leaves here: their characters are never walked.
        while (property.Next(property.propertyType != SerializedPropertyType.String))
        {
            if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null)
            {
                string path = AssetDatabase.GetAssetPath(property.objectReferenceValue);
                if (!string.IsNullOrEmpty(path)) used.Add(path);
            }
            else if (property.propertyType == SerializedPropertyType.String && property.name == nameof(CreatorModelFileBuildEntry.externalCustomFbxPath) &&
                     !string.IsNullOrWhiteSpace(property.stringValue))
                used.Add(NormalizeKept(property.stringValue));
        }
        var root = customBase.transform.root;
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer.sharedMesh != null && AssetDatabase.Contains(renderer.sharedMesh)) used.Add(AssetDatabase.GetAssetPath(renderer.sharedMesh));
        foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            if (animator.avatar != null && AssetDatabase.Contains(animator.avatar)) used.Add(AssetDatabase.GetAssetPath(animator.avatar));
        return used;
    }

    // What every open custom base uses (unsaved changes included), and what this session's imports used or made.
    private static HashSet<string> FilesInUse()
    {
        var used = RetainedThisSession();
        foreach (var customBase in Resources.FindObjectsOfTypeAll<MyCustomBase>())
            if (customBase != null) used.UnionWith(FilesUsedBy(customBase));
        return used;
    }

    // ---- Once per session ------------------------------------------------------------------------------------------

    [InitializeOnLoadMethod]
    private static void ScheduleSessionCleanup()
    {
        if (Application.isBatchMode || SessionState.GetBool(CleanedKey, false)) return;
        EditorApplication.update -= CleanupWhenSettled;
        EditorApplication.update += CleanupWhenSettled;
    }

    private static void CleanupWhenSettled()
    {
        if (EditorApplication.timeSinceStartup < CleanupDelaySeconds || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode) return;
        EditorApplication.update -= CleanupWhenSettled;
        if (SessionState.GetBool(CleanedKey, false)) return;
        SessionState.SetBool(CleanedKey, true);
        _ = CleanUnusedFilesAsync();
    }

    /// <summary>
    /// Takes the import copies nothing uses any more out of the project, in the background (one cleanup at a time with
    /// MCB's generated mesh cleanups, with Unity's progress). Never throws: what fails is logged and kept.
    /// </summary>
    internal static async Task CleanUnusedFilesAsync()
    {
        try
        {
            // Most projects never imported an export: nothing to look at.
            if (!AssetDatabase.IsValidFolder(ExportsFolder) && !AssetDatabase.IsValidFolder(PreviewsFolder) &&
                !Directory.Exists(UnityPathToAbsolute(AdvancedExportsFolder))) return;
            var roots = ReferenceRoots().ToList();
            foreach (var (folder, include, types) in new[]
                     {
                         (ExportsFolder, (Func<string, bool>)IsImportFile, new[] { "t:Model", "t:Avatar" }),
                         (PreviewsFolder, (Func<string, bool>)IsPreviewFile, new[] { "t:NativeMeshPayloadAsset", "t:Avatar" }),
                     })
            {
                if (!AssetDatabase.IsValidFolder(folder)) continue;
                var moved = await NativeMeshPayloadService.QueueUnusedAssetCleanup(folder, "made from Blender exports",
                    path => include(path) && !MadeThisSession(UnityPathToAbsolute(path)), types, FilesInUse, roots);
                if (moved == null) return; // cancelled or failed (logged): nothing else goes this time
                PruneEmptyAssetFolders(folder);
            }
            var copies = await DiscardUnusedAdvancedExportsAsync(UnityPathToAbsolute(AdvancedExportsFolder),
                roots.Where(IsSceneOrPrefab).Select(UnityPathToAbsolute).ToList(), FilesInUse, permanent: false, keep: MadeThisSession);
            if (copies.files > 0)
                MCBLogger.Log($"[BlenderExport] Removed {copies.files} copies of advanced-mesh Blender exports nothing uses any more " +
                              $"({EditorUtility.FormatBytes(copies.bytes)}).");
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning("[BlenderExport] Unused copies of Blender exports were kept: " + ex.Message);
        }
    }

    // A file written since the editor started may be one an Undo of this session goes back to (made before MCB kept a record
    // of the session's imports, for instance): it waits for a later session.
    private static bool MadeThisSession(string fullPath)
    {
        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists) return false;
            var started = DateTime.UtcNow - TimeSpan.FromSeconds(EditorApplication.timeSinceStartup);
            return file.CreationTimeUtc >= started || file.LastWriteTimeUtc >= started;
        }
        catch (Exception) { return true; }
    }

    // The custom base keeps an advanced-mesh copy by its path, and only scenes and prefabs hold custom bases.
    private static bool IsSceneOrPrefab(string path) =>
        path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

    private static void PruneEmptyAssetFolders(string folder)
    {
        string full = UnityPathToAbsolute(folder);
        if (!Directory.Exists(full)) return;
        foreach (string directory in Directory.GetDirectories(full))
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                AssetDatabase.DeleteAsset(MCBUtils.ToUnityPath(directory));
    }

    // ---- Advanced-mesh copies (outside the asset database) ---------------------------------------------------------

    /// <summary>The copies of advanced-mesh exports under <paramref name="folder"/> (full paths), by MCB's names.</summary>
    internal static List<string> AdvancedExportCopies(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*.fbx", SearchOption.AllDirectories).Where(IsAdvancedExportCopy).Select(Path.GetFullPath).ToList()
        : new List<string>();

    /// <summary>
    /// The <paramref name="copies"/> one of <paramref name="files"/> names: a custom base names its advanced-mesh export
    /// by its full path, which Unity may fold over several lines (at a space), so the names are compared without
    /// whitespace. A name that is not plain ASCII, or a file that cannot be read as text, keeps every copy.
    /// </summary>
    internal static HashSet<string> CopiesNamedIn(IReadOnlyCollection<string> copies, IEnumerable<string> files)
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needles = new List<(string copy, string name)>();
        foreach (string copy in copies)
        {
            string name = Path.GetFileName(copy);
            // Escaped by YAML or Unity in ways a plain search cannot see: kept.
            if (name.Any(c => c > 126 || c == '\'' || c == '"' || c == '\\')) named.Add(copy);
            else needles.Add((copy, WithoutWhitespace(name)));
        }
        foreach (string file in files ?? Enumerable.Empty<string>())
        {
            if (needles.All(n => named.Contains(n.copy))) break;
            string text;
            try
            {
                if (!File.Exists(file)) continue;
                text = File.ReadAllText(file);
            }
            catch (Exception)
            {
                named.UnionWith(copies);
                break;
            }
            if (!text.StartsWith("%YAML", StringComparison.Ordinal))
            {
                // Binary serialization: what it names cannot be read.
                named.UnionWith(copies);
                break;
            }
            string flat = WithoutWhitespace(text);
            foreach (var needle in needles)
                if (!named.Contains(needle.copy) && flat.IndexOf(needle.name, StringComparison.OrdinalIgnoreCase) >= 0) named.Add(needle.copy);
        }
        return named;
    }

    private static string WithoutWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsWhiteSpace(c)) builder.Append(c);
        return builder.ToString();
    }

    /// <summary>
    /// Removes the copies under <paramref name="folder"/> that neither <paramref name="files"/> (read in the background)
    /// nor <paramref name="inUse"/> (asked once the reading is done, on the main thread) name, then their empty folders.
    /// To the Recycle Bin on Windows unless <paramref name="permanent"/> (tests).
    /// </summary>
    internal static async Task<(int files, long bytes)> DiscardUnusedAdvancedExportsAsync(string folder, IReadOnlyCollection<string> files,
        Func<HashSet<string>> inUse, bool permanent, Func<string, bool> keep = null)
    {
        var copies = AdvancedExportCopies(folder).Where(copy => keep == null || !keep(copy)).ToList();
        if (copies.Count == 0) return (0, 0);
        var named = await Task.Run(() => CopiesNamedIn(copies, files));
        return DiscardCopies(folder, copies, named, inUse?.Invoke(), permanent);
    }

    /// <summary><see cref="DiscardUnusedAdvancedExportsAsync"/> at once, on the calling thread (tests).</summary>
    internal static (int files, long bytes) DiscardUnusedAdvancedExports(string folder, IReadOnlyCollection<string> files, HashSet<string> inUse, bool permanent)
    {
        var copies = AdvancedExportCopies(folder);
        return copies.Count == 0 ? (0, 0) : DiscardCopies(folder, copies, CopiesNamedIn(copies, files), inUse, permanent);
    }

    private static (int files, long bytes) DiscardCopies(string folder, List<string> copies, HashSet<string> named, HashSet<string> inUse, bool permanent)
    {
        var used = new HashSet<string>((inUse ?? new HashSet<string>()).Select(NormalizeKept), StringComparer.OrdinalIgnoreCase);
        int count = 0;
        long bytes = 0;
        foreach (string copy in copies)
        {
            if (named.Contains(copy) || used.Contains(copy) || !File.Exists(copy)) continue;
            long length = new FileInfo(copy).Length;
            if (!Discard(copy, permanent)) continue;
            count++;
            bytes += length;
        }
        foreach (string directory in Directory.GetDirectories(folder))
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                try { Directory.Delete(directory); }
                catch (IOException) { }
        return (count, bytes);
    }

    private static bool Discard(string fullPath, bool permanent)
    {
        try
        {
            if (permanent || Application.platform != RuntimePlatform.WindowsEditor) File.Delete(fullPath);
            else if (!RecycleBin.Send(fullPath)) return false;
            return !File.Exists(fullPath);
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[BlenderExport] Could not remove the unused copy '{fullPath}': {ex.Message}");
            return false;
        }
    }

    // Windows' shell moves a file to the Recycle Bin (silently, no dialog).
    private static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileOperation
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation(ref FileOperation operation);

        private const uint Delete = 3;
        private const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400;

        public static bool Send(string fullPath)
        {
            var operation = new FileOperation
            {
                wFunc = Delete,
                pFrom = fullPath + "\0", // a list of paths ends with an empty one
                fFlags = Silent | NoConfirmation | AllowUndo | NoErrorUi,
            };
            int status = SHFileOperation(ref operation);
            if (status == 0 && !operation.fAnyOperationsAborted) return true;
            MCBLogger.LogWarning($"[BlenderExport] Windows did not move '{fullPath}' to the Recycle Bin (status 0x{status:X}): it was kept.");
            return false;
        }
    }
}
#endif
