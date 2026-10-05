using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Picks the bones whose twist spreads along their mesh. Shows the version's custom model as a ghost with its skeleton;
/// clicking a bone (or its row) selects it, with its mirrored bone when Symmetry is on. Nothing changes until Confirm.
/// </summary>
internal sealed class TwistBoneWindow : EditorWindow
{
    [SerializeField] private MyCustomBase owner;
    [SerializeField] private VersionCustomization draft;
    [SerializeField] private bool symmetry = true;
    [SerializeField] private bool deformingOnly = true;
    [SerializeField] private int sourceIndex;
    private Action changed;
    private GameObject source;
    private TwistBoneStage stage;
    private ScrollView boneList;
    private ScrollView entries;
    private ToolbarSearchField search;
    private Label subtitle;
    private Label status;
    private string expanded;
    private readonly Dictionary<string, Transform> bones = new Dictionary<string, Transform>();
    private readonly HashSet<string> deforming = new HashSet<string>();

    public static void Open(MyCustomBase owner, Action changed)
    {
        var window = Resources.FindObjectsOfTypeAll<TwistBoneWindow>().FirstOrDefault(w => w.owner == owner) ?? CreateInstance<TwistBoneWindow>();
        window.owner = owner; window.changed = changed;
        window.draft = owner.creatorCustomization.Clone();
        window.titleContent = new GUIContent("Twisting bones");
        window.minSize = new Vector2(960, 600);
        window.Show();
        window.Build();
    }

    private void OnDisable() => stage?.Dispose();

    private void CreateGUI()
    {
        if (owner == null || draft == null)
        {
            rootVisualElement.Add(new HelpBox("The authoring avatar is no longer available. Reopen this window from Create MCB Version.", HelpBoxMessageType.Info));
            return;
        }
        Build();
    }

    private List<GameObject> Sources()
    {
        var sources = owner.modelFileBuildEntries.Where(e => e.customFbx != null).Select(e => e.customFbx)
            .Concat(new[] { owner.customFbxForCreator }).Where(g => g != null).Distinct().ToList();
        if (sources.Count == 0) sources.Add(AvatarPaths.Root(owner).gameObject);
        return sources;
    }

