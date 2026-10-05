#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Orbiters.Toolkit.Armature;
using UnityEditor;
using UnityEngine;

public static partial class AvatarDefinitionGenerationService
{
    public class GenerationResult
    {
        public Avatar avatar;
        public string avatarPath;
        public string message;
    }

    private class ImporterState
    {
        public ModelImporterAnimationType animationType;
        public ModelImporterAvatarSetup avatarSetup;
        public Avatar sourceAvatar;
        public HumanDescription humanDescription;
        public bool autoGenerateAvatarMappingIfUnspecified;
    }

    // Name-based mapping follows the Toolkit's humanoid naming, except where an MCB base names bones its own way:
    // the Alphazear rig continues its ring finger chain into the little finger.
    private static readonly Dictionary<string, HumanBodyBones> NameOverrides = new Dictionary<string, HumanBodyBones>(StringComparer.OrdinalIgnoreCase)
    {
        ["RingFinger.L.003"] = HumanBodyBones.LeftLittleProximal,
        ["RingFinger.L.004"] = HumanBodyBones.LeftLittleIntermediate,
        ["RingFinger.L.005"] = HumanBodyBones.LeftLittleDistal,
        ["RingFinger.R.003"] = HumanBodyBones.RightLittleProximal,
        ["RingFinger.R.004"] = HumanBodyBones.RightLittleIntermediate,
        ["RingFinger.R.005"] = HumanBodyBones.RightLittleDistal,
    };

    public static bool HasGenerationSource(GameObject customFbx, string externalFbxPath)
    {
        return customFbx != null || (!string.IsNullOrWhiteSpace(externalFbxPath) && File.Exists(externalFbxPath));
    }

    public static GenerationResult GenerateAvatarFromExternalFbx(string externalFbxPath, GameObject sourceMappingFbx, Avatar previousAvatar = null)
    {
        if (sourceMappingFbx == null) throw new ArgumentNullException(nameof(sourceMappingFbx));
        string folder = MCBUtils.CombineUnityPath(MCBUtils.ASSETS_BASE_FOLDER, "generated", "blenderAvatars");
        string outputPath = previousAvatar != null ? AssetDatabase.GetAssetPath(previousAvatar) : null;
        // A manually assigned original Avatar must never be overwritten by Update.
        if (string.IsNullOrWhiteSpace(outputPath) || !outputPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ||
            !AssetDatabase.IsMainAsset(previousAvatar))
        {
            EnsureAssetFolderExists(folder);
            outputPath = AssetDatabase.GenerateUniqueAssetPath(MCBUtils.CombineUnityPath(folder,
                BlenderProjectService.SanitizeFileName(sourceMappingFbx.name) + "Avatar.asset"));
        }

        string temporaryPath = null;
        try
        {
            var imported = NativeMeshPayloadService.ImportExternalFbxForPayload(externalFbxPath, out temporaryPath);
            return GenerateAvatarAsset(imported, sourceMappingFbx, outputPath,
                applyGeneratedAvatarToFbx: false, keepImporterConfiguredForEditing: true);
        }
        finally
        {
            NativeMeshPayloadService.DeleteTemporaryImportedFbx(temporaryPath);
        }
    }

    public static string GetDefaultAvatarPath(string fbxUnityPath)
    {
        string normalized = MCBUtils.ToUnityPath(fbxUnityPath);
        string folder = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "Assets";
        string name = Path.GetFileNameWithoutExtension(normalized) + "Avatar.asset";
        return string.IsNullOrWhiteSpace(folder) ? name : MCBUtils.CombineUnityPath(folder, name);
    }

    public static GenerationResult GenerateAvatarAsset(GameObject customFbx, GameObject sourceMappingFbx, bool applyGeneratedAvatarToFbx = true, bool keepImporterConfiguredForEditing = false)
    {
        string customPath = customFbx != null ? AssetDatabase.GetAssetPath(customFbx) : null;
        string outputPath = string.IsNullOrWhiteSpace(customPath) ? null : GetDefaultAvatarPath(customPath);
        return GenerateAvatarAsset(customFbx, sourceMappingFbx, outputPath, applyGeneratedAvatarToFbx, keepImporterConfiguredForEditing);
    }

