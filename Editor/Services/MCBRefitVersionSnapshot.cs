#if UNITY_EDITOR
using UnityEngine;

// Local avatar-specific authoring data, deliberately outside downloadable version packages.
public sealed class MCBRefitVersionSnapshot : ScriptableObject
{
    public bool enabledForVersion = true;
    public RefitAppliedMeshEntry original;
    public RefitAppliedMeshEntry fitted;
    public string metadataJson;
}
#endif
