#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Keeps renderers off the temporary meshes of <see cref="BlenderLivePreview"/> once no preview shows them. A live mesh is
/// destroyed when its preview ends, but a state recorded while it showed (an Undo step of another tool, such as textures
/// applied from Substance 3D Painter) can put it back on its renderer later: a missing mesh, the avatar's body gone, and
/// saved so if the scene is. Each live mesh's original is remembered for the editor session (across script reloads); after
/// Undo and Redo, after script reloads and before scene saves, a renderer showing a live mesh no preview shows gets the
/// original back.
/// </summary>
[InitializeOnLoad]
internal static class BlenderLiveMeshGuard
{
    private const string StateKey = "MCB.BlenderLiveMeshGuard";
    // Live mesh instance id -> its original's GlobalObjectId.
    private static readonly Dictionary<int, string> originals = Load();
    // The live meshes previews show now: left alone.
    private static readonly HashSet<int> shown = new HashSet<int>();

    static BlenderLiveMeshGuard()
    {
        Undo.undoRedoPerformed += () => Repair();
        EditorSceneManager.sceneSaving += (scene, path) => Repair();
        // After a script reload (the previews were reverted before it): once, without waiting for a repaint.
        EditorApplication.CallbackFunction once = null;
        once = () => { EditorApplication.update -= once; Repair(); };
        EditorApplication.update += once;
    }

    /// <summary>A preview shows <paramref name="live"/> in place of <paramref name="original"/>.</summary>
    public static void Shown(Mesh live, Mesh original)
    {
        int id = live.GetInstanceID();
        shown.Add(id);
        if (originals.ContainsKey(id) || !EditorUtility.IsPersistent(original)) return;
        originals[id] = GlobalObjectId.GetGlobalObjectIdSlow(original).ToString();
        SessionState.SetString(StateKey, string.Join("\n", originals.Select(pair => pair.Key + "\t" + pair.Value)));
    }

    /// <summary>The preview of <paramref name="live"/> ended (the mesh is destroyed next).</summary>
    public static void Released(Mesh live)
    {
        if (live != null) shown.Remove(live.GetInstanceID());
    }

    /// <summary>The original a live mesh stands for, or null when <paramref name="mesh"/> is not one.</summary>
    public static Mesh OriginalOf(Mesh mesh) => mesh != null ? Original(mesh.GetInstanceID()) : null;

    /// <summary>Puts the originals back on every renderer showing a live mesh no preview shows; how many it changed.</summary>
    public static int Repair()
    {
        if (originals.Count == 0) return 0;
        int repaired = 0;
        foreach (var renderer in Object.FindObjectsOfType<SkinnedMeshRenderer>(true))
        {
            // The serialized reference: a destroyed mesh reads as null through sharedMesh.
            int id = new SerializedObject(renderer).FindProperty("m_Mesh").objectReferenceInstanceIDValue;
            if (id == 0 || shown.Contains(id) || !originals.ContainsKey(id)) continue;
            var original = Original(id);
            if (original == null) continue;
            renderer.sharedMesh = original;
            EditorUtility.SetDirty(renderer);
            if (renderer.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
            MCBLogger.LogWarning($"[BlenderLive] '{renderer.name}' was showing a Blender live mesh no longer shown (an Undo or Redo of a step recorded while it was live): its mesh '{original.name}' is back.");
            repaired++;
        }
        return repaired;
    }

    private static Mesh Original(int liveId) =>
        originals.TryGetValue(liveId, out string id) && GlobalObjectId.TryParse(id, out var parsed)
            ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as Mesh
            : null;

    private static Dictionary<int, string> Load()
    {
        var result = new Dictionary<int, string>();
        foreach (string line in SessionState.GetString(StateKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0 && int.TryParse(line.Substring(0, tab), out int id)) result[id] = line.Substring(tab + 1);
        }
        return result;
    }
}
#endif