    private void Build()
    {
        var root = rootVisualElement; root.Clear();
        foreach (var path in new[] { "Packages/orbiters.mcb/Editor/Styles/mcb-version-compare.uss", "Packages/orbiters.mcb/Editor/Styles/mcb-twist.uss" })
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (sheet != null && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
        }
        root.AddToClassList("mcb-cmp"); root.AddToClassList("mcb-twist");
        var sources = Sources();
        sourceIndex = Mathf.Clamp(sourceIndex, 0, sources.Count - 1);

        var header = new VisualElement(); header.AddToClassList("mcb-cmp__header"); root.Add(header);
        var titles = new VisualElement(); titles.AddToClassList("mcb-cmp__titles"); header.Add(titles);
        titles.Add(Text("Twisting bones", "mcb-cmp__title"));
        subtitle = Text(string.Empty, "mcb-cmp__subtitle"); titles.Add(subtitle);
        if (sources.Count > 1)
        {
            var model = new SearchableDropdownField(null, "Preview model", sources.Select((g, i) => new KeyValuePair<string, string>(i.ToString(), g.name)),
                sourceIndex.ToString(), key => { sourceIndex = int.Parse(key); SetSource(Sources()[sourceIndex]); });
            model.AddToClassList("mcb-twist__model");
            header.Add(model);
        }
        var mirror = new VisualElement(); mirror.AddToClassList("mcb-twist__switch");
        mirror.tooltip = "Selecting a bone also selects its mirrored bone, like Left elbow and Right elbow.";
        var mirrorIcon = new VectorIcon(IconGlyph.Mirror); mirrorIcon.AddToClassList("mcb-twist__switch-icon"); mirror.Add(mirrorIcon);
        mirror.Add(Text("Symmetry", "mcb-twist__switch-label"));
        mirror.Add(new ToggleSwitch(symmetry, value => symmetry = value));
        header.Add(mirror);

        var body = new VisualElement(); body.AddToClassList("mcb-cmp__body"); root.Add(body);
        stage = new TwistBoneStage(ToggleBone, IsSelected, AimOf);
        body.Add(stage);

        var side = new VisualElement(); side.AddToClassList("mcb-cmp__side"); side.AddToClassList("mcb-twist__side"); body.Add(side);
        var bonesCard = new VisualElement(); bonesCard.AddToClassList("mcb-cmp__card"); bonesCard.AddToClassList("mcb-twist__bones-card"); side.Add(bonesCard);
        var bonesHeader = new VisualElement(); bonesHeader.AddToClassList("mcb-twist__card-header"); bonesCard.Add(bonesHeader);
        bonesHeader.Add(Text("Bones", "mcb-cmp__card-title", "mcb-twist__grow"));
        var deformToggle = new VisualElement(); deformToggle.AddToClassList("mcb-twist__mini-switch");
        deformToggle.tooltip = "Only list bones that deform a mesh";
        deformToggle.Add(Text("Weighted only", "mcb-cmp__caption"));
        deformToggle.Add(new ToggleSwitch(deformingOnly, value => { deformingOnly = value; RefreshBones(); }));
        bonesHeader.Add(deformToggle);
        search = new ToolbarSearchField { tooltip = "Find a bone" };
        search.AddToClassList("mcb-twist__search");
        search.RegisterValueChangedCallback(_ => RefreshBones());
        bonesCard.Add(search);
        boneList = new ScrollView(ScrollViewMode.Vertical); boneList.AddToClassList("mcb-twist__bones");
        bonesCard.Add(boneList);

        var selectedCard = new VisualElement(); selectedCard.AddToClassList("mcb-cmp__card"); selectedCard.AddToClassList("mcb-twist__selected-card"); side.Add(selectedCard);
        selectedCard.Add(Text("Selected", "mcb-cmp__card-title"));
        entries = new ScrollView(ScrollViewMode.Vertical); entries.AddToClassList("mcb-twist__entries");
        selectedCard.Add(entries);

        var footer = new VisualElement(); footer.AddToClassList("mcb-twist__footer"); root.Add(footer);
        status = Text(string.Empty, "mcb-twist__status"); footer.Add(status);
        footer.Add(Pill("Cancel", Close, "mcb-twist__cancel"));
        footer.Add(Pill("Confirm", Confirm, "mcb-cmp__apply", "mcb-twist__confirm"));
        SetSource(sources[sourceIndex]);
    }

    private void Confirm()
    {
        try
        {
            draft.Validate();
            foreach (var target in draft.twistBones) TwistBoneService.Resolve(source.transform, target);
            Undo.RecordObject(owner, "Set twisting bones");
            owner.creatorCustomization.twistBones = draft.Clone().twistBones;
            EditorUtility.SetDirty(owner);
            if (changed != null) changed();
            else foreach (var editor in Resources.FindObjectsOfTypeAll<MCBEditor>().Where(e => e.customBaseTarget == owner)) editor.creatorModule.RefreshUIToolkit();
            Close();
        }
        catch (Exception ex)
        {
            status.text = ex.Message;
            status.AddToClassList("mcb-twist__status--error");
        }
    }

    private void SetSource(GameObject value)
    {
        source = value; bones.Clear(); deforming.Clear();
        var skins = source.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
        var skinBones = new HashSet<Transform>(skins.SelectMany(r => r.bones).Where(b => b != null));
        foreach (var skin in skins)
        {
            // A bone deforms when at least one vertex carries weight on it.
            var weights = skin.sharedMesh.GetAllBoneWeights();
            var used = new HashSet<int>();
            foreach (var weight in weights) if (weight.weight > 0) used.Add(weight.boneIndex);
            var array = skin.bones;
            foreach (int index in used) if (index < array.Length && array[index] != null) deforming.Add(AnimationUtility.CalculateTransformPath(array[index], source.transform));
        }
        // Every transform of the skin hierarchy can be an aim or up reference, weighted or not.
        foreach (var bone in source.GetComponentsInChildren<Transform>(true).Where(t => t != source.transform && (skinBones.Contains(t) || skinBones.Any(t.IsChildOf))))
            bones[AnimationUtility.CalculateTransformPath(bone, source.transform)] = bone;
        stage.SetSource(source, bones);
        Refresh();
    }

