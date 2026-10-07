using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

/// <summary>Capture on the build copy before armature merges; apply after animation and clothing links.</summary>
public static class VersionCustomizationBuild
{
    private sealed class Property
    {
        public Transform Target;
        public Type Type;
        public string Name;
        public float Value;
    }
    private sealed class Plan
    {
        public readonly List<Property> Properties = new List<Property>();
        public readonly List<TwistBoneService.Target> Twists = new List<TwistBoneService.Target>();
        // Chain kinds the version supports: true adds PhysBones to their chains, false strips them.
        public readonly Dictionary<PhysicService.Kind, bool> Chains = new Dictionary<PhysicService.Kind, bool>();
        public readonly Dictionary<PhysicService.Kind, PhysBoneSettings> Settings = new Dictionary<PhysicService.Kind, PhysBoneSettings>();
        // The custom base's renderers: the blendshapes nothing uses leave their meshes.
        public readonly List<SkinnedMeshRenderer> Base = new List<SkinnedMeshRenderer>();
        // Bones resting in another frame than on the original base model: animations written for that model are turned.
        public Dictionary<Transform, BoneFrameRetarget.Frame> Frames = new Dictionary<Transform, BoneFrameRetarget.Frame>();
        public bool Applied;
    }
    // Re-exported bone rolls (Ultirex V5.1's toes: 100–180°); pose differences kept by the version stay well below.
    private const float ReorientedAngle = 20f;
    private static readonly ConditionalWeakTable<GameObject, Plan> Plans = new ConditionalWeakTable<GameObject, Plan>();

    public static void Capture(GameObject avatar)
    {
        if (Plans.TryGetValue(avatar, out var previous) && previous.Applied) return;
        var owner = avatar.GetComponentInChildren<MyCustomBase>(true);
        if (owner == null || owner.appliedCustomBaseAssetId <= 0) return;
        var plan = new Plan();
        plan.Base.AddRange(MCBReFitIntegration.GetCustomBaseRenderers(owner));
        plan.Frames = ReorientedBones(owner, avatar.transform, plan.Base);
        Plans.Remove(avatar); Plans.Add(avatar, plan);
        var config = owner.appliedCustomization;
        if (config == null)
        {
            if (plan.Frames.Count > 0) AttachmentAnimationBuild.Prepare(avatar);
            return;
        }
        config.Validate();
        if (config.modes.options.Count > 0)
        {
            // Bone poses and other animated values were applied to the scene with the modes; the build keeps them.
            // Blendshapes and objects are locked against the avatar's own animations.
            ModeService.ValidateTargets(avatar.transform, config.modes);
            var modes = ModeService.BuildPlan(config.modes, ModeService.EnabledIds(owner));
            foreach (var shape in modes.LockedShapes)
            {
                var target = AvatarPaths.Find(avatar.transform, shape.mesh);
                if (target == null) continue;
                plan.Properties.Add(new Property { Target = target, Type = typeof(SkinnedMeshRenderer), Name = "blendShape." + shape.name, Value = modes.Shapes[shape] });
            }
            foreach (string path in modes.LockedObjects)
            {
                var target = AvatarPaths.Find(avatar.transform, path);
                if (target == null) continue;
                bool active = modes.Objects[path] ?? owner.modeOriginalObjects.FirstOrDefault(o => o.target == target.gameObject)?.active ?? target.gameObject.activeSelf;
                plan.Properties.Add(new Property { Target = target, Type = typeof(GameObject), Name = "m_IsActive", Value = active ? 1 : 0 });
            }
        }
        foreach (var twist in config.twistBones) plan.Twists.Add(TwistBoneService.Resolve(avatar.transform, twist));
        foreach (var kind in PhysicService.Kind.All)
            if (kind.SupportedBy(config)) { plan.Chains[kind] = kind.EnabledOn(owner); plan.Settings[kind] = kind.SettingsOf(config); }
        var moved = MovedBones(owner, avatar.transform);
        // Own every authored controller graph too, including avatars that do not use VRCFury. Stripping physic keeps
        // its meshes in that build data.
        if (plan.Properties.Count > 0 || plan.Chains.ContainsValue(false) || moved.Count > 0 || plan.Frames.Count > 0)
        {
            var build = AttachmentAnimationBuild.Prepare(avatar);
            // The version reparented these bones; the avatar's own animations still use their former paths.
            build.Moved(moved);
        }
    }

