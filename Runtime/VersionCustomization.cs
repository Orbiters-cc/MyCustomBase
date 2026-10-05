using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>Authoring data shared by creator UI, MCP, version metadata and build preprocessing.</summary>
[Serializable]
public sealed class VersionCustomization
{
    public const string ModesKey = "modes";
    public const string TwistBonesKey = "twistBones";
    public const string NormalsKey = "dynamicNormalBlendshapes";
    public const string LayoutKey = "rendererLayout";
    public const string PhysicKey = "physic";
    public ModeConfiguration modes = new ModeConfiguration();
    public List<TwistBoneConfiguration> twistBones = new List<TwistBoneConfiguration>();
    public List<MeshBlendshapeSelection> dynamicNormalBlendshapes = new List<MeshBlendshapeSelection>();
    public RendererLayoutConfiguration rendererLayout = new RendererLayoutConfiguration();
    // Bones named with "physic" are secondary-motion chains: users choose between PhysBones on them and stripping them.
    public bool physic;

    public static VersionCustomization Read(IEnumerable<object> entries) => new VersionCustomization
    {
        modes = ExtraCustomizationUtils.GetObject<ModeConfiguration>(entries, ModesKey) ?? new ModeConfiguration(),
        twistBones = ExtraCustomizationUtils.GetObject<List<TwistBoneConfiguration>>(entries, TwistBonesKey) ?? new List<TwistBoneConfiguration>(),
        dynamicNormalBlendshapes = ExtraCustomizationUtils.GetObject<List<MeshBlendshapeSelection>>(entries, NormalsKey) ?? new List<MeshBlendshapeSelection>(),
        rendererLayout = ExtraCustomizationUtils.GetObject<RendererLayoutConfiguration>(entries, LayoutKey) ?? new RendererLayoutConfiguration(),
        physic = ExtraCustomizationUtils.HasFlag(entries, PhysicKey)
    };

    public void Write(List<object> entries)
    {
        ExtraCustomizationUtils.SetObject(entries, ModesKey, modes?.options?.Count > 0 ? modes : null);
        ExtraCustomizationUtils.SetObject(entries, TwistBonesKey, twistBones?.Count > 0 ? twistBones : null);
        ExtraCustomizationUtils.SetObject(entries, NormalsKey, dynamicNormalBlendshapes?.Count > 0 ? dynamicNormalBlendshapes : null);
        ExtraCustomizationUtils.SetObject(entries, LayoutKey, rendererLayout != null && !rendererLayout.IsEmpty ? rendererLayout : null);
        ExtraCustomizationUtils.SetFlag(entries, PhysicKey, physic);
    }

    public VersionCustomization Clone() => JsonConvert.DeserializeObject<VersionCustomization>(JsonConvert.SerializeObject(this));
    public string Signature() => JsonConvert.SerializeObject(this, Formatting.None);

    public void Validate()
    {
        if (modes?.options == null || modes.categories == null || twistBones == null || dynamicNormalBlendshapes == null)
            throw new ArgumentException("Customization collections cannot be null; use empty arrays for unused features.");
        modes.Validate();
        var bones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var twist in twistBones ?? new List<TwistBoneConfiguration>())
        {
            ValidatePath(twist?.bone); ValidatePath(twist.aim); ValidatePath(twist.up);
            if (!bones.Add(twist.bone)) throw new ArgumentException("A twist bone can only be configured once.");
            if (twist.bone == twist.aim || twist.bone == twist.up) throw new ArgumentException("Twist aim and up must differ from the target bone.");
            if (twist.curve == null || twist.upDirection == null || twist.upDirection.ToVector().sqrMagnitude < 0.000001f)
                throw new ArgumentException("A twist bone needs a curve and a nonzero up direction.");
            if (!Finite(twist.upDirection.x) || !Finite(twist.upDirection.y) || !Finite(twist.upDirection.z))
                throw new ArgumentException("Twist direction must be finite.");
            twist.curve.Validate();
        }
        var meshes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in dynamicNormalBlendshapes ?? new List<MeshBlendshapeSelection>())
        {
            ValidatePath(selection?.mesh);
            if (!meshes.Add(selection.mesh)) throw new ArgumentException("Group each mesh's dynamic normal blendshapes in one selection.");
            if (selection.names == null || selection.names.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Dynamic normal selections need valid blendshape names.");
        }
        (rendererLayout ?? throw new ArgumentException("The renderer layout cannot be null.")).Validate();
    }

    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains("\\") || path.Split('/').Any(s => s == ".." || s == "." || s.Length == 0))
            throw new ArgumentException("Expected an unambiguous avatar-relative path: " + path);
    }
}

/// <summary>
/// Modes users switch in MCB: a genre, dog ears, a body modification. Each changes blendshapes, objects and the poses
/// or values an animation clip sets. Options of an exclusive category (a genre) replace each other; others combine.
/// </summary>
[Serializable]
public sealed class ModeConfiguration
{
    public List<ModeCategory> categories = new List<ModeCategory>();
    public List<ModeOption> options = new List<ModeOption>();

