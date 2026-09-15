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
        var theme = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mcb/Editor/Styles/mcb-theme.uss");
        if (theme != null) root.styleSheets.Add(theme);
        var refreshStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mcb/Editor/Styles/mcb-advanced.uss");
        if (refreshStyle != null) root.styleSheets.Add(refreshStyle);
        var refresh = new VisualElement();
        refresh.AddToClassList("mcb-advanced-refresh");
        var feedback = new Label("Fetch the latest version list and fresh asset images.");
        feedback.AddToClassList("mcb-advanced-refresh__status");
        var button = new Button { text = "Reload versions and banners", name = "mcb-reload-versions-banners" };
        button.AddToClassList("mcb-button");
        button.AddToClassList("mcb-button--primary");
        button.AddToClassList("mcb-advanced-refresh__button");
        bool busy = false;
        System.Action reload = () =>
        {
            if (busy) return;
            if (activeEditor == null || !activeEditor.HasServerAccess)
            {
                feedback.text = "Connect to the server in the MCB inspector, then retry.";
                return;
            }
            busy = true;
            button.text = "Reloading…";
            button.SetEnabled(false);
            feedback.text = "Requesting fresh versions and images…";
            root.schedule.Execute(() =>
            {
                try
                {
                    if (activeEditor == null) throw new System.InvalidOperationException("Select an MCB component first.");
                    activeEditor.ReloadVersionsAndBanners();
                    feedback.text = "Reload requested. Progress and results appear in the MCB inspector.";
                }
                catch (System.Exception ex) { feedback.text = "Reload failed: " + ex.Message; }
                finally
                {
                    root.schedule.Execute(() =>
                    {
                        busy = false;
                        button.text = "Reload versions and banners";
                        button.SetEnabled(true);
                    }).StartingIn(1000);
                }
            });
        };
        button.clicked += reload;
        button.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) reload(); });
        refresh.Add(button);
        refresh.Add(feedback);
        root.Add(refresh);
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
