#if UNITY_EDITOR

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;

public partial class BlendShapeLinkService
{
    // Track layers already copied to FX during the current build session to avoid duplicates
    // across multiple ApplyPlannedLinks calls (version links + manual links)
    private static HashSet<string> _layersCopiedToFxThisSession = new HashSet<string>();
    private static int _lastBuildSessionFrame = -1;
    public void ClearSessionTracking()
    {
        _layersCopiedToFxThisSession.Clear();
        _lastBuildSessionFrame = Time.frameCount;
    }

    private static ApplyResult ApplyPlannedLinks(GameObject avatarRoot, List<PlannedLink> plannedLinks, string sourceLabel)
    {
        if (avatarRoot == null) return FailApply("Avatar root is null.");
        if (plannedLinks == null || plannedLinks.Count == 0) return FailApply("No corrective links to apply.");
        
        // Safety check to ensure session tracking is reset if this is clearly a new frame/build
        if (Time.frameCount != _lastBuildSessionFrame)
        {
            _layersCopiedToFxThisSession.Clear();
            _lastBuildSessionFrame = Time.frameCount;
        }

        var controllers = CollectVrcFuryBuiltControllers(avatarRoot);
        if (controllers.Count == 0)
        {
            return FailApply("No VRCFury temporary AnimatorController found on avatar descriptor.");
        }

        var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
        var changedControllersForDescriptor = new HashSet<AnimatorController>();

        // VRChat's Playable Layers system only allows blendshape animations in the FX layer.
        // If we modify layers in Additive/Gesture/etc with blendshape links, they won't work.
        // We need to identify these and move them to FX.
        var fxController = FindControllerForLayerType(descriptor, VRCAvatarDescriptor.AnimLayerType.FX);
        var additiveController = FindControllerForLayerType(descriptor, VRCAvatarDescriptor.AnimLayerType.Additive);
        
        // Track layers that need to be copied from Additive/etc to FX (use dictionary to avoid duplicates)
        var layersToCopyToFx = new Dictionary<string, (AnimatorController sourceController, int layerIndex, AnimatorControllerLayer layer)>();

        int linksProcessed = 0;
        int clipsWrapped = 0;
        int statesRewritten = 0;
        bool anyControllerChanged = false;
        var changedControllers = new HashSet<AnimatorController>();

        foreach (var planned in plannedLinks)
        {
            bool thisLinkChangedAnyController = false;
            MCBLogger.Log($"[MCB] Processing link: toFix='{planned.toFixName}' fixedBy='{planned.fixedByName}' across {controllers.Count} controllers");
            foreach (var controller in controllers)
            {
                MCBLogger.Log($"[MCB] Checking controller '{controller.name}' for link toFix='{planned.toFixName}'");
                if (!EnsureFloatParameter(controller, planned.factorParameterName, planned.setFactorDefaultValue, planned.factorDefaultValue, out var paramError))
                {
                    Debug.LogWarning("[MCB] " + paramError);
                    continue;
                }

                bool controllerChanged = false;
                var clipCache = new Dictionary<AnimationClip, Motion>();
                var visitedStateMachines = new HashSet<AnimatorStateMachine>();
                var visitedTrees = new HashSet<BlendTree>();

                var layers = controller.layers;
                bool layersArrayChanged = false;
                for (int i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    if (layer?.stateMachine == null) continue;
                    if (RewriteStateMachine(controller, layer.stateMachine, planned, clipCache, visitedStateMachines, visitedTrees, ref clipsWrapped, ref statesRewritten))
                    {
                        controllerChanged = true;
                        thisLinkChangedAnyController = true;

                        // If this is a non-FX controller (like Additive), mark layer for migration to FX
                        bool isFxController = (controller == fxController);
                        bool hasFxController = (fxController != null);
                        
                        if (!isFxController && hasFxController)
                        {
                            // Use a unique key to avoid duplicating the same layer if multiple links affect it
                            // Also check against layers already copied in this build session (across version + manual links)
                            string layerKey = $"{controller.name}:{i}:{layer.name}";
                            if (!layersToCopyToFx.ContainsKey(layerKey) && !_layersCopiedToFxThisSession.Contains(layerKey))
                            {
                                MCBLogger.Log($"[MCB] Layer '{layer.name}' in '{controller.name}' contains blendshape animations - will copy to FX controller.");
                                layersToCopyToFx[layerKey] = (controller, i, layer);
                            }
                        }
                        else if (!hasFxController)
                        {
                            MCBLogger.LogWarning($"[MCB] No FX controller found! Cannot copy layer '{layer.name}' for blendshape animations.");
                        }
                        
                        // Clear mask regardless (for FX layers with restrictive masks)
                        if (layer.avatarMask != null)
                        {
                            MCBLogger.Log($"[MCB] Clearing mask '{layer.avatarMask.name}' from layer '{layer.name}'.");
                            layer.avatarMask = null;
                            layers[i] = layer;
                            layersArrayChanged = true;
                        }
                    }
                }

                if (!controllerChanged) continue;
                if (layersArrayChanged)
                {
                    controller.layers = layers;
                }
                changedControllers.Add(controller);
                changedControllersForDescriptor.Add(controller);
                anyControllerChanged = true;
                EditorUtility.SetDirty(controller);

                // Register applied link for debug window
                int ctrlId = controller.GetInstanceID();
                if (!_appliedLinksRegistry.TryGetValue(ctrlId, out var records))
                {
                    records = new List<AppliedLinkRecord>();
                    _appliedLinksRegistry[ctrlId] = records;
                }
                records.Add(new AppliedLinkRecord
                {
                    controllerName = controller.name,
                    controllerAssetPath = AssetDatabase.GetAssetPath(controller),
                    factorParameterName = planned.factorParameterName,
                    targetRendererPath = planned.targetRendererPath,
                    toFixName = planned.toFixName,
                    fixedByName = planned.fixedByName,
                    sourceLabel = sourceLabel
                });
            }

            if (thisLinkChangedAnyController) linksProcessed++;
        }
        
        // Copy affected layers from non-FX controllers to FX (only once per unique layer)
        if (layersToCopyToFx.Count > 0 && fxController != null)
        {
            CopyLayersToFxController(layersToCopyToFx.Values.ToList(), fxController);
            changedControllers.Add(fxController);
            anyControllerChanged = true;
        }

        if (anyControllerChanged)
        {
            if (descriptor != null)
            {
                bool descriptorChanged = false;
                void ClearDescriptorLayerMasks(VRCAvatarDescriptor.CustomAnimLayer[] layers)
                {
                    if (layers == null) return;
                    for (int i = 0; i < layers.Length; i++)
                    {
                        if (layers[i].animatorController is AnimatorController ac &&
                            changedControllersForDescriptor.Contains(ac) &&
                            layers[i].mask != null)
                        {
                            MCBLogger.Log($"[MCB] Clearing mask from VRCAvatarDescriptor layer type '{layers[i].type}' because its controller '{ac.name}' was modified by a corrective link.");
                            layers[i].mask = null;
                            descriptorChanged = true;
                        }
                    }
                }

                if (descriptor.baseAnimationLayers != null)
                {
                    var layersArr = descriptor.baseAnimationLayers;
                    ClearDescriptorLayerMasks(layersArr);
                    descriptor.baseAnimationLayers = layersArr;
                }
                if (descriptor.specialAnimationLayers != null)
                {
                    var layersArr = descriptor.specialAnimationLayers;
                    ClearDescriptorLayerMasks(layersArr);
                    descriptor.specialAnimationLayers = layersArr;
                }

                if (descriptorChanged)
                {
                    EditorUtility.SetDirty(descriptor);
                }
            }

            AssetDatabase.SaveAssets();
            // Note: Layer migration to FX ensures blendshape animations work in VRChat's Playable Layer system.
            // No need to manipulate PlayableGraph at runtime - the fix is applied at build time.
            return new ApplyResult
            {
                success = true,
                linksProcessed = linksProcessed,
                controllersProcessed = changedControllers.Count,
                clipsWrapped = clipsWrapped,
                statesRewritten = statesRewritten,
                message = $"Applied {sourceLabel} corrective links: {linksProcessed} link(s), {changedControllers.Count} controller(s), {clipsWrapped} wrapped clip(s), {statesRewritten} rewritten state/tree motion reference(s)."
            };
        }

        return FailApply("No matching blendshape curves or animation motions were found in VRCFury temporary controllers.");
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

        var animator = avatarRoot.GetComponentInChildren<Animator>(true);
        if (animator?.runtimeAnimatorController is AnimatorController controllerFromAnimator && IsVrcFuryBuiltController(controllerFromAnimator))
        {
            found.Add(controllerFromAnimator);
        }

        return found.ToList();
    }

