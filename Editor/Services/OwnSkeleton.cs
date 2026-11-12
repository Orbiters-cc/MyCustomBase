#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The avatar's own skeleton and renderers, never an outfit's: one resolver for every place that maps a base model onto
/// an avatar. An avatar can carry a full copy of its base model as clothing (its own "Armature" and "Body"), clothing bones
/// nested in its armature, renamed or moved renderers: names alone are never trusted.
/// </summary>
public sealed class OwnSkeleton
{
    public Transform Root { get; private set; }
    /// <summary>
    /// The avatar's own armature: the ancestor of its Hips as many levels up as the model's armature is above the model's
    /// Hips, wherever it sits (a direct child of the root, or deeper: root/Model/Armature); null when none can be told apart.
    /// </summary>
    public Transform Armature { get; private set; }
    /// <summary>Model transform -> avatar transform, for every model transform that maps.</summary>
    public readonly Dictionary<Transform, Transform> Map = new Dictionary<Transform, Transform>();
    /// <summary>Model paths that have no counterpart on the avatar (no renderer needs them).</summary>
    public readonly List<string> Skipped = new List<string>();
    /// <summary>Why the model cannot be mapped (null when it can).</summary>
    public string Failure { get; private set; }
    /// <summary>The avatar renderers that stand for the model's renderers.</summary>
    public readonly Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer> Renderers = new Dictionary<SkinnedMeshRenderer, SkinnedMeshRenderer>();

    private readonly HashSet<Transform> baseWeighted = new HashSet<Transform>();
    private readonly HashSet<Transform> humanChain = new HashSet<Transform>();

    private static readonly Regex DuplicateSuffix = new Regex(@"(\.\d{3}| \(\d+\))$", RegexOptions.CultureInvariant);

    public static string BaseName(string name) => name == null ? null : DuplicateSuffix.Replace(name, "");

    /// <summary>The avatar transform for a model transform (null when it does not map).</summary>
    public Transform this[Transform model] => model != null && Map.TryGetValue(model, out var t) ? t : null;

    /// <summary>
    /// Maps <paramref name="modelRoot"/> (a base model's root) onto the avatar. Each model transform is found under its
    /// already mapped parent; a bone moved elsewhere is found only inside the own armature, outside nested outfit copies,
    /// preferring bones the base renderers use. Remaining ambiguity is a failure, never a first-found pick. A model
    /// transform a base renderer is skinned to that cannot be mapped is a failure too.
    /// </summary>
    public static OwnSkeleton Resolve(Transform avatarRoot, Transform modelRoot)
    {
        var result = new OwnSkeleton { Root = avatarRoot };
        if (avatarRoot == null || modelRoot == null)
        {
            result.Failure = "The avatar or its base model could not be loaded.";
            return result;
        }
        result.ResolveInternal(modelRoot);
        return result;
    }

    private static readonly Dictionary<Transform, OwnSkeleton> Scoped = new Dictionary<Transform, OwnSkeleton>();
    private static int scopeDepth;

    private sealed class ScopeEnd : IDisposable
    {
        private bool done;
        public void Dispose() { if (done) return; done = true; if (--scopeDepth == 0) Scoped.Clear(); }
    }

    /// <summary>Within one synchronous phase (a payload applied), the avatar's own armature is found once.</summary>
    public static IDisposable Scope() { scopeDepth++; return new ScopeEnd(); }

    /// <summary>Only the own armature (no model): for payload bones and generic lookups.</summary>
    public static OwnSkeleton ForAvatar(Transform avatarRoot)
    {
        if (avatarRoot != null && scopeDepth > 0 && Scoped.TryGetValue(avatarRoot, out var known)) return known;
        var built = BuildForAvatar(avatarRoot);
        if (avatarRoot != null && scopeDepth > 0) Scoped[avatarRoot] = built;
        return built;
    }

    private static OwnSkeleton BuildForAvatar(Transform avatarRoot)
    {
        var result = new OwnSkeleton { Root = avatarRoot };
        if (avatarRoot == null) return result;
        result.CollectHumanChain();
        result.Armature = result.FindOwnArmature(null, OwnerModelLevels(avatarRoot));
        if (result.Armature != null)
            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (result.IsOwnRenderer(renderer))
                    foreach (var bone in renderer.bones) if (bone != null) result.baseWeighted.Add(bone);
        return result;
    }

