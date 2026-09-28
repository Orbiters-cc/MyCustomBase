#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;

public partial class BlendShapeLinkService
{
    private static Dictionary<string, List<AnimationClip>> _animationClipLookupCache;
    private static double _animationClipLookupCacheTimestamp;
    private static bool _animationClipLookupProjectChangedHooked;
    private const double AnimationClipLookupCacheTtlSeconds = 2.0d;

    private static List<VersionLink> BuildVersionPlannedLinks(GameObject avatarRoot, CustomBaseVersion version,
        bool useCustomSliderSelection, List<string> customSliderSelectionNames,
        bool includeAnimationSignatures = true)
    {
        var output = new List<VersionLink>();
        if (avatarRoot == null || version?.customBlendshapes == null || version.customBlendshapes.Length == 0)
            return output;

        var selectedSliders = BuildSelectedSliderSet(version.customBlendshapes, useCustomSliderSelection,
            customSliderSelectionNames);
        var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true) ??
                        Array.Empty<SkinnedMeshRenderer>();
        var customBase = FindCustomBase(avatarRoot);
        Dictionary<string, List<AnimationClip>> animationClipLookup = null;
        Func<Dictionary<string, List<AnimationClip>>> getAnimationClipLookup = () =>
            animationClipLookup ?? (animationClipLookup = GetAnimationClipLookupCached());
        var dedupe = new HashSet<string>(StringComparer.Ordinal);
        foreach (var driver in version.customBlendshapes)
        {
            if (driver == null || string.IsNullOrWhiteSpace(driver.name)) continue;
            if (driver.correctiveBlendshapes == null || driver.correctiveBlendshapes.Length == 0) continue;

            bool isSliderFactor = driver.isSlider && selectedSliders.Contains(driver.name);
            float globalConstantFactor = GetIntendedFactor(customBase, driver, null, renderers);

            foreach (var corrective in driver.correctiveBlendshapes)
            {
                if (corrective == null) continue;
                if (string.IsNullOrWhiteSpace(corrective.toFix)) continue;
                if (string.IsNullOrWhiteSpace(corrective.fixedBy)) continue;

                AnimationClipSignature toFixSignature = null;
                if (includeAnimationSignatures &&
                    corrective.toFixType == CorrectiveActivationType.Animation &&
                    TryResolveAnimationClipByName(corrective.toFix, getAnimationClipLookup(), out var toFixClip))
                {
                    toFixSignature = AnimationClipSignature.Build(toFixClip);
                }

                AnimationClip fixedByClip = null;
                if (corrective.fixedByType == CorrectiveActivationType.Animation &&
                    !TryResolveAnimationClipByName(corrective.fixedBy, getAnimationClipLookup(), out fixedByClip))
                {
                    continue;
                }

                bool needsRenderer = corrective.toFixType == CorrectiveActivationType.Blendshape ||
                                     corrective.fixedByType == CorrectiveActivationType.Blendshape;
                if (!needsRenderer)
                {
                    string factorParamNoRenderer = isSliderFactor
                        ? VRCFuryService.GetSliderGlobalParamName(driver.name)
                        : BuildConstantFactorParamName(driver.name, "anim", corrective.toFixType.ToString(),
                            corrective.toFix, corrective.fixedByType.ToString(), corrective.fixedBy);

                    string keyNoRenderer = string.Join("|", "", corrective.toFixType, corrective.toFix,
                        corrective.fixedByType, corrective.fixedBy, factorParamNoRenderer);
                    if (!dedupe.Add(keyNoRenderer)) continue;

                    output.Add(new VersionLink
                    {
                        link = CorrectiveLink(string.Empty, corrective.toFixType, corrective.toFix, toFixSignature,
                            corrective.fixedByType, corrective.fixedBy, fixedByClip, factorParamNoRenderer,
                            !isSliderFactor, globalConstantFactor),
                        driverBlendshape = driver.name
                    });
                    continue;
                }

                foreach (var renderer in renderers)
                {
                    if (renderer == null || renderer.sharedMesh == null) continue;
                    var mesh = renderer.sharedMesh;
                    if (corrective.toFixType == CorrectiveActivationType.Blendshape &&
                        mesh.GetBlendShapeIndex(corrective.toFix) < 0) continue;
                    if (corrective.fixedByType == CorrectiveActivationType.Blendshape &&
                        mesh.GetBlendShapeIndex(corrective.fixedBy) < 0) continue;

                    string rendererPath =
                        AnimationUtility.CalculateTransformPath(renderer.transform, avatarRoot.transform);
                    if (string.IsNullOrWhiteSpace(rendererPath)) continue;

                    float constantFactor = GetIntendedFactor(customBase, driver, renderer, null);
                    string factorParam = isSliderFactor
                        ? VRCFuryService.GetSliderGlobalParamName(driver.name)
                        : BuildConstantFactorParamName(driver.name, rendererPath, corrective.toFixType.ToString(),
                            corrective.toFix, corrective.fixedByType.ToString(), corrective.fixedBy);

                    string key = string.Join("|", rendererPath, corrective.toFixType, corrective.toFix,
                        corrective.fixedByType, corrective.fixedBy, factorParam);
                    if (!dedupe.Add(key)) continue;

                    output.Add(new VersionLink
                    {
                        link = CorrectiveLink(rendererPath, corrective.toFixType, corrective.toFix, toFixSignature,
                            corrective.fixedByType, corrective.fixedBy, fixedByClip, factorParam,
                            !isSliderFactor, constantFactor),
                        driverBlendshape = driver.name
                    });
                }
            }
        }

