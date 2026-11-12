#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The person's own pose around a version install. Before an install every transform under the avatar is recorded
/// (parent, order, active state, local pose and its pose relative to the avatar root) with its renderers; the install runs
/// as it always did, then the person's adjustment of each base bone, measured against the model their skeleton comes from
/// in root space, is carried onto the installed version; objects that are not the version's keep their place on the
/// avatar and clothing skinned to a bone the version moved is rebound; whatever the install left different goes in
/// <see cref="MyCustomBase.poseLedger"/>. Going back runs the reset machinery, then puts every recorded transform exactly
/// where the person had it (with the edits the person made while the version was on), and every other transform where it
/// was just before going back.
/// </summary>
internal static class VersionPoseLedgerService
{
    // "The person did not adjust this": root-space position, quaternion components and relative scale.
    internal const double PositionTolerance = 1e-5;
    internal const double RotationTolerance = 1e-5;
    internal const double ScaleTolerance = 1e-6;
    // A computed local value this close to what is there is not written (no float noise in the scene).
    internal const double NoiseTolerance = 1e-6;

    /// <summary>Tests and diagnostics: when set, <see cref="CarryPose"/> writes every base bone's P, R, C and result here (JSON).</summary>
    internal static string DiagnosticsPathForTests;

    // ---------------------------------------------------------------------------------------------------------------
    // Affine transforms in double precision (rotation and scale as a 3x3, plus translation).

    internal sealed class Affine
    {
        public readonly double[] m = new double[12]; // row-major 3x4

        public static Affine Identity()
        {
            var a = new Affine(); a.m[0] = a.m[5] = a.m[10] = 1; return a;
        }

