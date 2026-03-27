#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

public static class FeatureFlags
{
    // Define flags
    public const string SUPPORT_USER_UNKNOWN_VERSION = "SUPPORT_USER_UNKNOWN_VERSION";

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
            description = "Detect and manage user-custom Winterpaw bases when the current FBX hash is unknown but a .fbx.old exists.",
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
