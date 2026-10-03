#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            Debug.Log("[NativeMeshPayloadHealthCheck] Passed.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[NativeMeshPayloadHealthCheck] Failed: " + ex);
        }
    }

    public static void RunOrThrow()
    {
        CheckHumanoidProportions();
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
            customRoot = BuildRig("MCB_NativeMeshHealth_Custom", CreateQuadMesh("BodyMesh (DynamicNormals)", 1.35f), 0.25f);
            customRoot.transform.Find("Body").localPosition = new Vector3(0.15f, 0.05f, -0.02f);
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
                    meshName = "BodyMesh (DynamicNormals)",
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
            ThrowIf(targetRenderer.sharedMesh.name.IndexOf("DynamicNormals", StringComparison.OrdinalIgnoreCase) < 0, "DynamicNormals payload mesh name was not preserved.");
            ThrowIf(Vector3.Distance(targetRenderer.transform.localPosition, new Vector3(0.15f, 0.05f, -0.02f)) > 0.0001f, "Renderer transform was not applied.");
            var generatedRenderers = NativeMeshPayloadService.ResolveAppliedGeneratedMeshRenderers(avatarRoot.transform, version);
            ThrowIf(generatedRenderers.Count != 1 || generatedRenderers[0] != targetRenderer, "Applied generated-mesh provenance did not resolve the target renderer.");
            ThrowIf(!NativeMeshPayloadService.TryGetAppliedGeneratedMeshVersion(avatarRoot.transform, out int generatedAssetId, out string generatedVersion), "Applied generated-mesh version identity was not detected.");
            ThrowIf(generatedAssetId != HealthCheckAssetId || !string.Equals(generatedVersion, HealthCheckVersion, StringComparison.Ordinal), "Applied generated-mesh version identity is incorrect.");

            var targetBone = avatarRoot.transform.Find("Armature/Hips");
            ThrowIf(targetBone == null, "Target bone was not found.");
            ThrowIf(Mathf.Abs(targetBone.localPosition.y - 0.25f) > 0.0001f, "Bone transform was not applied.");
            ThrowIf(Quaternion.Angle(targetBone.localRotation, Quaternion.Euler(0f, 0f, 25f)) > 0.01f, "Authoring pose rotation was not applied.");

            // Reproduce a reset from an advanced DynamicNormals payload whose version has no
            // source smrPaths. Full hierarchy matching must restore the FBX mesh, renderer
            // binding, renderer transform, and source armature pose.
            targetRenderer.bones = Array.Empty<Transform>();
            targetRenderer.rootBone = null;
            SmrPathService.RestoreTargetTransformHierarchyFromFbxRoot(
                avatarRoot.transform,
                sourceRoot.transform);
            int restoredRenderers = SmrPathService.RestoreTargetStateFromFbxRoot(
                avatarRoot.transform,
                sourceRoot.transform,
                Array.Empty<ModelFileSmrPathData>());

            var sourceRenderer = sourceRoot.transform.Find("Body")?.GetComponent<SkinnedMeshRenderer>();
            ThrowIf(restoredRenderers != 1, $"Expected one renderer to be restored by hierarchy, got {restoredRenderers}.");
            ThrowIf(sourceRenderer == null, "Source renderer was not found for restore verification.");
            ThrowIf(targetRenderer.sharedMesh != sourceRenderer.sharedMesh, "Advanced payload mesh was not replaced by the source FBX mesh.");
            ThrowIf(targetRenderer.bones == null || targetRenderer.bones.Length != 1 || targetRenderer.bones[0] != targetBone, "Source FBX renderer bones were not restored.");
            ThrowIf(targetRenderer.rootBone != targetBone, "Source FBX renderer root bone was not restored.");
            ThrowIf(Vector3.Distance(targetRenderer.transform.localPosition, Vector3.zero) > 0.0001f, "Source FBX renderer transform was not restored.");
            ThrowIf(Mathf.Abs(targetBone.localPosition.y) > 0.0001f, "Source FBX armature pose was not restored.");
            ThrowIf(Quaternion.Angle(targetBone.localRotation, Quaternion.identity) > 0.01f, "Source FBX armature rotation was not restored.");

            // Explicit smrPaths are an allowlist. They must not broaden into the empty-map
            // hierarchy fallback and replace unrelated renderers from the same source FBX.
            var sourceAccessory = AddSkinnedRenderer(
                sourceRoot.transform,
                "Accessory",
                CreateTriangleMesh("AccessorySourceMesh", 0.75f));
            var targetAccessory = AddSkinnedRenderer(
                avatarRoot.transform,
                "Accessory",
                CreateTriangleMesh("AccessoryTargetMesh", 0.55f));
            Mesh targetAccessoryMesh = targetAccessory.sharedMesh;
            int explicitlyRestored = SmrPathService.RestoreTargetStateFromFbxRoot(
                avatarRoot.transform,
                sourceRoot.transform,
                smrPaths);
            ThrowIf(explicitlyRestored != 1, $"Expected only the explicitly mapped renderer to restore, got {explicitlyRestored}.");
            ThrowIf(targetAccessory.sharedMesh != targetAccessoryMesh, "Explicit renderer restore overwrote an unrelated renderer.");

            // A missing source binding must leave the prior mesh and armature binding together.
            // Assigning only the mesh recreates the deformation this health check guards against.
            Mesh protectedMesh = targetRenderer.sharedMesh;
            Transform[] protectedBones = targetRenderer.bones;
            Transform protectedRootBone = targetRenderer.rootBone;
            Transform sourceBone = sourceRoot.transform.Find("Armature/Hips");
            ThrowIf(sourceBone == null, "Source bone was not found for atomic restore verification.");
            sourceBone.name = "MissingHips";
            sourceRenderer.sharedMesh = CreateQuadMesh("UnresolvedSourceMesh", 1.1f);
            int unsafeRestoreCount = SmrPathService.RestoreTargetStateFromFbxRoot(
                avatarRoot.transform,
                sourceRoot.transform,
                smrPaths);
            ThrowIf(unsafeRestoreCount != 0, "Renderer restore reported success despite an unresolved source bone.");
            ThrowIf(targetRenderer.sharedMesh != protectedMesh, "Renderer mesh changed despite an unresolved source bone.");
            ThrowIf(targetRenderer.bones == null || targetRenderer.bones.Length != protectedBones.Length || targetRenderer.bones[0] != protectedBones[0], "Renderer bones changed despite an unresolved source bone.");
            ThrowIf(targetRenderer.rootBone != protectedRootBone, "Renderer root bone changed despite an unresolved source bone.");
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

    private static void CheckHumanoidProportions()
    {
        var root = new GameObject("MCB humanoid health");
        var payload = ScriptableObject.CreateInstance<NativeMeshPayloadAsset>();
        Avatar source = null, result = null, repeated = null;
        try
        {
            var human = new System.Collections.Generic.List<HumanBone>();
            Transform Add(string name, Transform parent, Vector3 position, HumanBodyBones role)
            {
                var bone = new GameObject(name).transform;
                bone.SetParent(parent, false);
                bone.localPosition = position;
                human.Add(new HumanBone { boneName = name, humanName = HumanTrait.BoneName[(int)role], limit = new HumanLimit { useDefaultValues = true } });
                return bone;
            }
            var hips = Add("Hips", root.transform, Vector3.up, HumanBodyBones.Hips);
            var spine = Add("Spine", hips, Vector3.up * .15f, HumanBodyBones.Spine);
            var chest = Add("Chest", spine, Vector3.up * .15f, HumanBodyBones.Chest);
            var neck = Add("Neck", chest, Vector3.up * .25f, HumanBodyBones.Neck);
            Add("Head", neck, Vector3.up * .1f, HumanBodyBones.Head);
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0; string suffix = left ? "L" : "R"; float sign = left ? -1 : 1;
                var arm = Add("Arm" + suffix, chest, new Vector3(sign * .2f, .15f, 0), left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                var forearm = Add("Forearm" + suffix, arm, Vector3.right * (sign * .25f), left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                Add("Hand" + suffix, forearm, Vector3.right * (sign * .25f), left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var leg = Add("Leg" + suffix, hips, Vector3.right * (sign * .1f), left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
                var shin = Add("Shin" + suffix, leg, Vector3.down * .4f, left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                Add("Foot" + suffix, shin, Vector3.down * .4f, left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
            }
            var transforms = root.GetComponentsInChildren<Transform>();
            var description = new HumanDescription { human = human.ToArray(), skeleton = transforms.Select(t => new SkeletonBone
                { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f };
            source = AvatarBuilder.BuildHumanAvatar(root, description);
            ThrowIf(source == null || !source.isValid || !source.isHuman, "Humanoid fixture is invalid.");
            payload.bones = transforms.Where(t => t != root.transform).Select(t => new NativeMeshPayloadBone
                { path = AnimationUtility.CalculateTransformPath(t, root.transform), localPosition = t.localPosition * 1.2f,
                    localRotation = t.localRotation, localScale = t.localScale }).ToList();
            // These deltas must never be added to the already-complete absolute skeleton a second time.
            payload.authoringPoseBones = payload.bones.Select(b => new NativeMeshPayloadAuthoringPoseBone
                { path = b.path, localPositionOffset = Vector3.one * .1f }).ToList();
            result = AvatarDefinitionGenerationService.BuildNativeMeshAvatar(payload, source);
            repeated = AvatarDefinitionGenerationService.BuildNativeMeshAvatar(payload, result);
            foreach (var bone in payload.bones)
            {
                string name = Path.GetFileName(bone.path);
                var actual = result.humanDescription.skeleton.Single(b => b.name == name);
                var again = repeated.humanDescription.skeleton.Single(b => b.name == name);
                ThrowIf(Vector3.Distance(actual.position, bone.localPosition) > .0001f, "Humanoid changed payload proportions: " + name);
                ThrowIf(Vector3.Distance(actual.position, again.position) > .0001f, "Repeated humanoid build applied a delta twice: " + name);
            }
            ThrowIf(Mathf.Abs(source.humanDescription.skeleton.Single(b => b.name == "Hips").position.y - 1f) > .0001f, "Source humanoid was modified.");
            ThrowIf(Mathf.Abs(hips.localPosition.y - 1f) > .0001f, "Source hierarchy was modified.");
        }
        finally
        {
            if (source != null) Object.DestroyImmediate(source);
            if (result != null) Object.DestroyImmediate(result);
            if (repeated != null) Object.DestroyImmediate(repeated);
            Object.DestroyImmediate(payload);
            Object.DestroyImmediate(root);
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

    private static SkinnedMeshRenderer AddSkinnedRenderer(Transform root, string name, Mesh mesh)
    {
        var rendererObject = new GameObject(name);
        rendererObject.transform.SetParent(root, false);
        var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
        var bone = root.Find("Armature/Hips");
        renderer.sharedMesh = mesh;
        renderer.rootBone = bone;
        renderer.bones = bone != null ? new[] { bone } : Array.Empty<Transform>();
        return renderer;
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
