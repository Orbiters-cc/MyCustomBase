#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using MCBEditorUtils;

public class MaterialService
{
    private readonly Transform avatarRoot;
    
    // Static list of supported shaders

    private static readonly List<string> SupportedShaders = new List<string>
    {
        "Standard",
        "Poiyomi Toon",
        "Poiyomi Pro",
        "Mochie/Uber",
        "Mochie/Standard",
        "VRChat/Mobile/Standard Lite",
        "VRChat/Mobile/Toon Standard",
    };

    private static readonly string[] DetailNormalKeywordCandidates =
    {
        "_DETAIL_NORMAL_ON",
        "_DETAIL_NORMALMAP_ON",
        "_DETAIL_NORMALMAP",
        "_DETAILNORMALMAP",
        "_DetailNormalMap",
        "_DETAIL_MULX2",
        "DETAIL_NORMALS",
        "POI_DETAIL_NORMALS_ON",
        "_DETAIL",
        "USE_DETAIL_MAPS",
        "USE_NORMAL_MAPS",
        "_NORMALMAP",
        "USE_DETAIL_MAPS",
        "USE_NORMAL_MAPS",
        "DETAIL_NORMALS_ON",
        "_DetailEnabled"
    };

    private static readonly string[] DetailNormalToggleProperties =
    {
        "_UseDetailNormalMap",
        "_DetailNormalToggle",
        "_DetailNormalEnable",
        "_DetailNormalEnabled",
        "_DetailNormalsEnable",
        "_DetailNormalsEnabled",
        "_EnableDetailNormal",
        "_EnableDetailNormals",
        "_EnableDetailNormalMap",
        "_DetailNormal",
        "_DetailNormals",
        "_DetailNormToggle",
        "_DetailNormEnabled",
        "_DetailNormalsToggle",
        "_DetailNormalsToggleOn",
        "_N_DETAIL_ENABLE",
        "USE_DETAIL_MAPS",
        "USE_NORMAL_MAPS",
        "_NORMALMAP",
        "USE_DETAIL_MAPS",
        "USE_NORMAL_MAPS",
        "DETAIL_NORMALS_ON",
        "_DetailEnabled"
    };

    private static readonly string[] DetailNormalStrengthProperties =
    {
        "_DetailNormalMapScale",
        "_DetailNormalStrength",
        "_DetailNormalsStrength",
        "_DetailNormalsIntensity",
        "_DetailNormalFactor",
        "_DetailNormalMultiplier",
        "_DetailBumpScale",
        "_DetailBumpStrength",
        "_DetailBumpIntensity",
        "_DetailNormalPower",
        "_DetailNormalWeight"
    };

    private static readonly string[] DetailNormalTextureProperties =
    {
        "_DetailNormalMap",
        "_DetailNormalsTex",
        "_DetailBumpMap",
        "_DetailNormalTex"
    };

    private const string LightingModeProperty = "_LightingMode";
    private static readonly string[] LightingModeKeywords =
    {
        "_LIGHTINGMODE_TEXTURERAMP",
        "_LIGHTINGMODE_MULTILAYER_MATH",
        "_LIGHTINGMODE_WRAPPED",
        "_LIGHTINGMODE_SKIN",
        "_LIGHTINGMODE_SHADEMAP",
        "_LIGHTINGMODE_FLAT",
        "_LIGHTINGMODE_REALISTIC",
        "_LIGHTINGMODE_CLOTH",
        "_LIGHTINGMODE_SDF"
    };

    public MaterialService(Transform avatarRoot)
    {
        this.avatarRoot = avatarRoot; 
    }

    /// <summary>
    /// Gets the shader name from the specified material slot (e.g., "Body")
    /// </summary>
    public string GetShader(string materialSlot)
    {
        var material = GetMaterialForSlot(materialSlot);
        if (material == null || material.shader == null)
        {
            return null;
        }
        
        return material.shader.name;
    }

