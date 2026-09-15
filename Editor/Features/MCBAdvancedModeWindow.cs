#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class MCBAdvancedModeWindow : EditorWindow
{
    private static MCBEditor activeEditor;
    private static AdvancedModeModule activeModule;

    private Vector2 scrollPosition;

    public static void Open(MCBEditor editor, AdvancedModeModule module)
    {
        activeEditor = editor;
        activeModule = module;

        var window = GetWindow<MCBAdvancedModeWindow>("MCB Advanced Mode");
        window.minSize = new Vector2(620f, 520f);
        window.Show();
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.Clear();
        // Keep the existing, unmigrated advanced controls in their IMGUI surface.
        root.Add(new IMGUIContainer(DrawLegacyContents) { style = { flexGrow = 1 } });
    }

    private void DrawLegacyContents()
    {
        if (activeEditor == null || activeModule == null)
        {
            EditorGUILayout.HelpBox("Select an MCB component and open Advanced Mode from its status bar.", MessageType.Info);
            return;
        }

        activeEditor.serializedObject.Update();
        try
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            try
            {
                activeModule.DrawWindowContents();
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }
        finally
        {
            activeEditor.serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
