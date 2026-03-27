#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

public class LiveAvatarControllerService
{
    private static LiveAvatarControllerService _instance;
    public static LiveAvatarControllerService Instance => _instance ??= new LiveAvatarControllerService();

    public const string BlendShapeLinkWrapperPrefix = "UP_BSLINK_FACTOR_";
    public const string BlendShapeLinkVariantPrefix = "UP_BSLINK_VARIANT_";

    public class AvatarControllerSnapshot
    {
        public GameObject avatarRoot;
        public List<AnimatorLinkInfo> animators = new List<AnimatorLinkInfo>();
        public List<LiveStateInfo> blendShapeLinkStates = new List<LiveStateInfo>();
        public List<string> coveredLayers = new List<string>();
        public List<string> missingLayers = new List<string>();
        public AttachmentSource attachmentSource = AttachmentSource.None;
        public string attachmentStatus = "No animator source selected.";
        public List<BlendShapeLinkService.AppliedLinkRecord> appliedLinkRecords = new List<BlendShapeLinkService.AppliedLinkRecord>();
        public List<string> appliedLinksNotFoundInControllers = new List<string>();
    }

    public class AnimatorLinkInfo
    {
        public Animator animator;
        public string animatorPath;
        public bool isDescriptorAnimator;
        public bool isOnAvatarRoot;
        public string runtimeControllerName;
        public string runtimeControllerAssetPath;
        public string discoverySource;
        public List<string> matchedLayers = new List<string>();
    }

    public class LiveStateInfo
    {
        public Animator animator;
        public string animatorPath;
        public int layerIndex;
        public string layerName;
        public float layerWeight;
        public int fullPathHash;
        public string fullPathName;
        public string avatarMaskName;
        public List<LiveParameterValue> usedParameters = new List<LiveParameterValue>();
        public List<LiveClipActivation> clips = new List<LiveClipActivation>();
    }

    public class LiveParameterValue
    {
        public string name;
        public AnimatorControllerParameterType type;
        public float floatValue;
        public int intValue;
        public bool boolValue;
    }

    public class LiveClipActivation
    {
        public AnimationClip clip;
        public string clipName;
        public string clipAssetPath;
        public float activation;
        public bool isBlendShapeLinkVariant;
        // Blendshape value sampled from the clip at current normalized time (NaN if not applicable)
        public float clipSampledBlendshapeValue = float.NaN;
        // Binding path + property for the blendshape this clip drives (empty if not a blendshape clip)
        public string blendshapeBindingPath;
        public string blendshapeBindingProperty;
    }

    public class BindingSearchHit
    {
        public Animator animator;
        public string animatorPath;
        public int layerIndex;
        public string layerName;
        public string statePath;
        public string clipName;
        public string clipPath;
        public float clipActivation;
        public string bindingPath;
        public string bindingProperty;
        public string bindingTypeName;
        // Avatar mask name used on this layer (empty if none)
        public string avatarMaskName;
    }

    private struct LayerRequirement
    {
        public string name;
        public RuntimeAnimatorController controller;
    }

    public enum AttachmentSource
    {
        None,
        GestureManager,
        VrcDescriptor,
        AnimatorFallback
    }

    public GameObject ResolveActiveAvatarRoot()
    {
        var selected = Selection.activeGameObject;
        if (selected == null)
        {
            var allCustomBases = Resources.FindObjectsOfTypeAll<MyCustomBase>();
            var anyCustomBase = allCustomBases.FirstOrDefault(x =>
                x != null &&
                x.gameObject != null &&
                x.gameObject.scene.IsValid());
            if (anyCustomBase != null) return anyCustomBase.transform.root.gameObject;
            return null;
        }

        var customBase = selected.GetComponentInParent<MyCustomBase>();
        if (customBase != null) return customBase.transform.root.gameObject;

        customBase = selected.GetComponentInChildren<MyCustomBase>(true);
        if (customBase != null) return customBase.transform.root.gameObject;

        var descriptor = selected.GetComponentInParent<VRCAvatarDescriptor>();
        if (descriptor != null) return descriptor.transform.root.gameObject;

        descriptor = selected.GetComponentInChildren<VRCAvatarDescriptor>(true);
        if (descriptor != null) return descriptor.transform.root.gameObject;

        return selected.transform.root.gameObject;
    }

    public AvatarControllerSnapshot CaptureSnapshot(GameObject avatarRoot = null, bool forceRetryGestureManager = false)
    {
        if (avatarRoot == null) avatarRoot = ResolveActiveAvatarRoot();
        if (avatarRoot == null) return new AvatarControllerSnapshot();

        var snapshot = new AvatarControllerSnapshot { avatarRoot = avatarRoot };
        snapshot.animators = FindLinkedAnimators(avatarRoot, out var source, out var gmModule, forceRetryGestureManager);
        snapshot.attachmentSource = source;
        snapshot.attachmentStatus = BuildAttachmentStatus(source, snapshot.animators);
        BuildLayerCoverage(snapshot, avatarRoot);
        snapshot.blendShapeLinkStates = CollectBlendShapeLinkStates(snapshot.animators, gmModule);
        BuildAppliedLinksInfo(snapshot);
        return snapshot;
    }

    public List<AnimatorLinkInfo> FindLinkedAnimators(
        GameObject avatarRoot,
        out AttachmentSource source,
        bool forceRetryGestureManager = false)
    {
        return FindLinkedAnimators(avatarRoot, out source, out _, forceRetryGestureManager);
    }

    public List<AnimatorLinkInfo> FindLinkedAnimators(
        GameObject avatarRoot,
        out AttachmentSource source,
        out object gmModule,
        bool forceRetryGestureManager = false)
    {
        var result = new List<AnimatorLinkInfo>();
        source = AttachmentSource.None;
        gmModule = null;
        if (avatarRoot == null) return result;

        var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
        var descriptorAnimator = descriptor != null ? descriptor.GetComponent<Animator>() : null;
        var layerRequirements = BuildLayerRequirements(descriptor);
        var expectedLayerNames = layerRequirements.Select(x => x.name).Distinct(StringComparer.Ordinal).ToList();
        var seen = new HashSet<int>();
        var gmAnimators = TryFindGestureManagerAnimators(avatarRoot, forceRetryGestureManager, out var gmModuleLocal);
        AddAnimatorsToResult(result, gmAnimators, avatarRoot, descriptorAnimator, seen, "gesture-manager");
        UpdateAnimatorLayerMatches(result, layerRequirements, expectedLayerNames, gmModuleLocal);
        if (result.Count > 0)
        {
            source = AttachmentSource.GestureManager;
            gmModule = gmModuleLocal;
            result.Sort((a, b) => string.CompareOrdinal(a.animatorPath ?? string.Empty, b.animatorPath ?? string.Empty));
            return result;
        }

        result.Clear();
        seen.Clear();
        var descriptorAnimators = FindDescriptorLinkedAnimators(avatarRoot, descriptor, layerRequirements);
        AddAnimatorsToResult(result, descriptorAnimators, avatarRoot, descriptorAnimator, seen, "vrc-descriptor");
        result.Sort((a, b) => string.CompareOrdinal(a.animatorPath ?? string.Empty, b.animatorPath ?? string.Empty));
        if (result.Count > 0)
        {
            source = AttachmentSource.VrcDescriptor;
            return result;
        }

        result.Clear();
        seen.Clear();
        var fallbackAnimators = avatarRoot.GetComponentsInChildren<Animator>(true);
        AddAnimatorsToResult(result, fallbackAnimators, avatarRoot, descriptorAnimator, seen, "animator-fallback");
        result.Sort((a, b) => string.CompareOrdinal(a.animatorPath ?? string.Empty, b.animatorPath ?? string.Empty));
        if (result.Count > 0)
        {
            source = AttachmentSource.AnimatorFallback;
        }
        return result;
    }

