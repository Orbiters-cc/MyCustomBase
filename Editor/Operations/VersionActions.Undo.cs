#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCBEditorUtils;

// The switch as one transaction: the files it may change are kept first, restored on failure, and recorded for Undo/Redo.
public partial class VersionActions
{
    private void CommitActiveTransition()
    {
        if (activeTransitionRollback == null) return;

        activeTransitionRollback.Commit();
        activeTransitionRollback = null;
    }

    private void RollbackActiveTransition()
    {
        var snapshot = activeTransitionRollback;
        activeTransitionRollback = null;
        if (snapshot == null) return;

        try
        {
            snapshot.Rollback();
            MCBLogger.Log("[VersionActions] Restored the exact previous FBX and scene state after the failed version transition.");
        }
        catch (Exception rollbackException)
        {
            MCBLogger.LogError($"[VersionActions] Version transition rollback failed: {rollbackException}");
            ClearAppliedVersionState();
            session.warningsModule?.AddWarning(
                "The version switch failed and MCB could not fully restore the previous state. The applied-version marker was cleared to avoid reporting a mismatched version.",
                MessageType.Error,
                "Version rollback failed");
        }
    }

    // Before the mutation: the files it may change, to restore them on failure. On success the state after it is kept
    // too, so Undo/Redo of the switch also puts those files back (VersionSwitchFileUndo).
    private sealed class VersionTransitionRollbackSnapshot
    {
        private const string UndoName = "Switch MCB Version";
        private readonly VersionSwitchUndoScope undo;
        private readonly string id = Guid.NewGuid().ToString("N");
        private readonly string folder;
        private readonly List<string> fbxPaths = new List<string>();
        private readonly List<VersionSwitchFileUndo.Entry> entries = new List<VersionSwitchFileUndo.Entry>();
        private VersionSwitchFileUndo.Setting veinsBefore;
        private readonly McbSession session;
        private readonly Transform root;
        private readonly List<string> assetsBefore;
        private readonly bool editorWasCustomBase;
        private readonly bool editorCurrentWasCustom;
        private bool completed;

        private VersionTransitionRollbackSnapshot(MyCustomBase target, McbSession session)
        {
            this.session = session;
            folder = Path.Combine(VersionSwitchFileUndo.Folder, id);
            editorWasCustomBase = session != null && session.isCustomBase;
            editorCurrentWasCustom = session != null && session.currentIsCustom;
            root = target != null ? AvatarPaths.Root(target) : null;
            assetsBefore = VersionSwitchFileUndo.UsedGeneratedAssets(root).ToList();
            undo = new VersionSwitchUndoScope(root, UndoName);
            if (target != null)
            {
                Undo.RegisterCompleteObjectUndo(target, UndoName);
            }
        }

        public static VersionTransitionRollbackSnapshot Capture(
            IEnumerable<string> fbxPaths,
            MyCustomBase target,
            McbSession session)
        {
            var snapshot = new VersionTransitionRollbackSnapshot(target, session);
            try
            {
                snapshot.fbxPaths.AddRange(fbxPaths ?? Enumerable.Empty<string>());
                foreach (string path in VersionSwitchFileUndo.AffectedPaths(snapshot.fbxPaths))
                {
                    snapshot.entries.Add(new VersionSwitchFileUndo.Entry
                    {
                        unityPath = path,
                        before = VersionSwitchFileUndo.Save(path, snapshot.folder, "before")
                    });
                }
                snapshot.veinsBefore = VersionSwitchFileUndo.SaveVeins();
                return snapshot;
            }
            catch
            {
                snapshot.CleanupFiles();
                snapshot.undo.Abandon();
                throw;
            }
        }

        public void Commit()
        {
            if (completed) return;

            try
            {
                foreach (string path in VersionSwitchFileUndo.AffectedPaths(fbxPaths))
                {
                    if (entries.Any(entry => string.Equals(entry.unityPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                    // Created by the switch (a dynamic normals mesh): Undo removes it.
                    entries.Add(new VersionSwitchFileUndo.Entry { unityPath = path, before = new VersionSwitchFileUndo.FileState() });
                }
                foreach (var entry in entries)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(entry.unityPath);
                    if (asset != null && !(asset is GameObject)) AssetDatabase.SaveAssetIfDirty(asset);
                    // Import settings the switch left unsaved are written now: Undo compares the files with what it keeps here.
                    else if (asset is GameObject) AssetDatabase.WriteImportSettingsIfDirty(entry.unityPath);
                    entry.after = VersionSwitchFileUndo.Save(entry.unityPath, folder, "after", entry.before);
                }
                VersionSwitchFileUndo.Record(id, folder, entries, veinsBefore, VersionSwitchFileUndo.SaveVeins(), UndoName,
                    assetsBefore, VersionSwitchFileUndo.UsedGeneratedAssets(root));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MCBLogger.LogWarning($"[VersionActions] The version switch worked, but its files could not be kept for Undo ({ex.Message}). Undoing it will only restore the scene.");
                CleanupFiles();
            }
            undo.Commit();
            completed = true;
        }

        public void Rollback()
        {
            if (completed) return;

            Exception rollbackFailure = null;
            foreach (var entry in entries)
            {
                try
                {
                    VersionSwitchFileUndo.Restore(entry.unityPath, entry.before);
                }
                catch (Exception ex)
                {
                    rollbackFailure = rollbackFailure ?? ex;
                }
            }

            try
            {
                undo.Rollback();
            }
            catch (Exception ex)
            {
                rollbackFailure = rollbackFailure ?? ex;
            }

            if (session != null)
            {
                session.isCustomBase = editorWasCustomBase;
                session.currentIsCustom = editorCurrentWasCustom;
            }

            completed = true;
            if (rollbackFailure == null) CleanupFiles();

            if (rollbackFailure != null)
            {
                throw new InvalidOperationException("Could not restore the previous version-switch state. Recovery copies are retained at: " + folder, rollbackFailure);
            }
        }

        private void CleanupFiles()
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MCBLogger.LogWarning($"[VersionActions] Could not delete temporary version rollback files '{folder}': {ex.Message}");
            }
        }
    }
}
#endif
