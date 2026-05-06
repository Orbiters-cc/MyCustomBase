#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

public class AnimationPositionOffsetService
{
    private static AnimationPositionOffsetService _instance;
    public static AnimationPositionOffsetService Instance => _instance ??= new AnimationPositionOffsetService();

    private const string VariantPrefix = "UP_BONE_OFFSET_VARIANT_";
    private const float PositionEpsilon = 0.0001f;

    public struct ApplyResult
    {
        public bool success;
        public int offsetsResolved;
        public int controllersProcessed;
        public int clipsDuplicated;
        public int motionsRewritten;
        public string message;
    }

    private class SkeletonNode
    {
        public string name;
        public string parentName;
        public Vector3 position;
    }

    public static List<AnimationPositionOffsetEntry> BuildOffsetsForVersion(CustomBaseVersion version)
    {
        var output = new List<AnimationPositionOffsetEntry>();
        if (version?.versionFiles == null || version.versionFiles.Length == 0) return output;

        var dedupe = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patchFile in version.versionFiles)
        {
            if (!IsAnimationOffsetPatch(patchFile)) continue;

            var sourceFile = ResolveSourceFile(version, patchFile);
            string sourceMeta = GetModelImporterMeta(sourceFile, sourceFile?.path);
            string customMeta = GetModelImporterMeta(patchFile, GetMetadataString(patchFile, "customFbxPath"));
            if (string.IsNullOrWhiteSpace(sourceMeta) || string.IsNullOrWhiteSpace(customMeta)) continue;

            var sourceSkeleton = ParseSkeletonPositions(sourceMeta);
            var customSkeleton = ParseSkeletonPositions(customMeta);
            if (sourceSkeleton.Count == 0 || customSkeleton.Count == 0) continue;

            foreach (var sourceBone in sourceSkeleton)
            {
                if (string.IsNullOrWhiteSpace(sourceBone.Key)) continue;
                if (!TryGetSkeletonPosition(customSkeleton, sourceBone.Key, out var customPosition)) continue;

                Vector3 offset = customPosition - sourceBone.Value;
                if (offset.sqrMagnitude <= PositionEpsilon * PositionEpsilon) continue;

                string sourcePath = sourceFile != null ? MCBUtils.ToUnityPath(sourceFile.path) : string.Empty;
                string key = sourcePath + "|" + sourceBone.Key;
                if (!dedupe.Add(key)) continue;

                output.Add(new AnimationPositionOffsetEntry
                {
                    sourceFbxPath = sourcePath,
                    bonePath = sourceBone.Key,
                    offset = offset
                });
            }
        }

