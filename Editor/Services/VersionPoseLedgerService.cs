#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The person's own pose around a version install. Before an install every transform under the avatar is recorded
/// (parent, order, active state, local pose) with its renderers; the install runs as it always did, then the person's
/// deviation from the model rest is carried onto the installed version, and whatever the install left different goes in
/// <see cref="MyCustomBase.poseLedger"/>: where the person had it and what the install wrote. Going back runs the reset
/// machinery, then puts every recorded transform exactly where the person had it (with the edits the person made while
/// the version was on, composed on top), and every other transform where it was just before going back.
/// </summary>
internal static class VersionPoseLedgerService
{
    private const float PositionTolerance = 1e-5f;
    private const float AngleTolerance = 0.01f;
    private const float ScaleTolerance = 1e-5f;

    internal sealed class TransformState
    {
        public Transform parent; public int sibling; public bool active;
        public Vector3 position; public Quaternion rotation; public Vector3 scale;

        public static TransformState Of(Transform t) => new TransformState
        {
            parent = t.parent, sibling = t.GetSiblingIndex(), active = t.gameObject.activeSelf,
            position = t.localPosition, rotation = t.localRotation, scale = t.localScale
        };

        public bool SamePose(Transform t) => t.localPosition == position && t.localRotation == rotation && t.localScale == scale;
        public bool SamePoseExactly(Transform t) => Same(t.localPosition, position) && Same(t.localRotation, rotation) && Same(t.localScale, scale);
    }

    internal sealed class RendererState
    {
        public Mesh mesh; public Transform rootBone; public Transform[] bones; public Bounds bounds; public bool enabled, updateWhenOffscreen;
        public int meshId;

        public static RendererState Of(SkinnedMeshRenderer r) => new RendererState
        {
            mesh = r.sharedMesh, rootBone = r.rootBone, bones = r.bones, bounds = r.localBounds, enabled = r.enabled,
            updateWhenOffscreen = r.updateWhenOffscreen, meshId = MeshId(r)
        };

        // The serialized mesh reference: a missing asset (a buyer's prefab without its mesh files) keeps its own id.
        public static int MeshId(SkinnedMeshRenderer r)
        {
            if (r.sharedMesh != null) return r.sharedMesh.GetInstanceID();
            using (var serialized = new SerializedObject(r)) return serialized.FindProperty("m_Mesh").objectReferenceInstanceIDValue;
        }

        public bool Same(SkinnedMeshRenderer r) => r.sharedMesh == mesh && r.rootBone == rootBone && r.bones.SequenceEqual(bones)
                                                    && r.localBounds == bounds && r.enabled == enabled && r.updateWhenOffscreen == updateWhenOffscreen;
    }

    /// <summary>The avatar before an install, and the model rest its skeleton comes from.</summary>
    internal sealed class Snapshot
    {
        public Transform root;
        public readonly Dictionary<Transform, TransformState> transforms = new Dictionary<Transform, TransformState>();
        public readonly Dictionary<SkinnedMeshRenderer, RendererState> renderers = new Dictionary<SkinnedMeshRenderer, RendererState>();
        // Each base-model bone of the avatar and its local pose in the reference model.
        public readonly Dictionary<Transform, TransformState> reference = new Dictionary<Transform, TransformState>();
    }

    /// <summary>What going back needs from before the reset machinery ran.</summary>
    internal sealed class PendingRestore
    {
        public Snapshot before;
        public List<PoseLedgerEntry> entries;
        public List<PoseLedgerRenderer> renderers;
    }