    /// <summary>
    /// Checks if a shader is supported
    /// </summary>
    public bool IsShaderSupported(string shaderName)
    {
        if (string.IsNullOrEmpty(shaderName))
            return false;

        // Check if the shader name contains any of the supported shader keywords
        string lowerShaderName = shaderName.ToLowerInvariant();
        
        // Check for Poiyomi variants (case-insensitive contains)
        if (lowerShaderName.Contains("poiyomi"))
            return true;
        
        // Check for Mochie variants (case-insensitive contains)
        if (lowerShaderName.Contains("mochie"))
            return true;
        
        // Check for VRChat variants (case-insensitive contains)
        if (lowerShaderName.Contains("vrchat"))
            return true;
        
        // Check for the unity Standard shader (use equals to avoid match with other shaders)
        if (lowerShaderName.Equals("standard"))
            return true;
        
        return false;
    }

    /// <summary>
    /// Returns the list of supported shaders
    /// </summary>
    public List<string> GetSupportedShadersList()
    {
        return new List<string>(SupportedShaders);
    }

    /// <summary>
    /// Attempts to retrieve the shared material assigned to the specified slot.
    /// </summary>
    public bool TryGetMaterial(string materialSlot, out Material material)
    {
        material = GetMaterialForSlot(materialSlot);
        return material != null;
    }

