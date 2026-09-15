#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

public static partial class MCBReFitIntegration
{
    internal sealed class BuildFlexLink
    {
        public SkinnedMeshRenderer body;
        public SkinnedMeshRenderer accessory;
        public List<KeyValuePair<string, string>> mappings;
    }

    private static readonly ConditionalWeakTable<GameObject, List<BuildFlexLink>> buildFlexLinks =
        new ConditionalWeakTable<GameObject, List<BuildFlexLink>>();

    internal static List<KeyValuePair<string, string>> GetTransferredFlexMappings(
        RefitAppliedMeshEntry entry, SkinnedMeshRenderer body, SkinnedMeshRenderer accessory)
    {
        var mappings = new List<KeyValuePair<string, string>>();
        if (body == null || accessory == null || accessory == body || body.sharedMesh == null ||
            entry?.refitMesh == null || accessory.sharedMesh != entry.refitMesh ||
            entry.transferredBlendShapeSourceNames == null || entry.transferredBlendShapeNames == null ||
            IsGeneratedEditorOnlyRenderer(accessory)) return mappings;

        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        int count = Math.Min(entry.transferredBlendShapeSourceNames.Count, entry.transferredBlendShapeNames.Count);
        for (int i = 0; i < count; i++)
        {
            string source = entry.transferredBlendShapeSourceNames[i];
            string destination = entry.transferredBlendShapeNames[i];
            if (!IsFlexBlendShape(source) || string.IsNullOrEmpty(destination) ||
                body.sharedMesh.GetBlendShapeIndex(source) < 0 ||
                accessory.sharedMesh.GetBlendShapeIndex(destination) < 0) continue;
            if (destinations.TryGetValue(destination, out string previous))
            {
                if (previous != source)
                    throw new InvalidOperationException("Conflicting ReFit flex sources for " + accessory.name + "/" + destination);
                continue;
            }
            destinations.Add(destination, source);
            mappings.Add(new KeyValuePair<string, string>(source, destination));
        }
        return mappings;
    }

    // Runs before VRCFury can rename/merge renderers or optimize unused blendshapes away.
    // All references come from this build copy, never a similarly named authoring avatar.
    internal static List<BuildFlexLink> CollectBuildFlexLinks(GameObject avatarRoot)
    {
        var links = new List<BuildFlexLink>();
        if (avatarRoot == null) return links;
        foreach (var mcb in avatarRoot.GetComponentsInChildren<MyCustomBase>(true))
        {
            if (mcb.appliedRefits == null) continue;
            var body = FindPrimaryBodyRenderer(avatarRoot.transform, BuildBaseMeshMap(mcb));
            foreach (var entry in mcb.appliedRefits)
            {
                if (entry == null || string.IsNullOrEmpty(entry.rendererPath)) continue;
                var transform = avatarRoot.transform.Find(entry.rendererPath);
                var accessory = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
                if (accessory == null || entry.refitMesh == null || accessory.sharedMesh != entry.refitMesh) continue;
                if (body == null && entry.transferredBlendShapeSourceNames != null &&
                    entry.transferredBlendShapeSourceNames.Any(IsFlexBlendShape))
                    throw new InvalidOperationException("Cannot resolve the ReFit body for flex links. Check MCB's Base Fbx Files.");
                var mappings = GetTransferredFlexMappings(entry, body, accessory);
                if (mappings.Count == 0) continue;
                links.Add(new BuildFlexLink { body = body, accessory = accessory, mappings = mappings });
            }
        }
        return links;
    }

    internal static void CaptureBuildFlexLinks(GameObject avatarRoot)
    {
        if (avatarRoot == null) return;
        buildFlexLinks.Remove(avatarRoot);
        buildFlexLinks.Add(avatarRoot, CollectBuildFlexLinks(avatarRoot));
    }

    internal static List<BuildFlexLink> GetBuildFlexLinks(GameObject avatarRoot)
    {
        return avatarRoot != null && buildFlexLinks.TryGetValue(avatarRoot, out var links)
            ? links : CollectBuildFlexLinks(avatarRoot);
    }
}

public sealed class MCBReFitLinkCaptureHook : IVRCSDKPreprocessAvatarCallback
{
    public int callbackOrder => -10001;

    public bool OnPreprocessAvatar(GameObject avatarRoot)
    {
        try
        {
            MCBReFitIntegration.CaptureBuildFlexLinks(avatarRoot);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[MCB] ReFit flex links failed: " + ex.Message);
            return false;
        }
    }
}
#endif
