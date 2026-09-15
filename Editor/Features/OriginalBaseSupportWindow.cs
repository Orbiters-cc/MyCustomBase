#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static OriginalBaseVersionsEditor;

/// <summary>A guided creator workflow; registration and historical rekeying stay in shared services.</summary>
public sealed class OriginalBaseSupportWindow : EditorWindow
{
    [SerializeField] MyCustomBase avatar;
    [SerializeField] string assetJson;
    [SerializeField] string stateJson;
    MCBEditor editor;
    bool ownsEditor;
    AvatarDiscoveredAsset asset;
    OriginalBaseVersionData[] originals;
    CustomBaseVersion[] versions = Array.Empty<CustomBaseVersion>();
    readonly List<OriginalBaseVersionsEditor.Draft> drafts = new List<OriginalBaseVersionsEditor.Draft>();
    readonly HashSet<string> selected = new HashSet<string>();
    readonly HashSet<string> completed = new HashSet<string>();
    readonly HashSet<string> existingTargets = new HashSet<string>();
    readonly Dictionary<string, Toggle> versionRows = new Dictionary<string, Toggle>();
    [NonSerialized] bool busy;
    bool loaded, succeeded;
    [NonSerialized] bool contextRecovered;
    int step, totalWork, originalsAdded;
    string error, progress, completionTargets, search = "";
    Label footerSummary, selectionCount, progressLabel;
    Button primary, allVersionsButton, futureVersionsButton, versionsStep;
    ProgressBar progressBar;
    VisualElement versionList, errorNotice;

