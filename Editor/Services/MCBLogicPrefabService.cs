#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;
using VRC.Dynamics;

/// <summary>
/// Creator authoring: builds a version's logic prefab and custom model prefab from an existing avatar setup.
/// The logic prefab keeps the reference avatar's contacts at the same place, each one following its bone through a
/// "Target Bones" proxy that a VRCFury Armature Link merges into the avatar's bone, and merges the given FX controller
/// with a VRCFury Full Controller. Contacts held by a single-source Parent Constraint move under their source bone's
/// proxy instead, so the constraint is no longer needed.
/// </summary>
public static class MCBLogicPrefabService
{
    public const string LogicRootName = "mcb logic";
    public const string TargetBonesName = "Target Bones";
    private const float PlacementTolerance = 0.00001f;

    public sealed class LogicSpec
    {
        // Prefab with the avatar's transforms and the contacts to keep (other components are ignored).
        public string reference;
        // Logic prefab to write; replaced when it exists.
        public string output;
        // Reference object paths whose contacts are kept (all contacts when empty), and paths left out.
        public List<string> contactRoots = new List<string>();
        public List<string> skip = new List<string>();
        // Name of the object holding the converted contacts.
        public string contactsGroup = "Flex Objects";
        public string controller, menu, parameters, menuPrefix;
        public List<string> globalParams = new List<string>();
        public bool allNonsyncedAreGlobal;
        // Custom model prefab: controller clip bindings that neither it, the reference nor the logic resolves are removed
        // from the clips (run it on extracted copies, never on assets other avatars use).
        public string pruneAgainst;
    }

    public sealed class LogicResult
    {
        public string output;
        public int contacts, targetBones, layers;
        public float maximumPlacementError;
        public List<string> inactive = new List<string>();
        public List<string> removedBindings = new List<string>();
        public List<string> pathRewrites = new List<string>();
    }

    public sealed class CustomModelSpec
    {
        // The custom FBX (or a prefab of it), and the original base FBX whose renderer paths the custom model must use.
        public string model, original;
        public string output;
        // Custom renderer path -> original renderer path, for renderers the custom model names differently.
        public Dictionary<string, string> renames = new Dictionary<string, string>();
    }

