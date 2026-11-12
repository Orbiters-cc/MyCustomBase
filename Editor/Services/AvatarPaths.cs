using System;
using System.Linq;
using UnityEngine;

/// <summary>Avatar-relative paths used by version customization: the avatar root and unambiguous child lookup.</summary>
public static class AvatarPaths
{
    public const string LogicPrefix = "mcb logic/";

    public static Transform Root(MyCustomBase owner)
    {
        var descriptor = owner.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
        return descriptor != null ? descriptor.transform : owner.transform.root;
    }

    public static Transform Resolve(Transform root, string path)
    {
        VersionCustomization.ValidatePath(path);
        var current = root;
        foreach (string segment in path.Split('/'))
        {
            var matches = Enumerable.Range(0, current.childCount).Select(current.GetChild).Where(t => t.name == segment).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"'{path}' must resolve to exactly one object; '{segment}' has {matches.Length} matches.");
            current = matches[0];
        }
        return current;
    }

    /// <summary>
    /// The avatar's renderer for a version's mesh path ("Body"): the one at that path, else the avatar's own renderer
    /// named like it wherever the person moved it (never an outfit's "Body").
    /// </summary>
    public static SkinnedMeshRenderer Renderer(Transform root, string path)
    {
        var found = Find(root, path);
        var renderer = found != null ? found.GetComponent<SkinnedMeshRenderer>() : null;
        if (renderer != null) return renderer;
        var own = OwnSkeleton.ForAvatar(root).ResolveRenderer(path, out string ambiguous);
        if (ambiguous != null) throw new InvalidOperationException(ambiguous);
        return own;
    }

    /// <summary>Like <see cref="Resolve"/>, but a missing object is null instead of an error.</summary>
    public static Transform Find(Transform root, string path)
    {
        try { return Resolve(root, path); }
        catch (InvalidOperationException) { return null; }
    }
}