    private bool IsSelected(string path) => draft.twistBones.Any(t => t.bone == path);
    private string AimOf(string path) => draft.twistBones.FirstOrDefault(t => t.bone == path)?.aim;

    private void ToggleBone(string path)
    {
        bool select = !IsSelected(path);
        SetSelected(path, select);
        string mirrored = Mirror(path);
        if (symmetry && mirrored != null) SetSelected(mirrored, select);
        if (select) expanded = path;
        Refresh();
    }

    private string Mirror(string path)
    {
        string mirrored = string.Join("/", path.Split('/').Select(BoneNames.Mirror));
        return mirrored != path && bones.ContainsKey(mirrored) ? mirrored : null;
    }

    private void SetSelected(string path, bool selected)
    {
        draft.twistBones.RemoveAll(t => t.bone == path);
        if (!selected) return;
        var bone = bones[path];
        // A bone with one child aims at it; any other bone needs an explicit aim.
        draft.twistBones.Add(new TwistBoneConfiguration { bone = path,
            aim = bone.childCount == 1 ? AnimationUtility.CalculateTransformPath(bone.GetChild(0), source.transform) : "",
            up = bone.parent != source.transform ? AnimationUtility.CalculateTransformPath(bone.parent, source.transform) : "" });
    }

    private void Refresh()
    {
        int count = draft.twistBones.Count;
        subtitle.text = count == 0 ? "Click bones on the model or in the list. Their twist then spreads along the mesh, clothing included."
            : $"{count} selected · the built avatar gains {count} bone(s) and {count} VRC constraint(s)";
        int missing = draft.twistBones.Count(t => string.IsNullOrEmpty(t.aim) || string.IsNullOrEmpty(t.up));
        status.RemoveFromClassList("mcb-twist__status--error");
        status.text = missing > 0 ? $"{missing} bone(s) need an aim or up bone before you confirm."
            : "Changes apply when you confirm. Twists are generated on the build copy only.";
        status.EnableInClassList("mcb-twist__status--warning", missing > 0);
        RefreshBones();
        RefreshEntries();
        stage.RefreshSelection();
    }

