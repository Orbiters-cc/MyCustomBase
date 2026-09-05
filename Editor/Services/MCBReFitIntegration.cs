#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bridges MCB with the optional ReFit package (orbiters.refit): re-fits the avatar's clothing/accessory
/// meshes from the default base body (mesh A) to the applied custom version body (mesh B), transferring the
/// version's exposed blendshapes. Tracks the original meshes on the <see cref="MyCustomBase"/> component so a
/// reset to the default base restores them (this part works even without ReFit installed), and commits the
/// generated files through Unit Git when available.
/// </summary>
public static class MCBReFitIntegration
{
    /// <summary>Raised after the persisted ReFit state of one renderer changes.</summary>
    public static event Action<MyCustomBase, string> RefitStateChanged;

    private const string XRayGizmosObjectNamePrefix = "__XRayGizmos_";
    private const string XRayGizmosMeshNamePrefix = "XRayArmatureMesh";
    private const string XRayGizmosMeshEdgesSuffix = "_XRayMeshEdges";
    private const string XRayGizmosWeightPaintSuffix = "_XRayWeightPaint";

    private sealed class ReFitSourceReference
    {
        public GameObject sourceAvatar;
        public SkinnedMeshRenderer sourceBody;
        public SkinnedMeshRenderer targetBody;
    }

    private sealed class SourceBodyReference
    {
        public GameObject sourceAvatar;
        public SkinnedMeshRenderer renderer;
        public int score;
    }

    public static bool IsReFitAvailable
    {
        get { return ReFitApi.IsAvailable; }
    }

    public static string ReFitAvailabilityMessage
    {
        get { return ReFitApi.AvailabilityMessage; }
    }

    // ------------------------------------------------------------------
    // Candidates
    // ------------------------------------------------------------------