        public static Affine TRS(Vector3 p, Quaternion q, Vector3 s)
        {
            double x = q.x, y = q.y, z = q.z, w = q.w, n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n > 0) { x /= n; y /= n; z /= n; w /= n; }
            var r = new[,]
            {
                { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w) },
                { 2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
                { 2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) }
            };
            double[] sc = { s.x, s.y, s.z };
            var a = new Affine();
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++) a.m[row * 4 + col] = r[row, col] * sc[col];
                a.m[row * 4 + 3] = row == 0 ? p.x : row == 1 ? p.y : p.z;
            }
            return a;
        }

        public static Affine Local(Transform t) => TRS(t.localPosition, t.localRotation, t.localScale);

        public static Affine operator *(Affine a, Affine b)
        {
            var c = new Affine();
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                    c.m[row * 4 + col] = a.m[row * 4] * b.m[col] + a.m[row * 4 + 1] * b.m[4 + col] + a.m[row * 4 + 2] * b.m[8 + col];
                c.m[row * 4 + 3] = a.m[row * 4] * b.m[3] + a.m[row * 4 + 1] * b.m[7] + a.m[row * 4 + 2] * b.m[11] + a.m[row * 4 + 3];
            }
            return c;
        }

        public Affine Inverse()
        {
            double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            var r = new Affine();
            if (Math.Abs(det) < 1e-300) return Identity();
            double k = 1.0 / det;
            r.m[0] = A * k; r.m[1] = -(b * i - c * h) * k; r.m[2] = (b * f - c * e) * k;
            r.m[4] = B * k; r.m[5] = (a * i - c * g) * k; r.m[6] = -(a * f - c * d) * k;
            r.m[8] = C * k; r.m[9] = -(a * h - b * g) * k; r.m[10] = (a * e - b * d) * k;
            for (int row = 0; row < 3; row++)
                r.m[row * 4 + 3] = -(r.m[row * 4] * m[3] + r.m[row * 4 + 1] * m[7] + r.m[row * 4 + 2] * m[11]);
            return r;
        }

        /// <summary>Position, rotation (sign closest to <paramref name="hint"/>) and scale, in double precision.</summary>
        public void Decompose(out double[] position, out double[] rotation, out double[] scale, Quaternion hint)
        {
            position = new[] { m[3], m[7], m[11] };
            double[] c0 = { m[0], m[4], m[8] }, c1 = { m[1], m[5], m[9] }, c2 = { m[2], m[6], m[10] };
            double sx = Length(c0), sy = Length(c1), sz = Length(c2);
            double det = m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
            if (det < 0) sx = -sx;
            scale = new[] { sx, sy, sz };
            double r00 = SafeDiv(c0[0], sx), r10 = SafeDiv(c0[1], sx), r20 = SafeDiv(c0[2], sx);
            double r01 = SafeDiv(c1[0], sy), r11 = SafeDiv(c1[1], sy), r21 = SafeDiv(c1[2], sy);
            double r02 = SafeDiv(c2[0], sz), r12 = SafeDiv(c2[1], sz), r22 = SafeDiv(c2[2], sz);
            double qw, qx, qy, qz, trace = r00 + r11 + r22;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2; qw = 0.25 * s; qx = (r21 - r12) / s; qy = (r02 - r20) / s; qz = (r10 - r01) / s;
            }
            else if (r00 > r11 && r00 > r22)
            {
                double s = Math.Sqrt(1.0 + r00 - r11 - r22) * 2; qw = (r21 - r12) / s; qx = 0.25 * s; qy = (r01 + r10) / s; qz = (r02 + r20) / s;
            }
            else if (r11 > r22)
            {
                double s = Math.Sqrt(1.0 + r11 - r00 - r22) * 2; qw = (r02 - r20) / s; qx = (r01 + r10) / s; qy = 0.25 * s; qz = (r12 + r21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + r22 - r00 - r11) * 2; qw = (r10 - r01) / s; qx = (r02 + r20) / s; qy = (r12 + r21) / s; qz = 0.25 * s;
            }
            double n = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            qx /= n; qy /= n; qz /= n; qw /= n;
            if (qx * hint.x + qy * hint.y + qz * hint.z + qw * hint.w < 0) { qx = -qx; qy = -qy; qz = -qz; qw = -qw; }
            rotation = new[] { qx, qy, qz, qw };
        }

        public Matrix4x4 ToMatrix()
        {
            var r = Matrix4x4.identity;
            for (int row = 0; row < 3; row++) for (int col = 0; col < 4; col++) r[row, col] = (float)m[row * 4 + col];
            return r;
        }

        public static Affine FromMatrix(Matrix4x4 matrix)
        {
            var a = new Affine();
            for (int row = 0; row < 3; row++) for (int col = 0; col < 4; col++) a.m[row * 4 + col] = matrix[row, col];
            return a;
        }

        private static double Length(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        private static double SafeDiv(double a, double b) => b == 0 ? 0 : a / b;
    }

    /// <summary>Every transform under <paramref name="root"/> relative to it (the root's own position, rotation and scale left out).</summary>
    internal static Dictionary<Transform, Affine> RootSpace(Transform root)
    {
        var result = new Dictionary<Transform, Affine>();
        if (root == null) return result;
        result[root] = Affine.Identity();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            result[t] = (t.parent != null && result.TryGetValue(t.parent, out var parent) ? parent : Affine.Identity()) * Affine.Local(t);
        }
        return result;
    }

    private static Affine RootSpaceOf(Transform root, Transform t)
    {
        var chain = new List<Transform>();
        for (var node = t; node != null && node != root; node = node.parent) chain.Add(node);
        var result = Affine.Identity();
        for (int i = chain.Count - 1; i >= 0; i--) result = result * Affine.Local(chain[i]);
        return result;
    }

    /// <summary>The same pose within the robust tolerances (root-space position, quaternion components, relative scale).</summary>
    internal static bool SamePose(Affine a, Affine b)
    {
        a.Decompose(out var pa, out var qa, out var sa, Quaternion.identity);
        b.Decompose(out var pb, out var qb, out var sb, new Quaternion((float)qa[0], (float)qa[1], (float)qa[2], (float)qa[3]));
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(pa[i] - pb[i]) > PositionTolerance) return false;
            if (Math.Abs(sa[i] - sb[i]) > ScaleTolerance * Math.Max(Math.Abs(sb[i]), 1e-9)) return false;
        }
        for (int i = 0; i < 4; i++) if (Math.Abs(qa[i] - qb[i]) > RotationTolerance) return false;
        return true;
    }

    private static bool SameLocalRobust(TransformState a, TransformState b)
    {
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(a.position[i] - b.position[i]) > PositionTolerance) return false;
            if (Math.Abs(a.scale[i] - b.scale[i]) > ScaleTolerance * Math.Max(Math.Abs(b.scale[i]), 1e-9)) return false;
        }
        float dot = a.rotation.x * b.rotation.x + a.rotation.y * b.rotation.y + a.rotation.z * b.rotation.z + a.rotation.w * b.rotation.w;
        float sign = dot < 0 ? -1f : 1f;
        return Math.Abs(a.rotation.x - sign * b.rotation.x) <= RotationTolerance && Math.Abs(a.rotation.y - sign * b.rotation.y) <= RotationTolerance
            && Math.Abs(a.rotation.z - sign * b.rotation.z) <= RotationTolerance && Math.Abs(a.rotation.w - sign * b.rotation.w) <= RotationTolerance;
    }

    // ---------------------------------------------------------------------------------------------------------------

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
        public int[] materialIds;
        public float[] weights;

        public static RendererState Of(SkinnedMeshRenderer r) => new RendererState
        {
            mesh = r.sharedMesh, rootBone = r.rootBone, bones = r.bones, bounds = r.localBounds, enabled = r.enabled,
            updateWhenOffscreen = r.updateWhenOffscreen, meshId = MeshId(r), materialIds = MaterialIds(r),
            weights = r.sharedMesh == null ? new float[0] : Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray()
        };

        public static int[] MaterialIds(SkinnedMeshRenderer r)
        {
            using (var serialized = new SerializedObject(r))
            {
                var materials = serialized.FindProperty("m_Materials");
                return Enumerable.Range(0, materials.arraySize).Select(i => materials.GetArrayElementAtIndex(i).objectReferenceInstanceIDValue).ToArray();
            }
        }

        // The serialized mesh reference: a missing asset (a buyer's prefab without its mesh files) keeps its own id.
        public static int MeshId(SkinnedMeshRenderer r)
        {
            if (r.sharedMesh != null) return r.sharedMesh.GetInstanceID();
            using (var serialized = new SerializedObject(r)) return serialized.FindProperty("m_Mesh").objectReferenceInstanceIDValue;
        }

        public bool Same(SkinnedMeshRenderer r) => r.sharedMesh == mesh && r.rootBone == rootBone && r.bones.SequenceEqual(bones)
                                                    && r.localBounds == bounds && r.enabled == enabled && r.updateWhenOffscreen == updateWhenOffscreen
                                                    && (weights == null || r.sharedMesh == null || r.sharedMesh.blendShapeCount != weights.Length
                                                        || Enumerable.Range(0, weights.Length).All(i => r.GetBlendShapeWeight(i).Equals(weights[i])))
                                                    && (materialIds == null || MaterialIds(r).SequenceEqual(materialIds));
    }

    /// <summary>A base bone's rest in the reference model: local pose, pose relative to the model root, and its model parent's image.</summary>
    internal sealed class ReferenceBone
    {
        public TransformState rest; public Affine restRoot; public Transform model; public Transform parentImage;
    }

    /// <summary>The avatar before an install, and the model rest its skeleton comes from.</summary>
    internal sealed class Snapshot
    {
        public Transform root;
        public readonly Dictionary<Transform, TransformState> transforms = new Dictionary<Transform, TransformState>();
        public readonly Dictionary<SkinnedMeshRenderer, RendererState> renderers = new Dictionary<SkinnedMeshRenderer, RendererState>();
        // Every transform relative to the avatar root.
        public Dictionary<Transform, Affine> rootSpace = new Dictionary<Transform, Affine>();
        // Each base bone of the avatar and its rest in the reference model (empty when no model reproduces the skeleton).
        public Dictionary<Transform, ReferenceBone> reference = new Dictionary<Transform, ReferenceBone>();
        public GameObject referenceModel; public string referenceLabel;
        public readonly List<string> referenceNotes = new List<string>();
        // The avatar's own body objects (renderers, armature and its base-named bones): the version's to pose.
        public readonly HashSet<Transform> baseObjects = new HashSet<Transform>();
        public readonly HashSet<SkinnedMeshRenderer> ownRenderers = new HashSet<SkinnedMeshRenderer>();
        // References to missing assets: component, property path, the reference as the editor serializes it.
        public readonly List<(Component owner, string path, string token)> missing = new List<(Component, string, string)>();
        public bool hasAnimator; public Avatar animatorAvatar;
    }

    private static void CaptureMissing(Snapshot snapshot)
    {
        foreach (var component in snapshot.root.GetComponentsInChildren<Component>(true))
        {
            // MCB's own bookkeeping is not the person's.
            if (component == null || component is Transform || component is MyCustomBase) continue;
            List<string> paths = null;
            using (var serialized = new SerializedObject(component))
            {
                var it = serialized.GetIterator();
                while (it.Next(true))
                    if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue == null && it.objectReferenceInstanceIDValue != 0)
                        (paths ?? (paths = new List<string>())).Add(it.propertyPath);
            }
            if (paths == null) continue;
            // The reference as the editor serializes it (file id and GUID of the missing asset): it survives a restart.
            var fields = Fields(component);
            foreach (string path in paths)
            {
                var token = fields != null ? At(fields, path) : null;
                if (token != null) snapshot.missing.Add((component, path, token.ToString(Newtonsoft.Json.Formatting.None)));
            }
        }
    }

    private static Newtonsoft.Json.Linq.JObject Fields(Component component)
    {
        try
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse(EditorJsonUtility.ToJson(component));
            return json.Properties().FirstOrDefault()?.Value as Newtonsoft.Json.Linq.JObject;
        }
        catch (Exception) { return null; }
    }

    // The JSON token at a serialized property path ("m_Materials.Array.data[1]", "baseAnimationLayers.Array.data[2].animatorController").
    private static Newtonsoft.Json.Linq.JToken At(Newtonsoft.Json.Linq.JToken token, string path)
    {
        foreach (string segment in path.Split('.'))
        {
            if (token == null) return null;
            if (segment == "Array") continue;
            if (segment.StartsWith("data[", StringComparison.Ordinal) && segment.EndsWith("]", StringComparison.Ordinal))
            {
                if (!(token is Newtonsoft.Json.Linq.JArray array) || !int.TryParse(segment.Substring(5, segment.Length - 6), out int index) || index >= array.Count) return null;
                token = array[index];
            }
            else token = token is Newtonsoft.Json.Linq.JObject o ? o[segment] : null;
        }
        return token;
    }

    // A reference to a missing asset an operation emptied gets it back (Unity cannot assign a missing object through its
    // API: the editor's serialized form of the component, with only that field replaced, is written back).
    private static void PutMissing(IEnumerable<(Component owner, string path, string token)> missing, bool sameSession = true)
    {
        foreach (var group in missing.Where(m => m.owner != null && !string.IsNullOrEmpty(m.token)).GroupBy(m => m.owner))
        {
            var emptied = new List<(string path, string token)>();
            using (var serialized = new SerializedObject(group.Key))
                foreach (var entry in group)
                {
                    var property = serialized.FindProperty(entry.path);
                    if (property == null || property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    // Only a field that is empty now: a missing reference still there, or anything put there since, stays.
                    if (property.objectReferenceValue != null || property.objectReferenceInstanceIDValue != 0) continue;
                    // An instance id only means something in the editor session that recorded it.
                    if (!sameSession && entry.token.Contains("instanceID")) continue;
                    emptied.Add((entry.path, entry.token));
                }
            if (emptied.Count == 0) continue;
            var fields = Fields(group.Key);
            if (fields == null) continue;
            // Only the top-level fields holding those references are written back (the rest of the component is left alone:
            // the editor's JSON cannot carry references to scene objects back).
            var part = new Newtonsoft.Json.Linq.JObject();
            foreach (var entry in emptied)
            {
                string top = entry.path.Split('.')[0];
                var field = part[top] ?? fields[top]?.DeepClone();
                if (field == null) continue;
                var holder = new Newtonsoft.Json.Linq.JObject { [top] = field };
                var at = At(holder, entry.path);
                if (at == null) continue;
                at.Replace(Newtonsoft.Json.Linq.JToken.Parse(entry.token));
                part[top] = holder[top];
            }
            foreach (var property in part.Properties().ToList())
                if (property.Value.SelectTokens("$..instanceID").Any(t => t.Type == Newtonsoft.Json.Linq.JTokenType.Integer && (long)t != 0)) property.Remove();
            if (!part.HasValues) continue;
            Undo.RecordObject(group.Key, "Put missing references back");
            var wrapper = new Newtonsoft.Json.Linq.JObject { [Newtonsoft.Json.Linq.JObject.Parse(EditorJsonUtility.ToJson(group.Key)).Properties().First().Name] = part };
            EditorJsonUtility.FromJsonOverwrite(wrapper.ToString(Newtonsoft.Json.Formatting.None), group.Key);
            EditorUtility.SetDirty(group.Key);
        }
    }

    // An instance id (in a recorded reference) only means something within the editor session that recorded it.
    private const string SessionKey = "MCB.PoseLedger.Session";

    internal static string EditorSession()
    {
        string id = SessionState.GetString(SessionKey, "");
        if (id.Length == 0) { id = Guid.NewGuid().ToString("N"); SessionState.SetString(SessionKey, id); }
        return id;
    }

    /// <summary>What going back needs from before the reset machinery ran.</summary>
    internal sealed class PendingRestore
    {
        public Snapshot before;
        public List<PoseLedgerEntry> entries;
        public List<PoseLedgerRenderer> renderers;
        public bool sameSession;
        public bool animatorRecorded; public Avatar animatorAvatar;
    }

    private static bool Same(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
    private static bool Same(Quaternion a, Quaternion b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z) && a.w.Equals(b.w);

    private static void CaptureAvatar(Snapshot snapshot)
    {
        foreach (var t in snapshot.root.GetComponentsInChildren<Transform>(true))
            if (t != snapshot.root) snapshot.transforms[t] = TransformState.Of(t);
        foreach (var r in snapshot.root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) snapshot.renderers[r] = RendererState.Of(r);
        snapshot.rootSpace = RootSpace(snapshot.root);
        var animator = snapshot.root.GetComponent<Animator>();
        snapshot.hasAnimator = animator != null;
        snapshot.animatorAvatar = animator != null ? animator.avatar : null;
        CaptureMissing(snapshot);
    }

    /// <summary>
    /// Records the avatar before an install. The person's skeleton is measured against a reference rest: the one an
    /// earlier install of this avatar found (kept on <paramref name="owner"/>), else the first of
    /// <paramref name="referenceModels"/> whose rest reproduces the skeleton (at most half of its bones adjusted relative to
    /// their parent). A prefab the avatar is an instance of is matched object by object. With none, nothing is carried.
    /// </summary>
    public static Snapshot Capture(Transform root, IEnumerable<GameObject> referenceModels, MyCustomBase owner = null)
    {
        var snapshot = new Snapshot { root = root };
        CaptureAvatar(snapshot);
        var candidates = (referenceModels ?? Enumerable.Empty<GameObject>()).Where(m => m != null).Distinct().ToList();
        // The base model files' transform names: what a base bone is called (clothing bones of a prefab are not).
        var baseNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in candidates.Where(IsModelFile)) foreach (var t in model.GetComponentsInChildren<Transform>(true)) baseNames.Add(t.name);
        OwnSkeleton chosenSkeleton = null;
        // The rest an earlier install of this avatar measured it against.
        var recorded = owner != null && owner.poseReferenceRest != null
            ? owner.poseReferenceRest.Where(b => b != null && b.bone != null && b.bone.IsChildOf(root)).ToList() : new List<PoseReferenceBone>();
        if (recorded.Count > 0)
        {
            var bones = new Dictionary<Transform, ReferenceBone>();
            foreach (var b in recorded)
                if (!bones.ContainsKey(b.bone))
                    bones[b.bone] = new ReferenceBone { rest = new TransformState { position = b.position, rotation = b.rotation, scale = b.scale },
                        restRoot = Affine.FromMatrix(b.rootSpace), parentImage = b.parent };
            if (Choose(snapshot, bones, "the reference recorded at this avatar's first install (" + (owner.poseReference != null ? AssetDatabase.GetAssetPath(owner.poseReference) : "?") + ")"))
                snapshot.referenceModel = owner.poseReference;
        }
        foreach (var model in snapshot.reference.Count > 0 ? new List<GameObject>() : candidates)
        {
            string label = AssetDatabase.GetAssetPath(model);
            Dictionary<Transform, ReferenceBone> bones;
            OwnSkeleton skeleton = null;
            if (!IsModelFile(model)) bones = PrefabBones(root, model, baseNames);
            else
            {
                skeleton = OwnSkeleton.Resolve(root, model.transform);
                if (skeleton.Armature == null) { snapshot.referenceNotes.Add(label + ": no armature found"); continue; }
                var modelSpace = RootSpace(model.transform);
                bones = new Dictionary<Transform, ReferenceBone>();
                foreach (var pair in skeleton.Map)
                {
                    var bone = pair.Value; var source = pair.Key;
                    if (bone == null || bone == root || source == model.transform || bones.ContainsKey(bone)) continue;
                    if (!(bone == skeleton.Armature || bone.IsChildOf(skeleton.Armature)) || skeleton.InOutfitCopy(bone)) continue;
                    if (bone.GetComponent<Renderer>() != null) continue;
                    if (baseNames.Count > 0 && !baseNames.Contains(source.name)) continue;
                    bones[bone] = new ReferenceBone
                    {
                        rest = TransformState.Of(source), restRoot = modelSpace[source], model = source,
                        parentImage = source.parent == model.transform ? root : skeleton[source.parent]
                    };
                }
            }
            if (!Choose(snapshot, bones, label)) continue;
            snapshot.referenceModel = model;
            chosenSkeleton = skeleton;
            break;
        }
        if (snapshot.reference.Count == 0 && (candidates.Count > 0 || recorded.Count > 0))
            MCBLogger.Log("[MCB] None of this avatar's models reproduces its skeleton (" + string.Join("; ", snapshot.referenceNotes)
                + "): the version keeps its own rest pose and no posing is carried over.");
        var canonical = candidates.FirstOrDefault(IsModelFile);
        var own = chosenSkeleton ?? (canonical != null ? OwnSkeleton.Resolve(root, canonical.transform) : OwnSkeleton.ForAvatar(root));
        // The body's own renderers: those standing for the model's renderers, and those on the armature carrying a model's
        // mesh. A mesh of the person's own skinned to the armature (a jacket) is clothing, not the body.
        var modelFiles = new HashSet<string>(candidates.Where(IsModelFile).Select(AssetDatabase.GetAssetPath).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (own.Renderers.ContainsValue(renderer)
                || own.IsOwnRenderer(renderer) && renderer.sharedMesh != null && modelFiles.Contains(AssetDatabase.GetAssetPath(renderer.sharedMesh))
                || renderer.transform.parent == root && baseNames.Contains(renderer.name))
            {
                snapshot.ownRenderers.Add(renderer); snapshot.baseObjects.Add(renderer.transform);
            }
        if (own.Armature != null)
        {
            snapshot.baseObjects.Add(own.Armature);
            foreach (var t in own.Armature.GetComponentsInChildren<Transform>(true))
                if (snapshot.reference.ContainsKey(t) || !own.InOutfitCopy(t) && (baseNames.Count == 0 ? own.Map.ContainsValue(t) : baseNames.Contains(t.name)))
                    snapshot.baseObjects.Add(t);
        }
        // Base-named renderers right under the root (a base piece without bones) are the version's.
        foreach (Transform child in root)
            if (baseNames.Contains(child.name) && child.GetComponent<Renderer>() != null) snapshot.baseObjects.Add(child);
        return snapshot;
    }

    // A candidate reproduces the skeleton when at most half of its bones are adjusted relative to their parent.
    private static bool Choose(Snapshot snapshot, Dictionary<Transform, ReferenceBone> bones, string label)
    {
        int adjusted = Adjusted(snapshot, bones).Count;
        snapshot.referenceNotes.Add($"{label}: {bones.Count} bone(s), {adjusted} adjusted");
        if (bones.Count == 0 || adjusted * 2 > bones.Count) return false;
        snapshot.reference = bones;
        snapshot.referenceLabel = label;
        return true;
    }

    // The avatar as an instance of a prefab: each base bone matched to the prefab's own object (no name lookup).
    private static Dictionary<Transform, ReferenceBone> PrefabBones(Transform root, GameObject prefab, HashSet<string> baseNames)
    {
        var bones = new Dictionary<Transform, ReferenceBone>();
        var modelSpace = RootSpace(prefab.transform);
        var own = OwnSkeleton.ForAvatar(root);
        if (own.Armature == null) return bones;
        foreach (var t in own.Armature.GetComponentsInChildren<Transform>(true))
        {
            if (own.InOutfitCopy(t) || t.GetComponent<Renderer>() != null || baseNames.Count > 0 && !baseNames.Contains(t.name)) continue;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(t);
            if (source == null || source.root != prefab.transform || !modelSpace.TryGetValue(source, out var rest)) continue;
            Transform parentImage = null;
            if (source.parent == prefab.transform) parentImage = root;
            else if (t.parent != null && PrefabUtility.GetCorrespondingObjectFromSource(t.parent) == source.parent) parentImage = t.parent;
            bones[t] = new ReferenceBone { rest = TransformState.Of(source), restRoot = rest, model = source, parentImage = parentImage };
        }
        return bones;
    }

    /// <summary>The chosen reference rest, to keep on the avatar for its later installs.</summary>
    internal static List<PoseReferenceBone> ReferenceRest(Snapshot snapshot) =>
        snapshot.reference.Where(p => p.Key != null).Select(p => new PoseReferenceBone
        {
            bone = p.Key, parent = p.Value.parentImage, position = p.Value.rest.position, rotation = p.Value.rest.rotation, scale = p.Value.rest.scale,
            rootSpace = p.Value.restRoot.ToMatrix()
        }).ToList();

    // A model's hierarchy (a model file, or one built in memory), as opposed to a prefab the avatar is an instance of.
    private static bool IsModelFile(GameObject model) =>
        !AssetDatabase.GetAssetPath(model).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

    // The person's adjustment of a base bone in root space: P * R^-1.
    private static Affine Delta(Snapshot snapshot, Transform bone, ReferenceBone rest) =>
        snapshot.rootSpace.TryGetValue(bone, out var person) ? person * rest.restRoot.Inverse() : Affine.Identity();

    // Bones adjusted relative to their parent (their own adjustment, not one inherited from an adjusted parent).
    private static List<Transform> Adjusted(Snapshot snapshot, Dictionary<Transform, ReferenceBone> bones)
    {
        var deltas = bones.ToDictionary(p => p.Key, p => Delta(snapshot, p.Key, p.Value));
        var result = new List<Transform>();
        foreach (var pair in deltas)
        {
            Affine parent = null;
            for (var node = pair.Key.parent; node != null && node != snapshot.root && parent == null; node = node.parent)
                deltas.TryGetValue(node, out parent);
            if (!SamePose(pair.Value, parent ?? Affine.Identity())) result.Add(pair.Key);
        }
        return result;
    }

    /// <summary>
    /// The candidate models the avatar's skeleton comes from, best first: a migrated custom body's own models (recorded at its migration), the models its base renderers' meshes come from, its
    /// base models, and last the prefab the avatar is an instance of (its authored pose, when no model file has it).
    /// </summary>
    public static List<GameObject> ReferenceModels(MyCustomBase owner, Transform root)
    {
        var models = new List<GameObject>();
        void AddObject(GameObject model) { if (model != null && !models.Contains(model)) models.Add(model); }
        void Add(UnityEngine.Object asset)
        {
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            AddObject(AssetDatabase.LoadAssetAtPath<GameObject>(path));
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
        // The other models the avatar's renderers come from are accessories (hair, a hoodie): never the body's reference.
        if (PrefabUtility.GetNearestPrefabInstanceRoot(root.gameObject) == root.gameObject)
        {
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root.gameObject);
            if (!string.IsNullOrEmpty(path)) AddObject(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }
        return models;
    }

    /// <summary>
    /// After the payload and the base pose: each base bone gets the person's adjustment on top of what the version wrote,
    /// in root space: target = (P * R^-1) * C, converted to local values parents first. A bone the version left at the
    /// reference rest (its ancestors too) gets the person's local pose bit-exactly; a computed value equal to what is there
    /// within <see cref="NoiseTolerance"/> is not written; nothing is written when the person adjusted nothing.
    /// </summary>
    public static int CarryPose(Snapshot snapshot)
    {
        if (snapshot == null || snapshot.reference.Count == 0) { WriteDiagnostics(snapshot, null, null); return 0; }
        var root = snapshot.root;
        var installed = RootSpace(root);
        var deltas = snapshot.reference.Where(p => p.Key != null).ToDictionary(p => p.Key, p => Delta(snapshot, p.Key, p.Value));
        if (deltas.Values.All(d => SamePose(d, Affine.Identity()))) { WriteDiagnostics(snapshot, installed, null); return 0; }
        var installedLocal = snapshot.reference.Keys.Where(t => t != null).ToDictionary(t => t, TransformState.Of);
        var atRest = new Dictionary<Transform, bool>();
        bool AtRest(Transform t)
        {
            if (atRest.TryGetValue(t, out bool known)) return known;
            var rest = snapshot.reference[t];
            bool result = t.parent == rest.parentImage && SameLocalRobust(installedLocal[t], rest.rest)
                          && (t.parent == root || snapshot.reference.ContainsKey(t.parent) && AtRest(t.parent));
            atRest[t] = result;
            return result;
        }
        var final = new Dictionary<Transform, Affine> { [root] = Affine.Identity() };
        int carried = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            var parentFinal = t.parent != null && final.TryGetValue(t.parent, out var pf) ? pf : Affine.Identity();
            if (snapshot.reference.ContainsKey(t) && snapshot.transforms.TryGetValue(t, out var person))
            {
                if (AtRest(t))
                {
                    if (!person.SamePoseExactly(t))
                    {
                        Undo.RecordObject(t, "Keep the avatar's pose");
                        t.localPosition = person.position; t.localRotation = person.rotation; t.localScale = person.scale;
                        EditorUtility.SetDirty(t);
                        carried++;
                    }
                }
                else
                {
                    var target = deltas[t] * installed[t];
                    var local = parentFinal.Inverse() * target;
                    if (WriteLocal(t, local, "Keep the avatar's pose")) carried++;
                }
            }
            final[t] = parentFinal * Affine.Local(t);
        }
        WriteDiagnostics(snapshot, installed, final);
        return carried;
    }

    // Writes a local pose unless it equals the current one within the noise tolerance.
    private static bool WriteLocal(Transform t, Affine local, string step)
    {
        local.Decompose(out var p, out var q, out var s, t.localRotation);
        var position = t.localPosition; var rotation = t.localRotation; var scale = t.localScale;
        bool same = Math.Abs(p[0] - position.x) <= NoiseTolerance && Math.Abs(p[1] - position.y) <= NoiseTolerance && Math.Abs(p[2] - position.z) <= NoiseTolerance
                    && Math.Abs(q[0] - rotation.x) <= NoiseTolerance && Math.Abs(q[1] - rotation.y) <= NoiseTolerance
                    && Math.Abs(q[2] - rotation.z) <= NoiseTolerance && Math.Abs(q[3] - rotation.w) <= NoiseTolerance
                    && Math.Abs(s[0] - scale.x) <= NoiseTolerance * Math.Max(1, Math.Abs(scale.x))
                    && Math.Abs(s[1] - scale.y) <= NoiseTolerance * Math.Max(1, Math.Abs(scale.y))
                    && Math.Abs(s[2] - scale.z) <= NoiseTolerance * Math.Max(1, Math.Abs(scale.z));
        if (same) return false;
        Undo.RecordObject(t, step);
        t.localPosition = new Vector3((float)p[0], (float)p[1], (float)p[2]);
        t.localRotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        t.localScale = new Vector3((float)s[0], (float)s[1], (float)s[2]);
        EditorUtility.SetDirty(t);
        return true;
    }

    private static void WriteDiagnostics(Snapshot snapshot, Dictionary<Transform, Affine> installed, Dictionary<Transform, Affine> final)
    {
        if (string.IsNullOrEmpty(DiagnosticsPathForTests) || snapshot == null) return;
        try
        {
            var root = snapshot.root;
            object Local(TransformState s) => s == null ? null : new { p = new[] { s.position.x, s.position.y, s.position.z }, r = new[] { s.rotation.x, s.rotation.y, s.rotation.z, s.rotation.w }, s = new[] { s.scale.x, s.scale.y, s.scale.z } };
            var bones = snapshot.reference.Where(p => p.Key != null).Select(p => new
            {
                path = OwnSkeleton.Path(root, p.Key),
                model = snapshot.referenceLabel, modelPath = p.Value.model != null ? OwnSkeleton.Path(p.Value.model.root, p.Value.model) : null,
                P = Local(snapshot.transforms.TryGetValue(p.Key, out var person) ? person : null), R = Local(p.Value.rest),
                result = Local(TransformState.Of(p.Key)),
                P_root = snapshot.rootSpace.TryGetValue(p.Key, out var pr) ? pr.m : null, R_root = p.Value.restRoot.m,
                C_root = installed != null && installed.TryGetValue(p.Key, out var c) ? c.m : null,
                F_root = final != null && final.TryGetValue(p.Key, out var f) ? f.m : null
            }).ToList();
            System.IO.File.WriteAllText(DiagnosticsPathForTests, Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                reference = snapshot.referenceLabel,
                notes = snapshot.referenceNotes, bones
            }));
        }
        catch (Exception ex) { MCBLogger.LogWarning("[MCB] Pose diagnostics could not be written: " + ex.Message); }
    }

    /// <summary>
    /// After the carry: every object that is not the version's own (props, clothing bones, outfits, accessories), active
    /// or not, keeps the place it had relative to the avatar root, whatever the version did to the bone it hangs from.
    /// </summary>
    public static int KeepPlaces(Snapshot snapshot)
    {
        if (snapshot == null || snapshot.root == null) return 0;
        var root = snapshot.root;
        var current = new Dictionary<Transform, Affine> { [root] = Affine.Identity() };
        int kept = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            var parent = t.parent != null && current.TryGetValue(t.parent, out var pc) ? pc : Affine.Identity();
            if (snapshot.transforms.TryGetValue(t, out var was) && t.parent == was.parent && !snapshot.reference.ContainsKey(t) && !snapshot.baseObjects.Contains(t)
                && snapshot.rootSpace.TryGetValue(t, out var place))
            {
                var now = parent * Affine.Local(t);
                if (!SamePose(now, place) && WriteLocal(t, parent.Inverse() * place, "Keep objects on the body")) kept++;
            }
            current[t] = parent * Affine.Local(t);
        }
        return kept;
    }

    public const string ReboundFolder = "Assets/MCB/rebound";

    /// <summary>
    /// After <see cref="KeepPlaces"/>: a mesh that is not the version's (clothing, a hoodie, hair) skinned to a bone whose
    /// root-space pose the version changed keeps its world shape: its bind pose for each such bone becomes
    /// installed^-1 * before * bind pose. The mesh is one copy per avatar and source mesh at a fixed path in
    /// <see cref="ReboundFolder"/>, always computed from the original mesh and reused by later installs.
    /// </summary>
    public static int RebindClothing(Snapshot snapshot)
    {
        if (snapshot == null || snapshot.root == null) return 0;
        var root = snapshot.root;
        var now = RootSpace(root);
        int rebound = 0;
        foreach (var pair in snapshot.renderers)
        {
            var renderer = pair.Key;
            if (renderer == null || snapshot.ownRenderers.Contains(renderer)) continue;
            var mesh = renderer.sharedMesh;
            if (mesh == null || mesh != pair.Value.mesh || IsRebound(mesh)) continue;
            var bones = renderer.bones;
            var poses = mesh.bindposes;
            if (poses.Length != bones.Length) continue;
            bool changed = false;
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null || !snapshot.rootSpace.TryGetValue(bone, out var before) || !now.TryGetValue(bone, out var after) || SamePose(before, after)) continue;
                poses[i] = (after.Inverse() * before * Affine.FromMatrix(poses[i])).ToMatrix();
                changed = true;
            }
            if (!changed) continue;
            var copy = ReboundCopy(root, renderer, mesh, poses);
            Undo.RecordObject(renderer, "Keep meshes on the body");
            renderer.sharedMesh = copy;
            EditorUtility.SetDirty(renderer);
            rebound++;
        }
        return rebound;
    }

    private static Mesh ReboundCopy(Transform root, SkinnedMeshRenderer renderer, Mesh original, Matrix4x4[] poses)
    {
        string key = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out string guid, out long id) && !string.IsNullOrEmpty(guid)
            ? guid + ":" + id : "scene:" + original.name;
        key += "|" + OwnSkeleton.Path(root, renderer.transform);
        string folder = ReboundFolder + "/" + Safe(root.name);
        EnsureFolder(folder);
        string name = Safe(original.name);
        if (name.Length == 0) name = Safe(renderer.name);
        string path = folder + "/" + (name.Length == 0 ? "" : name + " ") + Fnv(key) + ".asset";
        var copy = UnityEngine.Object.Instantiate(original);
        copy.name = original.name;
        copy.bindposes = poses;
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }
        // The same copy as before, updated in place: an Undo step that still points at it stays valid.
        Undo.RecordObject(existing, "Keep meshes on the body");
        EditorUtility.CopySerialized(copy, existing);
        existing.name = original.name;
        UnityEngine.Object.DestroyImmediate(copy);
        EditorUtility.SetDirty(existing);
        AssetDatabase.SaveAssetIfDirty(existing);
        return existing;
    }

    private static string Fnv(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) { hash ^= c; hash *= 16777619; }
        return hash.ToString("x8");
    }

    private static string Safe(string name) => string.Concat((name ?? "Avatar").Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = MCBUtils.ToUnityPath(System.IO.Path.GetDirectoryName(folder));
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
    }

    /// <summary>
    /// What a reinstall's going-back half leaves as the transition and a custom body's migration have it (the base renderers
    /// back on the original model's meshes, a migration's renderers and switched-off objects): the new install keeps the
    /// first install's baseline for them.
    /// </summary>
    internal sealed class MigrationBaseline
    {
        public HashSet<SkinnedMeshRenderer> renderers = new HashSet<SkinnedMeshRenderer>();
        public HashSet<GameObject> objects = new HashSet<GameObject>();
        public List<PoseLedgerRenderer> rendererEntries = new List<PoseLedgerRenderer>();
        public List<PoseLedgerEntry> objectEntries = new List<PoseLedgerEntry>();
        public List<(UnityEngine.Object owner, string path, string token)> missing = new List<(UnityEngine.Object, string, string)>();
        public string session;
        // The Animator's Avatar before the first install (none included).
        public bool animatorRecorded; public Avatar animatorAvatar;
    }

    internal static MigrationBaseline TakeMigrationBaseline(MyCustomBase owner, Transform root, IEnumerable<string> baseModelPaths)
    {
        if (owner == null || owner.poseLedger == null || !owner.poseLedger.recorded) return null;
        var baseline = new MigrationBaseline { session = owner.poseLedger.session,
            animatorRecorded = owner.poseLedger.animatorRecorded, animatorAvatar = owner.poseLedger.animatorAvatar };
        if (owner.legacyOriginal != null && owner.legacyOriginal.migrated)
        {
            foreach (var state in owner.legacyOriginal.renderers ?? new List<LegacyRendererState>()) if (state?.renderer != null) baseline.renderers.Add(state.renderer);
            foreach (var state in owner.legacyOriginal.objects ?? new List<LegacyObjectState>()) if (state?.target != null) baseline.objects.Add(state.target);
        }
        // The base renderers the transition already put back on the original model's meshes (a custom body's piece whose
        // own mesh is missing included): the new install starts from them as the transition left them.
        var models = new HashSet<string>((baseModelPaths ?? Enumerable.Empty<string>()).Where(path => !string.IsNullOrEmpty(path)).Select(MCBUtils.ToUnityPath), StringComparer.OrdinalIgnoreCase);
        if (root != null)
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer.sharedMesh != null && models.Contains(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(renderer.sharedMesh)))) baseline.renderers.Add(renderer);
        baseline.rendererEntries = owner.poseLedger.renderers.Where(r => r?.renderer != null && baseline.renderers.Contains(r.renderer)).ToList();
        baseline.objectEntries = owner.poseLedger.entries.Where(e => e?.transform != null && baseline.objects.Contains(e.transform.gameObject)).ToList();
        var ledger = owner.poseLedger;
        for (int i = 0; ledger.missingOwners != null && i < ledger.missingOwners.Count && i < ledger.missingPaths.Count && i < ledger.missingTokens.Count; i++)
            if (ledger.missingOwners[i] is Component c && Owned(baseline, c)) baseline.missing.Add((c, ledger.missingPaths[i], ledger.missingTokens[i]));
        return baseline;
    }

    private static bool Owned(MigrationBaseline baseline, Component component) =>
        baseline != null && component != null && (component is SkinnedMeshRenderer smr && baseline.renderers.Contains(smr) || baseline.objects.Contains(component.gameObject));

    /// <summary>Once the install has completely finished: what it left different goes in the ledger.</summary>
    public static void Record(MyCustomBase owner, Snapshot snapshot, MigrationBaseline inherited = null)
    {
        var ledger = new PoseLedger { recorded = true, session = EditorSession(), animatorRecorded = snapshot.hasAnimator, animatorAvatar = snapshot.animatorAvatar };
        var root = snapshot.root;
        var now = RootSpace(root);
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
                writtenPosition = t.localPosition, writtenRotation = t.localRotation, writtenScale = t.localScale,
                bone = snapshot.baseObjects.Contains(t) || snapshot.reference.ContainsKey(t),
                baselineSpace = snapshot.rootSpace.TryGetValue(t, out var was) ? was.ToMatrix() : Matrix4x4.identity,
                writtenSpace = now.TryGetValue(t, out var written) ? written.ToMatrix() : Matrix4x4.identity
            });
        }
        foreach (var pair in snapshot.renderers)
        {
            var r = pair.Key; var b = pair.Value;
            if (r == null || b.Same(r)) continue;
            ledger.renderers.Add(new PoseLedgerRenderer { renderer = r, mesh = b.mesh, rootBone = b.rootBone, bones = b.bones, bounds = b.bounds,
                enabled = b.enabled, updateWhenOffscreen = b.updateWhenOffscreen, weights = b.weights });
        }
        foreach (var entry in snapshot.missing.Where(m => m.owner != null))
        {
            ledger.missingOwners.Add(entry.owner); ledger.missingPaths.Add(entry.path); ledger.missingTokens.Add(entry.token);
        }
        // A reinstall on a migrated custom body: the first install's baseline stays the truth for what the migration owns.
        if (inherited != null)
        {
            // The Animator's Avatar from before the first install, not the one the transition put back meanwhile.
            ledger.animatorRecorded = inherited.animatorRecorded; ledger.animatorAvatar = inherited.animatorAvatar;
            ledger.renderers.RemoveAll(r => r.renderer != null && inherited.renderers.Contains(r.renderer));
            ledger.renderers.AddRange(inherited.rendererEntries);
            foreach (var entry in inherited.objectEntries)
            {
                var existing = ledger.entries.FirstOrDefault(e => e.transform == entry.transform);
                if (existing != null) existing.active = entry.active;
                else ledger.entries.Add(entry);
            }
            foreach (var entry in inherited.missing)
                if (!ledger.missingOwners.Where((o, i) => o == entry.owner && ledger.missingPaths[i] == entry.path).Any())
                {
                    ledger.missingOwners.Add(entry.owner); ledger.missingPaths.Add(entry.path); ledger.missingTokens.Add(entry.token);
                }
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
        var ledger = owner.poseLedger;
        bool sameSession = !string.IsNullOrEmpty(ledger.session) && ledger.session == EditorSession();
        before.missing.Clear();
        for (int i = 0; ledger.missingOwners != null && i < ledger.missingOwners.Count && i < ledger.missingPaths.Count && i < ledger.missingTokens.Count; i++)
            if (ledger.missingOwners[i] is Component component) before.missing.Add((component, ledger.missingPaths[i], ledger.missingTokens[i]));
        return new PendingRestore
        {
            before = before, sameSession = sameSession,
            animatorRecorded = ledger.animatorRecorded, animatorAvatar = ledger.animatorAvatar,
            entries = ledger.entries.Where(e => e != null && e.transform != null).ToList(),
            renderers = ledger.renderers.Where(r => r != null && r.renderer != null).ToList()
        };
    }

    /// <summary>
    /// After the reset machinery removed the version: every ledger transform back to its parent, order, active state and
    /// local pose from before the install (bit-exact when the person did not touch it). An object the person moved while
    /// the version was on keeps the place relative to the avatar root it had just before going back; a bone they adjusted
    /// keeps that adjustment on top of its baseline. Every other transform stays as it was before going back; renderers
    /// likewise; then the ledger is cleared. <paramref name="migration"/>: a reinstall's going-back half, which leaves what
    /// a custom body's migration owns as the migration has it.
    /// </summary>
    public static void FinishRestore(MyCustomBase owner, PendingRestore pending, MigrationBaseline migration = null)
    {
        if (owner == null || pending == null) return;
        const string step = "Put the avatar back as it was";
        var root = pending.before.root;
        var wanted = new Dictionary<Transform, TransformState>();
        var ledgered = new HashSet<Transform>();
        var moved = new List<PoseLedgerEntry>();
        foreach (var e in pending.entries)
        {
            var t = e.transform;
            if (t == null) continue;
            ledgered.Add(t);
            pending.before.transforms.TryGetValue(t, out var now);
            var state = new TransformState { parent = e.parent, sibling = e.sibling, active = e.active, position = e.position, rotation = e.rotation, scale = e.scale };
            if (migration != null && migration.objects.Contains(t.gameObject) && now != null) state.active = now.active;
            if (now != null)
            {
                // Moved elsewhere by the person meanwhile: their parent stays.
                if (now.parent != e.writtenParent || e.parent == null) state.parent = now.parent;
                if (now.active != e.writtenActive) state.active = now.active;
                if (!(Same(now.position, e.writtenPosition) && Same(now.rotation, e.writtenRotation) && Same(now.scale, e.writtenScale)))
                {
                    if (e.bone)
                    {
                        state.position = e.position + (now.position - e.writtenPosition);
                        state.rotation = (e.rotation * (Quaternion.Inverse(e.writtenRotation) * now.rotation)).normalized;
                        state.scale = Vector3.Scale(e.scale, Ratio(now.scale, e.writtenScale));
                    }
                    else moved.Add(e);
                }
            }
            wanted[t] = state;
        }
        foreach (var pair in pending.before.transforms)
            if (pair.Key != null && !ledgered.Contains(pair.Key)) wanted[pair.Key] = pair.Value;
        var ledgerRenderers = pending.renderers.Where(r => r.renderer != null && (migration == null || !migration.renderers.Contains(r.renderer)))
            .GroupBy(r => r.renderer).ToDictionary(g => g.Key, g => g.First());
        var renderers = new Dictionary<SkinnedMeshRenderer, RendererState>();
        foreach (var pair in ledgerRenderers)
            renderers[pair.Key] = new RendererState { mesh = pair.Value.mesh, rootBone = pair.Value.rootBone, bones = pair.Value.bones, bounds = pair.Value.bounds,
                enabled = pair.Value.enabled, updateWhenOffscreen = pair.Value.updateWhenOffscreen, weights = pair.Value.weights };
        foreach (var pair in pending.before.renderers)
            if (pair.Key != null && !renderers.ContainsKey(pair.Key) && (migration == null || !migration.renderers.Contains(pair.Key))) renderers[pair.Key] = pair.Value;
        Put(wanted, renderers, step);
        // Objects the person moved while the version was on: where they put them, relative to the avatar root.
        foreach (var e in moved.OrderBy(e => Depth(e.transform)))
        {
            if (e.transform == null || !pending.before.rootSpace.TryGetValue(e.transform, out var place)) continue;
            var parent = e.transform.parent == null ? Affine.Identity() : RootSpaceOf(root, e.transform.parent);
            WriteLocal(e.transform, parent.Inverse() * place, step);
        }
        PutMissing(pending.before.missing.Where(m => !Owned(migration, m.owner)), pending.sameSession);
        if (migration == null && pending.animatorRecorded)
        {
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.avatar != pending.animatorAvatar)
            {
                Undo.RecordObject(animator, step);
                AvatarDefinitionGenerationService.AssignAvatarKeepingPose(animator, pending.animatorAvatar);
                EditorUtility.SetDirty(animator);
            }
        }

        Undo.RecordObject(owner, step);
        owner.poseLedger = new PoseLedger();
        EditorUtility.SetDirty(owner);
    }

    private static int Depth(Transform t) { int d = 0; for (var n = t; n != null; n = n.parent) d++; return d; }

    private static Vector3 Ratio(Vector3 a, Vector3 b) =>
        new Vector3(b.x != 0f ? a.x / b.x : 1f, b.y != 0f ? a.y / b.y : 1f, b.z != 0f ? a.z / b.z : 1f);

    /// <summary>
    /// The Animator caches each bone's default pose when it binds: once every final value is written, it binds again on
    /// exactly that pose (a later Rebind, Avatar change or re-activation then changes nothing).
    /// </summary>
    public static void BindAnimatorLast(Transform root)
    {
        var animator = root != null ? root.GetComponent<Animator>() : null;
        if (animator == null || animator.avatar == null) return;
        AvatarDefinitionGenerationService.AssignAvatarKeepingPose(animator, animator.avatar);
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
        PutMissing(snapshot.missing);
        var animator = snapshot.root != null ? snapshot.root.GetComponent<Animator>() : null;
        if (animator != null && snapshot.hasAnimator && animator.avatar != snapshot.animatorAvatar)
            AvatarDefinitionGenerationService.AssignAvatarKeepingPose(animator, snapshot.animatorAvatar);
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
        // A renderer that had no mesh (or a missing one) is emptied again instead of keeping the base's mesh, and a
        // clothing mesh MCB rebound for the version gets its own mesh back. The rebound copy itself stays: an Undo step
        // can still point at it, and the next install updates it in place.
        foreach (var pair in renderers)
        {
            var renderer = pair.Key; var state = pair.Value;
            if (renderer == null) continue;
            if (state.mesh == null && renderer.sharedMesh != null)
            {
                // No mesh (or a missing one, which PutMissing puts back) instead of the base's.
                Undo.RecordObject(renderer, step);
                renderer.sharedMesh = null;
            }
            else if (state.mesh != null && renderer.sharedMesh != null && renderer.sharedMesh != state.mesh && IsRebound(renderer.sharedMesh))
            {
                Undo.RecordObject(renderer, step);
                renderer.sharedMesh = state.mesh;
            }
            if (renderer.sharedMesh == state.mesh) Apply(renderer, state, step);
        }
    }

    internal static bool IsRebound(Mesh mesh)
    {
        string path = AssetDatabase.GetAssetPath(mesh);
        return path.StartsWith(ReboundFolder + "/", StringComparison.OrdinalIgnoreCase);
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
        // Blendshape weights as they were (missing material references come back with PutMissing).
        if (state.weights != null && renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount == state.weights.Length)
            for (int i = 0; i < state.weights.Length; i++)
                if (!renderer.GetBlendShapeWeight(i).Equals(state.weights[i])) renderer.SetBlendShapeWeight(i, state.weights[i]);
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
