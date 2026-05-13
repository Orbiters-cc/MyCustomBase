#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class NativeMeshPayloadHealthCheck
{
    private const int HealthCheckAssetId = 999999;
    private const string HealthCheckVersion = "nativeMeshHealthCheck";
    private static readonly string GeneratedPayloadFolder = "Assets/MCB/generated/advancedMeshPayloads/" + HealthCheckAssetId;

    [MenuItem("Tools/My Custom Base (MCB)/Health Checks/Native Mesh Payload")]
    public static void RunFromMenu()
    {
        try
        {
            RunOrThrow();
            EditorUtility.DisplayDialog("Native Mesh Payload", "Health check passed.", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogError("[NativeMeshPayloadHealthCheck] Failed: " + ex);
            EditorUtility.DisplayDialog("Native Mesh Payload", "Health check failed. See Console for details.", "OK");
        }
    }

    public static void RunFromCommandLine()
    {
        try
        {
            RunOrThrow();
            Debug.Log("[NativeMeshPayloadHealthCheck] Passed.");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[NativeMeshPayloadHealthCheck] Failed: " + ex);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(1);
                return;
            }

            throw;
        }
    }

    public static void RunOrThrow()
    {
        string sourceKeyPath = Path.Combine(Path.GetTempPath(), "mcb_native_mesh_health_source.fbx");
        string binPath = Path.Combine(Path.GetTempPath(), "mcb_native_mesh_health_payload.bin");
        GameObject sourceRoot = null;
        GameObject avatarRoot = null;
        GameObject customRoot = null;

        try
        {
            CleanupGeneratedAssets();
            File.WriteAllBytes(sourceKeyPath, BuildDeterministicSourceKey());

            sourceRoot = BuildRig("MCB_NativeMeshHealth_Source", CreateTriangleMesh("BodyMesh", 1f), 0f);
            avatarRoot = BuildRig("MCB_NativeMeshHealth_Avatar", CreateTriangleMesh("BodyMesh", 1f), 0f);
            customRoot = BuildRig("MCB_NativeMeshHealth_Custom", CreateQuadMesh("BodyMesh", 1.35f), 0.25f);
            var authoringBone = customRoot.transform.Find("Armature/Hips");
            if (authoringBone != null)
            {
                authoringBone.localRotation = Quaternion.Euler(0f, 0f, 25f);
            }

            var smrPaths = new[]
            {
                new ModelFileSmrPathData
                {
                    avatarPath = "Body",
                    fbxMeshPath = "Body",
                    meshName = "BodyMesh",
                    rendererName = "Body"
                }
            };

            var fileManager = new FileManagerService();
            var buildResult = NativeMeshPayloadService.WriteEncryptedPayload(
                sourceKeyPath,
                customRoot,
                smrPaths,
                fileManager,
                binPath,
                sourcePoseRoot: sourceRoot.transform);

            ThrowIf(buildResult.rendererCount != 1, "Expected one renderer in the generated payload.");
            ThrowIf(buildResult.payloadBytes <= 0, "Payload was empty.");
            ThrowIf(buildResult.payloadBytes > 1024 * 1024, "Synthetic native mesh payload is unexpectedly large.");
            ThrowIf(!string.Equals(buildResult.payloadCompression, NativeMeshPayloadService.PayloadCompressionNone, StringComparison.Ordinal), "Synthetic payload should default to uncompressed native mesh format.");
            ThrowIf(!File.Exists(binPath) || new FileInfo(binPath).Length == 0, "Encrypted payload file was not written.");
            ThrowIf(!string.Equals(buildResult.binHash, fileManager.CalculateFileHash(binPath), StringComparison.OrdinalIgnoreCase), "Streaming bin hash does not match file hash.");

            var patchFile = new ModelFileData
            {
                id = 1,
                path = "nativeMeshHealthPayload.bin",
                type = "BIN",
                role = "PATCH",
                transform = NativeMeshPayloadService.TransformName,
                compression = buildResult.payloadCompression,
                outputHash = buildResult.payloadHash,
                smrPaths = new List<ModelFileSmrPathData>(smrPaths),
                metadata = new Dictionary<string, object>
                {
                    { "sourcePath", sourceKeyPath },
                    { NativeMeshPayloadService.PayloadCompressionMetadataKey, buildResult.payloadCompression }
                }
            };

            var version = new CustomBaseVersion
            {
                assetId = HealthCheckAssetId,
                version = HealthCheckVersion,
                defaultAviVersion = "1.0.0",
                extraCustomization = new object[] { "advancedMeshReplacement" },
                versionFiles = new[] { patchFile }
            };

            CleanupGeneratedAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            NativeMeshPayloadAsset payload = NativeMeshPayloadService.ApplyEncryptedPayload(
                avatarRoot.transform,
                version,
                patchFile,
                binPath,
                sourceKeyPath,
                fileManager);

            ThrowIf(payload == null, "Payload asset was not created.");
            ThrowIf(payload.renderers == null || payload.renderers.Count != 1, "Payload asset renderer count is invalid.");

            var targetRenderer = avatarRoot.transform.Find("Body")?.GetComponent<SkinnedMeshRenderer>();
            ThrowIf(targetRenderer == null, "Target renderer was not found after apply.");
            ThrowIf(targetRenderer.sharedMesh == null, "Target renderer has no mesh after apply.");
            ThrowIf(targetRenderer.sharedMesh.vertexCount != 4, "Topology-changing mesh vertex count was not preserved.");
            ThrowIf(targetRenderer.sharedMesh.blendShapeCount != 1, "Blendshape data was not preserved.");
            ThrowIf(Mathf.Abs(targetRenderer.sharedMesh.vertices[2].y - 1.35f) > 0.0001f, "Applied mesh vertices do not match the custom mesh.");

            var targetBone = avatarRoot.transform.Find("Armature/Hips");
            ThrowIf(targetBone == null, "Target bone was not found.");
            ThrowIf(Mathf.Abs(targetBone.localPosition.y - 0.25f) > 0.0001f, "Bone transform was not applied.");
            ThrowIf(Quaternion.Angle(targetBone.localRotation, Quaternion.Euler(0f, 0f, 25f)) > 0.01f, "Authoring pose rotation was not applied.");
        }
        finally
        {
            if (sourceRoot != null) Object.DestroyImmediate(sourceRoot);
            if (avatarRoot != null) Object.DestroyImmediate(avatarRoot);
            if (customRoot != null) Object.DestroyImmediate(customRoot);
            if (File.Exists(sourceKeyPath)) File.Delete(sourceKeyPath);
            if (File.Exists(binPath)) File.Delete(binPath);
            CleanupGeneratedAssets();
        }
    }

    private static GameObject BuildRig(string rootName, Mesh mesh, float hipsY)
    {
        var root = new GameObject(rootName);
        var armature = new GameObject("Armature");
        armature.transform.SetParent(root.transform, false);
        var hips = new GameObject("Hips");
        hips.transform.SetParent(armature.transform, false);
        hips.transform.localPosition = new Vector3(0f, hipsY, 0f);

        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.rootBone = hips.transform;
        renderer.bones = new[] { hips.transform };
        return root;
    }

    private static Mesh CreateTriangleMesh(string name, float topY)
    {
        var mesh = new Mesh { name = name };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(0.5f, 0f, 0f),
            new Vector3(0f, topY, 0f)
        };
        mesh.normals = new[]
        {
            Vector3.forward,
            Vector3.forward,
            Vector3.forward
        };
        mesh.tangents = new[]
        {
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(1f, 0f, 0f, 1f)
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0.5f, 1f)
        };
        mesh.boneWeights = new[]
        {
            new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            new BoneWeight { boneIndex0 = 0, weight0 = 1f }
        };
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
        mesh.bounds = new Bounds(new Vector3(0f, topY * 0.5f, 0f), new Vector3(1f, topY, 0.1f));
        mesh.AddBlendShapeFrame(
            "HealthSparseBlendshape",
            100f,
            new[] { Vector3.zero, Vector3.zero, new Vector3(0f, 0.1f, 0f) },
            new[] { Vector3.zero, Vector3.zero, Vector3.zero },
            new[] { Vector3.zero, Vector3.zero, Vector3.zero });
        return mesh;
    }

    private static Mesh CreateQuadMesh(string name, float topY)
    {
        var mesh = new Mesh { name = name };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(0.5f, 0f, 0f),
            new Vector3(0.5f, topY, 0f),
            new Vector3(-0.5f, topY, 0f)
        };
        mesh.normals = new[]
        {
            Vector3.forward,
            Vector3.forward,
            Vector3.forward,
            Vector3.forward
        };
        mesh.tangents = new[]
        {
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(1f, 0f, 0f, 1f)
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f)
        };
        mesh.boneWeights = new[]
        {
            new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            new BoneWeight { boneIndex0 = 0, weight0 = 1f }
        };
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.bounds = new Bounds(new Vector3(0f, topY * 0.5f, 0f), new Vector3(1f, topY, 0.1f));
        mesh.AddBlendShapeFrame(
            "HealthSparseBlendshape",
            100f,
            new[] { Vector3.zero, Vector3.zero, new Vector3(0f, 0.1f, 0f), Vector3.zero },
            null,
            null);
        return mesh;
    }

    private static byte[] BuildDeterministicSourceKey()
    {
        var bytes = new byte[4096];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31 + 17) % 251);
        }

        return bytes;
    }

    private static void CleanupGeneratedAssets()
    {
        if (AssetDatabase.IsValidFolder(GeneratedPayloadFolder))
        {
            AssetDatabase.DeleteAsset(GeneratedPayloadFolder);
        }
    }

    private static void ThrowIf(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
#endif
