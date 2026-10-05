using UnityEngine;

// Keeps creator-selected fallback materials in the logic package's dependencies.
[AddComponentMenu("")]
public sealed class NativeRendererMaterialLibrary : MonoBehaviour
#if UNITY_EDITOR
    , VRC.SDKBase.IEditorOnly
#endif
{
    public Material[] materials;
}