    public ModeCategory Category(string id) => categories.FirstOrDefault(c => c.id == id);
    public bool IsExclusive(ModeOption option) => Category(option?.category)?.exclusive ?? false;

    public void Validate()
    {
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in categories)
            if (category == null || string.IsNullOrWhiteSpace(category.id) || string.IsNullOrWhiteSpace(category.label) || !categoryIds.Add(category.id))
                throw new ArgumentException("Every mode category needs a unique ID and a name.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (option == null || string.IsNullOrWhiteSpace(option.id) || !ids.Add(option.id))
                throw new ArgumentException("Every mode needs a unique, non-empty ID.");
            if (string.IsNullOrWhiteSpace(option.label)) throw new ArgumentException("Every mode needs a name.");
            if (!categoryIds.Contains(option.category ?? "")) throw new ArgumentException($"Mode '{option.label}' needs one of the version's categories.");
            if (option.blendshapes == null || option.gameObjects == null || option.animations == null) throw new ArgumentException("Mode rules cannot be null.");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shape in option.blendshapes)
            {
                VersionCustomization.ValidatePath(shape?.mesh);
                if (string.IsNullOrWhiteSpace(shape.name) || !VersionCustomization.Finite(shape.value) || !properties.Add(shape.mesh + "\n" + shape.name))
                    throw new ArgumentException("Mode blendshapes need unique mesh/name pairs and finite values.");
            }
            properties.Clear();
            foreach (var state in option.gameObjects)
            {
                VersionCustomization.ValidatePath(state?.path);
                if (!properties.Add(state.path)) throw new ArgumentException("A mode cannot set the same GameObject twice.");
            }
            if (option.animations.Any(guid => guid == null || !System.Text.RegularExpressions.Regex.IsMatch(guid, "^[0-9a-f]{32}$"))
                || option.animations.Distinct().Count() != option.animations.Count)
                throw new ArgumentException("Mode animations must be distinct animation clip assets.");
        }
        foreach (var category in categories.Where(c => c.exclusive))
        {
            var members = options.Where(o => o.category == category.id).ToList();
            if (members.Count > 0 && members.Count(o => o.enabledByDefault) != 1)
                throw new ArgumentException($"Choose exactly one default {category.label} mode.");
        }
    }
}

[Serializable]
public sealed class ModeCategory
{
    public const string GenreId = "genre", BodyId = "body";
    public string id = "";
    public string label = "";
    // Exclusive: exactly one option is on (e.g. a genre). Otherwise options combine freely.
    public bool exclusive;

    public static ModeCategory Genre() => new ModeCategory { id = GenreId, label = "Genre", exclusive = true };
    public static ModeCategory Body() => new ModeCategory { id = BodyId, label = "Body modification" };
}

[Serializable]
public sealed class ModeOption
{
    public string id = "";
    public string label = "";
    public string category = "";
    [JsonProperty("default")] public bool enabledByDefault;
    public List<ModeBlendshape> blendshapes = new List<ModeBlendshape>();
    public List<ModeGameObject> gameObjects = new List<ModeGameObject>();
    // Animation clip asset GUIDs. Their first frame applies while the mode is on (bone poses, shapes, objects).
    public List<string> animations = new List<string>();
}

/// <summary>How a custom model's renderers take over an original avatar's renderers, independent of the original's piece layout.</summary>
[Serializable]
public sealed class RendererLayoutConfiguration
{
    // Per custom renderer, the original material slot each submesh takes its material from.
    public List<RendererSlotNames> renderers = new List<RendererSlotNames>();
    // Original renderer names the custom model replaces. Applying hides them; reset shows them again.
    public List<string> hide = new List<string>();
    // Creator-bundled materials (asset GUIDs) for slots an original lacks, such as reduced Quest models.
    public List<SlotMaterial> fallbacks = new List<SlotMaterial>();

    [JsonIgnore] public bool IsEmpty => (renderers?.Count ?? 0) == 0 && (hide?.Count ?? 0) == 0 && (fallbacks?.Count ?? 0) == 0;

