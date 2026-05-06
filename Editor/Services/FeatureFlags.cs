#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

public static class FeatureFlags
{
    // Define flags
    public const string SUPPORT_USER_UNKNOWN_VERSION = "SUPPORT_USER_UNKNOWN_VERSION";
    public const string ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR = "ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR";
    public const string ALLOW_ADVANCED_MESH_ON_BLENDER_LINK = "ALLOW_ADVANCED_MESH_ON_BLENDER_LINK";

    private class FlagDef
    {
        public string key;
        public string label;
        public string description;
        public bool defaultValue;
    }

    private static readonly List<FlagDef> _defs = new List<FlagDef>
    {
        new FlagDef
        {
            key = SUPPORT_USER_UNKNOWN_VERSION,
            label = "Support custom (unknown-hash) base",
            description = "Detect and manage user-custom avatar bases when the current FBX hash is unknown but a .fbx.old exists.",
            defaultValue = false
        },
        new FlagDef
        {
            key = ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR,
            label = "Allow advanced replacement for creator",
            description = "Experimental: build submitted custom base model patches as encrypted native Unity mesh payloads instead of replacement FBX bytes.",
            defaultValue = false
        },
        new FlagDef
        {
            key = ALLOW_ADVANCED_MESH_ON_BLENDER_LINK,
            label = "Allow advanced mesh on Blender link",
            description = "Experimental: advertise native mesh transfer support to the Blender connector. Requires advanced replacement for creator.",
            defaultValue = false
        }
    };

    public static IEnumerable<(string key, string label, string description)> All()
    {
        foreach (var d in _defs)
            yield return (d.key, d.label, d.description);
    }

    public static bool IsEnabled(string key)
    {
        if (key == ALLOW_ADVANCED_MESH_ON_BLENDER_LINK && !IsEnabled(ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR))
        {
            return false;
        }

        var def = _defs.Find(d => d.key == key);
        bool defaultVal = def != null ? def.defaultValue : false;
        try { return EditorPrefs.GetBool(GetPrefKey(key), defaultVal); } catch { return defaultVal; }
    }

    public static void SetEnabled(string key, bool enabled)
    {
        try { EditorPrefs.SetBool(GetPrefKey(key), enabled); } catch { }
    }

    private static string GetPrefKey(string key) => $"MCB_FeatureFlag_{key}";
}
#endif
