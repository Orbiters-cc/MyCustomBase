using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Version settings edited on the custom meshes: dynamic normals and twisting bones.
public partial class CreatorModeModule
{
    private bool normalsExpanded;

    private IEnumerable<(string path, SkinnedMeshRenderer renderer)> CustomizationRenderers()
    {
        var sources = editor.customBaseTarget.modelFileBuildEntries.Where(e => e.customFbx != null).Select(e => e.customFbx)
            .Concat(new[] { editor.customBaseTarget.customFbxForCreator }).Where(g => g != null).Distinct().ToArray();
        if (sources.Length == 0) sources = new[] { AvatarPaths.Root(editor.customBaseTarget).gameObject };
        return sources.SelectMany(g => g.GetComponentsInChildren<SkinnedMeshRenderer>(true)
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
}
