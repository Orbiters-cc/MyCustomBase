using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Version settings edited on the custom meshes: dynamic normals, twisting bones and physic.
public partial class CreatorModeModule
{
    private bool normalsExpanded;

    private GameObject[] CustomizationModels()
    {
        var sources = editor.customBaseTarget.modelFileBuildEntries.Where(e => e.customFbx != null).Select(e => e.customFbx)
            .Concat(new[] { editor.customBaseTarget.customFbxForCreator }).Where(g => g != null).Distinct().ToArray();
        return sources.Length > 0 ? sources : new[] { AvatarPaths.Root(editor.customBaseTarget).gameObject };
    }

    private IEnumerable<(string path, SkinnedMeshRenderer renderer)> CustomizationRenderers()
    {
        return CustomizationModels().SelectMany(g => g.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh != null).Select(r => (AnimationUtility.CalculateTransformPath(r.transform, g.transform), r)))
            .GroupBy(r => r.Item1).Select(g => g.First());
    }

    private static string[] BlendshapeNames(SkinnedMeshRenderer renderer) =>
        Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.sharedMesh.GetBlendShapeName).ToArray();

    // ---- Dynamic normals --------------------------------------------------------------------------------------

    private void BuildNormalsSection(VisualElement root)
    {
        int count = Customization.dynamicNormalBlendshapes.Sum(s => s.names.Count);
        var section = McbSectionUi.Section("Dynamic normals",
            count == 0 ? "Recalculate the normals of chosen blendshapes in the built version, so muscles and flexing shade correctly."
                : $"{count} blendshape(s) recalculate their normals in the built version.", out var body,
            McbSectionUi.Pill(normalsExpanded ? "Done" : count == 0 ? "Set up dynamic normals" : "Edit", () => { normalsExpanded = !normalsExpanded; RefreshUIToolkit(); },
                normalsExpanded ? "accent" : null));
        if (!normalsExpanded)
        {
            var chips = McbSectionUi.Row("mcb-chips");
            chips.style.flexWrap = Wrap.Wrap;
            foreach (var selection in Customization.dynamicNormalBlendshapes)
                chips.Add(McbSectionUi.Chip(selection.mesh + " · " + selection.names.Count, accent: true));
            if (chips.childCount > 0) body.Add(chips); else section.Remove(body);
            root.Add(section);
            return;
        }
        foreach (var item in CustomizationRenderers())
        {
            string path = item.path;
            var names = BlendshapeNames(item.renderer);
            if (names.Length == 0) continue;
            var selection = Customization.dynamicNormalBlendshapes.FirstOrDefault(s => s.mesh == path);
            var chosen = selection != null ? new List<string>(selection.names) : new List<string>();
            var card = new VisualElement(); card.AddToClassList("mcb-subcard");
            var header = new VisualElement(); header.AddToClassList("mcb-subcard__header");
            header.Add(McbSectionUi.Text(path, "mcb-subcard__title"));
            BlendshapePicker picker = null;
            header.Add(McbSectionUi.Pill("Flexings", () => picker.SelectMatching("flex"), "small"));
            header.Add(McbSectionUi.Pill("Muscles", () => picker.SelectMatching("muscle"), "small"));
            card.Add(header);
            picker = new BlendshapePicker(names, chosen, () => ChangeCustomization(() =>
            {
                Customization.dynamicNormalBlendshapes.RemoveAll(s => s.mesh == path);
                if (chosen.Count > 0) Customization.dynamicNormalBlendshapes.Add(new MeshBlendshapeSelection { mesh = path, names = new List<string>(chosen) });
            }));
            card.Add(picker);
            body.Add(card);
        }
        root.Add(section);
    }

    // ---- Twisting bones --------------------------------------------------------------------------------------

    private void BuildTwistSection(VisualElement root)
    {
        var twists = Customization.twistBones;
        var section = McbSectionUi.Section("Twisting bones",
            twists.Count == 0 ? "Spread a bone's twist along its mesh, like forearms following the wrist. Works on any bone, clothing included."
                : $"{twists.Count} bone(s) twist · adds {twists.Count} bone(s) and {twists.Count} VRC constraint(s) to the built avatar.", out var body,
            McbSectionUi.Pill(twists.Count == 0 ? "Set twisting bones" : "Edit", () => TwistBoneWindow.Open(editor.customBaseTarget, () => RefreshUIToolkit())));
        if (twists.Count == 0) { section.Remove(body); root.Add(section); return; }
        var chips = McbSectionUi.Row("mcb-chips");
        chips.style.flexWrap = Wrap.Wrap;
        foreach (var twist in twists)
            chips.Add(McbSectionUi.Chip(twist.bone.Split('/').Last() + "  →  " + twist.aim.Split('/').Last(), accent: true, tooltip: twist.bone));
        body.Add(chips);
        root.Add(section);
    }

    // ---- Physic -----------------------------------------------------------------------------------------------

    // Chains are named, not configured: any bone of the custom models whose name contains "physic".
    private void BuildPhysicSection(VisualElement root)
    {
        bool on = Customization.physic;
        var chains = CustomizationModels().SelectMany(m => PhysicService.Chains(m.transform)).ToList();
        var groups = CustomizationModels().Sum(m => PhysicService.Groups(m.transform).Count);
        int bones = CustomizationModels().Sum(m => PhysicService.Bones(m.transform).Count);
        string caption = !on
            ? "Let users add secondary motion. Bones with \"physic\" in their name get PhysBones when users turn physic on, and are removed from the build when they leave it off."
            : chains.Count == 0 ? "No bone of the custom models has \"physic\" in its name yet."
            : $"{chains.Count} chain(s) · {bones} bone(s) · {groups} PhysBone(s) when users turn it on. Off by default: the build removes these bones.";
        var pill = McbSectionUi.Pill(on ? "Supported" : "Support physic", () => ChangeCustomization(() => Customization.physic = !Customization.physic, true), on ? "accent" : null);
        pill.tooltip = on ? "Stop supporting physic in this version" : "Users of this version can turn on PhysBones for the physic bones";
        var section = McbSectionUi.Section("Physic", caption, out var body, pill);
        if (!on) { section.Remove(body); root.Add(section); return; }
        if (chains.Count == 0)
        {
            body.Add(McbSectionUi.Note("Rename the secondary-motion bones of the custom model, e.g. \"Left triceps physic\". Every bone of a chain needs the word, its tip included.", "warning"));
            root.Add(section);
            return;
        }
        var chips = McbSectionUi.Row("mcb-chips");
        chips.style.flexWrap = Wrap.Wrap;
        foreach (var chain in chains)
        {
            int count = chain.GetComponentsInChildren<Transform>(true).Count(PhysicService.IsPhysic);
            chips.Add(McbSectionUi.Chip(chain.name + (count > 1 ? " · " + count : ""), accent: true, tooltip: chain.parent.name + " / " + chain.name));
        }
        body.Add(chips);
        root.Add(section);
    }
}
