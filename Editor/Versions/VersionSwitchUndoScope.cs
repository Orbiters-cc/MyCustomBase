#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Undo for the synchronous mutation phase of a version switch. All awaited preparation must finish before creation.
/// Keeping this phase inside one editor callback prevents unrelated scene and asset edits entering its Undo range.
/// </summary>
internal sealed class VersionSwitchUndoScope
{
    public int Group { get; }

    public VersionSwitchUndoScope(Transform avatarRoot, string name)
    {
        Undo.IncrementCurrentGroup();
        Group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(name);
    }

    public void Commit()
    {
        Undo.CollapseUndoOperations(Group);
        Undo.IncrementCurrentGroup();
    }

    public void Rollback()
    {
        try
        {
            Undo.FlushUndoRecordObjects();
            Undo.RevertAllDownToGroup(Group);
        }
        finally { Undo.IncrementCurrentGroup(); }
    }

    public void Abandon() => Undo.IncrementCurrentGroup();
}
#endif
