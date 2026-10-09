#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;

public partial class BlendShapeLinkService
{
    private static bool TryResolveManualLink(GameObject avatarRoot, BlendShapeFactorLinkEntry link,
        out BlendShapeLink resolved, out string error)
    {
        resolved = default;
        error = null;

        if (link == null)
        {
            error = "Link is null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(link.toFix))
        {
            error = "To-fix target is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(link.fixedBy))
        {
            error = "Fixing target is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(link.factorParameterName))
        {
            error = "Factor parameter name is empty.";
            return false;
        }

        SkinnedMeshRenderer target = null;
        if (link.toFixType == CorrectiveActivationType.Blendshape ||
            link.fixedByType == CorrectiveActivationType.Blendshape)
        {
            if (string.IsNullOrWhiteSpace(link.targetRendererPath))
            {
                error = "Target mesh path is required when using blendshape corrective targets.";
                return false;
            }

            target = FindRendererByPath(avatarRoot, link.targetRendererPath);
            if (target == null || target.sharedMesh == null)
            {
                error = $"Target mesh path '{link.targetRendererPath}' was not found on avatar clone.";
                return false;
            }

            if (link.toFixType == CorrectiveActivationType.Blendshape &&
                target.sharedMesh.GetBlendShapeIndex(link.toFix) < 0)
            {
                error = $"To-fix blendshape '{link.toFix}' was not found on target mesh '{target.name}'.";
                return false;
            }

            if (link.fixedByType == CorrectiveActivationType.Blendshape &&
                target.sharedMesh.GetBlendShapeIndex(link.fixedBy) < 0)
            {
                error = $"Fixing blendshape '{link.fixedBy}' was not found on target mesh '{target.name}'.";
                return false;
            }
        }

        AnimationClip fixedByClip = null;
        if (link.fixedByType == CorrectiveActivationType.Animation)
        {
            fixedByClip = FindAnimationClipsByName(link.fixedBy).FirstOrDefault();
            if (fixedByClip == null)
            {
                error = $"Fixing animation '{link.fixedBy}' was not found in project assets.";
                return false;
            }
        }

        AnimationClipSignature toFixSignature = null;
        if (link.toFixType == CorrectiveActivationType.Animation)
        {
            var toFixClip = FindAnimationClipsByName(link.toFix).FirstOrDefault();
            if (toFixClip != null)
            {
                toFixSignature = AnimationClipSignature.Build(toFixClip);
            }
        }

        resolved = CorrectiveLink(link.targetRendererPath ?? string.Empty, link.toFixType, link.toFix, toFixSignature,
            link.fixedByType, link.fixedBy, fixedByClip, link.factorParameterName, false, 0f);
        return true;
    }

    private static BlendShapeLink CorrectiveLink(string rendererPath, CorrectiveActivationType toFixType, string toFix,
        AnimationClipSignature toFixSignature, CorrectiveActivationType fixedByType, string fixedBy, AnimationClip fixedByClip,
        string factorParameterName, bool setFactorDefault, float factorDefault)
    {
        bool shapeTrigger = toFixType == CorrectiveActivationType.Blendshape;
        bool shapeEffect = fixedByType == CorrectiveActivationType.Blendshape;
        return new BlendShapeLink
        {
            TargetRendererPath = rendererPath,
            TriggerType = ToEndpoint(toFixType),
            TriggerName = toFix,
            TriggerSignature = toFixSignature,
            EffectType = ToEndpoint(fixedByType),
            EffectName = fixedBy,
            EffectClip = fixedByClip,
            SourcePath = shapeTrigger ? rendererPath : string.Empty,
            SourceProperty = shapeTrigger ? "blendShape." + toFix : string.Empty,
            DestinationPath = shapeEffect ? rendererPath : string.Empty,
            DestinationProperty = shapeEffect ? "blendShape." + fixedBy : string.Empty,
            FactorParameter = factorParameterName,
            SetFactorDefault = setFactorDefault,
            FactorDefault = factorDefault
        };
    }

    private static bool TryBuildAndValidateEntry(
        GameObject avatarRoot,
        SkinnedMeshRenderer targetRenderer,
        CorrectiveActivationType toFixType,
        string toFix,
        CorrectiveActivationType fixedByType,
        string fixedBy,
        string factorParameterName,
        bool enabled,
        out BlendShapeFactorLinkEntry entry,
        out string error)
    {
        entry = null;
        error = null;

        if (avatarRoot == null)
        {
            error = "Avatar root is null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(toFix))
        {
            error = "To-fix target is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(fixedBy))
        {
            error = "Fixing target is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(factorParameterName))
        {
            error = "Factor parameter name is empty.";
            return false;
        }

        string targetPath = string.Empty;
        if (toFixType == CorrectiveActivationType.Blendshape || fixedByType == CorrectiveActivationType.Blendshape)
        {
            if (targetRenderer == null)
            {
                error = "Target mesh is required when using blendshape corrective targets.";
                return false;
            }

            if (targetRenderer.sharedMesh == null)
            {
                error = "Target mesh renderer has no shared mesh.";
                return false;
            }

            if (!targetRenderer.transform.IsChildOf(avatarRoot.transform))
            {
                error = "Target mesh is not a child of avatar root.";
                return false;
            }

            if (toFixType == CorrectiveActivationType.Blendshape &&
                targetRenderer.sharedMesh.GetBlendShapeIndex(toFix) < 0)
            {
                error = $"To-fix blendshape '{toFix}' was not found on target mesh.";
                return false;
            }

            if (fixedByType == CorrectiveActivationType.Blendshape &&
                targetRenderer.sharedMesh.GetBlendShapeIndex(fixedBy) < 0)
            {
                error = $"Fixing blendshape '{fixedBy}' was not found on target mesh.";
                return false;
            }

            targetPath = AnimationUtility.CalculateTransformPath(targetRenderer.transform, avatarRoot.transform);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                error = "Failed to compute target mesh transform path.";
                return false;
            }
        }

        if (fixedByType == CorrectiveActivationType.Animation && FindAnimationClipsByName(fixedBy).Count == 0)
        {
            error = $"Fixing animation '{fixedBy}' was not found in project assets.";
            return false;
        }

        entry = new BlendShapeFactorLinkEntry
        {
            enabled = enabled,
            targetRendererPath = targetPath,
            toFixType = toFixType,
            toFix = toFix,
            fixedByType = fixedByType,
            fixedBy = fixedBy,
            factorParameterName = factorParameterName
        };

        return true;
    }

    private static List<AnimationClip> FindAnimationClipsByName(string clipName)
    {
        var clips = new List<AnimationClip>();
        if (string.IsNullOrWhiteSpace(clipName)) return clips;

        string[] guids = AssetDatabase.FindAssets("t:AnimationClip");
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null) continue;
            if (string.Equals(clip.name, clipName, StringComparison.Ordinal))
            {
                clips.Add(clip);
            }
        }

        clips.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
        return clips;
    }

    private static bool IsSameManualLink(BlendShapeFactorLinkEntry a, BlendShapeFactorLinkEntry b)
    {
        if (a == null || b == null) return false;
        return string.Equals(a.targetRendererPath, b.targetRendererPath, StringComparison.Ordinal)
               && a.toFixType == b.toFixType
               && a.fixedByType == b.fixedByType
               && string.Equals(a.toFix, b.toFix, StringComparison.Ordinal)
               && string.Equals(a.fixedBy, b.fixedBy, StringComparison.Ordinal);
    }

    // The avatar's MCB component, also on a build copy whose editor-only components are gone (by its root's name).
    // Shared with the bone position offsets, which run in the same build hook.
    internal static MyCustomBase FindCustomBase(GameObject avatarRoot)
    {
        if (avatarRoot == null) return null;

        var onRoot = avatarRoot.GetComponent<MyCustomBase>();
        if (onRoot != null) return onRoot;

        var inChildren = avatarRoot.GetComponentInChildren<MyCustomBase>(true);
        if (inChildren != null) return inChildren;

        string rootName = avatarRoot.name;
        if (!string.IsNullOrEmpty(rootName) && rootName.EndsWith("(Clone)", StringComparison.Ordinal))
        {
            rootName = rootName.Substring(0, rootName.Length - "(Clone)".Length);
        }

        var all = Resources.FindObjectsOfTypeAll<MyCustomBase>();
        return all.FirstOrDefault(x =>
            x != null &&
            x.gameObject != null &&
            x.gameObject.scene.IsValid() &&
            x.transform.root != null &&
            string.Equals(x.transform.root.name, rootName, StringComparison.Ordinal));
    }

    private static SkinnedMeshRenderer FindRendererByPath(GameObject avatarRoot, string targetPath)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(targetPath)) return null;
        var transform = avatarRoot.transform.Find(targetPath);
        if (transform == null) return null;
        return transform.GetComponent<SkinnedMeshRenderer>();
    }

    private static ConfigResult FailConfig(string message) => new ConfigResult { success = false, message = message };
}
#endif
