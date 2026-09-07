#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class SmrPathService
{
    private class FbxRendererLookup
    {
        public Transform root;
        public Dictionary<string, List<SkinnedMeshRenderer>> renderersByMeshName =
            new Dictionary<string, List<SkinnedMeshRenderer>>(StringComparer.Ordinal);
    }

    private class SmrPathCacheEntry
    {
        public int revision;
        public Dictionary<string, List<ModelFileSmrPathData>> pathsByFbx;
    }

    private static readonly Dictionary<string, SmrPathCacheEntry> SmrPathCache =
        new Dictionary<string, SmrPathCacheEntry>(StringComparer.Ordinal);

    private static int cacheRevision = 1;
    private static bool invalidationHooked;

    public static Dictionary<string, List<ModelFileSmrPathData>> CollectSmrPathsByFbx(Transform avatarRoot, IEnumerable<string> fbxPaths)
    {
        var result = new Dictionary<string, List<ModelFileSmrPathData>>(StringComparer.OrdinalIgnoreCase);
        if (avatarRoot == null) return result;

        EnsureInvalidationHooked();

        var targetPaths = new HashSet<string>(
            (fbxPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(MCBUtils.ToUnityPath),
            StringComparer.OrdinalIgnoreCase);

        if (targetPaths.Count == 0)
        {
            return result;
        }

        string cacheKey = BuildCacheKey(avatarRoot, targetPaths);
        if (SmrPathCache.TryGetValue(cacheKey, out var cached) && cached.revision == cacheRevision)
        {
            return CloneMap(cached.pathsByFbx);
        }

        foreach (string path in targetPaths)
        {
            result[path] = new List<ModelFileSmrPathData>();
        }

        var fbxLookups = new Dictionary<string, FbxRendererLookup>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in targetPaths)
        {
            fbxLookups[path] = BuildFbxRendererLookup(path);
        }

        foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null) continue;

            string meshAssetPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(smr.sharedMesh));
            if (string.IsNullOrWhiteSpace(meshAssetPath) || !targetPaths.Contains(meshAssetPath)) continue;

            if (!result.TryGetValue(meshAssetPath, out var entries))
            {
                entries = new List<ModelFileSmrPathData>();
                result[meshAssetPath] = entries;
            }

            fbxLookups.TryGetValue(meshAssetPath, out var lookup);
            var fbxRenderer = FindBestFbxRenderer(lookup, smr.sharedMesh.name, smr.transform.name);
            entries.Add(new ModelFileSmrPathData
            {
                avatarPath = GetRelativeTransformPath(avatarRoot, smr.transform),
                fbxMeshPath = fbxRenderer != null ? GetRelativeTransformPath(lookup?.root, fbxRenderer.transform) : "",
                meshName = smr.sharedMesh.name,
                rendererName = smr.transform.name
            });
        }

        foreach (string path in result.Keys.ToList())
        {
            result[path] = result[path]
                .Where(entry => entry != null && entry.avatarPath != null)
                .GroupBy(entry => entry.avatarPath, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(entry => entry.avatarPath, StringComparer.Ordinal)
                .ToList();
        }

        SmrPathCache[cacheKey] = new SmrPathCacheEntry
        {
            revision = cacheRevision,
            pathsByFbx = CloneMap(result)
        };

        return result;
    }

    public static void InvalidateCache()
    {
        cacheRevision++;
        SmrPathCache.Clear();
    }

    public static List<ModelFileSmrPathData> CollectSmrPathsForFbx(Transform avatarRoot, string fbxPath)
    {
        string unityPath = MCBUtils.ToUnityPath(fbxPath);
        var map = CollectSmrPathsByFbx(avatarRoot, new[] { unityPath });
        return map.TryGetValue(unityPath, out var entries) ? entries : new List<ModelFileSmrPathData>();
    }

    // Renderer ownership must survive a mesh swap. A Body with generated dynamic normals
    // or a native payload no longer has an FBX mesh asset path, even though it is still
    // the same renderer from that model. Build from the model hierarchy, then overlay
    // proven live bindings for renamed/moved renderers.
    public static List<ModelFileSmrPathData> CollectModelRendererBindings(
        Transform avatarRoot, string fbxPath, GameObject sourceFbx, GameObject customFbx = null)
    {
        if (avatarRoot == null || sourceFbx == null) return new List<ModelFileSmrPathData>();
        var live = CollectSmrPathsForFbx(avatarRoot, fbxPath);
        if (customFbx != null)
            live.AddRange(CollectSmrPathsForFbx(avatarRoot, AssetDatabase.GetAssetPath(customFbx)));
        var result = new List<ModelFileSmrPathData>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourceFbx.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (source.sharedMesh == null) continue;
            string modelPath = GetRelativeTransformPath(sourceFbx.transform, source.transform);
            var binding = live.FirstOrDefault(entry => entry.fbxMeshPath == modelPath);
            Transform target = binding != null ? FindTransformByRelativePath(avatarRoot, binding.avatarPath) : null;
            if (target == null) target = FindTransformByRelativePath(avatarRoot, modelPath);
            if (target == null) target = FindUniqueTransformByName(avatarRoot, source.name);
            var renderer = target != null ? target.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer == null)
                throw new InvalidOperationException($"Cannot locate target renderer '{modelPath}' from '{fbxPath}'. Resolve its avatar association before building.");
            string avatarPath = GetRelativeTransformPath(avatarRoot, target);
            if (!targets.Add(avatarPath))
                throw new InvalidOperationException($"Multiple model renderers resolve to '{avatarPath}'. Resolve the ambiguous avatar association before building.");
            result.Add(new ModelFileSmrPathData
            {
                avatarPath = avatarPath,
                fbxMeshPath = modelPath,
                meshName = source.sharedMesh.name,
                rendererName = source.name
            });
        }
        return result;
    }

    public static int RefreshTargetMeshesFromFbx(
        Transform avatarRoot,
        string fbxPath,
        IEnumerable<ModelFileSmrPathData> smrPaths,
        bool allowNameFallback = true,
        IEnumerable<string> meshNamesToRefresh = null)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(fbxPath)) return 0;

        string unityFbxPath = MCBUtils.ToUnityPath(fbxPath);
        var fbxRoot = GetFbxRoot(unityFbxPath);
        if (fbxRoot == null) return 0;
        var meshNameFilter = BuildMeshNameFilter(meshNamesToRefresh);

        var entries = (smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>())
            .Where(entry => entry != null && entry.avatarPath != null)
            .ToList();
        if (meshNameFilter.Count > 0)
        {
            entries = entries
                .Where(entry => EntryMatchesMeshFilter(entry, meshNameFilter))
                .ToList();
        }

        if (entries.Count > 0)
        {
            int refreshedCount = 0;
            foreach (var entry in entries)
            {
                var targetTransform = FindTransformByRelativePath(avatarRoot, entry.avatarPath);
                var targetSmr = targetTransform != null ? targetTransform.GetComponent<SkinnedMeshRenderer>() : null;
                if (targetSmr == null) continue;

                Mesh replacementMesh = ResolveFbxMesh(fbxRoot.transform, entry);
                if (replacementMesh == null) continue;

                Undo.RecordObject(targetSmr, "Refresh Mesh from FBX");
                targetSmr.sharedMesh = replacementMesh;
                EditorUtility.SetDirty(targetSmr);
                refreshedCount++;
            }

            return refreshedCount;
        }

        if (!allowNameFallback) return 0;
        return RefreshTargetMeshesByCurrentMeshName(avatarRoot, unityFbxPath, fbxRoot, meshNameFilter);
    }

    public static int RestoreTargetStateFromFbx(
        Transform avatarRoot,
        string fbxPath,
        IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(fbxPath)) return 0;

        var fbxRoot = GetFbxRoot(MCBUtils.ToUnityPath(fbxPath));
        return fbxRoot == null
            ? 0
            : RestoreTargetStateFromFbxRoot(avatarRoot, fbxRoot.transform, smrPaths);
    }

    internal static int RestoreTargetStateFromFbxRoot(
        Transform avatarRoot,
        Transform fbxRoot,
        IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        if (avatarRoot == null || fbxRoot == null) return 0;

        var mappedEntries = (smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>())
            .Where(value => value != null && value.avatarPath != null)
            .ToList();
        var plannedTargetIds = new HashSet<int>();
        var plans = new List<RendererRestorePlan>();
        if (mappedEntries.Count > 0)
        {
            foreach (var entry in mappedEntries)
            {
                var targetTransform = FindTransformByRelativePath(avatarRoot, entry.avatarPath);
                var targetRenderer = targetTransform != null ? targetTransform.GetComponent<SkinnedMeshRenderer>() : null;
                var sourceRenderer = ResolveFbxRenderer(fbxRoot, entry);
                if (targetRenderer == null || sourceRenderer == null)
                {
                    MCBLogger.LogWarning(
                        $"[SmrPathService] Could not safely restore mapped renderer avatarPath='{entry.avatarPath}' fbxPath='{entry.fbxMeshPath}'. No renderer state was changed for this FBX.");
                    return 0;
                }
                if (!plannedTargetIds.Add(targetRenderer.GetInstanceID()))
                {
                    MCBLogger.LogWarning(
                        $"[SmrPathService] Mapped renderer avatarPath='{entry.avatarPath}' resolves to a duplicate target. No renderer state was changed for this FBX.");
                    return 0;
                }

                if (!TryCreateRendererRestorePlan(avatarRoot, fbxRoot, targetRenderer, sourceRenderer, out var plan))
                {
                    return 0;
                }
                plans.Add(plan);
            }
        }
        else
        {
            // Older versions can have no source smrPaths. The avatar was instantiated from the
            // source FBX, so hierarchy paths are the authoritative fallback even when the current
            // mesh is a generated native payload or DynamicNormals asset with a different name.
            foreach (var sourceRenderer in fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (sourceRenderer == null || sourceRenderer.sharedMesh == null) continue;

                string rendererPath = GetRelativeTransformPath(fbxRoot, sourceRenderer.transform);
                var targetTransform = FindTransformByRelativePath(avatarRoot, rendererPath);
                var targetRenderer = targetTransform != null ? targetTransform.GetComponent<SkinnedMeshRenderer>() : null;
                if (targetRenderer == null)
                {
                    targetRenderer = FindUniqueRendererByName(avatarRoot, sourceRenderer.transform.name);
                }

                if (targetRenderer == null)
                {
                    MCBLogger.LogWarning(
                        $"[SmrPathService] Could not find avatar renderer for source path '{rendererPath}'. No renderer state was changed for this FBX.");
                    return 0;
                }
                if (!plannedTargetIds.Add(targetRenderer.GetInstanceID()))
                {
                    MCBLogger.LogWarning(
                        $"[SmrPathService] Source renderer path '{rendererPath}' resolves to a duplicate avatar target. No renderer state was changed for this FBX.");
                    return 0;
                }
                if (!TryCreateRendererRestorePlan(avatarRoot, fbxRoot, targetRenderer, sourceRenderer, out var plan))
                {
                    return 0;
                }
                plans.Add(plan);
            }
        }

        foreach (var plan in plans)
        {
            ApplyRendererRestorePlan(plan);
        }
        return plans.Count;
    }

    public static int RestoreTargetTransformHierarchyFromFbx(Transform avatarRoot, string fbxPath)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(fbxPath)) return 0;

        var fbxRoot = GetFbxRoot(MCBUtils.ToUnityPath(fbxPath));
        return fbxRoot == null
            ? 0
            : RestoreTargetTransformHierarchyFromFbxRoot(avatarRoot, fbxRoot.transform);
    }

    internal static int RestoreTargetTransformHierarchyFromFbxRoot(Transform avatarRoot, Transform fbxRoot)
    {
        if (avatarRoot == null || fbxRoot == null) return 0;

        var plans = new List<TransformRestorePlan>();
        var plannedTargetIds = new HashSet<int>();
        foreach (var sourceTransform in fbxRoot.GetComponentsInChildren<Transform>(true))
        {
            if (sourceTransform == null || sourceTransform == fbxRoot) continue;

            string path = GetRelativeTransformPath(fbxRoot, sourceTransform);
            var target = FindTransformByRelativePath(avatarRoot, path);
            if (target == null)
            {
                target = FindUniqueTransformByName(avatarRoot, sourceTransform.name);
            }
            if (target == null || !plannedTargetIds.Add(target.GetInstanceID()))
            {
                MCBLogger.LogWarning(
                    $"[SmrPathService] Could not safely restore canonical FBX transform '{path}'. No transform pose was changed.");
                return 0;
            }

            plans.Add(new TransformRestorePlan
            {
                target = target,
                source = sourceTransform
            });
        }

        foreach (var plan in plans)
        {
            Undo.RecordObject(plan.target, "Restore FBX Transform State");
            plan.target.localPosition = plan.source.localPosition;
            plan.target.localRotation = plan.source.localRotation;
            plan.target.localScale = plan.source.localScale;
            EditorUtility.SetDirty(plan.target);
        }

        return plans.Count;
    }

    public static List<ModelFileSmrPathData> ResolveSmrPathsForSource(
        CustomBaseVersion version,
        string sourceFbxPath,
        MyCustomBase target = null)
    {
        if (version?.sourceFiles == null || string.IsNullOrWhiteSpace(sourceFbxPath))
        {
            return new List<ModelFileSmrPathData>();
        }

        string normalized = MCBUtils.ToUnityPath(sourceFbxPath);
        var source = version.sourceFiles.FirstOrDefault(file =>
            file != null &&
            (string.Equals(MCBUtils.ToUnityPath(file.path), normalized, StringComparison.OrdinalIgnoreCase) ||
             (target != null && string.Equals(
                 AvatarPathOverrideService.ResolveLocalTargetPath(target, file, updateStoredPathFromGuid: false),
                 normalized,
                 StringComparison.OrdinalIgnoreCase))));

        return source?.smrPaths ?? new List<ModelFileSmrPathData>();
    }

    public static string GetRelativeTransformPath(Transform root, Transform target)
    {
        if (root == null || target == null) return string.Empty;
        if (target == root) return string.Empty;

        var segments = new List<string>();
        var current = target;
        while (current != null && current != root)
        {
            segments.Add(current.name);
            current = current.parent;
        }

        if (current != root) return string.Empty;
        segments.Reverse();
        return string.Join("/", segments);
    }

    private static Transform FindTransformByRelativePath(Transform root, string relativePath)
    {
        if (root == null) return null;
        if (string.IsNullOrWhiteSpace(relativePath)) return root;

        Transform current = root;
        foreach (string rawSegment in relativePath.Split('/'))
        {
            string segment = rawSegment.Trim();
            if (string.IsNullOrEmpty(segment)) continue;
            current = current.Find(segment);
            if (current == null) return null;
        }

        return current;
    }

    private static GameObject GetFbxRoot(string fbxPath)
    {
        return string.IsNullOrWhiteSpace(fbxPath)
            ? null
            : AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(fbxPath));
    }

    private static SkinnedMeshRenderer FindBestFbxRenderer(FbxRendererLookup lookup, string meshName, string rendererName)
    {
        if (lookup == null || lookup.root == null || string.IsNullOrWhiteSpace(meshName)) return null;
        if (!lookup.renderersByMeshName.TryGetValue(meshName, out var candidates) || candidates == null || candidates.Count == 0) return null;

        return candidates.FirstOrDefault(smr => string.Equals(smr.transform.name, rendererName, StringComparison.Ordinal))
               ?? candidates.FirstOrDefault();
    }

    private static FbxRendererLookup BuildFbxRendererLookup(string fbxPath)
    {
        var lookup = new FbxRendererLookup();
        var fbxRoot = GetFbxRoot(fbxPath);
        if (fbxRoot == null)
        {
            return lookup;
        }

        lookup.root = fbxRoot.transform;
        foreach (var smr in fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null || string.IsNullOrWhiteSpace(smr.sharedMesh.name)) continue;
            if (!lookup.renderersByMeshName.TryGetValue(smr.sharedMesh.name, out var renderers))
            {
                renderers = new List<SkinnedMeshRenderer>();
                lookup.renderersByMeshName[smr.sharedMesh.name] = renderers;
            }

            renderers.Add(smr);
        }

        return lookup;
    }

    private static void EnsureInvalidationHooked()
    {
        if (invalidationHooked) return;
        invalidationHooked = true;
        EditorApplication.hierarchyChanged += InvalidateCache;
        EditorApplication.projectChanged += InvalidateCache;
    }

    private static string BuildCacheKey(Transform avatarRoot, IEnumerable<string> targetPaths)
    {
        return avatarRoot.GetInstanceID() + "|" + string.Join("|",
            (targetPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(MCBUtils.ToUnityPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    private static Dictionary<string, List<ModelFileSmrPathData>> CloneMap(Dictionary<string, List<ModelFileSmrPathData>> source)
    {
        var clone = new Dictionary<string, List<ModelFileSmrPathData>>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return clone;
        }

        foreach (var pair in source)
        {
            clone[pair.Key] = pair.Value != null
                ? pair.Value
                    .Where(entry => entry != null)
                    .Select(entry => new ModelFileSmrPathData
                    {
                        avatarPath = entry.avatarPath,
                        fbxMeshPath = entry.fbxMeshPath,
                        meshName = entry.meshName,
                        rendererName = entry.rendererName
                    })
                    .ToList()
                : new List<ModelFileSmrPathData>();
        }

        return clone;
    }

    private static HashSet<string> BuildMeshNameFilter(IEnumerable<string> meshNames)
    {
        return new HashSet<string>(
            (meshNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .SelectMany(MeshNameVariants),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> MeshNameVariants(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        string trimmed = value.Trim();
        yield return trimmed;

        if (trimmed.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
        {
            yield return System.IO.Path.GetFileNameWithoutExtension(trimmed);
        }

        string fileName = System.IO.Path.GetFileName(trimmed.Replace('\\', '/'));
        if (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(fileName, trimmed, StringComparison.Ordinal))
        {
            yield return fileName;
            if (fileName.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                yield return System.IO.Path.GetFileNameWithoutExtension(fileName);
            }
        }
    }

    private static bool EntryMatchesMeshFilter(ModelFileSmrPathData entry, HashSet<string> meshNameFilter)
    {
        if (entry == null || meshNameFilter == null || meshNameFilter.Count == 0)
        {
            return true;
        }

        foreach (string value in MeshNameVariants(entry.meshName))
        {
            if (meshNameFilter.Contains(value)) return true;
        }

        foreach (string value in MeshNameVariants(entry.rendererName))
        {
            if (meshNameFilter.Contains(value)) return true;
        }

        foreach (string value in MeshNameVariants(entry.fbxMeshPath))
        {
            if (meshNameFilter.Contains(value)) return true;
        }

        return false;
    }

    private static Mesh ResolveFbxMesh(Transform fbxRoot, ModelFileSmrPathData entry)
    {
        if (fbxRoot == null || entry == null) return null;

        var renderer = ResolveFbxRenderer(fbxRoot, entry);
        if (renderer != null && renderer.sharedMesh != null)
        {
            return renderer.sharedMesh;
        }

        if (!string.IsNullOrWhiteSpace(entry.fbxMeshPath))
        {
            var transform = FindTransformByRelativePath(fbxRoot, entry.fbxMeshPath);
            var mesh = ResolveMeshFromTransform(transform);
            if (mesh != null) return mesh;
        }

        if (!string.IsNullOrWhiteSpace(entry.meshName))
        {
            foreach (var smr in fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (string.Equals(smr.sharedMesh.name, entry.meshName, StringComparison.Ordinal))
                {
                    return smr.sharedMesh;
                }
            }

            foreach (var meshFilter in fbxRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                if (string.Equals(meshFilter.sharedMesh.name, entry.meshName, StringComparison.Ordinal))
                {
                    return meshFilter.sharedMesh;
                }
            }
        }

        return null;
    }

    private static SkinnedMeshRenderer ResolveFbxRenderer(Transform fbxRoot, ModelFileSmrPathData entry)
    {
        if (fbxRoot == null || entry == null) return null;

        if (!string.IsNullOrWhiteSpace(entry.fbxMeshPath))
        {
            var transform = FindTransformByRelativePath(fbxRoot, entry.fbxMeshPath);
            var renderer = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (renderer != null)
            {
                return renderer;
            }
        }

        var renderers = fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (!string.IsNullOrWhiteSpace(entry.meshName))
        {
            var candidates = renderers
                .Where(value => value != null &&
                                value.sharedMesh != null &&
                                string.Equals(value.sharedMesh.name, entry.meshName, StringComparison.Ordinal))
                .ToList();
            var named = candidates.FirstOrDefault(value =>
                string.Equals(value.transform.name, entry.rendererName, StringComparison.Ordinal));
            if (named != null || candidates.Count == 1)
            {
                return named ?? candidates[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.rendererName))
        {
            var candidates = renderers
                .Where(value => value != null &&
                                string.Equals(value.transform.name, entry.rendererName, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        return null;
    }

    private static SkinnedMeshRenderer FindUniqueRendererByName(Transform avatarRoot, string rendererName)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(rendererName)) return null;

        var matches = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(value => value != null &&
                            string.Equals(value.transform.name, rendererName, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool TryCreateRendererRestorePlan(
        Transform avatarRoot,
        Transform fbxRoot,
        SkinnedMeshRenderer target,
        SkinnedMeshRenderer source,
        out RendererRestorePlan plan)
    {
        plan = null;
        if (avatarRoot == null || fbxRoot == null || target == null || source == null || source.sharedMesh == null)
        {
            return false;
        }

        var resolvedBones = new Transform[source.bones?.Length ?? 0];
        for (int i = 0; i < resolvedBones.Length; i++)
        {
            Transform sourceBone = source.bones[i];
            if (sourceBone == null)
            {
                resolvedBones[i] = null;
                continue;
            }

            string path = GetRelativeTransformPath(fbxRoot, sourceBone);
            resolvedBones[i] = sourceBone == fbxRoot
                ? avatarRoot
                : FindTransformByRelativePath(avatarRoot, path);
            if (resolvedBones[i] == null)
            {
                resolvedBones[i] = FindUniqueTransformByName(avatarRoot, sourceBone.name);
            }
            if (resolvedBones[i] == null)
            {
                MCBLogger.LogWarning($"[SmrPathService] Could not safely restore renderer '{target.name}': source bone '{path}' was not found on the avatar.");
                return false;
            }
        }

        if (source.sharedMesh.bindposes != null &&
            source.sharedMesh.bindposes.Length > 0 &&
            source.sharedMesh.bindposes.Length != resolvedBones.Length)
        {
            MCBLogger.LogWarning(
                $"[SmrPathService] Could not safely restore renderer '{target.name}': source mesh has {source.sharedMesh.bindposes.Length} bindposes but the renderer has {resolvedBones.Length} bones.");
            return false;
        }

        Transform resolvedRootBone = null;
        if (source.rootBone != null)
        {
            string rootBonePath = GetRelativeTransformPath(fbxRoot, source.rootBone);
            resolvedRootBone = source.rootBone == fbxRoot
                ? avatarRoot
                : FindTransformByRelativePath(avatarRoot, rootBonePath);
            if (resolvedRootBone == null)
            {
                resolvedRootBone = FindUniqueTransformByName(avatarRoot, source.rootBone.name);
            }
            if (resolvedRootBone == null)
            {
                MCBLogger.LogWarning($"[SmrPathService] Could not safely restore renderer '{target.name}': source root bone '{rootBonePath}' was not found on the avatar.");
                return false;
            }
        }

        plan = new RendererRestorePlan
        {
            target = target,
            source = source,
            resolvedBones = resolvedBones,
            resolvedRootBone = resolvedRootBone
        };
        return true;
    }

    private static void ApplyRendererRestorePlan(RendererRestorePlan plan)
    {
        if (plan?.target == null || plan.source == null) return;

        Undo.RecordObject(plan.target.transform, "Restore FBX Renderer Transform");
        plan.target.transform.localPosition = plan.source.transform.localPosition;
        plan.target.transform.localRotation = plan.source.transform.localRotation;
        plan.target.transform.localScale = plan.source.transform.localScale;
        EditorUtility.SetDirty(plan.target.transform);

        Undo.RecordObject(plan.target, "Restore FBX Renderer State");
        plan.target.sharedMesh = plan.source.sharedMesh;
        plan.target.bones = plan.resolvedBones;
        plan.target.rootBone = plan.resolvedRootBone;
        // Bounds validation requires the restored bone palette to match this mesh.
        plan.target.localBounds = plan.source.localBounds;
        EditorUtility.SetDirty(plan.target);
    }

    private sealed class RendererRestorePlan
    {
        public SkinnedMeshRenderer target;
        public SkinnedMeshRenderer source;
        public Transform[] resolvedBones;
        public Transform resolvedRootBone;
    }

    private sealed class TransformRestorePlan
    {
        public Transform target;
        public Transform source;
    }

    private static Transform FindUniqueTransformByName(Transform avatarRoot, string transformName)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(transformName)) return null;

        var matches = avatarRoot.GetComponentsInChildren<Transform>(true)
            .Where(value => value != null && string.Equals(value.name, transformName, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static Mesh ResolveMeshFromTransform(Transform transform)
    {
        if (transform == null) return null;

        var smr = transform.GetComponent<SkinnedMeshRenderer>();
        if (smr != null && smr.sharedMesh != null)
        {
            return smr.sharedMesh;
        }

        foreach (var childSmr in transform.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (childSmr != null && childSmr.sharedMesh != null)
            {
                return childSmr.sharedMesh;
            }
        }

        var meshFilter = transform.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            return meshFilter.sharedMesh;
        }

        foreach (var childMeshFilter in transform.GetComponentsInChildren<MeshFilter>(true))
        {
            if (childMeshFilter != null && childMeshFilter.sharedMesh != null)
            {
                return childMeshFilter.sharedMesh;
            }
        }

        return null;
    }

    private static int RefreshTargetMeshesByCurrentMeshName(Transform avatarRoot, string fbxPath, GameObject fbxRoot, HashSet<string> meshNameFilter)
    {
        var meshLookup = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (var smr in fbxRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null || meshLookup.ContainsKey(smr.sharedMesh.name)) continue;
            meshLookup.Add(smr.sharedMesh.name, smr.sharedMesh);
        }

        foreach (var meshFilter in fbxRoot.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter == null || meshFilter.sharedMesh == null || meshLookup.ContainsKey(meshFilter.sharedMesh.name)) continue;
            meshLookup.Add(meshFilter.sharedMesh.name, meshFilter.sharedMesh);
        }

        int refreshedCount = 0;
        foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null) continue;
            if (meshNameFilter != null && meshNameFilter.Count > 0)
            {
                bool matchesFilter = meshNameFilter.Contains(smr.sharedMesh.name) || meshNameFilter.Contains(smr.transform.name);
                if (!matchesFilter) continue;
            }

            string currentAssetPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(smr.sharedMesh));
            if (!string.Equals(currentAssetPath, fbxPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!meshLookup.TryGetValue(smr.sharedMesh.name, out var replacementMesh)) continue;

            Undo.RecordObject(smr, "Refresh Mesh from FBX");
            smr.sharedMesh = replacementMesh;
            EditorUtility.SetDirty(smr);
            refreshedCount++;
        }

        return refreshedCount;
    }
}
#endif
