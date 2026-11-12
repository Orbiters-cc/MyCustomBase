#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One install or one going-back as a transaction over the avatar: a single Undo step holding the complete state of
/// every object, renderer and transform of the avatar. <see cref="Fail"/> puts everything back (what Undo covers and
/// what it does not, such as recomputed bounds) and removes the files the step created in MCB's migration folder.
/// </summary>
internal sealed class AvatarTransaction
{
    public int Group { get; private set; }
    private Transform root;
    private VersionPoseLedgerService.Snapshot start;
    private HashSet<string> filesBefore;
    private bool ended;

    public static AvatarTransaction Begin(Transform root, string name)
    {
        var transaction = new AvatarTransaction { root = root };
        Undo.IncrementCurrentGroup();
        transaction.Group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(name);
        if (root != null)
            Undo.RegisterCompleteObjectUndo(root.GetComponentsInChildren<Transform>(true).SelectMany(t => new UnityEngine.Object[] { t, t.gameObject })
                .Concat(root.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is Transform))).ToArray(), name);
        transaction.start = VersionPoseLedgerService.CaptureState(root);
        transaction.filesBefore = Files();
        return transaction;
    }

    private static HashSet<string> Files()
    {
        string folder = Path.GetFullPath(LegacyMigrationService.MigratedFolder);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return result;
        foreach (string entry in Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories))
            if (!entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetFullPath(entry));
        result.Add(folder);
        return result;
    }

    /// <summary>Everything back as it was at <see cref="Begin"/>.</summary>
    public void Fail()
    {
        try { Undo.RevertAllDownToGroup(Group); }
        catch (Exception ex) { MCBLogger.LogWarning("[MCB] Undo could not put back every change: " + ex.Message); }
        VersionPoseLedgerService.RestoreSnapshot(start);
        string project = Path.GetFullPath(".");
        var created = Files().Where(f => !filesBefore.Contains(f)).OrderByDescending(f => f.Length).ToList();
        foreach (string full in created)
        {
            string asset = MCBUtils.ToUnityPath(full.Substring(project.Length).TrimStart('\\', '/'));
            if (!AssetDatabase.DeleteAsset(asset) && File.Exists(full)) File.Delete(full);
        }
    }

    public void End()
    {
        if (ended) return;
        ended = true;
        Undo.CollapseUndoOperations(Group);
        Undo.IncrementCurrentGroup();
    }
}
#endif
