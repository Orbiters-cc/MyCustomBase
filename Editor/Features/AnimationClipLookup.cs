#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Animation clips by name, for correctives that name the clip that drives them. A project holds thousands of clips
/// (the SDK's, VRCFury's, locomotion packs'), so a lookup loads none of them up front: a clip that is an asset of its
/// own (.anim) carries its file's name, and only the files named like the clip are loaded. Clips inside model files
/// can only be named by loading the model: those are read once and remembered until the project changes.
/// </summary>
internal static class AnimationClipLookup
{
    private static readonly Dictionary<string, List<AnimationClip>> ByName = new Dictionary<string, List<AnimationClip>>(StringComparer.Ordinal);
    // Path of each file whose main asset is not a clip (a model with takes) -> the name of the clip it yields, as
    // LoadAssetAtPath<AnimationClip> answers it (its first clip).
    private static Dictionary<string, string> modelClipNames;
    private static bool hooked;

    /// <summary>The project's clips with this exact name, ordered by asset path; empty for a blank name.</summary>
    public static List<AnimationClip> Find(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return new List<AnimationClip>();
        Hook();
        if (ByName.TryGetValue(clipName, out var known) && known.TrueForAll(clip => clip != null)) return known;

        var paths = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) continue;
            if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(AnimationClip))
            {
                // Unity names a clip asset after its file.
                if (string.Equals(Path.GetFileNameWithoutExtension(path), clipName, StringComparison.Ordinal)) paths.Add(path);
            }
            else if (string.Equals(ModelClipName(path), clipName, StringComparison.Ordinal))
            {
                paths.Add(path);
            }
        }

        var clips = new List<AnimationClip>();
        paths.Sort(string.CompareOrdinal);
        foreach (string path in paths)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip != null && string.Equals(clip.name, clipName, StringComparison.Ordinal)) clips.Add(clip);
        }

        ByName[clipName] = clips;
        return clips;
    }

    /// <summary>Forgets every answer (the project changed).</summary>
    public static void Clear()
    {
        ByName.Clear();
        modelClipNames = null;
    }

    private static string ModelClipName(string path)
    {
        if (modelClipNames == null) modelClipNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!modelClipNames.TryGetValue(path, out string name))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            name = clip != null ? clip.name : null;
            modelClipNames[path] = name;
        }
        return name;
    }

    private static void Hook()
    {
        if (hooked) return;
        hooked = true;
        EditorApplication.projectChanged += Clear;
    }
}
#endif
