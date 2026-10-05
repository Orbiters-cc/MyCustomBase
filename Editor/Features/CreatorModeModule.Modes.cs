using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Modes users switch in MCB (a genre, dog ears, a body modification): their categories and rules.
public partial class CreatorModeModule
{
    // Modes show folded to their summary; a mode opens when the creator expands or adds it.
    private readonly HashSet<string> expandedModes = new HashSet<string>();
    private string modePickerFor;
    private string modePickerMesh;
    private readonly List<string> modePickerSelection = new List<string>();

    private ModeConfiguration Modes => Customization.modes;

    private void BuildModesSection(VisualElement root)
    {
        var modes = Modes;
        var section = McbSectionUi.Section("Modes",
            modes.options.Count == 0
                ? "Let users switch parts of this version in MCB, such as a genre or dog ears. Their choice stays fixed in the built avatar."
                : $"{modes.options.Count} mode(s) in {modes.categories.Count(c => modes.options.Any(o => o.category == c.id))} categor{(modes.categories.Count == 1 ? "y" : "ies")}. Users switch them in MCB; the build keeps their choice.",
            out var body);
        var renderers = CustomizationRenderers().ToList();
        foreach (var category in modes.categories.ToArray()) body.Add(BuildModeCategory(category, renderers));

        var actions = McbSectionUi.Row("mcb-actions");
        if (modes.Category(ModeCategory.GenreId) == null)
            actions.Add(McbSectionUi.Pill("Add a genre", () => AddMode(ModeCategory.Genre(), "Male"), actions.childCount == 0 ? "first" : null));
        if (modes.Category(ModeCategory.BodyId) == null)
            actions.Add(McbSectionUi.Pill("Add a body modification", () => AddMode(ModeCategory.Body(), "New mode"), actions.childCount == 0 ? "first" : null));
        actions.Add(McbSectionUi.Pill("Add a category", () =>
        {
            string id = "category-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            ChangeCustomization(() => modes.categories.Add(new ModeCategory { id = id, label = "New category" }), true);
        }, actions.childCount == 0 ? "first" : null));
        body.Add(actions);
        root.Add(section);
    }

