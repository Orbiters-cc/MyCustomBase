using System;
using System.Collections.Generic;
using Newtonsoft.Json;

/// <summary>
/// XMuscles correctives baked in Blender: the <c>xmuscle</c> block of a Blender export manifest
/// (XMuscle Orbit Helper API <see cref="apiVersion"/>). Each muscle is a series of corrective
/// blendshapes that one bone's rotation blends between. Serialized by Unity (component data) and
/// by Newtonsoft (manifests, version metadata) with the same field names.
/// </summary>
[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleCorrectiveSet
{
    /// <summary>XMuscle Orbit Helper API version that described the bake.</summary>
    [JsonProperty] public int apiVersion;
    [JsonProperty] public List<MuscleCorrective> muscles = new List<MuscleCorrective>();
    /// <summary>Bake diagnostics from Blender (e.g. a muscle that could not be baked); not needed to drive the shapes.</summary>
    [JsonProperty] public List<string> warnings = new List<string>();
}

/// <summary>One baked muscle: blendshapes blended by the rotation of <see cref="bone"/> around <see cref="axis"/>.</summary>
[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleCorrective
{
    /// <summary>Muscle object name in Blender.</summary>
    [JsonProperty] public string name;
    /// <summary>Armature bone whose rotation drives the blendshapes (the same name in the FBX and in Unity).</summary>
    [JsonProperty] public string bone;
    /// <summary>"X", "Y" or "Z": the bone's local rotation axis of the bake, in Blender's bone space.</summary>
    [JsonProperty] public string axis;
    /// <summary>In bake order, from the start pose to the end pose.</summary>
    [JsonProperty] public List<MuscleCorrectiveSample> samples = new List<MuscleCorrectiveSample>();
}

/// <summary>A corrective blendshape that is fully on when the bone is rotated by <see cref="angleDeg"/>.</summary>
[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleCorrectiveSample
{
    /// <summary>Blendshape name.</summary>
    [JsonProperty] public string shapeKey;
    /// <summary>
    /// Blender object holding the blendshape: the FBX node name of the mesh (last segment of a renderer's
    /// <see cref="ModelFileSmrPathData.fbxMeshPath"/>) and the key of the export model's shapeKeysByMesh.
    /// </summary>
    [JsonProperty] public string mesh;
    /// <summary>Signed rotation of the bone around the axis, from its rest pose, in degrees.</summary>
    [JsonProperty] public float angleDeg;
}
