#if UNITY_EDITOR
using UnityEngine;

/// <summary>Compute culling bounds from posed geometry in the renderer's root-bone space.</summary>
public static class SkinnedMeshBoundsService
{
    public static void Refresh(SkinnedMeshRenderer renderer)
    {
        if (renderer == null || renderer.sharedMesh == null) return;
        var baked = new Mesh();
        try
        {
            renderer.BakeMesh(baked);
            var vertices = baked.vertices;
            if (vertices.Length == 0) return;
            var boundsRoot = renderer.rootBone != null ? renderer.rootBone : renderer.transform;
            var matrix = boundsRoot.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            var bounds = new Bounds(matrix.MultiplyPoint3x4(vertices[0]), Vector3.zero);
            for (int i = 1; i < vertices.Length; i++) bounds.Encapsulate(matrix.MultiplyPoint3x4(vertices[i]));
            // Keep a small margin for floating-point error at frustum edges.
            bounds.Expand(Mathf.Max(0.002f, bounds.size.magnitude * 0.02f));
            renderer.localBounds = bounds;
        }
        finally { Object.DestroyImmediate(baked); }
    }
}
#endif