    private void AddMode(ModeCategory category, string label)
    {
        var modes = Modes;
        string id = "mode-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        ChangeCustomization(() =>
        {
            if (modes.Category(category.id) == null) modes.categories.Add(category);
            bool first = modes.options.All(o => o.category != category.id);
            modes.options.Add(new ModeOption { id = id, label = label, category = category.id, enabledByDefault = category.exclusive && first });
            expandedModes.Add(id);
        }, true);
    }

    private VisualElement BuildModeCategory(ModeCategory category, List<(string path, SkinnedMeshRenderer renderer)> renderers)
    {
        var modes = Modes;
        var members = modes.options.Where(o => o.category == category.id).ToList();
        var group = new VisualElement(); group.AddToClassList("mcb-mode-category");
        var header = new VisualElement(); header.AddToClassList("mcb-mode-category__header");
        var name = new TextField { value = category.label, isDelayed = true, tooltip = "Category name shown to users" };
        name.AddToClassList("mcb-mode-category__name");
        name.RegisterValueChangedCallback(e => ChangeCustomization(() => category.label = string.IsNullOrWhiteSpace(e.newValue) ? category.label : e.newValue.Trim()));
        header.Add(name);
        var rule = new SegmentedControl(new[] { "Pick one", "Combine" }, index => ChangeCustomization(() =>
        {
            category.exclusive = index == 0;
            // An exclusive category keeps exactly one default.
            if (category.exclusive && members.Count > 0)
            {
                var keep = members.FirstOrDefault(o => o.enabledByDefault) ?? members[0];
                foreach (var member in members) member.enabledByDefault = member == keep;
            }
        }, true));
        rule.AddToClassList("orb-segmented--compact");
        rule.tooltip = "Pick one: the modes replace each other, like a genre. Combine: users turn each mode on or off.";
        rule.SetIndex(category.exclusive ? 0 : 1);
        header.Add(rule);
        header.Add(McbSectionUi.Pill("Add mode", () => AddMode(category, category.exclusive && members.Count == 0 ? "Male" : "New mode"), "small"));
        if (members.Count == 0)
            header.Add(McbSectionUi.IconAction(IconGlyph.Close, "Remove the " + category.label + " category", () => ChangeCustomization(() => modes.categories.Remove(category), true)));
        group.Add(header);
        if (members.Count == 0) group.Add(McbSectionUi.Text("No mode yet: add one to this category.", "mcb-muted"));
        foreach (var option in members) group.Add(BuildModeCard(option, category, renderers));
        return group;
    }

    private VisualElement BuildModeCard(ModeOption option, ModeCategory category, List<(string path, SkinnedMeshRenderer renderer)> renderers)
    {
        var modes = Modes;
        bool collapsed = !expandedModes.Contains(option.id);
        var card = new VisualElement(); card.AddToClassList("mcb-subcard");
        card.EnableInClassList("mcb-mode-card--default", option.enabledByDefault);
        var header = new VisualElement(); header.AddToClassList("mcb-subcard__header");
        var chevron = McbSectionUi.IconAction(IconGlyph.Chevron, collapsed ? "Show this mode" : "Hide this mode", () =>
        {
            if (!expandedModes.Add(option.id)) expandedModes.Remove(option.id);
            RefreshUIToolkit();
        });
        chevron.style.marginLeft = 0;
        chevron.style.rotate = new Rotate(new Angle(collapsed ? 0 : 90, AngleUnit.Degree));
        header.Add(chevron);
        var label = new TextField { value = option.label, isDelayed = true, tooltip = "Name shown to users" };
        label.AddToClassList("mcb-mode-card__label");
        label.RegisterValueChangedCallback(e => ChangeCustomization(() => option.label = string.IsNullOrWhiteSpace(e.newValue) ? option.label : e.newValue.Trim()));
        header.Add(label);
        if (category.exclusive)
        {
            var makeDefault = McbSectionUi.Pill(option.enabledByDefault ? "Default" : "Make default", () => ChangeCustomization(() =>
            {
                foreach (var member in modes.options.Where(o => o.category == category.id)) member.enabledByDefault = member == option;
            }, true), "small", option.enabledByDefault ? "accent" : null);
            makeDefault.AddToClassList("mcb-mode-card__default");
            makeDefault.tooltip = option.enabledByDefault ? "New users, and users whose choice this version lacks, start with it." : "Start new users with this mode";
            header.Add(makeDefault);
        }
        else
        {
            var defaultOn = McbSectionUi.Row("mcb-mode-card__default");
            defaultOn.Add(McbSectionUi.Text("On by default", "mcb-muted"));
            defaultOn.Add(new ToggleSwitch(option.enabledByDefault, value => ChangeCustomization(() => option.enabledByDefault = value, true)));
            defaultOn.tooltip = "Whether new users start with this mode on";
            header.Add(defaultOn);
        }
        header.Add(McbSectionUi.IconAction(IconGlyph.Close, "Remove " + option.label, () => ChangeCustomization(() =>
        {
            modes.options.Remove(option);
            var rest = modes.options.Where(o => o.category == category.id).ToList();
            if (category.exclusive && option.enabledByDefault && rest.Count > 0) rest[0].enabledByDefault = true;
        }, true)));
        card.Add(header);
        if (collapsed)
        {
            card.Add(McbSectionUi.Text($"{option.blendshapes.Count} blendshape(s) · {option.gameObjects.Count} object(s) · {option.animations.Count} animation(s)", "mcb-subcard__detail"));
            return card;
        }

        card.Add(McbSectionUi.Text("Blendshapes", "mcb-section__group-title"));
        if (option.blendshapes.Count == 0) card.Add(McbSectionUi.Text("Blendshapes another mode sets return to 0 unless an active mode sets them.", "mcb-muted"));
        foreach (var shape in option.blendshapes.ToArray()) card.Add(BuildShapeRule(option, shape, renderers));
        if (modePickerFor == option.id) card.Add(BuildModeShapePicker(option, renderers));
        else
        {
            var add = McbSectionUi.Row("mcb-actions");
            add.Add(McbSectionUi.Pill("Add blendshapes", () =>
            {
                modePickerFor = option.id;
                modePickerSelection.Clear();
                modePickerMesh = renderers.Any(r => r.path == modePickerMesh) ? modePickerMesh
                    : renderers.Select(r => r.path).FirstOrDefault(p => p == "Body") ?? renderers.FirstOrDefault().path;
                RefreshUIToolkit();
            }, "first", "small"));
            card.Add(add);
        }

        card.Add(McbSectionUi.Text("Objects", "mcb-section__group-title"));
        card.Add(McbSectionUi.Text("Objects keep their original state unless an active mode sets them.", "mcb-muted"));
        foreach (var state in option.gameObjects.ToArray()) card.Add(BuildObjectRule(option, state));
        card.Add(DropZone<GameObject>("Add an object", "Drop an object from this avatar or from the logic prefab", (go, error) =>
        {
            string path = ModeObjectPath(go);
            if (path == null) { error("Drop an object of this avatar or of its logic prefab."); return; }
            if (option.gameObjects.Any(s => s.path == path)) { error("This mode already sets that object."); return; }
            ChangeCustomization(() => option.gameObjects.Add(new ModeGameObject { path = path, active = !go.activeSelf }), true);
        }));

        card.Add(McbSectionUi.Text("Animations", "mcb-section__group-title"));
        card.Add(McbSectionUi.Text("The first frame applies while the mode is on: bone poses, blendshapes, objects. Ships with the version.", "mcb-muted"));
        foreach (string guid in option.animations.ToArray()) card.Add(BuildAnimationRule(option, guid));
        card.Add(DropZone<AnimationClip>("Add an animation", "Drop an animation clip, such as the toggle animation of the original avatar", (clip, error) =>
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long _) || !AssetDatabase.IsMainAsset(clip)) { error("Use an animation clip asset (.anim)."); return; }
            if (option.animations.Contains(guid)) { error("This mode already plays that animation."); return; }
            ChangeCustomization(() => option.animations.Add(guid), true);
        }));

        var identity = new Foldout { text = "Stable ID and category", value = false };
        var id = new TextField { value = option.id, isDelayed = true, tooltip = "Keep this ID across versions to remember the user's choice." };
        id.RegisterValueChangedCallback(e => ChangeCustomization(() =>
        {
            string value = (e.newValue ?? "").Trim();
            if (value.Length == 0 || modes.options.Any(o => o != option && o.id == value)) return;
            if (expandedModes.Remove(option.id)) expandedModes.Add(value);
            option.id = value;
        }, true));
        identity.Add(id);
        identity.Add(new SearchableDropdownField("Category", "Mode category", modes.categories.Select(c => new KeyValuePair<string, string>(c.id, c.label)), option.category,
            value => ChangeCustomization(() =>
            {
                var target = modes.Category(value);
                option.category = value;
                // Joining an exclusive category: the first member becomes its default, any other is not.
                option.enabledByDefault = target.exclusive ? modes.options.All(o => o == option || o.category != value) : option.enabledByDefault;
                var left = modes.options.Where(o => o.category == category.id).ToList();
                if (category.exclusive && left.Count > 0 && left.All(o => !o.enabledByDefault)) left[0].enabledByDefault = true;
            }, true)));
        identity.Add(McbSectionUi.Text("Users keep a mode's state across versions that share its ID.", "mcb-muted"));
        card.Add(identity);
        return card;
    }

    private VisualElement DropZone<T>(string label, string tooltip, Action<T, Action<string>> dropped) where T : UnityEngine.Object
    {
        var host = new VisualElement();
        var drop = new ObjectField(label) { objectType = typeof(T), allowSceneObjects = typeof(T) == typeof(GameObject), tooltip = tooltip };
        drop.AddToClassList("mcb-drop-zone");
        var error = McbSectionUi.Note(string.Empty, "error");
        error.style.display = DisplayStyle.None;
        drop.RegisterValueChangedCallback(e =>
        {
            if (!(e.newValue is T value)) return;
            dropped(value, message =>
            {
                drop.SetValueWithoutNotify(null);
                error.text = message;
                error.style.display = DisplayStyle.Flex;
            });
        });
        host.Add(drop);
        host.Add(error);
        return host;
    }

    private VisualElement BuildShapeRule(ModeOption option, ModeBlendshape shape, List<(string path, SkinnedMeshRenderer renderer)> renderers)
    {
        var row = new VisualElement(); row.AddToClassList("mcb-rule");
        var name = new VisualElement(); name.AddToClassList("mcb-rule__name"); name.style.flexDirection = FlexDirection.Row;
        name.Add(McbSectionUi.Text(shape.name, "mcb-strong"));
        name.Add(McbSectionUi.Text(shape.mesh, "mcb-rule__mesh"));
        bool exists = renderers.Any(r => r.path == shape.mesh && r.renderer.sharedMesh.GetBlendShapeIndex(shape.name) >= 0);
        name.tooltip = exists ? shape.mesh + " / " + shape.name : "Missing on the custom model: " + shape.mesh + " / " + shape.name;
        if (!exists) name.AddToClassList("mcb-section__note--error");
        row.Add(name);
        var slider = new Slider(0, 100) { value = Mathf.Clamp(shape.value, 0, 100) };
        slider.AddToClassList("mcb-rule__slider");
        var value = new FloatField { value = shape.value, isDelayed = true };
        value.AddToClassList("mcb-rule__value");
        slider.RegisterValueChangedCallback(e => { value.SetValueWithoutNotify(Mathf.Round(e.newValue)); ChangeCustomization(() => shape.value = Mathf.Round(e.newValue)); });
        value.RegisterValueChangedCallback(e =>
        {
            if (float.IsNaN(e.newValue) || float.IsInfinity(e.newValue)) return;
            slider.SetValueWithoutNotify(Mathf.Clamp(e.newValue, 0, 100));
            ChangeCustomization(() => shape.value = e.newValue);
        });
        row.Add(slider);
        row.Add(value);
        row.Add(McbSectionUi.IconAction(IconGlyph.Close, "Remove " + shape.name, () => ChangeCustomization(() => option.blendshapes.Remove(shape), true)));
        return row;
    }

    private VisualElement BuildModeShapePicker(ModeOption option, List<(string path, SkinnedMeshRenderer renderer)> renderers)
    {
        var host = new VisualElement(); host.AddToClassList("mcb-picker-host");
        var header = new VisualElement(); header.AddToClassList("mcb-picker-host__header");
        var meshField = new SearchableDropdownField("Mesh", "Custom mesh", renderers.Select(r => new KeyValuePair<string, string>(r.path, r.path)), modePickerMesh, mesh =>
        {
            modePickerMesh = mesh; modePickerSelection.Clear(); RefreshUIToolkit();
        });
        meshField.AddToClassList("mcb-grow");
        header.Add(meshField);
        host.Add(header);
        var renderer = renderers.FirstOrDefault(r => r.path == modePickerMesh).renderer;
        if (renderer != null)
        {
            var taken = new HashSet<string>(option.blendshapes.Where(s => s.mesh == modePickerMesh).Select(s => s.name));
            host.Add(new BlendshapePicker(BlendshapeNames(renderer).Where(n => !taken.Contains(n)), modePickerSelection, () => { },
                searchTooltip: "Search " + modePickerMesh + "'s blendshapes"));
        }
        var actions = McbSectionUi.Row("mcb-actions");
        actions.Add(McbSectionUi.Pill("Add selected at 100", () =>
        {
            if (modePickerSelection.Count == 0) return;
            ChangeCustomization(() =>
            {
                foreach (string name in modePickerSelection) option.blendshapes.Add(new ModeBlendshape { mesh = modePickerMesh, name = name, value = 100 });
                modePickerFor = null;
            }, true);
        }, "primary", "first"));
        actions.Add(McbSectionUi.Pill("Cancel", () => { modePickerFor = null; RefreshUIToolkit(); }));
        host.Add(actions);
        return host;
    }

    private VisualElement BuildObjectRule(ModeOption option, ModeGameObject state)
    {
        var row = new VisualElement(); row.AddToClassList("mcb-rule");
        var target = ResolveModeObject(state.path);
        var name = new VisualElement(); name.AddToClassList("mcb-rule__name"); name.AddToClassList("mcb-grow"); name.style.flexDirection = FlexDirection.Row;
        name.Add(McbSectionUi.Text(state.path.Split('/').Last(), "mcb-strong"));
        name.Add(McbSectionUi.Text(state.path, "mcb-rule__mesh"));
        name.tooltip = target != null ? state.path : "Not found on this avatar or its logic prefab: " + state.path;
        if (target == null) name.AddToClassList("mcb-section__note--error");
        row.Add(name);
        var active = new SegmentedControl(new[] { "Enabled", "Disabled" }, index => ChangeCustomization(() => state.active = index == 0));
        active.AddToClassList("orb-segmented--compact");
        active.SetIndex(state.active ? 0 : 1);
        row.Add(active);
        row.Add(McbSectionUi.IconAction(IconGlyph.Close, "Remove " + state.path, () => ChangeCustomization(() => option.gameObjects.Remove(state), true)));
        return row;
    }

    private VisualElement BuildAnimationRule(ModeOption option, string guid)
    {
        var row = new VisualElement(); row.AddToClassList("mcb-rule");
        var clip = ModeService.Clip(guid);
        var name = new VisualElement(); name.AddToClassList("mcb-rule__name"); name.AddToClassList("mcb-grow"); name.style.flexDirection = FlexDirection.Row;
        name.Add(McbSectionUi.Text(clip != null ? clip.name : "Missing animation", "mcb-strong"));
        if (clip != null)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            int shapes = bindings.Count(b => b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal));
            int objects = bindings.Count(b => b.propertyName == "m_IsActive");
            int bones = bindings.Where(b => b.type == typeof(Transform)).Select(b => b.path).Distinct().Count();
            var parts = new List<string>();
            if (bones > 0) parts.Add(bones + " bone(s)");
            if (shapes > 0) parts.Add(shapes + " blendshape(s)");
            if (objects > 0) parts.Add(objects + " object(s)");
            int other = bindings.Length - shapes - objects - bindings.Count(b => b.type == typeof(Transform));
            if (other > 0) parts.Add(other + " other value(s)");
            name.Add(McbSectionUi.Text(parts.Count > 0 ? string.Join(" · ", parts) : "Animates nothing", "mcb-rule__mesh"));
            var root = AvatarPaths.Root(editor.customBaseTarget);
            var missing = bindings.Select(b => b.path).Distinct().Where(p => p.Length > 0 && ResolveModeObject(p) == null && AvatarPaths.Find(root, p) == null).ToArray();
            name.tooltip = AssetDatabase.GetAssetPath(clip) + (missing.Length > 0 ? "\nNot on this avatar: " + string.Join(", ", missing.Take(6)) : "");
            if (missing.Length > 0) name.AddToClassList("mcb-section__note--warning");
            row.RegisterCallback<ClickEvent>(_ => EditorGUIUtility.PingObject(clip));
        }
        else
        {
            name.AddToClassList("mcb-section__note--error");
            name.tooltip = "This animation is missing from the project: " + guid;
        }
        row.Add(name);
        row.Add(McbSectionUi.IconAction(IconGlyph.Close, "Remove this animation", () => ChangeCustomization(() => option.animations.Remove(guid), true)));
        return row;
    }

    /// <summary>The avatar-relative path of a dropped object; logic prefab objects use the installed "mcb logic/" prefix.</summary>
    private string ModeObjectPath(GameObject go)
    {
        var avatar = AvatarPaths.Root(editor.customBaseTarget);
        var logic = editor.customBaseTarget.avatarLogicPrefab;
        if (go.transform.IsChildOf(avatar) && go.transform != avatar) return AnimationUtility.CalculateTransformPath(go.transform, avatar);
        if (logic != null && go.transform.IsChildOf(logic.transform) && go.transform != logic.transform)
            return AvatarPaths.LogicPrefix + AnimationUtility.CalculateTransformPath(go.transform, logic.transform);
        return null;
    }

    private GameObject ResolveModeObject(string path)
    {
        var logic = editor.customBaseTarget.avatarLogicPrefab;
        if (path.StartsWith(AvatarPaths.LogicPrefix, StringComparison.Ordinal) && logic != null)
            return logic.transform.Find(path.Substring(AvatarPaths.LogicPrefix.Length))?.gameObject;
        return AvatarPaths.Root(editor.customBaseTarget).Find(path)?.gameObject;
    }

    /// <summary>Animation clips the version's modes play: shipped in the logic package so their GUIDs resolve for users.</summary>
    private IEnumerable<string> ModeAnimationAssetPaths() =>
        Modes.options.SelectMany(o => o.animations).Distinct().Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p));
}
