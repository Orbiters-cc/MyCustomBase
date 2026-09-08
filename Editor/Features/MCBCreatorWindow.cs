#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Hosts the existing creator form without duplicating its draft or build pipeline.</summary>
public sealed class MCBCreatorWindow : EditorWindow
{
    private MCBEditor sourceEditor;
    private CreatorModeModule module;
    private int assetId;

    internal static MCBCreatorWindow Open(MCBEditor editor, CreatorModeModule creator)
    {
        var window = CreateInstance<MCBCreatorWindow>();
        window.sourceEditor = editor;
        window.module = creator;
        window.assetId = editor.GetSelectedAsset()?.id ?? 0;
        window.titleContent = new GUIContent("Create MCB Version");
        window.minSize = new Vector2(600, 480);
        window.position = new Rect(150, 120, 740, 820);
        window.BuildUI();
        return window;
    }

    internal bool MatchesContext(MCBEditor editor) => editor != null && editor == sourceEditor &&
        editor.customBaseTarget != null && (editor.GetSelectedAsset()?.id ?? 0) == assetId;

    internal void DetachAndClose()
    {
        // The inspector is disposing: do not rebuild it from the close callback.
        module = null;
        Close();
    }

    private void CreateGUI()
    {
        if (module != null) BuildUI();
        else rootVisualElement.Add(new Label("Open Create new version from the asset to continue."));
    }

    private void BuildUI()
    {
        rootVisualElement.Clear();
        MCBEditor.LoadUiToolkitStyleSheets(rootVisualElement);
        rootVisualElement.AddToClassList("mcb-creator-window");
        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.AddToClassList("mcb-creator-window__scroll");
        rootVisualElement.Add(scroll);
        var form = new VisualElement();
        scroll.Add(form);
        module.AttachWindow(this, form);
    }

    private void OnDisable()
    {
        var previous = module;
        module = null;
        previous?.WindowClosed();
    }
}
#endif