    private void RefreshBones()
    {
        boneList.Clear();
        string term = (search.value ?? "").Trim();
        int shown = 0;
        foreach (var pair in bones)
        {
            if (term.Length > 0 ? pair.Value.name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 : deformingOnly && !deforming.Contains(pair.Key)) continue;
            string path = pair.Key;
            bool selected = IsSelected(path);
            var row = new VisualElement(); row.AddToClassList("mcb-twist__bone");
            row.EnableInClassList("mcb-twist__bone--selected", selected);
            row.tooltip = path;
            if (term.Length == 0) row.style.paddingLeft = 8 + Mathf.Min(path.Count(c => c == '/'), 12) * 8;
            var dot = new VisualElement(); dot.AddToClassList("mcb-twist__bone-dot"); row.Add(dot);
            row.Add(Text(pair.Value.name, "mcb-twist__bone-name"));
            if (!deforming.Contains(path)) row.Add(Text("helper", "mcb-twist__bone-tag"));
            row.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { ToggleBone(path); evt.StopPropagation(); } });
            row.RegisterCallback<PointerEnterEvent>(_ => stage.Highlight(path));
            row.RegisterCallback<PointerLeaveEvent>(_ => stage.Highlight(null));
            boneList.Add(row);
            shown++;
        }
        if (shown == 0) boneList.Add(Text(term.Length > 0 ? "No bone matches." : "No weighted bone on this model.", "mcb-cmp__caption"));
    }

    private void RefreshEntries()
    {
        entries.Clear();
        if (draft.twistBones.Count == 0)
        {
            entries.Add(Text("Nothing selected yet. Forearms usually twist: try Left elbow with Symmetry on.", "mcb-cmp__caption"));
            return;
        }
        var choices = bones.Keys.Select(p => new KeyValuePair<string, string>(p, bones[p].name + "   " + p)).ToList();
        foreach (var item in draft.twistBones.ToList())
        {
            bool open = expanded == item.bone;
            var box = new VisualElement(); box.AddToClassList("mcb-twist__entry");
            box.EnableInClassList("mcb-twist__entry--open", open);
            var head = new VisualElement(); head.AddToClassList("mcb-twist__entry-head");
            var text = new VisualElement(); text.AddToClassList("mcb-twist__grow");
            text.Add(Text(item.bone.Split('/').Last(), "mcb-cmp__part-name"));
            bool incomplete = string.IsNullOrEmpty(item.aim) || string.IsNullOrEmpty(item.up);
            text.Add(Text(incomplete ? "Choose the bone it aims at" : "Aims at " + item.aim.Split('/').Last() + " · " + CurveName(item.curve),
                "mcb-cmp__part-detail", incomplete ? "mcb-twist__warning" : null));
            head.Add(text);
            var chevron = new VectorIcon(IconGlyph.Chevron); chevron.AddToClassList("mcb-twist__chevron"); head.Add(chevron);
            head.RegisterCallback<PointerDownEvent>(evt => { if (evt.button != 0) return; expanded = open ? null : item.bone; RefreshEntries(); evt.StopPropagation(); });
            head.RegisterCallback<PointerEnterEvent>(_ => stage.Highlight(item.bone));
            head.RegisterCallback<PointerLeaveEvent>(_ => stage.Highlight(null));
            var remove = new VisualElement { tooltip = "Remove" }; remove.AddToClassList("mcb-twist__remove");
            remove.Add(new VectorIcon(IconGlyph.Close));
            remove.RegisterCallback<PointerDownEvent>(evt => { if (evt.button != 0) return; evt.StopPropagation(); ToggleBone(item.bone); });
            head.Add(remove);
            box.Add(head);
            if (open)
            {
                var details = new VisualElement(); details.AddToClassList("mcb-twist__details");
                if (!bones.ContainsKey(item.bone)) details.Add(Text("This bone is not on the preview model.", "mcb-twist__warning"));
                Func<string, string> name = path => path.Split('/').Last();
                details.Add(new SearchableDropdownField("Aims at", "Aim bone", choices, NullIfEmpty(item.aim), value => { item.aim = value; Refresh(); }, "Choose a bone", name));
                details.Add(new SearchableDropdownField("Up reference", "Up bone", choices, NullIfEmpty(item.up), value => { item.up = value; Refresh(); }, "Choose a bone", name));
                var presets = new SegmentedControl(new[] { "Ultirex", "Linear", "Custom" }, index =>
                {
                    if (index == 0) item.curve = TwistCurve.Ultirex();
                    else if (index == 1) item.curve = TwistCurve.FromCurve(AnimationCurve.Linear(0, 0, 1, 1));
                    RefreshEntries();
                });
                presets.SetIndex(CurveName(item.curve) == "Ultirex curve" ? 0 : CurveName(item.curve) == "linear" ? 1 : 2);
                presets.AddToClassList("mcb-twist__presets");
                details.Add(Text("Weight along the bone", "mcb-twist__field-label"));
                details.Add(presets);
                var curve = new CurveField { value = item.curve.ToCurve(), ranges = new Rect(0, 0, 1, 1), tooltip = "Share of the bone's weight kept by the bone itself, from its start (0) to its aim (1)" };
                curve.AddToClassList("mcb-twist__curve");
                curve.RegisterValueChangedCallback(e => { item.curve = TwistCurve.FromCurve(e.newValue); presets.SetIndex(2); });
                details.Add(curve);
                var advanced = new Foldout { text = "Up direction", value = false };
                advanced.AddToClassList("mcb-twist__advanced");
                var direction = new Vector3Field { value = item.upDirection.ToVector(), tooltip = "Avatar-space direction that stays up for the twist bone (forward by default)" };
                direction.RegisterValueChangedCallback(e => item.upDirection = new TwistDirection { x = e.newValue.x, y = e.newValue.y, z = e.newValue.z });
                advanced.Add(direction);
                details.Add(advanced);
                box.Add(details);
            }
            entries.Add(box);
        }
    }

    private static string CurveName(TwistCurve curve)
    {
        string json(TwistCurve c) => JsonUtility.ToJson(c);
        if (json(curve) == json(TwistCurve.Ultirex())) return "Ultirex curve";
        if (json(curve) == json(TwistCurve.FromCurve(AnimationCurve.Linear(0, 0, 1, 1)))) return "linear";
        return "custom curve";
    }

    private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static Label Text(string text, params string[] classes)
    {
        var label = new Label(text);
        foreach (var name in classes) if (!string.IsNullOrEmpty(name)) label.AddToClassList(name);
        return label;
    }

    private static Button Pill(string text, Action action, params string[] classes)
    {
        var button = new Button { text = text }; button.AddToClassList("mcb-cmp__pill");
        foreach (var name in classes) button.AddToClassList(name);
        ButtonInteraction.RegisterImmediateClick(button, action);
        return button;
    }
}

