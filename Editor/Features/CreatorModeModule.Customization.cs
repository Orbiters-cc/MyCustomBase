using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Version settings with their own data: supported originals and the renderer layout. Protection is the asset's.
public partial class CreatorModeModule
{
    private bool originalsExpanded;
    private bool fallbacksExpanded;
    private bool materialSlotsExpanded;
    private VersionCustomization Customization => editor.customBaseTarget.creatorCustomization;
    private bool HasTypedCustomization() => Customization.modes.options.Count > 0 || Customization.twistBones.Count > 0
        || Customization.dynamicNormalBlendshapes.Count > 0 || !Customization.rendererLayout.IsEmpty || Customization.physic;

    public void ConfigureVersionMetadata(string version, string title, string changelog, Scope scope, CustomBaseVersion parent = null)
    {
        var parts = (version ?? "").Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch) || major < 0 || minor < 0 || patch < 0)
            throw new ArgumentException("Version must be three nonnegative numbers, e.g. 5.0.0.");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A version title is required.");
        newVersionMajor = major; newVersionMinor = minor; newVersionPatch = patch;
        newVersionTitle = title; newChangelog = changelog ?? ""; newVersionScope = scope;
        selectedParentVersionObject = parent;
    }

    private void ChangeCustomization(Action action, bool refresh = false)
    {
        editor.serializedObject.ApplyModifiedProperties();
        Undo.RecordObject(editor.customBaseTarget, "Edit version customization");
        action();
        // New authoring uses explicit selections. The keyword shortcuts only populate that list.
        editor.customBaseTarget.includeDynamicNormalsBodyForCreator = false;
        editor.customBaseTarget.includeDynamicNormalsFlexingForCreator = false;
        EditorUtility.SetDirty(editor.customBaseTarget);
        editor.serializedObject.Update();
        if (refresh) RefreshUIToolkit();
    }

    // ---- Protection ---------------------------------------------------------------------------------------------

    /// <summary>The asset's protection, which its versions inherit (set when creating the asset or in its Edit panel).</summary>
    internal VersionProtection ResolveVersionProtection()
    {
        var protection = editor.GetSelectedAsset()?.protection;
        return new VersionProtection { xor = protection?.xor ?? true, discordRole = protection?.discordRole ?? false };
    }

    /// <summary>
    /// Bones an avatar needs to see a plain version: those the payload binds that every selected original shares.
    /// Bones only some originals have (whiskers, a reparented chest) are created on apply instead of required.
    /// </summary>
    private static string[] RequiredSkeleton(IEnumerable<string> bound, AvatarDiscoveredAsset asset)
    {
        var bones = bound.Distinct(StringComparer.Ordinal).ToList();
        // One package serves every registered original, so their shared skeleton is required.
        var shared = OriginalBaseLibrary.SharedSkeleton(OriginalBaseLibrary.Versions(asset), bones);
        return bones.Where(name => shared == null || shared.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    // ---- Supported originals ----------------------------------------------------------------------------------

    private void BuildSupportedOriginalsSection(VisualElement root)
    {
        var asset = editor.GetSelectedAsset();
        if (asset == null) return;
        if (asset.UsesPlainPackages())
        {
            // An unencrypted asset ships one package for every original: there is nothing to choose.
            root.Add(McbSectionUi.Section("One package for every original",
                "This custom base verifies a Discord role instead of encrypting each original's copy. Change it in the asset's Edit panel.", out var plainBody));
            plainBody.parent.Remove(plainBody);
            return;
        }
        var versions = OriginalBaseLibrary.Versions(asset);
        if (versions.Length == 0) return;
        var selection = OriginalBaseLibrary.Selection(asset).ToList();
        var duplicates = new HashSet<string>(versions.GroupBy(v => v.label).Where(g => g.Count() > 1).Select(g => g.Key));
        string Label(string key)
        {
            var version = versions.First(v => v.key == key);
            return duplicates.Contains(version.label) ? version.label + " · " + key.Substring(0, 6) : version.label;
        }
        string Caption() => $"{selection.Count} of {versions.Length} selected · each receives its own encrypted copy of this version.";
        var section = McbSectionUi.Section("Supported original bases", Caption(), out var body,
            McbSectionUi.Pill(originalsExpanded ? "Done" : "Choose", () => { originalsExpanded = !originalsExpanded; RefreshUIToolkit(); }));
        if (originalsExpanded)
        {
            body.Add(new BlendshapePicker(versions.Select(v => v.key), selection, () =>
            {
                OriginalBaseLibrary.SaveSelection(asset.id, selection);
                ((Label)section.Q(className: "mcb-section__caption")).text = Caption();
            }, labelOf: Label, searchTooltip: "Search the registered original bases"));
            body.Add(McbSectionUi.Note("Your selection is reused for the next version."));
        }
        else
        {
            var chips = McbSectionUi.Row("mcb-chips");
            chips.style.flexWrap = Wrap.Wrap;
            foreach (string key in selection.Take(8)) chips.Add(McbSectionUi.Chip(Label(key)));
            if (selection.Count > 8) chips.Add(McbSectionUi.Chip("+" + (selection.Count - 8) + " more"));
            if (selection.Count == 0) chips.Add(McbSectionUi.Text("No original selected: choose at least one.", "mcb-muted", "mcb-section__note--warning"));
            body.Add(chips);
        }
        root.Add(section);
    }

    // ---- Renderer layout --------------------------------------------------------------------------------------

    /// <summary>The custom renderers this version ships with their material slot names, read from the model entries.</summary>
    private List<RendererSlotNames> CollectCustomRendererSlots()
    {
        var result = new List<RendererSlotNames>();
        SyncModelFileBuildEntryCount();
        for (int i = 0; i < editor.modelFileBuildEntriesProp.arraySize; i++)
        {
            var custom = editor.modelFileBuildEntriesProp.GetArrayElementAtIndex(i).FindPropertyRelative("customFbx").objectReferenceValue as GameObject;
            var target = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            if (custom == null || target == null) continue;
            try
            {
                var smrPaths = CollectTargetedSmrPathsForModelEntry(editor.customBaseTarget.transform.root, AssetDatabase.GetAssetPath(target), target, custom);
                result.AddRange(NativeMeshPayloadService.CollectRendererSlots(custom, smrPaths).Where(r => result.All(e => e.path != r.path)));
            }
            catch (Exception ex) { MCBLogger.LogWarning("[MCB] Could not read the custom renderers of " + custom.name + ": " + ex.Message); }
        }
        return result;
    }

    /// <summary>Original material slot and renderer names: the avatar's original models plus names already in use.</summary>
    private (List<string> slots, List<string> renderers) CollectOriginalNames()
    {
        var slots = new List<string>();
        var renderers = new List<string>();
        for (int i = 0; i < editor.baseFbxFilesProp.arraySize; i++)
        {
            var target = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            if (target == null) continue;
            foreach (var renderer in target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                renderers.Add(renderer.name);
                slots.AddRange(MaterialSlotNames.Of(renderer).Select(MaterialSlotNames.Normalize));
            }
        }
        slots.AddRange(Customization.rendererLayout.renderers.SelectMany(r => r.slots));
        slots.AddRange(Customization.rendererLayout.fallbacks.Select(f => f.slot));
        return (slots.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList(),
            renderers.Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList());
    }

    /// <summary>The layout a build ships: every built renderer keeps the creator's slot names when they still fit.</summary>
    internal static RendererLayoutConfiguration CompleteRendererLayout(RendererLayoutConfiguration creator, IEnumerable<RendererSlotNames> built)
    {
        creator = creator ?? new RendererLayoutConfiguration();
        var result = new RendererLayoutConfiguration { hide = creator.hide.ToList(), fallbacks = creator.fallbacks.ToList() };
        foreach (var renderer in built)
        {
            var chosen = creator.renderers.FirstOrDefault(r => r.path == renderer.path && r.slots.Count == renderer.slots.Count);
            result.renderers.Add(new RendererSlotNames { path = renderer.path,
                slots = chosen?.slots.ToList() ?? renderer.slots.Select(MaterialSlotNames.Normalize).ToList() });
        }
        result.hide.RemoveAll(name => result.renderers.Any(r => r.path == name));
        return result;
    }

    private void BuildMaterialSlotsSection(VisualElement root)
    {
        if (!(editor.useAdvancedMeshReplacementForCreatorProp?.boolValue ?? false)) return;
        var customs = CollectCustomRendererSlots();
        if (customs.Count == 0) return;
        var layout = Customization.rendererLayout;
        var (originalSlots, originalRenderers) = CollectOriginalNames();
        int changed = customs.Sum(c => layout.renderers.FirstOrDefault(r => r.path == c.path && r.slots.Count == c.slots.Count)?.slots
            .Where((slot, i) => !string.Equals(slot, MaterialSlotNames.Normalize(c.slots[i]), StringComparison.OrdinalIgnoreCase)).Count() ?? 0);
        string summary = $"{customs.Sum(c => c.slots.Count)} slot(s) matched by name" + (changed > 0 ? $", {changed} changed" : "")
            + $" · {layout.hide.Count} hidden original piece(s) · {layout.fallbacks.Count} fallback material(s).";
        var section = McbSectionUi.FoldableSection("Material slots", materialSlotsExpanded
                ? "Each custom slot takes the avatar's material from the original slot of the same name, whatever piece layout the original uses."
                : summary, materialSlotsExpanded, expanded => { materialSlotsExpanded = expanded; RefreshUIToolkit(); }, out var body,
            materialSlotsExpanded ? McbSectionUi.Pill("Match by names", () => ChangeCustomization(() => layout.renderers.Clear(), true)) : null);
        if (!materialSlotsExpanded) { root.Add(section); return; }
        var choices = originalSlots.Select(s => new KeyValuePair<string, string>(s, s)).ToList();
        // One card: multi-material renderers list their slots under their name; the others take one row each.
        var card = new VisualElement(); card.AddToClassList("mcb-subcard");
        body.Add(card);
        foreach (var custom in customs.OrderByDescending(c => c.slots.Count > 1))
        {
            bool single = custom.slots.Count == 1;
            if (!single)
            {
                var title = McbSectionUi.Text(custom.path, "mcb-subcard__title", "mcb-slot__group");
                card.Add(title);
            }
            var chosen = layout.renderers.FirstOrDefault(r => r.path == custom.path && r.slots.Count == custom.slots.Count);
            for (int i = 0; i < custom.slots.Count; i++)
            {
                int slot = i;
                string current = chosen?.slots[i] ?? MaterialSlotNames.Normalize(custom.slots[i]);
                var row = new VisualElement(); row.AddToClassList("mcb-slot");
                if (!single) row.AddToClassList("mcb-slot--nested");
                row.EnableInClassList("mcb-slot--changed", !string.Equals(current, MaterialSlotNames.Normalize(custom.slots[i]), StringComparison.OrdinalIgnoreCase));
                var source = new VisualElement(); source.AddToClassList("mcb-slot__source");
                source.Add(McbSectionUi.Text(single ? custom.path : (i + 1) + ".", "mcb-slot__name"));
                source.Add(McbSectionUi.Text(custom.slots[i], "mcb-slot__declared"));
                source.tooltip = custom.path + " · slot " + (i + 1) + " is named " + custom.slots[i] + " in the custom model";
                row.Add(source);
                row.Add(McbSectionUi.Text("→", "mcb-slot__arrow"));
                var options = choices.Any(c => c.Key == current) ? choices : choices.Append(new KeyValuePair<string, string>(current, current + " (not on the original)")).ToList();
                row.Add(new SearchableDropdownField(null, "Original material slot", options, current, value => ChangeCustomization(() =>
                {
                    var entry = layout.renderers.FirstOrDefault(r => r.path == custom.path);
                    if (entry == null || entry.slots.Count != custom.slots.Count)
                    {
                        layout.renderers.RemoveAll(r => r.path == custom.path);
                        entry = new RendererSlotNames { path = custom.path, slots = custom.slots.Select(MaterialSlotNames.Normalize).ToList() };
                        layout.renderers.Add(entry);
                    }
                    entry.slots[slot] = value;
                }, true)));
                card.Add(row);
            }
        }

        body.Add(McbSectionUi.Text("Hidden original pieces", "mcb-section__group-title"));
        body.Add(McbSectionUi.Text("Applying the version hides these renderers of the user's original model; resetting shows them again. Clothing is never hidden.", "mcb-muted"));
        var hidden = McbSectionUi.Row("mcb-chips");
        hidden.style.flexWrap = Wrap.Wrap;
        foreach (string name in layout.hide.ToArray())
            hidden.Add(McbSectionUi.Chip(name, () => ChangeCustomization(() => layout.hide.Remove(name), true)));
        if (layout.hide.Count == 0) hidden.Add(McbSectionUi.Text("None: every original piece stays visible.", "mcb-muted"));
        body.Add(hidden);
        var hideActions = McbSectionUi.Row("mcb-actions");
        var suggestions = originalRenderers.Where(n => customs.All(c => c.path != n) && !layout.hide.Contains(n)).ToList();
        var suggest = McbSectionUi.Pill("Hide pieces the custom model replaces", () => ChangeCustomization(() => layout.hide.AddRange(suggestions), true), "first");
        suggest.SetEnabled(suggestions.Count > 0);
        hideActions.Add(suggest);
        var nameField = new TextField { tooltip = "Name of a renderer in another original version, e.g. Claws. Press Enter to add it." };
        nameField.AddToClassList("mcb-grow");
        nameField.style.marginLeft = 6;
        nameField.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            string value = (nameField.value ?? "").Trim();
            if (value.Length == 0 || value.Contains("/") || layout.hide.Contains(value) || customs.Any(c => c.path == value)) return;
            ChangeCustomization(() => layout.hide.Add(value), true);
        });
        hideActions.Add(nameField);
        body.Add(hideActions);
        body.Add(McbSectionUi.Text("Other original versions can have more pieces: type a piece name in the field and press Enter to hide it too.", "mcb-muted"));

        var fallbackHeader = McbSectionUi.Row("mcb-actions");
        fallbackHeader.Add(McbSectionUi.Text("Fallback materials · " + layout.fallbacks.Count, "mcb-section__group-title", "mcb-grow"));
        fallbackHeader.Add(McbSectionUi.Pill(fallbacksExpanded ? "Done" : "Edit", () => { fallbacksExpanded = !fallbacksExpanded; RefreshUIToolkit(); }, "small"));
        body.Add(fallbackHeader);
        body.Add(McbSectionUi.Text("Used when the user's original has no slot of that name, such as reduced Quest models. Put these materials in the logic prefab's dependencies.", "mcb-muted"));
        if (fallbacksExpanded)
        {
            foreach (var fallback in layout.fallbacks.ToArray())
            {
                var row = new VisualElement(); row.AddToClassList("mcb-slot");
                var slotField = new SearchableDropdownField(null, "Original material slot", choices, fallback.slot, value => ChangeCustomization(() => fallback.slot = value, true));
                slotField.style.width = new Length(40, LengthUnit.Percent);
                row.Add(slotField);
                row.Add(McbSectionUi.Text("→", "mcb-slot__arrow"));
                var material = new ObjectField { objectType = typeof(Material), allowSceneObjects = false,
                    value = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(fallback.material)) };
                material.AddToClassList("mcb-grow");
                material.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(evt.newValue, out string guid, out long _))
                        ChangeCustomization(() => fallback.material = guid);
                });
                row.Add(material);
                row.Add(McbSectionUi.IconAction(IconGlyph.Close, "Remove this fallback", () => ChangeCustomization(() => layout.fallbacks.Remove(fallback), true)));
                body.Add(row);
            }
            var drop = new ObjectField("Add a fallback") { objectType = typeof(Material), allowSceneObjects = false };
            drop.AddToClassList("mcb-drop-zone");
            drop.RegisterValueChangedCallback(evt =>
            {
                if (!(evt.newValue is Material dropped) || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(dropped, out string guid, out long _)) return;
                string slot = originalSlots.FirstOrDefault(s => layout.fallbacks.All(f => !string.Equals(f.slot, s, StringComparison.OrdinalIgnoreCase))) ?? dropped.name;
                ChangeCustomization(() => layout.fallbacks.Add(new SlotMaterial { slot = slot, material = guid }), true);
            });
            body.Add(drop);
        }
        root.Add(section);
    }
}
