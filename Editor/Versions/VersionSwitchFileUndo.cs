#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The files a version switch changes (the base FBX with its import settings, dynamic normals meshes) and the veins
/// setting, before and after the switch. Unity's Undo only restores scene objects: its <see cref="applied"/> flag is
/// recorded in the switch's Undo step, and Undo/Redo puts the matching files back so the avatar, its meshes and MCB's
/// detected version agree again. Copies live in Library/MCB/VersionUndo for the last few switches of this session; an
/// older switch keeps its record without copies, so undoing it says its files stay as they are.
/// </summary>
internal sealed class VersionSwitchFileUndo : ScriptableObject
{
    private const int MaxRecords = 5;
    private const string SessionKey = "MCB.VersionUndo.OnDisk.";
    private const string DroppedKey = "MCB.VersionUndo.Dropped.";
    internal static string Folder => Path.GetFullPath("Library/MCB/VersionUndo");

    [Serializable]
    internal sealed class FileState
    {
        public bool exists, metaExists;
        public string copy, metaCopy;
    }

    [Serializable]
    internal sealed class Entry
    {
        public string unityPath;
        public FileState before, after;
    }

    [Serializable]
    internal sealed class Setting
    {
        public bool hasKey, value;
    }

    [SerializeField] internal string id;
    [SerializeField] internal long sequence;
    // Recorded by Undo: false once the switch is undone, true again on redo.
    [SerializeField] internal bool applied;
    [SerializeField] internal List<Entry> entries = new List<Entry>();
    [SerializeField] internal Setting veinsBefore, veinsAfter;
    // Generated meshes and Avatars the avatar used before and after the switch: clearing the cache keeps them for Undo.
    [SerializeField] internal List<string> assetsBefore = new List<string>(), assetsAfter = new List<string>();

    private bool OnDisk
    {
        get => SessionState.GetBool(SessionKey + id, true);
        set => SessionState.SetBool(SessionKey + id, value);
    }

    // Not recorded by Undo: undoing the switch must still see that its copies are gone.
    internal bool FilesDropped
    {
        get => SessionState.GetBool(DroppedKey + id, false);
        private set => SessionState.SetBool(DroppedKey + id, value);
    }

