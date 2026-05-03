#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class WarningsModule
{
    public class WarningEntry
    {
        public MessageType type;
        public string title;
        public string message;
    }

    private readonly List<WarningEntry> warnings = new List<WarningEntry>();

    public void AddWarning(string message, MessageType type = MessageType.Warning, string title = null)
    {
        if (string.IsNullOrEmpty(message)) return;
        
        // Avoid duplicates
        foreach (var w in warnings)
        {
            if (w.message == message && w.type == type && w.title == title) return;
        }
        
        warnings.Add(new WarningEntry { message = message, type = type, title = title });
    }

    public void Clear()
    {
        warnings.Clear();
    }
    
    public void Clear(string message)
    {
        warnings.RemoveAll(w => w.message == message);
    }

    public void Draw()
    {
        if (warnings.Count == 0) return;

        for (int i = warnings.Count - 1; i >= 0; i--)
        {
            var warning = warnings[i];
            DrawWarning(warning, i);
        }
    }

    private void DrawWarning(WarningEntry warning, int index)
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        
        // Icon
        GUIContent icon = null;
        switch (warning.type)
        {
            case MessageType.Error:
                icon = EditorGUIUtility.IconContent("console.erroricon");
                break;
            case MessageType.Warning:
                icon = EditorGUIUtility.IconContent("console.warnicon");
                break;
            case MessageType.Info:
                icon = EditorGUIUtility.IconContent("console.infoicon");
                break;
            default:
                 icon = EditorGUIUtility.IconContent("console.infoicon");
                 break;
        }

        if (icon != null)
        {
            // Bigger icon
            GUILayout.Label(icon, GUILayout.Width(40), GUILayout.Height(40)); 
        }

        EditorGUILayout.BeginVertical();
        
        if (!string.IsNullOrEmpty(warning.title))
        {
            GUILayout.Label(warning.title, EditorStyles.boldLabel);
        }

        // Message - vertically centered
        var style = new GUIStyle(EditorStyles.wordWrappedLabel);
        style.alignment = TextAnchor.MiddleLeft;
        GUILayout.Label(warning.message, style, GUILayout.ExpandHeight(true));
        
        EditorGUILayout.EndVertical();

        // OK Button
        if (GUILayout.Button("OK", GUILayout.Width(40), GUILayout.ExpandHeight(true)))
        {
            warnings.RemoveAt(index);
        }

        EditorGUILayout.EndHorizontal();
    }
}
#endif