    private void ResolveInternal(Transform modelRoot)
    {
        CollectHumanChain();
        var modelRenderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
        // Renderers exactly at the model's renderer paths: what tells the own armature apart when the Animator cannot.
        var atPaths = new List<SkinnedMeshRenderer>();
        foreach (var source in modelRenderers)
        {
            var at = FindByPath(Root, Path(modelRoot, source.transform));
            var renderer = at != null ? at.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null) atPaths.Add(renderer);
        }
        // Renderers moved elsewhere (under a "Model" object): those named like the model's tell the branch.
        if (atPaths.Count == 0)
        {
            var names = new HashSet<string>(modelRenderers.Select(r => BaseName(r.name)));
            atPaths.AddRange(Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => names.Contains(BaseName(r.name))));
        }
        int levels = ModelLevels(modelRoot, modelRenderers);
        Armature = FindOwnArmature(atPaths, levels);
        // Without a humanoid Hips the branch was chosen by the bones the body uses: the armature itself sits as many levels
        // above the model's Hips as in the model, inside that branch (root/Model/Armature).
        var modelHips = ModelHips(modelRoot) ?? ModelHipsByName(modelRoot, modelRenderers);
        if (Armature != null && levels > 0 && modelHips != null)
        {
            var hips = Armature.GetComponentsInChildren<Transform>(true).Where(t => t.name == modelHips.name && !InOutfitCopy(t)).Take(2).ToList();
            if (hips.Count == 1)
            {
                var above = Above(hips[0], levels);
                if (above != null && (above == Armature || above.IsChildOf(Armature))) Armature = above;
            }
        }
        foreach (var renderer in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (IsOwnRenderer(renderer)) foreach (var bone in renderer.bones) if (bone != null) baseWeighted.Add(bone);

        var modelArmature = ModelArmature(modelRoot, modelRenderers);
        var needed = new HashSet<Transform>(modelRenderers.SelectMany(r => r.bones.Append(r.rootBone)).Where(b => b != null && b != modelRoot));
        // Nothing tells the armature apart (no humanoid, no skinned body): the one child where the model has its armature.
        if (Armature == null && modelArmature != null) Armature = Pick(Root, modelArmature.name, modelArmature.name, out _);
        // The renderers first: a moved or renamed body is found by what it is, not by its path alone.
        foreach (var source in modelRenderers)
        {
            var renderer = ResolveRenderer(Path(modelRoot, source.transform), out string failure);
            if (failure != null) { Failure = failure; return; }
            if (renderer != null) Renderers[source] = renderer;
        }

        Map[modelRoot] = Root;
        var used = new HashSet<Transform> { Root };
        foreach (var source in modelRoot.GetComponentsInChildren<Transform>(true))
        {
            if (source == modelRoot) continue;
            string path = Path(modelRoot, source);
            var smr = source.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && Renderers.TryGetValue(smr, out var own))
            {
                if (used.Add(own.transform)) Map[source] = own.transform;
                continue;
            }
            if (!Map.TryGetValue(source.parent, out var parent))
            {
                if (needed.Contains(source)) { Failure = Unmapped(path); return; }
                Skipped.Add(path);
                continue;
            }
            Transform target;
            // The model's armature is the avatar's own one whatever it is named ("Armature.004"), unless it is itself a bone
            // the body is skinned to: then its name must match.
            if (source == modelArmature && Armature != null && (!needed.Contains(source) || BaseName(Armature.name) == BaseName(source.name))) target = Armature;
            else
            {
                target = Pick(parent, source.name, path, out string failure);
                if (failure != null) { Failure = failure; return; }
                if (target == null && Armature != null && (parent == Armature || parent.IsChildOf(Armature)))
                {
                    target = MovedElsewhere(source.name, used, path, out failure);
                    if (failure != null) { Failure = failure; return; }
                }
            }
            if (target == null || !used.Add(target))
            {
                if (needed.Contains(source)) { Failure = Unmapped(path); return; }
                Skipped.Add(path);
                continue;
            }
            Map[source] = target;
        }
    }

    private static string Unmapped(string path) =>
        $"Your avatar has no bone matching '{path}' of the base model, and the body is skinned to it. Nothing was changed. "
        + "Put the bone back (or undo the rename) and try again.";

    // The child of that name; same-named siblings are told apart by the humanoid chain and the base renderers' bones.
    private Transform Pick(Transform parent, string name, string path, out string failure)
    {
        failure = null;
        var children = Enumerable.Range(0, parent.childCount).Select(parent.GetChild).Where(c => c.name == name).ToList();
        if (children.Count <= 1) return children.FirstOrDefault();
        var preferred = children.Where(c => humanChain.Contains(c) || baseWeighted.Contains(c)).ToList();
        if (preferred.Count == 1) return preferred[0];
        failure = Ambiguous(children.Take(2));
        return null;
    }

    private Transform MovedElsewhere(string name, HashSet<Transform> used, string path, out string failure)
    {
        failure = null;
        var candidates = Armature.GetComponentsInChildren<Transform>(true)
            .Where(t => t != Armature && t.name == name && !used.Contains(t) && !InOutfitCopy(t)).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();
        var weighted = candidates.Where(baseWeighted.Contains).ToList();
        if (weighted.Count == 1) return weighted[0];
        failure = Ambiguous(candidates.Take(2));
        return null;
    }

    private string Ambiguous(IEnumerable<Transform> found)
    {
        var paths = found.Select(t => "'" + Path(Root, t) + "'").ToArray();
        return $"Your avatar has two objects that could be the same bone of its base: {string.Join(" and ", paths)}. Nothing was changed. "
            + "Rename the one that belongs to clothing or an accessory (or move it out of the armature) and try again.";
    }

    /// <summary>
    /// A transform inside a nested copy of a skeleton (an outfit's armature parented into the avatar's): some node between
    /// it and the own armature repeats the armature's name or one of its own ancestors' names, or is the root of another
    /// prefab instance than the avatar's own.
    /// </summary>
    public bool InOutfitCopy(Transform t)
    {
        if (Armature == null || t == null || !t.IsChildOf(Armature) || t == Armature) return false;
        var ownInstance = PrefabUtility.GetNearestPrefabInstanceRoot(Armature.gameObject);
        for (var node = t; node != null && node != Armature; node = node.parent)
        {
            if (BaseName(node.name) == BaseName(Armature.name)) return true;
            for (var ancestor = node.parent; ancestor != null; ancestor = ancestor.parent)
            {
                if (ancestor.name == node.name) return true;
                if (ancestor == Armature) break;
            }
            if (PrefabUtility.IsAnyPrefabInstanceRoot(node.gameObject) && node.gameObject != ownInstance) return true;
        }
        return false;
    }

    /// <summary>A renderer of the avatar's own: outside every top-level branch but the root's renderers, skinned mostly to own-armature bones.</summary>
    public bool IsOwnRenderer(SkinnedMeshRenderer renderer)
    {
        if (renderer == null || Armature == null) return false;
        var bones = (renderer.bones ?? Array.Empty<Transform>()).Where(b => b != null).ToList();
        if (bones.Count == 0) return false;
        if (InsideOutfitBranch(renderer.transform)) return false;
        int own = bones.Count(b => (b == Armature || b.IsChildOf(Armature)) && !InOutfitCopy(b));
        return own * 2 > bones.Count;
    }

    // A renderer under a branch that holds its own skeleton copy (an "Outfit" child with its own Armature).
    private bool InsideOutfitBranch(Transform t)
    {
        for (var node = t.parent; node != null && node != Root; node = node.parent)
        {
            if (node == Armature) return false;
            if (node.parent == Root || node.IsChildOf(Root))
                for (int i = 0; i < node.childCount; i++)
                {
                    var child = node.GetChild(i);
                    if (child != t && child != Armature && Armature != null && (child.name == Armature.name || BaseName(child.name) == BaseName(Armature.name))
                        && child.GetComponentsInChildren<Transform>(true).Length > 1) return true;
                }
        }
        return false;
    }

    /// <summary>
    /// The avatar renderer for a base renderer path: the renderer at that path; else the one renderer of the avatar's own
    /// (skinned mostly to own-armature bones, outside outfits) whose object or mesh is named like it (Blender/Unity
    /// duplicate suffixes allowed), used where it is. Several candidates are a failure; none is null (absent).
    /// </summary>
    public SkinnedMeshRenderer ResolveRenderer(string path, out string failure)
    {
        failure = null;
        if (Root == null || string.IsNullOrEmpty(path)) return null;
        var at = FindByPath(Root, path);
        var renderer = at != null ? at.GetComponent<SkinnedMeshRenderer>() : null;
        if (renderer != null && (Armature == null || IsOwnRenderer(renderer) || renderer.bones.All(b => b == null))) return renderer;
        string name = BaseName(path.Substring(path.LastIndexOf('/') + 1));
        var candidates = Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r != renderer && (IsOwnRenderer(r) || r.bones.All(b => b == null) && !InsideOutfitBranch(r.transform))
                        && (BaseName(r.name) == name || r.sharedMesh != null && BaseName(r.sharedMesh.name) == name)).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
        {
            failure = $"Your avatar has several bodies that could be its '{name}': {string.Join(", ", candidates.Select(c => "'" + Path(Root, c.transform) + "'"))}. "
                + "Nothing was changed. Rename the ones that belong to clothing and try again.";
            return null;
        }
        return renderer;
    }

    /// <summary>The own-armature transform for a bone path of a version's skeleton (null when the avatar lacks it).</summary>
    public Transform ResolveBone(string path, Func<Transform, bool> prefer = null)
    {
        if (Root == null || string.IsNullOrWhiteSpace(path)) return null;
        var byPath = FindByPath(Root, path);
        if (byPath != null && (Armature == null || byPath == Armature || byPath.IsChildOf(Armature) && !InOutfitCopy(byPath))) return byPath;
        if (Armature == null) return null;
        string name = path.Substring(path.LastIndexOf('/') + 1).Trim();
        if (path.IndexOf('/') < 0 && BaseName(name) == BaseName(Armature.name)) return Armature;
        var candidates = Armature.GetComponentsInChildren<Transform>(true).Where(t => t != Armature && t.name == name && !InOutfitCopy(t)).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();
        var weighted = candidates.Where(t => baseWeighted.Contains(t) || (prefer != null && prefer(t))).ToList();
        return weighted.Count == 1 ? weighted[0] : null;
    }

    private void CollectHumanChain()
    {
        var animator = Root.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman) return;
        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (bone == HumanBodyBones.LastBone) continue;
            Transform t = null;
            try { t = animator.GetBoneTransform(bone); } catch (Exception) { }
            if (t != null && t.IsChildOf(Root)) humanChain.Add(t);
        }
    }

    // The branch holding the Animator's humanoid Hips (as many levels above them as the model has its armature), unless
    // the base renderers are skinned to another branch; otherwise the branch holding most of the base renderers' bones.
    // Never chosen by its name.
    private Transform FindOwnArmature(List<SkinnedMeshRenderer> baseRenderers, int levels)
    {
        Transform hipsBranch = null;
        var animator = Root.GetComponent<Animator>();
        if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
        {
            Transform hips = null;
            try { hips = animator.GetBoneTransform(HumanBodyBones.Hips); } catch (Exception) { }
            if (hips != null && hips != Root && hips.IsChildOf(Root)) hipsBranch = Above(hips, levels);
        }
        if (hipsBranch != null && hipsBranch.parent != Root)
        {
            // Deeper than a child of the root: the base renderers' bones decide only whether it holds them.
            var bones = (baseRenderers ?? Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToList())
                .SelectMany(r => r.bones).Where(b => b != null && b != Root && b.IsChildOf(Root)).Distinct().ToList();
            if (bones.Count == 0 || bones.Count(b => b == hipsBranch || b.IsChildOf(hipsBranch)) * 2 > bones.Count) return hipsBranch;
        }
        var counts = new Dictionary<Transform, int>();
        foreach (var renderer in baseRenderers ?? Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.transform.parent == Root).ToList())
            foreach (var bone in renderer.bones.Where(b => b != null && b != Root && b.IsChildOf(Root)).Distinct())
            {
                var top = TopLevel(Root, bone);
                counts[top] = counts.TryGetValue(top, out int n) ? n + 1 : 1;
            }
        if (hipsBranch != null && (counts.Count == 0 || counts.ContainsKey(hipsBranch))) return hipsBranch;
        if (counts.Count == 0) return hipsBranch;
        var best = counts.OrderByDescending(p => p.Value).ToList();
        if (best.Count > 1 && best[0].Value == best[1].Value) return hipsBranch;
        return best[0].Key;
    }

    // The ancestor of the avatar's Hips that stands for the model's armature: as many levels up as in the model (the top-level
    // branch when that is unknown or would leave the avatar).
    private Transform Above(Transform hips, int levels)
    {
        if (levels <= 0) return TopLevel(Root, hips);
        var node = hips;
        for (int i = 0; i < levels && node != null && node != Root; i++) node = node.parent;
        return node == null || node == Root ? TopLevel(Root, hips) : node;
    }

    // How many levels the model's armature is above the model's humanoid Hips (0 when unknown).
    private static int ModelLevels(Transform modelRoot, List<SkinnedMeshRenderer> renderers)
    {
        var hips = ModelHips(modelRoot) ?? ModelHipsByName(modelRoot, renderers);
        var armature = ModelArmature(modelRoot, renderers);
        if (hips == null || armature == null || !hips.IsChildOf(armature)) return 0;
        int levels = 0;
        for (var node = hips; node != null && node != armature; node = node.parent) levels++;
        return levels;
    }

    // For an avatar alone: its MyCustomBase's original base model tells the levels.
    private static int OwnerModelLevels(Transform avatarRoot)
    {
        var owner = avatarRoot.GetComponentInChildren<MyCustomBase>(true);
        var model = owner == null ? null : (owner.versionOriginalModels != null && owner.versionOriginalModels.Count > 0 ? owner.versionOriginalModels : owner.baseFbxFiles)
            ?.FirstOrDefault(m => m != null);
        return model == null ? 0 : ModelLevels(model.transform, model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList());
    }

    // A model without a humanoid Avatar: the first child of its armature that holds bones (its root bone).
    private static Transform ModelHipsByName(Transform modelRoot, List<SkinnedMeshRenderer> renderers)
    {
        var armature = ModelArmature(modelRoot, renderers);
        if (armature == null) return null;
        var children = Enumerable.Range(0, armature.childCount).Select(armature.GetChild).Where(c => c.childCount > 0).ToList();
        return children.Count == 1 ? children[0] : null;
    }

    private static Transform ModelHips(Transform modelRoot)
    {
        var animator = modelRoot.GetComponent<Animator>();
        string hipsName = animator != null && animator.avatar != null && animator.avatar.isHuman
            ? animator.avatar.humanDescription.human.Where(h => h.humanName == HumanTrait.BoneName[(int)HumanBodyBones.Hips]).Select(h => h.boneName).FirstOrDefault()
            : null;
        if (string.IsNullOrEmpty(hipsName)) return null;
        var hips = modelRoot.GetComponentsInChildren<Transform>(true).Where(t => t.name == hipsName).Take(2).ToList();
        return hips.Count == 1 ? hips[0] : null;
    }

    // The model's armature: the top-level branch holding its humanoid Hips (by its Avatar's mapping), else most bones.
    private static Transform ModelArmature(Transform modelRoot, List<SkinnedMeshRenderer> renderers)
    {
        var animator = modelRoot.GetComponent<Animator>();
        string hipsName = animator != null && animator.avatar != null && animator.avatar.isHuman
            ? animator.avatar.humanDescription.human.Where(h => h.humanName == HumanTrait.BoneName[(int)HumanBodyBones.Hips]).Select(h => h.boneName).FirstOrDefault()
            : null;
        if (!string.IsNullOrEmpty(hipsName))
        {
            var hips = modelRoot.GetComponentsInChildren<Transform>(true).Where(t => t.name == hipsName).Take(2).ToList();
            if (hips.Count == 1 && hips[0] != modelRoot) return TopLevel(modelRoot, hips[0]);
        }
        return renderers.SelectMany(r => r.bones).Where(b => b != null && b != modelRoot && b.IsChildOf(modelRoot))
            .GroupBy(b => TopLevel(modelRoot, b)).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            // A bare skeleton: its deepest top-level branch.
            ?? Enumerable.Range(0, modelRoot.childCount).Select(modelRoot.GetChild).Where(c => c.childCount > 0)
                .OrderByDescending(c => c.GetComponentsInChildren<Transform>(true).Length).FirstOrDefault();
    }

    public static Transform TopLevel(Transform root, Transform t)
    {
        while (t != null && t.parent != root) t = t.parent;
        return t;
    }

    /// <summary>A path where every segment names exactly one child (a duplicated name never resolves to the first).</summary>
    public static Transform FindByPath(Transform root, string path)
    {
        if (root == null || path == null) return null;
        var current = root;
        foreach (string raw in path.Split('/'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            Transform found = null;
            for (int i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (child.name != part) continue;
                if (found != null) return null;
                found = child;
            }
            if (found == null) return null;
            current = found;
        }
        return current;
    }

    public static string Path(Transform root, Transform t)
    {
        if (t == null || root == null || t == root) return "";
        var parts = new List<string>();
        for (var node = t; node != null && node != root; node = node.parent) parts.Add(node.name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}
#endif
