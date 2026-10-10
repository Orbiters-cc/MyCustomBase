#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;

/// <summary>
/// Creator check of an applied version: builds a disposable preview clone of the avatar with VRCFury (MCB's own build
/// steps included) and reports what VRChat would get: the logic's proxies merged into the avatar's bones, contact
/// parameters present in the built controllers, animation paths that resolve nowhere, and the SDK performance figures.
/// The scene avatar is never changed.
/// </summary>
public static class MCBVersionValidation
{
    public static object Run(MyCustomBase owner)
    {
        if (owner == null || owner.appliedCustomBaseAssetId <= 0) throw new ArgumentException("Apply a version to the avatar first.");
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject clone = null;
        try
        {
            var source = AvatarPaths.Root(owner).gameObject;
            clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name + " (MCB validation)";
            SceneManager.MoveGameObjectToScene(clone, scene);
            clone.SetActive(true);
            var logic = clone.transform.Find(MCBLogicPrefabService.LogicRootName);
            var proxies = logic != null && logic.Find(MCBLogicPrefabService.TargetBonesName) is Transform targets
                ? targets.Cast<Transform>().ToList() : new List<Transform>();
            var receivers = clone.GetComponentsInChildren<VRCContactReceiver>(true).Where(r => logic != null && r.transform.IsChildOf(logic)).ToList();
            var senders = clone.GetComponentsInChildren<VRCContactSender>(true).Where(s => logic != null && s.transform.IsChildOf(logic)).ToList();

            VersionCustomizationBuild.Capture(clone);
            RunVrcFury(clone);
            VersionCustomizationBuild.Apply(clone);

            var controllers = Orbiters.Toolkit.Editor.VRChat.BlendShapes.BlendShapeLinkEngine.CollectBuiltControllers(clone).ToArray();
            var parameters = new HashSet<string>(controllers.OfType<AnimatorController>().SelectMany(c => c.parameters).Select(p => p.name), StringComparer.Ordinal);
            var unlinked = proxies.Where(p => p != null && p.IsChildOf(clone.transform) && p.parent != null && p.parent.name == MCBLogicPrefabService.TargetBonesName)
                .Select(p => p.name).ToArray();
            var missingParameters = receivers.Where(r => !string.IsNullOrEmpty(r.parameter) && !parameters.Contains(r.parameter))
                .Select(r => r.name + " -> " + r.parameter).Distinct().ToArray();
            var unresolved = controllers.SelectMany(c => c.animationClips).Distinct()
                .SelectMany(c => AnimationUtility.GetCurveBindings(c).Concat(AnimationUtility.GetObjectReferenceCurveBindings(c)))
                .Where(b => !string.IsNullOrEmpty(b.path) && clone.transform.Find(b.path) == null)
                .Select(b => b.path).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var stats = new VRC.SDKBase.Validation.Performance.Stats.AvatarPerformanceStats(false);
            VRC.SDKBase.Validation.Performance.AvatarPerformance.CalculatePerformanceStats(clone.name, clone, stats, false);
            return new
            {
                success = unlinked.Length == 0 && missingParameters.Length == 0,
                proxies = proxies.Count, unlinkedProxies = unlinked,
                receivers = receivers.Count, senders = senders.Count,
                receiversOnBones = receivers.Count(r => r.rootTransform != null && !r.rootTransform.IsChildOf(logic)),
                missingParameters,
                builtControllers = controllers.Length,
                unresolvedPaths = unresolved.Length, unresolvedSample = unresolved.Take(10).ToArray(),
                performance = new
                {
                    rating = stats.GetPerformanceRatingForCategory(VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory.Overall).ToString(),
                    triangles = stats.polyCount, bones = stats.boneCount, contacts = stats.contactCount,
                    physBones = stats.physBone?.componentCount, physBoneTransforms = stats.physBone?.transformCount,
                },
            };
        }
        finally
        {
            if (clone != null)
            {
                Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentAnimationBuild.Release(clone);
                UnityEngine.Object.DestroyImmediate(clone);
            }
            EditorSceneManager.ClosePreviewScene(scene);
            EditorUtility.ClearProgressBar();
        }
    }

    // VRCFury's own upload/test entry point, without the SDK (nothing uploaded). Its path caches are warmed first, as the
    // upload's first preprocessor does, and Write Defaults are left as they are: a broken mix would otherwise open
    // VRCFury's modal question in the middle of a check.
    private static void RunVrcFury(GameObject clone)
    {
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        var builder = Find("VF.Builder.VRCFuryBuilder") ?? throw new InvalidOperationException("VRCFury is required to validate a version.");
        var paths = Find("VF.Hooks.OriginalPathsHook");
        if (paths != null) ((VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback)Activator.CreateInstance(paths, true)).OnPreprocessAvatar(clone);
        var fixType = Find("VF.Model.Feature.FixWriteDefaults");
        var vrcFury = Find("VF.Model.VRCFury");
        if (fixType != null && vrcFury != null && !clone.GetComponentsInChildren(vrcFury, true)
                .Any(c => vrcFury.GetField("content").GetValue(c)?.GetType() == fixType))
        {
            var feature = Activator.CreateInstance(fixType, true);
            var mode = fixType.GetField("mode");
            mode.SetValue(feature, Enum.Parse(mode.FieldType, "Disabled"));
            vrcFury.GetField("content").SetValue(clone.AddComponent(vrcFury), feature);
        }
        var wrapper = Find("VF.Utils.VFGameObject");
        var convert = wrapper.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "op_Implicit" && m.ReturnType == wrapper && m.GetParameters()[0].ParameterType == typeof(GameObject));
        try { builder.GetMethod("RunMain", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { convert.Invoke(null, new object[] { clone }) }); }
        catch (TargetInvocationException ex) { throw new InvalidOperationException("VRCFury could not build the avatar: " + ex.GetBaseException().Message, ex.GetBaseException()); }
    }
}
#endif
