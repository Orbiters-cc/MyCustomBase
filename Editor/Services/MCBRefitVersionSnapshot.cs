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
    public RefitRendererState original;
    public RefitRendererState fitted;
    public List<RefitShape> shapes = new List<RefitShape>();
    public OrbitersRefit.FitKind kind;
    public string tool;
    public string metadataJson;
}
#endif
