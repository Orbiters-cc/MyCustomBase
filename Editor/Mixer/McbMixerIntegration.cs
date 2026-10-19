#if UNITY_EDITOR
using System;
using System.Linq;
using Orbiters.Mixer.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The live part of the creator mode's Blender card when Orbiters Mixer is installed: the Mixer link's state, Open scene
/// in Blender, and the custom base's renderers with what Blender or Mixer is doing to them, with Commit, Revert and Edit
/// in Mixer. Registers itself with MCB's creator mode when scripts load.
/// </summary>
[InitializeOnLoad]
internal static class McbMixerIntegration
{
    private static readonly Color Green = new Color(0.2f, 0.8f, 0.2f);
    private static readonly Color Grey = new Color(0.45f, 0.45f, 0.45f);
    private static readonly Color LiveText = new Color(0.5f, 0.85f, 0.6f);
    private static readonly Color IdleText = new Color(0.66f, 0.66f, 0.66f);

    static McbMixerIntegration()
    {
        McbBlenderCard.Mixer = Build;
        // Blender's nodes carry the target FBX of each mesh of a custom base: the Mixer extension's Export for MCB groups by it.
        MixerLink.TargetFbxResolver = BlenderExportImport.TargetFbxOf;
        MixerLink.BlenderStatus += (id, message) => { if (id == "mcb.export") BlenderExportImport.NoteExport(message); };
    }

    private static void Build(VisualElement card, MCBEditor editor, Action refresh)
    {
        var state = CreatorModeModule.CreateCreatorRow();
        state.AddToClassList("mcb-blender__state");
        var dot = new VisualElement();
        dot.AddToClassList("mcb-blender__state-dot");
        state.Add(dot);
        var text = CreatorModeModule.CreateMutedLabel(string.Empty);
        text.AddToClassList("mcb-blender__state-text");
        state.Add(text);
        card.Add(state);

        var actions = CreatorModeModule.CreateCreatorRow();
        actions.AddToClassList("mcb-blender__actions");
        actions.Add(CreatorModeModule.CreateTextButton("Open scene in Blender",
            "Starts Blender on this scene's Mixer project: the avatar with its meshes, bones and materials is there, and what you edit in Blender shows on the avatar live.", () =>
            {
                string error = MixerLink.OpenInBlender();
                McbBlenderCard.Say(error ?? "Blender is starting on the scene's project. Edit the base there: the edits show on the avatar live, and Commit keeps them.",
                    error != null ? MessageType.Warning : MessageType.Info);
                refresh?.Invoke();
            }));
        actions.Add(CreatorModeModule.CreateTextButton("Open Mixer",
            "Mixer's own tools (sculpt, weights, armature) and the Blender link's settings.", MixerLink.OpenMixer));
        card.Add(actions);

        var meshes = new VisualElement();
        meshes.AddToClassList("mcb-blender__meshes");
        card.Add(meshes);

        string shown = null;
        void Refresh()
        {
            bool connected = MixerLink.Connected, listening = MixerLink.Listening;
            dot.style.backgroundColor = connected ? Green : listening ? EditorUIUtils.OrangeColor : Grey;
            text.text = connected ? "Blender " + (MixerLink.BlenderVersion ?? string.Empty).Trim() + " is linked: what you edit there shows on the avatar."
                : listening ? "Waiting for Blender: open the scene in Blender, or connect from the Mixer extension in an open Blender."
                : "The Blender link is off: turn it on in Mixer's Blender menu.";

            var renderers = BlenderExportImport.TargetRenderers(editor);
            string key = string.Join("\n", renderers.Select(r => r.Renderer.GetInstanceID() + ":" + MixerLink.StateOf(r.Renderer) + ":" + MixerLink.HasEdits(r.Renderer)));
            if (key == shown) return;
            shown = key;
            meshes.Clear();
            foreach (var entry in renderers)
            {
                var renderer = entry.Renderer;
                var rendererState = MixerLink.StateOf(renderer);
                bool edits = MixerLink.HasEdits(renderer);
                var row = CreatorModeModule.CreateCreatorRow();
                row.AddToClassList("mcb-blender__mesh");
                string what = rendererState == MixerLink.RendererState.EditedInBlender ? "being edited in Blender"
                    : rendererState == MixerLink.RendererState.EditedInMixer ? (edits ? "edited in Mixer, not committed" : "open in Mixer")
                    : "as imported";
                var label = CreatorModeModule.CreateLabel(renderer.name + ": " + what, 11, FontStyle.Normal, rendererState == MixerLink.RendererState.Untouched ? IdleText : LiveText);
                label.style.flexGrow = 1;
                row.Add(label);
                if (rendererState == MixerLink.RendererState.Untouched)
                {
                    row.Add(CreatorModeModule.CreateTextButton("Edit in Mixer", "Sculpt, paint weights or edit the armature of this mesh in Unity.", () =>
                    {
                        string error = MixerLink.Edit(renderer);
                        if (error != null) McbBlenderCard.Say(error, MessageType.Warning);
                        refresh?.Invoke();
                    }));
                }
                else
                {
                    var commit = CreatorModeModule.CreateTextButton("Commit", "Saves the edits as a mesh asset shown on the renderer, in one Undo step.", () =>
                    {
                        var mesh = MixerLink.Commit(renderer);
                        McbBlenderCard.Say(mesh != null ? "Committed " + renderer.name + " to " + AssetDatabase.GetAssetPath(mesh) + "." : renderer.name + " has nothing to commit.", MessageType.Info);
                        refresh?.Invoke();
                    });
                    commit.SetEnabled(edits && rendererState != MixerLink.RendererState.EditedInBlender);
                    row.Add(commit);
                    row.Add(CreatorModeModule.CreateTextButton("Revert", "Puts the renderer's own mesh back; the Blender side keeps its copy.", () =>
                    {
                        MixerLink.Revert(renderer);
                        refresh?.Invoke();
                    }));
                }
                meshes.Add(row);
            }
        }

        Refresh();
        card.schedule.Execute(Refresh).Every(500);
        // An export Blender reports adds the newest-export button: the card is built again.
        Action onImportChanged = () => refresh?.Invoke();
        BlenderExportImport.Changed += onImportChanged;
        card.RegisterCallback<DetachFromPanelEvent>(_ => BlenderExportImport.Changed -= onImportChanged);
    }
}
#endif
