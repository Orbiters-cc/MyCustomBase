#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Posing;
using UnityEngine;

/// <summary>
/// Tells an avatar's base model apart from the other model files its meshes come from: the base model carries the
/// avatar's own armature, props and add-ons do not (Adjerry91's face tracking debug panel is a bone-less mesh from its own
/// FBX). MCB applies versions to the first base FBX, so the base model must come first and props must stay out.
/// </summary>
public sealed class AvatarBaseModels
{
    // The bones of the avatar's own armature its meshes are skinned to (not its "Armature" container, which every rigged
    // model has, nor the armatures outfits bring).
    private readonly HashSet<Transform> skeleton;
    private readonly HashSet<string> names;

    public AvatarBaseModels(Transform avatarRoot)
    {
        var renderers = avatarRoot != null ? avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true) : new SkinnedMeshRenderer[0];
        // Without a humanoid rig, the armature is the one of the skinned mesh with the most bones.
        var armature = avatarRoot != null
            ? AvatarSkeleton.Bones(avatarRoot, renderers.OrderByDescending(r => r.bones.Length).FirstOrDefault())
            : new HashSet<Transform>();
        skeleton = new HashSet<Transform>(renderers.SelectMany(r => r.bones).Where(b => b != null && armature.Contains(b)));
        names = Names(skeleton);
    }

    /// <summary>
    /// Whether <paramref name="model"/> carries the avatar's skeleton: transforms named like its bones, or meshes in the scene
    /// (<paramref name="sceneRenderers"/>) skinned to them. Without a skeleton to tell them apart, every model does.
    /// </summary>
    public bool Carries(GameObject model, IEnumerable<SkinnedMeshRenderer> sceneRenderers)
    {
        if (skeleton.Count == 0) return true;
        var score = Score(model, sceneRenderers);
        return score.named > 0 || score.skinned > 0;
    }

    /// <summary>
    /// The model files of an avatar's meshes, the base model first: by the skeleton's bones each one has, then by those its
    /// meshes in the scene are skinned to (an outfit exported with the whole armature is skinned to its own copy). Files
    /// carrying none of the skeleton are left out. Without a skeleton, the given order is kept.
    /// </summary>
    public List<string> BaseFirst(IEnumerable<string> paths, Func<string, GameObject> model,
        Func<string, IEnumerable<SkinnedMeshRenderer>> sceneRenderers)
    {
        var candidates = (paths ?? Enumerable.Empty<string>()).ToList();
        if (skeleton.Count == 0) return candidates;
        // OrderByDescending is stable: equal models keep the hierarchy order.
        return candidates.Select(path => (path, score: Score(model(path), sceneRenderers(path))))
            .Where(c => c.score.named > 0 || c.score.skinned > 0)
            .OrderByDescending(c => c.score.named)
            .ThenByDescending(c => c.score.skinned)
            .Select(c => c.path)
            .ToList();
    }

    /// <summary>The model with most of the skeleton of <paramref name="bones"/> (by bone name); null when none has any.</summary>
    public static GameObject WithSkeleton(IEnumerable<GameObject> models, IEnumerable<Transform> bones)
    {
        var boneNames = Names(bones);
        return (models ?? Enumerable.Empty<GameObject>()).Where(m => m != null)
            .Select(m => (model: m, share: Share(m, boneNames)))
            .Where(c => c.share > 0)
            .OrderByDescending(c => c.share)
            .Select(c => c.model)
            .FirstOrDefault();
    }

    private (int named, int skinned) Score(GameObject model, IEnumerable<SkinnedMeshRenderer> sceneRenderers)
    {
        int skinned = (sceneRenderers ?? Enumerable.Empty<SkinnedMeshRenderer>()).Where(r => r != null)
            .SelectMany(r => r.bones).Where(b => b != null && skeleton.Contains(b)).Distinct().Count();
        return (Share(model, names), skinned);
    }

    private static int Share(GameObject model, HashSet<string> boneNames) =>
        model == null || boneNames.Count == 0 ? 0 : model.GetComponentsInChildren<Transform>(true).Count(t => boneNames.Contains(t.name));

    private static HashSet<string> Names(IEnumerable<Transform> bones) =>
        new HashSet<string>((bones ?? Enumerable.Empty<Transform>()).Where(b => b != null).Select(b => b.name), StringComparer.Ordinal);
}
#endif
