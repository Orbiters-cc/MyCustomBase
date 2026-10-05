using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
        public bool Applied;
    }
    private static readonly ConditionalWeakTable<GameObject, Plan> Plans = new ConditionalWeakTable<GameObject, Plan>();

    public static void Capture(GameObject avatar)
    {
        if (Plans.TryGetValue(avatar, out var previous) && previous.Applied) return;
        var owner = avatar.GetComponentInChildren<MyCustomBase>(true);
        if (owner == null) return;
        var config = owner.appliedCustomization;
        if (config == null) return;
        config.Validate();
        var plan = new Plan();
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
        Plans.Remove(avatar); Plans.Add(avatar, plan);
        // Own every authored controller graph too, including avatars that do not use VRCFury.
        if (plan.Properties.Count > 0) AttachmentAnimationBuild.Prepare(avatar);
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
        foreach (var twist in plan.Twists) TwistBoneService.Generate(avatar, twist);
        plan.Applied = true;
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
