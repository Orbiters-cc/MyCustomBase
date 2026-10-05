#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;

public partial class BlendshapeDrawer
{
    private const float WeightEpsilon = 0.001f;

    private readonly MCBEditor editor;
    private readonly Action onBlendshapesChanged;
    private string lastAutoAppliedVersionKey;

    public BlendshapeDrawer(MCBEditor editor, Action onBlendshapesChanged = null)
    {
        this.editor = editor;
        this.onBlendshapesChanged = onBlendshapesChanged;
    }

    
    private void SetCustomOverrideValue(string blendshapeName, float value)
    {
        var overrideNames = editor.customBaseTarget.customBlendshapeOverrideNames;
        var overrideValues = editor.customBaseTarget.customBlendshapeOverrideValues;
        
        int index = overrideNames.IndexOf(blendshapeName);
        if (index >= 0)
        {
            overrideValues[index] = value;
        }
        else
        {
            overrideNames.Add(blendshapeName);
            overrideValues.Add(value);
        }
    }
    
    private void RemoveCustomOverride(string blendshapeName)
    {
        var overrideNames = editor.customBaseTarget.customBlendshapeOverrideNames;
        var overrideValues = editor.customBaseTarget.customBlendshapeOverrideValues;
        
        int index = overrideNames.IndexOf(blendshapeName);
        if (index >= 0)
        {
            overrideNames.RemoveAt(index);
            overrideValues.RemoveAt(index);
        }
    }
    
    private void ClearAllCustomOverrides()
    {
        editor.customBaseTarget.customBlendshapeOverrideNames.Clear();
        editor.customBaseTarget.customBlendshapeOverrideValues.Clear();
    }

    private static float ParseDefaultValue(string defaultValueStr)
    {
        return float.TryParse(defaultValueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedDefault)
            ? parsedDefault
            : 0f;
    }

    private void ApplyDefaultBlendshapeValues(SkinnedMeshRenderer[] renderers, SerializedProperty values, CustomBlendshapeEntry[] blendshapeEntries)
    {
        for (int i = 0; i < blendshapeEntries.Length; i++)
        {
            var entry = blendshapeEntries[i];
            float defaultValue = ParseDefaultValue(entry.defaultValue);
            bool applied = MCBReFitIntegration.ApplyBlendShapeWeightWithTransferredReFit(
                editor.customBaseTarget,
                renderers,
                entry.name,
                defaultValue);
            if (!applied) continue;
            values.GetArrayElementAtIndex(i).floatValue = defaultValue;
        }

        EditorUtility.SetDirty(editor.customBaseTarget);
    }

    private SkinnedMeshRenderer[] GetTargetBlendshapeRenderers(Transform root)
    {
        if (root == null) return Array.Empty<SkinnedMeshRenderer>();

        return root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(renderer => renderer?.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0)
            .ToArray();
    }
}
#endif
