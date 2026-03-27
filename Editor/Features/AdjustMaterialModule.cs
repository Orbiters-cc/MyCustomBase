#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Provides contextual warnings and quick fixes for Poiyomi body materials.
public class AdjustMaterialModule
{
    private const string MaterialSlotName = "Body";
    private static readonly string[] AdditionalLightingSlots = { "Tail1", "Tail2" };
    private const string LightingModeProperty = "_LightingMode";
    private const string BumpMapProperty = "_BumpMap";
    private const int LightingModeTextureRampIndex = 0;
    private const int LightingModeRealisticIndex = 6;

    private readonly MCBEditor editor;

    private MaterialService materialService;
    private Transform cachedRoot;
    private bool foldout = true;

    public AdjustMaterialModule(MCBEditor editor)
    {
        this.editor = editor;
    }

    public void Draw()
    {
        if (!EnsureMaterialService())
        {
            return;
        }

        if (editor?.customBaseTarget?.appliedCustomBaseVersion == null)
        {
            return;
        }

        if (!materialService.TryGetMaterialWithRenderer(MaterialSlotName, out var material, out var smr))
        {
            return;
        }

        if (!IsPoiyomiShader(material?.shader))
        {
            return;
        }

        var lightingInfos = new List<LightingMaterialInfo>();
        var bodyLightingInfo = CreateLightingMaterialInfo(MaterialSlotName, material);
        if (bodyLightingInfo != null)
        {
            lightingInfos.Add(bodyLightingInfo);
        }

        foreach (string slotName in AdditionalLightingSlots)
        {
            var slotInfo = CreateLightingMaterialInfo(slotName);
            if (slotInfo != null)
            {
                lightingInfos.Add(slotInfo);
            }
        }

        bool shouldShowLightingWarning = lightingInfos.Any(info => info.HasLightingProperty && info.IsTextureRamp);

        bool hasMuscleNormal = TryGetMuscleNormal(material, out var normalTexture, out var normalTexturePath);

        bool shouldShowModule = shouldShowLightingWarning || hasMuscleNormal;
        if (!shouldShowModule)
        {
            return;
        }

        EditorGUILayout.Space();
        foldout = EditorGUILayout.Foldout(foldout, "Adjust Material", true, EditorStyles.foldoutHeader);
        if (!foldout)
        {
            return;
        }

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        if (shouldShowLightingWarning)
        {
            DrawLightingWarning(lightingInfos);
        }

        if (hasMuscleNormal)
        {
            DrawNormalMapWarning(material, smr, normalTexture, normalTexturePath);
        }

        EditorGUILayout.EndVertical();
    }

    private bool EnsureMaterialService()
    {
        var target = editor?.customBaseTarget;
        if (target == null)
        {
            return false;
        }

        Transform root = target.transform != null ? target.transform.root : null;
        if (root == null)
        {
            return false;
        }

        if (materialService == null || root != cachedRoot)
        {
            materialService = new MaterialService(root);
            cachedRoot = root;
        }

        return true;
    }

