using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Lets one custom model take over any original renderer layout without touching clothing: creates the custom
/// renderers an avatar lacks, gives every custom material slot the avatar's material of the same slot name,
/// and hides the original pieces the custom model replaces. Restoring carries the user's material edits back
/// to the original slots of the same name.
/// </summary>
public static class NativeRendererLayoutService
{
    /// <param name="baseModelPaths">The avatar's base models: only renderers of these models lend materials or are hidden.</param>
    public static void Apply(MyCustomBase owner, CustomBaseVersion version, IEnumerable<string> baseModelPaths)
    {
        var layout = VersionCustomization.Read(version?.extraCustomization).rendererLayout;
        if (layout.IsEmpty) return;
        layout.Validate();
        Apply(owner, layout, baseModelPaths);
    }

    internal static void Apply(MyCustomBase owner, RendererLayoutConfiguration layout, IEnumerable<string> baseModelPaths)
    {
        var root = AvatarPaths.Root(owner);
        var models = new HashSet<string>((baseModelPaths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).Select(MCBUtils.ToUnityPath),
            StringComparer.OrdinalIgnoreCase);
        var originals = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh != null && models.Contains(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(r.sharedMesh)))).ToArray();
        Apply(owner, layout, originals.ToDictionary(r => r, MaterialSlotNames.Of));
    }

    /// <param name="originalSlots">The avatar's original base renderers with their material slot names.</param>
    internal static void Apply(MyCustomBase owner, RendererLayoutConfiguration layout, IReadOnlyDictionary<SkinnedMeshRenderer, string[]> originalSlots)
    {
        if (owner.nativeRendererOriginalStates.Count > 0 || owner.nativeGeneratedRenderers.Count > 0)
            throw new InvalidOperationException("Restore the previous renderer layout before applying another.");
        var root = AvatarPaths.Root(owner);
        var originals = originalSlots.Keys.ToArray();

        // Resolve every destination and material before changing the scene.
        var targets = new List<(RendererSlotNames entry, SkinnedMeshRenderer renderer, Transform parent)>();
        foreach (var entry in layout.renderers)
        {
            var renderer = FindRenderer(root, entry.path);
            Transform parent = null;
            if (renderer != null && !originalSlots.ContainsKey(renderer))
                throw new InvalidOperationException("A renderer that is not part of the original model occupies the custom renderer path: " + entry.path);
            if (renderer == null)
            {
                parent = ResolveParent(root, entry.path, originals);
                string name = entry.path.Substring(entry.path.LastIndexOf('/') + 1);
                if (Enumerable.Range(0, parent.childCount).Select(parent.GetChild).Any(t => t.name == name))
                    throw new InvalidOperationException("An object that is not a renderer occupies the custom renderer path: " + entry.path);
            }
            targets.Add((entry, renderer, parent));
        }
        // Prefer each destination's own original slots, then any base renderer declaring the slot name.
        var lenders = targets.Where(t => t.renderer != null && originalSlots.ContainsKey(t.renderer)).Select(t => t.renderer)
            .Concat(originals).Distinct().ToArray();
        Material Lend(string slot, SkinnedMeshRenderer own)
        {
            string key = MaterialSlotNames.Normalize(slot);
            foreach (var lender in own != null && originalSlots.ContainsKey(own) ? new[] { own }.Concat(lenders) : lenders)
            {
                var names = originalSlots[lender];
                var materials = lender.sharedMaterials;
                for (int i = 0; i < names.Length && i < materials.Length; i++)
                    if (materials[i] != null && string.Equals(MaterialSlotNames.Normalize(names[i]), key, StringComparison.OrdinalIgnoreCase)) return materials[i];
            }
            var fallback = layout.fallbacks.FirstOrDefault(f => string.Equals(MaterialSlotNames.Normalize(f.slot), key, StringComparison.OrdinalIgnoreCase));
            return fallback != null ? AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(fallback.material)) : null;
        }
        var assigned = targets.Select(t => t.entry.slots.Select((slot, i) =>
            Lend(slot, t.renderer) ?? (t.renderer != null && i < t.renderer.sharedMaterials.Length ? t.renderer.sharedMaterials[i] : null)).ToArray()).ToArray();
        var unmatched = targets.SelectMany((t, i) => t.entry.slots.Where((slot, s) => assigned[i][s] == null).Select(slot => t.entry.path + " / " + slot)).ToArray();
        if (unmatched.Length > 0)
            MCBLogger.LogWarning("[MCB] No material on this avatar matches these custom material slots: " + string.Join(", ", unmatched) + ". Assign their materials on the renderer.");
        var hidden = originals.Where(r => layout.hide.Contains(r.name) && !targets.Any(t => t.renderer == r)).ToArray();

        Undo.RecordObject(owner, "Track original renderer layout");
        foreach (var renderer in targets.Select(t => t.renderer).Where(r => r != null).Concat(hidden).Distinct())
        {
            owner.nativeRendererOriginalStates.Add(new NativeRendererOriginalState {
                renderer = renderer, materials = renderer.sharedMaterials.ToArray(), enabled = renderer.enabled,
                slots = originalSlots.TryGetValue(renderer, out var slots) ? slots : MaterialSlotNames.Of(renderer) });
            Undo.RecordObject(renderer, "Apply custom renderer layout");
        }
        var donor = targets.Select(t => t.renderer).FirstOrDefault(r => r != null) ?? originals.FirstOrDefault();
        for (int i = 0; i < targets.Count; i++)
        {
            var renderer = targets[i].renderer;
            if (renderer == null)
            {
                var go = new GameObject(targets[i].entry.path.Substring(targets[i].entry.path.LastIndexOf('/') + 1));
                Undo.RegisterCreatedObjectUndo(go, "Create custom renderer");
                go.transform.SetParent(targets[i].parent, false);
                renderer = Undo.AddComponent<SkinnedMeshRenderer>(go);
                if (donor != null)
                {
                    go.layer = donor.gameObject.layer;
                    renderer.rootBone = donor.rootBone;
                    renderer.quality = donor.quality;
                    renderer.shadowCastingMode = donor.shadowCastingMode;
                    renderer.receiveShadows = donor.receiveShadows;
                    renderer.updateWhenOffscreen = donor.updateWhenOffscreen;
                    renderer.probeAnchor = donor.probeAnchor;
                }
                owner.nativeGeneratedRenderers.Add(go);
            }
            renderer.sharedMaterials = assigned[i];
            EditorUtility.SetDirty(renderer);
        }
        foreach (var renderer in hidden) { renderer.enabled = false; EditorUtility.SetDirty(renderer); }
        EditorUtility.SetDirty(owner);
    }

    /// <summary>Restores hidden pieces and original materials, keeping materials the user changed on custom slots.</summary>
    public static void Restore(MyCustomBase owner)
    {
        if (owner.nativeRendererOriginalStates.Count == 0 && owner.nativeGeneratedRenderers.Count == 0) return;
        var edited = CollectEditedSlots(owner, owner.appliedCustomization?.rendererLayout ?? new RendererLayoutConfiguration());
        Undo.RecordObject(owner, "Restore original renderer layout");
        foreach (var go in owner.nativeGeneratedRenderers.AsEnumerable().Reverse())
            if (go != null) Undo.DestroyObjectImmediate(go);
        foreach (var state in owner.nativeRendererOriginalStates)
        {
            if (state.renderer == null) continue;
            Undo.RecordObject(state.renderer, "Restore original renderer materials");
            var materials = state.materials.ToArray();
            for (int i = 0; i < materials.Length && state.slots != null && i < state.slots.Length; i++)
                if (edited.TryGetValue(MaterialSlotNames.Normalize(state.slots[i]), out var material)) materials[i] = material;
            state.renderer.sharedMaterials = materials;
            state.renderer.enabled = state.enabled;
            EditorUtility.SetDirty(state.renderer);
        }
        owner.nativeGeneratedRenderers.Clear();
        owner.nativeRendererOriginalStates.Clear();
        EditorUtility.SetDirty(owner);
    }

    // A custom slot whose material differs from the one the original slot had was edited by the user.
    private static Dictionary<string, Material> CollectEditedSlots(MyCustomBase owner, RendererLayoutConfiguration layout)
    {
        var edited = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        var originalBySlot = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in owner.nativeRendererOriginalStates.Where(s => s.slots != null))
            for (int i = 0; i < state.slots.Length && i < state.materials.Length; i++)
            {
                string key = MaterialSlotNames.Normalize(state.slots[i]);
                if (!originalBySlot.ContainsKey(key) && state.materials[i] != null) originalBySlot[key] = state.materials[i];
            }
        var fallbacks = new HashSet<string>(layout.fallbacks.Select(f => f.material), StringComparer.OrdinalIgnoreCase);
        var root = AvatarPaths.Root(owner);
        foreach (var entry in layout.renderers)
        {
            var renderer = FindRenderer(root, entry.path);
            if (renderer == null) continue;
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < entry.slots.Count && i < materials.Length; i++)
            {
                var material = materials[i];
                string key = MaterialSlotNames.Normalize(entry.slots[i]);
                if (material == null || edited.ContainsKey(key) || !originalBySlot.ContainsKey(key) || originalBySlot[key] == material) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long _) && fallbacks.Contains(guid)) continue;
                edited[key] = material;
            }
        }
        return edited;
    }

    private static SkinnedMeshRenderer FindRenderer(Transform root, string path)
    {
        var exact = root.Find(path);
        if (exact != null) return exact.GetComponent<SkinnedMeshRenderer>();
        // The avatar may sit under an organizer object: accept one renderer whose path ends with the recorded one.
        string suffix = "/" + path;
        var matches = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => ("/" + AnimationUtility.CalculateTransformPath(r.transform, root)).EndsWith(suffix, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static Transform ResolveParent(Transform root, string path, IReadOnlyList<SkinnedMeshRenderer> originals)
    {
        int slash = path.LastIndexOf('/');
        if (slash >= 0)
        {
            var parent = root.Find(path.Substring(0, slash));
            if (parent == null) throw new InvalidOperationException("Missing parent for custom renderer: " + path);
            return parent;
        }
        // Top-level custom renderers join the original model's top-level pieces.
        var sibling = originals.FirstOrDefault(r => r.transform.parent != null && r.transform.parent.GetComponent<Animator>() != null) ?? originals.FirstOrDefault();
        return sibling != null ? sibling.transform.parent : root;
    }
}
