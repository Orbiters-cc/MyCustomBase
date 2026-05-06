#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;

public class BlendshapeDrawer
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

    public void Draw()
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (!editor.isCustomBase || appliedVersion?.customBlendshapes == null || !appliedVersion.customBlendshapes.Any()) return;

        var root = editor.customBaseTarget.transform.root;
        var renderers = GetTargetBlendshapeRenderers(root).ToArray();
        if (renderers.Length == 0) return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Blendshapes", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        var blendshapeEntries = appliedVersion.customBlendshapes;
        var values = editor.blendShapeValuesProp;

        while (values.arraySize < blendshapeEntries.Length) values.InsertArrayElementAtIndex(values.arraySize);
        while (values.arraySize > blendshapeEntries.Length) values.DeleteArrayElementAtIndex(values.arraySize - 1);

        string versionKey = $"{appliedVersion.version}|{appliedVersion.defaultAviVersion}";
        bool hasOverrides = editor.customBaseTarget.customBlendshapeOverrideNames.Count > 0;
        bool allWeightsZero = true;
        bool anyDefaultAboveZero = false;

        for (int i = 0; i < blendshapeEntries.Length; i++)
        {
            var entry = blendshapeEntries[i];
            float defaultValue = ParseDefaultValue(entry.defaultValue);
            if (defaultValue > WeightEpsilon) anyDefaultAboveZero = true;

            if (renderers.Any(renderer =>
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(entry.name);
                return index >= 0 && renderer.GetBlendShapeWeight(index) > WeightEpsilon;
            }))
            {
                allWeightsZero = false;
            }
        }

        if (!hasOverrides && anyDefaultAboveZero && allWeightsZero && lastAutoAppliedVersionKey != versionKey)
        {
            ApplyDefaultBlendshapeValues(renderers, values, blendshapeEntries);
            allWeightsZero = false;
            lastAutoAppliedVersionKey = versionKey;
        }
        else if (!allWeightsZero)
        {
            lastAutoAppliedVersionKey = versionKey;
        }

        
        
        for (int i = 0; i < blendshapeEntries.Length; i++)
        {
            var entry = blendshapeEntries[i];
            string shapeName = entry.name;
            float defaultValue = ParseDefaultValue(entry.defaultValue);
            
            var primaryRenderer = renderers.FirstOrDefault(renderer => renderer.sharedMesh.GetBlendShapeIndex(shapeName) >= 0);
            if (primaryRenderer == null) continue;

            // Get current weight from the mesh
            int primaryIndex = primaryRenderer.sharedMesh.GetBlendShapeIndex(shapeName);
            float currentWeight = primaryRenderer.GetBlendShapeWeight(primaryIndex);
            
            EditorGUI.BeginChangeCheck();
            float newWeight = EditorGUILayout.Slider(new GUIContent(shapeName), currentWeight, 0f, 100f);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var renderer in renderers)
                {
                    int index = renderer.sharedMesh.GetBlendShapeIndex(shapeName);
                    if (index >= 0)
                    {
                        renderer.SetBlendShapeWeight(index, newWeight);
                        EditorUtility.SetDirty(renderer);
                    }
                }

                values.GetArrayElementAtIndex(i).floatValue = newWeight;
                
                // Save custom override if different from default
                if (Mathf.Abs(newWeight - defaultValue) > WeightEpsilon)
                {
                    SetCustomOverrideValue(shapeName, newWeight);
                }
                else
                {
                    // If user set it back to default, remove the override
                    RemoveCustomOverride(shapeName);
                }
                
                EditorUtility.SetDirty(editor.customBaseTarget);
                onBlendshapesChanged?.Invoke();
            }
        }
        
        if (allWeightsZero)
        {
            EditorGUILayout.HelpBox("All the sliders are at 0.0, turn some of them up to use the the custom MCB blenshapes", MessageType.Warning);
        }
        
        EditorGUILayout.Space();
        if (GUILayout.Button("Set to Default Values"))
        {
            ClearAllCustomOverrides();
            ApplyDefaultBlendshapeValues(renderers, values, blendshapeEntries);
            onBlendshapesChanged?.Invoke();
        }
        
        EditorGUILayout.EndVertical();
    }
    
    private float GetCustomOverrideValue(string blendshapeName, float defaultValue)
    {
        var overrideNames = editor.customBaseTarget.customBlendshapeOverrideNames;
        var overrideValues = editor.customBaseTarget.customBlendshapeOverrideValues;
        
        int index = overrideNames.IndexOf(blendshapeName);
        if (index >= 0 && index < overrideValues.Count)
        {
            return overrideValues[index];
        }
        
        return defaultValue;
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
            bool applied = false;
            foreach (var renderer in renderers)
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(entry.name);
                if (index < 0) continue;
                renderer.SetBlendShapeWeight(index, defaultValue);
                EditorUtility.SetDirty(renderer);
                applied = true;
            }
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
