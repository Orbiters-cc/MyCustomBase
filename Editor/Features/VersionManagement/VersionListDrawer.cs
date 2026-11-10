
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public partial class VersionListDrawer
{
    private readonly MCBEditor editor;
    private readonly VersionActions actions;
    private readonly NetworkService networkService = new NetworkService();
    
    // UI State
    private static readonly Color AccentGreen = new Color32(0, 218, 109, 255);
    private static Dictionary<string, bool> individualChangelogStates = new Dictionary<string, bool>();
    private static bool isListCollapsed = true;
    private static string editingVersionKey;
    private static string editingVersionTitleDraft = "";
    private static string editingVersionChangelogDraft = "";
    private static bool isUpdatingVersionMetadata;
    private static string versionMetadataUpdateError;
    
    // Special reset version identifier
    public static readonly CustomBaseVersion RESET_VERSION = new CustomBaseVersion
    {
        version = "Base Default",
        scope = Scope.PUBLIC,
        changelog = "Reset to the original avatar configuration without any custom base modifications."
    };
    
    // Cached textures for collapse/expand icons
    private static Texture2D collapseIcon;
    private static Texture2D expandIcon;

    public VersionListDrawer(MCBEditor editor, VersionActions actions)
    {
        this.editor = editor;
        this.actions = actions;
        LoadIcons();
    }
    
    private void LoadIcons()
    {
        if (collapseIcon == null)
            collapseIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/collapse.png");
        if (expandIcon == null)
            expandIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/expand.png");
    }

    private static string GetInitials(string name)
    {
        if (string.IsNullOrEmpty(name)) return "?";
        string[] parts = name.Split(new[] { ' ', '\t', '\n', '\r', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        string a = parts[0].Substring(0, 1);
        string b = parts[parts.Length - 1].Substring(0, 1);
        return (a + b).ToUpperInvariant();
    }

    private Color GetColorForScope(Scope scope) => scope switch
    {
        Scope.PUBLIC => AccentGreen,
        Scope.BETA => Color.yellow,
        Scope.ALPHA => Color.red,
        _ => Color.magenta
    };
}
#endif