    public void Validate()
    {
        if (renderers == null || hide == null || fallbacks == null) throw new ArgumentException("Renderer layout lists cannot be null.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var renderer in renderers)
        {
            VersionCustomization.ValidatePath(renderer?.path);
            if (!paths.Add(renderer.path)) throw new ArgumentException("Each custom renderer needs one material-slot mapping: " + renderer.path);
            if (renderer.slots == null || renderer.slots.Count == 0 || renderer.slots.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Every material slot of " + renderer.path + " needs an original slot name.");
        }
        if (hide.Any(name => string.IsNullOrWhiteSpace(name) || name.Contains("/")) || hide.Distinct(StringComparer.Ordinal).Count() != hide.Count)
            throw new ArgumentException("Hidden original renderers need unique names.");
        if (hide.Any(name => paths.Contains(name))) throw new ArgumentException("A custom renderer cannot also be hidden.");
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fallback in fallbacks)
            if (fallback == null || string.IsNullOrWhiteSpace(fallback.slot) || !slots.Add(fallback.slot)
                || fallback.material == null || !System.Text.RegularExpressions.Regex.IsMatch(fallback.material, "^[0-9a-f]{32}$"))
                throw new ArgumentException("Fallback materials need one unique slot name and a material asset each.");
    }
}

[Serializable] public sealed class RendererSlotNames { public string path = ""; public List<string> slots = new List<string>(); }
[Serializable] public sealed class SlotMaterial { public string slot = ""; public string material = ""; }

[Serializable] public sealed class ModeBlendshape { public string mesh = "Body"; public string name = ""; public float value; }
[Serializable] public sealed class ModeGameObject { public string path = ""; public bool active; }
[Serializable] public sealed class MeshBlendshapeSelection { public string mesh = "Body"; public List<string> names = new List<string>(); }
// A custom base's blendshape values the user set, kept per asset while the avatar is back on its original base.
[Serializable] public sealed class BlendshapeMemory { public int assetId; public List<string> names = new List<string>(); public List<float> values = new List<float>(); }
// The user's modes per asset. Options the user has seen keep their choice; newer options start with their default.
[Serializable] public sealed class ModeChoice { public int assetId; public List<string> enabled = new List<string>(); public List<string> known = new List<string>(); }
[Serializable] public sealed class ModeOriginalObject { public GameObject target; public bool active; }
[Serializable] public sealed class ModeOriginalShape { public SkinnedMeshRenderer target; public string name; public float value; }
// A value a mode animation changed: restored on reset by sampling a constant clip.
[Serializable] public sealed class ModeOriginalProperty { public string path; public string type; public string property; public float value; }
[Serializable] public sealed class NativeMeshOriginalParent { public Transform target; public Transform parent; }

[Serializable]
public sealed class TwistBoneConfiguration
{
    public string bone = "";
    public string aim = "";
    public string up = "";
    // Directions are avatar-root local, so rotated/scaled instances retain the authored frame.
    public TwistDirection upDirection = new TwistDirection();
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public TwistCurve curve = TwistCurve.Ultirex();
}

[Serializable] public sealed class TwistDirection
{
    public float x, y, z = 1;
    public Vector3 ToVector() => new Vector3(x, y, z);
}

[Serializable]
public sealed class TwistCurve
{
    public List<TwistCurveKey> keys = new List<TwistCurveKey>();
    public int preWrapMode = (int)WrapMode.ClampForever;
    public int postWrapMode = (int)WrapMode.ClampForever;

    public static TwistCurve Ultirex() => new TwistCurve
    {
        keys = new List<TwistCurveKey>
        {
            new TwistCurveKey { time = 0, value = 0, inTangent = .5772961f, outTangent = .5772961f, outWeight = .4334126f },
            new TwistCurveKey { time = .6262437f, value = .3680387f, inTangent = .9305876f, outTangent = .9305876f, inWeight = 1f/3, outWeight = .2487787f },
            new TwistCurveKey { time = 1, value = 1, inTangent = 2, outTangent = 2 }
        }
    };
    public AnimationCurve ToCurve() => new AnimationCurve(keys.Select(k => new Keyframe(k.time, k.value, k.inTangent, k.outTangent, k.inWeight, k.outWeight)
        { weightedMode = (WeightedMode)k.weightedMode }).ToArray()) { preWrapMode = (WrapMode)preWrapMode, postWrapMode = (WrapMode)postWrapMode };
    public static TwistCurve FromCurve(AnimationCurve curve) => new TwistCurve
    {
        preWrapMode = (int)curve.preWrapMode, postWrapMode = (int)curve.postWrapMode,
        keys = curve.keys.Select(k => new TwistCurveKey { time = k.time, value = k.value, inTangent = k.inTangent,
            outTangent = k.outTangent, inWeight = k.inWeight, outWeight = k.outWeight, weightedMode = (int)k.weightedMode }).ToList()
    };
    public void Validate()
    {
        if (keys == null || keys.Count < 2 || keys.Any(k => k == null) || keys[0].time != 0 || keys[keys.Count-1].time != 1)
            throw new ArgumentException("A twist curve must cover 0 to 1 with at least two keys.");
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (k == null || !VersionCustomization.Finite(k.time) || !VersionCustomization.Finite(k.value)
                || float.IsNaN(k.inTangent) || float.IsNaN(k.outTangent)
                || !VersionCustomization.Finite(k.inWeight) || !VersionCustomization.Finite(k.outWeight)
                || k.inWeight < 0 || k.inWeight > 1 || k.outWeight < 0 || k.outWeight > 1 || k.weightedMode < 0 || k.weightedMode > 3
                || k.value < 0 || k.value > 1 || (i > 0 && k.time <= keys[i-1].time))
                throw new ArgumentException("Twist curve keys must increase in time and have values between 0 and 1.");
        }
    }
}
[Serializable] public sealed class TwistCurveKey
{
    public float time, value, inTangent, outTangent, inWeight, outWeight;
    public int weightedMode;
}