    public static LogicResult BuildLogic(LogicSpec spec)
    {
        if (spec == null || string.IsNullOrWhiteSpace(spec.reference) || string.IsNullOrWhiteSpace(spec.output))
            throw new ArgumentException("Provide the reference prefab and the output prefab path.");
        RequireAssetPath(spec.output, ".prefab");
        var controller = Load<RuntimeAnimatorController>(spec.controller, "FX controller");
        var menu = Load<ScriptableObject>(spec.menu, "menu");
        var parameters = Load<ScriptableObject>(spec.parameters, "parameters");
        var reference = PrefabUtility.LoadPrefabContents(spec.reference);
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject logic = null;
        var result = new LogicResult { output = spec.output };
        try
        {
            logic = new GameObject(LogicRootName);
            SceneManager.MoveGameObjectToScene(logic, scene);
            var targets = Child(logic.transform, TargetBonesName);
            var group = Child(logic.transform, string.IsNullOrWhiteSpace(spec.contactsGroup) ? "Contacts" : spec.contactsGroup);
            var proxies = new Dictionary<Transform, Transform>();
            Transform Proxy(Transform bone)
            {
                if (bone == null || bone == reference.transform) throw new InvalidOperationException("A contact follows the avatar root: give it a bone.");
                if (proxies.TryGetValue(bone, out var cached)) return cached;
                string name = bone.name;
                for (int i = 2; targets.Find(name) != null; i++) name = bone.name + " " + i;
                var proxy = Child(targets, name);
                proxy.SetPositionAndRotation(bone.position, bone.rotation);
                proxy.localScale = bone.lossyScale;
                if (VRCFuryService.Instance.AddArmatureLink(proxy.gameObject, Path(bone, reference.transform)) == null)
                    throw new InvalidOperationException("VRCFury is required for logic prefabs.");
                proxies.Add(bone, proxy);
                return proxy;
            }
            bool Kept(Transform t)
            {
                string path = Path(t, reference.transform);
                if (spec.skip.Any(s => Within(path, s))) return false;
                return spec.contactRoots.Count == 0 || spec.contactRoots.Any(r => Within(path, r));
            }

            var pathMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = reference.GetComponentsInChildren<Transform>(true).GroupBy(t => Path(t, reference.transform)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var source in reference.GetComponentsInChildren<ContactBase>(true).Select(c => c.gameObject).Distinct().Where(g => Kept(g.transform)).ToList())
            {
                var constraint = source.GetComponent<ParentConstraint>();
                Transform holder = group;
                // A saved pose can predate the constraint's offsets: evaluate it like Unity does (source pose, then the
                // offsets in the source's unscaled frame).
                Vector3 position = source.transform.position;
                Quaternion rotation = source.transform.rotation;
                if (constraint != null && constraint.constraintActive)
                {
                    var sources = new List<ConstraintSource>();
                    constraint.GetSources(sources);
                    var used = sources.Select((s, i) => (s, i)).Where(p => p.s.weight > 0).ToList();
                    if (used.Count != 1 || used[0].s.sourceTransform == null)
                        throw new InvalidOperationException("Give '" + Path(source.transform, reference.transform) + "' exactly one Parent Constraint source.");
                    var bone = used[0].s.sourceTransform;
                    holder = Proxy(bone);
                    position = bone.position + bone.rotation * constraint.GetTranslationOffset(used[0].i);
                    rotation = bone.rotation * Quaternion.Euler(constraint.GetRotationOffset(used[0].i));
                    source.transform.SetPositionAndRotation(position, rotation);
                }
                else if (source.GetComponents<ContactBase>().Any(c => c.rootTransform == null))
                    holder = Proxy(source.transform.parent);
                var copy = UnityEngine.Object.Instantiate(source, holder);
                // Siblings keep distinct names (the reference may repeat one, e.g. a left and a right receiver).
                string name = source.name;
                for (int n = 2; holder.Cast<Transform>().Any(t => t != copy.transform && t.name == name); n++) name = source.name + " " + n;
                copy.name = name;
                // Children with their own contacts are converted on their own.
                foreach (Transform child in copy.transform.Cast<Transform>().ToList()) UnityEngine.Object.DestroyImmediate(child.gameObject);
                foreach (var component in copy.GetComponents<IConstraint>().OfType<Component>().ToList()) UnityEngine.Object.DestroyImmediate(component);
                copy.transform.SetPositionAndRotation(position, rotation);
                var holderScale = holder.lossyScale;
                var scale = source.transform.lossyScale;
                copy.transform.localScale = new Vector3(scale.x / holderScale.x, scale.y / holderScale.y, scale.z / holderScale.z);
                var before = source.GetComponents<ContactBase>();
                var after = copy.GetComponents<ContactBase>();
                for (int i = 0; i < before.Length; i++)
                {
                    if (before[i].rootTransform != null) after[i].rootTransform = Proxy(before[i].rootTransform);
                    float error = Vector3.Distance(Center(before[i]), Center(after[i]));
                    result.maximumPlacementError = Mathf.Max(result.maximumPlacementError, error);
                    if (error > PlacementTolerance)
                        throw new InvalidOperationException("Contact placement changed for '" + source.name + "': " + error + " m.");
                    result.contacts++;
                }
                if (!source.activeInHierarchy) result.inactive.Add(Path(source.transform, reference.transform));
                // Animation paths are rewritten only for objects the reference names unambiguously.
                string from = Path(source.transform, reference.transform), to = Path(copy.transform, logic.transform);
                if (paths[from] == 1) pathMap[from] = to;
            }
            result.targetBones = proxies.Count;

            if (controller != null || menu != null || parameters != null)
            {
                var full = new VRCFuryService.FullControllerSpec
                {
                    Controller = controller, Menu = menu, Parameters = parameters, MenuPrefix = spec.menuPrefix ?? "",
                    AllNonsyncedAreGlobal = spec.allNonsyncedAreGlobal, GlobalParams = spec.globalParams.ToList()
                };
                // The controller's clips were written for the reference avatar's object paths.
                foreach (var pair in pathMap.Where(p => p.Key != p.Value))
                {
                    full.PathRewrites.Add(pair);
                    result.pathRewrites.Add(pair.Key + " -> " + pair.Value);
                }
                if (VRCFuryService.Instance.AddFullController(logic, full) == null) throw new InvalidOperationException("VRCFury is required for logic prefabs.");
            }
            if (controller is AnimatorController animator)
            {
                result.layers = animator.layers.Length;
                if (!string.IsNullOrWhiteSpace(spec.pruneAgainst))
                    result.removedBindings = PruneBindings(animator, Load<GameObject>(spec.pruneAgainst, "custom model"), reference, logic, pathMap);
            }
            EnsureFolder(System.IO.Path.GetDirectoryName(spec.output));
            PrefabUtility.SaveAsPrefabAsset(logic, spec.output, out bool saved);
            if (!saved) throw new InvalidOperationException("Could not save " + spec.output);
            AssetDatabase.SaveAssets();
            return result;
        }
        finally
        {
            if (logic != null) UnityEngine.Object.DestroyImmediate(logic);
            EditorSceneManager.ClosePreviewScene(scene);
            PrefabUtility.UnloadPrefabContents(reference);
        }
    }