    private static bool Same(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
    private static bool Same(Quaternion a, Quaternion b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z) && a.w.Equals(b.w);

    private static void CaptureAvatar(Snapshot snapshot)
    {
        foreach (var t in snapshot.root.GetComponentsInChildren<Transform>(true))
            if (t != snapshot.root) snapshot.transforms[t] = TransformState.Of(t);
        foreach (var r in snapshot.root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) snapshot.renderers[r] = RendererState.Of(r);
    }

    /// <summary>
    /// Records the avatar before an install. <paramref name="referenceModels"/> are the models the person's skeleton
    /// comes from, the custom body's own first: each base bone takes its rest from the first model that maps it.
    /// </summary>
    public static Snapshot Capture(Transform root, IEnumerable<GameObject> referenceModels)
    {
        var snapshot = new Snapshot { root = root };
        CaptureAvatar(snapshot);
        foreach (var model in (referenceModels ?? Enumerable.Empty<GameObject>()).Where(m => m != null).Distinct())
        {
            var skeleton = OwnSkeleton.Resolve(root, model.transform);
            foreach (var pair in skeleton.Map)
            {
                var bone = pair.Value;
                if (bone == null || bone == root || pair.Key == model.transform || snapshot.reference.ContainsKey(bone)) continue;
                if (bone.GetComponent<Renderer>() != null) continue;
                snapshot.reference[bone] = TransformState.Of(pair.Key);
            }
        }
        return snapshot;
    }

    /// <summary>
    /// The models the avatar's skeleton comes from: a migrated custom body's own models (recorded at its migration), the
    /// models its base renderers' meshes come from, then its base models.
    /// </summary>
    public static List<GameObject> ReferenceModels(MyCustomBase owner, Transform root)
    {
        var models = new List<GameObject>();
        void Add(UnityEngine.Object asset)
        {
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model != null && !models.Contains(model)) models.Add(model);
        }
        if (owner.legacyOriginal != null && owner.legacyOriginal.migrated)
            foreach (var state in owner.legacyOriginal.renderers ?? new List<LegacyRendererState>()) Add(state?.mesh);
        var canonical = (owner.versionOriginalModels != null && owner.versionOriginalModels.Count > 0 ? owner.versionOriginalModels : owner.baseFbxFiles)
            ?.FirstOrDefault(m => m != null);
        if (canonical != null)
        {
            var skeleton = OwnSkeleton.Resolve(root, canonical.transform);
            foreach (var renderer in skeleton.Renderers.Values) Add(renderer.sharedMesh);
        }
        Add(canonical);
        foreach (var model in owner.baseFbxFiles ?? new List<GameObject>()) Add(model);
        return models;
    }

    /// <summary>
    /// After the payload and the base pose: each base bone the person posed away from the model rest (P differs from R)
    /// keeps that deviation on top of what the version wrote (C): position C + (P - R), rotation C * (R^-1 * P), scale
    /// C * (P / R). A bone the version left at the model rest ends exactly at the person's pose; a bone the install did not
    /// write stays.
    /// </summary>
    public static int CarryPose(Snapshot snapshot)
    {
        int carried = 0;
        foreach (var pair in snapshot.reference)
        {
            var bone = pair.Key;
            if (bone == null || !snapshot.transforms.TryGetValue(bone, out var person)) continue;
            var rest = pair.Value;
            if (person.SamePoseExactly(bone)) continue;
            if (Near(person.position, rest.position, person.rotation, rest.rotation, person.scale, rest.scale)) continue;
            Vector3 position; Quaternion rotation; Vector3 scale;
            if (Near(bone.localPosition, rest.position, bone.localRotation, rest.rotation, bone.localScale, rest.scale))
            {
                position = person.position; rotation = person.rotation; scale = person.scale;
            }
            else
            {
                position = bone.localPosition + (person.position - rest.position);
                rotation = (bone.localRotation * (Quaternion.Inverse(rest.rotation) * person.rotation)).normalized;
                scale = Vector3.Scale(bone.localScale, Ratio(person.scale, rest.scale));
            }
            Undo.RecordObject(bone, "Keep the avatar's pose");
            bone.localPosition = position; bone.localRotation = rotation; bone.localScale = scale;
            EditorUtility.SetDirty(bone);
            carried++;
        }
        return carried;
    }

    private static bool Near(Vector3 p, Vector3 q, Quaternion r, Quaternion s, Vector3 a, Vector3 b) =>
        (p - q).magnitude <= PositionTolerance && Quaternion.Angle(r, s) <= AngleTolerance
        && Mathf.Abs(a.x - b.x) <= ScaleTolerance && Mathf.Abs(a.y - b.y) <= ScaleTolerance && Mathf.Abs(a.z - b.z) <= ScaleTolerance;

    private static Vector3 Ratio(Vector3 a, Vector3 b) =>
        new Vector3(b.x != 0f ? a.x / b.x : 1f, b.y != 0f ? a.y / b.y : 1f, b.z != 0f ? a.z / b.z : 1f);

    /// <summary>Once the install has completely finished: what it left different goes in the ledger.</summary>
    public static void Record(MyCustomBase owner, Snapshot snapshot)
    {
        var ledger = new PoseLedger { recorded = true };
        foreach (var pair in snapshot.transforms)
        {
            var t = pair.Key; var b = pair.Value;
            if (t == null) continue;
            if (t.parent == b.parent && t.GetSiblingIndex() == b.sibling && t.gameObject.activeSelf == b.active && b.SamePoseExactly(t)) continue;
            ledger.entries.Add(new PoseLedgerEntry
            {
                transform = t, parent = b.parent, sibling = b.sibling, active = b.active,
                position = b.position, rotation = b.rotation, scale = b.scale,
                writtenParent = t.parent, writtenActive = t.gameObject.activeSelf,
                writtenPosition = t.localPosition, writtenRotation = t.localRotation, writtenScale = t.localScale
            });
        }
        foreach (var pair in snapshot.renderers)
        {
            var r = pair.Key; var b = pair.Value;
            if (r == null || b.Same(r)) continue;
            ledger.renderers.Add(new PoseLedgerRenderer { renderer = r, mesh = b.mesh, rootBone = b.rootBone, bones = b.bones, bounds = b.bounds,
                enabled = b.enabled, updateWhenOffscreen = b.updateWhenOffscreen, meshId = b.meshId });
        }
        Undo.RecordObject(owner, "Record the avatar's pose");
        owner.poseLedger = ledger;
        EditorUtility.SetDirty(owner);
        MCBLogger.Log($"[MCB] Recorded {ledger.entries.Count} object(s) and {ledger.renderers.Count} renderer(s) the version changed, to put back on going back.");
    }

