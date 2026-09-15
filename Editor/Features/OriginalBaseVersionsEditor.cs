#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

/// <summary>Named original releases with independent imports, explicit mappings and inline validation.</summary>
public sealed class OriginalBaseVersionsEditor : VisualElement
{
    public sealed class Draft
    {
        public string label = "";
        public readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string name, string hash)> candidates = new List<(string, string)>();
        [Newtonsoft.Json.JsonIgnore] public string error;
        [Newtonsoft.Json.JsonIgnore] public bool reading;
    }
    readonly IList<Draft> drafts;
    readonly Func<string[]> slots;
    readonly Action changed;
    public OriginalBaseVersionsEditor(IList<Draft> drafts, Func<string[]> slots, Action changed)
    {
        this.drafts = drafts; this.slots = slots; this.changed = changed;
        AddToClassList("mcb-source-editor"); LoadStyles(this); Draw();
    }
    public static void LoadStyles(VisualElement root)
    {
        foreach (string name in new[] { "mcb-theme", "mcb-original-versions" })
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.mcb/Editor/Styles/" + name + ".uss");
            if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
        }
    }
    void Draw()
    {
        Clear(); int index = 0;
        foreach (var draft in drafts.ToArray())
        {
            index++;
            var card = Element("mcb-source-card", this);
            var heading = Element("mcb-source-heading", card);
            Text("ORIGINAL VERSION " + index, "mcb-support-eyebrow", heading);
            var badge = Text("", "mcb-source-status", heading);
            var remove = ActionButton("×", () => { drafts.Remove(draft); Draw(); changed?.Invoke(); });
            remove.AddToClassList("mcb-source-remove"); remove.tooltip = "Remove this original version"; heading.Add(remove);
            var label = new TextField("Name / version") { value = draft.label, maxLength = 128 };
            label.AddToClassList("mcb-source-name"); label.tooltip = "For example: 1.5 or Summer update"; card.Add(label);
            Action updateStatus = () => {
                int count = slots().Count(s => draft.hashes.ContainsKey(s));
                bool ready = count == slots().Length && count > 0 && !string.IsNullOrWhiteSpace(draft.label) && !draft.reading;
                badge.text = draft.reading ? "Reading…" : ready ? "✓  Ready" : count == 0 ? "Needs files" : count < slots().Length ? count + " / " + slots().Length + " mapped" : "Add a name";
                badge.EnableInClassList("is-ready", ready);
            };
            label.RegisterValueChangedCallback(evt => { draft.label = evt.newValue; updateStatus(); changed?.Invoke(); });
            var drop = Element("mcb-source-drop", card);
            Text(draft.candidates.Count == 0 ? "Drop original files here" : "Add or replace original files", "mcb-support-strong", drop);
            Text(".unitypackage or .fbx  ·  Original, unmodified files", "mcb-support-muted", drop);
            var imports = Element("mcb-source-imports", drop);
            imports.Add(ActionButton("Choose package", () => Browse(draft, true)));
            imports.Add(ActionButton("Choose FBX", () => Browse(draft, false)));
            drop.RegisterCallback<DragUpdatedEvent>(evt => {
                if (draft.reading) return;
                bool valid = CanImport(DragAndDrop.paths);
                DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                drop.EnableInClassList("is-dragging", valid); evt.StopPropagation();
            });
            drop.RegisterCallback<DragLeaveEvent>(evt => drop.RemoveFromClassList("is-dragging"));
            drop.RegisterCallback<DragPerformEvent>(evt => {
                if (draft.reading || !CanImport(DragAndDrop.paths)) return;
                var paths = DragAndDrop.paths.ToArray(); DragAndDrop.AcceptDrag();
                drop.RemoveFromClassList("is-dragging"); QueueImport(draft, paths); evt.StopPropagation();
            });
            if (draft.candidates.Count > 0)
            {
                var mapping = Element("mcb-source-mappings", card);
                Text("ORIGINAL FILE MAPPINGS", "mcb-support-eyebrow", mapping);
                foreach (string slot in slots())
                {
                    var row = Element("mcb-source-mapping", mapping);
                    var name = Text(Path.GetFileName(slot), "mcb-source-slot", row); name.tooltip = slot;
                    var choices = new List<string> { "Select an original FBX…" };
                    choices.AddRange(draft.candidates.Select(c => Path.GetFileName(c.name) + " · " + c.hash.Substring(0, 8)));
                    int selected = draft.hashes.TryGetValue(slot, out string hash) ? draft.candidates.FindIndex(c => c.hash == hash) + 1 : 0;
                    var field = new PopupField<string>(choices, Math.Max(0, selected)); field.AddToClassList("mcb-source-file"); row.Add(field);
                    field.RegisterValueChangedCallback(evt => {
                        int choice = choices.IndexOf(evt.newValue) - 1;
                        if (choice >= 0) draft.hashes[slot] = draft.candidates[choice].hash; else draft.hashes.Remove(slot);
                        updateStatus(); changed?.Invoke();
                    });
                }
            }
            if (!string.IsNullOrEmpty(draft.error)) Text(draft.error, "mcb-source-error", card);
            updateStatus(); card.SetEnabled(!draft.reading);
        }
        var add = ActionButton("+  Add original base version", () => { drafts.Add(new Draft()); Draw(); changed?.Invoke(); });
        add.AddToClassList("mcb-source-add"); Add(add);
    }
    static bool CanImport(IEnumerable<string> paths) => paths != null && paths.Any() && paths.All(path =>
        File.Exists(path) && (path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)));
    void Browse(Draft draft, bool package)
    {
        string path = EditorUtility.OpenFilePanel(package ? "Original base package" : "Original base FBX", "", package ? "unitypackage" : "fbx");
        if (!string.IsNullOrEmpty(path)) QueueImport(draft, new[] { path });
    }
    void QueueImport(Draft draft, string[] paths)
    {
        if (draft.reading) return;
        draft.reading = true; draft.error = null; Draw(); changed?.Invoke();
        schedule.Execute(() => {
            try
            {
                foreach (var path in paths)
                {
                    if (path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var extraction = UnityPackageFbxSourceExtractor.ExtractFbxEntries(path))
                            foreach (var entry in extraction.entries) AddCandidate(draft, entry.publishedSourcePath, entry.tempPath);
                    }
                    else AddCandidate(draft, Path.GetFileName(path), path);
                }
                if (string.IsNullOrWhiteSpace(draft.label)) draft.label = Path.GetFileNameWithoutExtension(paths[0]);
                foreach (string slot in slots())
                {
                    if (draft.hashes.ContainsKey(slot)) continue;
                    var matching = draft.candidates.Where(c => string.Equals(Path.GetFileName(c.name), Path.GetFileName(slot), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matching.Length == 1) draft.hashes[slot] = matching[0].hash;
                    else if (slots().Length == 1 && draft.candidates.Count == 1) draft.hashes[slot] = draft.candidates[0].hash;
                }
            }
            catch (Exception ex) { draft.error = ex.Message; }
            finally { draft.reading = false; Draw(); changed?.Invoke(); }
        }).ExecuteLater(30);
    }
    static void AddCandidate(Draft draft, string name, string path)
    {
        string hash = OriginalBaseLibrary.Cache(path);
        if (!draft.candidates.Any(c => c.hash == hash)) draft.candidates.Add((name, hash));
    }
    public static OriginalBaseVersionData[] Build(IList<Draft> drafts, string[] slots)
    {
        return drafts.Select(draft => {
            if (draft.reading) throw new InvalidOperationException("Wait for the original files to finish loading.");
            if (string.IsNullOrWhiteSpace(draft.label)) throw new InvalidOperationException("Give every original base a name or version number.");
            var files = slots.Select(slot => {
                if (!draft.hashes.TryGetValue(slot, out string hash)) throw new InvalidOperationException("Choose the original FBX for " + draft.label + ": " + Path.GetFileName(slot));
                return new ModelFileData { path = slot, hash = hash, type = "FBX", role = "SOURCE" };
            }).ToArray();
            return new OriginalBaseVersionData { key = OriginalBaseLibrary.Key(files), label = draft.label.Trim(), sourceFiles = files };
        }).ToArray();
    }
    public static Button ActionButton(string text, Action action)
    {
        var button = new Button { text = text }; button.AddToClassList("mcb-button"); bool queued = false;
        Action activate = () => {
            if (queued || !button.enabledInHierarchy) return;
            queued = true; button.AddToClassList("is-pressed");
            button.schedule.Execute(() => { try { action(); } finally { queued = false; button.RemoveFromClassList("is-pressed"); } });
        };
        button.clicked += activate;
        button.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { activate(); evt.StopImmediatePropagation(); evt.PreventDefault(); } }, TrickleDown.TrickleDown);
        return button;
    }
    internal static VisualElement Element(string classes, VisualElement parent)
    {
        var element = new VisualElement(); foreach (var cls in classes.Split(' ')) element.AddToClassList(cls); parent.Add(element); return element;
    }
    internal static Label Text(string text, string classes, VisualElement parent)
    {
        var label = new Label(text); foreach (var cls in classes.Split(' ')) label.AddToClassList(cls); parent.Add(label); return label;
    }
}
#endif
