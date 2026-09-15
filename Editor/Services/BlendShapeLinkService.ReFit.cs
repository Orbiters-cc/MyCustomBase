#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public partial class BlendShapeLinkService
{
    public ApplyResult ApplyReFitFlexLinks(GameObject avatarRoot)
    {
        if (avatarRoot == null) return FailApply("Avatar root is null.");
        var links = MCBReFitIntegration.GetBuildFlexLinks(avatarRoot);
        if (links.Count == 0) return FailApply("No transferred ReFit flex shapes to link.");
        // Use the same safety boundary as manual/version links, including initial weight synchronization.
        if (CollectVrcFuryBuiltControllers(avatarRoot).Count == 0)
            return FailApply("No temporary AnimatorController found for ReFit flex links.");
        var planned = new List<PlannedLink>();
        foreach (var link in links)
        {
            if (link.body == null || link.accessory == null || link.body.sharedMesh == null || link.accessory.sharedMesh == null ||
                !link.body.transform.IsChildOf(avatarRoot.transform) || !link.accessory.transform.IsChildOf(avatarRoot.transform))
                throw new InvalidOperationException("A ReFit flex renderer was removed during build; its links cannot be applied.");
            string sourcePath = AnimationUtility.CalculateTransformPath(link.body.transform, avatarRoot.transform);
            string destinationPath = AnimationUtility.CalculateTransformPath(link.accessory.transform, avatarRoot.transform);
            foreach (var pair in link.mappings)
            {
                int sourceIndex = link.body.sharedMesh.GetBlendShapeIndex(pair.Key);
                int destinationIndex = link.accessory.sharedMesh.GetBlendShapeIndex(pair.Value);
                if (sourceIndex < 0 || destinationIndex < 0)
                    throw new InvalidOperationException("ReFit flex shape removed during build: " + pair.Key + " -> " + pair.Value +
                        ". Keep transferred flex shapes when configuring blendshape optimization.");
                link.accessory.SetBlendShapeWeight(destinationIndex, link.body.GetBlendShapeWeight(sourceIndex));
                planned.Add(new PlannedLink
                {
                    targetRendererPath = destinationPath,
                    toFixType = CorrectiveActivationType.Blendshape, toFixName = pair.Key,
                    fixedByType = CorrectiveActivationType.Blendshape, fixedByName = pair.Value,
                    sourcePath = sourcePath, sourceProperty = "blendShape." + pair.Key,
                    destinationPath = destinationPath, destinationProperty = "blendShape." + pair.Value,
                    copyWithoutFactor = true
                });
            }
        }
        // Reuse the existing clip cloning, nested-motion traversal, FX routing and debug registry.
        // Direct copies need no extra animator parameters or runtime blend-tree wrappers.
        return ApplyPlannedLinks(avatarRoot, planned, "ReFit flex");
    }
}
#endif