    /// <summary>Before the reset machinery: the avatar as it is now (the version on it) and the ledger to restore.</summary>
    public static PendingRestore BeginRestore(MyCustomBase owner, Transform root)
    {
        if (owner == null || root == null || owner.poseLedger == null || !owner.poseLedger.recorded) return null;
        var before = new Snapshot { root = root };
        CaptureAvatar(before);
        return new PendingRestore
        {
            before = before,
            entries = owner.poseLedger.entries.Where(e => e != null && e.transform != null).ToList(),
            renderers = owner.poseLedger.renderers.Where(r => r != null && r.renderer != null).ToList()
        };
    }

    /// <summary>
    /// After the reset machinery removed the version: every ledger transform back to its parent, order, active state and
    /// local pose from before the install (a bit-exact copy when the person did not touch it, their edit composed on top
    /// when they did), every other transform as it was before going back, renderers likewise; then the ledger is cleared.
    /// </summary>
    public static void FinishRestore(MyCustomBase owner, PendingRestore pending)
    {
        if (owner == null || pending == null) return;
        const string step = "Put the avatar back as it was";
        var wanted = new Dictionary<Transform, TransformState>();
        var ledgered = new HashSet<Transform>();
        foreach (var e in pending.entries)
        {
            var t = e.transform;
            if (t == null) continue;
            ledgered.Add(t);
            pending.before.transforms.TryGetValue(t, out var now);
            var state = new TransformState { parent = e.parent, sibling = e.sibling, active = e.active, position = e.position, rotation = e.rotation, scale = e.scale };
            if (now != null)
            {
                // Moved elsewhere by the person meanwhile: their parent stays.
                if (now.parent != e.writtenParent || e.parent == null) state.parent = now.parent;
                if (now.active != e.writtenActive) state.active = now.active;
                if (!(Same(now.position, e.writtenPosition) && Same(now.rotation, e.writtenRotation) && Same(now.scale, e.writtenScale)))
                {
                    state.position = e.position + (now.position - e.writtenPosition);
                    state.rotation = (e.rotation * (Quaternion.Inverse(e.writtenRotation) * now.rotation)).normalized;
                    state.scale = Vector3.Scale(e.scale, Ratio(now.scale, e.writtenScale));
                }
            }
            wanted[t] = state;
        }
        foreach (var pair in pending.before.transforms)
            if (pair.Key != null && !ledgered.Contains(pair.Key)) wanted[pair.Key] = pair.Value;
        var ledgerRenderers = pending.renderers.Where(r => r.renderer != null).GroupBy(r => r.renderer).ToDictionary(g => g.Key, g => g.First());
        var renderers = new Dictionary<SkinnedMeshRenderer, RendererState>();
        foreach (var pair in ledgerRenderers)
            renderers[pair.Key] = new RendererState { mesh = pair.Value.mesh, rootBone = pair.Value.rootBone, bones = pair.Value.bones, bounds = pair.Value.bounds,
                enabled = pair.Value.enabled, updateWhenOffscreen = pair.Value.updateWhenOffscreen, meshId = pair.Value.meshId };
        foreach (var pair in pending.before.renderers)
            if (pair.Key != null && !renderers.ContainsKey(pair.Key)) renderers[pair.Key] = pair.Value;
        Put(wanted, renderers, step);

        Undo.RecordObject(owner, step);
        owner.poseLedger = new PoseLedger();
        EditorUtility.SetDirty(owner);
    }

    /// <summary>
    /// After a switch that failed and was rolled back: every transform and renderer exactly as recorded at its start
    /// (Undo does not cover everything a switch touches, such as recomputed renderer bounds).
    /// </summary>
    public static void RestoreSnapshot(Snapshot snapshot)
    {
        if (snapshot == null) return;
        Put(snapshot.transforms.Where(p => p.Key != null).ToDictionary(p => p.Key, p => p.Value),
            snapshot.renderers.Where(p => p.Key != null).ToDictionary(p => p.Key, p => p.Value), "Put the avatar back as it was");
    }