    /// <summary>
    /// Attempts to retrieve both the renderer and shared material for the specified slot.
    /// </summary>
    public bool TryGetMaterialWithRenderer(string materialSlot, out Material material, out SkinnedMeshRenderer smr)
    {
        smr = GetSkinnedMeshRendererForSlot(materialSlot);
        material = smr?.sharedMaterial;

        if (smr == null || material == null)
        {
            material = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sets the detail normal map texture for the specified material slot
    /// </summary>
    public bool SetDetailNormalMap(string materialSlot, string filePath)
    {
        var smr = GetSkinnedMeshRendererForSlot(materialSlot);
        if (smr == null) return false;
        
        var material = smr.sharedMaterial;
        if (material == null)
        {
            MCBLogger.LogError($"[MaterialService] SkinnedMeshRenderer '{materialSlot}' has no material assigned");
            return false;
        }

        if (!IsShaderSupported(material.shader.name))
        {
            MCBLogger.LogError($"[MaterialService] Shader '{material.shader.name}' is not supported");
            return false;
        }

        // Load the texture from the file path
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(filePath);
        if (texture == null)
        {
            MCBLogger.LogError($"[MaterialService] Could not load texture from path: {filePath}");
            return false;
        }

        // Set the detail normal map property
        PrepareMaterialEdit(smr, material, "MCB Apply Detail Normal Map");
        if (!TrySetDetailNormalTexture(material, texture))
        {
            MCBLogger.LogError("[MaterialService] Unable to find detail normal texture property on material");
            return false;
        }
        EnableDetailNormalFeatures(material);
        FinalizeMaterialEdit(smr, material);
        AssetDatabase.SaveAssets(); // Force save to disk
        AssetDatabase.Refresh(); // Force asset refresh
        
        MCBLogger.Log($"[MaterialService] Set detail normal map on {materialSlot} material to: {filePath}");
        return true;
    }

    /// <summary>
    /// Sets the detail normal map opacity (scale) for the specified material slot
    /// </summary>
    public bool SetDetailNormalOpacity(string materialSlot, float opacity)
    {
        var smr = GetSkinnedMeshRendererForSlot(materialSlot);
        if (smr == null) return false;
        
        var material = smr.sharedMaterial;
        if (material == null)
        {
            MCBLogger.LogError($"[MaterialService] SkinnedMeshRenderer '{materialSlot}' has no material assigned");
            return false;
        }

        if (!IsShaderSupported(material.shader.name))
        {
            MCBLogger.LogError($"[MaterialService] Shader '{material.shader.name}' is not supported");
            return false;
        }

        // Set the detail normal map scale property
        PrepareMaterialEdit(smr, material, "MCB Update Detail Normal Opacity");
        EnableDetailNormalFeatures(material);
        ApplyDetailNormalOpacity(material, opacity);
        FinalizeMaterialEdit(smr, material);
        AssetDatabase.SaveAssets(); // Force save to disk
        AssetDatabase.Refresh(); // Force asset refresh
        
        MCBLogger.Log($"[MaterialService] Set detail normal map opacity on {materialSlot} material to: {opacity}");
        return true;
    }

    /// <summary>
    /// Removes the detail normal map from the specified material slot
    /// </summary>
    public bool RemoveDetailNormalMap(string materialSlot)
    {
        var smr = GetSkinnedMeshRendererForSlot(materialSlot);
        if (smr == null) return false;
        
        var material = smr.sharedMaterial;
        if (material == null)
        {
            MCBLogger.LogError($"[MaterialService] SkinnedMeshRenderer '{materialSlot}' has no material assigned");
            return false;
        }

        if (!IsShaderSupported(material.shader.name))
        {
            MCBLogger.LogError($"[MaterialService] Shader '{material.shader.name}' is not supported");
            return false;
        }

        // Remove the detail normal map by setting it to null
        PrepareMaterialEdit(smr, material, "MCB Remove Detail Normal Map");
        if (!TrySetDetailNormalTexture(material, null))
        {
            MCBLogger.LogError("[MaterialService] Unable to clear detail normal texture on material");
            return false;
        }
        ApplyDetailNormalOpacity(material, 0f);
        DisableDetailNormalFeatures(material);
        FinalizeMaterialEdit(smr, material);
        AssetDatabase.SaveAssets(); // Force save to disk
        AssetDatabase.Refresh(); // Force asset refresh
        
        MCBLogger.Log($"[MaterialService] Removed detail normal map from {materialSlot} material");
        return true;
    }

    /// <summary>
    /// Checks whether a detail normal map texture is currently assigned on the specified material slot.
    /// </summary>
    public bool HasDetailNormalMap(string materialSlot)
    {
        return TryGetMaterial(materialSlot, out var material) && HasDetailNormalMap(material);
    }

    /// <summary>
    /// Checks whether a detail normal map texture is currently assigned on the provided material.
    /// </summary>
    public bool HasDetailNormalMap(Material material)
    {
        if (material == null)
        {
            return false;
        }

        foreach (var property in DetailNormalTextureProperties)
        {
            if (!material.HasProperty(property))
            {
                continue;
            }

            if (material.GetTexture(property) != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sets the lighting mode keyword enum to the provided value and refreshes the renderer.
    /// </summary>
    public bool SetLightingMode(string materialSlot, int lightingModeIndex)
    {
        var smr = GetSkinnedMeshRendererForSlot(materialSlot);
        if (smr == null)
        {
            return false;
        }

        var material = smr.sharedMaterial;
        if (material == null)
        {
            MCBLogger.LogError($"[MaterialService] SkinnedMeshRenderer '{materialSlot}' has no material assigned");
            return false;
        }

        if (!material.HasProperty(LightingModeProperty))
        {
            MCBLogger.LogError($"[MaterialService] Material on '{materialSlot}' does not expose property {LightingModeProperty}");
            return false;
        }

        lightingModeIndex = Mathf.Clamp(lightingModeIndex, 0, LightingModeKeywords.Length - 1);

        PrepareMaterialEdit(smr, material, "MCB Set Lighting Mode");

        material.SetFloat(LightingModeProperty, lightingModeIndex);
        ApplyLightingModeKeywords(material, lightingModeIndex);

        FinalizeMaterialEdit(smr, material);
        return true;
    }

    /// <summary>
    /// Gets the SkinnedMeshRenderer from the specified slot (e.g., "Body")
    /// </summary>
    private SkinnedMeshRenderer GetSkinnedMeshRendererForSlot(string materialSlot)
    {
        if (avatarRoot == null)
        {
            MCBLogger.LogError("[MaterialService] Avatar root is null");
            return null;
        }

        // Find the SkinnedMeshRenderer with the matching name
        var smr = MeshFinder.FindMeshPrioritizingRoot(avatarRoot, materialSlot);

        if (smr == null)
        {
            MCBLogger.LogError($"[MaterialService] Could not find SkinnedMeshRenderer with name: {materialSlot}");
            return null;
        }

        return smr;
    }

    /// <summary>
    /// Gets the material from the specified slot (e.g., "Body")
    /// </summary>
    private Material GetMaterialForSlot(string materialSlot)
    {
        var smr = GetSkinnedMeshRendererForSlot(materialSlot);
        if (smr == null) return null;

        // Get the shared material (the actual material asset, not an instance)
        if (smr.sharedMaterial == null)
        {
            MCBLogger.LogError($"[MaterialService] SkinnedMeshRenderer '{materialSlot}' has no material assigned");
            return null;
        }

        return smr.sharedMaterial;
    }

    private void EnableDetailNormalFeatures(Material material)
    {
        if (material == null)
        {
            return;
        }

        SetDetailNormalKeyword(material, true);

        foreach (var property in DetailNormalToggleProperties)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, 1f);
            }
        }
    }

    private void DisableDetailNormalFeatures(Material material)
    {
        if (material == null)
        {
            return;
        }

        SetDetailNormalKeyword(material, false);

        foreach (var property in DetailNormalToggleProperties)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, 0f);
            }
        }
    }

    private void ApplyDetailNormalOpacity(Material material, float opacity)
    {
        if (material == null)
        {
            return;
        }

        bool adjustedAny = false;

        foreach (var property in DetailNormalStrengthProperties)
        {
            if (!material.HasProperty(property))
            {
                continue;
            }

            float current = material.GetFloat(property);
            float target = Mathf.Clamp01(opacity);

            if (Mathf.Approximately(current, target))
            {
                float nudge = target < 0.5f ? Mathf.Min(target + 0.0001f, 1f) : Mathf.Max(target - 0.0001f, 0f);

                if (!Mathf.Approximately(nudge, target))
                {
                    material.SetFloat(property, nudge);
                }
                else
                {
                    material.SetFloat(property, target == 0f ? 0.0001f : Mathf.Clamp01(target - 0.0001f));
                }
            }

            material.SetFloat(property, target);
            adjustedAny = true;
        }

        if (!adjustedAny && material.HasProperty("_DetailNormalMapScale"))
        {
            // Fallback in case property list missed the active property
            float target = Mathf.Clamp01(opacity);
            float current = material.GetFloat("_DetailNormalMapScale");

            if (Mathf.Approximately(current, target))
            {
                float nudge = target < 0.5f ? Mathf.Min(target + 0.0001f, 1f) : Mathf.Max(target - 0.0001f, 0f);

                if (!Mathf.Approximately(nudge, target))
                {
                    material.SetFloat("_DetailNormalMapScale", nudge);
                }
                else
                {
                    material.SetFloat("_DetailNormalMapScale", target == 0f ? 0.0001f : Mathf.Clamp01(target - 0.0001f));
                }
            }

            material.SetFloat("_DetailNormalMapScale", target);
        }
    }

    private bool TrySetDetailNormalTexture(Material material, Texture texture)
    {
        if (material == null)
        {
            return false;
        }

        foreach (var property in DetailNormalTextureProperties)
        {
            if (!material.HasProperty(property))
            {
                continue;
            }

            material.SetTexture(property, texture);
            return true;
        }

        return false;
    }

    private void SetDetailNormalKeyword(Material material, bool enable)
    {
        if (material == null || material.shader == null)
        {
            return;
        }

        var shader = material.shader;
        var validKeywords = new List<(LocalKeyword keyword, string name)>();
        bool hasAnyKeyword = false;

        // Get all keyword names from the shader to avoid Unity error logging
        var shaderKeywordNames = shader.keywordSpace.keywordNames;

        foreach (var candidate in DetailNormalKeywordCandidates)
        {
            // Check if keyword exists in shader before constructing LocalKeyword
            // This avoids Unity's internal error logging
            bool keywordExists = false;
            foreach (var keywordName in shaderKeywordNames)
            {
                if (keywordName == candidate)
                {
                    keywordExists = true;
                    break;
                }
            }

            if (!keywordExists)
            {
                continue;
            }

            // Keyword exists, safe to construct LocalKeyword
            var localKeyword = new LocalKeyword(shader, candidate);
            if (localKeyword.isValid)
            {
                validKeywords.Add((localKeyword, candidate));
                hasAnyKeyword = true;
            }
        }

        if (hasAnyKeyword)
        {
            foreach (var (keyword, name) in validKeywords)
            {
                material.SetKeyword(keyword, enable);
            }
        }
        else if (enable)
        {
            // Only log error when trying to enable and no valid keyword was found
            MCBLogger.LogError($"[MaterialService] No valid detail normal keyword found in shader '{shader.name}'. Please report this shader for support.");
        }

#pragma warning disable CS0618
        foreach (var candidate in DetailNormalKeywordCandidates)
        {
            if (enable && validKeywords.Any(vk => vk.name == candidate))
            {
                continue;
            }

            try
            {
                material.DisableKeyword(candidate);
            }
            catch
            {
                // Expected - keyword doesn't exist, ignore
            }
        }
#pragma warning restore CS0618
    }
    private void PrepareMaterialEdit(SkinnedMeshRenderer smr, Material material, string undoLabel)
    {
        if (smr == null || material == null)
        {
            return;
        }

        Undo.RecordObject(material, undoLabel);
        Undo.RecordObject(smr, undoLabel);
    }

    private void ForceRendererRefresh(SkinnedMeshRenderer smr)
    {
        if (smr == null)
        {
            return;
        }

        var propertyBlock = new MaterialPropertyBlock();
        smr.GetPropertyBlock(propertyBlock);
        smr.SetPropertyBlock(propertyBlock);

        var materials = smr.sharedMaterials;
        if (materials != null && materials.Length > 0)
        {
            var tempMaterials = materials.ToArray();
            int swapIndex = System.Array.FindIndex(tempMaterials, mat => mat != null);

            if (swapIndex >= 0)
            {
                var original = tempMaterials[swapIndex];
                tempMaterials[swapIndex] = null;
                smr.sharedMaterials = tempMaterials;
                tempMaterials[swapIndex] = original;
                smr.sharedMaterials = tempMaterials;
            }
            else
            {
                smr.sharedMaterials = tempMaterials;
            }
        }
        else
        {
            smr.sharedMaterials = materials;
        }

        bool wasEnabled = smr.enabled;
        smr.enabled = false;
        smr.enabled = wasEnabled;

        SceneView.RepaintAll();
        EditorApplication.QueuePlayerLoopUpdate();
    }
    private void FinalizeMaterialEdit(SkinnedMeshRenderer smr, Material material)
    {
        if (smr == null || material == null)
        {
            return;
        }

        EditorUtility.SetDirty(material);
        EditorUtility.SetDirty(smr);

        if (PrefabUtility.IsPartOfPrefabInstance(smr))
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
        }

        var sharedMaterials = smr.sharedMaterials;
        if (sharedMaterials != null)
        {
            smr.sharedMaterials = sharedMaterials.ToArray();
        }

        smr.UpdateGIMaterials();
        ForceRendererRefresh(smr);
    }


    /// <summary>
    /// Determines whether the provided material references a locked Poiyomi shader.
    /// </summary>
    public bool IsMaterialLocked(Material material)
    {
        if (material == null || material.shader == null)
        {
            return false;
        }

        return material.shader.name.StartsWith("Hidden/", StringComparison.Ordinal) &&
               !string.IsNullOrEmpty(material.GetTag("OriginalShader", false, string.Empty));
    }

    /// <summary>
    /// Attempts to unlock a locked material by restoring its original shader reference.
    /// </summary>
    public bool UnlockMaterial(Material material)
    {
        if (material == null)
        {
            MCBLogger.LogError("[MaterialService] Material is null");
            return false;
        }

        Shader lockedShader = material.shader;

        if (!lockedShader.name.StartsWith("Hidden/", StringComparison.Ordinal))
        {
            MCBLogger.LogWarning($"[MaterialService] Shader {lockedShader.name} is not locked");
            return true;
        }

        string originalShaderName = material.GetTag("OriginalShader", false, string.Empty);
        if (string.IsNullOrEmpty(originalShaderName))
        {
            MCBLogger.LogError("[MaterialService] Original shader name not saved to material, could not unlock");
            return false;
        }

        Shader originalShader = Shader.Find(originalShaderName);

        if (originalShader == null)
        {
            MCBLogger.LogWarning($"[MaterialService] Original shader '{originalShaderName}' could not be found by exact match, trying fallback strategies...");
            originalShader = FindShaderByBaseName(originalShaderName);

            if (originalShader != null)
            {
                MCBLogger.Log($"[MaterialService] Found similar shader '{originalShader.name}' by base name matching");
            }
        }

        if (originalShader == null)
        {
            MCBLogger.LogError($"[MaterialService] Could not find any matching shader for '{originalShaderName}'");
            return false;
        }

        string renderType = material.GetTag("RenderType", false, string.Empty);
        int renderQueue = material.renderQueue;

        Undo.RecordObject(material, "Unlock Material Shader");

        material.shader = originalShader;
        material.SetOverrideTag("RenderType", renderType);
        material.renderQueue = renderQueue;

        string originalKeywords = material.GetTag("OriginalKeywords", false, string.Empty);
        if (!string.IsNullOrEmpty(originalKeywords))
        {
            material.shaderKeywords = originalKeywords.Split(' ');
        }

        EditorUtility.SetDirty(material);

        MCBLogger.Log($"[MaterialService] Successfully unlocked material shader from '{lockedShader.name}' to '{originalShader.name}'");
        return true;
    }

    private void ApplyLightingModeKeywords(Material material, int activeIndex)
    {
        if (material == null)
        {
            return;
        }

        for (int i = 0; i < LightingModeKeywords.Length; i++)
        {
            string keyword = LightingModeKeywords[i];
            if (string.IsNullOrEmpty(keyword))
            {
                continue;
            }

            if (i == activeIndex)
            {
                material.EnableKeyword(keyword);
            }
            else
            {
                material.DisableKeyword(keyword);
            }
        }
    }

    private Shader FindShaderByBaseName(string originalShaderName)
    {
        string baseName = originalShaderName;
        int lastSlashIndex = originalShaderName.LastIndexOf('/');
        if (lastSlashIndex >= 0 && lastSlashIndex < originalShaderName.Length - 1)
        {
            baseName = originalShaderName.Substring(lastSlashIndex + 1);
        }

        if (string.IsNullOrEmpty(baseName))
        {
            return null;
        }

        var shaderInfos = ShaderUtil.GetAllShaderInfo();

        foreach (var shaderInfo in shaderInfos)
        {
            if (!shaderInfo.supported)
            {
                continue;
            }

            if (shaderInfo.name.StartsWith("Hidden/", StringComparison.Ordinal))
            {
                continue;
            }

            Shader shader = Shader.Find(shaderInfo.name);
            if (shader == null)
            {
                continue;
            }

            string shaderBaseName = shader.name;
            int shaderLastSlash = shader.name.LastIndexOf('/');
            if (shaderLastSlash >= 0 && shaderLastSlash < shader.name.Length - 1)
            {
                shaderBaseName = shader.name.Substring(shaderLastSlash + 1);
            }

            if (shaderBaseName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            {
                return shader;
            }
        }

        Shader bestMatch = null;
        int bestScore = int.MaxValue;

        foreach (var shaderInfo in shaderInfos)
        {
            if (!shaderInfo.supported)
            {
                continue;
            }

            if (shaderInfo.name.StartsWith("Hidden/", StringComparison.Ordinal))
            {
                continue;
            }

            Shader shader = Shader.Find(shaderInfo.name);
            if (shader == null)
            {
                continue;
            }

            int score = CalculateShaderNameDistance(originalShaderName, shader.name);
            if (score < originalShaderName.Length / 2 && score < bestScore)
            {
                bestScore = score;
                bestMatch = shader;
            }
        }

        return bestMatch;
    }

    private int CalculateShaderNameDistance(string name1, string name2)
    {
        string normalized1 = name1.ToLowerInvariant().Replace(" ", string.Empty).Replace(".", string.Empty).Replace("/", string.Empty);
        string normalized2 = name2.ToLowerInvariant().Replace(" ", string.Empty).Replace(".", string.Empty).Replace("/", string.Empty);

        int[,] distance = new int[normalized1.Length + 1, normalized2.Length + 1];

        for (int i = 0; i <= normalized1.Length; i++)
        {
            distance[i, 0] = i;
        }

        for (int j = 0; j <= normalized2.Length; j++)
        {
            distance[0, j] = j;
        }

        for (int i = 1; i <= normalized1.Length; i++)
        {
            for (int j = 1; j <= normalized2.Length; j++)
            {
                int cost = normalized1[i - 1] == normalized2[j - 1] ? 0 : 1;

                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[normalized1.Length, normalized2.Length];
    }

}
#endif