    public static GenerationResult GenerateAvatarAsset(GameObject customFbx, GameObject sourceMappingFbx, string outputPath, bool applyGeneratedAvatarToFbx = true, bool keepImporterConfiguredForEditing = false)
    {
        if (customFbx == null)
        {
            throw new ArgumentNullException(nameof(customFbx));
        }

        string customPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(customFbx));
        if (string.IsNullOrWhiteSpace(customPath))
        {
            throw new InvalidOperationException("Custom FBX has no AssetDatabase path.");
        }

        return GenerateAvatarAsset(customPath, sourceMappingFbx != null ? AssetDatabase.GetAssetPath(sourceMappingFbx) : null, outputPath, applyGeneratedAvatarToFbx, keepImporterConfiguredForEditing);
    }

    public static GenerationResult GenerateAvatarAsset(string customFbxPath, string sourceMappingFbxPath, string outputPath = null, bool applyGeneratedAvatarToFbx = true, bool keepImporterConfiguredForEditing = false)
    {
        string normalizedCustomPath = MCBUtils.ToUnityPath(customFbxPath);
        string normalizedSourcePath = string.IsNullOrWhiteSpace(sourceMappingFbxPath) ? null : MCBUtils.ToUnityPath(sourceMappingFbxPath);
        outputPath = string.IsNullOrWhiteSpace(outputPath) ? GetDefaultAvatarPath(normalizedCustomPath) : MCBUtils.ToUnityPath(outputPath);

        var importer = AssetImporter.GetAtPath(normalizedCustomPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"No ModelImporter found for custom FBX: {normalizedCustomPath}");
        }

        var state = CaptureState(importer);
        Avatar savedAvatar = null;
        string mappingMessage = "";
        HumanDescription correctedDescription = default;
        bool keepGeneratedImporter = false;

        try
        {
            mappingMessage = ConfigureImporterForHumanoidCreation(
                importer,
                normalizedCustomPath,
                normalizedSourcePath,
                state.humanDescription,
                out correctedDescription);

            Avatar avatarToSave = null;
            Avatar builtAvatar = null;
            try
            {
                builtAvatar = BuildHumanAvatarFromDescription(normalizedCustomPath, correctedDescription, outputPath);
                avatarToSave = builtAvatar;
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[AvatarGeneration] Could not build Avatar directly from corrected humanoid mapping, falling back to Unity embedded Avatar: {ex.Message}");
            }

            if (avatarToSave == null)
            {
                Avatar embeddedAvatar = LoadEmbeddedHumanAvatar(normalizedCustomPath);
                if (embeddedAvatar == null)
                {
                    throw new InvalidOperationException($"Unity did not generate a humanoid Avatar for {normalizedCustomPath}.");
                }

                avatarToSave = embeddedAvatar;
            }

            try
            {
                savedAvatar = SaveAvatarCopy(avatarToSave, outputPath);
            }
            finally
            {
                if (builtAvatar != null)
                {
                    UnityEngine.Object.DestroyImmediate(builtAvatar);
                }
            }

            if (savedAvatar == null || !savedAvatar.isHuman || !savedAvatar.isValid)
            {
                throw new InvalidOperationException($"Generated Avatar is not a valid humanoid Avatar: {outputPath}");
            }

            if (applyGeneratedAvatarToFbx)
            {
                ApplyAvatarToFbxImporter(normalizedCustomPath, savedAvatar);
            }
            else if (keepImporterConfiguredForEditing)
            {
                keepGeneratedImporter = true;
            }
            else
            {
                RestoreState(importer, state);
            }

            return new GenerationResult
            {
                avatar = savedAvatar,
                avatarPath = AssetDatabase.GetAssetPath(savedAvatar),
                message = mappingMessage
            };
        }
        catch
        {
            if (!keepGeneratedImporter)
            {
                var restoreImporter = AssetImporter.GetAtPath(normalizedCustomPath) as ModelImporter;
                if (restoreImporter != null)
                {
                    RestoreState(restoreImporter, state);
                }
            }

            throw;
        }
    }

    public static string PrepareFbxForAvatarConfiguration(GameObject customFbx, GameObject sourceMappingFbx)
    {
        if (customFbx == null)
        {
            throw new ArgumentNullException(nameof(customFbx));
        }

        string customPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(customFbx));
        string sourcePath = sourceMappingFbx != null ? MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(sourceMappingFbx)) : null;
        return PrepareFbxForAvatarConfiguration(customPath, sourcePath);
    }

    public static string PrepareFbxForAvatarConfiguration(string customFbxPath, string sourceMappingFbxPath)
    {
        string normalizedCustomPath = MCBUtils.ToUnityPath(customFbxPath);
        if (string.IsNullOrWhiteSpace(normalizedCustomPath))
        {
            throw new InvalidOperationException("Custom FBX has no AssetDatabase path.");
        }

        var importer = AssetImporter.GetAtPath(normalizedCustomPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"No ModelImporter found for custom FBX: {normalizedCustomPath}");
        }

        HumanDescription correctedDescription;
        return ConfigureImporterForHumanoidCreation(
            importer,
            normalizedCustomPath,
            string.IsNullOrWhiteSpace(sourceMappingFbxPath) ? null : MCBUtils.ToUnityPath(sourceMappingFbxPath),
            importer.humanDescription,
            out correctedDescription);
    }

    public static bool OpenAvatarConfigurationWindow(GameObject customFbx, GameObject sourceMappingFbx, out string message)
    {
        if (customFbx == null)
        {
            throw new ArgumentNullException(nameof(customFbx));
        }

        string customPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(customFbx));
        string sourcePath = sourceMappingFbx != null ? MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(sourceMappingFbx)) : null;
        message = PrepareFbxForAvatarConfiguration(customPath, sourcePath);

        if (TryOpenAvatarConfigurationStage(customPath, out string failureReason))
        {
            return true;
        }

        SelectFbxAsset(customPath);
        message = $"{message} Unity's internal avatar stage could not be opened automatically ({failureReason}). The FBX has been selected so the Inspector Configure button is available.";
        return false;
    }

    public static bool ApplyAvatarToFbxAndAnimator(GameObject fbx, Avatar avatar, Transform avatarRoot)
    {
        bool changed = false;
        if (fbx != null && avatar != null)
        {
            string fbxPath = AssetDatabase.GetAssetPath(fbx);
            if (!string.IsNullOrWhiteSpace(fbxPath))
            {
                changed |= ApplyAvatarToFbxImporter(fbxPath, avatar);
            }
        }

        changed |= SetRootAnimatorAvatar(avatarRoot, avatar);
        return changed;
    }

    public static bool SetRootAnimatorAvatar(Transform avatarRoot, Avatar avatar)
    {
        if (avatarRoot == null || avatar == null)
        {
            return false;
        }

        Animator animator = avatarRoot.GetComponent<Animator>();
        if (animator == null)
        {
            animator = Undo.AddComponent<Animator>(avatarRoot.gameObject);
        }

        if (animator.avatar == avatar)
        {
            // Same definition, but the skeleton may have changed since the Animator bound it.
            AssignAvatarKeepingPose(animator, avatar);
            return false;
        }

        Undo.RecordObject(animator, "Set Root Animator Avatar");
        AssignAvatarKeepingPose(animator, avatar);
        EditorUtility.SetDirty(animator);
        return true;
    }

    /// <summary>
    /// An Animator caches each bone's default pose and parent when it first binds. Assigning an Avatar (or any later
    /// rebind) writes that cache back: a version's reparented or moved bones (e.g. a Neck moved under ChestUp) return to
    /// the original FBX pose and stretch the mesh. Clearing the Avatar drops the cache; the caller's pose is kept exactly.
    /// </summary>
    public static void AssignAvatarKeepingPose(Animator animator, Avatar avatar)
    {
        var transforms = animator.GetComponentsInChildren<Transform>(true);
        var pose = transforms.Select(t => (t.localPosition, t.localRotation, t.localScale)).ToArray();
        void Restore()
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null || (t.localPosition == pose[i].localPosition && t.localRotation == pose[i].localRotation && t.localScale == pose[i].localScale)) continue;
                t.localPosition = pose[i].localPosition;
                t.localRotation = pose[i].localRotation;
                t.localScale = pose[i].localScale;
            }
        }
        animator.avatar = null;
        Restore();
        animator.avatar = avatar;
        Restore();
    }

    public static void RefreshSkinnedMeshRenderers(Transform avatarRoot)
    {
        if (avatarRoot == null)
        {
            return;
        }

        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null)
            {
                continue;
            }

            Undo.RecordObject(renderer, "Refresh Skinned Mesh Renderer");
            Mesh mesh = renderer.sharedMesh;
            var blendshapeWeights = CaptureBlendshapeWeights(renderer, mesh);
            bool previousEnabled = renderer.enabled;
            bool previousUpdateWhenOffscreen = renderer.updateWhenOffscreen;

            renderer.updateWhenOffscreen = true;
            renderer.sharedMesh = null;
            renderer.sharedMesh = mesh;
            RestoreBlendshapeWeights(renderer, mesh, blendshapeWeights);

            if (previousEnabled)
            {
                renderer.enabled = false;
                renderer.enabled = true;
            }

            renderer.updateWhenOffscreen = previousUpdateWhenOffscreen;
            EditorUtility.SetDirty(renderer);
        }

        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }

    private static Dictionary<string, float> CaptureBlendshapeWeights(SkinnedMeshRenderer renderer, Mesh mesh)
    {
        var weights = new Dictionary<string, float>(StringComparer.Ordinal);
        if (renderer == null || mesh == null)
        {
            return weights;
        }

        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            weights[mesh.GetBlendShapeName(i)] = renderer.GetBlendShapeWeight(i);
        }

        return weights;
    }

    private static void RestoreBlendshapeWeights(SkinnedMeshRenderer renderer, Mesh mesh, Dictionary<string, float> weights)
    {
        if (renderer == null || mesh == null || weights == null || weights.Count == 0)
        {
            return;
        }

        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            if (weights.TryGetValue(mesh.GetBlendShapeName(i), out float weight))
            {
                renderer.SetBlendShapeWeight(i, weight);
            }
        }
    }

    private static ImporterState CaptureState(ModelImporter importer)
    {
        return new ImporterState
        {
            animationType = importer.animationType,
            avatarSetup = importer.avatarSetup,
            sourceAvatar = importer.sourceAvatar,
            humanDescription = importer.humanDescription,
            autoGenerateAvatarMappingIfUnspecified = importer.autoGenerateAvatarMappingIfUnspecified
        };
    }

    private static void RestoreState(ModelImporter importer, ImporterState state)
    {
        if (importer == null || state == null)
        {
            return;
        }

        string assetPath = importer.assetPath;
        importer.animationType = state.animationType;
        importer.avatarSetup = state.avatarSetup;
        importer.sourceAvatar = state.sourceAvatar;
        importer.autoGenerateAvatarMappingIfUnspecified = state.autoGenerateAvatarMappingIfUnspecified;
        importer.humanDescription = state.humanDescription;
        SaveImporterSettingsAndReimport(importer, assetPath);
    }

    private static string ConfigureImporterForHumanoidCreation(
        ModelImporter importer,
        string customPath,
        string sourceMappingFbxPath,
        HumanDescription fallbackDescription,
        out HumanDescription correctedDescription)
    {
        if (importer == null)
        {
            throw new ArgumentNullException(nameof(importer));
        }

        HumanDescription description = importer.humanDescription;
        if (description.skeleton == null || description.skeleton.Length == 0)
        {
            description.skeleton = BuildSkeletonFromFbxHierarchy(customPath);
        }

        HumanDescription sourceDescription = GetSourceHumanDescription(sourceMappingFbxPath, customPath, fallbackDescription);
        description.human = BuildHumanBoneMap(description, sourceDescription, out string mappingMessage);
        if (TryBuildPoseCorrectedSkeleton(customPath, description.human, out SkeletonBone[] correctedSkeleton, out string poseMessage))
        {
            description.skeleton = correctedSkeleton;
            mappingMessage = string.IsNullOrWhiteSpace(mappingMessage)
                ? poseMessage
                : $"{mappingMessage} {poseMessage}";
        }

        correctedDescription = description;

        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.sourceAvatar = null;
        importer.autoGenerateAvatarMappingIfUnspecified = false;
        importer.humanDescription = description;
        SaveImporterSettingsAndReimport(importer, customPath);

        return mappingMessage;
    }

    private static HumanDescription GetSourceHumanDescription(string sourceMappingFbxPath, string customFbxPath, HumanDescription fallback)
    {
        if (!string.IsNullOrWhiteSpace(sourceMappingFbxPath))
        {
            var sourceImporter = AssetImporter.GetAtPath(sourceMappingFbxPath) as ModelImporter;
            if (sourceImporter != null)
            {
                var sourceDescription = sourceImporter.humanDescription;
                if (sourceDescription.human != null && sourceDescription.human.Length > 0)
                {
                    return sourceDescription;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(customFbxPath))
        {
            var customImporter = AssetImporter.GetAtPath(customFbxPath) as ModelImporter;
            if (customImporter != null)
            {
                var customDescription = customImporter.humanDescription;
                if (customDescription.human != null && customDescription.human.Length > 0)
                {
                    return customDescription;
                }
            }
        }

        return fallback;
    }

    private static HumanBone[] BuildHumanBoneMap(HumanDescription targetDescription, HumanDescription sourceDescription, out string message)
    {
        var orderedTargetBoneNames = (targetDescription.skeleton ?? Array.Empty<SkeletonBone>())
            .Where(bone => !string.IsNullOrWhiteSpace(bone.name))
            .Select(bone => bone.name)
            .ToList();
        var targetBoneNames = new HashSet<string>(orderedTargetBoneNames, StringComparer.Ordinal);

        var mappedBones = new List<HumanBone>();
        var usedHumanNames = new HashSet<string>(StringComparer.Ordinal);

        HumanBone[] sourceHuman = sourceDescription.human ?? Array.Empty<HumanBone>();
        if (sourceHuman.Length > 0)
        {
            foreach (HumanBone sourceBone in sourceHuman)
            {
                if (string.IsNullOrWhiteSpace(sourceBone.humanName) ||
                    string.Equals(sourceBone.humanName, "Jaw", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(sourceBone.boneName) ||
                    !targetBoneNames.Contains(sourceBone.boneName) ||
                    !usedHumanNames.Add(sourceBone.humanName))
                {
                    continue;
                }

                mappedBones.Add(sourceBone);
            }

            message = $"Used source humanoid mapping ({mappedBones.Count}/{sourceHuman.Length} bones after MCB corrections).";
            AddMissingNameBasedMappings(mappedBones, usedHumanNames, orderedTargetBoneNames);
        }
        else
        {
            AddMissingNameBasedMappings(mappedBones, usedHumanNames, orderedTargetBoneNames);

            message = mappedBones.Count > 0
                ? $"Used name-based humanoid mapping ({mappedBones.Count} bones after MCB corrections)."
                : "Used Unity auto-mapping because no source humanoid mapping was available.";
        }

        ForceChestMapping(mappedBones, usedHumanNames, targetBoneNames);
        mappedBones.RemoveAll(bone => string.Equals(bone.humanName, "Jaw", StringComparison.Ordinal));

        return mappedBones.ToArray();
    }

    private static bool TryBuildPoseCorrectedSkeleton(string fbxPath, HumanBone[] humanBones, out SkeletonBone[] correctedSkeleton, out string message)
    {
        correctedSkeleton = null;
        message = null;

        var source = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(fbxPath));
        if (source == null || humanBones == null || humanBones.Length == 0)
        {
            return false;
        }

        var instance = UnityEngine.Object.Instantiate(source);
        instance.name = Path.GetFileNameWithoutExtension(MCBUtils.ToUnityPath(fbxPath));
        instance.hideFlags = HideFlags.HideAndDontSave;
        try { return TryBuildPoseCorrectedSkeleton(instance, humanBones, out correctedSkeleton, out message); }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
    }

    // The caller owns this temporary hierarchy. Never pass the user's scene skeleton here.
    private static bool TryBuildPoseCorrectedSkeleton(GameObject instance, HumanBone[] humanBones, out SkeletonBone[] correctedSkeleton, out string message)
    {
        correctedSkeleton = null;
        message = null;

        Type setupToolType = Type.GetType("UnityEditor.AvatarSetupTool, UnityEditor.CoreModule");
        Type boneWrapperType = Type.GetType("UnityEditor.AvatarSetupTool+BoneWrapper, UnityEditor.CoreModule");
        if (setupToolType == null || boneWrapperType == null)
        {
            return false;
        }

        ConstructorInfo boneWrapperConstructor = boneWrapperType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(Transform) },
            null);
        MethodInfo makePoseValid = setupToolType.GetMethod("MakePoseValid", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo isPoseValid = setupToolType.GetMethod("IsPoseValid", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo getPoseError = setupToolType.GetMethod("GetPoseError", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (boneWrapperConstructor == null || makePoseValid == null)
        {
            return false;
        }

        try
        {
            var humanToTransform = humanBones
                .Where(bone => !string.IsNullOrWhiteSpace(bone.humanName) && !string.IsNullOrWhiteSpace(bone.boneName))
                .GroupBy(bone => bone.humanName, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => FindChildTransformByName(instance.transform, group.First().boneName),
                    StringComparer.Ordinal);

            Array boneWrappers = Array.CreateInstance(boneWrapperType, HumanTrait.BoneCount);
            for (int i = 0; i < HumanTrait.BoneCount; i++)
            {
                humanToTransform.TryGetValue(HumanTrait.BoneName[i], out Transform mappedTransform);
                boneWrappers.SetValue(boneWrapperConstructor.Invoke(new object[] { HumanTrait.BoneName[i], mappedTransform }), i);
            }

            float errorBefore = TryGetPoseError(getPoseError, boneWrappers);
            makePoseValid.Invoke(null, new object[] { boneWrappers });
            bool validAfter = TryGetPoseValidity(isPoseValid, boneWrappers);
            float errorAfter = TryGetPoseError(getPoseError, boneWrappers);

            if (!validAfter)
            {
                message = $"Unity T-Pose correction was unavailable (pose error {errorBefore:0.###} -> {errorAfter:0.###}).";
                return false;
            }

            correctedSkeleton = instance.GetComponentsInChildren<Transform>(true)
                .Select(transform => new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale
                })
                .ToArray();
            message = $"Unity T-Pose correction applied (pose error {errorBefore:0.###} -> {errorAfter:0.###}).";
            return correctedSkeleton.Length > 0;
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[AvatarGeneration] Unity T-Pose correction failed, using imported skeleton pose: {ex.Message}");
            return false;
        }
    }

    private static float TryGetPoseError(MethodInfo getPoseError, Array boneWrappers)
    {
        if (getPoseError == null)
        {
            return float.NaN;
        }

        object value = getPoseError.Invoke(null, new object[] { boneWrappers });
        return value is float poseError ? poseError : float.NaN;
    }

    private static bool TryGetPoseValidity(MethodInfo isPoseValid, Array boneWrappers)
    {
        if (isPoseValid == null)
        {
            return true;
        }

        object value = isPoseValid.Invoke(null, new object[] { boneWrappers });
        return value is bool isValid && isValid;
    }

    private static Transform FindChildTransformByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (string.Equals(root.name, name, StringComparison.Ordinal))
        {
            return root;
        }

        foreach (Transform child in root)
        {
            Transform match = FindChildTransformByName(child, name);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static void AddMissingNameBasedMappings(List<HumanBone> mappedBones, HashSet<string> usedHumanNames, IReadOnlyList<string> orderedTargetBoneNames)
    {
        // Skeleton order puts parents first, so "Toe_Base_L" wins over its child "Toe.L".
        foreach (string boneName in orderedTargetBoneNames)
        {
            if (!NameOverrides.TryGetValue(boneName, out HumanBodyBones bone) && !BoneNames.TryInferHumanoid(boneName, out bone))
            {
                continue;
            }

            // MCB leaves the jaw unmapped and takes an upper chest only from the source mapping.
            string humanName = HumanTrait.BoneName[(int)bone];
            if (bone == HumanBodyBones.Jaw || bone == HumanBodyBones.UpperChest || !usedHumanNames.Add(humanName))
            {
                continue;
            }

            mappedBones.Add(new HumanBone
            {
                humanName = humanName,
                boneName = boneName,
                limit = CreateDefaultHumanLimit()
            });
        }
    }

    private static void ForceChestMapping(List<HumanBone> mappedBones, HashSet<string> usedHumanNames, HashSet<string> targetBoneNames)
    {
        string chestBoneName = FindTargetBoneName(targetBoneNames, "Chest", "chest", "Spine2", "spine2", "Spine_02", "spine_02", "spine.002");
        if (string.IsNullOrWhiteSpace(chestBoneName))
        {
            return;
        }

        int chestIndex = mappedBones.FindIndex(bone => string.Equals(bone.humanName, "Chest", StringComparison.Ordinal));
        if (chestIndex >= 0)
        {
            HumanBone chest = mappedBones[chestIndex];
            chest.boneName = chestBoneName;
            mappedBones[chestIndex] = chest;
            return;
        }

        if (usedHumanNames.Add("Chest"))
        {
            mappedBones.Add(new HumanBone
            {
                humanName = "Chest",
                boneName = chestBoneName,
                limit = CreateDefaultHumanLimit()
            });
        }
    }

    private static string FindTargetBoneName(HashSet<string> targetBoneNames, params string[] candidates)
    {
        if (targetBoneNames == null || candidates == null)
        {
            return null;
        }

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && targetBoneNames.Contains(candidate))
            {
                return candidate;
            }
        }

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string match = targetBoneNames.FirstOrDefault(name =>
                string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return null;
    }

    private static HumanLimit CreateDefaultHumanLimit()
    {
        return new HumanLimit
        {
            useDefaultValues = true
        };
    }

    private static Avatar LoadEmbeddedHumanAvatar(string fbxPath)
    {
        return AssetDatabase.LoadAllAssetsAtPath(MCBUtils.ToUnityPath(fbxPath))
            .OfType<Avatar>()
            .FirstOrDefault(avatar => avatar != null && avatar.isHuman && avatar.isValid);
    }

    private static Avatar BuildHumanAvatarFromDescription(string fbxPath, HumanDescription description, string outputPath)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(fbxPath));
        if (source == null)
        {
            throw new InvalidOperationException($"Could not load FBX for AvatarBuilder: {fbxPath}");
        }

        var instance = UnityEngine.Object.Instantiate(source);
        instance.name = Path.GetFileNameWithoutExtension(fbxPath);
        instance.hideFlags = HideFlags.HideAndDontSave;

        try
        {
            Avatar avatar = AvatarBuilder.BuildHumanAvatar(instance, description);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                if (avatar != null)
                {
                    UnityEngine.Object.DestroyImmediate(avatar);
                }

                throw new InvalidOperationException("AvatarBuilder returned an invalid humanoid Avatar.");
            }

            avatar.name = Path.GetFileNameWithoutExtension(MCBUtils.ToUnityPath(outputPath));
            return avatar;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    private static SkeletonBone[] BuildSkeletonFromFbxHierarchy(string fbxPath)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(fbxPath));
        if (source == null)
        {
            return Array.Empty<SkeletonBone>();
        }

        var instance = UnityEngine.Object.Instantiate(source);
        instance.name = Path.GetFileNameWithoutExtension(MCBUtils.ToUnityPath(fbxPath));
        instance.hideFlags = HideFlags.HideAndDontSave;

        try
        {
            return instance.GetComponentsInChildren<Transform>(true)
                .Select(transform => new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale
                })
                .ToArray();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    internal static Avatar SaveAvatarCopy(Avatar embeddedAvatar, string outputPath)
    {
        if (embeddedAvatar == null)
        {
            return null;
        }

        string normalizedOutputPath = MCBUtils.ToUnityPath(outputPath);
        EnsureAssetFolderExists(Path.GetDirectoryName(normalizedOutputPath)?.Replace('\\', '/'));

        UnityEngine.Object mainAsset = AssetDatabase.LoadMainAssetAtPath(normalizedOutputPath);
        if (mainAsset != null && !(mainAsset is Avatar))
        {
            throw new InvalidOperationException($"Cannot write generated Avatar because another asset already exists at {normalizedOutputPath}.");
        }

        Avatar copy = UnityEngine.Object.Instantiate(embeddedAvatar);
        copy.name = Path.GetFileNameWithoutExtension(normalizedOutputPath);

        Avatar existing = mainAsset as Avatar;
        if (existing != null)
        {
            try
            {
                EditorUtility.CopySerialized(copy, existing);
                existing.name = copy.name;
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(copy);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(normalizedOutputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                return AssetDatabase.LoadAssetAtPath<Avatar>(normalizedOutputPath);
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[AvatarGeneration] Failed to update existing Avatar in place, recreating it: {ex.Message}");
                UnityEngine.Object.DestroyImmediate(copy);
                AssetDatabase.DeleteAsset(normalizedOutputPath);
                copy = UnityEngine.Object.Instantiate(embeddedAvatar);
                copy.name = Path.GetFileNameWithoutExtension(normalizedOutputPath);
            }
        }

        AssetDatabase.CreateAsset(copy, normalizedOutputPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(normalizedOutputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<Avatar>(normalizedOutputPath);
    }

    private static void EnsureAssetFolderExists(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || AssetDatabase.IsValidFolder(folderPath))
        {
            return;
        }

        string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(parent))
        {
            EnsureAssetFolderExists(parent);
        }

        string parentFolder = string.IsNullOrWhiteSpace(parent) ? "Assets" : parent;
        string folderName = Path.GetFileName(folderPath);
        if (!AssetDatabase.IsValidFolder(folderPath))
        {
            AssetDatabase.CreateFolder(parentFolder, folderName);
        }
    }

    private static bool ApplyAvatarToFbxImporter(string fbxPath, Avatar avatar)
    {
        if (avatar == null || string.IsNullOrWhiteSpace(fbxPath))
        {
            return false;
        }

        var importer = AssetImporter.GetAtPath(MCBUtils.ToUnityPath(fbxPath)) as ModelImporter;
        if (importer == null)
        {
            return false;
        }

        bool changed = false;
        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            changed = true;
        }

        if (importer.avatarSetup != ModelImporterAvatarSetup.CopyFromOther)
        {
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            changed = true;
        }

        if (importer.sourceAvatar != avatar)
        {
            importer.sourceAvatar = avatar;
            changed = true;
        }

        if (changed)
        {
            SaveImporterSettingsAndReimport(importer, MCBUtils.ToUnityPath(fbxPath));
        }

        return changed;
    }

    private static void SaveImporterSettingsAndReimport(ModelImporter importer, string assetPath)
    {
        if (importer == null)
        {
            return;
        }

        string normalizedPath = MCBUtils.ToUnityPath(string.IsNullOrWhiteSpace(assetPath) ? importer.assetPath : assetPath);
        EditorUtility.SetDirty(importer);
        AssetDatabase.WriteImportSettingsIfDirty(normalizedPath);
        importer.SaveAndReimport();
        AssetDatabase.WriteImportSettingsIfDirty(normalizedPath);
        AssetDatabase.SaveAssets();

        var reloadedImporter = AssetImporter.GetAtPath(normalizedPath);
        if (reloadedImporter != null)
        {
            EditorUtility.ClearDirty(reloadedImporter);
        }
    }

    private static bool TryOpenAvatarConfigurationStage(string fbxPath, out string failureReason)
    {
        failureReason = null;
        try
        {
            var stageType = Type.GetType("UnityEditor.SceneManagement.AvatarConfigurationStage, UnityEditor.CoreModule");
            var stageUtilityType = Type.GetType("UnityEditor.SceneManagement.StageUtility, UnityEditor.CoreModule");
            if (stageType == null || stageUtilityType == null)
            {
                failureReason = "Unity editor stage types were not found";
                return false;
            }

            var createStage = stageType.GetMethod(
                "CreateStage",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var goToStage = stageUtilityType
                .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "GoToStage" && method.GetParameters().Length == 2);

            if (createStage == null || goToStage == null)
            {
                failureReason = "Unity editor stage methods were not found";
                return false;
            }

            object stage = createStage.Invoke(null, new object[] { MCBUtils.ToUnityPath(fbxPath), null });
            if (stage == null)
            {
                failureReason = "Unity returned no avatar configuration stage";
                return false;
            }

            goToStage.Invoke(null, new[] { stage, (object)true });
            return true;
        }
        catch (Exception ex)
        {
            Exception root = ex.InnerException ?? ex;
            failureReason = root.Message;
            return false;
        }
    }

    private static void SelectFbxAsset(string fbxPath)
    {
        var asset = AssetDatabase.LoadMainAssetAtPath(MCBUtils.ToUnityPath(fbxPath));
        if (asset == null)
        {
            return;
        }

        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
    }
}
#endif