    /// <summary>Bones the applied version moved to another parent, with the path they had on the original avatar.</summary>
    internal static List<(Transform target, string formerPath)> MovedBones(MyCustomBase owner, Transform root)
    {
        var original = new Dictionary<Transform, Transform>();
        foreach (var record in owner.nativeMeshOriginalParents)
            if (record?.target != null && record.parent != null && record.target.IsChildOf(root)) original[record.target] = record.parent;
        string Former(Transform t, int depth)
        {
            if (depth > 256) throw new InvalidOperationException("The recorded original skeleton has a parent cycle at " + t.name + ".");
            var parent = original.TryGetValue(t, out var recorded) ? recorded : t.parent;
            return parent == null || parent == root ? t.name : Former(parent, depth + 1) + "/" + t.name;
        }
        return original.Keys.Select(t => (t, Former(t, 0))).Where(m => m.Item2 != AnimationUtility.CalculateTransformPath(m.t, root)).ToList();
    }

    /// <summary>
    /// The custom base's bones whose frame turned from the original base model's (the base FBX with its skeleton), compared in the
    /// avatar's applied pose: a re-exported model can roll bones while keeping their shape, and the avatar's animations,
    /// written for the original, store absolute rotations. Humanoid bones are posed through muscles and stay out.
    /// </summary>
    internal static Dictionary<Transform, BoneFrameRetarget.Frame> ReorientedBones(MyCustomBase owner, Transform root, IEnumerable<SkinnedMeshRenderer> renderers)
    {
        var frames = new Dictionary<Transform, BoneFrameRetarget.Frame>();
        var bones = renderers.Where(r => r != null).SelectMany(r => r.bones).Where(b => b != null && b != root && b.IsChildOf(root)).Distinct().ToList();
        // The base model with this skeleton: a manually listed or recorded model can be a prop (a face tracking debug panel).
        var model = AvatarBaseModels.WithSkeleton(owner.baseFbxFiles, bones);
        if (model == null) return frames;
        var original = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var animator = root.GetComponent<Animator>();
        var humanoid = new HashSet<string>(animator != null && animator.avatar != null && animator.avatar.isHuman
            ? animator.avatar.humanDescription.human.Select(h => h.boneName) : Enumerable.Empty<string>(), StringComparer.Ordinal);
        Quaternion Rest(Transform t, Transform space) => Quaternion.Inverse(space.rotation) * t.rotation;
        foreach (var bone in bones)
        {
            if (humanoid.Contains(bone.name) || bone.parent == null || !original.TryGetValue(bone.name, out var before) || before.parent == null) continue;
            var frame = BoneFrameRetarget.Frame.Between(Rest(before.parent, model.transform), Rest(before, model.transform), Rest(bone.parent, root), Rest(bone, root));
            if (Quaternion.Angle(frame.Left, Quaternion.identity) > ReorientedAngle || Quaternion.Angle(frame.Right, Quaternion.identity) > ReorientedAngle)
                frames[bone] = frame;
        }
        return frames;
    }

    // After VRCFury: its merged controllers (the logic's own animations included) are build copies by now.
    private static void Reorient(GameObject avatar, IEnumerable<AnimatorController> controllers, Dictionary<Transform, BoneFrameRetarget.Frame> frames)
    {
        var byPath = new Dictionary<string, BoneFrameRetarget.Frame>(StringComparer.Ordinal);
        foreach (var pair in frames)
            if (pair.Key != null && pair.Key.IsChildOf(avatar.transform)) byPath[AnimationUtility.CalculateTransformPath(pair.Key, avatar.transform)] = pair.Value;
        var clips = controllers.SelectMany(c => c.animationClips).Where(AttachmentAnimationBuild.IsBuildData).ToList();
        int changed = BoneFrameRetarget.Retarget(clips, byPath);
        if (changed > 0) MCBLogger.Log("[MCB] " + changed + " animations follow the custom base's re-oriented bones (" + string.Join(", ", frames.Keys.Where(k => k != null).Select(k => k.name).Take(6)) + (frames.Count > 6 ? "…" : "") + ").");
    }