    /// <summary>The files to keep for a switch touching these FBXs: each FBX and the dynamic normals meshes next to it.</summary>
    internal static List<string> AffectedPaths(IEnumerable<string> fbxPaths)
    {
        var result = new List<string>();
        foreach (string raw in fbxPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path = MCBUtils.ToUnityPath(raw);
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.GetFiles(directory, "*_DynamicNormals.asset"))
            {
                string unityPath = MCBUtils.ToUnityPath(file);
                if (!result.Contains(unityPath, StringComparer.OrdinalIgnoreCase)) result.Add(unityPath);
            }
            // The import settings saved for reset are created and removed by switches too.
            string importBackup = AvatarDefinitionGenerationService.OriginalImportSettingsPath(path);
            if (!result.Contains(importBackup, StringComparer.OrdinalIgnoreCase)) result.Add(importBackup);
        }
        return result;
    }

    /// <summary>The generated meshes and Avatar the avatar under <paramref name="root"/> uses now.</summary>
    internal static IEnumerable<string> UsedGeneratedAssets(Transform root)
    {
        if (root == null) return Enumerable.Empty<string>();
        var animator = root.GetComponent<Animator>();
        return root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(renderer => renderer.sharedMesh != null).Select(renderer => AssetDatabase.GetAssetPath(renderer.sharedMesh))
            .Append(animator != null && animator.avatar != null ? AssetDatabase.GetAssetPath(animator.avatar) : null)
            .Where(path => !string.IsNullOrEmpty(path) && path.StartsWith(MCBUtils.ASSETS_BASE_FOLDER + "/generated/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Generated assets a version switch Undo/Redo can still bring back.</summary>
    internal static IEnumerable<string> RetainedAssets() => All().SelectMany(r => r.assetsBefore.Concat(r.assetsAfter)).ToList();

    /// <summary>Copies the current state of <paramref name="unityPath"/> into <paramref name="folder"/>.</summary>
    internal static FileState Save(string unityPath, string folder, string label, FileState same = null)
    {
        string full = Path.GetFullPath(unityPath);
        var state = new FileState { exists = File.Exists(full), metaExists = File.Exists(full + ".meta") };
        Directory.CreateDirectory(folder);
        string name = label + "-" + Guid.NewGuid().ToString("N");
        if (state.exists)
        {
            // An unchanged file shares the earlier copy: an FBX is often left as it was.
            if (same != null && same.exists && SameContent(full, same.copy)) state.copy = same.copy;
            else File.Copy(full, state.copy = Path.Combine(folder, name), false);
        }
        if (state.metaExists) File.Copy(full + ".meta", state.metaCopy = Path.Combine(folder, name + ".meta"), false);
        return state;
    }

    internal static Setting SaveVeins() => new Setting
    {
        hasKey = EditorPrefs.HasKey(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY),
        value = EditorPrefs.GetBool(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY, true)
    };

    /// <summary>Puts <paramref name="unityPath"/> back to <paramref name="state"/> and reimports it.</summary>
    internal static void Restore(string unityPath, FileState state)
    {
        string full = Path.GetFullPath(unityPath);
        // Unity ignores names ending in '~': a plain file, not an asset.
        if (unityPath.EndsWith("~", StringComparison.Ordinal))
        {
            if (!state.exists) File.Delete(full);
            else File.Copy(state.copy, full, true);
            return;
        }
        if (!state.exists)
        {
            if (File.Exists(full)) AssetDatabase.DeleteAsset(unityPath);
            return;
        }
        // A mesh re-created under a new GUID: remove it first, so the restored .meta brings the original GUID back.
        if (File.Exists(full) && state.metaExists && File.Exists(full + ".meta") && MetaGuid(full + ".meta") != MetaGuid(state.metaCopy))
            AssetDatabase.DeleteAsset(unityPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        if (state.metaExists) File.Copy(state.metaCopy, full + ".meta", true);
        File.Copy(state.copy, full, true);
        AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
    }

    /// <summary>Records a committed switch: <see cref="applied"/> turns true inside its (still open) Undo group.</summary>
    internal static void Record(string id, string folder, List<Entry> entries, Setting veinsBefore, Setting veinsAfter, string undoName,
        IEnumerable<string> assetsBefore = null, IEnumerable<string> assetsAfter = null)
    {
        var record = CreateInstance<VersionSwitchFileUndo>();
        record.hideFlags = HideFlags.HideAndDontSave;
        record.id = id;
        record.sequence = DateTime.UtcNow.Ticks;
        record.entries = entries;
        record.veinsBefore = veinsBefore;
        record.veinsAfter = veinsAfter;
        record.assetsBefore = (assetsBefore ?? Enumerable.Empty<string>()).ToList();
        record.assetsAfter = (assetsAfter ?? Enumerable.Empty<string>()).ToList();
        record.OnDisk = true;
        Undo.RecordObject(record, undoName);
        record.applied = true;
        Undo.FlushUndoRecordObjects();
        // The record stays while Undo can reach its step; only the file copies of older switches are dropped.
        foreach (var old in All().Where(r => !r.FilesDropped).OrderByDescending(r => r.sequence).Skip(MaxRecords)) old.DropFiles();
    }

    /// <summary>Drops the records of switches touching <paramref name="unityFolder"/> (tests removing their fixtures).</summary>
    internal static void Forget(string unityFolder)
    {
        foreach (var record in All().Where(r => r.entries.Any(e => e.unityPath.StartsWith(unityFolder + "/", StringComparison.OrdinalIgnoreCase)))) record.Delete();
    }

    private static List<VersionSwitchFileUndo> All() =>
        Resources.FindObjectsOfTypeAll<VersionSwitchFileUndo>().Where(r => r != null && !string.IsNullOrEmpty(r.id)).ToList();

    private void Delete()
    {
        SessionState.EraseBool(SessionKey + id);
        SessionState.EraseBool(DroppedKey + id);
        DeleteCopies();
        DestroyImmediate(this);
    }

    private void DropFiles()
    {
        FilesDropped = true;
        DeleteCopies();
    }

    private void DeleteCopies()
    {
        try { Directory.Delete(Path.Combine(Folder, id), true); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
    }

    [InitializeOnLoadMethod]
    private static void Hook()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
        Undo.undoRedoPerformed += OnUndoRedo;
        // Undo history ends with the editor session: copies without a record are from an earlier one.
        EditorApplication.delayCall += () =>
        {
            var live = new HashSet<string>(All().Select(r => r.id), StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(Folder)) return;
            foreach (string directory in Directory.GetDirectories(Folder))
                if (!live.Contains(Path.GetFileName(directory)))
                {
                    try { Directory.Delete(directory, true); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
        };
    }

    private static void OnUndoRedo()
    {
        var changed = All().Where(r => r.applied != r.OnDisk).ToList();
        if (changed.Count == 0) return;
        // Undo walks back from the newest switch, redo forward from the oldest.
        bool undoing = changed.Any(r => !r.applied);
        changed = undoing ? changed.OrderByDescending(r => r.sequence).ToList() : changed.OrderBy(r => r.sequence).ToList();
        var failures = new List<string>();
        var dropped = new List<string>();
        foreach (var record in changed)
        {
            if (record.FilesDropped)
            {
                // Too old to have kept its copies: the scene moved back, its files stay as they are now.
                dropped.AddRange(record.entries.Select(e => e.unityPath).Where(path => !path.EndsWith("~", StringComparison.Ordinal)));
                record.OnDisk = record.applied;
                continue;
            }
            // Files of a folder deleted since the switch stay deleted.
            foreach (var entry in record.entries.Where(e => Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(e.unityPath)))))
            {
                try { Restore(entry.unityPath, record.applied ? entry.after : entry.before); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failures.Add(entry.unityPath + ": " + ex.Message); }
            }
            var veins = record.applied ? record.veinsAfter : record.veinsBefore;
            if (veins != null)
            {
                if (veins.hasKey) EditorPrefs.SetBool(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY, veins.value);
                else EditorPrefs.DeleteKey(CustomVeinsDrawer.CUSTOM_VEINS_PREF_KEY);
            }
            record.OnDisk = record.applied;
        }
        foreach (var editor in Resources.FindObjectsOfTypeAll<MCBEditor>())
        {
            editor.versionModule?.actions?.StartRecalculateCurrentFbxHash();
            editor.Repaint();
        }
        if (failures.Count > 0)
            MCBLogger.LogError("[MCB] The version switch was " + (undoing ? "undone" : "redone") + " in the scene, but these files could not be restored:\n" + string.Join("\n", failures));
        if (dropped.Count > 0)
            MCBLogger.LogError("[MCB] The version switch was " + (undoing ? "undone" : "redone") + " in the scene, but MCB only keeps the files of the last " + MaxRecords +
                " switches. These files were left as they are, so the avatar may not match its models: apply a version or reset the avatar.\n" +
                string.Join("\n", dropped.Distinct(StringComparer.OrdinalIgnoreCase)));
        var missing = changed.SelectMany(r => r.applied ? r.assetsAfter : r.assetsBefore).Where(path => AssetDatabase.LoadMainAssetAtPath(path) == null)
            .Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            MCBLogger.LogError("[MCB] The version switch was " + (undoing ? "undone" : "redone") + ", but these generated meshes were deleted since; apply the version again:\n" + string.Join("\n", missing));
    }

    private static bool SameContent(string a, string b)
    {
        if (!File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using (var sha = SHA256.Create())
        {
            byte[] first, second;
            using (var stream = File.OpenRead(a)) first = sha.ComputeHash(stream);
            using (var stream = File.OpenRead(b)) second = sha.ComputeHash(stream);
            return first.SequenceEqual(second);
        }
    }

    private static string MetaGuid(string metaPath)
    {
        foreach (string line in File.ReadLines(metaPath))
            if (line.StartsWith("guid:", StringComparison.Ordinal)) return line.Substring(5).Trim();
        return null;
    }
}
#endif
