#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEngine;

public partial class BlendShapeLinkService
{
    public BlendShapeLinkResult ApplyReFitFlexLinks(GameObject avatarRoot)
    {
        if (avatarRoot == null) return BlendShapeLinkResult.Fail("Avatar root is null.");
        var links = MCBReFitIntegration.GetBuildFlexLinks(avatarRoot);
        if (links.Count == 0) return BlendShapeLinkResult.Fail("No transferred ReFit flex shapes to link.");
        // Use the same safety boundary as manual/version links, including initial weight synchronization.
        if (BlendShapeLinkEngine.CollectBuiltControllers(avatarRoot).Count == 0)
            return BlendShapeLinkResult.Fail("No temporary AnimatorController found for ReFit flex links.");
        var planned = new List<BlendShapeLink>();
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
                planned.Add(BlendShapeLink.Copy(sourcePath, pair.Key, destinationPath, pair.Value));
            }
        }
        return BlendShapeLinkEngine.Apply(avatarRoot, planned, "ReFit flex");
    }
}
#endif
