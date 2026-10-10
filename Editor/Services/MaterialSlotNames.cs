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
        var names = source.sharedMaterials.Select(material =>
        {
            if (material == null) return string.Empty;
            if (remapped.TryGetValue(material, out string name) && !ambiguous.Contains(material)) return name;
            // A material embedded in the model is the slot itself.
            return AssetDatabase.GetAssetPath(material) == path ? material.name : null;
        }).ToArray();
        // A model imported without materials (Material Creation Mode: None) still names its slots in the file.
        return names.Any(n => !string.IsNullOrEmpty(n)) ? names : FromFile(path, AnimationUtility.CalculateTransformPath(source.transform, model.transform), mesh) ?? names;
    }

    private static readonly Dictionary<string, (long stamp, List<Orbiters.Toolkit.Editor.Meshes.FbxMesh> meshes)> Files =
        new Dictionary<string, (long, List<Orbiters.Toolkit.Editor.Meshes.FbxMesh>)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The binary FBX's material slot names for the mesh at <paramref name="rendererPath"/>, in Unity's submesh order. Unity
    /// orders submeshes its own way, so each one takes the file slot with the same triangle count (equal counts keep the
    /// file's order); null when the file cannot be read or the slots do not correspond.
    /// </summary>
    private static string[] FromFile(string path, string rendererPath, Mesh mesh)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            if (!info.Exists || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Files.TryGetValue(path, out var cached) || cached.stamp != info.LastWriteTimeUtc.Ticks)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                if (!Orbiters.Toolkit.Editor.Meshes.FbxReader.IsBinary(bytes)) return null;
                Files[path] = cached = (info.LastWriteTimeUtc.Ticks, Orbiters.Toolkit.Editor.Meshes.FbxReader.Read(bytes, Orbiters.Toolkit.Editor.Meshes.FbxReadOptions.Render));
            }
            var file = cached.meshes.FirstOrDefault(m => m.Path == rendererPath)
                ?? cached.meshes.Where(m => m.Name == rendererPath.Split('/').Last()).Take(2).SingleOrDefault();
            if (file?.SubMeshes == null || file.SubMeshes.Count != mesh.subMeshCount || file.Materials.Length != file.SubMeshes.Count) return null;
            var free = Enumerable.Range(0, file.SubMeshes.Count).ToList();
            var names = new string[mesh.subMeshCount];
            for (int i = 0; i < names.Length; i++)
            {
                int triangles = (int)(mesh.GetSubMesh(i).indexCount / 3);
                int slot = free.Where(s => file.SubMeshes[s].Length / 3 == triangles).DefaultIfEmpty(-1).First();
                if (slot < 0) return null;
                free.Remove(slot);
                names[i] = file.Materials[slot];
            }
            return names;
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is InvalidOperationException || ex is ArgumentException || ex is System.IO.InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds each slot's material to <paramref name="bySlot"/> under its comparable name (<see cref="Normalize"/>; make the
    /// dictionary with <see cref="StringComparer.OrdinalIgnoreCase"/>). A name already there keeps its material, so the
    /// first renderer collected wins.
    /// </summary>
    public static void Collect(IDictionary<string, Material> bySlot, IReadOnlyList<string> slots, IReadOnlyList<Material> materials)
    {
        if (bySlot == null || slots == null || materials == null) return;
        for (int i = 0; i < slots.Count && i < materials.Count; i++)
        {
            string key = Normalize(slots[i]);
            if (key.Length > 0 && materials[i] != null && !bySlot.ContainsKey(key)) bySlot[key] = materials[i];
        }
    }

    /// <summary>
    /// The material of each slot, found by name in <paramref name="bySlot"/> (see <see cref="Collect"/>), whatever order
    /// the mesh has its submeshes in. A slot with no name or no material of its name keeps <paramref name="fallback"/>'s
    /// material at its index, as a renderer does; unknown slots (null) keep the whole fallback.
    /// </summary>
    public static Material[] Assign(IReadOnlyList<string> slots, IReadOnlyDictionary<string, Material> bySlot, IReadOnlyList<Material> fallback)
    {
        if (slots == null) return fallback?.ToArray() ?? Array.Empty<Material>();
        var result = new Material[slots.Count];
        for (int i = 0; i < result.Length; i++)
        {
            string key = Normalize(slots[i]);
            result[i] = key.Length > 0 && bySlot != null && bySlot.TryGetValue(key, out var material) ? material
                : fallback != null && i < fallback.Count ? fallback[i] : null;
        }
        return result;
    }

    private static Mesh SharedMesh(Renderer renderer) =>
        renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
        renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
}
#endif