    /// <summary>
    /// All skinned meshes on the avatar that are NOT part of the avatar's base body — i.e. the
    /// clothing/accessories that ReFit can adapt. A renderer is considered part of the body when a mesh of the
    /// same name exists in one of the avatar's base FBX files (this matches by name, so it still works after a
    /// version swapped Body/Tail/Hair for native-mesh assets).
    /// </summary>
    public static List<SkinnedMeshRenderer> GetRefitCandidates(MCBEditor editor)
    {
        var result = new List<SkinnedMeshRenderer>();
        var target = editor != null ? editor.customBaseTarget : null;
        if (target == null) return result;
        var root = target.transform.root;
        var baseMeshes = BuildBaseMeshMap(target);

        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null || smr.sharedMesh == null) continue;
            if (IsGeneratedEditorOnlyRenderer(smr)) continue;
            if (IsBodyRenderer(smr, baseMeshes)) continue; // part of the base body (Body, Tail, Hair, ...)
            result.Add(smr);
        }
        return result;
    }

    internal static bool IsGeneratedEditorOnlyRenderer(SkinnedMeshRenderer renderer)
    {
        if (renderer == null) return false;
        if (HasEditorGeneratedHideFlags(renderer.hideFlags)) return true;

        var go = renderer.gameObject;
        if (go != null)
        {
            if (HasEditorGeneratedHideFlags(go.hideFlags)) return true;
            if (IsXRayGizmosObject(go.transform)) return true;
        }

        var mesh = renderer.sharedMesh;
        return mesh != null && IsXRayGizmosMeshName(mesh.name);
    }

    private static bool HasEditorGeneratedHideFlags(HideFlags flags)
    {
        return flags == HideFlags.HideAndDontSave || flags == HideFlags.DontSave;
    }

    private static bool IsXRayGizmosObject(Transform transform)
    {
        while (transform != null)
        {
            if (!string.IsNullOrEmpty(transform.name) &&
                transform.name.StartsWith(XRayGizmosObjectNamePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            transform = transform.parent;
        }

        return false;
    }

    private static bool IsXRayGizmosMeshName(string name)
    {
        return !string.IsNullOrEmpty(name) &&
               (name.StartsWith(XRayGizmosMeshNamePrefix, StringComparison.Ordinal) ||
                name.EndsWith(XRayGizmosMeshEdgesSuffix, StringComparison.Ordinal) ||
                name.EndsWith(XRayGizmosWeightPaintSuffix, StringComparison.Ordinal));
    }

    /// <summary>Name -> Mesh of every skinned mesh found in the avatar's base FBX files.</summary>
    private static Dictionary<string, Mesh> BuildBaseMeshMap(MyCustomBase target)
    {
        var baseMeshes = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
        if (target == null) return baseMeshes;
        foreach (var fbx in target.baseFbxFiles)
        {
            if (fbx == null) continue;
            string path = AssetDatabase.GetAssetPath(fbx);
            if (string.IsNullOrEmpty(path)) continue;
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                if (obj is Mesh m && !baseMeshes.ContainsKey(m.name)) baseMeshes[m.name] = m;
        }
        return baseMeshes;
    }

    /// <summary>A renderer is part of the base body when its mesh maps to a base FBX mesh by name.</summary>
    private static bool IsBodyRenderer(SkinnedMeshRenderer smr, Dictionary<string, Mesh> baseMeshes)
    {
        return smr != null && smr.sharedMesh != null &&
               ResolveBaseMesh(baseMeshes, smr.sharedMesh.name, smr.transform.name) != null;
    }

    /// <summary>Finds the base FBX mesh corresponding to a (possibly version-swapped) renderer, by name.</summary>
    private static Mesh ResolveBaseMesh(Dictionary<string, Mesh> baseMeshes, string currentMeshName, string rendererName)
    {
        if (baseMeshes == null) return null;
        string clean = CleanMeshName(currentMeshName);
        if (baseMeshes.TryGetValue(clean, out var m)) return m;
        int dot = clean.IndexOf('.');
        if (dot > 0 && baseMeshes.TryGetValue(clean.Substring(0, dot), out m)) return m;
        if (!string.IsNullOrEmpty(rendererName) && baseMeshes.TryGetValue(rendererName, out m)) return m;
        return null;
    }

    private static string CleanMeshName(string name)
    {
        return (name ?? string.Empty).Replace("(Clone)", "").Replace("_ReFit", "").Trim();
    }

    /// <summary>Hierarchy path of a renderer relative to the avatar root (Transform.Find compatible).</summary>
    public static string GetRendererPath(Transform root, Transform t)
    {
        return SmrPathService.GetRelativeTransformPath(root, t);
    }

    public static RefitAppliedMeshEntry FindEntry(MyCustomBase target, string rendererPath)
    {
        if (target == null || target.appliedRefits == null) return null;
        return target.appliedRefits.FirstOrDefault(e => e != null && e.rendererPath == rendererPath);
    }

    /// <summary>True when the renderer currently uses a mesh generated by ReFit.</summary>
    public static bool IsRefitApplied(MyCustomBase target, SkinnedMeshRenderer smr)
    {
        if (target == null || smr == null) return false;
        var entry = FindEntry(target, GetRendererPath(target.transform.root, smr.transform));
        return entry != null && entry.refitMesh != null && smr.sharedMesh == entry.refitMesh;
    }

    /// <summary>
    /// Registers a successful standalone ReFit operation so MCB can synchronize its transferred blendshapes
    /// and restore the original renderer state from the ReFit frame.
    /// </summary>
    public static bool RegisterStandaloneRefit(
        GameObject targetAvatar,
        SkinnedMeshRenderer renderer,
        object originalRendererState,
        Mesh refitMesh,
        string refitMeshAssetPath,
        string[] sourceShapeNames,
        string[] generatedShapeNames)
    {
        if (renderer == null || refitMesh == null || originalRendererState == null) return false;

        var mcb = renderer.GetComponentInParent<MyCustomBase>(true);
        if (mcb == null && targetAvatar != null)
            mcb = targetAvatar.GetComponentInChildren<MyCustomBase>(true);
        if (mcb == null) return false;

        var root = mcb.transform.root;
        if (!renderer.transform.IsChildOf(root)) return false;

        string rendererPath = GetRendererPath(root, renderer.transform);
        var entry = FindEntry(mcb, rendererPath);
        Undo.RecordObject(mcb, "Register standalone ReFit");
        if (entry == null)
        {
            entry = CaptureStandaloneRendererState(root, rendererPath, originalRendererState);
            if (entry == null) return false;
            if (mcb.appliedRefits == null) mcb.appliedRefits = new List<RefitAppliedMeshEntry>();
            mcb.appliedRefits.Add(entry);
        }

        entry.refitMesh = refitMesh;
        entry.refitMeshAssetPath = refitMeshAssetPath;
        UpdateTransferredBlendShapeMap(entry, sourceShapeNames, generatedShapeNames);
        SyncTransferredBlendShapeWeightsFromAvatar(entry, mcb, renderer);
        EditorUtility.SetDirty(mcb);
        RefitStateChanged?.Invoke(mcb, rendererPath);
        return true;
    }

    private static RefitAppliedMeshEntry CaptureStandaloneRendererState(
        Transform root, string rendererPath, object snapshot)
    {
        var entry = new RefitAppliedMeshEntry
        {
            rendererPath = rendererPath,
            originalMesh = ReFitApi.GetMemberValue(snapshot, "mesh") as Mesh,
            originalRootBoneCaptured = true,
            originalRootBone = ReFitApi.GetMemberValue(snapshot, "rootBone") as Transform,
            originalRootBonePath = ConvertSnapshotPath(root, snapshot,
                ReFitApi.GetMemberValue(snapshot, "rootBonePath") as string,
                ReFitApi.GetMemberValue(snapshot, "rootBone") as Transform),
            originalUpdateWhenOffscreenCaptured = true,
            originalUpdateWhenOffscreen = GetReflectedValue(snapshot, "updateWhenOffscreen", false),
            originalLocalBoundsCaptured = true,
            originalLocalBounds = GetReflectedValue(snapshot, "localBounds", default(Bounds))
        };

        var bones = ReFitApi.GetMemberValue(snapshot, "bones") as Transform[];
        var bonePaths = ReFitApi.GetMemberValue(snapshot, "bonePaths") as string[];
        int boneCount = Math.Max(bones?.Length ?? 0, bonePaths?.Length ?? 0);
        for (int i = 0; i < boneCount; i++)
        {
            var bone = bones != null && i < bones.Length ? bones[i] : null;
            string path = bonePaths != null && i < bonePaths.Length ? bonePaths[i] : null;
            entry.originalBones.Add(bone);
            entry.originalBonePaths.Add(ConvertSnapshotPath(root, snapshot, path, bone));
        }

        var shapeNames = ReFitApi.GetMemberValue(snapshot, "blendShapeNames") as string[];
        var shapeWeights = ReFitApi.GetMemberValue(snapshot, "blendShapeWeights") as float[];
        if (shapeNames != null) entry.originalBlendShapeNames.AddRange(shapeNames);
        if (shapeWeights != null) entry.originalBlendShapeWeights.AddRange(shapeWeights);

        if (ReFitApi.GetMemberValue(snapshot, "transformStates") is IEnumerable transformStates)
        {
            foreach (var state in transformStates)
            {
                if (state == null) continue;
                var transform = ReFitApi.GetMemberValue(state, "transform") as Transform;
                string snapshotPath = ReFitApi.GetMemberValue(state, "path") as string;
                entry.originalTransformStates.Add(new RefitTransformState
                {
                    transform = transform,
                    path = ConvertSnapshotPath(root, snapshot, snapshotPath, transform),
                    localPosition = GetReflectedValue(state, "localPosition", Vector3.zero),
                    localRotation = GetReflectedValue(state, "localRotation", Quaternion.identity),
                    localScale = GetReflectedValue(state, "localScale", Vector3.one)
                });
            }
        }

        return entry;
    }

    private static T GetReflectedValue<T>(object source, string memberName, T fallback)
    {
        object value = ReFitApi.GetMemberValue(source, memberName);
        return value is T typed ? typed : fallback;
    }

    private static string ConvertSnapshotPath(Transform root, object snapshot, string snapshotPath, Transform transform)
    {
        if (TryGetPathUnderRoot(root, transform, out string actualPath)) return actualPath;

        var snapshotRoot = ReFitApi.GetMemberValue(snapshot, "snapshotRoot") as Transform;
        if (snapshotRoot == root) return snapshotPath;
        if (snapshotRoot != null && snapshotRoot.IsChildOf(root) &&
            TryGetPathUnderRoot(root, snapshotRoot, out string prefix))
        {
            return string.IsNullOrEmpty(snapshotPath) ? prefix : prefix + "/" + snapshotPath;
        }

        return snapshotPath;
    }

    // ------------------------------------------------------------------
    // Reset restore (works without ReFit installed)
    // ------------------------------------------------------------------

    /// <summary>
    /// Restores every ReFit-modified asset renderer back to its original mesh, blendshape weights and armature
    /// transform state, then clears the tracking list.
    /// Called when resetting the avatar to the default original base.
    /// </summary>
    public static void RestoreOriginalAssetMeshes(MyCustomBase target)
    {
        if (target == null || target.appliedRefits == null || target.appliedRefits.Count == 0) return;
        var root = target.transform.root;
        int restored = 0;
        foreach (var entry in target.appliedRefits)
        {
            if (entry == null || string.IsNullOrEmpty(entry.rendererPath)) continue;
            var t = root.Find(entry.rendererPath);
            var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
            if (smr == null) continue;
            if (RestoreRendererState(root, smr, entry, "MCB ReFit restore")) restored++;
        }
        Undo.RecordObject(target, "MCB ReFit restore");
        target.appliedRefits.Clear();
        EditorUtility.SetDirty(target);
        MCBLogger.Log($"[MCB] ReFit: restored {restored} asset mesh(es) to their original version.");
    }

    private static RefitAppliedMeshEntry CaptureRendererState(Transform root, string rendererPath, SkinnedMeshRenderer smr)
    {
        var entry = new RefitAppliedMeshEntry
        {
            rendererPath = rendererPath,
            originalMesh = smr.sharedMesh,
            originalRootBoneCaptured = true,
            originalUpdateWhenOffscreenCaptured = true,
            originalUpdateWhenOffscreen = smr.updateWhenOffscreen,
            originalLocalBoundsCaptured = true,
            originalLocalBounds = smr.localBounds
        };

        var mesh = smr.sharedMesh;
        if (mesh != null)
        {
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                entry.originalBlendShapeNames.Add(mesh.GetBlendShapeName(i));
                entry.originalBlendShapeWeights.Add(smr.GetBlendShapeWeight(i));
            }
        }

        entry.originalRootBone = smr.rootBone;
        if (TryGetPathUnderRoot(root, smr.rootBone, out string rootBonePath))
        {
            entry.originalRootBonePath = rootBonePath;
        }

        if (smr.bones != null)
        {
            foreach (var bone in smr.bones)
            {
                entry.originalBones.Add(bone);
                entry.originalBonePaths.Add(TryGetPathUnderRoot(root, bone, out string bonePath) ? bonePath : null);
            }
        }

        var capturedTransforms = new HashSet<string>(StringComparer.Ordinal);
        CaptureTransformState(root, smr.transform, entry.originalTransformStates, capturedTransforms);
        CaptureTransformState(root, smr.rootBone, entry.originalTransformStates, capturedTransforms);
        if (smr.bones != null)
        {
            foreach (var bone in smr.bones)
            {
                CaptureTransformState(root, bone, entry.originalTransformStates, capturedTransforms);
            }
        }

        return entry;
    }

    private static void CaptureTransformState(Transform root, Transform transform, List<RefitTransformState> states, HashSet<string> capturedPaths)
    {
        if (transform == null) return;
        TryGetPathUnderRoot(root, transform, out string path);
        string key = path ?? "instance:" + transform.GetInstanceID();
        if (!capturedPaths.Add(key)) return;

        states.Add(new RefitTransformState
        {
            transform = transform,
            path = path,
            localPosition = transform.localPosition,
            localRotation = transform.localRotation,
            localScale = transform.localScale
        });
    }

    private static bool RestoreRendererState(Transform root, SkinnedMeshRenderer smr, RefitAppliedMeshEntry entry, string undoName)
    {
        if (root == null || smr == null || entry == null) return false;
        bool restored = false;
        var transformStatesByPath = BuildTransformStateMap(entry.originalTransformStates);

        if (entry.originalTransformStates != null)
        {
            foreach (var state in entry.originalTransformStates)
            {
                if (state == null) continue;
                var transform = state.transform != null
                    ? state.transform
                    : ResolveOrCreateStoredTransform(root, state.path, transformStatesByPath, undoName);
                if (transform == null) continue;

                Undo.RecordObject(transform, undoName);
                transform.localPosition = state.localPosition;
                transform.localRotation = state.localRotation;
                transform.localScale = state.localScale;
                EditorUtility.SetDirty(transform);
                restored = true;
            }
        }

        Undo.RecordObject(smr, undoName);
        if (entry.originalMesh != null)
        {
            smr.sharedMesh = entry.originalMesh;
            restored = true;
        }

        if (entry.originalBonePaths != null && entry.originalBonePaths.Count > 0)
        {
            var bones = new Transform[entry.originalBonePaths.Count];
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = ResolveStoredBone(root, entry, i, transformStatesByPath, undoName);
            }
            smr.bones = bones;
            restored = true;
        }

        if (entry.originalRootBoneCaptured)
        {
            smr.rootBone = entry.originalRootBone != null
                ? entry.originalRootBone
                : ResolveOrCreateStoredTransform(root, entry.originalRootBonePath, transformStatesByPath, undoName);
            restored = true;
        }

        if (entry.originalUpdateWhenOffscreenCaptured)
        {
            smr.updateWhenOffscreen = entry.originalUpdateWhenOffscreen;
            restored = true;
        }

        if (entry.originalLocalBoundsCaptured)
        {
            smr.localBounds = entry.originalLocalBounds;
            restored = true;
        }

        if (RestoreBlendShapeWeights(smr, entry))
        {
            restored = true;
        }

        if (RemoveReFitGeneratedMetadata(smr, undoName))
        {
            restored = true;
        }

        EditorUtility.SetDirty(smr);
        return restored;
    }

    private static bool RestoreBlendShapeWeights(SkinnedMeshRenderer smr, RefitAppliedMeshEntry entry)
    {
        var mesh = smr.sharedMesh;
        if (mesh == null ||
            entry.originalBlendShapeNames == null ||
            entry.originalBlendShapeWeights == null ||
            entry.originalBlendShapeNames.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            smr.SetBlendShapeWeight(i, 0f);
        }

        int count = Math.Min(entry.originalBlendShapeNames.Count, entry.originalBlendShapeWeights.Count);
        for (int i = 0; i < count; i++)
        {
            string shapeName = entry.originalBlendShapeNames[i];
            if (string.IsNullOrEmpty(shapeName)) continue;
            int index = mesh.GetBlendShapeIndex(shapeName);
            if (index >= 0) smr.SetBlendShapeWeight(index, entry.originalBlendShapeWeights[i]);
        }

        return true;
    }

    public static List<string> GetBlendShapeNamesWithTransferredReFit(MyCustomBase target, string sourceBlendShapeName)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(sourceBlendShapeName)) return names;

        names.Add(sourceBlendShapeName);
        if (target?.appliedRefits != null)
        {
            foreach (var entry in target.appliedRefits)
            {
                if (entry?.transferredBlendShapeSourceNames == null || entry.transferredBlendShapeNames == null) continue;
                int count = Math.Min(entry.transferredBlendShapeSourceNames.Count, entry.transferredBlendShapeNames.Count);
                for (int i = 0; i < count; i++)
                {
                    if (!string.Equals(entry.transferredBlendShapeSourceNames[i], sourceBlendShapeName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    AddUniqueBlendShapeName(names, entry.transferredBlendShapeNames[i]);
                }
            }
        }

        // Standalone ReFit uses "refit_" by default. Detect the actual mesh name so older or
        // unregistered standalone results still participate in version and slider synchronization.
        if (target != null)
        {
            string expectedSuffix = sourceBlendShapeName;
            foreach (var renderer in target.transform.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string candidate = mesh.GetBlendShapeName(i);
                    if (candidate != null && candidate.StartsWith("refit_", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.Substring("refit_".Length), expectedSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        AddUniqueBlendShapeName(names, candidate);
                    }
                }
            }
        }

        return names;
    }

    private static void AddUniqueBlendShapeName(List<string> names, string candidate)
    {
        if (string.IsNullOrEmpty(candidate) ||
            names.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))) return;
        names.Add(candidate);
    }

    public static bool ApplyBlendShapeWeightWithTransferredReFit(MyCustomBase target,
        IEnumerable<SkinnedMeshRenderer> renderers, string sourceBlendShapeName, float weight)
    {
        if (renderers == null || string.IsNullOrEmpty(sourceBlendShapeName)) return false;

        var names = GetBlendShapeNamesWithTransferredReFit(target, sourceBlendShapeName);
        if (names.Count == 0) return false;

        bool changed = false;
        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.sharedMesh == null) continue;

            var appliedIndices = new HashSet<int>();
            foreach (string name in names)
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(name);
                if (index < 0 || !appliedIndices.Add(index)) continue;

                renderer.SetBlendShapeWeight(index, weight);
                EditorUtility.SetDirty(renderer);
                changed = true;
            }
        }

        return changed;
    }

    private static bool TryGetPathUnderRoot(Transform root, Transform transform, out string path)
    {
        path = null;
        if (root == null || transform == null || !transform.IsChildOf(root)) return false;
        path = GetRendererPath(root, transform);
        return true;
    }

    private static Transform ResolveStoredTransform(Transform root, string path)
    {
        if (root == null || path == null) return null;
        return path.Length == 0 ? root : root.Find(path);
    }

    private static Dictionary<string, RefitTransformState> BuildTransformStateMap(List<RefitTransformState> states)
    {
        var map = new Dictionary<string, RefitTransformState>(StringComparer.Ordinal);
        if (states == null) return map;
        foreach (var state in states)
        {
            if (state == null || string.IsNullOrEmpty(state.path) || map.ContainsKey(state.path)) continue;
            map.Add(state.path, state);
        }
        return map;
    }

    private static Transform ResolveOrCreateStoredTransform(Transform root, string path,
        Dictionary<string, RefitTransformState> statesByPath, string undoName)
    {
        var existing = ResolveStoredTransform(root, path);
        if (existing != null || root == null || path == null || path.Length == 0) return existing;

        var parts = path.Split('/');
        var parent = root;
        string currentPath = string.Empty;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (string.IsNullOrEmpty(part)) return null;

            currentPath = currentPath.Length == 0 ? part : currentPath + "/" + part;
            var child = parent.Find(part);
            if (child == null)
            {
                var go = new GameObject(part);
                Undo.RegisterCreatedObjectUndo(go, undoName);
                child = go.transform;
                child.SetParent(parent, false);
            }

            if (statesByPath != null && statesByPath.TryGetValue(currentPath, out var state))
            {
                Undo.RecordObject(child, undoName);
                child.localPosition = state.localPosition;
                child.localRotation = state.localRotation;
                child.localScale = state.localScale;
                EditorUtility.SetDirty(child);
            }

            parent = child;
        }

        return parent;
    }

    private static Transform ResolveStoredBone(Transform root, RefitAppliedMeshEntry entry, int index,
        Dictionary<string, RefitTransformState> statesByPath, string undoName)
    {
        if (entry.originalBones != null &&
            index >= 0 &&
            index < entry.originalBones.Count &&
            entry.originalBones[index] != null)
        {
            return entry.originalBones[index];
        }

        return ResolveOrCreateStoredTransform(root, entry.originalBonePaths[index], statesByPath, undoName);
    }

    private static bool RemoveReFitGeneratedMetadata(SkinnedMeshRenderer smr, string undoName)
    {
        if (smr == null) return false;
        var metadataType = ReFitApi.FindOptionalType("Orbiters.ReFit.ReFitGeneratedAssetMetadata");
        if (metadataType == null) return false;
        var metadata = smr.GetComponent(metadataType);
        if (metadata == null) return false;
        Undo.DestroyObjectImmediate(metadata);
        EditorUtility.SetDirty(smr);
        return true;
    }

    // ------------------------------------------------------------------
    // Source avatar (mesh A = default base body, with its own armature)
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves model A for ReFit from the configured base FBX asset, preserving that FBX's own armature,
    /// bindposes and skin weights. ReFit clones this source internally during staging.
    /// </summary>
    private static ReFitSourceReference BuildSourceReference(MyCustomBase mcb, out string error)
    {
        error = null;
        var root = mcb.transform.root;

        var baseMeshes = BuildBaseMeshMap(mcb);
        if (baseMeshes.Count == 0)
        {
            error = "ReFit could not find any mesh in the avatar's base FBX file(s). Make sure the MCB component's " +
                    "'Base Fbx Files' list points at the avatar's base FBX.";
            Debug.LogWarning("[MCB] ReFit: " + error);
            return null;
        }

        var targetBody = FindPrimaryBodyRenderer(root, baseMeshes);
        if (targetBody == null)
        {
            error = "ReFit could not match any of the avatar's meshes to the base FBX (looked for meshes named like " +
                    string.Join(", ", baseMeshes.Keys.Take(6)) +
                    (baseMeshes.Count > 6 ? ", ..." : string.Empty) +
                    "). The avatar's body mesh may have been renamed.";
            Debug.LogWarning("[MCB] ReFit: " + error);
            return null;
        }

        var baseMesh = ResolveBaseMesh(baseMeshes, targetBody.sharedMesh.name, targetBody.transform.name);
        if (baseMesh == null)
        {
            error = $"ReFit could not resolve the original base mesh for target body renderer '{targetBody.name}'.";
            Debug.LogWarning("[MCB] ReFit: " + error);
            return null;
        }

        var source = ResolveSourceBodyRenderer(mcb, baseMesh, targetBody.transform.name);
        if (source == null || source.sourceAvatar == null || source.renderer == null)
        {
            error = $"ReFit could not find a skinned renderer for original mesh '{baseMesh.name}' inside the configured base FBX asset.";
            Debug.LogWarning("[MCB] ReFit: " + error);
            return null;
        }

        return new ReFitSourceReference
        {
            sourceAvatar = source.sourceAvatar,
            sourceBody = source.renderer,
            targetBody = targetBody
        };
    }

    private static SkinnedMeshRenderer FindPrimaryBodyRenderer(Transform root, Dictionary<string, Mesh> baseMeshes)
    {
        SkinnedMeshRenderer primary = null;
        int bestVerts = -1;
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!IsBodyRenderer(smr, baseMeshes)) continue;
            if (smr.sharedMesh.vertexCount > bestVerts)
            {
                bestVerts = smr.sharedMesh.vertexCount;
                primary = smr;
            }
        }

        return primary;
    }

    private static SourceBodyReference ResolveSourceBodyRenderer(MyCustomBase target, Mesh baseMesh, string targetRendererName)
    {
        SourceBodyReference best = null;
        if (target == null || baseMesh == null) return null;

        foreach (var fbx in target.baseFbxFiles)
        {
            if (fbx == null) continue;
            foreach (var renderer in fbx.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.sharedMesh == null) continue;
                int score = ScoreSourceBodyRenderer(renderer, baseMesh, targetRendererName);
                if (score <= 0) continue;
                if (best == null ||
                    score > best.score ||
                    (score == best.score && renderer.sharedMesh.vertexCount > best.renderer.sharedMesh.vertexCount))
                {
                    best = new SourceBodyReference
                    {
                        sourceAvatar = fbx,
                        renderer = renderer,
                        score = score
                    };
                }
            }
        }

        return best;
    }

    private static int ScoreSourceBodyRenderer(SkinnedMeshRenderer renderer, Mesh baseMesh, string targetRendererName)
    {
        int score = 0;
        if (renderer.sharedMesh == baseMesh) score += 10000;
        if (string.Equals(CleanMeshName(renderer.sharedMesh.name), CleanMeshName(baseMesh.name), StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }
        if (!string.IsNullOrWhiteSpace(targetRendererName) &&
            string.Equals(renderer.transform.name, targetRendererName, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        return score;
    }

    // ------------------------------------------------------------------
    // Run
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-fits the given asset renderers from the default base body to the current version body, transferring
    /// the version's exposed blendshapes. Heavy geometry runs on a background thread; progress is reported into
    /// <paramref name="progressState"/> (drives the integrated progress button). On completion the scene and
    /// assets are saved and the generated files are committed through Unit Git when available.
    /// </summary>
    public static IEnumerator RunReFitCoroutine(MCBEditor editor, List<SkinnedMeshRenderer> targets,
        VersionApplyProgressState progressState, Action<bool, string> onComplete)
    {
        var mcb = editor.customBaseTarget;
        var root = mcb.transform.root;
        progressState.Begin("Preparing ReFit...", new Color32(0, 218, 109, 255));

        if (!IsReFitAvailable)
        {
            progressState.Fail();
            string msg = string.IsNullOrWhiteSpace(ReFitAvailabilityMessage)
                ? "The ReFit package (orbiters.refit) is not installed."
                : ReFitAvailabilityMessage;
            Debug.LogWarning("[MCB] ReFit aborted: " + msg);
            onComplete?.Invoke(false, msg);
            yield break;
        }

        var shapes = (mcb.appliedCustomBaseVersion != null && mcb.appliedCustomBaseVersion.customBlendshapes != null
                ? mcb.appliedCustomBaseVersion.customBlendshapes
                    .Where(b => b != null && !string.IsNullOrEmpty(b.name))
                    .Select(b => b.name)
                : Enumerable.Empty<string>())
            .Distinct()
            .ToList();

        var source = BuildSourceReference(mcb, out string sourceError);
        if (source == null)
        {
            progressState.Fail();
            string msg = sourceError ?? "Could not build the default base reference.";
            Debug.LogWarning("[MCB] ReFit aborted: " + msg);
            onComplete?.Invoke(false, msg);
            yield break;
        }

        var changedPaths = new List<string>();
        var failures = new List<string>();
        int done = 0;

        var enumerator = RefitTargets(editor, mcb, root, source.sourceAvatar, source.sourceBody, source.targetBody, shapes, targets,
            progressState, changedPaths, failures, v => done = v);
        while (true)
        {
            bool moved;
            try { moved = enumerator.MoveNext(); }
            catch (Exception ex)
            {
                progressState.Fail();
                Debug.LogError($"[MCB] ReFit failed: {ex}");
                onComplete?.Invoke(false, $"ReFit failed: {ex.Message}");
                yield break;
            }
            if (!moved) break;
            yield return enumerator.Current;
        }

        // Save + commit
        progressState.Report(0.97f, "Saving & committing...");
        AssetDatabase.SaveAssets();
        string scenePath = SaveAvatarScene(root.gameObject);
        string commitMessage = CommitReFitChanges(changedPaths, scenePath, changedPaths.Count);

        bool success = failures.Count == 0 && changedPaths.Count > 0;
        if (success) progressState.Complete();
        else progressState.Fail();

        string message = success
            ? $"Re-fitted {changedPaths.Count} asset(s)." + (string.IsNullOrEmpty(commitMessage) ? string.Empty : $" {commitMessage}")
            : (failures.Count > 0 ? string.Join("\n", failures) : "Nothing was re-fitted.");
        if (!success) Debug.LogWarning("[MCB] ReFit: " + message);
        onComplete?.Invoke(success, message);
    }

    private static IEnumerator RefitTargets(MCBEditor editor, MyCustomBase mcb, Transform root,
        GameObject source, SkinnedMeshRenderer sourceBody, SkinnedMeshRenderer targetBody, List<string> shapes,
        List<SkinnedMeshRenderer> targets, VersionApplyProgressState progressState,
        List<string> changedPaths, List<string> failures, Action<int> reportDone)
    {
        int done = 0;
        foreach (var renderer in targets)
        {
            if (renderer == null || renderer.sharedMesh == null) { reportDone(++done); continue; }
            string rendererPath = GetRendererPath(root, renderer.transform);

            // Re-running: recompute from the clean original renderer state instead of stacking refits
            // or inheriting armature edits made after the first ReFit.
            var existing = FindEntry(mcb, rendererPath);
            RefitAppliedMeshEntry capturedOriginalState = null;
            if (existing != null)
            {
                RestoreRendererState(root, renderer, existing, "MCB ReFit");
            }
            else
            {
                capturedOriginalState = CaptureRendererState(root, rendererPath, renderer);
            }
            var originalMesh = renderer.sharedMesh;

            float baseT = (float)done / targets.Count;
            float slice = 1f / targets.Count;
            object result = null;
            IEnumerator coroutine = null;

            try
            {
                object request = ReFitApi.CreateRequest(
                    shapes.Count > 0,
                    renderer,
                    source,
                    root.gameObject,
                    sourceBody,
                    targetBody,
                    shapes);
                coroutine = ReFitApi.ExecuteCoroutine(
                    request,
                    (t, label) => progressState.Report(baseT + Mathf.Clamp01(t) * slice * 0.95f, $"{renderer.name}: {label}"),
                    r => result = r);
            }
            catch (Exception ex)
            {
                failures.Add($"{renderer.name}: {ex.Message}");
                reportDone(++done);
                continue;
            }

            yield return coroutine;

            if (result != null && ReFitApi.IsSuccessful(result))
            {
                if (existing == null)
                {
                    existing = capturedOriginalState ?? CaptureRendererState(root, rendererPath, renderer);
                    if (existing.originalMesh == null) existing.originalMesh = originalMesh;
                    mcb.appliedRefits.Add(existing);
                }
                if (existing.originalMesh == null) existing.originalMesh = originalMesh;
                existing.refitMesh = ReFitApi.GetMesh(result);
                existing.refitMeshAssetPath = ReFitApi.GetMeshAssetPath(result);
                UpdateTransferredBlendShapeMap(existing,
                    ReFitApi.GetSecondarySourceShapeNames(result),
                    ReFitApi.GetSecondaryShapeNames(result));
                SyncTransferredBlendShapeWeights(existing, targetBody, renderer);
                if (!string.IsNullOrEmpty(existing.refitMeshAssetPath)) changedPaths.Add(existing.refitMeshAssetPath);
                EditorUtility.SetDirty(mcb);
            }
            else
            {
                string detail = result != null ? ReFitApi.GetErrorSummary(result) : "unknown error";
                failures.Add($"{renderer.name}: {detail}");
            }
            reportDone(++done);
        }
    }

    private static void UpdateTransferredBlendShapeMap(RefitAppliedMeshEntry entry, string[] sourceNames, string[] generatedNames)
    {
        if (entry == null) return;
        if (entry.transferredBlendShapeSourceNames == null)
            entry.transferredBlendShapeSourceNames = new List<string>();
        if (entry.transferredBlendShapeNames == null)
            entry.transferredBlendShapeNames = new List<string>();

        entry.transferredBlendShapeSourceNames.Clear();
        entry.transferredBlendShapeNames.Clear();

        if (sourceNames == null || generatedNames == null) return;
        int count = Math.Min(sourceNames.Length, generatedNames.Length);
        for (int i = 0; i < count; i++)
        {
            string sourceName = sourceNames[i];
            string generatedName = generatedNames[i];
            if (string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(generatedName)) continue;
            entry.transferredBlendShapeSourceNames.Add(sourceName);
            entry.transferredBlendShapeNames.Add(generatedName);
        }
    }

    private static void SyncTransferredBlendShapeWeights(RefitAppliedMeshEntry entry,
        SkinnedMeshRenderer sourceBody, SkinnedMeshRenderer targetRenderer)
    {
        if (entry?.transferredBlendShapeSourceNames == null ||
            entry.transferredBlendShapeNames == null ||
            sourceBody == null ||
            sourceBody.sharedMesh == null ||
            targetRenderer == null ||
            targetRenderer.sharedMesh == null)
        {
            return;
        }

        bool changed = false;
        int count = Math.Min(entry.transferredBlendShapeSourceNames.Count, entry.transferredBlendShapeNames.Count);
        for (int i = 0; i < count; i++)
        {
            string sourceName = entry.transferredBlendShapeSourceNames[i];
            string generatedName = entry.transferredBlendShapeNames[i];
            if (string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(generatedName)) continue;

            int sourceIndex = sourceBody.sharedMesh.GetBlendShapeIndex(sourceName);
            int generatedIndex = targetRenderer.sharedMesh.GetBlendShapeIndex(generatedName);
            if (sourceIndex < 0 || generatedIndex < 0) continue;

            if (!changed)
            {
                Undo.RecordObject(targetRenderer, "MCB ReFit blendshape sync");
                changed = true;
            }

            targetRenderer.SetBlendShapeWeight(generatedIndex, sourceBody.GetBlendShapeWeight(sourceIndex));
        }

        if (changed) EditorUtility.SetDirty(targetRenderer);
    }

    private static void SyncTransferredBlendShapeWeightsFromAvatar(
        RefitAppliedMeshEntry entry, MyCustomBase mcb, SkinnedMeshRenderer targetRenderer)
    {
        if (entry?.transferredBlendShapeSourceNames == null ||
            entry.transferredBlendShapeNames == null ||
            mcb == null || targetRenderer == null || targetRenderer.sharedMesh == null) return;

        var renderers = mcb.transform.root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var baseMeshes = BuildBaseMeshMap(mcb);
        bool changed = false;
        int count = Math.Min(entry.transferredBlendShapeSourceNames.Count, entry.transferredBlendShapeNames.Count);
        for (int i = 0; i < count; i++)
        {
            string sourceName = entry.transferredBlendShapeSourceNames[i];
            string generatedName = entry.transferredBlendShapeNames[i];
            int generatedIndex = targetRenderer.sharedMesh.GetBlendShapeIndex(generatedName);
            if (string.IsNullOrEmpty(sourceName) || generatedIndex < 0) continue;

            var sourceRenderer = FindBlendShapeSourceRenderer(
                renderers, targetRenderer, sourceName, baseMeshes, true) ??
                FindBlendShapeSourceRenderer(renderers, targetRenderer, sourceName, baseMeshes, false);
            if (sourceRenderer != null)
            {
                int sourceIndex = sourceRenderer.sharedMesh.GetBlendShapeIndex(sourceName);
                if (!changed)
                {
                    Undo.RecordObject(targetRenderer, "MCB ReFit blendshape sync");
                    changed = true;
                }
                targetRenderer.SetBlendShapeWeight(generatedIndex, sourceRenderer.GetBlendShapeWeight(sourceIndex));
            }
        }

        if (changed) EditorUtility.SetDirty(targetRenderer);
    }

    private static SkinnedMeshRenderer FindBlendShapeSourceRenderer(
        IEnumerable<SkinnedMeshRenderer> renderers,
        SkinnedMeshRenderer targetRenderer,
        string sourceName,
        Dictionary<string, Mesh> baseMeshes,
        bool requireBodyRenderer)
    {
        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer == targetRenderer || renderer.sharedMesh == null) continue;
            if (requireBodyRenderer && !IsBodyRenderer(renderer, baseMeshes)) continue;
            if (renderer.sharedMesh.GetBlendShapeIndex(sourceName) >= 0) return renderer;
        }

        return null;
    }

    /// <summary>Restores a single asset renderer to its original renderer state and drops it from the tracking list.</summary>
    public static void RestoreAsset(MyCustomBase mcb, string rendererPath)
    {
        if (mcb == null) return;
        var entry = FindEntry(mcb, rendererPath);
        if (entry == null) return;
        var root = mcb.transform.root;
        var t = root.Find(rendererPath);
        var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
        if (smr != null)
        {
            RestoreRendererState(root, smr, entry, "MCB ReFit restore");
        }
        Undo.RecordObject(mcb, "MCB ReFit restore");
        mcb.appliedRefits.Remove(entry);
        EditorUtility.SetDirty(mcb);
        RefitStateChanged?.Invoke(mcb, rendererPath);
    }

    private static string SaveAvatarScene(GameObject avatarRoot)
    {
        try
        {
            var scene = avatarRoot.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return null;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
            return scene.path;
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[MCB] ReFit: could not save the scene: {ex.Message}");
            return null;
        }
    }

    /// <summary>Commits only the ReFit-generated files (and the scene) through Unit Git when installed.</summary>
    private static string CommitReFitChanges(List<string> meshAssetPaths, string scenePath, int meshCount)
    {
        try
        {
            if (meshAssetPaths.Count == 0) return null;
            var paths = new List<string>();
            foreach (var p in meshAssetPaths)
            {
                AddWithMeta(paths, p);
                AddFolderMetas(paths, Path.GetDirectoryName(p)?.Replace('\\', '/'));
            }
            if (!string.IsNullOrEmpty(scenePath)) AddWithMeta(paths, scenePath);

            if (UnitGitReleasePublisher.TryCommitFiles(
                "MCB : ReFit",
                $"Re-fitted {meshCount} asset mesh(es) to the applied custom base version.\n" +
                string.Join("\n", meshAssetPaths),
                paths,
                out string message,
                out string commitHash))
            {
                MCBLogger.Log($"[MCB] ReFit changes committed via Unit Git: {commitHash}");
                return "Committed via Unit Git.";
            }

            if (UnitGitReleasePublisher.IsUnitGitAvailable)
            {
                MCBLogger.LogWarning($"[MCB] ReFit Unit Git commit failed: {message}");
            }
            return null;
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[MCB] ReFit Unit Git commit failed: {ex.Message}");
            return null;
        }
    }

    private static void AddWithMeta(List<string> paths, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        paths.Add(path);
        if (File.Exists(path + ".meta")) paths.Add(path + ".meta");
    }

    private static void AddFolderMetas(List<string> paths, string folder)
    {
        // Include the .meta of each (possibly new) folder up to Assets so fresh folders are committed too.
        while (!string.IsNullOrEmpty(folder) && folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(folder, "Assets", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(folder + ".meta")) paths.Add(folder + ".meta");
            folder = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        }
    }

    private static class ReFitApi
    {
        private const string RequestTypeName = "Orbiters.ReFit.ReFitRequest";
        private const string SettingsTypeName = "Orbiters.ReFit.ReFitSettings";
        private const string ModeTypeName = "Orbiters.ReFit.ReFitMode";
        private const string ProgressTypeName = "Orbiters.ReFit.ReFitProgress";
        private const string ServiceTypeName = "Orbiters.ReFit.Editor.ReFitService";
        private const string ResultTypeName = "Orbiters.ReFit.Editor.ReFitResult";

        public static bool IsAvailable
        {
            get { return string.IsNullOrEmpty(AvailabilityMessage); }
        }

        public static string AvailabilityMessage
        {
            get { return GetAvailabilityMessage(); }
        }

        public static Type FindOptionalType(string fullName)
        {
            return FindType(fullName);
        }

        private static Type RequestType { get { return FindType(RequestTypeName); } }
        private static Type SettingsType { get { return FindType(SettingsTypeName); } }
        private static Type ModeType { get { return FindType(ModeTypeName); } }
        private static Type ProgressType { get { return FindType(ProgressTypeName); } }
        private static Type ServiceType { get { return FindType(ServiceTypeName); } }
        private static Type ResultType { get { return FindType(ResultTypeName); } }

        public static object CreateRequest(
            bool meshAndBlendshape,
            SkinnedMeshRenderer assetRenderer,
            GameObject sourceAvatar,
            GameObject targetAvatar,
            SkinnedMeshRenderer sourceBodyRenderer,
            SkinnedMeshRenderer targetBodyRenderer,
            List<string> targetBlendshapes)
        {
            EnsureAvailable();

            object settings = Activator.CreateInstance(SettingsType);
            SetMember(settings, "replaceArmature", false);
            SetMember(settings, "transferWeights", false);
            SetMember(settings, "prefixTransferredShapes", false);
            SetMember(settings, "savePrefab", false);

            object request = Activator.CreateInstance(RequestType);
            SetMember(request, "mode", Enum.Parse(ModeType, meshAndBlendshape ? "MeshAndBlendshape" : "MeshToMesh"));
            SetMember(request, "assetRenderer", assetRenderer);
            SetMember(request, "sourceAvatar", sourceAvatar);
            SetMember(request, "targetAvatar", targetAvatar);
            SetMember(request, "sourceBodyRenderer", sourceBodyRenderer);
            SetMember(request, "targetBodyRenderer", targetBodyRenderer);
            SetMember(request, "targetBlendshapes", targetBlendshapes);
            SetMember(request, "settings", settings);
            return request;
        }

        public static IEnumerator ExecuteCoroutine(
            object request,
            Action<float, string> onProgress,
            Action<object> onComplete)
        {
            EnsureAvailable();

            var progressCallback = new ReFitProgressCallback(onProgress);
            Delegate progressDelegate = Delegate.CreateDelegate(
                ProgressType,
                progressCallback,
                nameof(ReFitProgressCallback.OnProgress));
            Type completeDelegateType = typeof(Action<>).MakeGenericType(ResultType);
            Delegate completeDelegate = CreateCompleteDelegate(ResultType, onComplete);

            var method = GetExecuteCoroutineMethod();
            if (method == null)
            {
                throw new MissingMethodException(ServiceType.FullName, "ExecuteCoroutine");
            }

            try
            {
                object result = method.Invoke(null, new object[] { request, progressDelegate, completeDelegate });
                if (result is IEnumerator enumerator)
                {
                    return enumerator;
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }

            throw new InvalidOperationException("ReFit ExecuteCoroutine did not return an editor coroutine.");
        }

        public static bool IsSuccessful(object result)
        {
            object value = GetMemberValue(result, "success");
            return value is bool success && success;
        }

        public static Mesh GetMesh(object result)
        {
            return GetMemberValue(result, "mesh") as Mesh;
        }

        public static string GetMeshAssetPath(object result)
        {
            return GetMemberValue(result, "meshAssetPath") as string;
        }

        public static string[] GetSecondaryShapeNames(object result)
        {
            return GetStringArray(result, "secondaryShapeNames");
        }

        public static string[] GetSecondarySourceShapeNames(object result)
        {
            return GetStringArray(result, "secondarySourceShapeNames");
        }

        public static string GetErrorSummary(object result)
        {
            var errors = new List<string>();
            object report = GetMemberValue(result, "report");
            if (GetMemberValue(report, "messages") is IEnumerable messages)
            {
                foreach (object message in messages)
                {
                    object severity = GetMemberValue(message, "severity");
                    if (!string.Equals(severity != null ? severity.ToString() : null, "Error", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string text = GetMemberValue(message, "text") as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        errors.Add(text);
                    }
                }
            }

            return errors.Count == 0 ? "unknown error" : string.Join("; ", errors);
        }

        private static string[] GetStringArray(object result, string name)
        {
            object value = GetMemberValue(result, name);
            if (value is string[] array) return array;
            if (value is IEnumerable enumerable)
            {
                var strings = new List<string>();
                foreach (object item in enumerable)
                {
                    strings.Add(item as string);
                }
                return strings.ToArray();
            }
            return null;
        }

        private static void EnsureAvailable()
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException(AvailabilityMessage);
            }
        }

        private static string GetAvailabilityMessage()
        {
            if (RequestType == null ||
                SettingsType == null ||
                ModeType == null ||
                ProgressType == null ||
                ServiceType == null ||
                ResultType == null)
            {
                return "The ReFit package (orbiters.refit) is not installed.";
            }

            if (GetExecuteCoroutineMethod() == null)
            {
                return "The ReFit package is installed but incompatible. Missing ReFitService.ExecuteCoroutine(ReFitRequest, ReFitProgress, Action<ReFitResult>).";
            }

            return string.Empty;
        }

        private static MethodInfo GetExecuteCoroutineMethod()
        {
            if (RequestType == null || ProgressType == null || ServiceType == null || ResultType == null)
            {
                return null;
            }

            Type completeDelegateType = typeof(Action<>).MakeGenericType(ResultType);
            return ServiceType.GetMethod(
                "ExecuteCoroutine",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { RequestType, ProgressType, completeDelegateType },
                null);
        }

        private static Delegate CreateCompleteDelegate(Type resultType, Action<object> onComplete)
        {
            var method = typeof(ReFitApi)
                .GetMethod(nameof(CreateCompleteDelegateTyped), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(resultType);
            return (Delegate)method.Invoke(null, new object[] { onComplete });
        }

        private static Action<T> CreateCompleteDelegateTyped<T>(Action<object> onComplete)
        {
            return result => onComplete?.Invoke(result);
        }

        private static Type FindType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return null;
            }

            var direct = Type.GetType(fullName);
            if (direct != null)
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName); }
                catch { }
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static void SetMember(object target, string name, object value)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            var type = target.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
            }
        }

        public static object GetMemberValue(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var type = target.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(target);
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property != null && property.CanRead ? property.GetValue(target) : null;
        }

        private sealed class ReFitProgressCallback
        {
            private readonly Action<float, string> onProgress;

            public ReFitProgressCallback(Action<float, string> onProgress)
            {
                this.onProgress = onProgress;
            }

            public void OnProgress(float t, string label)
            {
                onProgress?.Invoke(t, label);
            }
        }
    }
}
#endif
