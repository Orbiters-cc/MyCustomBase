#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCBEditorUtils;

// Blendshape weights across a switch: captured, remembered per version, restored, and the version's defaults.
public partial class VersionActions
{
    private Dictionary<string, Dictionary<string, float>> CaptureBlendshapeState(Transform root)
    {
        var snapshot = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
        if (root == null) return snapshot;

        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.sharedMesh == null) continue;

            var weights = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                string blendshapeName = renderer.sharedMesh.GetBlendShapeName(i);
                if (string.IsNullOrEmpty(blendshapeName)) continue;
                weights[blendshapeName] = renderer.GetBlendShapeWeight(i);
            }

            snapshot[GetRelativeTransformPath(root, renderer.transform)] = weights;
        }

        return snapshot;
    }

    private void RestoreBlendshapeState(Transform root, Dictionary<string, Dictionary<string, float>> snapshot, Dictionary<string, float> defaultValuesByName)
    {
        if (root == null) return;

        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.sharedMesh == null) continue;

            string rendererPath = GetRelativeTransformPath(root, renderer.transform);
            snapshot.TryGetValue(rendererPath, out var savedWeightsForRenderer);

            Undo.RecordObject(renderer, "Restore Blendshape Values");
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                string blendshapeName = renderer.sharedMesh.GetBlendShapeName(i);
                float valueToApply = 0f;

                if (!string.IsNullOrEmpty(blendshapeName))
                {
                    if (savedWeightsForRenderer != null && savedWeightsForRenderer.TryGetValue(blendshapeName, out var savedValue))
                    {
                        valueToApply = savedValue;
                    }
                    else if (defaultValuesByName != null && defaultValuesByName.TryGetValue(blendshapeName, out var defaultValue))
                    {
                        valueToApply = defaultValue;
                    }
                }

                renderer.SetBlendShapeWeight(i, valueToApply);
            }

            EditorUtility.SetDirty(renderer);
        }
    }

    private Dictionary<string, float> BuildBlendshapeDefaultLookup(CustomBaseVersion version, Dictionary<string, float> remembered = null)
    {
        var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        if (version?.customBlendshapes == null) return defaults;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null || string.IsNullOrEmpty(entry.name)) continue;
            defaults[entry.name] = remembered != null && remembered.TryGetValue(entry.name, out float value)
                ? value : ParseBlendshapeDefaultValue(entry.defaultValue);
        }

        return defaults;
    }

    /// <summary>Saves the applied version's exposed blendshape values for this asset before the avatar returns to its original base.</summary>
    private void RememberVersionBlendshapes(Transform root, CustomBaseVersion version)
    {
        var owner = session.customBaseTarget;
        if (root == null || owner == null || version?.customBlendshapes == null || version.assetId <= 0) return;
        var renderers = GetTargetBlendshapeRenderers(root).OrderByDescending(r => r.name == "Body").ToList();
        // Merged into what the asset already remembers: a migrated legacy release's own shapes ("arcane correction") are
        // no sliders of the version but must come back on the next install too.
        var existing = owner.blendshapeMemory.FirstOrDefault(m => m.assetId == version.assetId);
        var names = new List<string>(existing?.names ?? new List<string>());
        names.AddRange(version.customBlendshapes.Where(e => e != null && !string.IsNullOrEmpty(e.name)).Select(e => e.name).Where(n => !names.Contains(n)));
        var memory = new BlendshapeMemory { assetId = version.assetId };
        for (int i = 0; i < names.Count; i++)
        {
            var renderer = renderers.FirstOrDefault(r => r.sharedMesh.GetBlendShapeIndex(names[i]) >= 0);
            int old = existing?.names.IndexOf(names[i]) ?? -1;
            if (renderer == null && (old < 0 || old >= existing.values.Count)) continue;
            memory.names.Add(names[i]);
            memory.values.Add(renderer != null ? renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(names[i])) : existing.values[old]);
        }
        if (memory.names.Count == 0) return;
        Undo.RecordObject(owner, "Remember custom base blendshapes");
        owner.blendshapeMemory.RemoveAll(m => m.assetId == version.assetId);
        owner.blendshapeMemory.Add(memory);
        EditorUtility.SetDirty(owner);
    }

    // Memory 0 holds the original base's own shape values, saved when the avatar leaves it.
    private const int OriginalBaseMemoryId = 0;

    private void RememberOriginalBaseBlendshapes(Transform root, Dictionary<string, Dictionary<string, float>> snapshot)
    {
        var owner = session.customBaseTarget;
        if (root == null || owner == null || snapshot == null) return;
        var memory = new BlendshapeMemory { assetId = OriginalBaseMemoryId };
        // The body's values first when two renderers share a shape name.
        foreach (var pair in snapshot.OrderByDescending(p => p.Key == "Body" || p.Key.EndsWith("/Body", StringComparison.Ordinal)))
            foreach (var weight in pair.Value)
                if (!memory.names.Contains(weight.Key)) { memory.names.Add(weight.Key); memory.values.Add(weight.Value); }
        if (memory.names.Count == 0) return;
        Undo.RecordObject(owner, "Remember original blendshapes");
        owner.blendshapeMemory.RemoveAll(m => m.assetId == OriginalBaseMemoryId);
        owner.blendshapeMemory.Add(memory);
        EditorUtility.SetDirty(owner);
    }

    /// <summary>
    /// Back on the original meshes, each shape is set by name (Unity would keep the version's values by slot index):
    /// the version's own shapes return to the original base's saved values, the others keep the person's value, and a
    /// shape the version lacks gets its saved value (else 0).
    /// </summary>
    private void RestoreOriginalBaseBlendshapes(Transform root, Dictionary<string, Dictionary<string, float>> snapshot, CustomBaseVersion previousVersion, bool keepValues)
    {
        if (root == null || snapshot == null) return;
        var original = new Dictionary<string, float>(StringComparer.Ordinal);
        var memory = session.customBaseTarget?.blendshapeMemory.FirstOrDefault(m => m.assetId == OriginalBaseMemoryId);
        for (int i = 0; memory != null && i < memory.names.Count && i < memory.values.Count; i++) original[memory.names[i]] = memory.values[i];
        var owned = new HashSet<string>((previousVersion?.customBlendshapes ?? new CustomBlendshapeEntry[0]).Where(e => e != null && !string.IsNullOrEmpty(e.name)).Select(e => e.name), StringComparer.Ordinal);
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null || renderer.sharedMesh == null) continue;
            if (!snapshot.TryGetValue(GetRelativeTransformPath(root, renderer.transform), out var current)) continue;
            Undo.RecordObject(renderer, "Restore Blendshape Values");
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
            {
                string name = renderer.sharedMesh.GetBlendShapeName(i);
                bool hasOriginal = original.TryGetValue(name, out float saved);
                float value = keepValues && !(owned.Contains(name) && hasOriginal) && current.TryGetValue(name, out float now) ? now : hasOriginal ? saved : 0f;
                renderer.SetBlendShapeWeight(i, value);
            }
            EditorUtility.SetDirty(renderer);
        }
    }

    private Dictionary<string, float> RememberedBlendshapes(CustomBaseVersion version)
    {
        var memory = session.customBaseTarget?.blendshapeMemory.FirstOrDefault(m => version != null && m.assetId == version.assetId);
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int i = 0; memory != null && i < memory.names.Count && i < memory.values.Count; i++) result[memory.names[i]] = memory.values[i];
        return result;
    }

    private void SyncBlendshapeOverridesFromCurrentWeights(Transform root, CustomBaseVersion version)
    {
        session.customBaseTarget.customBlendshapeOverrideNames.Clear();
        session.customBaseTarget.customBlendshapeOverrideValues.Clear();
        session.customBaseTarget.blendShapeValues.Clear();

        if (root == null || version?.customBlendshapes == null) return;

        var renderers = GetTargetBlendshapeRenderers(root).ToList();
        if (renderers.Count == 0) return;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null)
            {
                session.customBaseTarget.blendShapeValues.Add(0f);
                continue;
            }

            float defaultValue = ParseBlendshapeDefaultValue(entry.defaultValue);
            float currentValue = defaultValue;
            foreach (var renderer in renderers)
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(entry.name);
                if (index >= 0)
                {
                    currentValue = renderer.GetBlendShapeWeight(index);
                    break;
                }
            }

            session.customBaseTarget.blendShapeValues.Add(currentValue);

            if (Mathf.Abs(currentValue - defaultValue) > BlendshapeWeightEpsilon)
            {
                session.customBaseTarget.customBlendshapeOverrideNames.Add(entry.name);
                session.customBaseTarget.customBlendshapeOverrideValues.Add(currentValue);
            }
        }
    }

    private void ApplyVersionBlendshapeValues(Transform root, CustomBaseVersion version)
    {
        session.customBaseTarget.blendShapeValues.Clear();

        if (root == null || version?.customBlendshapes == null || version.customBlendshapes.Length == 0) return;

        var renderers = GetTargetBlendshapeRenderers(root).ToList();
        if (renderers.Count == 0) return;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null)
            {
                session.customBaseTarget.blendShapeValues.Add(0f);
                continue;
            }

            float defaultValue = ParseBlendshapeDefaultValue(entry.defaultValue);
            float valueToApply = RememberedBlendshapes(version).TryGetValue(entry.name, out float remembered) ? remembered : defaultValue;
            int overrideIdx = session.customBaseTarget.customBlendshapeOverrideNames.IndexOf(entry.name);
            if (overrideIdx >= 0 && overrideIdx < session.customBaseTarget.customBlendshapeOverrideValues.Count)
            {
                valueToApply = session.customBaseTarget.customBlendshapeOverrideValues[overrideIdx];
            }

            MCBReFitIntegration.ApplyBlendShapeWeightWithTransferredReFit(
                session.customBaseTarget,
                renderers,
                entry.name,
                valueToApply);

            session.customBaseTarget.blendShapeValues.Add(valueToApply);
        }

    }

    private void SyncReFitTransferredBlendshapeWeights(Transform root, CustomBaseVersion version)
    {
        if (root == null || version?.customBlendshapes == null || version.customBlendshapes.Length == 0) return;

        var renderers = GetTargetBlendshapeRenderers(root).ToList();
        if (renderers.Count == 0) return;

        foreach (var entry in version.customBlendshapes)
        {
            if (entry == null || string.IsNullOrEmpty(entry.name)) continue;
            if (!TryGetCurrentBlendshapeWeight(renderers, entry.name, out float currentWeight)) continue;
            MCBReFitIntegration.ApplyBlendShapeWeightWithTransferredReFit(
                session.customBaseTarget,
                renderers,
                entry.name,
                currentWeight);
        }
    }

    private static bool TryGetCurrentBlendshapeWeight(IEnumerable<SkinnedMeshRenderer> renderers, string blendshapeName, out float weight)
    {
        weight = 0f;
        if (renderers == null || string.IsNullOrEmpty(blendshapeName)) return false;

        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.sharedMesh == null) continue;
            int index = renderer.sharedMesh.GetBlendShapeIndex(blendshapeName);
            if (index < 0) continue;
            weight = renderer.GetBlendShapeWeight(index);
            return true;
        }

        return false;
    }

    private IEnumerable<SkinnedMeshRenderer> GetTargetBlendshapeRenderers(Transform root)
    {
        if (root == null) yield break;

        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer?.sharedMesh == null || renderer.sharedMesh.blendShapeCount == 0) continue;
            yield return renderer;
        }
    }

    private static float ParseBlendshapeDefaultValue(string defaultValue)
    {
        return float.TryParse(defaultValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : 0f;
    }
}
#endif
