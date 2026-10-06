#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The XMuscles correctives a creator's custom base has, as Blender exports report them. An export carries only the meshes
/// it exported (Sync on Save can export a few), so a muscle is replaced when its meshes were exported again, and kept
/// otherwise.
/// </summary>
internal static class MuscleCorrectiveStore
{
    // VRChat's performance ranks for contacts (PC): Excellent up to 8, Good 16, Medium 24, Poor 32.
    internal static readonly int[] ContactRanks = { 8, 16, 24, 32 };

    /// <summary>The correctives after an export of <paramref name="exportedMeshes"/> reported <paramref name="incoming"/>.</summary>
    public static MuscleCorrectiveSet Merge(MuscleCorrectiveSet existing, MuscleCorrectiveSet incoming, ICollection<string> exportedMeshes)
    {
        var meshes = new HashSet<string>(exportedMeshes ?? new string[0]);
        var merged = new MuscleCorrectiveSet { apiVersion = incoming?.apiVersion ?? existing?.apiVersion ?? 0 };
        if (existing?.muscles != null)
            merged.muscles.AddRange(existing.muscles.Where(muscle =>
                muscle?.samples != null && !muscle.samples.Any(sample => meshes.Contains(sample.mesh)) &&
                (incoming?.muscles == null || incoming.muscles.All(other => other?.name != muscle.name))));
        if (incoming?.muscles != null) merged.muscles.AddRange(incoming.muscles.Where(muscle => muscle?.samples != null && muscle.samples.Count > 0));
        if (incoming?.warnings != null) merged.warnings.AddRange(incoming.warnings);
        return merged;
    }

    /// <summary>Records an export's correctives on the custom base (undoable).</summary>
    public static void Apply(MyCustomBase customBase, MuscleCorrectiveSet incoming, ICollection<string> exportedMeshes)
    {
        if (customBase == null || incoming == null) return;
        Undo.RecordObject(customBase, "XMuscles correctives");
        customBase.muscleCorrectives = Merge(customBase.muscleCorrectives, incoming, exportedMeshes);
        EditorUtility.SetDirty(customBase);
    }

    /// <summary>Each sensor of a muscle with samples is a receiver and a sender.</summary>
    public static int ContactCount(MuscleCorrectiveSet set) =>
        set?.muscles?.Where(muscle => muscle?.samples?.Count > 0).Sum(muscle => 2 * (muscle.sensors?.Count ?? 0)) ?? 0;

    public static int ShapeCount(MuscleCorrectiveSet set) =>
        set?.muscles?.Where(muscle => muscle?.samples != null).SelectMany(muscle => muscle.samples).Select(sample => sample.mesh + "/" + sample.shapeKey).Distinct().Count() ?? 0;
}
#endif
