#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using MCBEditorUtils;

public class DynamicNormalsService
{
    private readonly MCBEditor editor;
    private List<string> activeBlendshapes = new List<string>();
    private Dictionary<SkinnedMeshRenderer, Mesh> originalMeshes = new Dictionary<SkinnedMeshRenderer, Mesh>();

    public DynamicNormalsService(MCBEditor editor)
    {
        this.editor = editor;
    }

    public List<string> GetActiveBlendshapes()
    {
        return new List<string>(activeBlendshapes);
    }

    public void FlushOriginalMeshes()
    {
        int count = originalMeshes.Count;
        originalMeshes.Clear();
        MCBLogger.Log($"[DynamicNormals] Flushed {count} original mesh reference(s) from cache.");
    }

    public void Apply(IReadOnlyList<MeshBlendshapeSelection> selections)
    {
        if (editor.customBaseTarget == null) return;
        var root = AvatarPaths.Root(editor.customBaseTarget);
        var resolved = selections.Select(s => (selection: s, renderer: AvatarPaths.Resolve(root, s.mesh).GetComponent<SkinnedMeshRenderer>())).ToArray();
        foreach (var item in resolved)
        {
            if (item.renderer?.sharedMesh == null) throw new System.InvalidOperationException("Missing normal renderer: " + item.selection.mesh);
            foreach (string name in item.selection.names)
                if (item.renderer.sharedMesh.GetBlendShapeIndex(name) < 0) throw new System.InvalidOperationException("Missing normal blendshape: " + name);
        }
        activeBlendshapes.Clear();
        foreach (var item in resolved)
        {
            var renderer = item.renderer;
            if (item.selection.names.Count == 0) continue;
            if (!originalMeshes.ContainsKey(renderer)) originalMeshes[renderer] = renderer.sharedMesh;
            Undo.RecordObject(renderer, "Apply dynamic normals");
            // Rebuild from the retained source when the explicit selection changes.
            renderer.sharedMesh = originalMeshes[renderer];
            DynamicNormals.ForRoot(root).limitToMeshes(new[] { renderer }).applyToBlendshapes(item.selection.names)
                .withBoneTranslations(LegSeparation(renderer)).enable(true).Apply();
            activeBlendshapes.AddRange(item.selection.names);
            EditorUtility.SetDirty(renderer);
        }
    }

