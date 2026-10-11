#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

/// <summary>Hosts the existing creator form without duplicating its draft or build pipeline.</summary>
public sealed class MCBCreatorWindow : EditorWindow
{
    private MCBEditor sourceEditor;
    private CreatorModeModule module;
    [SerializeField] private MyCustomBase avatar;
    [SerializeField] private string assetJson;
    [SerializeField] private int assetId;
    [SerializeField] private string draftJson;
    [SerializeField] internal bool showBuildSuccess;
    [SerializeField] internal string builtVersion;
    [SerializeField] internal string builtDefaultVersion;
    private bool disposing;
    [System.Serializable] private class WindowState
    {
        public string draft, version, defaultVersion;
        public bool success;
    }
    private string SessionKey => "MCB_CreatorWindow_" + (avatar != null ? avatar.mcbComponentId : "") + "_" + assetId;

    internal static MCBCreatorWindow Open(MCBEditor editor, CreatorModeModule creator)
    {
        var existing = Resources.FindObjectsOfTypeAll<MCBCreatorWindow>()
            .FirstOrDefault(w => w.avatar == editor.customBaseTarget && w.assetId == editor.GetSelectedAsset()?.id);
        if (existing != null) return existing;
        var window = CreateInstance<MCBCreatorWindow>();
        window.avatar = editor.customBaseTarget;
        McbInstanceIdentityService.EnsureIdentity(window.avatar);
        window.assetJson = JsonConvert.SerializeObject(editor.GetSelectedAsset());
        window.draftJson = creator.SaveWindowDraft();
        window.assetId = editor.GetSelectedAsset()?.id ?? 0;
        string saved = SessionState.GetString(window.SessionKey, "");
        if (!string.IsNullOrEmpty(saved))
        {
            var state = JsonUtility.FromJson<WindowState>(saved);
            window.draftJson = state.draft; window.showBuildSuccess = state.success;
            window.builtVersion = state.version; window.builtDefaultVersion = state.defaultVersion;
        }
        window.titleContent = new GUIContent("Create MCB Version");
        window.minSize = new Vector2(600, 480);
        window.position = new Rect(150, 120, 740, 820);
        window.RecoverContext();
        return window;
    }

    internal bool MatchesContext(MCBEditor editor) => editor != null && editor == sourceEditor &&
        editor.customBaseTarget != null && (editor.GetSelectedAsset()?.id ?? 0) == assetId;

    internal void DetachAndClose()
    {
        // Inspector/domain reloads must not destroy the window's saved context. The editor stays referenced: the
        // window's OnDisable destroys it. Dropping it here (its OnDisable can run first during a reload) leaked one
        // live editor, background services included, per reload.
        module = null;
    }

    // Names the editors creator windows create, so ones a reload left without a window can be found and destroyed.
    private const string OwnedEditorName = "MCB Creator Window Editor";

    private void CreateGUI()
    {
        RecoverContext();
    }

    private void OnEnable() { EditorApplication.delayCall += RecoverContext; }

    private void RecoverContext()
    {
        if (disposing || avatar == null || string.IsNullOrEmpty(assetJson)) return;
        if (sourceEditor == null)
        {
            DestroyOrphanedEditors();
            sourceEditor = (MCBEditor)UnityEditor.Editor.CreateEditor(avatar, typeof(MCBEditor));
            sourceEditor.name = OwnedEditorName;
            sourceEditor.SetCreatorWindowAsset(JsonConvert.DeserializeObject<AvatarDiscoveredAsset>(assetJson));
            sourceEditor.serializedObject.Update();
            sourceEditor.isCreatorModeProp.boolValue = true;
            sourceEditor.serializedObject.ApplyModifiedProperties();
            module = sourceEditor.creatorModule;
            module.LoadWindowDraft(draftJson);
        }
        BuildUI();
    }

    // Editors an earlier creator window created but no open window holds any more (a domain reload drops the reference).
    private static void DestroyOrphanedEditors()
    {
        var held = new HashSet<MCBEditor>(Resources.FindObjectsOfTypeAll<MCBCreatorWindow>().Select(w => w.sourceEditor).Where(e => e != null));
        foreach (var orphan in Resources.FindObjectsOfTypeAll<MCBEditor>().Where(e => e.name == OwnedEditorName && !held.Contains(e) && !e.isSubmitting))
            DestroyImmediate(orphan);
    }

    private void Update()
    {
        if (module != null) draftJson = module.SaveWindowDraft();
    }

    internal void Built(CustomBaseVersion version)
    {
        showBuildSuccess = true;
        builtVersion = version.version;
        builtDefaultVersion = version.defaultAviVersion;
    }

    private void BuildUI()
    {
        rootVisualElement.Clear();
        var theme = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss");
        if (theme != null && !rootVisualElement.styleSheets.Contains(theme)) rootVisualElement.styleSheets.Add(theme);
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
        EditorApplication.delayCall -= RecoverContext;
        if (module != null) draftJson = module.SaveWindowDraft();
        if (avatar != null) SessionState.SetString(SessionKey, JsonUtility.ToJson(new WindowState {
            draft = draftJson, version = builtVersion, defaultVersion = builtDefaultVersion, success = showBuildSuccess }));
        disposing = true;
        var previousEditor = sourceEditor;
        module = null;
        sourceEditor = null;
        if (previousEditor != null && previousEditor.isSubmitting)
        {
            // Closing the UI must not dispose the context of an upload still in flight.
            previousEditor.creatorModule.DetachUIToolkit();
            EditorApplication.CallbackFunction cleanup = null;
            cleanup = () =>
            {
                if (previousEditor != null && previousEditor.isSubmitting) return;
                EditorApplication.update -= cleanup;
                if (previousEditor != null) DestroyImmediate(previousEditor);
            };
            EditorApplication.update += cleanup;
        }
        else if (previousEditor != null) DestroyImmediate(previousEditor);
        disposing = false;
    }
}
#endif
