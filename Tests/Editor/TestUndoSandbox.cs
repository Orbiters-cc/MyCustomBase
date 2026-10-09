using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Keeps a test out of the user's open scenes and Undo history. Unity runs edit-mode tests in the open scene and reverts
/// their Undo steps only when the whole run ends, so a step left behind replays later (then, or on the user's Ctrl+Z and
/// Ctrl+Y) and can bring an object the test destroyed back into the user's scene. <see cref="Begin"/> fences the test's
/// steps off from the user's; <see cref="End"/> reverts them all, destroys what the test or that revert left in a loaded
/// scene, and fails a test that left a root object there itself. Call it after the test's own cleanup.
/// </summary>
internal sealed class TestUndoSandbox
{
    private const string FenceName = "Test Undo fence";
    private readonly HashSet<int> roots = new HashSet<int>();
    private AnimationClip fence;
    private int group;

    public static TestUndoSandbox Begin()
    {
        var sandbox = new TestUndoSandbox();
        foreach (var root in SceneRoots()) sandbox.roots.Add(root.GetInstanceID());
        Undo.FlushUndoRecordObjects();
        Undo.IncrementCurrentGroup();
        sandbox.group = Undo.GetCurrentGroup();
        // Undoing past the test's own steps reaches this step first, never the user's.
        sandbox.fence = new AnimationClip { name = FenceName, hideFlags = HideFlags.HideAndDontSave };
        Undo.SetCurrentGroupName(FenceName);
        Undo.RegisterCompleteObjectUndo(sandbox.fence, FenceName);
        Undo.IncrementCurrentGroup();
        return sandbox;
    }

    /// <summary>Undoes the test's last step. Fails instead of reaching the user's history when the test has none left.</summary>
    public void PerformUndo()
    {
        string undone = null;
        Undo.UndoRedoEventCallback watch = (in UndoRedoInfo info) => undone = info.undoName;
        Undo.undoRedoEvent += watch;
        try { Undo.PerformUndo(); }
        finally { Undo.undoRedoEvent -= watch; }
        if (undone == null || undone == FenceName) Assert.Fail("Undo went past the test's own steps.");
    }

    /// <summary>Root objects of the loaded (non-preview) scenes that were not there when the test began.</summary>
    public List<GameObject> NewRoots() =>
        SceneRoots().Where(root => !roots.Contains(root.GetInstanceID()) && (root.hideFlags & HideFlags.DontSaveInEditor) == 0).ToList();

    public void DestroyNewRoots()
    {
        foreach (var root in NewRoots()) Object.DestroyImmediate(root);
    }

    public void End()
    {
        var leftByTest = new HashSet<int>(NewRoots().Select(root => root.GetInstanceID()));
        Undo.FlushUndoRecordObjects();
        // A new step drops the steps the test undid (the user's redo steps already went with the fence).
        Undo.IncrementCurrentGroup();
        Undo.RegisterCompleteObjectUndo(fence, FenceName);
        Undo.RevertAllDownToGroup(group);
        Undo.IncrementCurrentGroup();
        // Reverting can bring back objects the test destroyed with Undo, outside their (closed) preview scenes.
        var left = NewRoots();
        var leaked = left.Where(root => leftByTest.Contains(root.GetInstanceID())).Select(root => root.name).ToList();
        foreach (var root in left) Object.DestroyImmediate(root);
        Undo.ClearUndo(fence);
        Object.DestroyImmediate(fence);
        if (leaked.Count > 0) Assert.Fail("The test left " + string.Join(", ", leaked) + " in the user's scene.");
    }

    private static IEnumerable<GameObject> SceneRoots()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects()) yield return root;
        }
    }
}
