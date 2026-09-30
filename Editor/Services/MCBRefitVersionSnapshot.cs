#if UNITY_EDITOR
using System.Collections.Generic;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

// Local avatar-specific authoring data, deliberately outside downloadable version packages: one refitted mesh as it was
// fitted for one custom base version.
public sealed class MCBRefitVersionSnapshot : ScriptableObject
{
    public bool enabledForVersion = true;
    /// <summary>The renderer's path under the avatar root.</summary>
    public string rendererPath;
    /// <summary>
    /// For each segment of <see cref="rendererPath"/>, its position among same-named siblings: tells apart accessories that
    /// share a name. Empty or all zero: the first of each name.
    /// </summary>
    public List<int> rendererSiblingOrdinals = new List<int>();
    public RefitRendererState original;
    public RefitRendererState fitted;
    public List<RefitShape> shapes = new List<RefitShape>();
    public OrbitersRefit.FitKind kind;
    public string tool;
    public string metadataJson;
}
#endif