    /// <summary>
    /// The custom model of a version: the custom FBX's renderers that the original base also has (by path, after
    /// <see cref="CustomModelSpec.renames"/>), without materials and with every blendshape at 0. Version defaults set the
    /// shapes; materials stay the user's.
    /// </summary>
    public static object PrepareCustomModel(CustomModelSpec spec)
    {
        if (spec == null || string.IsNullOrWhiteSpace(spec.output)) throw new ArgumentException("Provide model, original and output.");
        RequireAssetPath(spec.output, ".prefab");
        var source = Load<GameObject>(spec.model, "custom model") ?? throw new ArgumentException("Provide the custom model.");
        var original = Load<GameObject>(spec.original, "original base") ?? throw new ArgumentException("Provide the original base model.");
        var originalPaths = new HashSet<string>(original.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => Path(r.transform, original.transform)), StringComparer.Ordinal);
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject model = null;
        try
        {
            model = UnityEngine.Object.Instantiate(source);
            model.name = System.IO.Path.GetFileNameWithoutExtension(spec.output);
            SceneManager.MoveGameObjectToScene(model, scene);
            foreach (var rename in spec.renames ?? new Dictionary<string, string>())
            {
                var target = model.transform.Find(rename.Key) ?? throw new ArgumentException("The custom model has no '" + rename.Key + "'.");
                target.name = rename.Value.Split('/').Last();
            }
            var kept = new List<object>();
            var removed = new List<string>();
            foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string path = Path(renderer.transform, model.transform);
                if (!originalPaths.Contains(path)) { removed.Add(path); UnityEngine.Object.DestroyImmediate(renderer.gameObject); continue; }
                renderer.sharedMaterials = new Material[renderer.sharedMesh.subMeshCount];
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++) renderer.SetBlendShapeWeight(i, 0);
                kept.Add(new { path, vertices = renderer.sharedMesh.vertexCount, blendshapes = renderer.sharedMesh.blendShapeCount, bones = renderer.bones.Length });
            }
            if (kept.Count == 0) throw new InvalidOperationException("No custom renderer matches a renderer path of the original base.");
            EnsureFolder(System.IO.Path.GetDirectoryName(spec.output));
            PrefabUtility.SaveAsPrefabAsset(model, spec.output, out bool saved);
            if (!saved) throw new InvalidOperationException("Could not save " + spec.output);
            AssetDatabase.SaveAssets();
            return new { output = spec.output, renderers = kept, removed };
        }
        finally
        {
            if (model != null) UnityEngine.Object.DestroyImmediate(model);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    // Removes bindings to objects, components or blendshapes that exist on none of the avatar parts the logic runs on.
    private static List<string> PruneBindings(AnimatorController controller, GameObject model, GameObject reference, GameObject logic, Dictionary<string, string> pathMap)
    {
        if (model == null) throw new ArgumentException("pruneAgainst must be a model or prefab.");
        var removed = new List<string>();
        bool Resolves(EditorCurveBinding binding)
        {
            foreach (var (root, path) in new[] { (model.transform, binding.path), (reference.transform, binding.path),
                         (logic.transform, pathMap.TryGetValue(binding.path, out string moved) ? moved : null) })
            {
                if (path == null) continue;
                var target = path.Length == 0 ? root : root.Find(path);
                if (target == null) continue;
                if (binding.type == typeof(GameObject) || binding.type == typeof(Transform)) return true;
                var component = target.GetComponent(binding.type);
                if (component == null) continue;
                if (!binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) return true;
                var mesh = (component as SkinnedMeshRenderer)?.sharedMesh;
                if (mesh != null && mesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) >= 0) return true;
            }
            return false;
        }
        foreach (var clip in controller.animationClips.Distinct())
        {
            bool changed = false;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(b => !Resolves(b)))
            {
                AnimationUtility.SetEditorCurve(clip, binding, null);
                removed.Add(clip.name + ": " + binding.path + "/" + binding.propertyName);
                changed = true;
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(b => !Resolves(b)))
            {
                AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                removed.Add(clip.name + ": " + binding.path + "/" + binding.propertyName);
                changed = true;
            }
            if (changed) EditorUtility.SetDirty(clip);
        }
        return removed;
    }

    private static Vector3 Center(ContactBase contact) =>
        (contact.rootTransform != null ? contact.rootTransform : contact.transform).TransformPoint(contact.position);

    private static bool Within(string path, string root) =>
        path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string Path(Transform target, Transform root) => AnimationUtility.CalculateTransformPath(target, root);

    private static Transform Child(Transform parent, string name)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }

    private static T Load<T>(string path, string label) where T : UnityEngine.Object
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new ArgumentException("The " + label + " does not load: " + path);
    }

    private static void RequireAssetPath(string path, string extension)
    {
        if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Write " + extension + " files below Assets/: " + path);
    }

    private static void EnsureFolder(string folder)
    {
        folder = folder.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
    }
}
#endif
