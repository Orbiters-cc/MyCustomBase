#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

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

    private void OnGUI()
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
