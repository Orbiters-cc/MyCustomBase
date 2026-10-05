using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The user's version modes (genre, dog ears, ...): which are on, applying them to the avatar in the editor, restoring
/// the avatar on reset, and the values the build locks.
/// </summary>
public static class ModeService
{
    /// <summary>What the enabled modes set. Blendshapes any mode controls are 0 unless an enabled mode sets them;
    /// objects and animated values keep their original state unless an enabled mode sets them.</summary>
    public sealed class Plan
    {
        public readonly Dictionary<(string mesh, string name), float> Shapes = new Dictionary<(string, string), float>();
        public readonly Dictionary<string, bool?> Objects = new Dictionary<string, bool?>(StringComparer.Ordinal);
        public readonly Dictionary<EditorCurveBinding, float?> Properties = new Dictionary<EditorCurveBinding, float?>();
        // Locked in the build against other animations: everything an exclusive category declares, and what enabled
        // options of other categories set. Animated values (bone poses) are kept in the scene instead.
        public readonly HashSet<(string mesh, string name)> LockedShapes = new HashSet<(string, string)>();
        public readonly HashSet<string> LockedObjects = new HashSet<string>(StringComparer.Ordinal);
    }

    public static ModeChoice Choice(MyCustomBase owner) => owner.modeChoices.FirstOrDefault(c => c.assetId == owner.appliedCustomBaseAssetId);

    public static List<string> EnabledIds(MyCustomBase owner) => Enabled(owner.appliedCustomization?.modes, Choice(owner));

    /// <summary>Known options keep the user's choice, new ones their default; an exclusive category always has exactly one on.</summary>
    public static List<string> Enabled(ModeConfiguration config, ModeChoice choice)
    {
        if (config?.options == null || config.options.Count == 0) return new List<string>();
        var on = new HashSet<string>(config.options.Where(o => choice != null && choice.known.Contains(o.id) ? choice.enabled.Contains(o.id) : o.enabledByDefault).Select(o => o.id));
        foreach (var category in config.categories.Where(c => c.exclusive))
        {
            var members = config.options.Where(o => o.category == category.id).ToList();
            if (members.Count == 0) continue;
            var keep = members.FirstOrDefault(o => on.Contains(o.id)) ?? members.FirstOrDefault(o => o.enabledByDefault) ?? members[0];
            foreach (var member in members) on.Remove(member.id);
            on.Add(keep.id);
        }
        return config.options.Where(o => on.Contains(o.id)).Select(o => o.id).ToList();
    }

    public static void Set(MyCustomBase owner, string id, bool enabled)
    {
        var config = owner.appliedCustomization?.modes ?? throw new InvalidOperationException("This version has no modes.");
        var option = config.options.FirstOrDefault(o => o.id == id) ?? throw new ArgumentException("Unknown mode: " + id);
        var on = EnabledIds(owner);
        if (config.IsExclusive(option))
        {
            if (!enabled) return; // Choose another option of the category instead.
            on.RemoveAll(other => config.options.First(o => o.id == other).category == option.category);
            on.Add(id);
        }
        else
        {
            on.Remove(id);
            if (enabled) on.Add(id);
        }
        // Resolve everything first; a missing reference must not partially switch the avatar.
        ValidateTargets(AvatarPaths.Root(owner), config);
        Undo.RecordObject(owner, "Change MCB mode");
        Remember(owner, config, on);
        Apply(owner);
    }

    private static void Remember(MyCustomBase owner, ModeConfiguration config, List<string> on)
    {
        var choice = Choice(owner);
        if (choice == null) { choice = new ModeChoice { assetId = owner.appliedCustomBaseAssetId }; owner.modeChoices.Add(choice); }
        choice.enabled = on.Distinct().ToList();
        choice.known = choice.known.Union(config.options.Select(o => o.id)).ToList();
        EditorUtility.SetDirty(owner);
    }