/// <summary>Private preview scene; selection and camera never touch the user's scene or Selection.</summary>
internal sealed class TwistBoneStage : VisualElement, IDisposable
{
    private readonly Action<string> pick;
    private readonly Func<string, bool> selected;
    private readonly Func<string, string> aimOf;
    private readonly Image image;
    private readonly VisualElement overlay;
    private readonly Label hoverLabel;
    private PreviewRenderUtility preview;
    private Material material;
    private RenderTexture texture;
    private readonly List<(Mesh mesh, Matrix4x4 matrix)> meshes = new List<(Mesh, Matrix4x4)>();
    private Dictionary<string, Transform> bones = new Dictionary<string, Transform>();
    private readonly Dictionary<string, Vector2> screen = new Dictionary<string, Vector2>();
    private Transform root;
    private Vector3 pivot;
    private float yaw = 180, pitch = 6, distance = 3, targetYaw = 180, targetPitch = 6, targetDistance = 3;
    private bool dirty = true;
    private Vector2 down, last;
    private int pointer = -1;
    private string hovered, highlighted;

    public TwistBoneStage(Action<string> pick, Func<string, bool> selected, Func<string, string> aimOf)
    {
        this.pick = pick; this.selected = selected; this.aimOf = aimOf;
        AddToClassList("mcb-cmp-stage"); AddToClassList("mcb-twist__stage");
        focusable = true;
        image = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore }; image.StretchToParentSize(); Add(image);
        overlay = new VisualElement { pickingMode = PickingMode.Ignore }; overlay.StretchToParentSize(); Add(overlay);
        overlay.generateVisualContent += DrawBones;
        hoverLabel = new Label { pickingMode = PickingMode.Ignore }; hoverLabel.AddToClassList("mcb-twist__hover"); Add(hoverLabel);
        var hint = new Label("Click a bone to select it · Drag to turn · Scroll to zoom") { pickingMode = PickingMode.Ignore };
        hint.AddToClassList("mcb-cmp-stage__hint"); hint.AddToClassList("mcb-twist__hint"); Add(hint);
        RegisterCallback<PointerDownEvent>(e => { if (e.button != 0) return; pointer = e.pointerId; down = last = e.localPosition; this.CapturePointer(pointer); });
        RegisterCallback<PointerMoveEvent>(e =>
        {
            var p = (Vector2)e.localPosition;
            if (pointer == e.pointerId)
            {
                var delta = p - last;
                if (Vector2.Distance(down, p) > 4) AddToClassList("mcb-cmp-stage--dragging");
                targetYaw += delta.x * .45f; targetPitch = Mathf.Clamp(targetPitch + delta.y * .35f, -85, 85); last = p;
                return;
            }
            string near = Nearest(p);
            if (near != hovered) { hovered = near; UpdateHoverLabel(); overlay.MarkDirtyRepaint(); }
        });
        RegisterCallback<PointerUpEvent>(e =>
        {
            if (pointer != e.pointerId) return;
            RemoveFromClassList("mcb-cmp-stage--dragging");
            if (Vector2.Distance(down, (Vector2)e.localPosition) < 5)
            {
                string near = Nearest(down);
                if (near != null) { pick(near); overlay.MarkDirtyRepaint(); }
            }
            this.ReleasePointer(pointer); pointer = -1;
        });
        RegisterCallback<PointerLeaveEvent>(_ => { hovered = null; UpdateHoverLabel(); overlay.MarkDirtyRepaint(); });
        RegisterCallback<PointerCaptureOutEvent>(_ => { pointer = -1; RemoveFromClassList("mcb-cmp-stage--dragging"); });
        RegisterCallback<WheelEvent>(e => { targetDistance = Mathf.Clamp(targetDistance * Mathf.Exp(e.delta.y * .06f), .05f, 100); e.StopPropagation(); });
        RegisterCallback<GeometryChangedEvent>(_ => dirty = true);
        RegisterCallback<DetachFromPanelEvent>(_ => Dispose());
        schedule.Execute(Render).Every(16);
    }

    public void RefreshSelection() => overlay.MarkDirtyRepaint();

    public void Highlight(string path) { highlighted = path; UpdateHoverLabel(); overlay.MarkDirtyRepaint(); }

    private string Nearest(Vector2 point)
    {
        if (screen.Count == 0) return null;
        var closest = screen.OrderBy(p => Vector2.Distance(p.Value, point)).First();
        return Vector2.Distance(closest.Value, point) < 16 ? closest.Key : null;
    }

    private void UpdateHoverLabel()
    {
        string path = hovered ?? highlighted;
        if (path == null || !screen.TryGetValue(path, out var position) || !bones.TryGetValue(path, out var bone)) { hoverLabel.style.display = DisplayStyle.None; return; }
        hoverLabel.text = bone.name;
        hoverLabel.style.display = DisplayStyle.Flex;
        hoverLabel.style.left = position.x + 10;
        hoverLabel.style.top = position.y - 26;
    }

    public void SetSource(GameObject source, Dictionary<string, Transform> targets)
    {
        foreach (var part in meshes) UnityEngine.Object.DestroyImmediate(part.mesh); meshes.Clear();
        root = source.transform; bones = new Dictionary<string, Transform>(targets);
        var bounds = new Bounds(); bool first = true;
        foreach (var renderer in source.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null))
        {
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave }; renderer.BakeMesh(mesh);
            var matrix = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix; meshes.Add((mesh, matrix));
            foreach (var vertex in mesh.vertices) { var p = matrix.MultiplyPoint3x4(vertex); if (first) { bounds = new Bounds(p, Vector3.zero); first = false; } else bounds.Encapsulate(p); }
        }
        pivot = bounds.center; distance = targetDistance = Mathf.Max(.5f, bounds.size.magnitude * 1.5f); dirty = true;
    }

    private void Render()
    {
        if (panel == null || root == null || contentRect.width < 2 || contentRect.height < 2) return;
        bool moving = Mathf.Abs(yaw - targetYaw) + Mathf.Abs(pitch - targetPitch) + Mathf.Abs(distance - targetDistance) > .001f;
        if (!dirty && !moving) return;
        yaw = Mathf.Lerp(yaw, targetYaw, .25f); pitch = Mathf.Lerp(pitch, targetPitch, .25f); distance = Mathf.Lerp(distance, targetDistance, .25f);
        if (preview == null)
        {
            preview = new PreviewRenderUtility();
            material = new Material(Shader.Find("Hidden/MCB/VersionCompareGhost")) { hideFlags = HideFlags.HideAndDontSave };
            preview.camera.clearFlags = CameraClearFlags.SolidColor; preview.camera.backgroundColor = new Color(.071f, .075f, .086f);
        }
        int width = Mathf.CeilToInt(contentRect.width), height = Mathf.CeilToInt(contentRect.height);
        if (texture == null || texture.width != width || texture.height != height)
        {
            if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
            texture = new RenderTexture(width, height, 24) { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 4 }; texture.Create();
        }
        var camera = preview.camera; camera.targetTexture = texture; camera.aspect = (float)width / height; camera.fieldOfView = 28;
        camera.nearClipPlane = .001f; camera.farClipPlane = distance + 100;
        camera.transform.rotation = Quaternion.Euler(pitch, yaw, 0); camera.transform.position = pivot - camera.transform.forward * distance;
        foreach (var part in meshes) for (int i = 0; i < part.mesh.subMeshCount; i++) preview.DrawMesh(part.mesh, part.matrix, material, i);
        // PreviewRenderUtility overrides the scene lighting until restored; never leave it overridden.
        try { preview.Render(true, false); } finally { Unsupported.RestoreOverrideLightingSettings(); camera.targetTexture = null; }
        image.image = texture;
        screen.Clear();
        foreach (var pair in bones)
        {
            var point = camera.WorldToViewportPoint(root.InverseTransformPoint(pair.Value.position));
            if (point.z > 0) screen[pair.Key] = new Vector2(point.x * width, (1 - point.y) * height);
        }
        UpdateHoverLabel();
        overlay.MarkDirtyRepaint(); dirty = false;
    }

    private void DrawBones(MeshGenerationContext context)
    {
        var painter = context.painter2D;
        var idle = new Color(.70f, .76f, .84f, .45f);
        var accent = new Color(0, .855f, .427f);
        foreach (var pair in screen)
        {
            string parent = pair.Key.Contains("/") ? pair.Key.Substring(0, pair.Key.LastIndexOf('/')) : "";
            if (!screen.TryGetValue(parent, out var start)) continue;
            painter.strokeColor = idle; painter.lineWidth = 1;
            painter.BeginPath(); painter.MoveTo(start); painter.LineTo(pair.Value); painter.Stroke();
        }
        foreach (var pair in screen)
        {
            bool on = selected(pair.Key);
            if (on && aimOf(pair.Key) is string aim && screen.TryGetValue(aim, out var aimPoint))
            {
                painter.strokeColor = accent; painter.lineWidth = 3;
                painter.BeginPath(); painter.MoveTo(pair.Value); painter.LineTo(aimPoint); painter.Stroke();
            }
        }
        foreach (var pair in screen)
        {
            bool on = selected(pair.Key);
            bool hot = pair.Key == hovered || pair.Key == highlighted;
            painter.fillColor = on ? accent : hot ? Color.white : idle;
            painter.BeginPath(); painter.Arc(pair.Value, on ? 5 : hot ? 4.5f : 2.2f, 0, 360); painter.Fill();
            if (hot)
            {
                painter.strokeColor = on ? accent : Color.white; painter.lineWidth = 1.5f;
                painter.BeginPath(); painter.Arc(pair.Value, 9, 0, 360); painter.Stroke();
            }
        }
    }

    public void Dispose()
    {
        foreach (var part in meshes) UnityEngine.Object.DestroyImmediate(part.mesh); meshes.Clear();
        preview?.Cleanup(); preview = null;
        if (material != null) UnityEngine.Object.DestroyImmediate(material);
        if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); texture = null; }
    }
}