    private static void CollectFromLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers, ISet<AnimatorController> found)
    {
        if (layers == null) return;
        foreach (var layer in layers)
        {
            if (layer.isDefault) continue;
            var ctrl = layer.animatorController as AnimatorController;
            if (ctrl == null) continue;
            if (!IsVrcFuryBuiltController(ctrl)) continue;
            found.Add(ctrl);
        }
    }

    private static bool IsVrcFuryBuiltController(AnimatorController controller)
    {
        string path = AssetDatabase.GetAssetPath(controller);
        if (string.IsNullOrEmpty(path)) return false;
        string normalized = path.Replace("\\", "/");
        return normalized.IndexOf("com.vrcfury.temp", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static AnimatorController FindControllerForLayerType(VRCAvatarDescriptor descriptor, VRCAvatarDescriptor.AnimLayerType layerType)
    {
        if (descriptor == null) return null;
        
        foreach (var layer in descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
        {
            if (layer.type == layerType && !layer.isDefault && layer.animatorController is AnimatorController ctrl)
                return ctrl;
        }
        foreach (var layer in descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
        {
            if (layer.type == layerType && !layer.isDefault && layer.animatorController is AnimatorController ctrl)
                return ctrl;
        }
        return null;
    }

    private static void CopyLayersToFxController(
        List<(AnimatorController sourceController, int layerIndex, AnimatorControllerLayer layer)> layersToMove,
        AnimatorController fxController)
    {
        if (layersToMove == null || layersToMove.Count == 0 || fxController == null) return;

        // VRChat's Playable Layers have strict rules:
        // - Additive/Gesture/Base/Action: Only affects transforms (bones/muscles)
        // - FX: Affects everything EXCEPT transforms (blendshapes, materials, etc.)
        //
        // When we add blendshape animations to a non-FX layer (like eye tracking in Additive),
        // we need to COPY (not move) the layer to FX so:
        // 1. The original layer keeps working for bone/muscle animations
        // 2. The FX copy handles the blendshape animations
        //
        // We create a simplified copy in FX that only contains the blendshape-affecting clips.
        
        var currentFxLayerNames = new HashSet<string>(fxController.layers.Select(l => l.name));

        foreach (var (sourceController, layerIndex, layer) in layersToMove)
        {
            if (sourceController == fxController) continue;
            
            // Use a unique key to track this layer as copied in the current session
            string layerKey = $"{sourceController.name}:{layerIndex}:{layer.name}";
            _layersCopiedToFxThisSession.Add(layerKey);

            string newLayerName = "[UP_FX] " + layer.name;
            if (currentFxLayerNames.Contains(newLayerName))
            {
                MCBLogger.Log($"[MCB] Layer '{newLayerName}' already exists in FX controller. Skipping copy (state machine is shared and already updated).");
                continue;
            }
            
            MCBLogger.Log($"[MCB] Copying layer '{layer.name}' from '{sourceController.name}' to FX controller for blendshape animations.");
            
            // Copy parameters from source controller to FX controller
            foreach (var param in sourceController.parameters)
            {
                if (!fxController.parameters.Any(p => p.name == param.name))
                {
                    fxController.AddParameter(param.name, param.type);
                    var newParam = fxController.parameters.FirstOrDefault(p => p.name == param.name);
                    if (newParam != null)
                    {
                        newParam.defaultBool = param.defaultBool;
                        newParam.defaultFloat = param.defaultFloat;
                        newParam.defaultInt = param.defaultInt;
                    }
                }
            }
            
            // Create a copy of the layer for FX
            // Note: We share the same state machine reference. This works because:
            // - The state machine contains blend trees with both original clips (transform animations)
            //   and variant clips (blendshape animations)
            // - VRChat's FX layer will automatically filter to only process non-transform animations
            // - The original layer in Additive will process only transform animations
            var newLayer = new AnimatorControllerLayer
            {
                name = "[UP_FX] " + layer.name,
                stateMachine = layer.stateMachine, // Share the state machine reference
                avatarMask = null, // No mask - FX needs access to all blendshapes
                blendingMode = AnimatorLayerBlendingMode.Override,
                defaultWeight = layer.defaultWeight,
                syncedLayerIndex = -1,
                syncedLayerAffectsTiming = false
            };
            
            // Add to FX controller
            var fxLayers = fxController.layers.ToList();
            fxLayers.Add(newLayer);
            fxController.layers = fxLayers.ToArray();
        }
        
        // Note: We do NOT remove the original layers from the source controller.
        // They need to stay for the bone/muscle animations to work.
        
        EditorUtility.SetDirty(fxController);
    }

    private static bool EnsureFloatParameter(AnimatorController controller, string parameterName, bool setDefaultValue, float defaultValue, out string error)
    {
        error = null;
        var parameters = controller.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.name == parameterName)
            {
                if (p.type != AnimatorControllerParameterType.Float)
                {
                    error = $"Controller '{controller.name}' already has parameter '{parameterName}' but it is not Float.";
                    return false;
                }

                if (setDefaultValue && Mathf.Abs(p.defaultFloat - defaultValue) > 0.0001f)
                {
                    p.defaultFloat = defaultValue;
                    controller.parameters = parameters; // Reassign because parameters returns a copy
                    EditorUtility.SetDirty(controller);
                }

                return true;
            }
        }

        // Parameter does not exist, add it
        var newParam = new AnimatorControllerParameter
        {
            name = parameterName,
            type = AnimatorControllerParameterType.Float,
            defaultFloat = setDefaultValue ? defaultValue : 0f
        };
        controller.AddParameter(newParam);
        EditorUtility.SetDirty(controller);
        return true;
    }

    private static bool RewriteStateMachine(
        AnimatorController controller,
        AnimatorStateMachine stateMachine,
        PlannedLink planned,
        IDictionary<AnimationClip, Motion> clipCache,
        ISet<AnimatorStateMachine> visitedStateMachines,
        ISet<BlendTree> visitedTrees,
        ref int clipsWrapped,
        ref int statesRewritten)
    {
        if (visitedStateMachines.Contains(stateMachine)) return false;
        visitedStateMachines.Add(stateMachine);

        bool changed = false;
        bool hasExistingWrapper = false;

        foreach (var childState in stateMachine.states)
        {
            var state = childState.state;
            if (state == null || state.motion == null) continue;

            // Check if motion already contains a wrapper for this link (from previous builds)
            if (ContainsWrapperForFactor(state.motion, planned.factorParameterName, visitedTrees))
            {
                hasExistingWrapper = true;
            }

            bool motionChanged = false;
            Motion rewritten = RewriteMotion(controller, state.motion, planned, clipCache, visitedTrees, ref clipsWrapped, ref motionChanged);

            if (motionChanged || (rewritten != null && rewritten != state.motion))
            {
                state.motion = rewritten;
                EditorUtility.SetDirty(state);
                statesRewritten++;
                changed = true;
            }
        }

        foreach (var childMachine in stateMachine.stateMachines)
        {
            if (childMachine.stateMachine == null) continue;
            if (RewriteStateMachine(controller, childMachine.stateMachine, planned, clipCache, visitedStateMachines, visitedTrees, ref clipsWrapped, ref statesRewritten))
            {
                changed = true;
            }
        }

        // Return true if we made changes OR if we found existing wrappers (mask still needs clearing)
        return changed || hasExistingWrapper;
    }

    // Recursively checks if a motion or its children contain a wrapper tree for the given factor parameter
    private static bool ContainsWrapperForFactor(Motion motion, string factorParameterName, ISet<BlendTree> visited)
    {
        if (motion == null) return false;
        
        if (motion is BlendTree tree)
        {
            if (visited.Contains(tree)) return false;
            // Don't add to visited here - we're just checking, not modifying
            
            if (IsWrapperTree(tree, factorParameterName))
            {
                return true;
            }
            
            var children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                if (ContainsWrapperForFactor(children[i].motion, factorParameterName, visited))
                {
                    return true;
                }
            }
        }
        
        return false;
    }

    private static Motion RewriteMotion(
        AnimatorController controller,
        Motion motion,
        PlannedLink planned,
        IDictionary<AnimationClip, Motion> clipCache,
        ISet<BlendTree> visitedTrees,
        ref int clipsWrapped,
        ref bool changed)
    {
        if (motion == null) return null;

        if (motion is BlendTree tree)
        {
            if (visitedTrees.Contains(tree)) return tree;
            visitedTrees.Add(tree);

            if (IsWrapperTree(tree, planned.factorParameterName))
            {
                // Already wrapped for this factor. Apply subsequent links only to the
                // variant child so links can stack without re-wrapping.
                var wrapperChildren = tree.children;
                if (wrapperChildren != null && wrapperChildren.Length == 2)
                {
                    var variantMotion = wrapperChildren[1].motion;
                    bool variantChanged = false;
                    var rewrittenVariant = RewriteMotion(controller, variantMotion, planned, clipCache, visitedTrees, ref clipsWrapped, ref variantChanged);
                    if (variantChanged || (rewrittenVariant != null && rewrittenVariant != variantMotion))
                    {
                        wrapperChildren[1].motion = rewrittenVariant;
                        tree.children = wrapperChildren;
                        EditorUtility.SetDirty(tree);
                        changed = true;
                    }
                }

                return tree;
            }

            bool childrenChanged = false;
            var children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                var childMotion = children[i].motion;
                bool childChanged = false;
                var rewritten = RewriteMotion(controller, childMotion, planned, clipCache, visitedTrees, ref clipsWrapped, ref childChanged);
                if (childChanged || (rewritten != null && rewritten != childMotion))
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
            return cached;
        }

        var variantClip = TryCreateVariantClip(controller, clip, planned);
        if (variantClip == null)
        {
            clipCache[clip] = clip;
            return clip;
        }

        var wrapperTree = CreateWrapperTree(controller, clip, variantClip, planned.factorParameterName);
        clipCache[clip] = wrapperTree;
        clipsWrapped++;
        changed = true;
        return wrapperTree;
    }

    private static AnimationClip TryCreateVariantClip(AnimatorController controller, AnimationClip sourceClip, PlannedLink planned)
    {
        if (sourceClip == null)
        {
            return null;
        }
        if (planned.fixedByType == CorrectiveActivationType.Animation && planned.fixedByAnimationClip == null)
        {
            return null;
        }
        if (planned.toFixType == CorrectiveActivationType.Animation)
        {
            bool matches = DoesClipMatchAnimationTarget(sourceClip, planned.toFixName, planned.toFixAnimationSignature);
            if (!matches) return null;
        }
        
        if (planned.toFixType == CorrectiveActivationType.Blendshape && planned.fixedByType == CorrectiveActivationType.Blendshape)
        {
            if (!TryGetBlendshapeCurve(sourceClip, planned.sourcePath, planned.sourceProperty, out var sourceCurve)) return null;
            return CreateVariantClipWithBlendshapeCurve(controller, sourceClip, planned.destinationPath, planned.destinationProperty, sourceCurve);
        }

        if (planned.toFixType == CorrectiveActivationType.Animation && planned.fixedByType == CorrectiveActivationType.Blendshape)
        {
            if (string.IsNullOrWhiteSpace(planned.destinationPath) || string.IsNullOrWhiteSpace(planned.destinationProperty)) return null;
            return CreateVariantClipWithConstantBlendshape(controller, sourceClip, planned.destinationPath, planned.destinationProperty, 100f);
        }

        if (planned.toFixType == CorrectiveActivationType.Blendshape && planned.fixedByType == CorrectiveActivationType.Animation)
        {
            if (!TryGetBlendshapeCurve(sourceClip, planned.sourcePath, planned.sourceProperty, out var sourceCurve)) return null;
            return CreateVariantClipWithScaledAnimationOverlay(controller, sourceClip, planned.fixedByAnimationClip, sourceCurve);
        }

        if (planned.toFixType == CorrectiveActivationType.Animation && planned.fixedByType == CorrectiveActivationType.Animation)
        {
            return CreateVariantClipWithAnimationOverlay(controller, sourceClip, planned.fixedByAnimationClip);
        }

        return null;
    }

    private static bool DoesClipMatchAnimationTarget(AnimationClip clip, string targetName,
        List<AnimationBindingSignature> targetSignature)
    {
        if (clip == null || string.IsNullOrWhiteSpace(targetName)) return false;

        // Prefer semantic matching: same animated bindings with same sampled values.
        if (targetSignature != null && targetSignature.Count > 0 && DoesClipMatchSignature(clip, targetSignature))
        {
            return true;
        }

        string clipName = NormalizeAnimationKey(clip.name);
        string target = NormalizeAnimationKey(targetName);
        if (string.Equals(clipName, target, StringComparison.Ordinal)) return true;

        string clipPath = AssetDatabase.GetAssetPath(clip);
        string fileName = string.IsNullOrWhiteSpace(clipPath)
            ? string.Empty
            : NormalizeAnimationKey(System.IO.Path.GetFileNameWithoutExtension(clipPath));

        return string.Equals(fileName, target, StringComparison.Ordinal);
    }

    private static bool DoesClipMatchSignature(AnimationClip clip, List<AnimationBindingSignature> signature)
    {
        if (clip == null || signature == null || signature.Count == 0) return false;

        var candidates = CollectClipCurveCandidates(clip);
        if (candidates.Count == 0) return false;

        const float Epsilon = 0.001f;
        foreach (var sig in signature)
        {
            if (sig.sampleTimes == null || sig.sampleValues == null) continue;
            if (sig.sampleTimes.Length != sig.sampleValues.Length) continue;

            bool hasExactPath = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!IsSameTypeAndProperty(sig, candidate)) continue;
                if (!string.Equals(sig.path ?? string.Empty, candidate.path ?? string.Empty, StringComparison.Ordinal))
                    continue;

                hasExactPath = true;
                if (DoesCurveMatchSamples(candidate.curve, sig.sampleTimes, sig.sampleValues, Epsilon)) return true;
            }

            if (hasExactPath) continue;

            // Fallback for VRCFury-remapped paths: require same type/property and sampled values.
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!IsSameTypeAndProperty(sig, candidate)) continue;
                if (DoesCurveMatchSamples(candidate.curve, sig.sampleTimes, sig.sampleValues, Epsilon)) return true;
            }
        }

        return false;
    }

    private static List<CurveCandidate> CollectClipCurveCandidates(AnimationClip clip)
    {
        var output = new List<CurveCandidate>();
        if (clip == null) return output;

        var dedupe = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            TryAppendCurveCandidate(output, dedupe, binding.path, binding.type, binding.propertyName, curve);
        }

        return output;
    }

    private static void TryAppendCurveCandidate(
        List<CurveCandidate> output,
        HashSet<string> dedupe,
        string path,
        Type type,
        string propertyName,
        AnimationCurve curve)
    {
        if (output == null || dedupe == null) return;
        if (curve == null || curve.length == 0) return;
        if (string.IsNullOrWhiteSpace(propertyName)) return;

        string normalizedPath = path ?? string.Empty;
        string normalizedProperty = propertyName ?? string.Empty;
        string typeName = type != null ? type.FullName : string.Empty;
        string dedupeKey = normalizedPath + "|" + normalizedProperty + "|" + typeName;
        if (!dedupe.Add(dedupeKey)) return;

        output.Add(new CurveCandidate
        {
            path = normalizedPath,
            type = type,
            propertyName = normalizedProperty,
            curve = curve
        });
    }

    private static bool IsSameTypeAndProperty(AnimationBindingSignature signature, CurveCandidate candidate)
    {
        if (!string.Equals(signature.propertyName ?? string.Empty, candidate.propertyName ?? string.Empty, StringComparison.Ordinal))
            return false;

        // Allow one side to be null when Unity returns reduced type metadata.
        if (signature.type == null || candidate.type == null) return true;
        return signature.type == candidate.type;
    }

    private static bool DoesCurveMatchSamples(AnimationCurve curve, float[] sampleTimes, float[] sampleValues, float epsilon)
    {
        if (curve == null || sampleTimes == null || sampleValues == null) return false;
        if (sampleTimes.Length != sampleValues.Length) return false;

        for (int i = 0; i < sampleTimes.Length; i++)
        {
            float actual = curve.Evaluate(sampleTimes[i]);
            float expected = sampleValues[i];
            if (Mathf.Abs(actual - expected) > epsilon) return false;
        }

        return true;
    }

    private static bool IsWrapperTree(BlendTree tree, string factorParameterName)
    {
        if (tree == null) return false;
        if (!tree.name.StartsWith(WrapperPrefix, StringComparison.Ordinal)) return false;
        if (tree.blendType != BlendTreeType.Simple1D) return false;
        if (!string.Equals(tree.blendParameter, factorParameterName, StringComparison.Ordinal)) return false;
        var children = tree.children;
        return children != null && children.Length == 2;
    }

}
#endif
