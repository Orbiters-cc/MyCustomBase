#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

/// <summary>
/// The avatar descriptor names its eyelid blendshapes (blink, looking up, looking down) by their index in the eyelid mesh.
/// When MCB gives that renderer another mesh (a version's, or the original back), the indices follow the shapes' names;
/// otherwise VRChat animates whatever now sits there: Ultirex's mesh has "Throat" and "TailSkinny" where Rexouium had
/// "LookUp" and "LookDown". A shape the new mesh does not have is turned off rather than pointed at another one.
/// </summary>
public static class EyelidBlendshapes
{
    /// <summary>Gives <paramref name="renderer"/> <paramref name="mesh"/>, keeping the descriptor's eyelid shapes. Record the renderer for Undo first.</summary>
    public static void Assign(SkinnedMeshRenderer renderer, Mesh mesh)
    {
        var previous = renderer.sharedMesh;
        renderer.sharedMesh = mesh;
        Follow(renderer, previous, mesh);
    }

    public static void Follow(SkinnedMeshRenderer renderer, Mesh from, Mesh to)
    {
        if (renderer == null || from == null || to == null || from == to) return;
        var descriptor = renderer.GetComponentInParent<VRCAvatarDescriptor>(true);
        if (descriptor == null) return;
        var eyes = descriptor.customEyeLookSettings;
        if (eyes.eyelidsSkinnedMesh != renderer || eyes.eyelidsBlendshapes == null || eyes.eyelidsBlendshapes.Length == 0) return;
        var moved = eyes.eyelidsBlendshapes
            .Select(index => index >= 0 && index < from.blendShapeCount ? to.GetBlendShapeIndex(from.GetBlendShapeName(index)) : index).ToArray();
        if (moved.SequenceEqual(eyes.eyelidsBlendshapes)) return;
        Undo.RecordObject(descriptor, "Keep eyelid blendshapes");
        eyes.eyelidsBlendshapes = moved;
        descriptor.customEyeLookSettings = eyes;
        EditorUtility.SetDirty(descriptor);
    }
}
#endif