    public static void Apply(GameObject avatar)
    {
        if (!Plans.TryGetValue(avatar, out var plan) || plan.Applied) return;
        var properties = new Dictionary<(string, Type, string), float>();
        foreach (var property in plan.Properties)
        {
            if (property.Target == null || !property.Target.IsChildOf(avatar.transform))
                throw new InvalidOperationException("A mode-controlled object was removed during the avatar build: " + property.Name);
            string path = AnimationUtility.CalculateTransformPath(property.Target, avatar.transform);
            properties.Add((path, property.Type, property.Name), property.Value);
            if (property.Type == typeof(GameObject)) property.Target.gameObject.SetActive(property.Value > .5f);
            else
            {
                var renderer = property.Target.GetComponent<SkinnedMeshRenderer>();
                int index = renderer.sharedMesh.GetBlendShapeIndex(property.Name.Substring("blendShape.".Length));
                if (index < 0) throw new InvalidOperationException("A mode blendshape was removed by an optimizer: " + property.Name);
                renderer.SetBlendShapeWeight(index, property.Value);
            }
        }
        var controllers = BlendShapeLinkEngine.CollectBuiltControllers(avatar).ToArray();
        if (plan.Frames.Count > 0) Reorient(avatar, controllers, plan.Frames);
        foreach (var clip in controllers.SelectMany(c => c.animationClips).Where(c => c != null).Distinct())
        {
            // Remove all competing writers, including additive layers. A final override writes the fixed values once.
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (properties.ContainsKey((binding.path, binding.type, binding.propertyName))) AnimationUtility.SetEditorCurve(clip, binding, null);
        }
        foreach (var controller in controllers.Where(c => properties.Count > 0))
        {
            var clip = new AnimationClip { name = "MCB Modes", hideFlags = HideFlags.HideInHierarchy };
            foreach (var property in properties)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(property.Key.Item1, property.Key.Item2, property.Key.Item3), AnimationCurve.Constant(0, 1, property.Value));
            var machine = new AnimatorStateMachine { name = "MCB Modes", hideFlags = HideFlags.HideInHierarchy };
            if (AssetDatabase.Contains(controller)) { AssetDatabase.AddObjectToAsset(clip, controller); AssetDatabase.AddObjectToAsset(machine, controller); }
            var state = machine.AddState("Locked modes"); state.motion = clip; state.writeDefaultValues = false; machine.defaultState = state;
            controller.AddLayer(new AnimatorControllerLayer { name = "MCB Modes", stateMachine = machine, defaultWeight = 1, blendingMode = AnimatorLayerBlendingMode.Override });
        }
        // The animations are final from here: the unused blendshapes leave the meshes before the twist and physic copy them.
        TwistBoneService.Generate(avatar, plan.Twists, PruneBlendShapes(avatar, plan));
        // After armature links: clothing merged onto the chains is rebound with the body.
        var stripped = plan.Chains.Where(pair => !pair.Value).Select(pair => pair.Key).ToList();
        if (stripped.Count > 0) PhysicService.Strip(avatar, stripped, AttachmentAnimationBuild.Prepare(avatar).Keep);
        foreach (var pair in plan.Chains.Where(pair => pair.Value)) PhysicService.AddPhysBones(avatar, pair.Key, plan.Settings[pair.Key]);
        plan.Applied = true;
    }

    // Several hundred MB on a sculpted body (Ultirex: 380 unused shapes, 448 MB), over VRChat's upload limits. Play mode
    // keeps them: it uploads nothing and enters faster.
    private static Dictionary<SkinnedMeshRenderer, Mesh> PruneBlendShapes(GameObject avatar, Plan plan)
    {
        var copies = new Dictionary<SkinnedMeshRenderer, Mesh>();
        if (EditorApplication.isPlayingOrWillChangePlaymode) return copies;
        int removed = 0;
        foreach (var renderer in plan.Base.Where(r => r != null && r.sharedMesh != null && r.transform.IsChildOf(avatar.transform)))
        {
            int before = renderer.sharedMesh.blendShapeCount;
            var copy = BlendShapePruning.Prune(avatar, renderer);
            if (copy == null) continue;
            removed += before - copy.blendShapeCount;
            copies.Add(renderer, copy);
        }
        if (removed > 0) MCBLogger.Log("[MCB] " + removed + " unused blendshapes left the custom base's meshes (" + copies.Count + " renderers).");
        return copies;
    }
}

internal sealed class VersionCustomizationCaptureHook : IVRCSDKPreprocessAvatarCallback
{
    public int callbackOrder => -10105;
    public bool OnPreprocessAvatar(GameObject avatarGameObject)
    {
        try { VersionCustomizationBuild.Capture(avatarGameObject); return true; }
        catch (Exception ex) { Debug.LogException(ex); return false; }
    }
}

internal sealed class VersionCustomizationApplyHook : IVRCSDKPreprocessAvatarCallback
{
    public int callbackOrder => -8800;
    public bool OnPreprocessAvatar(GameObject avatarGameObject)
    {
        try { VersionCustomizationBuild.Apply(avatarGameObject); return true; }
        catch (Exception ex) { Debug.LogException(ex); return false; }
    }
}
