#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCBEditorUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Meshes;

// Putting a payload on an avatar: renderers, bones, pose.
public static partial class NativeMeshPayloadService
{
    /// <summary>The custom model's renderer for an avatar renderer's mapping: by its path in the model, else its mesh's or its own name.</summary>
    internal static SkinnedMeshRenderer ResolveCustomRenderer(Transform customRoot, ModelFileSmrPathData entry)
    {
        if (customRoot == null || entry == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(entry.fbxMeshPath))
        {
            var transform = FindTransformByRelativePath(customRoot, entry.fbxMeshPath);
            var renderer = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null)
            {
                return renderer;
            }
        }

        var renderers = customRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (!string.IsNullOrWhiteSpace(entry.meshName))
        {
            var byMesh = renderers.FirstOrDefault(renderer =>
                renderer != null &&
                renderer.sharedMesh != null &&
                string.Equals(renderer.sharedMesh.name, entry.meshName, StringComparison.Ordinal));
            if (byMesh != null)
            {
                return byMesh;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.rendererName))
        {
            return renderers.FirstOrDefault(renderer =>
                renderer != null &&
                string.Equals(renderer.transform.name, entry.rendererName, StringComparison.Ordinal));
        }

        return null;
    }

    private static void ApplyPayloadToAvatar(Transform avatarRoot, NativeMeshPayloadAsset payload)
    {
        if (avatarRoot == null || payload == null)
        {
            return;
        }

        // Reject incomplete associations before assigning any mesh. Positional bone
        // fallback can silently bind a skin weight to the wrong joint after rig edits.
        var records = payload.renderers ?? new List<NativeMeshPayloadRenderer>();
        EnsurePayloadBones(avatarRoot, payload);
        var targets = new SkinnedMeshRenderer[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record?.mesh == null) throw new InvalidDataException("Advanced mesh payload contains a missing mesh.");
            var renderer = ResolveAvatarRenderer(avatarRoot, record);
            if (renderer == null)
                throw new InvalidOperationException($"Cannot find the avatar renderer '{record.avatarPath}': it was moved or renamed. Put it back at '{record.avatarPath}' and try again.");
            int duplicate = Array.IndexOf(targets, renderer);
            if (duplicate >= 0)
                throw new InvalidOperationException($"Advanced mesh targets '{records[duplicate].avatarPath}' and '{record.avatarPath}' both resolve to the renderer '{renderer.name}'.");
            var bones = ResolveBoneArray(avatarRoot, record.bonePaths, renderer);
            if (bones.Length != (record.bonePaths?.Count ?? 0)
                || (record.mesh.bindposes.Length > 0 && bones.Length != record.mesh.bindposes.Length))
                throw new InvalidOperationException($"Cannot safely associate all skin bones for '{record.avatarPath}'.");
            targets[i] = renderer;
        }

        var total = System.Diagnostics.Stopwatch.StartNew();
        var step = System.Diagnostics.Stopwatch.StartNew();
        int rendererTotal = records.Count;
        for (int rendererIndex = 1; rendererIndex <= rendererTotal; rendererIndex++)
        {
            var record = records[rendererIndex - 1];
            var targetRenderer = targets[rendererIndex - 1];
            var rendererStep = System.Diagnostics.Stopwatch.StartNew();

            ApplyPayloadBoneTransformsForRenderer(avatarRoot, targetRenderer, payload, record);

            Undo.RecordObject(targetRenderer.transform, "Apply Native Mesh Renderer Transform");
            targetRenderer.transform.localPosition = record.localPosition;
            targetRenderer.transform.localRotation = record.localRotation;
            targetRenderer.transform.localScale = record.localScale;
            EditorUtility.SetDirty(targetRenderer.transform);

            Undo.RecordObject(targetRenderer, "Apply Native Mesh Payload");

            var bonePaths = record.bonePaths ?? new List<string>();
            var resolvedBones = ResolveBoneArray(avatarRoot, bonePaths, targetRenderer);
            if (record.mesh.bindposes != null && record.mesh.bindposes.Length > 0 && record.mesh.bindposes.Length != resolvedBones.Length)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Renderer '{targetRenderer.name}' resolved {resolvedBones.Length} bones for {record.mesh.bindposes.Length} bindposes. The mesh may deform incorrectly.");
            }
            if (resolvedBones.Length == bonePaths.Count && resolvedBones.Length > 0)
            {
                targetRenderer.bones = resolvedBones;
            }
            else if (bonePaths.Count > 0)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve all bones for renderer '{targetRenderer.name}'. Resolved {resolvedBones.Length}/{bonePaths.Count}; the mesh may deform incorrectly.");
            }

            var rootBone = ResolveAvatarTransform(avatarRoot, record.rootBonePath);
            if (rootBone == null && !string.IsNullOrWhiteSpace(record.rootBonePath))
            {
                rootBone = ResolveBoneByName(targetRenderer, GetLastPathSegment(record.rootBonePath));
            }
            if (rootBone != null)
            {
                targetRenderer.rootBone = rootBone;
            }

            if (targetRenderer.sharedMesh != record.mesh) {
                EyelidBlendshapes.Assign(targetRenderer, record.mesh);
                RefreshSkinnedRenderer(targetRenderer, record.mesh);
            }
            EditorUtility.SetDirty(targetRenderer);
            MCBLogger.Log(
                $"[NativeMeshPayloadProfile] Applied renderer {rendererIndex}/{rendererTotal} '{targetRenderer.name}' " +
                $"mesh='{record.mesh.name}' bones={(record.bonePaths?.Count ?? 0)} verts={record.mesh.vertexCount}: " +
                $"step={rendererStep.Elapsed.TotalMilliseconds:F1} ms total={total.Elapsed.TotalMilliseconds:F1} ms");
            step.Restart();
        }
        LogApplyProfile("Finished renderer mesh assignment", step, total, $"renderers={rendererTotal}");
    }

    /// <param name="basePoseFbxPath">The original base model the avatar is on. A plain payload's skeleton is the version
    /// model's own rest pose (Ultirex's is a star pose); with this model the avatar keeps the base's pose instead.</param>
    public static void ApplyPayloadAuthoringPose(Transform avatarRoot, NativeMeshPayloadAsset payload, string basePoseFbxPath = null)
    {
        if (avatarRoot == null || payload == null)
        {
            return;
        }

        if (payload.authoringPoseBones == null || payload.authoringPoseBones.Count == 0)
        {
            throw new InvalidDataException("Native mesh payload is missing required authoring pose data.");
        }

        var payloadBonesByPath = (payload.bones ?? new List<NativeMeshPayloadBone>())
            .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.path))
            .GroupBy(bone => bone.path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        ApplyPayloadStructuralBoneTransforms(avatarRoot, payload);
        // Plain payloads name no source model: their absolute custom skeleton is the authoring pose.
        Transform sourceRoot = ResolveSourcePoseRoot(payload.sourceFbxPath);
        // The payload names the creator's model path. A buyer's copy of that same original may live elsewhere (a moved or
        // renamed package folder): the avatar's own base model is then the source.
        if (sourceRoot == null && !string.IsNullOrEmpty(payload.sourceFbxPath) && IsModelWithHash(basePoseFbxPath, payload.sourceFbxHash))
        {
            sourceRoot = ResolveSourcePoseRoot(basePoseFbxPath);
        }
        // Another original (a different release of the base) keeps the payload's own skeleton, which its meshes are bound to.
        if (sourceRoot == null && !string.IsNullOrEmpty(payload.sourceFbxPath))
        {
            MCBLogger.Log($"[NativeMeshPayload] This avatar's original is not '{payload.sourceFbxPath}': the payload's own skeleton is the authoring pose.");
        }

        int applied = 0;
        foreach (var poseBone in payload.authoringPoseBones)
        {
            if (poseBone == null || string.IsNullOrWhiteSpace(poseBone.path))
            {
                continue;
            }

            var target = ResolveAvatarTransform(avatarRoot, poseBone.path);
            if (target == null)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve authoring pose transform '{poseBone.path}'.");
                continue;
            }

            var source = sourceRoot != null ? FindTransformByRelativePath(sourceRoot, poseBone.path) : null;
            Vector3 targetPosition;
            Quaternion targetRotation;
            Vector3 targetScale;
            if (source != null)
            {
                targetPosition = source.localPosition + poseBone.localPositionOffset;
                targetRotation = source.localRotation * poseBone.localRotationDelta;
                targetScale = Vector3.Scale(source.localScale, poseBone.localScaleMultiplier);
            }
            else if (payloadBonesByPath.TryGetValue(poseBone.path, out var payloadBone))
            {
                targetPosition = payloadBone.localPosition;
                targetRotation = payloadBone.localRotation;
                targetScale = payloadBone.localScale;
            }
            else
            {
                // The payload's skeleton holds bones only: a renderer's own transform keeps its place without a source model.
                if (sourceRoot != null) MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve authoring base transform '{poseBone.path}'.");
                continue;
            }

            Undo.RecordObject(target, "Apply Native Mesh Authoring Pose");
            target.localPosition = targetPosition;
            target.localRotation = targetRotation;
            target.localScale = targetScale;
            EditorUtility.SetDirty(target);
            applied++;
        }

        AvatarDefinitionGenerationService.ApplyNativeMeshAvatar(avatarRoot, payload);
        if (string.IsNullOrEmpty(payload.sourceFbxPath) && !string.IsNullOrEmpty(basePoseFbxPath)) KeepBasePose(avatarRoot, basePoseFbxPath);
        RefreshAvatarSkinnedRenderers(avatarRoot, payload.payloadHash != null && payload.payloadHash.StartsWith("raw:", StringComparison.Ordinal));
        MCBLogger.Log($"[NativeMeshPayload] Applied authoring pose deltas to {applied}/{payload.authoringPoseBones.Count} transforms.");
    }

    // Limb bones and the humanoid bone each one points at. The spine, neck and head keep the version's own shape.
    private static readonly (HumanBodyBones bone, HumanBodyBones toward)[] PoseLimbs =
    {
        (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot), (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes),
        (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg), (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot), (HumanBodyBones.RightFoot, HumanBodyBones.RightToes),
        (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm), (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand), (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal),
        (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm), (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand), (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal),
        (HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate), (HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal),
        (HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate), (HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal),
        (HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate), (HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal),
        (HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate), (HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal),
        (HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate), (HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal),
        (HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate), (HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal),
        (HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate), (HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal),
        (HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate), (HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal),
        (HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate), (HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal),
        (HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate), (HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal),
    };

    /// <summary>
    /// Poses the avatar's limbs like its original base model's rest pose (e.g. a T-pose): each limb bone is turned, parents
    /// first, to point where the same humanoid bone points in that model. Bone lengths, the hips and the spine stay the
    /// version's own; non-humanoid bones follow their parents.
    /// </summary>
    internal static bool KeepBasePose(Transform avatarRoot, string basePoseFbxPath)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(basePoseFbxPath));
        var baseAnimator = model != null ? model.GetComponent<Animator>() : null;
        var animator = avatarRoot != null ? avatarRoot.GetComponent<Animator>() : null;
        if (baseAnimator == null || baseAnimator.avatar == null || !baseAnimator.avatar.isHuman || animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            MCBLogger.LogWarning($"[NativeMeshPayload] The original base pose could not be kept: '{basePoseFbxPath}' or the applied avatar is not a valid humanoid.");
            return false;
        }

        var instance = UnityEngine.Object.Instantiate(model);
        instance.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var reference = instance.GetComponent<Animator>();
            // The bones the meshes are skinned to: MCB's logic proxies carry the same names, and an Animator may bind those.
            var weighted = new HashSet<Transform>(avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(b => b != null));
            var byName = avatarRoot.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(weighted.Contains).First(), StringComparer.Ordinal);
            var mapped = animator.avatar.humanDescription.human.GroupBy(h => h.humanName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().boneName, StringComparer.Ordinal);
            Transform Bone(HumanBodyBones bone) =>
                mapped.TryGetValue(HumanTrait.BoneName[(int)bone], out var name) && byName.TryGetValue(name, out var transform) ? transform : null;
            // Directions are compared in each avatar's own root space, so where the avatar stands does not matter.
            var toAvatar = avatarRoot.rotation * Quaternion.Inverse(instance.transform.rotation);
            var recorded = new HashSet<Transform>();
            int turned = 0;
            foreach (var (bone, toward) in PoseLimbs)
            {
                Transform from = Bone(bone), to = Bone(toward);
                Transform baseFrom = reference.GetBoneTransform(bone), baseTo = reference.GetBoneTransform(toward);
                if (from == null || to == null || baseFrom == null || baseTo == null) continue;
                Vector3 current = to.position - from.position, wanted = toAvatar * (baseTo.position - baseFrom.position);
                if (current.sqrMagnitude < 1e-10f || wanted.sqrMagnitude < 1e-10f) continue;
                if (recorded.Add(from)) Undo.RecordObject(from, "Keep Original Base Pose");
                from.rotation = Quaternion.FromToRotation(current, wanted) * from.rotation;
                turned++;
            }
            return turned > 0;
        }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
    }

    private static void ApplyPayloadStructuralBoneTransforms(Transform avatarRoot, NativeMeshPayloadAsset payload)
    {
        if (avatarRoot == null || payload?.bones == null)
        {
            return;
        }

        foreach (var payloadBone in payload.bones)
        {
            if (payloadBone == null || string.IsNullOrWhiteSpace(payloadBone.path))
            {
                continue;
            }

            var target = ResolveAvatarTransform(avatarRoot, payloadBone.path);
            if (target == null)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve structural bone transform '{payloadBone.path}'.");
                continue;
            }

            Undo.RecordObject(target, "Apply Native Mesh Structural Bone Pose");
            target.localPosition = payloadBone.localPosition;
            target.localRotation = payloadBone.localRotation;
            target.localScale = payloadBone.localScale;
            EditorUtility.SetDirty(target);
        }
    }

    private static void RefreshSkinnedRenderer(SkinnedMeshRenderer renderer, Mesh mesh)
    {
        if (renderer == null)
        {
            return;
        }

        bool wasEnabled = renderer.enabled;
        bool previousUpdateWhenOffscreen = renderer.updateWhenOffscreen;

        renderer.updateWhenOffscreen = true;
        renderer.sharedMesh = null;
        renderer.sharedMesh = mesh;

        if (wasEnabled)
        {
            renderer.enabled = false;
            renderer.enabled = true;
        }

        renderer.updateWhenOffscreen = previousUpdateWhenOffscreen;
        SkinnedMeshBounds.Refresh(renderer);
    }

    private static void RefreshAvatarSkinnedRenderers(Transform avatarRoot, bool preserveSharedMeshes = false)
    {
        if (avatarRoot == null)
        {
            return;
        }

        foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer != null)
            {
                if (preserveSharedMeshes && IsSharedMeshForVersion(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)), null))
                {
                    SkinnedMeshBounds.Refresh(renderer);
                    continue;
                }
                RefreshSkinnedRenderer(renderer, renderer.sharedMesh);
            }
        }
    }

    private static void ApplyPayloadBoneTransformsForRenderer(
        Transform avatarRoot,
        SkinnedMeshRenderer targetRenderer,
        NativeMeshPayloadAsset payload,
        NativeMeshPayloadRenderer record)
    {
        if (avatarRoot == null || targetRenderer == null || payload == null || record == null)
        {
            return;
        }

        var payloadBonesByPath = (payload.bones ?? new List<NativeMeshPayloadBone>())
            .Where(bone => bone != null && !string.IsNullOrWhiteSpace(bone.path))
            .GroupBy(bone => bone.path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (string path in CollectPayloadBonePaths(new[] { record }))
        {
            if (!payloadBonesByPath.TryGetValue(path, out var payloadBone))
            {
                continue;
            }

            var target = ResolveAvatarTransform(avatarRoot, path);
            if (target == null)
            {
                target = ResolveBoneByName(targetRenderer, GetLastPathSegment(path));
            }

            if (target == null)
            {
                MCBLogger.LogWarning($"[NativeMeshPayload] Could not resolve bone transform '{path}' for renderer '{targetRenderer.name}'.");
                continue;
            }

            Undo.RecordObject(target, "Apply Native Mesh Bone Pose");
            target.localPosition = payloadBone.localPosition;
            target.localRotation = payloadBone.localRotation;
            target.localScale = payloadBone.localScale;
            EditorUtility.SetDirty(target);
        }
    }

    private static HashSet<string> CollectPayloadBonePaths(IEnumerable<NativeMeshPayloadRenderer> renderers)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var renderer in renderers ?? Enumerable.Empty<NativeMeshPayloadRenderer>())
        {
            if (renderer == null)
            {
                continue;
            }

            AddPathAndAncestors(paths, renderer.rootBonePath);
            foreach (string bonePath in renderer.bonePaths ?? new List<string>())
            {
                AddPathAndAncestors(paths, bonePath);
            }
        }

        return paths;
    }

    private static void AddPathAndAncestors(HashSet<string> paths, string path)
    {
        if (paths == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string[] parts = path.Split('/');
        string current = "";
        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim();
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";
            paths.Add(current);
        }
    }

    private static Transform[] ResolveBoneArray(Transform avatarRoot, List<string> bonePaths, SkinnedMeshRenderer targetRenderer)
    {
        if (avatarRoot == null || bonePaths == null || bonePaths.Count == 0)
        {
            return Array.Empty<Transform>();
        }

        var result = new List<Transform>(bonePaths.Count);
        for (int i = 0; i < bonePaths.Count; i++)
        {
            string path = bonePaths[i];
            var bone = ResolveAvatarTransform(avatarRoot, path);
            if (bone == null)
            {
                bone = ResolveBoneByName(targetRenderer, GetLastPathSegment(path));
            }
            if (bone == null)
            {
                return Array.Empty<Transform>();
            }

            result.Add(bone);
        }

        return result.ToArray();
    }

    private static Transform ResolveBoneByName(SkinnedMeshRenderer renderer, string boneName)
    {
        if (renderer == null || string.IsNullOrWhiteSpace(boneName))
        {
            return null;
        }

        var matches = (renderer.bones ?? Array.Empty<Transform>())
            .Where(bone => bone != null && string.Equals(bone.name, boneName, StringComparison.Ordinal)).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static SkinnedMeshRenderer ResolveAvatarRenderer(Transform avatarRoot, NativeMeshPayloadRenderer record)
    {
        if (avatarRoot == null || record == null || record.avatarPath == null) return null;
        // Mesh/renderer names are not identities: clothing can be the only remaining mesh named "Body".
        // An empty recorded path is a renderer on the avatar root; a missing path must be remapped explicitly.
        var target = record.avatarPath.Length == 0 ? avatarRoot : FindTransformByRelativePath(avatarRoot, record.avatarPath);
        var renderer = target != null ? target.GetComponent<SkinnedMeshRenderer>() : null;
        if (renderer != null || record.avatarPath.Length == 0) return renderer;
        // The avatar may sit under a scene organizer object: accept the single renderer whose path ends with the recorded one.
        string suffix = "/" + record.avatarPath.Trim('/');
        var matches = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r != null && ("/" + GetRelativePath(avatarRoot, r.transform)).EndsWith(suffix, StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string GetRelativePath(Transform root, Transform target)
    {
        var parts = new List<string>();
        for (var t = target; t != null && t != root; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static IEnumerable<ModelFileData> GetSourceFilesForPatch(CustomBaseVersion version, ModelFileData patch)
    {
        if (version?.sourceFiles == null || patch == null)
        {
            yield break;
        }

        if (patch.sourceModelFileId.HasValue)
        {
            var source = version.sourceFiles.FirstOrDefault(file => file != null && file.id == patch.sourceModelFileId.Value);
            if (source != null)
            {
                yield return source;
                yield break;
            }
        }

        string sourcePath = null;
        if (patch.metadata != null &&
            patch.metadata.TryGetValue("sourcePath", out object sourcePathValue))
        {
            sourcePath = sourcePathValue?.ToString();
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var source = version.sourceFiles.FirstOrDefault(file =>
                file != null &&
                SourceFileMatchesPath(file, MCBUtils.ToUnityPath(sourcePath)));
            if (source != null)
            {
                yield return source;
                yield break;
            }
        }
    }

    private static IEnumerable<ModelFileData> GetAdvancedPatchFiles(CustomBaseVersion version)
    {
        return version?.versionFiles?
            .Where(file => file != null &&
                           string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase) &&
                           IsAdvancedMeshPatchTransform(file.transform)) ??
               Enumerable.Empty<ModelFileData>();
    }

    private static IEnumerable<SkinnedMeshRenderer> ResolveRenderersForSource(Transform avatarRoot, ModelFileData source)
    {
        if (avatarRoot == null || source == null)
        {
            yield break;
        }

        foreach (var entry in source.smrPaths ?? new List<ModelFileSmrPathData>())
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.avatarPath))
            {
                continue;
            }

            var transform = FindTransformByRelativePath(avatarRoot, entry.avatarPath);
            var renderer = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null)
            {
                yield return renderer;
            }
        }
    }

    private static Transform ResolveAvatarTransform(Transform avatarRoot, string path)
    {
        if (avatarRoot == null)
        {
            return null;
        }

        var byPath = FindTransformByRelativePath(avatarRoot, path);
        if (byPath != null)
        {
            return byPath;
        }

        string name = GetLastPathSegment(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Logic proxies can have the same name. Prefer the uniquely weighted skeleton bone.
        var weighted = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .SelectMany(renderer => renderer.bones).Where(bone => bone != null && bone.name == name && bone.IsChildOf(avatarRoot))
            .Distinct().Take(2).ToArray();
        if (weighted.Length == 1) return weighted[0];

        var matches = avatarRoot
            .GetComponentsInChildren<Transform>(true)
            .Where(transform => transform != null && string.Equals(transform.name, name, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static Transform FindTransformByRelativePath(Transform root, string relativePath)
    {
        if (root == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        Transform current = root;
        foreach (string rawPart in relativePath.Split('/'))
        {
            string part = rawPart.Trim();
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            current = current.Find(part);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static string GetRelativeTransformPath(Transform root, Transform target)
    {
        if (root == null || target == null || target == root)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        Transform current = target;
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }
}
#endif
