#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Names of a renderer's material slots as the model file declares them. Unity keeps materials by submesh
/// index only, so versions match slots by these names instead: an original's "BodyMatt" material lands on
/// the custom mesh's "BodyMatt" submesh whatever order either model uses.
/// </summary>
public static class MaterialSlotNames
{
    private static readonly Regex BlenderDuplicateSuffix = new Regex(@"\.\d{3}$", RegexOptions.Compiled);

    /// <summary>The comparable slot name: Blender's ".001" duplicate suffix and surrounding spaces removed.</summary>
    public static string Normalize(string name) => BlenderDuplicateSuffix.Replace((name ?? string.Empty).Trim(), string.Empty);

    /// <summary>Slot names of the renderer's mesh in submesh order, or of its current materials when no model declares them.</summary>
    public static string[] Of(SkinnedMeshRenderer renderer)
    {
        var mesh = renderer != null ? renderer.sharedMesh : null;
        if (mesh == null) return Array.Empty<string>();
        var declared = FromModel(mesh);
        var names = new string[mesh.subMeshCount];
        var current = renderer.sharedMaterials;
        for (int i = 0; i < names.Length; i++)
        {
            string name = declared != null && i < declared.Length ? declared[i] : null;
            if (string.IsNullOrEmpty(name) && i < current.Length && current[i] != null) name = current[i].name;
            names[i] = name ?? string.Empty;
        }
        return names;
    }

    /// <summary>The model's own slot names for a mesh imported from it: the material the importer created for a
    /// slot carries its name; a remapped slot is found through the importer's external-object map.</summary>
    public static string[] FromModel(Mesh mesh)
    {
        string path = mesh != null ? AssetDatabase.GetAssetPath(mesh) : null;
        if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is ModelImporter importer)) return null;
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var source = model != null ? model.GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => SharedMesh(r) == mesh) : null;
        if (source == null) return null;
        var remapped = new Dictionary<Material, string>();
        var ambiguous = new HashSet<Material>();
        foreach (var pair in importer.GetExternalObjectMap())
        {
            if (pair.Key.type != typeof(Material) || !(pair.Value is Material material)) continue;
            if (remapped.ContainsKey(material)) ambiguous.Add(material); else remapped[material] = pair.Key.name;
        }
        return source.sharedMaterials.Select(material =>
        {
            if (material == null) return string.Empty;
            if (remapped.TryGetValue(material, out string name) && !ambiguous.Contains(material)) return name;
            // A material embedded in the model is the slot itself.
            return AssetDatabase.GetAssetPath(material) == path ? material.name : null;
        }).ToArray();
    }

    private static Mesh SharedMesh(Renderer renderer) =>
        renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
        renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
}
#endif