    sealed class State
    {
        public OriginalBaseVersionData[] originals;
        public CustomBaseVersion[] versions;
        public List<OriginalBaseVersionsEditor.Draft> drafts;
        public string[] selected, completed, existingTargets;
        public string completionTargets;
        public int step;
        public bool loaded;
    }
    string SessionKey => "MCB.SourceSupport." + (avatar != null ? avatar.GetInstanceID() : 0) + "." + (asset?.id ?? 0);
    string[] Slots => (asset?.sourceFiles?.Length > 0 ? asset.sourceFiles : originals?.FirstOrDefault()?.sourceFiles ?? Array.Empty<ModelFileData>())
        .Select(f => f.path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    List<OriginalBaseVersionsEditor.Draft> ActiveDrafts => drafts.Where(d => d.reading || !string.IsNullOrWhiteSpace(d.label) || d.candidates.Count > 0).ToList();

    public static void Open(MCBEditor editor, AvatarDiscoveredAsset asset)
    {
        var window = Resources.FindObjectsOfTypeAll<OriginalBaseSupportWindow>().FirstOrDefault(w => w.avatar == editor.customBaseTarget && w.asset?.id == asset.id)
            ?? CreateInstance<OriginalBaseSupportWindow>();
        window.editor = editor; window.avatar = editor.customBaseTarget; window.asset = asset;
        window.assetJson = JsonConvert.SerializeObject(asset);
        window.titleContent = new GUIContent("Support new version");
        window.minSize = new Vector2(520, 580);
        if (window.position.width < 600) window.position = new Rect(window.position.position, new Vector2(620, 760));
        if (!window.loaded) window.RestoreState(SessionState.GetString(window.SessionKey, ""));
        window.contextRecovered = true;
        window.Show(); window.Load();
    }

    void OnEnable() { EditorApplication.delayCall += RecoverContext; }
    void RecoverContext()
    {
        if (this == null || string.IsNullOrEmpty(assetJson)) return;
        if (asset == null) asset = JsonConvert.DeserializeObject<AvatarDiscoveredAsset>(assetJson);
        if (editor == null && avatar != null)
        {
            editor = Resources.FindObjectsOfTypeAll<MCBEditor>().FirstOrDefault(e => e.customBaseTarget == avatar);
            ownsEditor = false;
            if (editor == null) { editor = (MCBEditor)UnityEditor.Editor.CreateEditor(avatar, typeof(MCBEditor)); ownsEditor = true; }
        }
        // Unity hot reload can restore primitive fields while losing non-serializable DTOs and lists.
        // Restore the complete snapshot together, regardless of the old loaded flag.
        if (!contextRecovered) { RestoreState(stateJson); contextRecovered = true; }
        CreateGUI();
        if (!loaded && !busy && editor != null) Load();
    }
    void OnDisable()
    {
        EditorApplication.delayCall -= RecoverContext;
        SaveState();
        if (ownsEditor && editor != null) { DestroyImmediate(editor); editor = null; ownsEditor = false; }
    }
    void SaveState()
    {
        if (asset == null) return;
        stateJson = JsonConvert.SerializeObject(new State { originals = originals, versions = versions, drafts = drafts,
            selected = selected.ToArray(), completed = completed.ToArray(), existingTargets = existingTargets.ToArray(),
            completionTargets = completionTargets, step = succeeded ? 0 : step, loaded = loaded });
        SessionState.SetString(SessionKey, stateJson);
    }
    void RestoreState(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        var state = JsonConvert.DeserializeObject<State>(json);
        if (state == null) return;
        originals = state.originals; versions = state.versions ?? Array.Empty<CustomBaseVersion>();
        drafts.Clear(); drafts.AddRange(state.drafts ?? new List<OriginalBaseVersionsEditor.Draft>());
        selected.Clear(); selected.UnionWith(state.selected ?? Array.Empty<string>());
        completed.Clear(); completed.UnionWith(state.completed ?? Array.Empty<string>());
        existingTargets.Clear(); existingTargets.UnionWith(state.existingTargets ?? Array.Empty<string>());
        completionTargets = state.completionTargets; step = state.step; loaded = state.loaded;
    }
    async void Load()
    {
        if (busy || editor == null || asset == null) return;
        busy = true; error = null; progress = "Loading original bases and custom versions…"; CreateGUI();
        try
        {
            originals = await OriginalBaseSupportService.Load(asset.id, editor.authToken);
            var response = await new NetworkService().FetchVersionsAsync(OriginalBaseSupportService.Url(asset.id, "/versions", editor.authToken) + "&allSourceVersions=1");
            if (!response.success) throw new InvalidOperationException(response.error);
            versions = response.response.versions.OrderByDescending(v => Version.TryParse(v.version, out var number) ? number : new Version()).ToArray();
            if (!loaded) selected.UnionWith(versions.Select(v => v.version));
            selected.IntersectWith(versions.Select(v => v.version));
            asset.sourceVersions = originals; loaded = true;
            if (drafts.Count == 0 && existingTargets.Count == 0) drafts.Add(new OriginalBaseVersionsEditor.Draft());
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; if (this != null) { SaveState(); CreateGUI(); } }
    }

    public void CreateGUI()
    {
        primary = null; footerSummary = null; selectionCount = null; progressLabel = null; progressBar = null;
        allVersionsButton = null; futureVersionsButton = null; versionsStep = null; errorNotice = null;
        var root = rootVisualElement; root.Clear(); root.AddToClassList("mcb-support-window");
        OriginalBaseVersionsEditor.LoadStyles(root);
        var header = Element("mcb-support-header", root);
        Text(asset?.name ?? "CUSTOM BASE", "mcb-support-eyebrow", header);
        Text(step == 1 && loaded && !succeeded ? "Choose custom versions" : "Support more base versions", "mcb-support-title", header);
        Text(step == 1 && loaded && !succeeded ? "Your saved geometry and settings stay the same." : "Add original releases, then choose which custom versions to update.", "mcb-support-muted", header);
        if (loaded && !succeeded)
        {
            var steps = Element("mcb-support-steps", header);
            AddStep(steps, 0, "Original files"); AddStep(steps, 1, "Custom versions");
        }
        VisualElement body;
        if (loaded && step == 1 && !busy && !succeeded) body = Element("mcb-support-body mcb-support-version-body", root);
        else
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.AddToClassList("mcb-support-scroll"); root.Add(scroll);
            body = Element("mcb-support-body", scroll);
        }
        if (!string.IsNullOrEmpty(error))
        {
            var notice = Element("mcb-support-error", body);
            errorNotice = notice;
            Text("Support could not be completed", "mcb-support-strong", notice);
            Text(error, "mcb-support-wrap", notice);
            if (completed.Count > 0) Text("Completed versions are saved. Retry continues the remaining ones.", "mcb-support-muted", notice);
        }
        if (succeeded) BuildSuccess(body);
        else if (!loaded) Text(busy ? progress : "Connect to MCB to load this asset's original bases and saved versions.", "mcb-support-muted", body);
        else if (busy) BuildProgress(body);
        else if (step == 0) BuildOriginals(body);
        else BuildVersions(body);
        BuildFooter(root);
    }
    void AddStep(VisualElement parent, int index, string title)
    {
        var button = OriginalBaseVersionsEditor.ActionButton((index + 1) + "  " + title, () => { step = index; error = null; SaveState(); CreateGUI(); });
        button.AddToClassList("mcb-support-step"); button.EnableInClassList("is-active", step == index);
        if (index == 1) versionsStep = button;
        button.SetEnabled(!busy && (index == 0 || TryTargets(out _, out _))); parent.Add(button);
    }
    void BuildOriginals(VisualElement body)
    {
        Text("Add an original release", "mcb-support-section-title", body);
        Text("Each release can use its own Unity package or FBX files.", "mcb-support-muted", body);
        body.Add(new OriginalBaseVersionsEditor(drafts, () => Slots, Changed));
        if (originals?.Length > 0)
        {
            var registered = new Foldout { text = "Use registered originals  ·  " + originals.Length, value = existingTargets.Count > 0 };
            registered.AddToClassList("mcb-support-registered"); body.Add(registered);
            Text("Select an existing original to add its support to more custom versions.", "mcb-support-muted", registered);
            foreach (var original in originals)
            {
                var row = ChoiceRow(original.label, Count(original.sourceFiles.Length, "original FBX file"), "REGISTERED", existingTargets.Contains(original.key));
                row.RegisterValueChangedCallback(evt => { if (evt.newValue) existingTargets.Add(original.key); else existingTargets.Remove(original.key); Changed(); });
                registered.Add(row);
            }
        }
    }
    void BuildVersions(VisualElement body)
    {
        TryTargets(out var targets, out _);
        var summary = Element("mcb-support-target-summary", body);
        Text("FOR", "mcb-support-eyebrow", summary);
        var targetNames = Text(targets.Length <= 2 ? string.Join("  ·  ", targets.Select(v => v.label)) : Count(targets.Length, "original version"), "mcb-support-strong mcb-support-wrap", summary);
        targetNames.tooltip = string.Join("\n", targets.Select(v => v.label));
        var choices = Element("mcb-support-bulk", body);
        var all = OriginalBaseVersionsEditor.ActionButton("All versions", () => SelectVersions(true));
        allVersionsButton = all;
        all.EnableInClassList("is-active", selected.Count == versions.Length && versions.Length > 0); choices.Add(all);
        var future = OriginalBaseVersionsEditor.ActionButton("Future versions only", () => SelectVersions(false));
        futureVersionsButton = future;
        future.EnableInClassList("is-active", selected.Count == 0); choices.Add(future);
        var toolbar = Element("mcb-support-list-toolbar", body);
        var searchBox = new ToolbarSearchField { value = search }; searchBox.AddToClassList("mcb-support-search");
        searchBox.tooltip = "Search version numbers or titles"; toolbar.Add(searchBox);
        searchBox.RegisterValueChangedCallback(evt => { search = evt.newValue; DrawVersionRows(); });
        selectionCount = Text("", "mcb-support-muted", toolbar);
        var releases = new ScrollView(ScrollViewMode.Vertical); releases.AddToClassList("mcb-support-release-scroll"); body.Add(releases);
        versionList = Element("mcb-support-version-list", releases);
        DrawVersionRows();
    }
    void SelectVersions(bool all)
    {
        selected.Clear(); if (all) selected.UnionWith(versions.Select(v => v.version));
        SaveState(); CreateGUI();
    }
    void DrawVersionRows()
    {
        versionList.Clear(); versionRows.Clear();
        var matching = versions.Where(v => string.IsNullOrWhiteSpace(search) || (v.version + " " + v.title).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        foreach (var version in matching)
        {
            var row = ChoiceRow(string.IsNullOrWhiteSpace(version.title) ? "Untitled release" : version.title, null, version.version, selected.Contains(version.version));
            row.name = "mcb-support-version-" + version.version;
            row.tooltip = version.version + (string.IsNullOrWhiteSpace(version.title) ? "" : " — " + version.title);
            row.RegisterValueChangedCallback(evt => { if (evt.newValue) selected.Add(version.version); else selected.Remove(version.version); Changed(); });
            versionList.Add(row); versionRows[version.version] = row;
        }
        if (matching.Length == 0) Text(versions.Length == 0 ? "No saved releases yet. These originals will be available for your next build." : "No versions match your search.", "mcb-support-empty", versionList);
        UpdateFooter();
    }
    void BuildProgress(VisualElement body)
    {
        Text("Adding original base support", "mcb-support-section-title", body);
        Text("Each saved release keeps its geometry and settings.", "mcb-support-muted", body);
        progressLabel = Text(progress, "mcb-support-progress-label", body);
        progressBar = new ProgressBar { lowValue = 0, highValue = Math.Max(1, totalWork), value = completed.Count, title = completed.Count + " of " + totalWork + " versions completed" };
        progressBar.AddToClassList("mcb-support-progress"); body.Add(progressBar);
    }
    void BuildSuccess(VisualElement body)
    {
        body.AddToClassList("mcb-support-success");
        Text("✓", "mcb-support-success-icon", body);
        Text("Support is ready", "mcb-support-title", body);
        Text(Count(originalsAdded, "original version") + " supported\n" + Count(completed.Count, "saved custom version") + " processed", "mcb-support-muted mcb-support-wrap", body);
        Text("Matching users will receive the right custom base automatically.", "mcb-support-muted", body);
    }
    void BuildFooter(VisualElement root)
    {
        var footer = Element("mcb-support-footer", root);
        footerSummary = Text("", "mcb-support-muted", footer);
        var actions = Element("mcb-support-footer-actions", footer);
        var secondary = OriginalBaseVersionsEditor.ActionButton(succeeded ? "Support another original" : step == 1 ? "Back" : "Cancel", () => {
            if (succeeded) { succeeded = false; step = 0; completed.Clear(); existingTargets.Clear(); drafts.Clear(); drafts.Add(new OriginalBaseVersionsEditor.Draft()); Load(); }
            else if (step == 1) { step = 0; CreateGUI(); } else Close();
        });
        secondary.SetEnabled(!busy); actions.Add(secondary);
        primary = OriginalBaseVersionsEditor.ActionButton("Continue", () => {
            if (succeeded) Close();
            else if (!loaded) Load();
            else if (step == 0) { step = 1; error = null; SaveState(); CreateGUI(); }
            else Submit();
        });
        primary.name = "mcb-support-primary";
        primary.AddToClassList("mcb-support-primary"); actions.Add(primary); UpdateFooter();
    }
    void Changed() { error = null; if (errorNotice != null) errorNotice.style.display = DisplayStyle.None; SaveState(); UpdateFooter(); }
    static string Count(int value, string noun) => value + " " + noun + (value == 1 ? "" : "s");
    bool TryTargets(out OriginalBaseVersionData[] targets, out string problem)
    {
        targets = Array.Empty<OriginalBaseVersionData>(); problem = null;
        try
        {
            if (Slots.Length == 0) throw new InvalidOperationException("No original FBX mappings were found for this asset.");
            var added = OriginalBaseVersionsEditor.Build(ActiveDrafts, Slots);
            targets = added.Concat((originals ?? Array.Empty<OriginalBaseVersionData>()).Where(v => existingTargets.Contains(v.key))).GroupBy(v => v.key).Select(g => g.First()).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("Add original files or choose a registered original to continue.");
            if (added.Select(v => v.key).Distinct().Count() != added.Length) throw new InvalidOperationException("Two releases contain the same original files. Keep one of them.");
            return true;
        }
        catch (Exception ex) { problem = ex.Message; return false; }
    }
    void UpdateFooter()
    {
        if (primary == null || footerSummary == null) return;
        if (selectionCount != null) selectionCount.text = selected.Count + " / " + versions.Length + " selected";
        allVersionsButton?.EnableInClassList("is-active", selected.Count == versions.Length && versions.Length > 0);
        futureVersionsButton?.EnableInClassList("is-active", selected.Count == 0);
        if (succeeded) { primary.text = "Done"; primary.SetEnabled(true); footerSummary.text = "Original base support saved."; return; }
        if (busy) { primary.text = "Working…"; primary.SetEnabled(false); footerSummary.text = progress; return; }
        if (!loaded) { primary.text = "Retry connection"; primary.SetEnabled(editor != null); footerSummary.text = "Your work stays in this window."; return; }
        bool valid = TryTargets(out var targets, out string problem);
        versionsStep?.SetEnabled(valid);
        primary.SetEnabled(valid);
        primary.text = step == 0 ? "Continue  →" : completed.Count > 0 && error != null ? "Retry remaining versions" : selected.Count == 0 ? "Register originals" : "Add support to " + selected.Count + " version" + (selected.Count == 1 ? "" : "s");
        footerSummary.text = !valid ? problem : step == 0 ? Count(targets.Length, "original version") + " ready" : selected.Count == 0 ? "New originals will be available for future builds only." : Count(targets.Length, "original version") + "  ·  " + Count(selected.Count, "custom release");
    }
    async void Submit()
    {
        if (busy || !TryTargets(out var plannedTargets, out _)) return;
        string plannedSignature = string.Join("|", plannedTargets.Select(v => v.key).OrderBy(key => key, StringComparer.Ordinal));
        if (plannedSignature != completionTargets) { completed.Clear(); completionTargets = plannedSignature; }
        string token = editor.authToken;
        busy = true; error = null; progress = "Verifying original files…"; totalWork = selected.Count; CreateGUI();
        try
        {
            var added = OriginalBaseVersionsEditor.Build(ActiveDrafts, Slots);
            if (added.Length > 0) originals = await OriginalBaseSupportService.Register(asset.id, token, added);
            asset.sourceVersions = originals;
            var keys = new HashSet<string>(added.Select(v => v.key).Concat(existingTargets));
            string signature = string.Join("|", keys.OrderBy(key => key, StringComparer.Ordinal));
            if (signature != completionTargets) { completed.Clear(); completionTargets = signature; }
            var targets = originals.Where(v => keys.Contains(v.key)).ToArray(); originalsAdded = targets.Length;
            foreach (var version in versions.Where(v => selected.Contains(v.version) && !completed.Contains(v.version)))
            {
                await OriginalBaseSupportService.AddHistoricalSupport(asset.id, token, version, targets, UpdateProgress);
                completed.Add(version.version); SaveState(); UpdateProgress("Saved support for " + version.version);
            }
            OriginalBaseLibrary.SaveSelection(asset.id, OriginalBaseLibrary.Selection(asset).Concat(keys).Distinct());
            succeeded = true; drafts.Clear(); existingTargets.Clear();
            if (editor != null) { editor.ReloadVersionsAndBanners(); editor.RefreshUiToolkitSections(); }
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; if (this != null) { SaveState(); CreateGUI(); } }
    }
    void UpdateProgress(string text)
    {
        progress = text;
        if (this == null) return;
        if (progressLabel != null) progressLabel.text = text;
        if (progressBar != null) { progressBar.value = completed.Count; progressBar.title = completed.Count + " of " + totalWork + " versions completed"; }
        if (footerSummary != null) footerSummary.text = text;
    }
    internal static Toggle ChoiceRow(string title, string subtitle, string badge, bool value)
    {
        var row = new Toggle { value = value }; row.AddToClassList("mcb-support-choice"); row.EnableInClassList("is-selected", value);
        if (!string.IsNullOrEmpty(badge)) Text(badge, "mcb-support-badge", row);
        var content = Element("mcb-support-choice-content", row);
        Text(title, "mcb-support-choice-title", content);
        if (!string.IsNullOrEmpty(subtitle)) Text(subtitle, "mcb-support-muted", content);
        row.RegisterValueChangedCallback(evt => row.EnableInClassList("is-selected", evt.newValue));
        row.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0 && row.enabledInHierarchy) { row.Focus(); row.value = !row.value; evt.StopImmediatePropagation(); evt.PreventDefault(); } }, TrickleDown.TrickleDown);
        return row;
    }

}
#endif
