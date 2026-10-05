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

    /// <summary>Like <see cref="Resolve"/>, but a missing object is null instead of an error.</summary>
    public static Transform Find(Transform root, string path)
    {
        try { return Resolve(root, path); }
        catch (InvalidOperationException) { return null; }
    }
}
