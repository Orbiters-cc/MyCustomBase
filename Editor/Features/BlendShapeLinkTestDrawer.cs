#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class BlendShapeLinkTestDrawer
{
    private const double AnimationClipCacheTtlSeconds = 2.0d;
    private const double DebugInfoRefreshIntervalSeconds = 0.75d;
    private static Dictionary<string, AnimationClip> animationClipByNameCache;
    private static double animationClipByNameCacheTimestamp;
    private static bool projectChangedHooked;

    private readonly MCBEditor editor;

    private SkinnedMeshRenderer targetRenderer;
    private CorrectiveActivationType toFixType = CorrectiveActivationType.Blendshape;
    private string toFix = "Blink";
    private CorrectiveActivationType fixedByType = CorrectiveActivationType.Blendshape;
    private string fixedBy = "Blink Fix";
    private string factorParameterName = "custom_face";
    private bool foldout;
    private bool activeVersionLinksFoldout;
    private bool testEnabled;
    private int cachedBlendshapeMeshId;
    private List<string> cachedBlendshapeNames = new List<string>();
    private List<BlendShapeLinkService.VersionLinkDebugInfo> cachedDebugInfos;
    private int cachedDebugAvatarRootId;
    private double nextDebugRefreshAt;

    private const string FoldoutPrefKey = "MCB_BlendShapeLinkTest_Foldout";
    private const string EnabledPrefKey = "MCB_BlendShapeLinkTest_Enabled";
    private const string ToFixTypePrefKey = "MCB_BlendShapeLinkTest_ToFixType";
    private const string ToFixPrefKey = "MCB_BlendShapeLinkTest_ToFix";
    private const string FixedByTypePrefKey = "MCB_BlendShapeLinkTest_FixedByType";
    private const string FixedByPrefKey = "MCB_BlendShapeLinkTest_FixedBy";
    private const string ParamPrefKey = "MCB_BlendShapeLinkTest_Param";
    private const string ActiveVersionLinksFoldoutPrefKey = "MCB_BlendShapeLinkTest_ActiveVersionLinksFoldout";

    public BlendShapeLinkTestDrawer(MCBEditor editor)
    {
        this.editor = editor;
        foldout = EditorPrefs.GetBool(FoldoutPrefKey, false);
        testEnabled = EditorPrefs.GetBool(EnabledPrefKey, true);
        toFixType = (CorrectiveActivationType)EditorPrefs.GetInt(ToFixTypePrefKey, (int)CorrectiveActivationType.Blendshape);
        fixedByType = (CorrectiveActivationType)EditorPrefs.GetInt(FixedByTypePrefKey, (int)CorrectiveActivationType.Blendshape);
        toFix = EditorPrefs.GetString(ToFixPrefKey, toFix);
        fixedBy = EditorPrefs.GetString(FixedByPrefKey, fixedBy);
        factorParameterName = EditorPrefs.GetString(ParamPrefKey, factorParameterName);
        activeVersionLinksFoldout = EditorPrefs.GetBool(ActiveVersionLinksFoldoutPrefKey, false);
    }

    public void Draw()
    {
        foldout = EditorGUILayout.Foldout(foldout, "BlendShape Link Factor Test", true);
        EditorPrefs.SetBool(FoldoutPrefKey, foldout);
        if (!foldout) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.HelpBox(
            "Saves a corrective factor link configuration. " +
            "The patch is applied only to VRCFury temporary controllers during avatar preprocess (build/upload and play-mode build).",
            MessageType.Info
        );

        GameObject avatarRoot = editor?.customBaseTarget != null ? editor.customBaseTarget.transform.root.gameObject : null;
        if (avatarRoot == null)
        {
            EditorGUILayout.HelpBox("Avatar root not found.", MessageType.Warning);
            EditorGUILayout.EndVertical();
            return;
        }

        if (targetRenderer == null)
        {
            targetRenderer = FindDefaultBodyRenderer(avatarRoot);
        }

        EditorGUI.BeginChangeCheck();
        testEnabled = EditorGUILayout.Toggle(
            new GUIContent("Enable Test Link", "Toggles only this manual test link. Does not affect version-based corrective links."),
            testEnabled
        );
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetBool(EnabledPrefKey, testEnabled);
            if (IsReadyToSave())
            {
                BlendShapeLinkService.Instance.UpsertFactorLinkConfig(
                    avatarRoot,
                    targetRenderer,
                    toFixType,
                    toFix,
                    fixedByType,
                    fixedBy,
                    factorParameterName,
                    testEnabled
                );
            }
        }

        using (new EditorGUI.DisabledScope(!testEnabled))
        {
            EditorGUI.BeginChangeCheck();
            targetRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                new GUIContent("Target Mesh", "Required when either side uses Blendshape."),
                targetRenderer,
                typeof(SkinnedMeshRenderer),
                true
            );
            if (EditorGUI.EndChangeCheck())
            {
                toFix = string.Empty;
                fixedBy = string.Empty;
            }

            DrawTypedTargetField(ref toFixType, ref toFix, "to fix", true);
            DrawTypedTargetField(ref fixedByType, ref fixedBy, "fixing", false);

            EditorPrefs.SetInt(ToFixTypePrefKey, (int)toFixType);
            EditorPrefs.SetInt(FixedByTypePrefKey, (int)fixedByType);
            EditorPrefs.SetString(ToFixPrefKey, toFix ?? string.Empty);
            EditorPrefs.SetString(FixedByPrefKey, fixedBy ?? string.Empty);

            EditorGUI.BeginChangeCheck();
            factorParameterName = EditorGUILayout.TextField(
                new GUIContent("Factor Parameter", "Float parameter name used as the multiplier (0..1)."),
                factorParameterName
            );
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(ParamPrefKey, factorParameterName ?? string.Empty);
            }
        }

        bool needsRenderer = toFixType == CorrectiveActivationType.Blendshape || fixedByType == CorrectiveActivationType.Blendshape;
        if (needsRenderer && targetRenderer == null)
        {
            EditorGUILayout.HelpBox("Target Mesh is required when one side uses Blendshape.", MessageType.Warning);
        }

        EditorGUILayout.Space(4f);
        using (new EditorGUI.DisabledScope(!IsReadyToSave()))
        {
            if (GUILayout.Button("Save Factor Link Config"))
            {
                SaveConfig(avatarRoot);
            }
        }

        DrawActiveVersionLinksDebug(avatarRoot);

        EditorGUILayout.EndVertical();
    }

    private void DrawTypedTargetField(ref CorrectiveActivationType type, ref string value, string suffix, bool typeFirst)
    {
        EditorGUILayout.BeginHorizontal();
        if (typeFirst)
        {
            var next = (CorrectiveActivationType)EditorGUILayout.EnumPopup(type, GUILayout.Width(92f));
            if (next != type) value = string.Empty;
            type = next;
            EditorGUILayout.LabelField(suffix, EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField(suffix, EditorStyles.miniLabel, GUILayout.Width(42f));
            var next = (CorrectiveActivationType)EditorGUILayout.EnumPopup(type, GUILayout.Width(92f));
            if (next != type) value = string.Empty;
            type = next;
        }
        EditorGUILayout.EndHorizontal();

        if (type == CorrectiveActivationType.Blendshape)
        {
            var names = GetBlendshapeNamesCached(targetRenderer);
            if (names.Count == 0)
            {
                value = EditorGUILayout.TextField(value);
                return;
            }

            int index = Mathf.Max(0, names.IndexOf(value));
            index = EditorGUILayout.Popup(index, names.ToArray());
            if (index >= 0 && index < names.Count)
            {
                value = names[index];
            }
            return;
        }

        var selectedClip = FindAnimationClipByName(value);
        var nextClip = EditorGUILayout.ObjectField(selectedClip, typeof(AnimationClip), false) as AnimationClip;
        value = nextClip != null ? nextClip.name : string.Empty;
    }

    private static AnimationClip FindAnimationClipByName(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return null;

        EnsureProjectChangedHook();
        RebuildAnimationClipByNameCacheIfNeeded();
        animationClipByNameCache.TryGetValue(clipName, out var clipFromCache);
        return clipFromCache;
    }

    private bool IsReadyToSave()
    {
        bool needsRenderer = toFixType == CorrectiveActivationType.Blendshape || fixedByType == CorrectiveActivationType.Blendshape;
        if (needsRenderer && targetRenderer == null) return false;
        if (string.IsNullOrWhiteSpace(toFix)) return false;
        if (string.IsNullOrWhiteSpace(fixedBy)) return false;
        if (string.IsNullOrWhiteSpace(factorParameterName)) return false;
        return true;
    }

    private void SaveConfig(GameObject avatarRoot)
    {
        var result = BlendShapeLinkService.Instance.UpsertFactorLinkConfig(
            avatarRoot,
            targetRenderer,
            toFixType,
            toFix,
            fixedByType,
            fixedBy,
            factorParameterName,
            testEnabled
        );

        if (!result.success)
        {
            EditorUtility.DisplayDialog("BlendShape Factor Link", result.message, "Ok");
            return;
        }

        EditorUtility.SetDirty(editor?.customBaseTarget);
        MCBLogger.Log("[MCB] BlendShape factor link config saved.");
        EditorUtility.DisplayDialog("BlendShape Factor Link", result.message, "Ok");
    }

    public void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // Intentionally no-op. Execution is handled by BlendShapeLinkPostVrcfuryHook.
    }

    private void DrawActiveVersionLinksDebug(GameObject avatarRoot)
    {
        EditorGUILayout.Space(6f);
        activeVersionLinksFoldout = EditorGUILayout.Foldout(activeVersionLinksFoldout, "Active Version Links (Debug)", true);
        EditorPrefs.SetBool(ActiveVersionLinksFoldoutPrefKey, activeVersionLinksFoldout);
        if (!activeVersionLinksFoldout) return;

        int avatarRootId = avatarRoot != null ? avatarRoot.GetInstanceID() : 0;
        double now = EditorApplication.timeSinceStartup;
        if (cachedDebugInfos == null || cachedDebugAvatarRootId != avatarRootId || now >= nextDebugRefreshAt)
        {
            cachedDebugInfos = BlendShapeLinkService.Instance.GetActiveVersionLinkDebugInfo(avatarRoot);
            cachedDebugAvatarRootId = avatarRootId;
            nextDebugRefreshAt = now + DebugInfoRefreshIntervalSeconds;
        }

        var infos = cachedDebugInfos;
        if (infos == null || infos.Count == 0)
        {
            EditorGUILayout.HelpBox("No active version corrective links.", MessageType.None);
            return;
        }

        foreach (var info in infos)
        {
            string factorInfo = info.usesConstantFactor
                ? $"param={info.factorParameterName}, constant={info.constantFactor.ToString("G9", CultureInfo.InvariantCulture)}"
                : $"param={info.factorParameterName}";

            EditorGUILayout.LabelField(
                $"[{info.targetRendererPath}] {info.toFixType}:{info.toFix} -> {info.fixedByType}:{info.fixedBy} | {factorInfo}");
        }
    }

    private List<string> GetBlendshapeNamesCached(SkinnedMeshRenderer renderer)
    {
        if (renderer == null || renderer.sharedMesh == null)
        {
            cachedBlendshapeMeshId = 0;
            cachedBlendshapeNames.Clear();
            return cachedBlendshapeNames;
        }

        int meshId = renderer.sharedMesh.GetInstanceID();
        if (meshId == cachedBlendshapeMeshId) return cachedBlendshapeNames;

        var mesh = renderer.sharedMesh;
        cachedBlendshapeNames.Clear();
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            cachedBlendshapeNames.Add(mesh.GetBlendShapeName(i));
        }

        cachedBlendshapeMeshId = meshId;
        return cachedBlendshapeNames;
    }

    private static SkinnedMeshRenderer FindDefaultBodyRenderer(GameObject avatarRoot)
    {
        if (avatarRoot == null) return null;
        return avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(r => r != null && string.Equals(r.name, "Body", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureProjectChangedHook()
    {
        if (projectChangedHooked) return;
        projectChangedHooked = true;
        EditorApplication.projectChanged += ClearAnimationClipByNameCache;
    }

    private static void RebuildAnimationClipByNameCacheIfNeeded()
    {
        double now = EditorApplication.timeSinceStartup;
        if (animationClipByNameCache != null && (now - animationClipByNameCacheTimestamp) <= AnimationClipCacheTtlSeconds)
        {
            return;
        }

        var cache = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
        string[] guids = AssetDatabase.FindAssets("t:AnimationClip");
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null || string.IsNullOrWhiteSpace(clip.name)) continue;

            if (!cache.ContainsKey(clip.name))
            {
                cache[clip.name] = clip;
            }
        }

        animationClipByNameCache = cache;
        animationClipByNameCacheTimestamp = now;
    }

    private static void ClearAnimationClipByNameCache()
    {
        animationClipByNameCache = null;
        animationClipByNameCacheTimestamp = 0d;
    }
}
#endif
