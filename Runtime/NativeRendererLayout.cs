using System;
using UnityEngine;

// The state a renderer layout changed on an original renderer, restored on reset or version switch.
[Serializable] public sealed class NativeRendererOriginalState
{
    public SkinnedMeshRenderer renderer;
    public Material[] materials;
    // The original material slot names, read before the custom mesh replaced the renderer's mesh.
    public string[] slots;
    public bool enabled;
}