        return output;
    }

    public ApplyResult ApplyActiveVersionOffsets(GameObject avatarRoot)
    {
        if (avatarRoot == null) return Fail("Avatar root is null.");

        if (!TryResolveOffsetState(avatarRoot, out var offsets))
        {
            return Fail("No applied version bone position offsets were found.");
        }

        var offsetLookup = BuildOffsetLookup(offsets);
        if (offsetLookup.Count == 0)
        {
            return Fail("No moved bones with usable animation paths were found.");
        }

        var controllers = CollectVrcFuryBuiltControllers(avatarRoot);
        if (controllers.Count == 0)
        {
            return Fail("No VRCFury temporary AnimatorController found on avatar.");
        }

        int clipsDuplicated = 0;
        int motionsRewritten = 0;
        var changedControllers = new HashSet<AnimatorController>();

        foreach (var controller in controllers)
        {
            if (controller == null) continue;

            bool controllerChanged = false;
            var clipCache = new Dictionary<AnimationClip, AnimationClip>();
            var visitedStateMachines = new HashSet<AnimatorStateMachine>();
            var visitedTrees = new HashSet<BlendTree>();

            foreach (var layer in controller.layers ?? Array.Empty<AnimatorControllerLayer>())
            {
                if (layer?.stateMachine == null) continue;
                if (RewriteStateMachine(controller, layer.stateMachine, offsetLookup, clipCache, visitedStateMachines, visitedTrees,
                        ref clipsDuplicated, ref motionsRewritten))
                {
                    controllerChanged = true;
                }
            }

            if (!controllerChanged) continue;
            changedControllers.Add(controller);
            EditorUtility.SetDirty(controller);
        }

        if (changedControllers.Count == 0)
        {
            return Fail("No animation clips with local-position curves on moved bones were found.");
        }

        AssetDatabase.SaveAssets();
        return new ApplyResult
        {
            success = true,
            offsetsResolved = offsetLookup.Count,
            controllersProcessed = changedControllers.Count,
            clipsDuplicated = clipsDuplicated,
            motionsRewritten = motionsRewritten,
            message = $"Applied bone animation offsets: {offsetLookup.Count} moved bone(s), {changedControllers.Count} controller(s), {clipsDuplicated} duplicated clip(s), {motionsRewritten} rewritten motion reference(s)."
        };
    }

    private static bool TryResolveOffsetState(GameObject avatarRoot, out List<AnimationPositionOffsetEntry> offsets)
    {
        offsets = null;
        var customBase = FindCustomBase(avatarRoot);
        if (customBase == null) return false;

        if (customBase.appliedCustomBaseVersion != null)
        {
            var versionOffsets = BuildOffsetsForVersion(customBase.appliedCustomBaseVersion);
            if (versionOffsets.Count > 0)
            {
                offsets = versionOffsets;
                return true;
            }
        }

        if (customBase.appliedVersionAnimationPositionOffsetsCache != null &&
            customBase.appliedVersionAnimationPositionOffsetsCache.Count > 0)
        {
            offsets = customBase.appliedVersionAnimationPositionOffsetsCache
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.bonePath) &&
                            x.offset.sqrMagnitude > PositionEpsilon * PositionEpsilon)
                .Select(x => new AnimationPositionOffsetEntry
                {
                    sourceFbxPath = x.sourceFbxPath,
                    bonePath = x.bonePath,
                    offset = x.offset
                })
                .ToList();
            return offsets.Count > 0;
        }

        var persistedVersion = TryResolvePersistedVersion(customBase);
        if (persistedVersion != null)
        {
            var versionOffsets = BuildOffsetsForVersion(persistedVersion);
            if (versionOffsets.Count > 0)
            {
                SyncOffsetCache(customBase, versionOffsets);
                offsets = versionOffsets;
                return true;
            }
        }

        return false;
    }

    private static void SyncOffsetCache(MyCustomBase customBase, List<AnimationPositionOffsetEntry> offsets)
    {
        if (customBase == null || offsets == null || offsets.Count == 0) return;

        var cache = customBase.appliedVersionAnimationPositionOffsetsCache;
        if (cache == null)
        {
            cache = new List<AnimationPositionOffsetEntry>();
            customBase.appliedVersionAnimationPositionOffsetsCache = cache;
        }

        cache.Clear();
        foreach (var offset in offsets)
        {
            if (offset == null || string.IsNullOrWhiteSpace(offset.bonePath)) continue;
            cache.Add(new AnimationPositionOffsetEntry
            {
                sourceFbxPath = offset.sourceFbxPath,
                bonePath = offset.bonePath,
                offset = offset.offset
            });
        }

        if (!EditorApplication.isPlaying)
        {
            EditorUtility.SetDirty(customBase);
        }
    }

    private static CustomBaseVersion TryResolvePersistedVersion(MyCustomBase customBase)
    {
        if (customBase == null || customBase.appliedCustomBaseAssetId <= 0 ||
            string.IsNullOrWhiteSpace(customBase.appliedCustomBaseVersionString))
        {
            return null;
        }

        var downloadedVersion = TryLoadDownloadedVersion(customBase);
        if (downloadedVersion != null)
        {
            return downloadedVersion;
        }

        foreach (string unityPath in GetBaseFbxLookupPaths(customBase))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(unityPath);
            }
            catch
            {
                continue;
            }

            string baseHash = PersistentCache.Instance.GetCachedHash(fullPath);
            if (string.IsNullOrWhiteSpace(baseHash)) continue;

            var cached = PersistentCache.Instance.GetCachedVersions(baseHash, null, customBase.appliedCustomBaseAssetId);
            var match = FindMatchingVersion(cached?.serverVersions, customBase);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static CustomBaseVersion TryLoadDownloadedVersion(MyCustomBase customBase)
    {
        string versionPath = MCBUtils.GetVersionDataPath(
            customBase.appliedCustomBaseAssetId,
            customBase.appliedCustomBaseVersionString,
            customBase.appliedCustomBaseDefaultAviVersion);
        if (string.IsNullOrWhiteSpace(versionPath)) return null;

        string jsonPath = Path.Combine(versionPath, "version.json");
        try
        {
            string fullPath = Path.GetFullPath(jsonPath);
            if (!File.Exists(fullPath)) return null;

            var version = JsonConvert.DeserializeObject<CustomBaseVersion>(File.ReadAllText(fullPath));
            if (FindMatchingVersion(new[] { version }, customBase) == null) return null;
            if (version.assetId <= 0) version.assetId = customBase.appliedCustomBaseAssetId;
            return version;
        }
        catch
        {
            return null;
        }
    }

    private static CustomBaseVersion FindMatchingVersion(IEnumerable<CustomBaseVersion> versions, MyCustomBase customBase)
    {
        if (versions == null || customBase == null) return null;

        return versions.FirstOrDefault(version =>
            version != null &&
            (version.assetId <= 0 || version.assetId == customBase.appliedCustomBaseAssetId) &&
            string.Equals(version.version, customBase.appliedCustomBaseVersionString, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(customBase.appliedCustomBaseDefaultAviVersion) ||
             string.Equals(version.defaultAviVersion, customBase.appliedCustomBaseDefaultAviVersion, StringComparison.Ordinal)));
    }

    private static IEnumerable<string> GetBaseFbxLookupPaths(MyCustomBase customBase)
    {
        var baseFbxFiles = customBase?.baseFbxFiles;
        if (baseFbxFiles == null) yield break;

        foreach (var fbx in baseFbxFiles)
        {
            if (fbx == null) continue;

            string unityPath = AssetDatabase.GetAssetPath(fbx);
            if (string.IsNullOrWhiteSpace(unityPath)) continue;

            string normalizedPath = MCBUtils.ToUnityPath(unityPath);
            string originalPath = normalizedPath + FileManagerService.OriginalSuffix;
            if (TryFileExists(originalPath))
            {
                yield return originalPath;
            }

            yield return normalizedPath;
        }
    }

    private static bool TryFileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, Vector3> BuildOffsetLookup(IEnumerable<AnimationPositionOffsetEntry> offsets)
    {
        var lookup = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var ambiguousPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var offset in offsets ?? Enumerable.Empty<AnimationPositionOffsetEntry>())
        {
            if (offset == null || string.IsNullOrWhiteSpace(offset.bonePath)) continue;
            if (offset.offset.sqrMagnitude <= PositionEpsilon * PositionEpsilon) continue;

            if (ambiguousPaths.Contains(offset.bonePath)) continue;
            if (lookup.TryGetValue(offset.bonePath, out var existingOffset))
            {
                if ((existingOffset - offset.offset).sqrMagnitude > PositionEpsilon * PositionEpsilon)
                {
                    lookup.Remove(offset.bonePath);
                    ambiguousPaths.Add(offset.bonePath);
                }
                continue;
            }

            lookup.Add(offset.bonePath, offset.offset);
        }

        return lookup;
    }

    private static bool RewriteStateMachine(
        AnimatorController controller,
        AnimatorStateMachine stateMachine,
        Dictionary<string, Vector3> offsetLookup,
        Dictionary<AnimationClip, AnimationClip> clipCache,
        ISet<AnimatorStateMachine> visitedStateMachines,
        ISet<BlendTree> visitedTrees,
        ref int clipsDuplicated,
        ref int motionsRewritten)
    {
        if (stateMachine == null || visitedStateMachines.Contains(stateMachine)) return false;
        visitedStateMachines.Add(stateMachine);

        bool changed = false;
        foreach (var childState in stateMachine.states)
        {
            var state = childState.state;
            if (state == null || state.motion == null) continue;

            bool motionChanged = false;
            var rewritten = RewriteMotion(controller, state.motion, offsetLookup, clipCache, visitedTrees,
                ref clipsDuplicated, ref motionChanged);
            if (motionChanged && rewritten != null && rewritten != state.motion)
            {
                state.motion = rewritten;
                EditorUtility.SetDirty(state);
                motionsRewritten++;
                changed = true;
            }
        }

        foreach (var childMachine in stateMachine.stateMachines)
        {
            if (childMachine.stateMachine == null) continue;
            if (RewriteStateMachine(controller, childMachine.stateMachine, offsetLookup, clipCache, visitedStateMachines,
                    visitedTrees, ref clipsDuplicated, ref motionsRewritten))
            {
                changed = true;
            }
        }

        return changed;
    }

    private static Motion RewriteMotion(
        AnimatorController controller,
        Motion motion,
        Dictionary<string, Vector3> offsetLookup,
        Dictionary<AnimationClip, AnimationClip> clipCache,
        ISet<BlendTree> visitedTrees,
        ref int clipsDuplicated,
        ref bool changed)
    {
        if (motion == null) return null;

        if (motion is BlendTree tree)
        {
            if (visitedTrees.Contains(tree)) return tree;
            visitedTrees.Add(tree);

            bool childrenChanged = false;
            var children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                var childMotion = children[i].motion;
                bool childChanged = false;
                var rewritten = RewriteMotion(controller, childMotion, offsetLookup, clipCache, visitedTrees,
                    ref clipsDuplicated, ref childChanged);
                if (childChanged && rewritten != null && rewritten != childMotion)
                {
                    children[i].motion = rewritten;
                    childrenChanged = true;
                }
            }

            if (childrenChanged)
            {
                tree.children = children;
                EditorUtility.SetDirty(tree);
                changed = true;
            }

            return tree;
        }

        if (!(motion is AnimationClip clip)) return motion;
        if (clipCache.TryGetValue(clip, out var cached))
        {
            if (cached != clip) changed = true;
            return cached;
        }

        var variant = TryCreateOffsetVariant(controller, clip, offsetLookup);
        if (variant == null)
        {
            clipCache[clip] = clip;
            return clip;
        }

        clipCache[clip] = variant;
        clipsDuplicated++;
        changed = true;
        return variant;
    }

    private static AnimationClip TryCreateOffsetVariant(
        AnimatorController controller,
        AnimationClip sourceClip,
        Dictionary<string, Vector3> offsetLookup)
    {
        if (sourceClip == null || offsetLookup == null || offsetLookup.Count == 0) return null;
        if (sourceClip.name.StartsWith(VariantPrefix, StringComparison.Ordinal)) return null;

        var matchingBindings = new List<(EditorCurveBinding binding, float offset)>();
        foreach (var binding in AnimationUtility.GetCurveBindings(sourceClip))
        {
            if (!IsTransformLocalPositionBinding(binding, out int axis)) continue;
            if (!TryGetOffsetForPath(offsetLookup, binding.path, out var offset)) continue;

            float component = axis == 0 ? offset.x : axis == 1 ? offset.y : offset.z;
            if (Mathf.Abs(component) <= PositionEpsilon) continue;

            var curve = AnimationUtility.GetEditorCurve(sourceClip, binding);
            if (curve == null || curve.length == 0) continue;
            matchingBindings.Add((binding, component));
        }

        if (matchingBindings.Count == 0) return null;

        var variantClip = UnityEngine.Object.Instantiate(sourceClip);
        variantClip.name = BuildName(VariantPrefix, sourceClip.name);
        variantClip.hideFlags = HideFlags.HideInHierarchy;

        foreach (var match in matchingBindings)
        {
            var curve = AnimationUtility.GetEditorCurve(sourceClip, match.binding);
            AnimationUtility.SetEditorCurve(variantClip, match.binding, OffsetCurve(curve, match.offset));
        }

        AttachAsSubAsset(controller, variantClip);
        EditorUtility.SetDirty(variantClip);
        return variantClip;
    }

    private static AnimationCurve OffsetCurve(AnimationCurve source, float offset)
    {
        if (source == null) return null;

        var curve = new AnimationCurve(source.keys)
        {
            preWrapMode = source.preWrapMode,
            postWrapMode = source.postWrapMode
        };

        var keys = curve.keys;
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i].value += offset;
        }

        curve.keys = keys;
        return curve;
    }

    private static bool IsTransformLocalPositionBinding(EditorCurveBinding binding, out int axis)
    {
        axis = -1;
        if (binding.type != typeof(Transform)) return false;

        string propertyName = binding.propertyName ?? string.Empty;
        if (string.Equals(propertyName, "m_LocalPosition.x", StringComparison.Ordinal) ||
            string.Equals(propertyName, "localPosition.x", StringComparison.Ordinal))
        {
            axis = 0;
            return true;
        }

        if (string.Equals(propertyName, "m_LocalPosition.y", StringComparison.Ordinal) ||
            string.Equals(propertyName, "localPosition.y", StringComparison.Ordinal))
        {
            axis = 1;
            return true;
        }

        if (string.Equals(propertyName, "m_LocalPosition.z", StringComparison.Ordinal) ||
            string.Equals(propertyName, "localPosition.z", StringComparison.Ordinal))
        {
            axis = 2;
            return true;
        }

        return false;
    }

    private static bool TryGetOffsetForPath(Dictionary<string, Vector3> offsetLookup, string bindingPath, out Vector3 offset)
    {
        offset = default;
        string normalizedBindingPath = bindingPath ?? string.Empty;
        if (offsetLookup.TryGetValue(normalizedBindingPath, out offset))
        {
            return true;
        }

        var matches = offsetLookup
            .Where(pair => IsSameOrHierarchySuffix(pair.Key, normalizedBindingPath) ||
                           IsSameOrHierarchySuffix(normalizedBindingPath, pair.Key))
            .Take(2)
            .ToList();
        if (matches.Count != 1) return false;

        offset = matches[0].Value;
        return true;
    }

    private static bool TryGetSkeletonPosition(Dictionary<string, Vector3> skeleton, string sourcePath, out Vector3 position)
    {
        position = default;
        if (skeleton == null || string.IsNullOrWhiteSpace(sourcePath)) return false;
        if (skeleton.TryGetValue(sourcePath, out position)) return true;

        var matches = skeleton
            .Where(pair => IsSameOrHierarchySuffix(sourcePath, pair.Key) ||
                           IsSameOrHierarchySuffix(pair.Key, sourcePath))
            .Take(2)
            .ToList();
        if (matches.Count != 1) return false;

        position = matches[0].Value;
        return true;
    }

    private static bool IsSameOrHierarchySuffix(string fullPath, string suffix)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(suffix)) return false;
        return string.Equals(fullPath, suffix, StringComparison.Ordinal) ||
               fullPath.EndsWith("/" + suffix, StringComparison.Ordinal);
    }

    private static bool IsAnimationOffsetPatch(ModelFileData file)
    {
        if (file == null) return false;
        if (!string.Equals(file.role, "PATCH", StringComparison.OrdinalIgnoreCase)) return false;

        string transform = string.IsNullOrWhiteSpace(file.transform) ? "XOR_BIN_TO_FBX" : file.transform;
        return string.Equals(transform, "XOR_BIN_TO_FBX", StringComparison.OrdinalIgnoreCase) ||
               NativeMeshPayloadService.IsAdvancedMeshPatchTransform(transform);
    }

    private static ModelFileData ResolveSourceFile(CustomBaseVersion version, ModelFileData patchFile)
    {
        if (version?.sourceFiles == null || patchFile == null) return null;

        if (patchFile.sourceModelFileId.HasValue)
        {
            var byId = version.sourceFiles.FirstOrDefault(file => file != null && file.id == patchFile.sourceModelFileId.Value);
            if (byId != null) return byId;
        }

        string sourcePath = GetMetadataString(patchFile, "sourcePath");
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            sourcePath = GetMetadataString(patchFile, "targetPath");
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string normalizedSourcePath = MCBUtils.ToUnityPath(sourcePath);
            var byPath = version.sourceFiles.FirstOrDefault(file =>
                file != null &&
                string.Equals(MCBUtils.ToUnityPath(file.path), normalizedSourcePath, StringComparison.OrdinalIgnoreCase));
            if (byPath != null) return byPath;
        }

        return version.sourceFiles.Length == 1 ? version.sourceFiles[0] : null;
    }

    private static string GetModelImporterMeta(ModelFileData file, string fallbackUnityPath)
    {
        if (file?.metas != null)
        {
            foreach (var entry in file.metas)
            {
                if (entry == null) continue;
                if (!entry.TryGetValue("meta", out string meta)) continue;
                if (string.IsNullOrWhiteSpace(meta)) continue;
                if (meta.IndexOf("ModelImporter:", StringComparison.Ordinal) < 0) continue;
                if (meta.IndexOf("skeleton:", StringComparison.Ordinal) < 0) continue;
                return meta;
            }
        }

        return TryReadMetaFromAssetPath(fallbackUnityPath);
    }

    private static string TryReadMetaFromAssetPath(string unityAssetPath)
    {
        if (string.IsNullOrWhiteSpace(unityAssetPath)) return null;
        string metaPath = MCBUtils.ToUnityPath(unityAssetPath) + ".meta";
        try
        {
            string fullPath = Path.GetFullPath(metaPath);
            if (!File.Exists(fullPath)) return null;
            string meta = File.ReadAllText(fullPath);
            if (meta.IndexOf("ModelImporter:", StringComparison.Ordinal) < 0) return null;
            if (meta.IndexOf("skeleton:", StringComparison.Ordinal) < 0) return null;
            return meta;
        }
        catch
        {
            return null;
        }
    }

    private static string GetMetadataString(ModelFileData file, string key)
    {
        if (file?.metadata == null || string.IsNullOrWhiteSpace(key)) return null;
        return file.metadata.TryGetValue(key, out object value) ? value?.ToString() : null;
    }

    private static Dictionary<string, Vector3> ParseSkeletonPositions(string meta)
    {
        var nodes = ParseSkeletonNodes(meta);
        var nodesByName = nodes
            .Where(node => node != null && !string.IsNullOrWhiteSpace(node.name))
            .GroupBy(node => node.name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var nodeNameCounts = nodes
            .Where(node => node != null && !string.IsNullOrWhiteSpace(node.name))
            .GroupBy(node => node.name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var pathCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.name)) continue;
            string path = BuildSkeletonPath(node, nodesByName, pathCache, new HashSet<string>(StringComparer.Ordinal));
            if (string.IsNullOrWhiteSpace(path) && nodeNameCounts.TryGetValue(node.name, out int count) && count == 1)
            {
                path = node.name;
            }
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!output.ContainsKey(path))
            {
                output.Add(path, node.position);
            }
        }

        return output;
    }

    private static List<SkeletonNode> ParseSkeletonNodes(string meta)
    {
        var nodes = new List<SkeletonNode>();
        if (string.IsNullOrWhiteSpace(meta)) return nodes;

        var lines = meta.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        bool inSkeleton = false;
        SkeletonNode current = null;

        foreach (string rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (!inSkeleton)
            {
                if (string.Equals(trimmed, "skeleton:", StringComparison.Ordinal))
                {
                    inSkeleton = true;
                }
                continue;
            }

            if (trimmed.StartsWith("armTwist:", StringComparison.Ordinal) ||
                trimmed.StartsWith("lastHumanDescriptionAvatarSource:", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.StartsWith("- name:", StringComparison.Ordinal))
            {
                if (current != null) nodes.Add(current);
                current = new SkeletonNode
                {
                    name = UnquoteYamlScalar(trimmed.Substring("- name:".Length).Trim())
                };
                continue;
            }

            if (current == null) continue;

            if (trimmed.StartsWith("parentName:", StringComparison.Ordinal))
            {
                current.parentName = UnquoteYamlScalar(trimmed.Substring("parentName:".Length).Trim());
                continue;
            }

            if (trimmed.StartsWith("position:", StringComparison.Ordinal) &&
                TryParseVector3(trimmed, out var position))
            {
                current.position = position;
            }
        }

        if (current != null) nodes.Add(current);
        return nodes;
    }

    private static string BuildSkeletonPath(
        SkeletonNode node,
        Dictionary<string, SkeletonNode> nodesByName,
        Dictionary<string, string> pathCache,
        HashSet<string> visiting)
    {
        if (node == null || string.IsNullOrWhiteSpace(node.name)) return string.Empty;
        if (pathCache.TryGetValue(node.name, out string cached)) return cached;
        if (!visiting.Add(node.name)) return node.name;

        string path;
        if (string.IsNullOrWhiteSpace(node.parentName) || !nodesByName.TryGetValue(node.parentName, out var parent))
        {
            path = string.Empty;
        }
        else
        {
            string parentPath = BuildSkeletonPath(parent, nodesByName, pathCache, visiting);
            path = string.IsNullOrWhiteSpace(parentPath) ? node.name : parentPath + "/" + node.name;
        }

        visiting.Remove(node.name);
        pathCache[node.name] = path;
        return path;
    }

    private static bool TryParseVector3(string line, out Vector3 value)
    {
        value = default;
        var match = Regex.Match(line,
            @"position:\s*\{\s*x:\s*([^,\}]+)\s*,\s*y:\s*([^,\}]+)\s*,\s*z:\s*([^,\}]+)\s*\}",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;

        if (!TryParseFloat(match.Groups[1].Value, out float x)) return false;
        if (!TryParseFloat(match.Groups[2].Value, out float y)) return false;
        if (!TryParseFloat(match.Groups[3].Value, out float z)) return false;

        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryParseFloat(string value, out float parsed)
    {
        return float.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static string UnquoteYamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        value = value.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[value.Length - 1] == '"') ||
             (value[0] == '\'' && value[value.Length - 1] == '\'')))
        {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static List<AnimatorController> CollectVrcFuryBuiltControllers(GameObject avatarRoot)
    {
        var found = new HashSet<AnimatorController>();
        var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);

        if (descriptor != null)
        {
            CollectFromLayers(descriptor.baseAnimationLayers, found);
            CollectFromLayers(descriptor.specialAnimationLayers, found);
        }

        foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
        {
            if (animator?.runtimeAnimatorController is AnimatorController controller &&
                IsVrcFuryBuiltController(controller))
            {
                found.Add(controller);
            }
        }

        return found.ToList();
    }

    private static void CollectFromLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers, ISet<AnimatorController> found)
    {
        if (layers == null) return;
        foreach (var layer in layers)
        {
            if (layer.isDefault) continue;
            var controller = layer.animatorController as AnimatorController;
            if (controller == null) continue;
            if (!IsVrcFuryBuiltController(controller)) continue;
            found.Add(controller);
        }
    }

    private static bool IsVrcFuryBuiltController(AnimatorController controller)
    {
        string path = AssetDatabase.GetAssetPath(controller);
        if (string.IsNullOrEmpty(path)) return false;
        string normalized = path.Replace("\\", "/");
        return normalized.IndexOf("com.vrcfury.temp", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static MyCustomBase FindCustomBase(GameObject avatarRoot)
    {
        if (avatarRoot == null) return null;

        var onRoot = avatarRoot.GetComponent<MyCustomBase>();
        if (onRoot != null) return onRoot;

        var inChildren = avatarRoot.GetComponentInChildren<MyCustomBase>(true);
        if (inChildren != null) return inChildren;

        string rootName = avatarRoot.name;
        if (!string.IsNullOrEmpty(rootName) && rootName.EndsWith("(Clone)", StringComparison.Ordinal))
        {
            rootName = rootName.Substring(0, rootName.Length - "(Clone)".Length);
        }

        var all = Resources.FindObjectsOfTypeAll<MyCustomBase>();
        return all.FirstOrDefault(x =>
            x != null &&
            x.gameObject != null &&
            x.gameObject.scene.IsValid() &&
            x.transform.root != null &&
            string.Equals(x.transform.root.name, rootName, StringComparison.Ordinal));
    }

    private static string BuildName(string prefix, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return prefix + "Clip";
        return prefix + sourceName;
    }

    private static void AttachAsSubAsset(AnimatorController controller, UnityEngine.Object obj)
    {
        if (controller == null || obj == null) return;

        string path = AssetDatabase.GetAssetPath(controller);
        if (string.IsNullOrEmpty(path)) return;
        if (AssetDatabase.Contains(obj)) return;

        AssetDatabase.AddObjectToAsset(obj, controller);
    }

    private static ApplyResult Fail(string message)
    {
        return new ApplyResult
        {
            success = false,
            offsetsResolved = 0,
            controllersProcessed = 0,
            clipsDuplicated = 0,
            motionsRewritten = 0,
            message = message
        };
    }
}

[InitializeOnLoad]
public static class AnimationPositionOffsetPlayModeHook
{
    static AnimationPositionOffsetPlayModeHook()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode) return;
        EditorApplication.delayCall += ApplyToPlayModeControllers;
    }

    private static void ApplyToPlayModeControllers()
    {
        if (!EditorApplication.isPlaying) return;

        var processedRoots = new HashSet<GameObject>();
        foreach (var customBase in Resources.FindObjectsOfTypeAll<MyCustomBase>())
        {
            if (customBase == null || customBase.gameObject == null) continue;
            if (!customBase.gameObject.scene.IsValid()) continue;

            var root = customBase.transform.root != null ? customBase.transform.root.gameObject : customBase.gameObject;
            if (root == null || !processedRoots.Add(root)) continue;

            var result = AnimationPositionOffsetService.Instance.ApplyActiveVersionOffsets(root);
            if (result.success)
            {
                MCBLogger.Log("[MCB] Play-mode " + result.message);
            }
        }
    }
}
#endif