    private static string BuildAttachmentStatus(AttachmentSource source, IReadOnlyList<AnimatorLinkInfo> animators)
    {
        int count = animators != null ? animators.Count : 0;
        switch (source)
        {
            case AttachmentSource.GestureManager:
                return "Attached to Gesture Manager (" + count + " animator(s)).";
            case AttachmentSource.VrcDescriptor:
                return "Gesture Manager unavailable. Using VRC Avatar Descriptor controllers (" + count +
                       " animator(s)).";
            case AttachmentSource.AnimatorFallback:
                return "Gesture/VRC descriptor unavailable. Using Animator fallback (" + count + " animator(s)).";
            default:
                return "No animator source selected.";
        }
    }

    private static void AddAnimatorsToResult(
        IList<AnimatorLinkInfo> result,
        IEnumerable<Animator> animators,
        GameObject avatarRoot,
        Animator descriptorAnimator,
        ISet<int> seen,
        string source)
    {
        if (result == null || animators == null || avatarRoot == null || seen == null) return;
        foreach (var animator in animators)
        {
            if (animator == null) continue;
            if (!seen.Add(animator.GetInstanceID())) continue;

            var runtimeController = animator.runtimeAnimatorController;
            result.Add(new AnimatorLinkInfo
            {
                animator = animator,
                animatorPath = CalculatePath(avatarRoot.transform, animator.transform),
                isDescriptorAnimator = descriptorAnimator == animator,
                isOnAvatarRoot = animator.transform == avatarRoot.transform,
                runtimeControllerName = runtimeController != null ? runtimeController.name : string.Empty,
                runtimeControllerAssetPath = runtimeController != null ? AssetDatabase.GetAssetPath(runtimeController) : string.Empty,
                discoverySource = source
            });
        }
    }

    private static List<Animator> FindDescriptorLinkedAnimators(
        GameObject avatarRoot,
        VRCAvatarDescriptor descriptor,
        List<LayerRequirement> layerRequirements)
    {
        var output = new List<Animator>();
        if (avatarRoot == null) return output;

        var targetControllers = new HashSet<RuntimeAnimatorController>();
        if (layerRequirements != null)
        {
            for (int i = 0; i < layerRequirements.Count; i++)
            {
                var c = layerRequirements[i].controller;
                if (c != null) targetControllers.Add(c);
            }
        }

        if (descriptor != null)
        {
            var descriptorAnimator = descriptor.GetComponent<Animator>();
            if (descriptorAnimator != null) output.Add(descriptorAnimator);
        }

        if (targetControllers.Count == 0)
        {
            return output;
        }

        var allAnimators = avatarRoot.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < allAnimators.Length; i++)
        {
            var animator = allAnimators[i];
            if (animator == null) continue;
            var runtime = animator.runtimeAnimatorController;
            if (runtime == null) continue;

            if (targetControllers.Contains(runtime))
            {
                output.Add(animator);
                continue;
            }

            var resolved = ResolveAnimatorController(runtime);
            for (int c = 0; c < layerRequirements.Count; c++)
            {
                var reqController = layerRequirements[c].controller;
                if (reqController == null) continue;
                var reqResolved = ResolveAnimatorController(reqController);
                if (resolved != null && reqResolved != null && resolved == reqResolved)
                {
                    output.Add(animator);
                    break;
                }
            }
        }

