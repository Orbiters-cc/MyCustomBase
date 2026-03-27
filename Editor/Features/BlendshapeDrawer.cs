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
        var smr = MeshFinder.FindMeshPrioritizingRoot(root, "Body");
        var mohawkSmr = MeshFinder.FindMeshPrioritizingRoot(root, "MohawkHair");
        var maneSmr = MeshFinder.FindMeshPrioritizingRoot(root, "ManeHair");
        
        if (smr?.sharedMesh == null) return;

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

            int index = smr.sharedMesh.GetBlendShapeIndex(entry.name);
            if (index < 0) continue;

            if (smr.GetBlendShapeWeight(index) > WeightEpsilon)
            {
                allWeightsZero = false;
            }
        }

        if (!hasOverrides && anyDefaultAboveZero && allWeightsZero && lastAutoAppliedVersionKey != versionKey)
        {
            ApplyDefaultBlendshapeValues(smr, values, blendshapeEntries);
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
            
            int index = smr.sharedMesh.GetBlendShapeIndex(shapeName);
            if (index < 0) continue;

            // Get current weight from the mesh
            float currentWeight = smr.GetBlendShapeWeight(index);
            
            EditorGUI.BeginChangeCheck();
            float newWeight = EditorGUILayout.Slider(new GUIContent(shapeName), currentWeight, 0f, 100f);
            if (EditorGUI.EndChangeCheck())
            {
                smr.SetBlendShapeWeight(index, newWeight);

                // Also apply the same-named blendshape to MohawkHair and ManeHair if present
                if (mohawkSmr != null && mohawkSmr.sharedMesh != null)
                {
                    int mhIndex = mohawkSmr.sharedMesh.GetBlendShapeIndex(shapeName);
                    if (mhIndex >= 0)
                    {
                        mohawkSmr.SetBlendShapeWeight(mhIndex, newWeight);
                        EditorUtility.SetDirty(mohawkSmr);
                    }
                }
                if (maneSmr != null && maneSmr.sharedMesh != null)
                {
                    int maIndex = maneSmr.sharedMesh.GetBlendShapeIndex(shapeName);
                    if (maIndex >= 0)
                    {
                        maneSmr.SetBlendShapeWeight(maIndex, newWeight);
                        EditorUtility.SetDirty(maneSmr);
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
                
                EditorUtility.SetDirty(smr);
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
            ApplyDefaultBlendshapeValuesWithHair(smr, mohawkSmr, maneSmr, values, blendshapeEntries);
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

    private void ApplyDefaultBlendshapeValues(SkinnedMeshRenderer smr, SerializedProperty values, CustomBlendshapeEntry[] blendshapeEntries)
    {
        for (int i = 0; i < blendshapeEntries.Length; i++)
        {
            var entry = blendshapeEntries[i];
            float defaultValue = ParseDefaultValue(entry.defaultValue);
            int index = smr.sharedMesh.GetBlendShapeIndex(entry.name);
            if (index < 0) continue;

            smr.SetBlendShapeWeight(index, defaultValue);
            values.GetArrayElementAtIndex(i).floatValue = defaultValue;
        }

        EditorUtility.SetDirty(smr);
        EditorUtility.SetDirty(editor.customBaseTarget);
    }

    private void ApplyDefaultBlendshapeValuesWithHair(SkinnedMeshRenderer smr, SkinnedMeshRenderer mohawkSmr, SkinnedMeshRenderer maneSmr, SerializedProperty values, CustomBlendshapeEntry[] blendshapeEntries)
    {
        if (smr == null || smr.sharedMesh == null || blendshapeEntries == null) return;

        for (int i = 0; i < blendshapeEntries.Length; i++)
        {
            var entry = blendshapeEntries[i];
            float defaultValue = ParseDefaultValue(entry.defaultValue);

            int bodyIndex = smr.sharedMesh.GetBlendShapeIndex(entry.name);
            if (bodyIndex >= 0)
            {
                smr.SetBlendShapeWeight(bodyIndex, defaultValue);
                values.GetArrayElementAtIndex(i).floatValue = defaultValue;
            }

            if (mohawkSmr != null && mohawkSmr.sharedMesh != null)
            {
                int mhIndex = mohawkSmr.sharedMesh.GetBlendShapeIndex(entry.name);
                if (mhIndex >= 0)
                {
                    mohawkSmr.SetBlendShapeWeight(mhIndex, defaultValue);
                }
            }

            if (maneSmr != null && maneSmr.sharedMesh != null)
            {
                int maIndex = maneSmr.sharedMesh.GetBlendShapeIndex(entry.name);
                if (maIndex >= 0)
                {
                    maneSmr.SetBlendShapeWeight(maIndex, defaultValue);
                }
            }
        }

        EditorUtility.SetDirty(smr);
        if (mohawkSmr != null) EditorUtility.SetDirty(mohawkSmr);
        if (maneSmr != null) EditorUtility.SetDirty(maneSmr);
        EditorUtility.SetDirty(editor.customBaseTarget);
    }
}
#endif