    private void DrawLightingWarning(IReadOnlyList<LightingMaterialInfo> lightingInfos)
    {
        if (lightingInfos == null || lightingInfos.Count == 0)
        {
            return;
        }

        List<string> textureRampSlots = lightingInfos
            .Where(info => info.HasLightingProperty && info.IsTextureRamp)
            .Select(info => info.SlotName)
            .ToList();

        if (textureRampSlots.Count == 0)
        {
            return;
        }

        string slotLabel = string.Join(", ", textureRampSlots);
        string message = textureRampSlots.Count == 1
            ? $"Your {slotLabel} material is set to a lighting type that won't show the muscles well, it is recommended to set the lighting mode to Realistic."
            : $"Your {slotLabel} materials are set to a lighting type that won't show the muscles well, it is recommended to set their lighting mode to Realistic.";

        EditorGUILayout.HelpBox(message, MessageType.Warning);

        bool anyLocked = lightingInfos.Any(info =>
            info.Material != null &&
            info.HasLightingProperty &&
            materialService.IsMaterialLocked(info.Material));

        bool canUpdateLighting = lightingInfos.Any(info => info.HasLightingProperty);

        using (new EditorGUI.DisabledScope(!canUpdateLighting))
        {
            string buttonLabel = anyLocked ? "Unlock and set to Realistic" : "Set to Realistic";

            if (GUILayout.Button(buttonLabel, GUILayout.Width(220f)))
            {
                foreach (LightingMaterialInfo info in lightingInfos)
                {
                    if (!info.HasLightingProperty || info.Material == null)
                    {
                        continue;
                    }

                    if (!EnsureUnlocked(info.Material))
                    {
                        EditorUtility.DisplayDialog(
                            "Unlock Failed",
                            $"Could not unlock the material shader on {info.SlotName}. Please unlock it manually from Poiyomi before trying again.",
                            "Ok");
                        return;
                    }
                }

                List<string> slotsToUpdate = lightingInfos
                    .Where(info => info.HasLightingProperty)
                    .Select(info => info.SlotName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (slotsToUpdate.Count > 0)
                {
                    ApplyLightingMode(slotsToUpdate);
                }
            }
        }
    }

    private void DrawNormalMapWarning(Material material, SkinnedMeshRenderer smr, Texture normalTexture, string normalTexturePath)
    {
        EditorGUILayout.HelpBox(
            "Your body material is using a normal map with fake muscles, it will conflict with the custom base muscles look.",
            MessageType.Warning);

        if (normalTexture != null)
        {
            EditorGUILayout.LabelField("Detected normal map:", EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(normalTexture, typeof(Texture), false);
            }
        }
        else if (!string.IsNullOrEmpty(normalTexturePath))
        {
            EditorGUILayout.LabelField("Detected normal map:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(normalTexturePath, EditorStyles.wordWrappedMiniLabel);
        }

        bool isLocked = materialService.IsMaterialLocked(material);
        string buttonLabel = isLocked ? "Unlock and remove normal map" : "Remove normal map";

        if (GUILayout.Button(buttonLabel, GUILayout.Width(220f)))
        {
            if (!EnsureUnlocked(material))
            {
                EditorUtility.DisplayDialog(
                    "Unlock Failed",
                    "Could not unlock the material shader. Please unlock it manually from Poiyomi before trying again.",
                    "Ok");
                return;
            }

            RemoveNormalMap(material, smr);
        }
    }

    private bool EnsureUnlocked(Material material)
    {
        if (!materialService.IsMaterialLocked(material))
        {
            return true;
        }

        return materialService.UnlockMaterial(material);
    }

    private static bool IsPoiyomiShader(Shader shader)
    {
        if (shader == null)
        {
            return false;
        }

        string shaderName = shader.name;
        return !string.IsNullOrEmpty(shaderName) && shaderName.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void ApplyLightingMode(IEnumerable<string> slotNames)
    {
        if (slotNames == null)
        {
            return;
        }

        bool anyFailures = false;

        foreach (string slotName in slotNames)
        {
            if (!materialService.SetLightingMode(slotName, LightingModeRealisticIndex))
            {
                anyFailures = true;
                continue;
            }

            MCBLogger.Log($"[AdjustMaterial] Set {slotName} lighting mode to Realistic");
        }

        if (anyFailures)
        {
            EditorUtility.DisplayDialog(
                "Lighting Mode Update Failed",
                "MCB could not switch one or more materials to Realistic lighting. Please try updating the property manually.",
                "Ok");
        }
    }

    private LightingMaterialInfo CreateLightingMaterialInfo(string slotName, Material existingMaterial = null)
    {
        Material slotMaterial = existingMaterial;

        if (slotMaterial == null)
        {
            if (!materialService.TryGetMaterialWithRenderer(slotName, out slotMaterial, out _))
            {
                return null;
            }
        }

        if (!IsPoiyomiShader(slotMaterial?.shader))
        {
            return null;
        }

        var info = new LightingMaterialInfo
        {
            SlotName = slotName,
            Material = slotMaterial
        };

        if (slotMaterial.HasProperty(LightingModeProperty))
        {
            float lightingValue = slotMaterial.GetFloat(LightingModeProperty);
            info.HasLightingProperty = true;
            info.IsTextureRamp = Mathf.Approximately(lightingValue, LightingModeTextureRampIndex);
        }

        return info;
    }

    private static void RemoveNormalMap(Material material, SkinnedMeshRenderer smr)
    {
        if (material == null || smr == null)
        {
            return;
        }

        if (!material.HasProperty(BumpMapProperty))
        {
            return;
        }

        Undo.RecordObject(material, "Remove body normal map");
        Undo.RecordObject(smr, "Remove body normal map");

        material.SetTexture(BumpMapProperty, null);
        material.DisableKeyword("_NORMALMAP");
        material.DisableKeyword("_BUMP");

        EditorUtility.SetDirty(material);
        EditorUtility.SetDirty(smr);

        MCBLogger.Log("[AdjustMaterial] Removed body normal map");
    }

    private static bool TryGetMuscleNormal(Material material, out Texture texture, out string texturePath)
    {
        texture = null;
        texturePath = null;

        if (material == null || !material.HasProperty(BumpMapProperty))
        {
            return false;
        }

        texture = material.GetTexture(BumpMapProperty);
        if (texture == null)
        {
            return false;
        }

        string assetPath = AssetDatabase.GetAssetPath(texture);
        string nameToCheck = !string.IsNullOrEmpty(assetPath) ? Path.GetFileName(assetPath) : texture.name;
        string lower = nameToCheck.ToLowerInvariant();

        if (!lower.Contains("muscle") && !lower.Contains("buff"))
        {
            texture = null;
            return false;
        }

        texturePath = !string.IsNullOrEmpty(assetPath) ? assetPath : texture.name;
        return true;
    }

    private sealed class LightingMaterialInfo
    {
        public string SlotName;
        public Material Material;
        public bool HasLightingProperty;
        public bool IsTextureRamp;
    }
}
#endif
