using System;
using System.Collections.Generic;
using Newtonsoft.Json;

/// <summary>
/// XMuscles correctives baked in Blender: the <c>xmuscle</c> block of a Blender export manifest
/// (XMuscle Orbit Helper API <see cref="apiVersion"/>). Each muscle is a series of corrective
/// blendshapes that the pose blends between, as the muscle's sensors measure it: a sensor is two points
/// on the avatar's bones whose distance follows the pose (in VRChat, a contact receiver and a sender),
/// and each blendshape is fully on at the distances it was baked at. Serialized by Unity (component
/// data) and by Newtonsoft (manifests, version metadata) with the same field names.
/// </summary>
[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleCorrectiveSet
{
    /// <summary>The XMuscle Orbit Helper API this layout comes from; other versions are not read.</summary>
    public const int ApiVersion = 2;

    /// <summary>XMuscle Orbit Helper API version that described the bake.</summary>
    [JsonProperty] public int apiVersion;
    [JsonProperty] public List<MuscleCorrective> muscles = new List<MuscleCorrective>();
    /// <summary>Bake diagnostics from Blender (e.g. a muscle that could not be baked); not needed to drive the shapes.</summary>
    [JsonProperty] public List<string> warnings = new List<string>();
}

/// <summary>One baked muscle: blendshapes blended by the readings of its sensors.</summary>
[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleCorrective
{
    /// <summary>Muscle object name in Blender.</summary>
    [JsonProperty] public string name;
    /// <summary>The muscle's stretch, from its origin to its insertion, then (when its drivers read one) a bone's twist.</summary>
    [JsonProperty] public List<MuscleSensor> sensors = new List<MuscleSensor>();
    /// <summary>In bake order, from the start pose to the end pose (a twist grid one twist after the other).</summary>
    [JsonProperty] public List<MuscleCorrectiveSample> samples = new List<MuscleCorrectiveSample>();
}

/// <summary>
/// Two points whose distance follows the pose. With <see cref="aim"/>, the receiver rides on a pivot at the aim bone's head,
/// a child of the receiver's bone, that aims its Y along the aim bone and keeps the aim bone's rest X (carried by the
/// receiver's bone) as up: the reading then follows the aim bone's twist alone.
/// </summary>
[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleSensor
{
    [JsonProperty] public MuscleSensorPoint receiver = new MuscleSensorPoint();
    [JsonProperty] public MuscleSensorPoint sender = new MuscleSensorPoint();
    /// <summary>Null (Newtonsoft) or without a bone (Unity serialization) for a sensor without a pivot.</summary>
    [JsonProperty] public MuscleSensorAim aim;

    public bool HasAim => !string.IsNullOrEmpty(aim?.bone);
}

/// <summary>
/// A point on a bone: <see cref="position"/> is its offset from the bone's head along the bone's rest axes (the aim bone's,
/// for the receiver of a sensor with a pivot), in Blender's bone axes and world units.
/// </summary>
[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleSensorPoint
{
    /// <summary>Armature bone (the same name in the FBX and in Unity).</summary>
    [JsonProperty] public string bone;
    [JsonProperty] public float[] position = new float[3];
}

[Serializable]
[JsonObject(MemberSerialization.OptIn)]
public class MuscleSensorAim
{
    [JsonProperty] public string bone;
}

/// <summary>A corrective blendshape that is fully on when each sensor measures its <see cref="distances"/>.</summary>
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
    /// <summary>Receiver to sender distance of each sensor, in Blender world units, in the pose the shape was baked for.</summary>
    [JsonProperty] public List<float> distances = new List<float>();
}
