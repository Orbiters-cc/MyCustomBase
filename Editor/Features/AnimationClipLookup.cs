#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Animation clips by name, for correctives that name the clip that drives them. A project holds thousands of clips
/// (the SDK's, VRCFury's, locomotion packs'), so the name of each is indexed without loading it where it can be: a
/// clip asset saved as text (.anim) carries its name on its <c>m_Name</c> line, which need not be its file name
/// ("fa9011af_Spine_Corrective.anim" holds "Spine_Corrective"). A binary clip asset, or a model file with takes, is
/// loaded once to read the name. The index lives until an import, move or delete touches a file, and then only that
/// file is read again; a lookup loads only the clips it returns.
/// </summary>
/// <remarks>Same answers as loading every clip of FindAssets("t:AnimationClip") and grouping by name: one clip per
/// file (LoadAssetAtPath&lt;AnimationClip&gt;, a model's first clip), ordered by path.</remarks>
internal static class AnimationClipLookup
{
    // Path -> the name of the clip it yields (null: none). Null until first used.
    private static Dictionary<string, string> nameByPath;
    // Files imported, moved in or deleted since the index was read.
    private static readonly HashSet<string> stale = new HashSet<string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<AnimationClip>> ByName = new Dictionary<string, List<AnimationClip>>(StringComparer.Ordinal);

    /// <summary>The project's clips with this exact name, ordered by asset path; empty for a blank name.</summary>
    public static List<AnimationClip> Find(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return new List<AnimationClip>();
        Refresh();
        if (ByName.TryGetValue(clipName, out var known) && known.TrueForAll(clip => clip != null)) return known;

        var paths = new List<string>();
        foreach (var pair in nameByPath)
            if (string.Equals(pair.Value, clipName, StringComparison.Ordinal)) paths.Add(pair.Key);
        paths.Sort(string.CompareOrdinal);

        var clips = new List<AnimationClip>();
        foreach (string path in paths)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip != null && string.Equals(clip.name, clipName, StringComparison.Ordinal)) clips.Add(clip);
        }

        ByName[clipName] = clips;
        return clips;
    }

    /// <summary>Forgets everything: the next lookup reads the whole index again.</summary>
    public static void Clear()
    {
        nameByPath = null;
        stale.Clear();
        ByName.Clear();
    }

    /// <summary>These files changed: they are read again on the next lookup.</summary>
    internal static void MarkChanged(IEnumerable<string> paths)
    {
        if (nameByPath == null) return;
        foreach (string path in paths)
            if (!string.IsNullOrEmpty(path)) stale.Add(path);
    }

    private static void Refresh()
    {
        if (nameByPath == null)
        {
            nameByPath = new Dictionary<string, string>(StringComparer.Ordinal);
            stale.Clear();
            ByName.Clear();
            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) nameByPath[path] = ReadName(path, true);
            }
            return;
        }

        if (stale.Count == 0) return;
        foreach (string path in stale)
        {
            bool wasIndexed = nameByPath.Remove(path);
            // A deleted file, or one that holds no clip, is no longer indexed.
            string name = ReadName(path, wasIndexed);
            if (name != null) nameByPath[path] = name;
        }
        stale.Clear();
        ByName.Clear();
    }

    // knownToHoldClips: the file is known to hold a clip (found by FindAssets, or indexed before). Otherwise only clip
    // assets and model files are opened: an imported texture or prefab is never loaded to find out.
    private static string ReadName(string path, bool knownToHoldClips)
    {
        var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
        if (mainType == null) return null;
        if (mainType == typeof(AnimationClip))
        {
            string name = ReadSerializedName(path);
            if (name != null) return name;
        }
        else if (!knownToHoldClips && !(AssetImporter.GetAtPath(path) is ModelImporter))
        {
            return null;
        }
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        return clip != null ? clip.name : null;
    }

    /// <summary>The <c>m_Name</c> of a clip asset saved as text; null when it cannot be read that way (binary, quoted).</summary>
    internal static string ReadSerializedName(string path)
    {
        try
        {
            using (var reader = new StreamReader(FileUtil.GetPhysicalPath(path)))
            {
                string first = reader.ReadLine();
                if (first == null || !first.StartsWith("%YAML", StringComparison.Ordinal)) return null;
                for (int i = 0; i < 64; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) return null;
                    string trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("m_Name:", StringComparison.Ordinal)) continue;
                    string value = trimmed.Substring("m_Name:".Length).Trim();
                    if (value.Length == 0) return string.Empty;
                    if (value[0] == '\'' && value.Length >= 2 && value[value.Length - 1] == '\'')
                        return value.Substring(1, value.Length - 2).Replace("''", "'");
                    // Double quotes carry escapes: leave those to Unity.
                    return value[0] == '"' ? null : value;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }
}

/// <summary>Keeps <see cref="AnimationClipLookup"/>'s index in step with the files the import pipeline touches.</summary>
internal sealed class AnimationClipLookupPostprocessor : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        AnimationClipLookup.MarkChanged(imported);
        AnimationClipLookup.MarkChanged(deleted);
        AnimationClipLookup.MarkChanged(moved);
        AnimationClipLookup.MarkChanged(movedFrom);
    }
}
#endif
