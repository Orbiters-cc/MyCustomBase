using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Build;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

/// <summary>
/// Capture on the build copy before armature merges; lock the modes' values after the correctives and before the clothing
/// links (which then copy the locked values onto clothing too); apply the rest (re-oriented bones, twists, pruning, physic)
/// after every animation-adding step.
/// </summary>
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
        // The locked values the avatar's own animations write, right after VRCFury built them: others are added later by
        // build steps (face tracking, correctives) that the lock then overrides.
        public HashSet<(string, Type, string)> OwnAnimated;
        public bool Locked, Applied;
    }
    // The build's plan (BuildContext.State): Capture makes it, the later steps of the same build read it.
    private sealed class Planned { public Plan Plan; }
    // Re-exported bone rolls (Ultirex V5.1's toes: 100–180°); pose differences kept by the version stay well below.
    private const float ReorientedAngle = 20f;
    // Objects whose build applied MCB's plan already: building one again (Play Mode builds the scene's avatar, which then
    // keeps what the first build did) changes nothing more. Only this verdict outlives a build.
    private static readonly ConditionalWeakTable<GameObject, Plan> AppliedBuilds = new ConditionalWeakTable<GameObject, Plan>();

    // The plan of the build of avatar under way (a caller running the steps by hand gets a build of its own), or null.
    private static Plan PlanOf(GameObject avatar) => BuildContext.Of(avatar)?.State<Planned>().Plan;

    public static void Capture(GameObject avatar)
    {
        if (AppliedBuilds.TryGetValue(avatar, out _)) return;
        var owner = avatar.GetComponentInChildren<MyCustomBase>(true);
        if (owner == null || owner.appliedCustomBaseAssetId <= 0) return;
        var plan = new Plan();
        plan.Base.AddRange(MCBReFitIntegration.GetCustomBaseRenderers(owner));
        plan.Frames = ReorientedBones(owner, avatar.transform, plan.Base);
        BuildContext.For(avatar).State<Planned>().Plan = plan;
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
                plan.Properties.Add(new Property { Target = target, Type = typeof(SkinnedMeshRenderer), Name = "blendShape." + shape.name,
                    Value = ModeService.ShapeValue(owner, modes, shape, target.GetComponent<SkinnedMeshRenderer>()) });
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

    /// <summary>Notes which locked values the avatar's own animations write, once VRCFury built them (see <see cref="Lock"/>).</summary>
    public static void Survey(GameObject avatar)
    {
        var plan = PlanOf(avatar);
        if (plan == null || plan.Locked || plan.Properties.Count == 0) return;
        var locked = new HashSet<(string, Type, string)>(plan.Properties.Where(p => p.Target != null && p.Target.IsChildOf(avatar.transform))
            .Select(p => (AnimationUtility.CalculateTransformPath(p.Target, avatar.transform), p.Type, p.Name)));
        plan.OwnAnimated = new HashSet<(string, Type, string)>(Animated(avatar).Where(locked.Contains));
    }

    private static IEnumerable<(string, Type, string)> Animated(GameObject avatar) =>
        BlendShapeLinkEngine.CollectBuiltControllers(avatar).SelectMany(c => c.animationClips).Where(c => c != null).Distinct()
            .SelectMany(AnimationUtility.GetCurveBindings).Select(b => (b.path, b.type, b.propertyName));

    /// <summary>
    /// Locks the enabled modes' blendshapes and objects: the renderers' weights, no other animation writing them, and a
    /// constant override layer. Runs before the clothing links (refits, Follow Body Blendshapes, accessories), so they copy
    /// the locked values onto the clothing shapes too.
    /// </summary>
    public static void Lock(GameObject avatar)
    {
        var plan = PlanOf(avatar);
        if (plan == null || plan.Locked) return;
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
        var overridden = new HashSet<(string, Type, string)>();
        foreach (var clip in controllers.SelectMany(c => c.animationClips).Where(c => c != null).Distinct())
        {
            // Remove all competing writers, including additive layers. A final override writes the fixed values once.
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var key = (binding.path, binding.type, binding.propertyName);
                if (!properties.ContainsKey(key)) continue;
                AnimationUtility.SetEditorCurve(clip, binding, null);
                if (plan.OwnAnimated != null && !plan.OwnAnimated.Contains(key)) overridden.Add(key);
            }
        }
        // Animations other build steps added (My Avatar's face tracking, another tool's layers) lose to the mode: say so.
        foreach (var key in overridden)
            Debug.LogWarning("[MCB] The enabled modes keep " + (key.Item2 == typeof(GameObject) ? "object '" + key.Item1 + "' " + (properties[key] > .5f ? "on" : "off")
                : "blendshape '" + key.Item3.Substring("blendShape.".Length) + "' of '" + key.Item1 + "' at " + properties[key]) +
                ", which a build step after VRCFury also animates (face tracking or another tool): the mode overrides those animations.", avatar);
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
        plan.Locked = true;
    }

    public static void Apply(GameObject avatar)
    {
        var plan = PlanOf(avatar);
        if (plan == null || plan.Applied) return;
        Lock(avatar);
        if (plan.Frames.Count > 0) Reorient(avatar, BlendShapeLinkEngine.CollectBuiltControllers(avatar), plan.Frames);
        // The animations are final from here: the unused blendshapes leave the meshes before the twist and physic copy them.
        TwistBoneService.Generate(avatar, plan.Twists, PruneBlendShapes(avatar, plan));
        // After armature links: clothing merged onto the chains is rebound with the body.
        var stripped = plan.Chains.Where(pair => !pair.Value).Select(pair => pair.Key).ToList();
        if (stripped.Count > 0) PhysicService.Strip(avatar, stripped, AttachmentAnimationBuild.Prepare(avatar).Keep);
        foreach (var pair in plan.Chains.Where(pair => pair.Value)) PhysicService.AddPhysBones(avatar, pair.Key, plan.Settings[pair.Key]);
        plan.Applied = true;
        AppliedBuilds.Remove(avatar);
        AppliedBuilds.Add(avatar, plan);
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

// MCB's steps in Orbiters' build (Toolkit's AvatarBuildPipeline), each where its SDK callback ran: the capture at
// -10105 (after N4N0's hooks, ReFit's capture -10110 and kept fits -10105, before the Toolkit's attachments -10100), the
// survey at -9500 (right after VRCFury and the Toolkit's built-animations step -9999, before My Avatar's -9010), the apply
// at -8800 (after Follow Body Blendshapes -8950 and the accessories' finish -8900, before My Avatar's -1100). The lock
// (-8970) and the correctives (-9000) stay SDK callbacks: no phase sits between My Avatar's -9005 and ReFit's -8960.
internal static class McbBuildSteps
{
    // The Toolkit's own steps are internal to its assembly: named here. A name that no longer finds its type would drop
    // the ordering silently (the pipeline ignores null), so it is an error at load, and the steps ordered against it
    // refuse to run (the build stops) rather than run in another place.
    private static readonly List<string> Missing = new List<string>();
    internal static Type Toolkit(string name)
    {
        var type = typeof(BuildStep).Assembly.GetType(name);
        if (type == null) Missing.Add(name);
        return type;
    }
    internal static readonly Type Attachments = Toolkit("Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentBuildHook");
    internal static readonly Type AttachmentsFinish = Toolkit("Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentFinishHook");
    internal static readonly Type SurfaceFollow = Toolkit("Orbiters.Toolkit.Editor.VRChat.SurfaceFollow.SurfaceFollowHook");
    internal static readonly Type BuiltAnimations = Toolkit("Orbiters.Toolkit.Editor.VRChat.VrcFuryBuiltAnimationsHook");

    internal static IReadOnlyList<string> MissingSteps => Missing;

    [InitializeOnLoadMethod]
    private static void Check()
    {
        if (Missing.Count > 0) Debug.LogError(MissingMessage("MCB's build steps"));
    }

    private static string MissingMessage(string who) =>
        "[MCB] " + who + " cannot keep their place in the avatar build: Orbiters Toolkit has no build step " + string.Join(", ", Missing) +
        " any more (renamed?). MCB must be updated to order against what replaced it.";

    internal static bool Run(Action<GameObject> step, GameObject avatar)
    {
        try { step(avatar); return true; }
        catch (Exception ex) { Debug.LogException(ex); return false; }
    }

    /// <summary>Runs a pipeline step whose order needs <paramref name="constraints"/>: refused (the build stops) when one is missing.</summary>
    internal static bool Run(BuildStep owner, IReadOnlyList<Type> constraints, Action<GameObject> step, GameObject avatar)
    {
        foreach (var type in constraints)
            if (type == null)
            {
                Debug.LogError(MissingMessage(owner.GetType().Name));
                return false;
            }
        return Run(step, avatar);
    }
}

// Where its SDK callback ran: it shared -10105 with ReFit's kept fits and the SDK ran MCB's first (the Toolkit's phase
// of that name records how that is known), so before them, and before the attachment copies of the next phase.
internal sealed class VersionCustomizationCaptureHook : BuildStep
{
    public override BuildPhase Phase => BuildPhase.BeforeRefitKeptFits;
    public override string Owner => "MCB";
    public override bool Run(BuildContext context) => McbBuildSteps.Run(VersionCustomizationBuild.Capture, context.Avatar);
}

// Right after VRCFury (-10000) built the avatar's animations, before face tracking (-9010) and the correctives (-9000).
internal sealed class VersionCustomizationSurveyHook : BuildStep
{
    public override BuildPhase Phase => BuildPhase.AfterVrcFury;
    public override IReadOnlyList<Type> After => new[] { McbBuildSteps.BuiltAnimations };
    public override string Owner => "MCB";
    public override bool Run(BuildContext context) => McbBuildSteps.Run(this, After, VersionCustomizationBuild.Survey, context.Avatar);
}

// After the correctives (-9000), before the clothing links copy body curves: ReFit (-8960), Follow Body Blendshapes
// (-8950) and accessories (-8900). An SDK callback: no phase of Orbiters' build sits there.
internal sealed class VersionCustomizationLockHook : IVRCSDKPreprocessAvatarCallback
{
    public int callbackOrder => -8970;
    public bool OnPreprocessAvatar(GameObject avatarGameObject) => McbBuildSteps.Run(VersionCustomizationBuild.Lock, avatarGameObject);
}

// After every animation-adding step: pruning must see the final animations.
internal sealed class VersionCustomizationApplyHook : BuildStep
{
    public override BuildPhase Phase => BuildPhase.AccessoryShapes;
    public override IReadOnlyList<Type> After => new[] { McbBuildSteps.SurfaceFollow, McbBuildSteps.AttachmentsFinish };
    public override string Owner => "MCB";
    public override bool Run(BuildContext context) => McbBuildSteps.Run(this, After, VersionCustomizationBuild.Apply, context.Avatar);
}
