#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.Globalization;
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

    public bool BuildUIToolkit(VisualElement root)
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (!editor.isCustomBase || appliedVersion?.customBlendshapes == null || !appliedVersion.customBlendshapes.Any())
        {
            return false;
        }

        var avatarRoot = editor.customBaseTarget.transform.root;
        var renderers = GetTargetBlendshapeRenderers(avatarRoot).ToArray();
        if (renderers.Length == 0)
        {
            return false;
        }

        var blendshapeEntries = appliedVersion.customBlendshapes;
        var values = editor.blendShapeValuesProp;
        SyncBlendshapeValueArray(values, blendshapeEntries.Length);

        string versionKey = $"{appliedVersion.version}|{appliedVersion.defaultAviVersion}";
        bool hasOverrides = editor.customBaseTarget.customBlendshapeOverrideNames.Count > 0;
        bool allWeightsZero = AreAllBlendshapeWeightsZero(renderers, blendshapeEntries);
        bool anyDefaultAboveZero = blendshapeEntries.Any(entry => ParseDefaultValue(entry.defaultValue) > WeightEpsilon);

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

        var card = AvatarOptionsModule.CreateOptionCard("mcb-avatar-blendshapes");
        card.Add(AvatarOptionsModule.CreateOptionTitle("Blendshapes"));

        var list = new VisualElement();
        list.AddToClassList("mcb-avatar-blendshapes__list");
        card.Add(list);

        VisualElement zeroWarning = AvatarOptionsModule.CreateOptionHelpBox(
            "All the sliders are at 0.0, turn some of them up to use the the custom MCB blenshapes",
            HelpBoxMessageType.Warning);

        for (int i = 0; i < blendshapeEntries.Length; i++)
        {
            var entry = blendshapeEntries[i];
            string shapeName = entry.name;
            float defaultValue = ParseDefaultValue(entry.defaultValue);
            int entryIndex = i;

            var primaryRenderer = renderers.FirstOrDefault(renderer => renderer.sharedMesh.GetBlendShapeIndex(shapeName) >= 0);
            if (primaryRenderer == null)
            {
                continue;
            }

            int primaryIndex = primaryRenderer.sharedMesh.GetBlendShapeIndex(shapeName);
            float currentWeight = primaryRenderer.GetBlendShapeWeight(primaryIndex);
            list.Add(BuildBlendshapeRowUIToolkit(renderers, blendshapeEntries, zeroWarning, entryIndex, shapeName, defaultValue, currentWeight));
        }

        zeroWarning.style.display = allWeightsZero ? DisplayStyle.Flex : DisplayStyle.None;
        list.Add(zeroWarning);

        var defaultButton = AvatarOptionsModule.CreateOptionButton("Set to Default Values", () =>
        {
            editor.serializedObject.Update();
            ClearAllCustomOverrides();
            ApplyDefaultBlendshapeValues(renderers, editor.blendShapeValuesProp, blendshapeEntries);
            editor.serializedObject.ApplyModifiedProperties();
            onBlendshapesChanged?.Invoke();
            AvatarOptionsModule.RefreshEditorUi(editor);
        });
        defaultButton.AddToClassList("mcb-avatar-blendshapes__default-button");
        list.Add(defaultButton);

        root.Add(card);
        return true;
    }

    private VisualElement BuildBlendshapeRowUIToolkit(
        SkinnedMeshRenderer[] renderers,
        CustomBlendshapeEntry[] blendshapeEntries,
        VisualElement zeroWarning,
        int entryIndex,
        string shapeName,
        float defaultValue,
        float currentWeight)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-avatar-blendshape-row");

        var slider = new Slider(shapeName, 0f, 100f) { value = currentWeight };
        slider.AddToClassList("mcb-avatar-blendshape-row__slider");

        var valueField = new FloatField { value = currentWeight };
        valueField.AddToClassList("mcb-avatar-blendshape-row__value");

        bool isSynchronizing = false;

        slider.RegisterValueChangedCallback(evt =>
        {
            if (isSynchronizing)
            {
                return;
            }

            float newWeight = Mathf.Clamp(evt.newValue, 0f, 100f);
            isSynchronizing = true;
            valueField.SetValueWithoutNotify(newWeight);
            isSynchronizing = false;
            ApplyBlendshapeWeightUIToolkit(renderers, entryIndex, shapeName, defaultValue, newWeight);
            UpdateZeroWarningUIToolkit(zeroWarning, renderers, blendshapeEntries);
        });

        valueField.RegisterValueChangedCallback(evt =>
        {
            if (isSynchronizing)
            {
                return;
            }

            float newWeight = Mathf.Clamp(evt.newValue, 0f, 100f);
            isSynchronizing = true;
            slider.SetValueWithoutNotify(newWeight);
            valueField.SetValueWithoutNotify(newWeight);
            isSynchronizing = false;
            ApplyBlendshapeWeightUIToolkit(renderers, entryIndex, shapeName, defaultValue, newWeight);
            UpdateZeroWarningUIToolkit(zeroWarning, renderers, blendshapeEntries);
        });

        row.Add(slider);
        row.Add(valueField);
        return row;
    }

    private void ApplyBlendshapeWeightUIToolkit(
        SkinnedMeshRenderer[] renderers,
        int entryIndex,
        string shapeName,
        float defaultValue,
        float newWeight)
    {
        MCBReFitIntegration.ApplyBlendShapeWeightWithTransferredReFit(
            editor.customBaseTarget,
            renderers,
            shapeName,
            newWeight);

        editor.serializedObject.Update();
        var values = editor.blendShapeValuesProp;
        SyncBlendshapeValueArray(values, entryIndex + 1);
        values.GetArrayElementAtIndex(entryIndex).floatValue = newWeight;

        if (Mathf.Abs(newWeight - defaultValue) > WeightEpsilon)
        {
            SetCustomOverrideValue(shapeName, newWeight);
        }
        else
        {
            RemoveCustomOverride(shapeName);
        }

        EditorUtility.SetDirty(editor.customBaseTarget);
        editor.serializedObject.ApplyModifiedProperties();
        onBlendshapesChanged?.Invoke();
        editor.Repaint();
    }

    private void UpdateZeroWarningUIToolkit(VisualElement zeroWarning, SkinnedMeshRenderer[] renderers, CustomBlendshapeEntry[] blendshapeEntries)
    {
        if (zeroWarning == null)
        {
            return;
        }

        zeroWarning.style.display = AreAllBlendshapeWeightsZero(renderers, blendshapeEntries)
            ? DisplayStyle.Flex
            : DisplayStyle.None;
    }

    private bool AreAllBlendshapeWeightsZero(SkinnedMeshRenderer[] renderers, CustomBlendshapeEntry[] blendshapeEntries)
    {
        foreach (var entry in blendshapeEntries)
        {
            if (renderers.Any(renderer =>
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(entry.name);
                return index >= 0 && renderer.GetBlendShapeWeight(index) > WeightEpsilon;
            }))
            {
                return false;
            }
        }

        return true;
    }

    private static void SyncBlendshapeValueArray(SerializedProperty values, int size)
    {
        while (values.arraySize < size) values.InsertArrayElementAtIndex(values.arraySize);
        while (values.arraySize > size) values.DeleteArrayElementAtIndex(values.arraySize - 1);
    }
}
#endif