    public static AnimationClip Clip(string guid)
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
    }

    public static void ValidateTargets(Transform root, ModeConfiguration config)
    {
        foreach (var option in config.options)
        {
            foreach (var shape in option.blendshapes)
            {
                var smr = AvatarPaths.Resolve(root, shape.mesh).GetComponent<SkinnedMeshRenderer>();
                if (smr?.sharedMesh == null || smr.sharedMesh.GetBlendShapeIndex(shape.name) < 0)
                    throw new InvalidOperationException($"Mode '{option.label}': missing blendshape '{shape.mesh}/{shape.name}'.");
            }
            foreach (var state in option.gameObjects) AvatarPaths.Resolve(root, state.path);
            foreach (string guid in option.animations)
                if (Clip(guid) == null) throw new InvalidOperationException($"Mode '{option.label}': its animation is missing from this project.");
        }
    }

    public static Plan BuildPlan(ModeConfiguration config, ICollection<string> enabled)
    {
        var plan = new Plan();
        if (config?.options == null) return plan;
        foreach (var option in config.options)
        {
            bool on = enabled.Contains(option.id);
            bool exclusive = config.IsExclusive(option);
            void SetShape(string mesh, string name, float value)
            {
                var key = (mesh, name);
                if (!plan.Shapes.ContainsKey(key)) plan.Shapes[key] = 0;
                if (on) plan.Shapes[key] = value;
                if (on || exclusive) plan.LockedShapes.Add(key);
            }
            void SetObject(string path, bool active)
            {
                if (!plan.Objects.ContainsKey(path)) plan.Objects[path] = null;
                if (on) plan.Objects[path] = active;
                if (on || exclusive) plan.LockedObjects.Add(path);
            }
            foreach (var shape in option.blendshapes) SetShape(shape.mesh, shape.name, shape.value);
            foreach (var state in option.gameObjects) SetObject(state.path, state.active);
            foreach (var clip in option.animations.Select(Clip).Where(c => c != null))
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    float value = AnimationUtility.GetEditorCurve(clip, binding).Evaluate(0);
                    if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        SetShape(binding.path, binding.propertyName.Substring("blendShape.".Length), value);
                    else if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
                        SetObject(binding.path, value > .5f);
                    else
                    {
                        if (!plan.Properties.ContainsKey(binding)) plan.Properties[binding] = null;
                        if (on) plan.Properties[binding] = value;
                    }
                }
        }
        return plan;
    }

    public static void Apply(MyCustomBase owner)
    {
        var config = owner.appliedCustomization?.modes;
        if (config?.options == null || config.options.Count == 0) return;
        var root = AvatarPaths.Root(owner);
        ValidateTargets(root, config);
        var plan = BuildPlan(config, EnabledIds(owner));
        Undo.RecordObject(owner, "Apply MCB modes");
        foreach (var shape in plan.Shapes)
        {
            var smr = AvatarPaths.Find(root, shape.Key.mesh)?.GetComponent<SkinnedMeshRenderer>();
            int index = smr != null && smr.sharedMesh != null ? smr.sharedMesh.GetBlendShapeIndex(shape.Key.name) : -1;
            if (index < 0) { MCBLogger.LogWarning($"[MCB] A mode sets '{shape.Key.mesh}/{shape.Key.name}', which this avatar does not have."); continue; }
            if (!owner.modeOriginalShapes.Any(s => s.target == smr && s.name == shape.Key.name))
                owner.modeOriginalShapes.Add(new ModeOriginalShape { target = smr, name = shape.Key.name, value = smr.GetBlendShapeWeight(index) });
            Undo.RecordObject(smr, "Apply MCB modes");
            smr.SetBlendShapeWeight(index, shape.Value);
            EditorUtility.SetDirty(smr);
        }
        foreach (var state in plan.Objects)
        {
            var target = AvatarPaths.Find(root, state.Key)?.gameObject;
            if (target == null) { MCBLogger.LogWarning($"[MCB] A mode sets '{state.Key}', which this avatar does not have."); continue; }
            var original = owner.modeOriginalObjects.FirstOrDefault(o => o.target == target);
            if (original == null) { original = new ModeOriginalObject { target = target, active = target.activeSelf }; owner.modeOriginalObjects.Add(original); }
            Undo.RecordObject(target, "Apply MCB modes");
            target.SetActive(state.Value ?? original.active);
            EditorUtility.SetDirty(target);
        }
        var values = new Dictionary<EditorCurveBinding, float>();
        foreach (var property in plan.Properties)
        {
            var binding = property.Key;
            var original = owner.modeOriginalProperties.FirstOrDefault(p => Matches(p, binding));
            if (original == null)
            {
                if (!AnimationUtility.GetFloatValue(root.gameObject, binding, out float current)) continue; // Not on this avatar.
                original = new ModeOriginalProperty { path = binding.path, type = binding.type.AssemblyQualifiedName, property = binding.propertyName, value = current };
                owner.modeOriginalProperties.Add(original);
            }
            values[binding] = property.Value ?? original.value;
        }
        Write(root, values, "Apply MCB modes");
        EditorUtility.SetDirty(owner);
    }

    public static void Restore(MyCustomBase owner)
    {
        Undo.RecordObject(owner, "Restore MCB modes");
        foreach (var original in owner.modeOriginalObjects)
            if (original.target != null) { Undo.RecordObject(original.target, "Restore MCB modes"); original.target.SetActive(original.active); }
        foreach (var original in owner.modeOriginalShapes)
            if (original.target?.sharedMesh != null)
            {
                int index = original.target.sharedMesh.GetBlendShapeIndex(original.name);
                if (index >= 0) { Undo.RecordObject(original.target, "Restore MCB modes"); original.target.SetBlendShapeWeight(index, original.value); }
            }
        var root = AvatarPaths.Root(owner);
        var values = new Dictionary<EditorCurveBinding, float>();
        foreach (var original in owner.modeOriginalProperties)
        {
            var type = Type.GetType(original.type);
            if (type != null) values[EditorCurveBinding.FloatCurve(original.path, type, original.property)] = original.value;
        }
        Write(root, values, "Restore MCB modes");
        owner.modeOriginalObjects.Clear(); owner.modeOriginalShapes.Clear(); owner.modeOriginalProperties.Clear();
        EditorUtility.SetDirty(owner);
    }

    public static void Install(MyCustomBase owner, CustomBaseVersion version)
    {
        owner.appliedCustomization = VersionCustomization.Read(version?.extraCustomization);
        owner.appliedCustomization.Validate();
        var config = owner.appliedCustomization.modes;
        if (config.options.Count > 0)
        {
            // A choice this version cannot honour (an unknown genre) becomes its default and persists on later switches.
            ValidateTargets(AvatarPaths.Root(owner), config);
            Remember(owner, config, EnabledIds(owner));
            Apply(owner);
        }
        EditorUtility.SetDirty(owner);
    }

    private static bool Matches(ModeOriginalProperty original, EditorCurveBinding binding) =>
        original.path == binding.path && original.property == binding.propertyName && Type.GetType(original.type) == binding.type;

    /// <summary>Writes animated values: transform components directly, other properties by sampling a constant clip.</summary>
    private static void Write(Transform root, Dictionary<EditorCurveBinding, float> values, string undo)
    {
        var sampled = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
        bool sample = false;
        try
        {
            foreach (var group in values.GroupBy(v => v.Key.path))
            {
                var target = AvatarPaths.Find(root, group.Key) ?? (group.Key.Length == 0 ? root : null);
                if (target == null) continue;
                var transformValues = group.Where(v => v.Key.type == typeof(Transform)).ToDictionary(v => v.Key.propertyName, v => v.Value);
                if (transformValues.Count > 0)
                {
                    Undo.RecordObject(target, undo);
                    float Get(string name, float fallback) => transformValues.TryGetValue(name, out float value) ? value : fallback;
                    var p = target.localPosition; var s = target.localScale; var q = target.localRotation; var e = target.localEulerAngles;
                    target.localPosition = new Vector3(Get("m_LocalPosition.x", p.x), Get("m_LocalPosition.y", p.y), Get("m_LocalPosition.z", p.z));
                    target.localScale = new Vector3(Get("m_LocalScale.x", s.x), Get("m_LocalScale.y", s.y), Get("m_LocalScale.z", s.z));
                    if (transformValues.Keys.Any(k => k.StartsWith("m_LocalRotation.", StringComparison.Ordinal)))
                        target.localRotation = new Quaternion(Get("m_LocalRotation.x", q.x), Get("m_LocalRotation.y", q.y), Get("m_LocalRotation.z", q.z), Get("m_LocalRotation.w", q.w)).normalized;
                    else if (transformValues.Keys.Any(k => k.StartsWith("localEulerAngles", StringComparison.Ordinal)))
                        target.localEulerAngles = new Vector3(Get("localEulerAnglesRaw.x", e.x), Get("localEulerAnglesRaw.y", e.y), Get("localEulerAnglesRaw.z", e.z));
                    EditorUtility.SetDirty(target);
                }
                foreach (var other in group.Where(v => v.Key.type != typeof(Transform)))
                {
                    var component = other.Key.type == typeof(GameObject) ? (UnityEngine.Object)target.gameObject : target.GetComponent(other.Key.type);
                    if (component != null) Undo.RecordObject(component, undo);
                    AnimationUtility.SetEditorCurve(sampled, other.Key, AnimationCurve.Constant(0, 1, other.Value));
                    sample = true;
                }
            }
            if (sample) sampled.SampleAnimation(root.gameObject, 0);
        }
        finally { UnityEngine.Object.DestroyImmediate(sampled); }
    }
}
