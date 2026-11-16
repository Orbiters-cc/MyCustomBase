using UnityEngine;

// Keeps creator-selected fallback materials in the logic package's dependencies.
// Published logic prefabs hold this script by GUID, so its .meta is the one MCB ships (ci/build-package-file-list.py):
// without it each project gives the script its own GUID and the component is a missing script there.
[AddComponentMenu("")]
public sealed class NativeRendererMaterialLibrary : MonoBehaviour
#if UNITY_EDITOR
    , VRC.SDKBase.IEditorOnly
#endif
{
    public Material[] materials;
}