    /// <summary>The avatar's state before a switch, for <see cref="RestoreSnapshot"/>.</summary>
    public static Snapshot CaptureState(Transform root)
    {
        if (root == null) return null;
        var snapshot = new Snapshot { root = root };
        CaptureAvatar(snapshot);
        return snapshot;
    }

    private static void Put(Dictionary<Transform, TransformState> wanted, Dictionary<SkinnedMeshRenderer, RendererState> renderers, string step)
    {
        foreach (var pair in wanted)
        {
            var t = pair.Key; var state = pair.Value;
            if (state.parent == null || t.parent == state.parent || state.parent.IsChildOf(t)) continue;
            UnpackFor(t);
            Undo.SetTransformParent(t, state.parent, step);
        }
        foreach (var pair in wanted)
        {
            var t = pair.Key; var state = pair.Value;
            if (!state.SamePoseExactly(t))
            {
                Undo.RecordObject(t, step);
                t.localPosition = state.position; t.localRotation = state.rotation; t.localScale = state.scale;
                EditorUtility.SetDirty(t);
            }
            if (t.gameObject.activeSelf != state.active)
            {
                Undo.RecordObject(t.gameObject, step);
                t.gameObject.SetActive(state.active);
            }
        }
        foreach (var group in wanted.Where(p => p.Key != null && p.Key.parent == p.Value.parent).GroupBy(p => p.Key.parent))
            foreach (var pair in group.OrderBy(p => p.Value.sibling))
            {
                int index = Mathf.Min(pair.Value.sibling, group.Key.childCount - 1);
                if (pair.Key.GetSiblingIndex() != index) Undo.SetSiblingIndex(pair.Key, index, step);
            }

        // A renderer's fields come back when it carries the recorded mesh again (a restored custom body is not overwritten).
        // A renderer that had no mesh (or a missing one) gets that reference back instead of the base's mesh, and a
        // clothing mesh MCB rebound for the version gets its own mesh back.
        var rebound = new HashSet<Mesh>();
        foreach (var pair in renderers)
        {
            var renderer = pair.Key; var state = pair.Value;
            if (renderer == null) continue;
            if (state.mesh == null && renderer.sharedMesh != null && state.meshId != renderer.sharedMesh.GetInstanceID())
            {
                using (var serialized = new SerializedObject(renderer))
                {
                    serialized.FindProperty("m_Mesh").objectReferenceInstanceIDValue = state.meshId;
                    serialized.ApplyModifiedProperties();
                }
            }
            else if (state.mesh != null && renderer.sharedMesh != null && renderer.sharedMesh != state.mesh && IsRebound(renderer.sharedMesh))
            {
                rebound.Add(renderer.sharedMesh);
                Undo.RecordObject(renderer, step);
                renderer.sharedMesh = state.mesh;
            }
            if (renderer.sharedMesh == state.mesh) Apply(renderer, state, step);
        }
        DeleteUnused(rebound);
    }

    public const string ReboundFolder = "Assets/MCB/rebound";

    private static bool IsRebound(Mesh mesh)
    {
        string path = AssetDatabase.GetAssetPath(mesh);
        return path.StartsWith(ReboundFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    // A rebound copy no renderer of an open scene uses any more is removed: one avatar never piles copies up.
    private static void DeleteUnused(HashSet<Mesh> meshes)
    {
        if (meshes.Count == 0) return;
        var used = new HashSet<Mesh>(UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh).Where(m => m != null));
        foreach (var mesh in meshes)
            if (mesh != null && !used.Contains(mesh)) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(mesh));
    }

    private static void Apply(SkinnedMeshRenderer renderer, RendererState state, string step)
    {
        if (state.Same(renderer)) return;
        Undo.RecordObject(renderer, step);
        if (state.bones != null && (renderer.sharedMesh == null || renderer.sharedMesh.bindposes.Length == 0 || state.bones.Length == renderer.sharedMesh.bindposes.Length))
            renderer.bones = state.bones;
        renderer.rootBone = state.rootBone;
        renderer.localBounds = state.bounds;
        renderer.updateWhenOffscreen = state.updateWhenOffscreen;
        renderer.enabled = state.enabled;
        EditorUtility.SetDirty(renderer);
    }

    /// <summary>
    /// Unity cannot reparent an object inside a prefab instance: unpack only the outermost instance holding it (nested
    /// prefabs inside keep their link), as often as it takes.
    /// </summary>
    public static void UnpackFor(Transform t)
    {
        for (int i = 0; i < 8 && t != null && PrefabUtility.IsPartOfPrefabInstance(t.gameObject) && !PrefabUtility.IsAddedGameObjectOverride(t.gameObject); i++)
        {
            var outermost = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
            if (outermost == null) break;
            PrefabUtility.UnpackPrefabInstance(outermost, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);
        }
    }
}
#endif
