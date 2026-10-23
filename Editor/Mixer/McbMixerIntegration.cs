#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Mixer.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The live part of the creator mode's Blender card when Orbiters Mixer is installed: the Mixer link's state, Open scene
/// in Blender, and the custom base's renderers with what Blender or Mixer is doing to them, with Commit, Revert and Edit
/// in Mixer. Registers itself with MCB's creator mode when scripts load. Committed Mixer and Blender edits of the base's
/// meshes go into the next version build (written into their model's custom FBX, <see cref="VersionAvatarMeshes"/>), which
/// waits while a mesh is still in a Mixer session; armature edits reach a version through Export for MCB and the card's
/// import. The card says so. Also tells Mixer's blendshape list which shapes of a custom base are correctives (the applied
/// version's and the factor links set by hand), so a shape's target button lists them.
/// </summary>
[InitializeOnLoad]
internal static class McbMixerIntegration
{
    private static readonly Color Green = new Color(0.2f, 0.8f, 0.2f);
    private static readonly Color Grey = new Color(0.45f, 0.45f, 0.45f);
    private static readonly Color LiveText = new Color(0.5f, 0.85f, 0.6f);
    private static readonly Color IdleText = new Color(0.66f, 0.66f, 0.66f);
    // The base's renderers are looked up again at least this often (and at once after a hierarchy change or an Undo).
    private const double RendererLookupSeconds = 3.0;

    private const string IntoVersions = "Building a version writes committed meshes into a new custom FBX of their model. Armature edits reach a version " +
                                        "through Export for MCB in Blender and the import below.";

    static McbMixerIntegration()
    {
        McbBlenderCard.Mixer = Build;
        // A mesh in a Mixer session shows a working copy (no asset): MCB reads the base's model files through it.
        AvatarSceneMeshProvenanceService.WorkingMeshOriginal = MixerLink.OriginalOf;
        VersionAvatarMeshes.InMixerSession = renderer => MixerLink.StateOf(renderer) != MixerLink.RendererState.Untouched;
        // Blender's nodes carry the target FBX of each mesh of a custom base: the Mixer extension's Export for MCB groups by it.
        MixerLink.TargetFbxResolver = BlenderExportImport.TargetFbxOf;
        MixerLink.BlenderStatus += (id, message) => { if (id == "mcb.export") BlenderExportImport.NoteExport(message); };
        MixerLink.BlendshapeUses += CorrectiveUses;
    }

    private const string CorrectiveGroup = "MCB corrective";

    // The links MCB's build applies (BlendShapeLinkService, as its preprocess does) that name this shape of this renderer:
    // the shape it corrects, the shape correcting it, the custom blendshape whose value scales the correction. Paths are
    // the build's: from the avatar (its descriptor), wherever the base's component sits on it.
    private static IEnumerable<MixerLink.BlendshapeUse> CorrectiveUses(SkinnedMeshRenderer renderer, string shape)
    {
        var uses = new List<MixerLink.BlendshapeUse>();
        if (renderer == null || string.IsNullOrEmpty(shape)) return uses;
        // The renderer's own avatar only: a build copy's fallback by root name would claim another avatar of that name.
        var customBase = BlenderExportImport.CustomBaseOf(renderer.transform);
        if (customBase == null) return uses;
        var root = AvatarPaths.Root(customBase);
        if (!renderer.transform.IsChildOf(root)) return uses;
        string path = AnimationUtility.CalculateTransformPath(renderer.transform, root);
        string version = string.IsNullOrEmpty(customBase.appliedCustomBaseVersionString) ? "the applied version" : "version " + customBase.appliedCustomBaseVersionString;
        bool Named(CorrectiveActivationType type, string name) => type == CorrectiveActivationType.Blendshape && string.Equals(name, shape, StringComparison.Ordinal);
        string Endpoint(CorrectiveActivationType type, string name) => type == CorrectiveActivationType.Animation ? "the animation " + name : name;
        // A link without a renderer path is an animation-only one (the build refuses blendshape ends without a path): only its
        // custom blendshape can be this renderer's.
        bool OnThisRenderer(string targetPath, CorrectiveActivationType toFixType, CorrectiveActivationType fixedByType) =>
            string.IsNullOrEmpty(targetPath)
                ? toFixType != CorrectiveActivationType.Blendshape && fixedByType != CorrectiveActivationType.Blendshape
                : targetPath == path;
        foreach (var link in BlendShapeLinkService.Instance.GetActiveVersionLinkDebugInfo(root.gameObject))
        {
            if (!OnThisRenderer(link.targetRendererPath, link.toFixType, link.fixedByType)) continue;
            string scaled = string.IsNullOrEmpty(link.driverBlendshape) ? string.Empty : " \u00b7 scaled by the custom blendshape " + link.driverBlendshape;
            if (Named(link.fixedByType, link.fixedBy))
                uses.Add(new MixerLink.BlendshapeUse { Group = CorrectiveGroup, Title = "Corrects " + Endpoint(link.toFixType, link.toFix), Detail = "Custom base " + version + scaled, Target = customBase });
            if (Named(link.toFixType, link.toFix))
                uses.Add(new MixerLink.BlendshapeUse { Group = CorrectiveGroup, Title = "Corrected by " + Endpoint(link.fixedByType, link.fixedBy), Detail = "Custom base " + version + scaled, Target = customBase });
            if (string.Equals(link.driverBlendshape, shape, StringComparison.Ordinal) && !Named(link.fixedByType, link.fixedBy) && !Named(link.toFixType, link.toFix))
                uses.Add(new MixerLink.BlendshapeUse { Group = CorrectiveGroup, Title = "Scales the correction of " + Endpoint(link.toFixType, link.toFix) + " by " + Endpoint(link.fixedByType, link.fixedBy), Detail = "Custom blendshape of " + version, Target = customBase });
        }
        if (customBase.blendShapeFactorLinks != null)
        {
            foreach (var link in customBase.blendShapeFactorLinks)
            {
                if (link == null || !OnThisRenderer(link.targetRendererPath, link.toFixType, link.fixedByType)) continue;
                string state = link.enabled ? "BlendShape factor link set by hand" : "BlendShape factor link set by hand (off)";
                if (Named(link.fixedByType, link.fixedBy))
                    uses.Add(new MixerLink.BlendshapeUse { Group = CorrectiveGroup, Title = "Corrects " + Endpoint(link.toFixType, link.toFix), Detail = state, Target = customBase });
                if (Named(link.toFixType, link.toFix))
                    uses.Add(new MixerLink.BlendshapeUse { Group = CorrectiveGroup, Title = "Corrected by " + Endpoint(link.fixedByType, link.fixedBy), Detail = state, Target = customBase });
            }
        }
        return uses;
    }

