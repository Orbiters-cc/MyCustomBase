#if UNITY_EDITOR
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Orbiters.Toolkit.Editor.Vpm;

public partial class CustomVeinsDrawer
{
    private readonly MCBEditor editor;
    private Texture2D customVeinsTexture;
    private Texture2D okIcon;
    private Texture2D koIcon;
    private MaterialService materialService;
    private Transform cachedRoot;

    public const string CUSTOM_VEINS_PREF_KEY = "MCB_CustomVeins_Enabled";

    public CustomVeinsDrawer(MCBEditor editor)
    {
        this.editor = editor;
        LoadTextures();
    }

    private void LoadTextures()
    {
        customVeinsTexture = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/customVeins.png");
        okIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/ok.png");
        koIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/orbiters.mcb/Editor/ko.png");
    }

    private bool HasLockedPoiyomiMaterial(IReadOnlyList<Material> materials)
    {
        if (materials == null)
        {
            return false;
        }

        foreach (var material in materials)
        {
            if (material == null || !materialService.IsMaterialLocked(material))
            {
                continue;
            }

            string originalShader = material.GetTag("OriginalShader", false, string.Empty);
            if (!string.IsNullOrEmpty(originalShader) &&
                originalShader.IndexOf("poiyomi", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }


    private bool ApplyCustomVeins()
    {
        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;
        if (appliedVersion == null)
        {
            MCBLogger.LogError("[CustomVeinsDrawer] No applied version found");
            return false;
        }

        var targetRenderers = GetTargetRenderers();
        var targetMaterials = GetTargetMaterials(targetRenderers);
        if (!EnsureUnlocked(targetMaterials))
        {
            return false;
        }

        // Construct the path to the veins normal map using the utility method
        string versionFolder = MCBUtils.GetVersionDataPath(appliedVersion);
        string veinsNormalPath = System.IO.Path.Combine(versionFolder, "veins normal.png").Replace("\\", "/");

        MCBLogger.Log($"[CustomVeinsDrawer] Applying custom veins from: {veinsNormalPath}");

        bool success = false;
        bool anyFailures = false;
        foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetRenderers))
        {
            bool rendererSuccess = materialService.SetDetailNormalMap(renderer, veinsNormalPath, false);
            if (rendererSuccess)
            {
                materialService.SetDetailNormalOpacity(renderer, 1.0f, false);
                success = true;
            }
            else
            {
                anyFailures = true;
            }
        }
        materialService.SaveTouchedMaterialsSoon();

        if (success)
        {
            MCBLogger.Log("[CustomVeinsDrawer] Custom veins applied successfully");
        }
        if (!success || anyFailures)
        {
            MCBLogger.LogError("[CustomVeinsDrawer] Failed to apply custom veins");
        }
        return success;
    }

    private bool RemoveCustomVeins()
    {
        MCBLogger.Log("[CustomVeinsDrawer] Removing custom veins");

        var targetRenderers = GetTargetRenderers();
        var targetMaterials = GetTargetMaterials(targetRenderers);
        if (!EnsureUnlocked(targetMaterials))
        {
            return false;
        }

        bool success = false;
        foreach (var renderer in MaterialService.DistinctMaterialRenderers(targetRenderers))
        {
            success |= materialService.RemoveDetailNormalMap(renderer, false);
        }
        // The original base may order its materials differently: any material still carrying version veins.
        success |= materialService.RemoveVersionVeins();
        materialService.SaveTouchedMaterialsSoon();

        if (success)
        {
            MCBLogger.Log("[CustomVeinsDrawer] Custom veins removed successfully");
        }
        else
        {
            MCBLogger.LogError("[CustomVeinsDrawer] Failed to remove custom veins");
        }
        return success;
    }

    private bool EnsureUnlocked(IReadOnlyList<Material> materials)
    {
        if (materials == null || materials.Count == 0)
        {
            return false;
        }

        foreach (var material in materials)
        {
            if (!EnsureUnlocked(material))
            {
                return false;
            }
        }

        return true;
    }

    private bool EnsureUnlocked(Material material)
    {
        if (material == null || !materialService.IsMaterialLocked(material))
        {
            return true;
        }

        if (materialService.UnlockMaterial(material))
        {
            return true;
        }

        EditorUtility.DisplayDialog(
            "Unlock Failed",
            "Could not unlock the material shader. Please unlock it manually from Poiyomi before trying again.",
            "Ok");
        return false;
    }

    private bool EnsureMaterialService()
    {
        Transform root = editor?.customBaseTarget != null && editor.customBaseTarget.transform != null
            ? editor.customBaseTarget.transform.root
            : null;

        if (root == null)
        {
            return false;
        }

        if (materialService == null || cachedRoot != root)
        {
            materialService = new MaterialService(root);
            cachedRoot = root;
        }

        return true;
    }

    private List<SkinnedMeshRenderer> GetTargetRenderers()
    {
        var targetFbxPaths = GetTargetFbxPaths();
        var appliedVersion = editor?.customBaseTarget != null
            ? editor.customBaseTarget.appliedCustomBaseVersion
            : null;

        if (NativeMeshPayloadService.VersionUsesAdvancedMesh(appliedVersion))
        {
            var advancedRenderers = ResolveAdvancedMeshTargetRenderers(appliedVersion, targetFbxPaths);
            if (advancedRenderers.Count > 0)
            {
                return advancedRenderers;
            }
        }

        return materialService
            .GetSkinnedMeshRenderersForFbxPaths(targetFbxPaths)
            .Where(renderer => renderer?.sharedMaterial != null)
            .ToList();
    }

    private List<SkinnedMeshRenderer> ResolveAdvancedMeshTargetRenderers(CustomBaseVersion appliedVersion, List<string> targetFbxPaths)
    {
        if (cachedRoot == null || appliedVersion == null)
        {
            return new List<SkinnedMeshRenderer>();
        }

        var lookupPaths = targetFbxPaths != null && targetFbxPaths.Count > 0
            ? targetFbxPaths
            : GetVersionSourcePaths(appliedVersion);

        var renderers = ResolveAdvancedMeshRenderers(appliedVersion, lookupPaths);
        if (renderers.Count > 0)
        {
            return renderers;
        }

        // Advanced mesh swaps renderer.sharedMesh away from the FBX asset, so fall back to
        // the version source mapping if the editor FBX object list is stale or path-normalized differently.
        var sourcePaths = GetVersionSourcePaths(appliedVersion);
        renderers = sourcePaths.Count > 0
            ? ResolveAdvancedMeshRenderers(appliedVersion, sourcePaths)
            : renderers;
        if (renderers.Count > 0)
        {
            return renderers;
        }

        // Imported native-mesh versions can legitimately omit smrPaths. In that case the
        // generated mesh asset folder is the durable source of renderer provenance.
        return NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(cachedRoot, appliedVersion)
            .Where(renderer => renderer?.sharedMaterial != null)
            .ToList();
    }

    private List<SkinnedMeshRenderer> ResolveAdvancedMeshRenderers(CustomBaseVersion appliedVersion, IEnumerable<string> sourcePaths)
    {
        return NativeMeshPayloadService
            .ResolveRenderersForSourcePaths(cachedRoot, appliedVersion, sourcePaths, editor?.customBaseTarget)
            .Where(renderer => renderer?.sharedMaterial != null)
            .GroupBy(renderer => renderer.GetInstanceID())
            .Select(group => group.First())
            .ToList();
    }

    private List<string> GetVersionSourcePaths(CustomBaseVersion appliedVersion)
    {
        if (appliedVersion?.sourceFiles == null)
        {
            return new List<string>();
        }

        return appliedVersion.sourceFiles
            .Select(file => file?.path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(MCBUtils.ToUnityPath)
            .Distinct()
            .ToList();
    }

    private List<Material> GetTargetMaterials(List<SkinnedMeshRenderer> renderers = null)
    {
        renderers = renderers ?? GetTargetRenderers();
        return renderers
            .Select(renderer => renderer.sharedMaterial)
            .Where(material => material != null)
            .GroupBy(material => material.GetInstanceID())
            .Select(group => group.First())
            .ToList();
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
            .Distinct()
            .ToList();
    }
}
#endif