    public void Apply(bool includeBody = true, bool includeFlexing = true)
    {
        if (editor.customBaseTarget == null) return;
        var root = AvatarPaths.Root(editor.customBaseTarget);
        var body = MeshFinder.FindMeshPrioritizingRoot(root, "Body");
        if (body?.sharedMesh == null) return;
        var names = Enumerable.Range(0, body.sharedMesh.blendShapeCount).Select(body.sharedMesh.GetBlendShapeName)
            .Where(n => (includeBody && n.IndexOf("muscle", System.StringComparison.OrdinalIgnoreCase) >= 0)
                || (includeFlexing && n.IndexOf("flex", System.StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        Apply(new[] { new MeshBlendshapeSelection { mesh = AnimationUtility.CalculateTransformPath(body.transform, root), names = names } });
    }

    private Dictionary<string, Vector3> LegSeparation(SkinnedMeshRenderer renderer)
    {
        var result = new Dictionary<string, Vector3>(System.StringComparer.OrdinalIgnoreCase);
        if (!editor.customBaseTarget.useAPoseForDynamicNormals) return result;
        foreach (var bone in renderer.bones.Where(b => b != null))
        {
            if (!Orbiters.Toolkit.Armature.BoneNames.TryInferHumanoid(bone.name, out var humanoid)) continue;
            if (humanoid != HumanBodyBones.LeftUpperLeg && humanoid != HumanBodyBones.RightUpperLeg) continue;
            var offset = new Vector3(humanoid == HumanBodyBones.LeftUpperLeg ? -.15f : .15f, 0, 0);
            foreach (var child in bone.GetComponentsInChildren<Transform>(true)) result[child.name] = offset;
        }
        return result;
    }

    public void Remove()
    {
        Remove(null);
    }

    public void Remove(IEnumerable<string> preferredFbxPaths)
    {
        if (editor.customBaseTarget == null) return;
        var root = AvatarPaths.Root(editor.customBaseTarget);
        var paths = editor.customBaseTarget.appliedCustomization.dynamicNormalBlendshapes.Select(s => s.mesh).ToHashSet();
        var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.name == "Body"
            || paths.Contains(AnimationUtility.CalculateTransformPath(r.transform, root)) || originalMeshes.ContainsKey(r)).ToArray();
        var remove = new HashSet<string>();
        foreach (var renderer in renderers)
        {
            var current = renderer.sharedMesh;
            if (current == null) continue;
            if (!originalMeshes.TryGetValue(renderer, out var original))
            {
                if (!current.name.Contains("(DynamicNormals)")) continue;
                original = FindOriginalMeshInAssets(NormalizeDynamicNormalsMeshName(current.name), GetPreferredFbxPaths(preferredFbxPaths));
            }
            if (original == null) throw new System.InvalidOperationException("Cannot restore the original mesh for " + renderer.name);
            remove.Add(AssetDatabase.GetAssetPath(current));
            Undo.RecordObject(renderer, "Remove dynamic normals"); renderer.sharedMesh = original;
            EditorUtility.SetDirty(renderer); originalMeshes.Remove(renderer);
        }
        foreach (string path in remove) DeleteMeshAssetIfDynamicNormals(path);
        activeBlendshapes.Clear();
    }

    private static string NormalizeDynamicNormalsMeshName(string meshName)
    {
        if (string.IsNullOrWhiteSpace(meshName))
        {
            return meshName;
        }

        return meshName
            .Replace(" (DynamicNormals)", "")
            .Replace("(DynamicNormals)", "")
            .Trim();
    }

    private void DeleteMeshAssetIfDynamicNormals(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        string normalizedPath = assetPath.Replace("\\", "/");
        if (!normalizedPath.EndsWith("_DynamicNormals.asset", System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (AssetDatabase.LoadAssetAtPath<Mesh>(normalizedPath) == null)
        {
            return;
        }

        if (AssetDatabase.DeleteAsset(normalizedPath))
        {
            MCBLogger.Log($"[DynamicNormals] Deleted current dynamic normals asset at: {normalizedPath}");
            AssetDatabase.SaveAssets();
        }
        else
        {
            Debug.LogWarning($"[DynamicNormals] Failed to delete current dynamic normals asset at: {normalizedPath}");
        }
    }

    private List<string> GetPreferredFbxPaths(IEnumerable<string> preferredFbxPaths)
    {
        var paths = new List<string>();
        foreach (string path in preferredFbxPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string normalized = MCBUtils.ToUnityPath(path);
            if (!paths.Contains(normalized, System.StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(normalized);
            }
        }

        var baseFbxFilesProp = editor.serializedObject.FindProperty("baseFbxFiles");
        if (baseFbxFilesProp != null)
        {
            for (int i = 0; i < baseFbxFilesProp.arraySize; i++)
            {
                var fbx = baseFbxFilesProp.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                if (fbx == null)
                {
                    continue;
                }

                string path = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(fbx));
                if (!string.IsNullOrWhiteSpace(path) &&
                    !paths.Contains(path, System.StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
        }

        if (paths.Count > 0)
        {
            MCBLogger.Log($"[DynamicNormals] Preferred original mesh FBX path(s): {string.Join(", ", paths)}");
        }

        return paths;
    }

    private Mesh FindOriginalMeshInAssets(string meshName, IEnumerable<string> preferredFbxPaths)
    {
        var preferredPaths = (preferredFbxPaths ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(MCBUtils.ToUnityPath)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string preferredPath in preferredPaths)
        {
            var mesh = FindMeshAtPath(meshName, preferredPath);
            if (mesh != null)
            {
                MCBLogger.Log($"[DynamicNormals] Found original mesh in preferred FBX at: {preferredPath}");
                return mesh;
            }
        }

        // Search for mesh assets with the given name
        string[] guids = AssetDatabase.FindAssets($"t:Mesh {meshName}");
        Mesh fallbackMesh = null;
        
        foreach (string guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            
            // Load all assets at this path (FBX files can contain multiple meshes)
            var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            
            foreach (var asset in assets)
            {
                if (asset is Mesh mesh && mesh.name == meshName)
                {
                    // Store as fallback if we don't find the preferred one
                    if (fallbackMesh == null)
                    {
                        fallbackMesh = mesh;
                        MCBLogger.Log($"[DynamicNormals] Found fallback mesh at: {assetPath}");
                    }
                }
            }
            
        }

        if (fallbackMesh != null)
        {
            Debug.LogWarning($"[DynamicNormals] Using fallback mesh (preferred FBX path not found or not specified).");
            return fallbackMesh;
        }
        
        Debug.LogWarning($"[DynamicNormals] Could not find mesh '{meshName}' in asset database.");
        return null;
    }

    private static Mesh FindMeshAtPath(string meshName, string assetPath)
    {
        if (string.IsNullOrWhiteSpace(meshName) || string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(MCBUtils.ToUnityPath(assetPath)))
        {
            if (asset is Mesh mesh && mesh.name == meshName)
            {
                return mesh;
            }
        }

        return null;
    }

    private void DeleteDynamicNormalsAsset(Mesh originalMesh)
    {
        if (originalMesh == null) return;

        // Get the original mesh asset path
        string originalMeshPath = AssetDatabase.GetAssetPath(originalMesh);
        if (string.IsNullOrEmpty(originalMeshPath))
        {
            MCBLogger.Log("[DynamicNormals] Original mesh has no asset path, no dynamic normals asset to delete.");
            return;
        }

        // Construct the expected dynamic normals asset path (same logic as in DynamicNormals.cs)
        string directory = System.IO.Path.GetDirectoryName(originalMeshPath);
        string meshNameSafe = originalMesh.name.Replace(" ", "_").Replace("(", "").Replace(")", "");
        string assetPath = System.IO.Path.Combine(directory, $"{meshNameSafe}_DynamicNormals.asset").Replace("\\", "/");

        // Delete the asset if it exists
        if (AssetDatabase.LoadAssetAtPath<Mesh>(assetPath) != null)
        {
            bool deleted = AssetDatabase.DeleteAsset(assetPath);
            if (deleted)
            {
                MCBLogger.Log($"[DynamicNormals] Deleted dynamic normals asset at: {assetPath}");
                AssetDatabase.SaveAssets();
            }
            else
            {
                Debug.LogWarning($"[DynamicNormals] Failed to delete dynamic normals asset at: {assetPath}");
            }
        }
        else
        {
            MCBLogger.Log($"[DynamicNormals] No dynamic normals asset found at: {assetPath}");
        }
    }

    private void AddHierarchyToSet(Transform root, HashSet<Transform> set)
    {
        if (root == null) return;
        set.Add(root);
        foreach (Transform child in root)
        {
            AddHierarchyToSet(child, set);
        }
    }
}
#endif
