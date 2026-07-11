#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Provides contextual warnings and quick fixes for Poiyomi body materials.
public class AdjustMaterialModule
{
    private const string LightingModeProperty = "_LightingMode";
    private const string BumpMapProperty = "_BumpMap";
    private const int LightingModeTextureRampIndex = 0;
    private const int LightingModeRealisticIndex = 6;

    private readonly MCBEditor editor;

    private MaterialService materialService;
    private Transform cachedRoot;

    public AdjustMaterialModule(MCBEditor editor)
    {
        this.editor = editor;
    }

    public bool BuildUIToolkit(VisualElement root)
    {
        if (root == null || !EnsureMaterialService())
        {
            return false;
        }

        if (!CollectWarningState(out var lightingInfos, out bool shouldShowLightingWarning, out var normalInfos))
        {
            return false;
        }

        var card = AvatarOptionsModule.CreateOptionCard("mcb-adjust-material");
        card.Add(AvatarOptionsModule.CreateOptionTitle("Adjust Material"));

        if (shouldShowLightingWarning)
        {
            BuildLightingWarningUIToolkit(card, lightingInfos);
        }

        foreach (NormalMaterialInfo normalInfo in normalInfos)
        {
            BuildNormalMapWarningUIToolkit(card, normalInfo);
        }

        root.Add(card);
        return true;
    }