    // Mixer's own Reverts ask the same before edits are lost for good (MixerConfirm, inside Mixer). It asks while Blender
    // edits the mesh too: Blender's edit in that mode is no longer taken (Blender keeps it and offers to send it again).
    private const string RevertOptOutKey = "MCB.Mixer.Confirm.Revert";

    private static bool ConfirmRevert(Renderer renderer)
    {
        bool inBlender = MixerLink.StateOf(renderer) == MixerLink.RendererState.EditedInBlender;
        if (!inBlender && !MixerLink.HasEdits(renderer)) return true;
        string message = inBlender
            ? renderer.name + " is being edited in Blender. Its original mesh comes back in Unity, and Mixer edits not committed are dropped: this cannot be undone. Blender keeps what it edits in that mode; its Mixer panel then sends it to Unity again or takes Unity's mesh."
            : "The Mixer edits of " + renderer.name + " have not been committed. Its original mesh comes back, in a linked Blender too. This cannot be undone.";
        return EditorUtility.DisplayDialog("Revert " + renderer.name, message, "Revert", "Cancel", DialogOptOutDecisionType.ForThisMachine, RevertOptOutKey);
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

        // Say() refreshes the card (the creator form follows BlenderExportImport.Changed): no second refresh after it.
        var actions = CreatorModeModule.CreateCreatorRow();
        actions.AddToClassList("mcb-blender__actions");
        actions.Add(CreatorModeModule.CreateTextButton("Open scene in Blender",
            "Starts Blender on this scene's Mixer project: the avatar with its meshes, bones and materials is there, and what you edit in Blender shows on the avatar live.", () =>
            {
                string error = MixerLink.OpenInBlender();
                McbBlenderCard.Say(editor, error ?? "Blender is starting on the scene's project. What you edit there shows on the avatar live, and Commit keeps it. " + IntoVersions,
                    error != null ? MessageType.Warning : MessageType.Info);
            }));
        actions.Add(CreatorModeModule.CreateTextButton("Open Mixer",
            "Mixer's own tools (sculpt, weights, armature) and the Blender link's settings.", MixerLink.OpenMixer));
        card.Add(actions);

        var meshes = new VisualElement();
        meshes.AddToClassList("mcb-blender__meshes");
        card.Add(meshes);

        List<(SkinnedMeshRenderer Renderer, string TargetUnityPath, string FbxMesh)> renderers = null;
        double lookedUpAt = 0;
        string targetsKey = null;
        string shown = null;
        string failure = null;
        void Stale() => renderers = null;

        void Refresh()
        {
            try
            {
                bool connected = MixerLink.Connected, listening = MixerLink.Listening;
                dot.style.backgroundColor = connected ? Green : listening ? EditorUIUtils.OrangeColor : Grey;
                text.text = connected ? "Blender " + (MixerLink.BlenderVersion ?? string.Empty).Trim() + " is linked: what you edit there shows on the avatar."
                    : listening ? "Waiting for Blender: open the scene in Blender, or connect from the Mixer extension in an open Blender."
                    : "The Blender link is off: turn it on in Mixer's Blender menu.";

                double now = EditorApplication.timeSinceStartup;
                if (renderers == null || now - lookedUpAt > RendererLookupSeconds)
                {
                    renderers = BlenderExportImport.TargetRenderers(editor);
                    lookedUpAt = now;
                    // Blender learns each mesh's target when the scene is sent: send it again once the targets changed.
                    string targets = string.Join("\n", renderers.Select(r => r.Renderer.GetInstanceID() + ">" + r.TargetUnityPath));
                    if (targetsKey != null && targets != targetsKey)
                    {
                        BlenderExportImport.ForgetTargets();
                        MixerLink.RefreshScene();
                    }
                    targetsKey = targets;
                }

                var live = renderers.Where(r => r.Renderer != null).ToList();
                string key = string.Join("\n", live.Select(r => r.Renderer.GetInstanceID() + ":" + MixerLink.StateOf(r.Renderer) + ":" + MixerLink.HasEdits(r.Renderer) + ":" +
                                                                 (r.Renderer.sharedMesh != null ? r.Renderer.sharedMesh.GetInstanceID() : 0)));
                if (key == shown) return;
                shown = key;
                meshes.Clear();
                var committedMeshes = new List<Renderer>();
                var inSession = new List<Renderer>();
                foreach (var entry in live)
                {
                    var renderer = entry.Renderer;
                    var rendererState = MixerLink.StateOf(renderer);
                    bool edits = MixerLink.HasEdits(renderer);
                    bool committed = rendererState == MixerLink.RendererState.Untouched && VersionAvatarMeshes.IsCommitted(renderer);
                    if (committed) committedMeshes.Add(renderer);
                    else if (rendererState != MixerLink.RendererState.Untouched) inSession.Add(renderer);
                    var row = CreatorModeModule.CreateCreatorRow();
                    row.AddToClassList("mcb-blender__mesh");
                    string what = rendererState == MixerLink.RendererState.EditedInBlender ? "being edited in Blender"
                        : rendererState == MixerLink.RendererState.EditedInMixer ? (edits ? "edited in Mixer, not committed" : "open in Mixer")
                        : committed ? "committed in Mixer: the next version build takes it in"
                        : "as imported";
                    var label = CreatorModeModule.CreateLabel(renderer.name + ": " + what, 11, FontStyle.Normal,
                        rendererState == MixerLink.RendererState.Untouched && !committed ? IdleText : LiveText);
                    label.style.flexGrow = 1;
                    row.Add(label);
                    if (rendererState == MixerLink.RendererState.Untouched)
                    {
                        row.Add(CreatorModeModule.CreateTextButton("Edit in Mixer", "Sculpt, paint weights or edit the armature of this mesh in Unity.", () =>
                        {
                            string error = MixerLink.Edit(renderer);
                            if (error != null) McbBlenderCard.Say(editor, error, MessageType.Warning);
                            else refresh?.Invoke();
                        }));
                    }
                    else
                    {
                        var commit = CreatorModeModule.CreateTextButton("Commit",
                            "Saves the edits as a mesh asset shown on the renderer, in one Undo step. Building a version writes it into a new custom FBX of its model.", () =>
                            {
                                var mesh = MixerLink.Commit(renderer);
                                McbBlenderCard.Say(editor, mesh != null
                                    ? "Committed " + renderer.name + " to " + AssetDatabase.GetAssetPath(mesh) + ". It shows on the avatar. " + IntoVersions
                                    : renderer.name + " has nothing to commit.", MessageType.Info);
                            });
                        commit.SetEnabled(edits && rendererState != MixerLink.RendererState.EditedInBlender);
                        row.Add(commit);
                        row.Add(CreatorModeModule.CreateTextButton("Revert",
                            "Puts the renderer's own mesh back and drops the edits not committed (asks first when there are any). A linked Blender gets that mesh's positions, shapes and weights again; while it edits the mesh, it keeps its edits and offers to send them again or take Unity's.", () =>
                            {
                                if (!ConfirmRevert(renderer)) return;
                                MixerLink.Revert(renderer);
                                refresh?.Invoke();
                            }));
                    }
                    meshes.Add(row);
                }
                string said = string.Join(" ", new[] { VersionAvatarMeshes.CommittedMessage(committedMeshes), VersionAvatarMeshes.InSessionMessage(inSession) }
                    .Where(text => text != null));
                if (said.Length > 0)
                {
                    var note = CreatorModeModule.CreateMutedLabel(said);
                    note.AddToClassList("mcb-blender__note");
                    note.style.whiteSpace = WhiteSpace.Normal;
                    meshes.Add(note);
                }
                failure = null;
            }
            catch (Exception ex)
            {
                // Mixer's part only: the rest of the creator form stays, and the same failure is logged once.
                if (ex.Message != failure) MCBLogger.LogWarning("[MCB] The Blender card could not show Mixer's state: " + ex);
                failure = ex.Message;
            }
        }

        Refresh();
        card.schedule.Execute(Refresh).Every(500);
        Action stale = Stale;
        MixerLink.Changed += stale;
        EditorApplication.hierarchyChanged += stale;
        Undo.undoRedoPerformed += new Undo.UndoRedoCallback(stale);
        card.RegisterCallback<DetachFromPanelEvent>(_ =>
        {
            MixerLink.Changed -= stale;
            EditorApplication.hierarchyChanged -= stale;
            Undo.undoRedoPerformed -= new Undo.UndoRedoCallback(stale);
        });
    }
}
#endif
