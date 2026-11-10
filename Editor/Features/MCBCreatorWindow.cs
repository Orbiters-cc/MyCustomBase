#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Linq;
using Newtonsoft.Json;

/// <summary>Hosts the creator form of an avatar's session for one asset, without an Inspector of the avatar.</summary>
public sealed class MCBCreatorWindow : EditorWindow
{
    private CreatorModeModule module;
    [SerializeField] private MyCustomBase avatar;
    [SerializeField] private string assetJson;
    [SerializeField] private int assetId;
    [SerializeField] private string draftJson;
    [SerializeField] internal bool showBuildSuccess;
    [SerializeField] internal string builtVersion;
    [SerializeField] internal string builtDefaultVersion;
    [System.Serializable] private class WindowState
    {
        public string draft, version, defaultVersion;
        public bool success;
    }
    private string SessionKey => "MCB_CreatorWindow_" + (avatar != null ? avatar.mcbComponentId : "") + "_" + assetId;

    /// <summary>The window of this form's avatar and asset: the open one, else a new one starting from the form's draft.</summary>
    internal static MCBCreatorWindow Open(McbSession session, CreatorModeModule creator)
    {
        var existing = Resources.FindObjectsOfTypeAll<MCBCreatorWindow>()
            .FirstOrDefault(w => w.avatar == session.customBaseTarget && w.assetId == creator.Asset?.id);
        if (existing != null) return existing;
        var window = CreateInstance<MCBCreatorWindow>();
        window.avatar = session.customBaseTarget;
        McbInstanceIdentityService.EnsureIdentity(window.avatar);
        window.assetJson = JsonConvert.SerializeObject(creator.Asset);
        window.draftJson = creator.SaveWindowDraft();
        window.assetId = creator.Asset?.id ?? 0;
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

    /// <summary>Whether this window shows that form.</summary>
    internal bool Holds(CreatorModeModule form) => form != null && form == module;

    internal void DetachAndClose()
    {
        // Inspector/domain reloads must not destroy the window's saved context. The draft is kept first: the window's own
        // OnDisable may come after this and find no module.
        if (module != null) draftJson = module.SaveWindowDraft();
        module = null;
    }

    private void CreateGUI()
    {
        RecoverContext();
    }

    private void OnEnable() { EditorApplication.delayCall += RecoverContext; }

    private void RecoverContext()
    {
        if (avatar == null || string.IsNullOrEmpty(assetJson)) return;
        if (module == null)
        {
            var session = McbSession.For(avatar);
            session.serializedObject.Update();
            session.isCreatorModeProp.boolValue = true;
            session.serializedObject.ApplyModifiedProperties();
            module = new CreatorModeModule(session, JsonConvert.DeserializeObject<AvatarDiscoveredAsset>(assetJson));
            module.LoadWindowDraft(draftJson);
        }
        BuildUI();
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
        // An upload still in flight keeps running: it is the session's, not this window's.
        var previous = module;
        module = null;
        previous?.DetachUIToolkit();
    }
}
#endif