    private bool CollectWarningState(
        out List<LightingMaterialInfo> lightingInfos,
        out bool shouldShowLightingWarning,
        out List<NormalMaterialInfo> normalInfos)
    {
        lightingInfos = new List<LightingMaterialInfo>();
        normalInfos = new List<NormalMaterialInfo>();
        shouldShowLightingWarning = false;

        var appliedVersion = editor?.customBaseTarget?.appliedCustomBaseVersion;
        if (appliedVersion == null)
        {
            return false;
        }

        List<SkinnedMeshRenderer> defaultTargetRenderers = GetDefaultMaterialTargetRenderers(appliedVersion)
            .Where(renderer => materialService.TryGetMaterialWithRenderer(renderer, out var material) && IsPoiyomiShader(material?.shader))
            .ToList();

        var suggestRealisticTargets = ResolveSuggestRealisticTargets(appliedVersion)
            .Where(target => target?.Renderer != null &&
                             materialService.TryGetMaterialWithRenderer(target.Renderer, out var material) &&
                             IsPoiyomiShader(material?.shader))
            .ToList();

        lightingInfos = suggestRealisticTargets
            .Select(target => CreateLightingMaterialInfo(target.Renderer, target.AvatarPath))
            .Where(info => info != null)
            .ToList();

        shouldShowLightingWarning = lightingInfos.Any(info => info.HasLightingProperty && info.IsTextureRamp);

        normalInfos = defaultTargetRenderers
            .Select(renderer => CreateNormalMaterialInfo(renderer, null))
            .Where(info => info != null)
            .ToList();

        return shouldShowLightingWarning || normalInfos.Count > 0;
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

    private void BuildLightingWarningUIToolkit(VisualElement root, IReadOnlyList<LightingMaterialInfo> lightingInfos)
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

        bool anyLocked = lightingInfos.Any(info =>
            info.Material != null &&
            info.HasLightingProperty &&
            info.IsTextureRamp &&
            materialService.IsMaterialLocked(info.Material));

        bool canUpdateLighting = lightingInfos.Any(info => info.HasLightingProperty && info.IsTextureRamp);
        string buttonLabel = anyLocked ? "Unlock and set to Realistic" : "Set to Realistic";

        var box = CreateWarningActionBox(message, buttonLabel, () =>
        {
            foreach (LightingMaterialInfo info in lightingInfos)
            {
                if (!info.HasLightingProperty || !info.IsTextureRamp || info.Material == null)
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

            List<SkinnedMeshRenderer> renderersToUpdate = lightingInfos
                .Where(info => info.HasLightingProperty && info.IsTextureRamp)
                .Select(info => info.Renderer)
                .Where(renderer => renderer != null)
                .GroupBy(renderer => renderer.GetInstanceID())
                .Select(group => group.First())
                .ToList();

            if (renderersToUpdate.Count > 0)
            {
                ApplyLightingMode(renderersToUpdate);
                AvatarOptionsModule.RefreshEditorUi(editor);
            }
        });
        box.SetEnabled(canUpdateLighting);
        root.Add(box);
    }

    private void BuildNormalMapWarningUIToolkit(VisualElement root, NormalMaterialInfo info)
    {
        if (root == null || info == null)
        {
            return;
        }

        bool isLocked = materialService.IsMaterialLocked(info.Material);
        string buttonLabel = isLocked ? "Unlock and remove normal map" : "Remove normal map";

        var box = CreateWarningActionBox(
            $"Your {info.SlotName} material is using a normal map with fake muscles, it will conflict with the custom base muscles look.",
            buttonLabel,
            () =>
            {
                if (!EnsureUnlocked(info.Material))
                {
                    EditorUtility.DisplayDialog(
                        "Unlock Failed",
                        "Could not unlock the material shader. Please unlock it manually from Poiyomi before trying again.",
                        "Ok");
                    return;
                }

                RemoveNormalMap(info.Material, info.Renderer);
                AvatarOptionsModule.RefreshEditorUi(editor);
            },
            out var content);

        if (content != null)
        {
            if (info.NormalTexture != null)
            {
                var label = AvatarOptionsModule.CreateOptionLabel("Detected normal map", 11, FontStyle.Normal, new Color(0.7f, 0.7f, 0.7f));
                label.AddToClassList("mcb-adjust-material__normal-label");
                content.Add(label);

                var field = new ObjectField
                {
                    objectType = typeof(Texture),
                    value = info.NormalTexture,
                    allowSceneObjects = false
                };
                field.SetEnabled(false);
                field.AddToClassList("mcb-adjust-material__normal-field");
                content.Add(field);
            }
            else if (!string.IsNullOrEmpty(info.NormalTexturePath))
            {
                var label = AvatarOptionsModule.CreateOptionLabel("Detected normal map", 11, FontStyle.Normal, new Color(0.7f, 0.7f, 0.7f));
                label.AddToClassList("mcb-adjust-material__normal-label");
                content.Add(label);

                var path = AvatarOptionsModule.CreateOptionLabel(info.NormalTexturePath, 11, FontStyle.Normal, new Color(0.62f, 0.62f, 0.62f));
                path.AddToClassList("mcb-avatar-helpbox__secondary");
                content.Add(path);
            }
        }

        root.Add(box);
    }

    private static VisualElement CreateWarningActionBox(string message, string buttonLabel, Action clicked)
    {
        return CreateWarningActionBox(message, buttonLabel, clicked, out _);
    }

    private static VisualElement CreateWarningActionBox(string message, string buttonLabel, Action clicked, out VisualElement content)
    {
        var box = new VisualElement();
        box.AddToClassList("mcb-avatar-helpbox");
        box.AddToClassList("mcb-avatar-helpbox--warning");
        box.AddToClassList("mcb-adjust-material__warning");

        var icon = AvatarOptionsModule.CreateOptionLabel("!", 14, FontStyle.Bold, Color.white);
        icon.AddToClassList("mcb-avatar-helpbox__icon");
        box.Add(icon);

        content = new VisualElement();
        content.AddToClassList("mcb-avatar-helpbox__content");

        var label = AvatarOptionsModule.CreateOptionLabel(message, 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
        label.AddToClassList("mcb-avatar-helpbox__text");
        content.Add(label);

        var button = AvatarOptionsModule.CreateOptionButton(buttonLabel, clicked);
        button.AddToClassList("mcb-adjust-material__action");
        content.Add(button);

        box.Add(content);
        return box;
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

    private void ApplyLightingMode(IEnumerable<SkinnedMeshRenderer> renderers)
    {
        if (renderers == null)
        {
            return;
        }

        bool anyFailures = false;

        foreach (SkinnedMeshRenderer renderer in renderers)
        {
            if (!materialService.SetLightingMode(renderer, LightingModeRealisticIndex))
            {
                anyFailures = true;
                continue;
            }

            MCBLogger.Log($"[AdjustMaterial] Set {GetRendererLabel(renderer)} lighting mode to Realistic");
        }

        if (anyFailures)
        {
            EditorUtility.DisplayDialog(
                "Lighting Mode Update Failed",
                "MCB could not switch one or more materials to Realistic lighting. Please try updating the property manually.",
                "Ok");
        }
    }

    private LightingMaterialInfo CreateLightingMaterialInfo(SkinnedMeshRenderer renderer, string avatarPath)
    {
        if (!materialService.TryGetMaterialWithRenderer(renderer, out Material slotMaterial))
        {
            return null;
        }

        if (!IsPoiyomiShader(slotMaterial?.shader))
        {
            return null;
        }

        var info = new LightingMaterialInfo
        {
            SlotName = GetRendererLabel(renderer, avatarPath),
            Renderer = renderer,
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

        Undo.RecordObject(material, "Remove normal map");
        Undo.RecordObject(smr, "Remove normal map");

        material.SetTexture(BumpMapProperty, null);
        material.DisableKeyword("_NORMALMAP");
        material.DisableKeyword("_BUMP");

        EditorUtility.SetDirty(material);
        EditorUtility.SetDirty(smr);

        MCBLogger.Log($"[AdjustMaterial] Removed normal map from {GetRendererLabel(smr)}");
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

    private NormalMaterialInfo CreateNormalMaterialInfo(SkinnedMeshRenderer renderer, string avatarPath)
    {
        if (!materialService.TryGetMaterialWithRenderer(renderer, out Material material))
        {
            return null;
        }

        if (!TryGetMuscleNormal(material, out Texture normalTexture, out string normalTexturePath))
        {
            return null;
        }

        return new NormalMaterialInfo
        {
            SlotName = GetRendererLabel(renderer, avatarPath),
            Renderer = renderer,
            Material = material,
            NormalTexture = normalTexture,
            NormalTexturePath = normalTexturePath
        };
    }

    private List<string> GetTargetFbxPaths()
    {
        var paths = new List<string>();
        if (editor?.baseFbxFilesProp == null)
        {
            return paths;
        }

        for (int i = 0; i < editor.baseFbxFilesProp.arraySize; i++)
        {
            var fbx = editor.baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            string path = fbx != null ? AssetDatabase.GetAssetPath(fbx) : null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(MCBUtils.ToUnityPath(path));
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<SkinnedMeshRenderer> GetDefaultMaterialTargetRenderers(CustomBaseVersion version)
    {
        Transform root = cachedRoot;
        var targetFbxPaths = GetTargetFbxPaths();
        if (NativeMeshPayloadService.VersionUsesAdvancedMesh(version))
        {
            return NativeMeshPayloadService.ResolveRenderersForSourcePaths(root, version, targetFbxPaths, editor?.customBaseTarget);
        }

        return materialService.GetSkinnedMeshRenderersForFbxPaths(targetFbxPaths);
    }

    private List<SuggestRealisticTarget> ResolveSuggestRealisticTargets(CustomBaseVersion version)
    {
        var result = new List<SuggestRealisticTarget>();
        var seen = new HashSet<int>();
        Transform root = cachedRoot;
        if (root == null)
        {
            return result;
        }

        foreach (string avatarPath in ExtraCustomizationUtils.GetStringList(version?.extraCustomization, ExtraCustomizationUtils.SuggestRealisticKey))
        {
            var transform = FindTransformByRelativePath(root, avatarPath);
            var renderer = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer == null || !seen.Add(renderer.GetInstanceID()))
            {
                continue;
            }

            result.Add(new SuggestRealisticTarget
            {
                Renderer = renderer,
                AvatarPath = avatarPath
            });
        }

        return result;
    }

    private static Transform FindTransformByRelativePath(Transform root, string relativePath)
    {
        if (root == null || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        Transform current = root;
        foreach (string rawSegment in relativePath.Split('/'))
        {
            string segment = rawSegment.Trim();
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            current = current.Find(segment);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static string GetRendererLabel(SkinnedMeshRenderer renderer, string avatarPath = null)
    {
        if (!string.IsNullOrWhiteSpace(avatarPath))
        {
            return avatarPath;
        }

        return renderer == null ? "Unknown" : renderer.name;
    }

    private sealed class SuggestRealisticTarget
    {
        public SkinnedMeshRenderer Renderer;
        public string AvatarPath;
    }

    private sealed class LightingMaterialInfo
    {
        public string SlotName;
        public SkinnedMeshRenderer Renderer;
        public Material Material;
        public bool HasLightingProperty;
        public bool IsTextureRamp;
    }

    private sealed class NormalMaterialInfo
    {
        public string SlotName;
        public SkinnedMeshRenderer Renderer;
        public Material Material;
        public Texture NormalTexture;
        public string NormalTexturePath;
    }
}
#endif
