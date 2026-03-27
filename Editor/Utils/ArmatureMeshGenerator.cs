#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.IO;

public static class ArmatureMeshGenerator
{
    public enum MeshType { Pyramid, Tapered, Box }

    public static void Generate(GameObject avatarRoot, MeshType meshType, float baseThickness = 0.05f)
    {
        if (avatarRoot == null)
        {
            MCBLogger.LogError("Avatar root is null.");
            return;
        }

        // Look for the main "Armature" child specifically
        Transform armatureTransform = avatarRoot.transform.Find("Armature");
        if (armatureTransform == null)
        {
            MCBLogger.LogError("Could not find 'Armature' child in avatar root.", avatarRoot);
            return;
        }

        // Find the SkinnedMeshRenderer that uses this main armature
        SkinnedMeshRenderer targetSkinnedMeshRenderer = null;
        var allSkinnedMeshRenderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>();
        
        foreach (var skinnedMeshRenderer in allSkinnedMeshRenderers)
        {
            if (skinnedMeshRenderer.rootBone != null && IsChildOf(skinnedMeshRenderer.rootBone, armatureTransform))
            {
                targetSkinnedMeshRenderer = skinnedMeshRenderer;
                break;
            }
        }

        if (targetSkinnedMeshRenderer == null || targetSkinnedMeshRenderer.bones.Length == 0)
        {
            MCBLogger.LogError("Could not find a SkinnedMeshRenderer that uses the main 'Armature'.", avatarRoot);
            return;
        }

        Transform rootBone = targetSkinnedMeshRenderer.rootBone;
        if (rootBone == null)
        {
            MCBLogger.LogError("SkinnedMeshRenderer is missing its root bone.", targetSkinnedMeshRenderer);
            return;
        }
        
        Transform existingDebugMesh = avatarRoot.transform.Find("ArmatureDebugMesh");
        if (existingDebugMesh != null)
        {
            Object.DestroyImmediate(existingDebugMesh.gameObject);
        }

        GameObject meshGo = new GameObject("ArmatureDebugMesh");
        meshGo.transform.SetParent(avatarRoot.transform, false);

        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var boneWeights = new List<BoneWeight>();
        
        var bones = targetSkinnedMeshRenderer.bones;
        var boneIndexMap = new Dictionary<Transform, int>();
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null)
            {
                boneIndexMap[bones[i]] = i;
            }
        }

        foreach (var bone in bones)
        {
            if (bone == null) continue;
            
            foreach (Transform child in bone.transform)
            {
                if (boneIndexMap.ContainsKey(child))
                {
                    GenerateBoneGeometry(bone, child, meshType, baseThickness, vertices, triangles, boneWeights, boneIndexMap);
                }
            }
        }

        Mesh mesh = new Mesh { name = "ArmatureDebugMesh_Data" };

        var localVerts = new Vector3[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            localVerts[i] = meshGo.transform.InverseTransformPoint(vertices[i]);
        }
        mesh.vertices = localVerts;
        mesh.triangles = triangles.ToArray();
        mesh.boneWeights = boneWeights.ToArray();
        
        var bindPoses = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null)
            {
                bindPoses[i] = bones[i].worldToLocalMatrix * meshGo.transform.localToWorldMatrix;
            }
        }
        mesh.bindposes = bindPoses;

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        smr.sharedMesh = mesh;
        smr.bones = bones;
        smr.rootBone = rootBone;

        // Load the custom material
        string materialPath = Path.Combine(MCBUtils.PACKAGE_BASE_FOLDER, "ArmatureDebugMaterial.mat");
        Material debugMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        
        if (debugMaterial == null)
        {
            MCBLogger.LogWarning($"Could not find ArmatureDebugMaterial at {materialPath}. Creating fallback material.");
            debugMaterial = CreateFallbackMaterial();
        }
        
        smr.sharedMaterial = debugMaterial;
        
        MCBLogger.Log($"Generated armature mesh '{meshType}' successfully using main Armature.", meshGo);
    }

    private static Material CreateFallbackMaterial()
    {
        var material = new Material(Shader.Find("Standard"));
        material.color = new Color(0.5f, 0.8f, 1.0f, 0.8f);
        material.SetFloat("_Mode", 3); // Set to transparent
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
        return material;
    }

    private static bool IsChildOf(Transform child, Transform parent)
    {
        Transform current = child;
        while (current != null)
        {
            if (current == parent)
                return true;
            current = current.parent;
        }
        return false;
    }

    private static void GenerateBoneGeometry(
        Transform boneHead, Transform boneTail, MeshType meshType, float baseThickness, 
        List<Vector3> vertices, List<int> triangles, List<BoneWeight> boneWeights,
        IReadOnlyDictionary<Transform, int> boneIndexMap)
    {
        int b = vertices.Count;
        
        Vector3 headPos = boneHead.position;
        Vector3 tailPos = boneTail.position;
        Vector3 boneVec = tailPos - headPos;
        if (boneVec.magnitude < 0.001f) return;

        // Scale thickness based on bone length
        float boneLength = boneVec.magnitude;
        float scaledThickness = baseThickness * Mathf.Clamp(boneLength * 2.0f, 0.1f, 2.0f);

        Vector3 boneDir = boneVec.normalized;
        Vector3 up = boneHead.up;
        if (Mathf.Abs(Vector3.Dot(up, boneDir)) > 0.99f) up = boneHead.forward;
        
        Vector3 xAxis = Vector3.Cross(up, boneDir).normalized;
        Vector3 zAxis = Vector3.Cross(boneDir, xAxis).normalized;

        Vector3 x1, z1, x2, z2;
        switch (meshType)
        {
            case MeshType.Tapered:
                x1 = xAxis * scaledThickness;
                z1 = zAxis * scaledThickness;
                x2 = xAxis * scaledThickness * 0.5f;
                z2 = zAxis * scaledThickness * 0.5f;
                break;
            case MeshType.Box:
                x1 = xAxis * scaledThickness;
                z1 = zAxis * scaledThickness;
                x2 = x1;
                z2 = z1;
                break;
            default: // Pyramid
                x1 = xAxis * scaledThickness;
                z1 = zAxis * scaledThickness;
                x2 = Vector3.zero;
                z2 = Vector3.zero;
                break;
        }

        vertices.AddRange(new []
        {
            headPos - x1 + z1, headPos + x1 + z1, headPos - x1 - z1, headPos + x1 - z1,
            tailPos - x2 + z2, tailPos + x2 + z2, tailPos - x2 - z2, tailPos + x2 - z2
        });

        triangles.AddRange(new []
        {
            b+3, b+1, b+0, b+3, b+0, b+2, // Head cap
            b+6, b+4, b+5, b+6, b+5, b+7, // Tail cap
            b+4, b+0, b+1, b+4, b+1, b+5, // Side
            b+7, b+3, b+2, b+7, b+2, b+6, // Side
            b+5, b+1, b+3, b+5, b+3, b+7, // Side
            b+6, b+2, b+0, b+6, b+0, b+4  // Side
        });
        
        if (boneIndexMap.TryGetValue(boneHead, out int boneIndex))
        {
            var bw = new BoneWeight { boneIndex0 = boneIndex, weight0 = 1.0f };
            for(int i = 0; i < 8; i++) boneWeights.Add(bw);
        }
        else
        {
            MCBLogger.LogWarning($"Bone '{boneHead.name}' not in map.", boneHead);
            for(int i = 0; i < 8; i++) boneWeights.Add(new BoneWeight());
        }
    }
}
#endif