        return output;
    }

    private static List<LayerRequirement> BuildLayerRequirements(VRCAvatarDescriptor descriptor)
    {
        var output = new List<LayerRequirement>();
        if (descriptor == null) return output;

        var layerMap = new Dictionary<VRCAvatarDescriptor.AnimLayerType, string>
        {
            { VRCAvatarDescriptor.AnimLayerType.Base, "Base" },
            { VRCAvatarDescriptor.AnimLayerType.Additive, "Additive" },
            { VRCAvatarDescriptor.AnimLayerType.Sitting, "Sitting" },
            { VRCAvatarDescriptor.AnimLayerType.TPose, "TPose" },
            { VRCAvatarDescriptor.AnimLayerType.IKPose, "IKPose" },
            { VRCAvatarDescriptor.AnimLayerType.Gesture, "Gesture" },
            { VRCAvatarDescriptor.AnimLayerType.Action, "Action" },
            { VRCAvatarDescriptor.AnimLayerType.FX, "FX" }
        };

        var all = new List<VRCAvatarDescriptor.CustomAnimLayer>();
        if (descriptor.baseAnimationLayers != null) all.AddRange(descriptor.baseAnimationLayers);
        if (descriptor.specialAnimationLayers != null) all.AddRange(descriptor.specialAnimationLayers);

        for (int i = 0; i < all.Count; i++)
        {
            var layer = all[i];
            if (!layerMap.TryGetValue(layer.type, out var name)) continue;
            output.Add(new LayerRequirement
            {
                name = name,
                controller = layer.animatorController
            });
        }

        // Ensure all target layers are present in output even when descriptor has no explicit entry.
        foreach (var targetName in layerMap.Values)
        {
            if (output.Any(x => string.Equals(x.name, targetName, StringComparison.Ordinal))) continue;
            output.Add(new LayerRequirement { name = targetName, controller = null });
        }

        return output;
    }

    private static void UpdateAnimatorLayerMatches(
        List<AnimatorLinkInfo> animators,
        List<LayerRequirement> requirements,
        List<string> expectedLayerNames,
        object gmModule)
    {
        if (animators == null) return;
        
        List<string> gmLayerNames = null;
        if (gmModule != null)
        {
            gmLayerNames = new List<string>();
            var moduleType = gmModule.GetType();
            var layersField = moduleType.GetField("_layers", BindingFlags.NonPublic | BindingFlags.Instance);
            if (layersField != null)
            {
                var layersDict = layersField.GetValue(gmModule) as System.Collections.IDictionary;
                if (layersDict != null)
                {
                    foreach (var key in layersDict.Keys)
                    {
                        gmLayerNames.Add(key.ToString());
                    }
                }
            }
        }

        for (int i = 0; i < animators.Count; i++)
        {
            var info = animators[i];
            if (info == null) continue;
            
            // If this is the root animator managed by GM, we already know its layers from GM's state
            if (info.isOnAvatarRoot && info.discoverySource == "gesture-manager" && gmLayerNames != null)
            {
                info.matchedLayers = new List<string>(gmLayerNames);
            }
            else
            {
                info.matchedLayers = InferLayerMatches(info, requirements, expectedLayerNames);
            }
        }
    }

    private static List<string> InferLayerMatches(
        AnimatorLinkInfo info,
        List<LayerRequirement> requirements,
        List<string> expectedLayerNames)
    {
        var matches = new List<string>();
        if (info == null) return matches;

        RuntimeAnimatorController runtime = info.animator != null ? info.animator.runtimeAnimatorController : null;
        var resolvedController = ResolveAnimatorController(runtime);
        var resolvedPath = runtime != null ? AssetDatabase.GetAssetPath(runtime) : string.Empty;

        for (int i = 0; i < requirements.Count; i++)
        {
            var req = requirements[i];
            bool matched = false;
            if (req.controller != null && runtime != null)
            {
                var reqResolved = ResolveAnimatorController(req.controller);
                if (reqResolved != null && resolvedController != null && reqResolved == resolvedController)
                {
                    matched = true;
                }
                else
                {
                    string reqPath = AssetDatabase.GetAssetPath(req.controller);
                    if (!string.IsNullOrWhiteSpace(reqPath) && !string.IsNullOrWhiteSpace(resolvedPath) &&
                        string.Equals(reqPath, resolvedPath, StringComparison.Ordinal))
                    {
                        matched = true;
                    }
                }
            }

            if (!matched && ContainsLayerToken(info.runtimeControllerName, req.name))
            {
                matched = true;
            }

            if (!matched && ContainsLayerToken(resolvedPath, req.name))
            {
                matched = true;
            }

            if (matched && !matches.Contains(req.name))
            {
                matches.Add(req.name);
            }
        }

        // If no strong match but controller naming carries one of required layers, capture that.
        if (matches.Count == 0)
        {
            for (int i = 0; i < expectedLayerNames.Count; i++)
            {
                string layer = expectedLayerNames[i];
                if (ContainsLayerToken(info.runtimeControllerName, layer) || ContainsLayerToken(resolvedPath, layer))
                {
                    matches.Add(layer);
                }
            }
        }

        return matches;
    }

    private static bool ContainsLayerToken(string input, string layerName)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(layerName)) return false;
        string normalizedInput = NormalizeToken(input);
        string normalizedLayer = NormalizeToken(layerName);
        return normalizedInput.Contains(normalizedLayer);
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static HashSet<string> CollectCoveredLayers(List<AnimatorLinkInfo> animators)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        if (animators == null) return covered;
        for (int i = 0; i < animators.Count; i++)
        {
            var info = animators[i];
            if (info == null || info.matchedLayers == null) continue;
            for (int j = 0; j < info.matchedLayers.Count; j++)
            {
                covered.Add(info.matchedLayers[j]);
            }
        }

        return covered;
    }

    private static void BuildLayerCoverage(AvatarControllerSnapshot snapshot, GameObject avatarRoot)
    {
        snapshot.coveredLayers.Clear();
        snapshot.missingLayers.Clear();

        var descriptor = avatarRoot != null ? avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true) : null;
        var expected = BuildLayerRequirements(descriptor).Select(x => x.name).Distinct(StringComparer.Ordinal).ToList();
        var covered = CollectCoveredLayers(snapshot.animators).ToList();
        covered.Sort(StringComparer.Ordinal);

        snapshot.coveredLayers.AddRange(covered);
        for (int i = 0; i < expected.Count; i++)
        {
            if (!covered.Contains(expected[i])) snapshot.missingLayers.Add(expected[i]);
        }
    }

    private static readonly List<Animator> _gestureManagerAnimatorCache = new List<Animator>();
    private static bool _gestureManagerCacheReady;

    private static System.Type _cachedGmType;

    private static System.Type FindGestureManagerType()
    {
        if (_cachedGmType != null) return _cachedGmType;
        // Type.GetType with simple assembly name doesn't work in Unity for package assemblies.
        // Scan all loaded assemblies instead.
        var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            var asm = assemblies[i];
            if (asm == null) continue;
            string asmName = asm.GetName().Name ?? string.Empty;
            if (!asmName.Contains("gesture-manager", StringComparison.OrdinalIgnoreCase) &&
                !asmName.Contains("GestureManager", StringComparison.OrdinalIgnoreCase) &&
                !asmName.Contains("BlackStartX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var t = asm.GetType("BlackStartX.GestureManager.GestureManager");
            if (t != null)
            {
                _cachedGmType = t;
                return t;
            }
        }
        return null;
    }

    private static List<Animator> TryFindGestureManagerAnimators(GameObject avatarRoot, bool forceRetry, out object gmModule)
    {
        gmModule = null;

        // Try GestureManager.ControlledAvatars dictionary.
        // GM may register the avatar under a different key than our avatarRoot (it picks its own root),
        // so we iterate ALL entries and pick the first active module — attachment is independent of avatarRoot.
        var gmType = FindGestureManagerType();
        if (gmType != null)
        {
            var controlledAvatarsField = gmType.GetField("ControlledAvatars", BindingFlags.Public | BindingFlags.Static);
            if (controlledAvatarsField != null)
            {
                var dict = controlledAvatarsField.GetValue(null) as System.Collections.IDictionary;
                if (dict != null && dict.Count > 0)
                {
                    // First try exact match by avatarRoot key (fast path)
                    object candidate = avatarRoot != null && dict.Contains(avatarRoot) ? dict[avatarRoot] : null;

                    // If no exact match, iterate all entries and pick the first non-null module
                    if (candidate == null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in dict)
                        {
                            if (entry.Value != null)
                            {
                                candidate = entry.Value;
                                break;
                            }
                        }
                    }

                    if (candidate != null)
                    {
                        var moduleType = candidate.GetType();
                        var avatarAnimatorField = moduleType.GetField("AvatarAnimator", BindingFlags.Public | BindingFlags.Instance)
                                               ?? moduleType.BaseType?.GetField("AvatarAnimator", BindingFlags.Public | BindingFlags.Instance);
                        if (avatarAnimatorField != null)
                        {
                            var animator = avatarAnimatorField.GetValue(candidate) as Animator;
                            if (animator != null)
                            {
                                gmModule = candidate;
                                return new List<Animator> { animator };
                            }
                        }
                    }
                }
            }
        }

        if (avatarRoot == null) return new List<Animator>();

        // Fallback: if gmType was found but ControlledAvatars was empty, try finding the GestureManager MonoBehaviour
        // and extracting its Module.AvatarAnimator directly.
        if (gmType != null && !forceRetry)
        {
            // Try to find active GestureManager instances in scene
            var gmInstances = Resources.FindObjectsOfTypeAll(gmType);
            for (int i = 0; i < gmInstances.Length; i++)
            {
                var gm = gmInstances[i];
                if (gm == null) continue;
                var moduleField = gmType.GetField("Module", BindingFlags.Public | BindingFlags.Instance);
                if (moduleField == null) continue;
                var module = moduleField.GetValue(gm);
                if (module == null) continue;
                var moduleType = module.GetType();
                var avatarAnimatorField = moduleType.GetField("AvatarAnimator", BindingFlags.Public | BindingFlags.Instance)
                                       ?? moduleType.BaseType?.GetField("AvatarAnimator", BindingFlags.Public | BindingFlags.Instance);
                if (avatarAnimatorField == null) continue;
                var animator = avatarAnimatorField.GetValue(module) as Animator;
                if (animator == null) continue;
                gmModule = module;
                return new List<Animator> { animator };
            }
        }

        // Fallback to the slow MonoBehaviour scan
        if (_gestureManagerCacheReady && !forceRetry)
        {
            return FilterAnimatorsForAvatar(_gestureManagerAnimatorCache, avatarRoot);
        }

        var result = new List<Animator>();
        var seen = new HashSet<int>();
        var allBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
        for (int i = 0; i < allBehaviours.Length; i++)
        {
            var behaviour = allBehaviours[i];
            if (behaviour == null) continue;
            var type = behaviour.GetType();
            string fullName = type.FullName ?? string.Empty;
            if (!fullName.Contains("blackstartx", StringComparison.OrdinalIgnoreCase) &&
                !fullName.Contains("GestureManager", StringComparison.OrdinalIgnoreCase) &&
                !fullName.Contains("ModuleVrc3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ExtractAnimatorsFromObject(behaviour, seen, result);
        }

        _gestureManagerAnimatorCache.Clear();
        _gestureManagerAnimatorCache.AddRange(result);
        _gestureManagerCacheReady = true;

        return FilterAnimatorsForAvatar(result, avatarRoot);
    }

    private static List<Animator> FilterAnimatorsForAvatar(IEnumerable<Animator> input, GameObject avatarRoot)
    {
        var output = new List<Animator>();
        if (input == null) return output;

        Transform rootTransform = avatarRoot != null ? avatarRoot.transform : null;
        foreach (var animator in input)
        {
            if (animator == null) continue;
            if (rootTransform != null && animator.transform.root != rootTransform) continue;
            output.Add(animator);
        }

        return output;
    }

    private static void ExtractAnimatorsFromObject(object instance, ISet<int> seen, IList<Animator> output)
    {
        if (instance == null || seen == null || output == null) return;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();

        var fields = type.GetFields(flags);
        for (int i = 0; i < fields.Length; i++)
        {
            object value = null;
            try { value = fields[i].GetValue(instance); } catch { }
            AppendAnimatorValues(value, seen, output);
        }

        var properties = type.GetProperties(flags);
        for (int i = 0; i < properties.Length; i++)
        {
            if (!properties[i].CanRead || properties[i].GetIndexParameters().Length > 0) continue;
            object value = null;
            try { value = properties[i].GetValue(instance, null); } catch { }
            AppendAnimatorValues(value, seen, output);
        }
    }

    private static void AppendAnimatorValues(object value, ISet<int> seen, IList<Animator> output)
    {
        if (value == null) return;

        var animator = value as Animator;
        if (animator != null)
        {
            if (seen.Add(animator.GetInstanceID())) output.Add(animator);
            return;
        }

        var enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null) return;
        foreach (var item in enumerable)
        {
            var a = item as Animator;
            if (a == null) continue;
            if (seen.Add(a.GetInstanceID())) output.Add(a);
        }
    }

    private void TraverseAndCollectStates(Playable playable, Animator animator, AnimatorLinkInfo info, List<LiveStateInfo> states, HashSet<PlayableHandle> visited, Dictionary<int, string> layerMaskNames = null)
    {
        if (!playable.IsValid() || !visited.Add(playable.GetHandle())) return;

        if (playable.IsPlayableOfType<AnimatorControllerPlayable>())
        {
            var acPlayable = (AnimatorControllerPlayable)playable;

            int layerCount = acPlayable.GetLayerCount();
            for (int l = 0; l < layerCount; l++)
            {
                var stateInfo = acPlayable.GetCurrentAnimatorStateInfo(l);
                var clipInfos = acPlayable.GetCurrentAnimatorClipInfo(l);
                if (!HasVariantClip(clipInfos)) continue;

                string maskName = string.Empty;
                if (layerMaskNames != null) layerMaskNames.TryGetValue(l, out maskName);

                var state = new LiveStateInfo
                {
                    animator = animator,
                    animatorPath = info.animatorPath,
                    layerIndex = l,
                    layerName = acPlayable.GetLayerName(l),
                    layerWeight = acPlayable.GetLayerWeight(l),
                    fullPathHash = stateInfo.fullPathHash,
                    fullPathName = "[UNKNOWN]#" + stateInfo.fullPathHash,
                    avatarMaskName = maskName ?? string.Empty
                };

                // Read all factor parameters (UP_BSLINK_FACTOR_*) present in this playable
                int paramCount = acPlayable.GetParameterCount();
                for (int pi = 0; pi < paramCount; pi++)
                {
                    var p = acPlayable.GetParameter(pi);
                    if (p.name != null && p.name.StartsWith(BlendShapeLinkWrapperPrefix, StringComparison.Ordinal))
                    {
                        state.usedParameters.Add(ReadParameterFromPlayable(acPlayable, p.name));
                    }
                }

                for (int clipIdx = 0; clipIdx < clipInfos.Length; clipIdx++)
                {
                    var clipInfo = clipInfos[clipIdx];
                    if (clipInfo.clip == null) continue;
                    bool isVariant = clipInfo.clip.name.StartsWith(BlendShapeLinkVariantPrefix, StringComparison.Ordinal);
                    var lca = new LiveClipActivation
                    {
                        clip = clipInfo.clip,
                        clipName = clipInfo.clip.name,
                        clipAssetPath = AssetDatabase.GetAssetPath(clipInfo.clip),
                        activation = clipInfo.weight,
                        isBlendShapeLinkVariant = isVariant
                    };
                    if (isVariant)
                    {
                        float normalizedTime = stateInfo.normalizedTime % 1f;
                        if (normalizedTime < 0f) normalizedTime += 1f;
                        SampleBlendshapeFromClip(clipInfo.clip, normalizedTime, lca);
                    }
                    state.clips.Add(lca);
                }

                states.Add(state);
            }
        }

        int inputCount = playable.GetInputCount();
        for (int i = 0; i < inputCount; i++)
        {
            TraverseAndCollectStates(playable.GetInput(i), animator, info, states, visited, layerMaskNames);
        }
    }

    // Samples the first blendshape curve in the clip at the given normalizedTime and stores result in lca.
    private static void SampleBlendshapeFromClip(AnimationClip clip, float normalizedTime, LiveClipActivation lca)
    {
        if (clip == null || lca == null) return;
        float time = normalizedTime * clip.length;
        var bindings = AnimationUtility.GetCurveBindings(clip);
        for (int b = 0; b < bindings.Length; b++)
        {
            var binding = bindings[b];
            if (!binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null) continue;
            lca.clipSampledBlendshapeValue = curve.Evaluate(time);
            lca.blendshapeBindingPath = binding.path;
            lca.blendshapeBindingProperty = binding.propertyName;
            return;
        }
    }

    // Reads the current blendshape value from the SkinnedMeshRenderer on the avatar.
    public static float ReadSmrBlendshapeValue(GameObject avatarRoot, string rendererPath, string blendshapeProperty)
    {
        if (avatarRoot == null || string.IsNullOrWhiteSpace(rendererPath) || string.IsNullOrWhiteSpace(blendshapeProperty))
            return float.NaN;
        // blendshapeProperty is like "blendShape.SomeName" — strip prefix
        string bsName = blendshapeProperty.StartsWith("blendShape.", StringComparison.Ordinal)
            ? blendshapeProperty.Substring("blendShape.".Length)
            : blendshapeProperty;
        var animator = avatarRoot.GetComponentInChildren<Animator>(true);
        var pathRoot = animator != null ? animator.transform : avatarRoot.transform;
        var rendererTransform = pathRoot.Find(rendererPath);
        if (rendererTransform == null) return float.NaN;
        var smr = rendererTransform.GetComponent<SkinnedMeshRenderer>();
        if (smr == null || smr.sharedMesh == null) return float.NaN;
        int idx = smr.sharedMesh.GetBlendShapeIndex(bsName);
        if (idx < 0) return float.NaN;
        return smr.GetBlendShapeWeight(idx);
    }

    // Extracts avatar mask names per layer-index from GM's _layers dictionary.
    // Returns a dict mapping layer index (0-based slot in the mixer) to mask name.
    public static Dictionary<int, string> ExtractGmLayerMaskNames(object gmModule)
    {
        var result = new Dictionary<int, string>();
        if (gmModule == null) return result;
        try
        {
            var moduleType = gmModule.GetType();
            var layersField = moduleType.GetField("_layers", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? moduleType.BaseType?.GetField("_layers", BindingFlags.NonPublic | BindingFlags.Instance);
            if (layersField == null) return result;
            var layersDict = layersField.GetValue(gmModule) as System.Collections.IDictionary;
            if (layersDict == null) return result;

            // GM stores _layers as Dictionary<VRCAvatarDescriptor.AnimLayerType, LayerData>
            // The mixer slot index corresponds to iteration order (1-based in mixer, 0-based here).
            // We read the AvatarMask from the mixer via reflection on LayerData.Weight.
            // Simpler: read from the descriptor layer list in the same sorted order GM uses.
            // We'll just label by key.ToString() and slot index.
            int slotIdx = 0;
            foreach (System.Collections.DictionaryEntry entry in layersDict)
            {
                // Try to get mask from LayerData via reflection
                string maskName = string.Empty;
                try
                {
                    var layerData = entry.Value;
                    if (layerData != null)
                    {
                        var ldType = layerData.GetType();
                        // LayerData has no direct mask field; mask is set on the mixer.
                        // We can at least label the layer type.
                        maskName = entry.Key != null ? entry.Key.ToString() : string.Empty;
                    }
                }
                catch { }
                result[slotIdx] = maskName;
                slotIdx++;
            }
        }
        catch { }
        return result;
    }

    private static LiveParameterValue ReadParameterFromPlayable(AnimatorControllerPlayable playable, string paramName)
    {
        var output = new LiveParameterValue
        {
            name = paramName,
            type = AnimatorControllerParameterType.Float
        };

        if (!playable.IsValid() || string.IsNullOrWhiteSpace(paramName)) return output;
        
        // We can't easily get the type from the playable without searching, but we can try to read it
        // Or we can just use the controller's parameter definitions if we had them.
        // For now, let's try to find it in the playable's parameter list if possible.
        // Actually, AnimatorControllerPlayable has GetParameterCount/GetParameter.
        int paramCount = playable.GetParameterCount();
        for (int i = 0; i < paramCount; i++)
        {
            var p = playable.GetParameter(i);
            if (p.name == paramName)
            {
                output.type = p.type;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: output.floatValue = playable.GetFloat(p.nameHash); break;
                    case AnimatorControllerParameterType.Int: output.intValue = playable.GetInteger(p.nameHash); break;
                    case AnimatorControllerParameterType.Bool:
                    case AnimatorControllerParameterType.Trigger: output.boolValue = playable.GetBool(p.nameHash); break;
                }
                break;
            }
        }

        return output;
    }

    public List<LiveStateInfo> CollectBlendShapeLinkStates(IReadOnlyList<AnimatorLinkInfo> animators, object gmModule = null)
    {
        var states = new List<LiveStateInfo>();
        if (animators == null) return states;

        // When Gesture Manager is active, it owns the PlayableGraph externally.
        // animator.playableGraph is NOT valid because GM creates its own graph via PlayableGraph.Create()
        // and targets the animator via AnimationPlayableOutput. We must extract GM's graph via reflection.
        PlayableGraph gmGraph = default;
        bool hasGmGraph = false;
        if (gmModule != null)
        {
            hasGmGraph = TryExtractGmPlayableGraph(gmModule, out gmGraph);
        }

        for (int i = 0; i < animators.Count; i++)
        {
            var info = animators[i];
            var animator = info != null ? info.animator : null;
            if (animator == null || !animator.isActiveAndEnabled) continue;

            // Try GM's external graph first (the primary path when GM is active)
            if (animator.runtimeAnimatorController == null && hasGmGraph && gmGraph.IsValid())
            {
                CollectStatesFromGraph(gmGraph, animator, info, states, gmModule);
                continue;
            }

            // Fallback: animator's own playable graph (rare, but possible without GM)
            if (animator.runtimeAnimatorController == null)
            {
                try
                {
                    var ownGraph = animator.playableGraph;
                    if (ownGraph.IsValid())
                    {
                        CollectStatesFromGraph(ownGraph, animator, info, states);
                        continue;
                    }
                }
                catch
                {
                    // animator.playableGraph can throw if no graph exists
                }
                continue;
            }

            int layerCount = animator.layerCount;
            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                var stateInfo = animator.GetCurrentAnimatorStateInfo(layerIndex);
                var clipInfos = animator.GetCurrentAnimatorClipInfo(layerIndex);
                if (!HasVariantClip(clipInfos)) continue;

                var state = new LiveStateInfo
                {
                    animator = animator,
                    animatorPath = info.animatorPath,
                    layerIndex = layerIndex,
                    layerName = animator.GetLayerName(layerIndex),
                    layerWeight = animator.GetLayerWeight(layerIndex),
                    fullPathHash = stateInfo.fullPathHash,
                    fullPathName = "[UNKNOWN]#" + stateInfo.fullPathHash
                };

                // Read all factor parameters (UP_BSLINK_FACTOR_*) from the animator
                var parameters = animator.parameters;
                for (int pi = 0; pi < parameters.Length; pi++)
                {
                    var p = parameters[pi];
                    if (p.name != null && p.name.StartsWith(BlendShapeLinkWrapperPrefix, StringComparison.Ordinal))
                    {
                        state.usedParameters.Add(ReadParameter(animator, p.name));
                    }
                }

                for (int clipIdx = 0; clipIdx < clipInfos.Length; clipIdx++)
                {
                    var clipInfo = clipInfos[clipIdx];
                    var clip = clipInfo.clip;
                    if (clip == null) continue;
                    state.clips.Add(new LiveClipActivation
                    {
                        clip = clip,
                        clipName = clip.name,
                        clipAssetPath = AssetDatabase.GetAssetPath(clip),
                        activation = clipInfo.weight,
                        isBlendShapeLinkVariant = clip.name.StartsWith(BlendShapeLinkVariantPrefix, StringComparison.Ordinal)
                    });
                }

                states.Add(state);
            }
        }

        return states;
    }

    private static bool TryExtractGmPlayableGraph(object gmModule, out PlayableGraph graph)
    {
        graph = default;
        if (gmModule == null) return false;
        try
        {
            var moduleType = gmModule.GetType();
            var graphField = moduleType.GetField("_playableGraph", BindingFlags.NonPublic | BindingFlags.Instance);
            if (graphField == null)
            {
                // Try base type in case of inheritance
                graphField = moduleType.BaseType?.GetField("_playableGraph", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (graphField != null)
            {
                var value = graphField.GetValue(gmModule);
                if (value is PlayableGraph pg && pg.IsValid())
                {
                    graph = pg;
                    return true;
                }
            }
        }
        catch { /* reflection safety */ }
        return false;
    }

    private void CollectStatesFromGraph(PlayableGraph graph, Animator animator, AnimatorLinkInfo info, List<LiveStateInfo> states, object gmModule = null)
    {
        if (!graph.IsValid()) return;

        var layerMaskNames = gmModule != null ? ExtractGmLayerMaskNames(gmModule) : null;

        int outputCount = graph.GetOutputCount();
        for (int i = 0; i < outputCount; i++)
        {
            var output = graph.GetOutput(i);
            if (!output.IsPlayableOutputOfType<AnimationPlayableOutput>()) continue;

            var sourcePlayable = output.GetSourcePlayable();
            if (!sourcePlayable.IsValid()) continue;

            var visited = new HashSet<PlayableHandle>();
            TraverseAndCollectStates(sourcePlayable, animator, info, states, visited, layerMaskNames);
        }
    }

    private static void BuildAppliedLinksInfo(AvatarControllerSnapshot snapshot)
    {
        if (snapshot == null) return;
        var registry = BlendShapeLinkService.AppliedLinksRegistry;
        if (registry == null || registry.Count == 0) return;

        // Collect all controller asset paths present in the snapshot's animators
        var foundControllerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.animators != null)
        {
            for (int i = 0; i < snapshot.animators.Count; i++)
            {
                var a = snapshot.animators[i];
                if (a != null && !string.IsNullOrWhiteSpace(a.runtimeControllerAssetPath))
                    foundControllerPaths.Add(a.runtimeControllerAssetPath);
            }
        }

        // Collect all applied link records and check which ones are found in current controllers
        foreach (var kvp in registry)
        {
            for (int i = 0; i < kvp.Value.Count; i++)
            {
                var record = kvp.Value[i];
                snapshot.appliedLinkRecords.Add(record);
                if (!string.IsNullOrWhiteSpace(record.controllerAssetPath) &&
                    !foundControllerPaths.Contains(record.controllerAssetPath))
                {
                    string msg = string.Format("Link '{0}' -> '{1}' was applied to controller '{2}' ({3}) but that controller was not found in the current animator set.",
                        record.toFixName, record.fixedByName, record.controllerName, record.controllerAssetPath);
                    if (!snapshot.appliedLinksNotFoundInControllers.Contains(msg))
                        snapshot.appliedLinksNotFoundInControllers.Add(msg);
                }
            }
        }
    }

    private static bool HasVariantClip(AnimatorClipInfo[] clipInfos)
    {
        if (clipInfos == null || clipInfos.Length == 0) return false;
        for (int i = 0; i < clipInfos.Length; i++)
        {
            var clip = clipInfos[i].clip;
            if (clip == null) continue;
            if (clip.name.StartsWith(BlendShapeLinkVariantPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Scans ALL clips in ALL states across all controllers (not just currently active ones).
    // Returns every state/clip that contains a binding matching the query.
    public List<BindingSearchHit> SearchAllAnimationBindings(GameObject avatarRoot, string query)
    {
        var hits = new List<BindingSearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;

        var snapshot = CaptureSnapshot(avatarRoot);
        if (snapshot == null || snapshot.animators == null) return hits;

        // Extract GM graph if attached via GM
        PlayableGraph gmGraph = default;
        bool hasGmGraph = false;
        object gmModule = null;
        if (snapshot.attachmentSource == AttachmentSource.GestureManager)
        {
            TryFindGestureManagerAnimators(avatarRoot, false, out gmModule);
            if (gmModule != null)
                hasGmGraph = TryExtractGmPlayableGraph(gmModule, out gmGraph);
        }

        var dedupe = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < snapshot.animators.Count; i++)
        {
            var animatorInfo = snapshot.animators[i];
            var animator = animatorInfo != null ? animatorInfo.animator : null;
            if (animator == null || !animator.isActiveAndEnabled) continue;

            // Collect all AnimatorControllerPlayables from the graph
            if (animator.runtimeAnimatorController == null)
            {
                // In GM mode, also scan the original AnimatorController assets from the VRC descriptor.
                // GM wraps them in AnimatorOverrideController at runtime, so GetCurrentAnimatorClipInfo
                // only returns currently-active clips. Scanning the source controllers covers all states.
                var descriptorControllers = CollectDescriptorControllers(avatarRoot);
                for (int dc = 0; dc < descriptorControllers.Count; dc++)
                {
                    SearchAllBindingsInController(descriptorControllers[dc], animator, animatorInfo, query, hits, dedupe);
                }

                // Also scan active clips from the graph for any runtime-generated clips not in the assets
                PlayableGraph searchGraph = default;
                bool hasSearchGraph = false;
                if (hasGmGraph && gmGraph.IsValid()) { searchGraph = gmGraph; hasSearchGraph = true; }
                else
                {
                    try { var g = animator.playableGraph; if (g.IsValid()) { searchGraph = g; hasSearchGraph = true; } } catch { }
                }
                if (hasSearchGraph)
                    SearchAllBindingsInGraph(searchGraph, animator, animatorInfo, query, hits, dedupe);
                continue;
            }

            // Direct animator path: iterate all layers, all states via AnimatorController asset
            var runtimeCtrl = animator.runtimeAnimatorController;
            var ctrl = ResolveAnimatorController(runtimeCtrl);
            if (ctrl == null) continue;
            SearchAllBindingsInController(ctrl, animator, animatorInfo, query, hits, dedupe);
        }

        return hits;
    }

    private static List<AnimatorController> CollectDescriptorControllers(GameObject avatarRoot)
    {
        var result = new List<AnimatorController>();
        if (avatarRoot == null) return result;
        var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
        if (descriptor == null) return result;
        var seen = new HashSet<int>();
        var allLayers = new List<VRCAvatarDescriptor.CustomAnimLayer>();
        if (descriptor.baseAnimationLayers != null) allLayers.AddRange(descriptor.baseAnimationLayers);
        if (descriptor.specialAnimationLayers != null) allLayers.AddRange(descriptor.specialAnimationLayers);
        for (int i = 0; i < allLayers.Count; i++)
        {
            var ctrl = allLayers[i].animatorController as AnimatorController;
            if (ctrl == null) continue;
            if (!seen.Add(ctrl.GetInstanceID())) continue;
            result.Add(ctrl);
        }
        return result;
    }

    private void SearchAllBindingsInGraph(
        PlayableGraph graph, Animator animator, AnimatorLinkInfo animatorInfo,
        string query, List<BindingSearchHit> hits, HashSet<string> dedupe)
    {
        if (!graph.IsValid()) return;
        int outputCount = graph.GetOutputCount();
        for (int o = 0; o < outputCount; o++)
        {
            var output = graph.GetOutput(o);
            if (!output.IsPlayableOutputOfType<AnimationPlayableOutput>()) continue;
            var source = output.GetSourcePlayable();
            if (!source.IsValid()) continue;
            SearchAllBindingsInPlayable(source, animator, animatorInfo, query, hits, dedupe, new HashSet<PlayableHandle>());
        }
    }

    private void SearchAllBindingsInPlayable(
        Playable playable, Animator animator, AnimatorLinkInfo animatorInfo,
        string query, List<BindingSearchHit> hits, HashSet<string> dedupe, HashSet<PlayableHandle> visited)
    {
        if (!playable.IsValid() || !visited.Add(playable.GetHandle())) return;

        if (playable.IsPlayableOfType<AnimatorControllerPlayable>())
        {
            var acPlayable = (AnimatorControllerPlayable)playable;
            // We need the underlying AnimatorController asset to enumerate all states.
            // Extract it via the runtimeAnimatorController of the animator if available,
            // otherwise fall back to scanning only currently-active clips.
            // Since runtimeAnimatorController is null in GM mode, we use the active-clip fallback
            // but scan ALL layers regardless of weight.
            int layerCount = acPlayable.GetLayerCount();
            for (int l = 0; l < layerCount; l++)
            {
                var stateInfo = acPlayable.GetCurrentAnimatorStateInfo(l);
                string statePath = acPlayable.GetLayerName(l) + "#" + stateInfo.fullPathHash;
                var clipInfos = acPlayable.GetCurrentAnimatorClipInfo(l);
                for (int c = 0; c < clipInfos.Length; c++)
                {
                    var clip = clipInfos[c].clip;
                    if (clip == null) continue;
                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    SearchClipBindings(clip, clipPath, animator, animatorInfo, l,
                        acPlayable.GetLayerName(l), statePath, clipInfos[c].weight, query, hits, dedupe);
                }

                // Also scan next state if transitioning
                var nextClipInfos = acPlayable.GetNextAnimatorClipInfo(l);
                for (int c = 0; c < nextClipInfos.Length; c++)
                {
                    var clip = nextClipInfos[c].clip;
                    if (clip == null) continue;
                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    SearchClipBindings(clip, clipPath, animator, animatorInfo, l,
                        acPlayable.GetLayerName(l), statePath + "[next]", nextClipInfos[c].weight, query, hits, dedupe);
                }
            }
        }

        int inputCount = playable.GetInputCount();
        for (int i = 0; i < inputCount; i++)
            SearchAllBindingsInPlayable(playable.GetInput(i), animator, animatorInfo, query, hits, dedupe, visited);
    }

    private static void SearchAllBindingsInController(
        AnimatorController ctrl, Animator animator, AnimatorLinkInfo animatorInfo,
        string query, List<BindingSearchHit> hits, HashSet<string> dedupe)
    {
        if (ctrl == null) return;
        var layers = ctrl.layers;
        for (int l = 0; l < layers.Length; l++)
        {
            var layer = layers[l];
            if (layer?.stateMachine == null) continue;
            string layerName = layer.name;
            CollectAllClipsFromStateMachine(layer.stateMachine, new HashSet<AnimatorStateMachine>(),
                out var allStateClips);
            foreach (var pair in allStateClips)
            {
                string statePath = pair.Key;
                var clips = pair.Value;
                for (int c = 0; c < clips.Count; c++)
                {
                    var clip = clips[c];
                    if (clip == null) continue;
                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    // Use current activation from animator if possible.
                    // Note: l is the controller asset's layer index, which may exceed the
                    // animator's runtime layer count (e.g. when GM merges multiple controllers
                    // into a single PlayableGraph). Guard against invalid index.
                    float activation = 0f;
                    try
                    {
                        if (l < animator.layerCount)
                        {
                            var currentClips = animator.GetCurrentAnimatorClipInfo(l);
                            for (int ci = 0; ci < currentClips.Length; ci++)
                            {
                                if (currentClips[ci].clip == clip) { activation = currentClips[ci].weight; break; }
                            }
                        }
                    }
                    catch { }
                    SearchClipBindings(clip, clipPath, animator, animatorInfo, l, layerName, statePath, activation, query, hits, dedupe);
                }
            }
        }
    }

    private static void CollectAllClipsFromStateMachine(
        AnimatorStateMachine sm,
        HashSet<AnimatorStateMachine> visited,
        out Dictionary<string, List<AnimationClip>> stateClips)
    {
        stateClips = new Dictionary<string, List<AnimationClip>>(StringComparer.Ordinal);
        if (sm == null || !visited.Add(sm)) return;
        CollectClipsFromSM(sm, visited, stateClips, sm.name);
    }

    private static void CollectClipsFromSM(
        AnimatorStateMachine sm,
        HashSet<AnimatorStateMachine> visited,
        Dictionary<string, List<AnimationClip>> stateClips,
        string prefix)
    {
        if (sm == null) return;
        var states = sm.states;
        for (int i = 0; i < states.Length; i++)
        {
            var state = states[i].state;
            if (state == null) continue;
            string statePath = prefix + "/" + state.name;
            var clips = new List<AnimationClip>();
            CollectClipsFromMotion(state.motion, clips, new HashSet<BlendTree>());
            if (clips.Count > 0)
                stateClips[statePath] = clips;
        }
        var subSMs = sm.stateMachines;
        for (int i = 0; i < subSMs.Length; i++)
        {
            var sub = subSMs[i].stateMachine;
            if (sub == null || !visited.Add(sub)) continue;
            CollectClipsFromSM(sub, visited, stateClips, prefix + "/" + sub.name);
        }
    }

    private static void CollectClipsFromMotion(Motion motion, List<AnimationClip> clips, HashSet<BlendTree> visitedTrees)
    {
        if (motion == null) return;
        var clip = motion as AnimationClip;
        if (clip != null) { clips.Add(clip); return; }
        var tree = motion as BlendTree;
        if (tree == null || !visitedTrees.Add(tree)) return;
        var children = tree.children;
        for (int i = 0; i < children.Length; i++)
            CollectClipsFromMotion(children[i].motion, clips, visitedTrees);
    }

    private static void SearchClipBindings(
        AnimationClip clip, string clipPath,
        Animator animator, AnimatorLinkInfo animatorInfo,
        int layerIndex, string layerName, string statePath, float activation,
        string query, List<BindingSearchHit> hits, HashSet<string> dedupe)
    {
        var curveBindings = AnimationUtility.GetCurveBindings(clip);
        for (int b = 0; b < curveBindings.Length; b++)
        {
            var binding = curveBindings[b];
            if (!BindingMatchesQuery(binding.path, binding.propertyName, binding.type, query)) continue;
            AppendDirectHit(hits, dedupe, animator, animatorInfo.animatorPath, layerIndex, layerName,
                statePath, clip, clipPath, activation,
                binding.path, binding.propertyName,
                binding.type != null ? binding.type.FullName : string.Empty);
        }
        var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        for (int b = 0; b < objectBindings.Length; b++)
        {
            var binding = objectBindings[b];
            if (!BindingMatchesQuery(binding.path, binding.propertyName, binding.type, query)) continue;
            AppendDirectHit(hits, dedupe, animator, animatorInfo.animatorPath, layerIndex, layerName,
                statePath, clip, clipPath, activation,
                binding.path, binding.propertyName,
                binding.type != null ? binding.type.FullName : string.Empty);
        }
    }

    public List<BindingSearchHit> SearchCurrentAnimationBindings(GameObject avatarRoot, string query)
    {
        var hits = new List<BindingSearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;

        var snapshot = CaptureSnapshot(avatarRoot);
        if (snapshot == null || snapshot.animators == null) return hits;

        // Extract GM graph if attached via GM
        PlayableGraph gmGraph = default;
        bool hasGmGraph = false;
        if (snapshot.attachmentSource == AttachmentSource.GestureManager)
        {
            var gmAnimators = TryFindGestureManagerAnimators(avatarRoot, false, out var gmModule);
            if (gmModule != null)
                hasGmGraph = TryExtractGmPlayableGraph(gmModule, out gmGraph);
        }

        var dedupe = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < snapshot.animators.Count; i++)
        {
            var animatorInfo = snapshot.animators[i];
            var animator = animatorInfo != null ? animatorInfo.animator : null;
            if (animator == null || !animator.isActiveAndEnabled) continue;

            // If runtimeAnimatorController is null (GM case), search via PlayableGraph
            if (animator.runtimeAnimatorController == null)
            {
                PlayableGraph searchGraph = default;
                bool hasSearchGraph = false;
                if (hasGmGraph && gmGraph.IsValid())
                {
                    searchGraph = gmGraph;
                    hasSearchGraph = true;
                }
                else
                {
                    try
                    {
                        var ownGraph = animator.playableGraph;
                        if (ownGraph.IsValid()) { searchGraph = ownGraph; hasSearchGraph = true; }
                    }
                    catch { /* no graph */ }
                }

                if (hasSearchGraph)
                    SearchBindingsInGraph(searchGraph, animator, animatorInfo, query, hits, dedupe);
                continue;
            }

            int layerCount = animator.layerCount;
            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                float layerWeight = layerIndex == 0 ? 1f : animator.GetLayerWeight(layerIndex);
                if (layerWeight <= 0.0001f) continue;

                var currentState = animator.GetCurrentAnimatorStateInfo(layerIndex);
                string statePath = "[UNKNOWN]#" + currentState.fullPathHash;

                var clipInfos = animator.GetCurrentAnimatorClipInfo(layerIndex);
                for (int clipIndex = 0; clipIndex < clipInfos.Length; clipIndex++)
                {
                    var clipInfo = clipInfos[clipIndex];
                    var clip = clipInfo.clip;
                    if (clip == null) continue;
                    float activation = layerWeight * clipInfo.weight;
                    if (activation <= 0.0001f) continue;

                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    var curveBindings = AnimationUtility.GetCurveBindings(clip);
                    for (int b = 0; b < curveBindings.Length; b++)
                    {
                        var binding = curveBindings[b];
                        if (!BindingMatchesQuery(binding.path, binding.propertyName, binding.type, query)) continue;
                        AppendDirectHit(hits, dedupe, animator, animatorInfo.animatorPath, layerIndex,
                            animator.GetLayerName(layerIndex), statePath, clip, clipPath, activation,
                            binding.path, binding.propertyName,
                            binding.type != null ? binding.type.FullName : string.Empty);
                    }

                    var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                    for (int b = 0; b < objectBindings.Length; b++)
                    {
                        var binding = objectBindings[b];
                        if (!BindingMatchesQuery(binding.path, binding.propertyName, binding.type, query)) continue;
                        AppendDirectHit(hits, dedupe, animator, animatorInfo.animatorPath, layerIndex,
                            animator.GetLayerName(layerIndex), statePath, clip, clipPath, activation,
                            binding.path, binding.propertyName,
                            binding.type != null ? binding.type.FullName : string.Empty);
                    }
                }
            }
        }

        return hits;
    }

    private void SearchBindingsInGraph(
        PlayableGraph graph, Animator animator, AnimatorLinkInfo animatorInfo,
        string query, List<BindingSearchHit> hits, HashSet<string> dedupe)
    {
        if (!graph.IsValid()) return;
        int outputCount = graph.GetOutputCount();
        for (int o = 0; o < outputCount; o++)
        {
            var output = graph.GetOutput(o);
            if (!output.IsPlayableOutputOfType<AnimationPlayableOutput>()) continue;
            var source = output.GetSourcePlayable();
            if (!source.IsValid()) continue;
            SearchBindingsInPlayable(source, animator, animatorInfo, query, hits, dedupe, new HashSet<PlayableHandle>());
        }
    }

    private void SearchBindingsInPlayable(
        Playable playable, Animator animator, AnimatorLinkInfo animatorInfo,
        string query, List<BindingSearchHit> hits, HashSet<string> dedupe, HashSet<PlayableHandle> visited)
    {
        if (!playable.IsValid() || !visited.Add(playable.GetHandle())) return;

        if (playable.IsPlayableOfType<AnimatorControllerPlayable>())
        {
            var acPlayable = (AnimatorControllerPlayable)playable;
            int layerCount = acPlayable.GetLayerCount();
            for (int l = 0; l < layerCount; l++)
            {
                float layerWeight = l == 0 ? 1f : acPlayable.GetLayerWeight(l);
                if (layerWeight <= 0.0001f) continue;

                var stateInfo = acPlayable.GetCurrentAnimatorStateInfo(l);
                string statePath = "[UNKNOWN]#" + stateInfo.fullPathHash;
                var clipInfos = acPlayable.GetCurrentAnimatorClipInfo(l);

                for (int c = 0; c < clipInfos.Length; c++)
                {
                    var clipInfo = clipInfos[c];
                    var clip = clipInfo.clip;
                    if (clip == null) continue;
                    float activation = layerWeight * clipInfo.weight;
                    if (activation <= 0.0001f) continue;

                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    var curveBindings = AnimationUtility.GetCurveBindings(clip);
                    for (int b = 0; b < curveBindings.Length; b++)
                    {
                        var binding = curveBindings[b];
                        if (!BindingMatchesQuery(binding.path, binding.propertyName, binding.type, query)) continue;
                        AppendDirectHit(hits, dedupe, animator, animatorInfo.animatorPath, l,
                            acPlayable.GetLayerName(l), statePath, clip, clipPath, activation,
                            binding.path, binding.propertyName,
                            binding.type != null ? binding.type.FullName : string.Empty);
                    }

                    var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                    for (int b = 0; b < objectBindings.Length; b++)
                    {
                        var binding = objectBindings[b];
                        if (!BindingMatchesQuery(binding.path, binding.propertyName, binding.type, query)) continue;
                        AppendDirectHit(hits, dedupe, animator, animatorInfo.animatorPath, l,
                            acPlayable.GetLayerName(l), statePath, clip, clipPath, activation,
                            binding.path, binding.propertyName,
                            binding.type != null ? binding.type.FullName : string.Empty);
                    }
                }
            }
        }

        int inputCount = playable.GetInputCount();
        for (int i = 0; i < inputCount; i++)
        {
            SearchBindingsInPlayable(playable.GetInput(i), animator, animatorInfo, query, hits, dedupe, visited);
        }
    }

    private static AnimatorController ResolveAnimatorController(RuntimeAnimatorController runtimeController)
    {
        if (runtimeController == null) return null;

        RuntimeAnimatorController current = runtimeController;
        int guard = 0;
        while (current != null && guard < 8)
        {
            var direct = current as AnimatorController;
            if (direct != null) return direct;

            var overrideController = current as AnimatorOverrideController;
            if (overrideController == null) break;
            current = overrideController.runtimeAnimatorController;
            guard++;
        }

        return null;
    }

    private static LiveParameterValue ReadParameter(Animator animator, string paramName)
    {
        var output = new LiveParameterValue
        {
            name = paramName,
            type = AnimatorControllerParameterType.Float
        };

        if (animator == null || string.IsNullOrWhiteSpace(paramName)) return output;
        var param = animator.parameters.FirstOrDefault(p => string.Equals(p.name, paramName, StringComparison.Ordinal));
        if (param == null) return output;

        output.type = param.type;
        int hash = Animator.StringToHash(param.name);
        switch (param.type)
        {
            case AnimatorControllerParameterType.Float:
                output.floatValue = animator.GetFloat(hash);
                break;
            case AnimatorControllerParameterType.Int:
                output.intValue = animator.GetInteger(hash);
                break;
            case AnimatorControllerParameterType.Bool:
            case AnimatorControllerParameterType.Trigger:
                output.boolValue = animator.GetBool(hash);
                break;
        }

        return output;
    }

    private static bool BindingMatchesQuery(string bindingPath, string propertyName, Type bindingType, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        if (q.Length == 0) return false;

        var corpus = ((bindingPath ?? string.Empty) + "|" +
                      (propertyName ?? string.Empty) + "|" +
                      (bindingType != null ? bindingType.FullName : string.Empty)).ToLowerInvariant();

        string qLower = q.ToLowerInvariant();

        // First try plain substring match (handles exact path/property matches)
        if (corpus.Contains(qLower)) return true;

        // Also try alphanumeric-only normalized match to catch humanoid muscle properties
        // e.g. query "LeftEye" should match property "Left Eye Down-Up"
        string normalizedCorpus = NormalizeAlphanumeric(corpus);
        string normalizedQuery = NormalizeAlphanumeric(qLower);
        return normalizedCorpus.Contains(normalizedQuery);
    }

    private static string NormalizeAlphanumeric(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var chars = new List<char>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsLetterOrDigit(value[i]))
                chars.Add(char.ToLowerInvariant(value[i]));
        }
        return new string(chars.ToArray());
    }

    private static void AppendHit(
        IList<BindingSearchHit> hits,
        ISet<string> dedupe,
        LiveStateInfo state,
        LiveClipActivation clip,
        string bindingPath,
        string propertyName,
        string typeName)
    {
        if (hits == null || dedupe == null || state == null || clip == null) return;

        string key = (state.animatorPath ?? string.Empty) + "|" +
                     state.layerIndex + "|" +
                     (state.fullPathName ?? string.Empty) + "|" +
                     (clip.clipAssetPath ?? string.Empty) + "|" +
                     (bindingPath ?? string.Empty) + "|" +
                     (propertyName ?? string.Empty) + "|" +
                     (typeName ?? string.Empty);
        if (!dedupe.Add(key)) return;

        hits.Add(new BindingSearchHit
        {
            animator = state.animator,
            animatorPath = state.animatorPath,
            layerIndex = state.layerIndex,
            layerName = state.layerName,
            statePath = state.fullPathName,
            clipName = clip.clipName,
            clipPath = clip.clipAssetPath,
            clipActivation = clip.activation,
            bindingPath = bindingPath,
            bindingProperty = propertyName,
            bindingTypeName = typeName
        });
    }

    private static void AppendDirectHit(
        IList<BindingSearchHit> hits,
        ISet<string> dedupe,
        Animator animator,
        string animatorPath,
        int layerIndex,
        string layerName,
        string statePath,
        AnimationClip clip,
        string clipPath,
        float activation,
        string bindingPath,
        string propertyName,
        string typeName)
    {
        if (hits == null || dedupe == null || animator == null || clip == null) return;

        string key = (animatorPath ?? string.Empty) + "|" +
                     layerIndex + "|" +
                     (statePath ?? string.Empty) + "|" +
                     (clipPath ?? string.Empty) + "|" +
                     (bindingPath ?? string.Empty) + "|" +
                     (propertyName ?? string.Empty) + "|" +
                     (typeName ?? string.Empty);
        if (!dedupe.Add(key)) return;

        hits.Add(new BindingSearchHit
        {
            animator = animator,
            animatorPath = animatorPath,
            layerIndex = layerIndex,
            layerName = layerName,
            statePath = statePath,
            clipName = clip.name,
            clipPath = clipPath,
            clipActivation = activation,
            bindingPath = bindingPath,
            bindingProperty = propertyName,
            bindingTypeName = typeName
        });
    }

    private static string CalculatePath(Transform root, Transform current)
    {
        if (root == null || current == null) return string.Empty;
        if (root == current) return root.name;

        var stack = new Stack<string>();
        var cursor = current;
        while (cursor != null && cursor != root)
        {
            stack.Push(cursor.name);
            cursor = cursor.parent;
        }

        if (cursor == root) stack.Push(root.name);
        return string.Join("/", stack.ToArray());
    }
}
#endif