        return output;
    }

    private static HashSet<string> BuildSelectedSliderSet(CustomBlendshapeEntry[] entries,
        bool useCustomSliderSelection, List<string> customSliderSelectionNames)
    {
        if (entries == null) return new HashSet<string>(StringComparer.Ordinal);

        if (useCustomSliderSelection)
        {
            return new HashSet<string>(customSliderSelectionNames ?? new List<string>(), StringComparer.Ordinal);
        }

        var defaults = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry != null && entry.isSlider && entry.isSliderDefault && !string.IsNullOrWhiteSpace(entry.name))
            {
                defaults.Add(entry.name);
            }
        }

        return defaults;
    }

    private static Dictionary<string, List<AnimationClip>> BuildAnimationClipLookup()
    {
        var lookup = new Dictionary<string, List<AnimationClip>>(StringComparer.Ordinal);
        string[] guids = AssetDatabase.FindAssets("t:AnimationClip");
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null || string.IsNullOrWhiteSpace(clip.name)) continue;

            if (!lookup.TryGetValue(clip.name, out var list))
            {
                list = new List<AnimationClip>();
                lookup[clip.name] = list;
            }

            list.Add(clip);
        }

        foreach (var kvp in lookup)
        {
            kvp.Value.Sort((a, b) =>
                string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
        }

        return lookup;
    }

    private static Dictionary<string, List<AnimationClip>> GetAnimationClipLookupCached()
    {
        EnsureAnimationLookupProjectChangedHook();

        double now = EditorApplication.timeSinceStartup;
        if (_animationClipLookupCache == null || (now - _animationClipLookupCacheTimestamp) > AnimationClipLookupCacheTtlSeconds)
        {
            _animationClipLookupCache = BuildAnimationClipLookup();
            _animationClipLookupCacheTimestamp = now;
        }

        return _animationClipLookupCache;
    }

    private static void EnsureAnimationLookupProjectChangedHook()
    {
        if (_animationClipLookupProjectChangedHooked) return;
        _animationClipLookupProjectChangedHooked = true;
        EditorApplication.projectChanged += ClearAnimationClipLookupCache;
    }

    private static void ClearAnimationClipLookupCache()
    {
        _animationClipLookupCache = null;
        _animationClipLookupCacheTimestamp = 0d;
    }

    private static bool TryResolveAnimationClipByName(string clipName, Dictionary<string, List<AnimationClip>> lookup,
        out AnimationClip clip)
    {
        clip = null;
        if (string.IsNullOrWhiteSpace(clipName) || lookup == null) return false;
        if (lookup.TryGetValue(clipName, out var exactCandidates) && exactCandidates != null && exactCandidates.Count > 0)
        {
            clip = exactCandidates[0];
            return clip != null;
        }

        string normalizedTarget = AnimationClipSignature.NormalizeClipName(clipName);
        if (string.IsNullOrWhiteSpace(normalizedTarget)) return false;

        foreach (var pair in lookup)
        {
            if (pair.Value == null || pair.Value.Count == 0) continue;

            if (string.Equals(AnimationClipSignature.NormalizeClipName(pair.Key), normalizedTarget, StringComparison.Ordinal))
            {
                clip = pair.Value[0];
                return clip != null;
            }

            for (int i = 0; i < pair.Value.Count; i++)
            {
                var candidate = pair.Value[i];
                if (candidate == null) continue;
                string path = AssetDatabase.GetAssetPath(candidate);
                string fileName = string.IsNullOrWhiteSpace(path)
                    ? string.Empty
                    : AnimationClipSignature.NormalizeClipName(System.IO.Path.GetFileNameWithoutExtension(path));

                if (string.Equals(fileName, normalizedTarget, StringComparison.Ordinal))
                {
                    clip = candidate;
                    return clip != null;
                }
            }
        }

        var targetTokens = TokenizeAnimationName(clipName);
        if (targetTokens.Count == 0) return false;

        AnimationClip best = null;
        int bestScore = 0;
        foreach (var pair in lookup)
        {
            if (pair.Value == null || pair.Value.Count == 0) continue;

            int keyScore = ScoreAnimationNameTokenMatch(targetTokens, TokenizeAnimationName(pair.Key));
            if (keyScore > bestScore)
            {
                bestScore = keyScore;
                best = pair.Value[0];
            }

            for (int i = 0; i < pair.Value.Count; i++)
            {
                var candidate = pair.Value[i];
                if (candidate == null) continue;
                string path = AssetDatabase.GetAssetPath(candidate);
                string fileName = string.IsNullOrWhiteSpace(path)
                    ? string.Empty
                    : System.IO.Path.GetFileNameWithoutExtension(path);

                int fileScore = ScoreAnimationNameTokenMatch(targetTokens, TokenizeAnimationName(fileName));
                if (fileScore > bestScore)
                {
                    bestScore = fileScore;
                    best = candidate;
                }
            }
        }

        if (best != null && bestScore >= 2)
        {
            clip = best;
            return true;
        }

        return false;
    }

    private static int ScoreAnimationNameTokenMatch(HashSet<string> targetTokens, HashSet<string> candidateTokens)
    {
        if (targetTokens == null || candidateTokens == null || targetTokens.Count == 0 || candidateTokens.Count == 0)
            return 0;

        int score = 0;
        foreach (var token in targetTokens)
        {
            if (candidateTokens.Contains(token)) score++;
        }

        return score;
    }

    private static HashSet<string> TokenizeAnimationName(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value)) return tokens;

        var normalized = new StringBuilder(value.Length * 2);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsLetterOrDigit(c))
            {
                if (i > 0 && char.IsUpper(c) && char.IsLetter(value[i - 1]) && char.IsLower(value[i - 1]))
                {
                    normalized.Append(' ');
                }

                normalized.Append(char.ToLowerInvariant(c));
            }
            else
            {
                normalized.Append(' ');
            }
        }

        foreach (var part in normalized.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length <= 1) continue;
            if (IsAnimationNameNoiseToken(part)) continue;
            tokens.Add(part);
        }

        return tokens;
    }

    private static bool IsAnimationNameNoiseToken(string token)
    {
        switch (token)
        {
            case "anim":
            case "animation":
            case "copied":
            case "from":
            case "debug":
            case "ft":
            case "ue":
            case "v2":
            case "vf":
            case "true":
            case "false":
            case "rot":
            case "rotation":
                return true;
            default:
                return false;
        }
    }

    private static float GetIntendedFactor(MyCustomBase customBase, CustomBlendshapeEntry driver,
        SkinnedMeshRenderer specificRenderer, SkinnedMeshRenderer[] allRenderers)
    {
        // 1. Priority: MCB override from the component (this handles user intent best)
        if (customBase != null)
        {
            int idx = customBase.customBlendshapeOverrideNames.IndexOf(driver.name);
            if (idx >= 0 && idx < customBase.customBlendshapeOverrideValues.Count)
            {
                return Mathf.Clamp01(customBase.customBlendshapeOverrideValues[idx] / 100f);
            }
        }

        // 2. Fallback: Live SMR weight from the ORIGINAL avatar (if we can find it)
        if (customBase != null)
        {
            var originalRoot = customBase.transform.root;
            if (specificRenderer != null)
            {
                // Try to find the same-named renderer on original root
                var originalRenderer = originalRoot.Find(specificRenderer.name)?.GetComponent<SkinnedMeshRenderer>();
                if (originalRenderer == null)
                {
                    originalRenderer = originalRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .FirstOrDefault(s => s.name == specificRenderer.name);
                }

                if (originalRenderer != null)
                {
                    float val = ReadBlendshapeWeight01(originalRenderer, driver.name);
                    if (!float.IsNaN(val)) return val;
                }
            }
            else
            {
                var originalRenderers = originalRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                float val = ReadBlendshapeWeight01(originalRenderers, driver.name);
                if (!float.IsNaN(val)) return val;
            }
        }

        // 3. Fallback: Providing renderers (usually the clone)
        float smrWeight = float.NaN;
        if (specificRenderer != null)
        {
            smrWeight = ReadBlendshapeWeight01(specificRenderer, driver.name);
        }
        else if (allRenderers != null)
        {
            smrWeight = ReadBlendshapeWeight01(allRenderers, driver.name);
        }

        // Only use clone weight if it's non-zero (heuristic: if it's zero on clone but not on original, clone was reset)
        if (!float.IsNaN(smrWeight) && smrWeight > 0.0001f) return smrWeight;

        // 4. Last resort: Version default value (if SMR weight was zero or not found)
        if (driver != null && float.TryParse(driver.defaultValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed))
        {
            return Mathf.Clamp01(parsed / 100f);
        }

        return 0f;
    }

    private static float ReadBlendshapeWeight01(SkinnedMeshRenderer[] renderers, string blendshapeName)
    {
        if (renderers == null || string.IsNullOrWhiteSpace(blendshapeName)) return float.NaN;

        foreach (var renderer in renderers)
        {
            float value = ReadBlendshapeWeight01(renderer, blendshapeName);
            if (!float.IsNaN(value) && value > 0f) return value;
        }

        // If we didn't find any non-zero, check if we found at least one zero
        foreach (var renderer in renderers)
        {
            float value = ReadBlendshapeWeight01(renderer, blendshapeName);
            if (!float.IsNaN(value)) return 0f;
        }

        return float.NaN;
    }

    private static float ReadBlendshapeWeight01(SkinnedMeshRenderer renderer, string blendshapeName)
    {
        if (renderer == null || renderer.sharedMesh == null || string.IsNullOrWhiteSpace(blendshapeName)) return float.NaN;
        int index = renderer.sharedMesh.GetBlendShapeIndex(blendshapeName);
        if (index < 0) return float.NaN;
        return renderer.GetBlendShapeWeight(index) / 100f;
    }

    private static string BuildConstantFactorParamName(string driverBlendshape, params string[] contextParts)
    {
        var allParts = new List<string> { driverBlendshape };
        if (contextParts != null) allParts.AddRange(contextParts.Where(p => !string.IsNullOrWhiteSpace(p)));

        string baseName = string.Join("_", allParts.Select(SanitizeForParam));
        if (baseName.Length > 72) baseName = baseName.Substring(0, 72);
        return ConstParamPrefix + baseName;
    }

    private static string SanitizeForParam(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "X";
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.ToString();
    }
}
#endif
