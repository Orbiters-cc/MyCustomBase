#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class BlendShapeLinksDebugWindow : EditorWindow
{
    private enum SearchType
    {
        Blendshape,
        BonePath
    }

    private GameObject _avatarRoot;
    private bool _autoRefresh = true;
    private double _lastAutoRefreshTime;
    private string _status = "No scan yet.";
    private Vector2 _mainScroll;
    private SearchType _searchType = SearchType.Blendshape;
    private string _searchText = string.Empty;

    private LiveAvatarControllerService.AvatarControllerSnapshot _snapshot;
    // Accumulated search hits — grows over time as new states are observed, never shrinks until query changes.
    private List<LiveAvatarControllerService.BindingSearchHit> _searchResults =
        new List<LiveAvatarControllerService.BindingSearchHit>();
    private HashSet<string> _searchResultKeys = new HashSet<string>(StringComparer.Ordinal);
    private string _lastSearchQuery = string.Empty;
    private SearchType _lastSearchType = SearchType.Blendshape;
    private bool _boneFound;
    private string _boneFoundPath = string.Empty;
    private bool _forceRetryGestureManagerOnNextRefresh;

    // Recording state for Live Binding Search
    private bool _isRecording;

    // Bone dropdown (BonePath mode)
    private string[] _boneNames = System.Array.Empty<string>();
    private int _boneDropdownIndex = -1;

    [MenuItem("Tools/My Custom Base (MCB)/blendshape links debug")]
    public static void OpenWindow()
    {
        var window = GetWindow<BlendShapeLinksDebugWindow>("Blendshape Links Debug");
        window.minSize = new Vector2(820f, 520f);
        window.Show();
    }

    private void OnEnable()
    {
        if (_avatarRoot == null)
        {
            _avatarRoot = LiveAvatarControllerService.Instance.ResolveActiveAvatarRoot();
        }

        RefreshData();
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        if (!_autoRefresh || !EditorApplication.isPlaying) return;
        if (EditorApplication.timeSinceStartup - _lastAutoRefreshTime < 0.25d) return;
        _lastAutoRefreshTime = EditorApplication.timeSinceStartup;
        RefreshData(false);
        // If recording, keep accumulating search hits on each tick
        if (_isRecording && !string.IsNullOrWhiteSpace(_searchText))
            RunSearch();
        Repaint();
    }

    private void OnGUI()
    {
        DrawToolbar();
        EditorGUILayout.Space(6f);

        _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);
        DrawFoundAnimatorsSection();
        EditorGUILayout.Space(10f);

        // Two-column layout: each column gets exactly half the window width minus the gutter.
        float colWidth = (position.width - 18f) * 0.5f;

        EditorGUILayout.BeginHorizontal();

        // Left column — Edited States (no inner scroll; word-wrap handles overflow)
        EditorGUILayout.BeginVertical(GUILayout.Width(colWidth));
        DrawEditedStatesSection();
        EditorGUILayout.EndVertical();

        GUILayout.Space(6f);

        // Right column — Live Binding Search
        EditorGUILayout.BeginVertical(GUILayout.Width(colWidth));
        DrawSearchSection();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndScrollView();
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginVertical("box");
        bool isGM = _snapshot != null && _snapshot.attachmentSource == LiveAvatarControllerService.AttachmentSource.GestureManager;
        using (new EditorGUI.DisabledScope(isGM))
        {
            EditorGUI.BeginChangeCheck();
            _avatarRoot = (GameObject)EditorGUILayout.ObjectField("Avatar Root", _avatarRoot, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                RefreshData();
            }
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Resolve Active Root", GUILayout.Width(150f)))
        {
            _avatarRoot = LiveAvatarControllerService.Instance.ResolveActiveAvatarRoot();
            RefreshData();
        }

        if (GUILayout.Button("Refresh", GUILayout.Width(90f)))
        {
            RefreshData();
        }

        if (GUILayout.Button("Retry Gesture Manager Attach", GUILayout.Width(210f)))
        {
            _forceRetryGestureManagerOnNextRefresh = true;
            RefreshData();
        }

        if (isGM)
        {
            EditorGUILayout.LabelField("(Locked: Using Gesture Manager's active avatar)", EditorStyles.miniLabel);
        }

        _autoRefresh = EditorGUILayout.ToggleLeft("Auto Refresh (Play Mode)", _autoRefresh, GUILayout.Width(190f));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Status", _status);
        DrawAttachmentSourceStatus();
        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to inspect live animator states/parameters.", MessageType.Info);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawFoundAnimatorsSection()
    {
        EditorGUILayout.LabelField("Found Animators", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");

        var animators = _snapshot != null ? _snapshot.animators : null;
        int count = animators != null ? animators.Count : 0;
        EditorGUILayout.LabelField("Total: " + count);
        if (_snapshot != null)
        {
            string covered = _snapshot.coveredLayers != null && _snapshot.coveredLayers.Count > 0
                ? string.Join(", ", _snapshot.coveredLayers)
                : "<none>";
            string missing = _snapshot.missingLayers != null && _snapshot.missingLayers.Count > 0
                ? string.Join(", ", _snapshot.missingLayers)
                : "<none>";
            EditorGUILayout.LabelField("Covered Layers: " + covered, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Missing Layers: " + missing, EditorStyles.miniLabel);
        }

        if (count == 0)
        {
            EditorGUILayout.HelpBox("No linked animators found for avatar root.", MessageType.Warning);
            EditorGUILayout.EndVertical();
            return;
        }

        for (int i = 0; i < animators.Count; i++)
        {
            var a = animators[i];
            if (a == null) continue;

            EditorGUILayout.BeginVertical("box");

            string flags = string.Empty;
            if (a.isDescriptorAnimator) flags += " descriptor";
            if (a.isOnAvatarRoot) flags += " root";
            if (string.IsNullOrWhiteSpace(flags)) flags = " linked";
            string layerTags = a.matchedLayers != null && a.matchedLayers.Count > 0
                ? string.Join(", ", a.matchedLayers)
                : "none";
            string source = string.IsNullOrWhiteSpace(a.discoverySource) ? "avatar-root" : a.discoverySource;

            EditorGUILayout.LabelField(
                string.Format("#{0} ({1}) layers=[{2}] source={3}",
                    i, flags.Trim(), layerTags, source),
                EditorStyles.miniBoldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                if (a.animator != null)
                {
                    EditorGUILayout.ObjectField("Animator", a.animator, typeof(Animator), true);
                    if (a.animator.gameObject != null)
                        EditorGUILayout.ObjectField("GameObject", a.animator.gameObject, typeof(GameObject), true);
                    var rtc = a.animator.runtimeAnimatorController;
                    if (rtc != null)
                        EditorGUILayout.ObjectField("Controller", rtc, typeof(RuntimeAnimatorController), false);
                    else
                        EditorGUILayout.LabelField("Controller", "<null> (driven by PlayableGraph)", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.LabelField(
                string.Format("Path: {0} | Controller Name: {1}",
                    a.animatorPath,
                    string.IsNullOrWhiteSpace(a.runtimeControllerName) ? "<no controller>" : a.runtimeControllerName),
                EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawEditedStatesSection()
    {
        EditorGUILayout.LabelField("Edited States (Blendshape Link System)", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");

        // Show applied links from registry
        var appliedRecords = _snapshot != null ? _snapshot.appliedLinkRecords : null;
        int appliedCount = appliedRecords != null ? appliedRecords.Count : 0;
        EditorGUILayout.LabelField("Applied Links (from session registry): " + appliedCount, EditorStyles.miniLabel);
        if (appliedCount > 0)
        {
            for (int i = 0; i < appliedRecords.Count; i++)
            {
                var rec = appliedRecords[i];
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField(string.Format("[{0}]", rec.sourceLabel), EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(string.Format("'{0}' ? '{1}'", rec.toFixName, rec.fixedByName), EditorStyles.miniLabel);
                EditorGUILayout.LabelField(string.Format("Param: {0}", rec.factorParameterName), EditorStyles.miniLabel);
                EditorGUILayout.LabelField(string.Format("Controller: {0}", rec.controllerName), EditorStyles.miniLabel);
                if (!string.IsNullOrWhiteSpace(rec.controllerAssetPath))
                    EditorGUILayout.LabelField(rec.controllerAssetPath, EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        var notFound = _snapshot != null ? _snapshot.appliedLinksNotFoundInControllers : null;
        if (notFound != null && notFound.Count > 0)
        {
            for (int i = 0; i < notFound.Count; i++)
            {
                EditorGUILayout.HelpBox(notFound[i], MessageType.Warning);
            }
        }

        EditorGUILayout.Space(4f);

        var states = _snapshot != null ? _snapshot.blendShapeLinkStates : null;
        int count = states != null ? states.Count : 0;
        EditorGUILayout.LabelField("Live Edited States: " + count);

        if (count == 0)
        {
            string hint = appliedCount > 0
                ? "No live BlendShapeLink-edited states detected in current animators. Links were applied (" + appliedCount + " record(s) in registry) but none matched active animator states. This may indicate the controllers are wrapped behind an AnimatorOverrideController or PlayableGraph indirection."
                : "No live BlendShapeLink-edited states found. No applied link records in session registry either.";
            EditorGUILayout.HelpBox(hint, MessageType.Warning);
            EditorGUILayout.EndVertical();
            return;
        }

        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state == null) continue;

            EditorGUILayout.BeginVertical("box");

            var stateWrapStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            var stateWrapBoldStyle = new GUIStyle(EditorStyles.miniBoldLabel) { wordWrap = true };

            // Header: layer / state path
            EditorGUILayout.LabelField(
                string.Format("{0} / {1}", state.layerName, state.fullPathName),
                stateWrapBoldStyle);
            EditorGUILayout.LabelField(
                string.Format("Animator: {0}", state.animatorPath),
                stateWrapStyle);
            // Avatar mask
            if (!string.IsNullOrWhiteSpace(state.avatarMaskName))
                EditorGUILayout.LabelField(string.Format("Avatar Mask: {0}", state.avatarMaskName), stateWrapStyle);
            // Layer weight bar
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(string.Format("Layer Weight: {0:0.000}", state.layerWeight), stateWrapStyle, GUILayout.Width(160f));
            var lwBarRect = EditorGUILayout.GetControlRect(GUILayout.Height(10f));
            EditorGUI.DrawRect(lwBarRect, new Color(0.2f, 0.2f, 0.2f));
            var lwFillRect = new Rect(lwBarRect.x, lwBarRect.y, lwBarRect.width * Mathf.Clamp01(state.layerWeight), lwBarRect.height);
            EditorGUI.DrawRect(lwFillRect, state.layerWeight > 0.001f ? new Color(0.3f, 0.6f, 1f) : new Color(0.5f, 0.5f, 0.5f));
            EditorGUILayout.EndHorizontal();

            // Parameters
            if (state.usedParameters != null && state.usedParameters.Count > 0)
            {
                EditorGUILayout.LabelField("Parameters:", stateWrapStyle);
                for (int p = 0; p < state.usedParameters.Count; p++)
                {
                    var param = state.usedParameters[p];
                    string value = param.type == AnimatorControllerParameterType.Float
                        ? param.floatValue.ToString("0.000")
                        : param.type == AnimatorControllerParameterType.Int
                            ? param.intValue.ToString()
                            : param.boolValue.ToString();
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        string.Format("  {0} ({1}) = {2}", param.name, param.type, value),
                        stateWrapStyle);
                    // Mini activation bar for float params
                    if (param.type == AnimatorControllerParameterType.Float)
                    {
                        var pBarRect = EditorGUILayout.GetControlRect(GUILayout.Width(80f), GUILayout.Height(10f));
                        EditorGUI.DrawRect(pBarRect, new Color(0.2f, 0.2f, 0.2f));
                        var pFillRect = new Rect(pBarRect.x, pBarRect.y, pBarRect.width * Mathf.Clamp01(param.floatValue), pBarRect.height);
                        EditorGUI.DrawRect(pFillRect, new Color(1f, 0.7f, 0.1f));
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            else
            {
                EditorGUILayout.LabelField("Parameters: none", stateWrapStyle);
            }

            // Clips — only show BSLink variant clips to avoid blend tree clutter
            if (state.clips != null && state.clips.Count > 0)
            {
                var variantClips = new List<LiveAvatarControllerService.LiveClipActivation>();
                for (int c = 0; c < state.clips.Count; c++)
                {
                    if (state.clips[c] != null && state.clips[c].isBlendShapeLinkVariant)
                        variantClips.Add(state.clips[c]);
                }

                if (variantClips.Count > 0)
                {
                    EditorGUILayout.LabelField(
                        string.Format("BSLink Clips ({0} of {1} total):", variantClips.Count, state.clips.Count),
                        stateWrapStyle);
                    for (int c = 0; c < variantClips.Count; c++)
                    {
                        var clip = variantClips[c];
                        EditorGUILayout.BeginVertical("box");
                        EditorGUILayout.LabelField(clip.clipName, stateWrapBoldStyle);
                        // Activation bar
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(string.Format("Activation: {0:0.000}", clip.activation), stateWrapStyle, GUILayout.Width(160f));
                        var cBarRect = EditorGUILayout.GetControlRect(GUILayout.Height(10f));
                        EditorGUI.DrawRect(cBarRect, new Color(0.2f, 0.2f, 0.2f));
                        var cFillRect = new Rect(cBarRect.x, cBarRect.y, cBarRect.width * Mathf.Clamp01(clip.activation), cBarRect.height);
                        EditorGUI.DrawRect(cFillRect, clip.activation > 0.001f ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.5f, 0.5f, 0.5f));
                        EditorGUILayout.EndHorizontal();
                        // Blendshape value from clip at current position
                        if (!float.IsNaN(clip.clipSampledBlendshapeValue))
                            EditorGUILayout.LabelField(string.Format("Clip value at current time: {0:0.00}", clip.clipSampledBlendshapeValue), stateWrapStyle);
                        // Live SMR blendshape value
                        if (!string.IsNullOrWhiteSpace(clip.blendshapeBindingPath) && !string.IsNullOrWhiteSpace(clip.blendshapeBindingProperty) && _snapshot?.avatarRoot != null)
                        {
                            float smrVal = LiveAvatarControllerService.ReadSmrBlendshapeValue(_snapshot.avatarRoot, clip.blendshapeBindingPath, clip.blendshapeBindingProperty);
                            if (!float.IsNaN(smrVal))
                                EditorGUILayout.LabelField(string.Format("SMR live value: {0:0.00}", smrVal), stateWrapStyle);
                        }
                        if (!string.IsNullOrWhiteSpace(clip.clipAssetPath))
                            EditorGUILayout.LabelField(clip.clipAssetPath, stateWrapStyle);
                        EditorGUILayout.EndVertical();
                    }
                }
                else
                {
                    EditorGUILayout.LabelField(
                        string.Format("Clips: {0} total (no variant clips active)", state.clips.Count),
                        stateWrapStyle);
                }
            }
            else
            {
                EditorGUILayout.LabelField("Clips: none", stateWrapStyle);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawSearchSection()
    {
        EditorGUILayout.LabelField("Live Binding Search", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");

        EditorGUILayout.BeginHorizontal();
        var prevType = _searchType;
        _searchType = (SearchType)EditorGUILayout.EnumPopup("Type", _searchType, GUILayout.Width(280f));
        if (_searchType != prevType) { ClearSearchHistory(); _boneNames = System.Array.Empty<string>(); _boneDropdownIndex = -1; _isRecording = false; }
        EditorGUILayout.EndHorizontal();
        string searchHint = _searchType == SearchType.Blendshape
            ? "Matches blendShape.* property names (e.g. \"LeftEye\")"
            : "Matches transform path OR humanoid muscle property (e.g. \"LeftEye\")";
        EditorGUILayout.LabelField(searchHint, EditorStyles.miniLabel);
        EditorGUILayout.BeginHorizontal();
        var prevText = _searchText;
        _searchText = EditorGUILayout.TextField("Query", _searchText);
        if (_searchText != prevText) { ClearSearchHistory(); _boneDropdownIndex = -1; _isRecording = false; }
        if (GUILayout.Button("Search", GUILayout.Width(70f)))
        {
            ClearSearchHistory();
            RunSearch();
        }
        // Record / Stop button
        var recordLabel = _isRecording ? "? Stop" : "? Record";
        var recordColor = _isRecording ? new Color(0.9f, 0.2f, 0.2f) : new Color(0.2f, 0.75f, 0.2f);
        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = recordColor;
        if (GUILayout.Button(recordLabel, GUILayout.Width(80f)))
        {
            if (!_isRecording)
            {
                // Start recording: clear history and do an initial search
                ClearSearchHistory();
                RunSearch();
                _isRecording = true;
            }
            else
            {
                _isRecording = false;
            }
        }
        GUI.backgroundColor = prevBg;
        if (GUILayout.Button("Clear", GUILayout.Width(50f)))
        {
            ClearSearchHistory();
            _isRecording = false;
        }
        EditorGUILayout.EndHorizontal();
        if (_isRecording)
        {
            var recStyle = new GUIStyle(EditorStyles.miniLabel);
            recStyle.normal.textColor = new Color(0.9f, 0.2f, 0.2f);
            EditorGUILayout.LabelField("? Recording — accumulating hits across animation states...", recStyle);
        }

        // Bone dropdown — only in BonePath mode
        if (_searchType == SearchType.BonePath)
        {
            if (_boneNames.Length == 0 && _avatarRoot != null)
                RebuildBoneList();

            if (_boneNames.Length > 0)
            {
                EditorGUI.BeginChangeCheck();
                _boneDropdownIndex = EditorGUILayout.Popup("Bones", _boneDropdownIndex, _boneNames);
                if (EditorGUI.EndChangeCheck() && _boneDropdownIndex >= 0 && _boneDropdownIndex < _boneNames.Length)
                {
                    _searchText = _boneNames[_boneDropdownIndex];
                    ClearSearchHistory();
                    RunSearch();
                }
            }
        }

        // Bone found / not found indicator — only in BonePath mode
        if (_searchType == SearchType.BonePath && !string.IsNullOrWhiteSpace(_lastSearchQuery))
        {
            // Check if the bone transform actually exists in the hierarchy (independent of animation)
            bool boneExistsInHierarchy = false;
            string resolvedBonePath = string.Empty;
            Transform resolvedBoneTransform = null;
            if (_avatarRoot != null)
            {
                var animator = _avatarRoot.GetComponentInChildren<Animator>(true);
                var pathRoot = animator != null ? animator.transform : _avatarRoot.transform;
                var candidate = pathRoot.Find(_lastSearchQuery);
                if (candidate != null)
                {
                    boneExistsInHierarchy = true;
                    resolvedBonePath = _lastSearchQuery;
                    resolvedBoneTransform = candidate;
                }
                else
                {
                    // Partial match: search all children for a transform whose relative path contains the query
                    string normalizedQuery = NormalizeText(_lastSearchQuery);
                    var allTransforms = pathRoot.GetComponentsInChildren<Transform>(true);
                    foreach (var t in allTransforms)
                    {
                        if (t == pathRoot) continue;
                        string relPath = BuildRelativePath(pathRoot, t);
                        if (NormalizeText(relPath).Contains(normalizedQuery))
                        {
                            boneExistsInHierarchy = true;
                            resolvedBonePath = relPath;
                            resolvedBoneTransform = t;
                            break;
                        }
                    }
                }
            }

            var indicatorStyle = new GUIStyle(EditorStyles.boldLabel);
            if (_boneFound)
            {
                indicatorStyle.normal.textColor = new Color(0.2f, 0.8f, 0.2f);
                string displayPath = string.IsNullOrWhiteSpace(_boneFoundPath) ? "(empty path — humanoid muscle)" : _boneFoundPath;
                EditorGUILayout.LabelField("Bone found & animated: " + displayPath, indicatorStyle);
                if (resolvedBoneTransform != null)
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("Bone", resolvedBoneTransform.gameObject, typeof(GameObject), true);
                }
            }
            else if (boneExistsInHierarchy)
            {
                indicatorStyle.normal.textColor = new Color(1f, 0.7f, 0.1f);
                EditorGUILayout.LabelField("Bone exists in hierarchy (not animated by any discovered clip): " + resolvedBonePath, indicatorStyle);
                if (resolvedBoneTransform != null)
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("Bone", resolvedBoneTransform.gameObject, typeof(GameObject), true);
                }
            }
            else
            {
                indicatorStyle.normal.textColor = new Color(0.9f, 0.2f, 0.2f);
                EditorGUILayout.LabelField("Bone not found in hierarchy", indicatorStyle);
            }
        }

        EditorGUILayout.LabelField("Results (accumulated): " + _searchResults.Count);

        var wrapStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
        var wrapBoldStyle = new GUIStyle(EditorStyles.miniBoldLabel) { wordWrap = true };
        for (int i = 0; i < _searchResults.Count; i++)
        {
            var hit = _searchResults[i];
            if (hit == null) continue;

            // Get live activation + clip sampled value + mask from current snapshot if available
            float liveActivation = hit.clipActivation;
            float clipSampledValue = float.NaN;
            string hitMaskName = string.Empty;
            if (_snapshot != null && _snapshot.blendShapeLinkStates != null)
            {
                foreach (var state in _snapshot.blendShapeLinkStates)
                {
                    if (state == null || state.layerIndex != hit.layerIndex) continue;
                    if (!string.IsNullOrWhiteSpace(state.avatarMaskName))
                        hitMaskName = state.avatarMaskName;
                    if (state.clips == null) continue;
                    foreach (var c in state.clips)
                    {
                        if (c != null && string.Equals(c.clipName, hit.clipName, StringComparison.Ordinal))
                        {
                            liveActivation = c.activation;
                            clipSampledValue = c.clipSampledBlendshapeValue;
                            break;
                        }
                    }
                }
            }

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(
                string.Format("{0} / {1} / {2}", hit.animatorPath, hit.layerName, hit.statePath),
                wrapBoldStyle);
            EditorGUILayout.LabelField(
                string.Format("Clip: {0}", hit.clipName),
                wrapStyle);
            // Avatar mask
            string displayMask = !string.IsNullOrWhiteSpace(hitMaskName) ? hitMaskName
                : !string.IsNullOrWhiteSpace(hit.avatarMaskName) ? hit.avatarMaskName : string.Empty;
            if (!string.IsNullOrWhiteSpace(displayMask))
                EditorGUILayout.LabelField(string.Format("Avatar Mask: {0}", displayMask), wrapStyle);
            // Live activation bar
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(string.Format("Live Activation: {0:0.000}", liveActivation), wrapStyle, GUILayout.Width(160f));
            var barRect = EditorGUILayout.GetControlRect(GUILayout.Height(10f));
            EditorGUI.DrawRect(barRect, new Color(0.2f, 0.2f, 0.2f));
            var fillRect = new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(liveActivation), barRect.height);
            EditorGUI.DrawRect(fillRect, liveActivation > 0.001f ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.5f, 0.5f, 0.5f));
            EditorGUILayout.EndHorizontal();
            // Blendshape value from clip at current position
            if (!float.IsNaN(clipSampledValue))
                EditorGUILayout.LabelField(string.Format("Clip value at current time: {0:0.00}", clipSampledValue), wrapStyle);
            // Live SMR blendshape value
            if (!string.IsNullOrWhiteSpace(hit.bindingPath) && !string.IsNullOrWhiteSpace(hit.bindingProperty)
                && hit.bindingProperty.StartsWith("blendShape.", StringComparison.Ordinal) && _snapshot?.avatarRoot != null)
            {
                float smrVal = LiveAvatarControllerService.ReadSmrBlendshapeValue(_snapshot.avatarRoot, hit.bindingPath, hit.bindingProperty);
                if (!float.IsNaN(smrVal))
                    EditorGUILayout.LabelField(string.Format("SMR live value: {0:0.00}", smrVal), wrapStyle);
            }
            if (!string.IsNullOrWhiteSpace(hit.clipPath))
                EditorGUILayout.LabelField(hit.clipPath, wrapStyle);
            EditorGUILayout.LabelField(
                string.Format("path='{0}'", hit.bindingPath),
                wrapStyle);
            EditorGUILayout.LabelField(
                string.Format("property='{0}'", hit.bindingProperty),
                wrapStyle);
            EditorGUILayout.LabelField(
                string.Format("type='{0}'", hit.bindingTypeName),
                wrapStyle);
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndVertical();
    }

    private void RefreshData(bool rerunSearch = true)
    {
        if (_avatarRoot == null)
        {
            _snapshot = new LiveAvatarControllerService.AvatarControllerSnapshot();
            ClearSearchHistory();
            _status = "Avatar root is null.";
            return;
        }

        bool forceRetry = _forceRetryGestureManagerOnNextRefresh;
        _forceRetryGestureManagerOnNextRefresh = false;
        _snapshot = LiveAvatarControllerService.Instance.CaptureSnapshot(_avatarRoot, forceRetry);
        
        // If we are attached to GM, ensure our root matches what GM is controlling
        if (_snapshot != null && _snapshot.attachmentSource == LiveAvatarControllerService.AttachmentSource.GestureManager && _snapshot.avatarRoot != null)
        {
            _avatarRoot = _snapshot.avatarRoot;
        }

        int animatorCount = _snapshot != null && _snapshot.animators != null ? _snapshot.animators.Count : 0;
        int stateCount = _snapshot != null && _snapshot.blendShapeLinkStates != null ? _snapshot.blendShapeLinkStates.Count : 0;
        _status = string.Format("Scanned {0} animators, {1} live BlendShapeLink states.", animatorCount, stateCount);

        if (rerunSearch && !string.IsNullOrWhiteSpace(_searchText))
        {
            RunSearch();
        }
    }

    private void ClearSearchHistory()
    {
        _searchResults.Clear();
        _searchResultKeys.Clear();
        _boneFound = false;
        _boneFoundPath = string.Empty;
    }

    private void RebuildBoneList()
    {
        if (_avatarRoot == null) { _boneNames = System.Array.Empty<string>(); return; }

        // Animation clips store paths relative to the Animator's GameObject, not the avatar root.
        // Use the first Animator found under the avatar root as the path root so dropdown entries
        // match what AnimationUtility.GetCurveBindings returns.
        var animator = _avatarRoot.GetComponentInChildren<Animator>(true);
        var root = animator != null ? animator.transform : _avatarRoot.transform;

        var transforms = root.GetComponentsInChildren<Transform>(true);
        var names = new List<string>(transforms.Length);
        foreach (var t in transforms)
        {
            if (t == root) continue;
            // Build relative path from animator root
            var stack = new System.Collections.Generic.Stack<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                stack.Push(cur.name);
                cur = cur.parent;
            }
            names.Add(string.Join("/", stack.ToArray()));
        }
        names.Sort(StringComparer.Ordinal);
        _boneNames = names.ToArray();
    }

    private void DrawAttachmentSourceStatus()
    {
        var source = _snapshot != null
            ? _snapshot.attachmentSource
            : LiveAvatarControllerService.AttachmentSource.None;
        string label = _snapshot != null && !string.IsNullOrWhiteSpace(_snapshot.attachmentStatus)
            ? _snapshot.attachmentStatus
            : "No attachment status.";

        Color color;
        switch (source)
        {
            case LiveAvatarControllerService.AttachmentSource.GestureManager:
                color = Color.green;
                break;
            case LiveAvatarControllerService.AttachmentSource.VrcDescriptor:
                color = new Color(1f, 0.6f, 0f);
                break;
            case LiveAvatarControllerService.AttachmentSource.AnimatorFallback:
                color = Color.red;
                break;
            default:
                color = Color.gray;
                break;
        }

        var style = new GUIStyle(EditorStyles.label);
        style.normal.textColor = color;
        EditorGUILayout.LabelField("Attach Source: " + label, style);
    }

    private void RunSearch()
    {
        if (_avatarRoot == null || string.IsNullOrWhiteSpace(_searchText)) return;

        string trimmed = _searchText.Trim();
        // If query or type changed, history was already cleared by the field change handlers.
        _lastSearchQuery = trimmed;
        _lastSearchType = _searchType;

        // Build list of queries to run. For BonePath mode with a path containing '/',
        // also search with just the last segment (bone name) to catch humanoid muscle bindings
        // which use empty paths and property names like "LeftEye Down-Up".
        var queries = new List<string> { trimmed };
        if (_searchType == SearchType.BonePath && trimmed.Contains("/"))
        {
            string lastSegment = trimmed.Substring(trimmed.LastIndexOf('/') + 1);
            if (!string.IsNullOrWhiteSpace(lastSegment) && !string.Equals(lastSegment, trimmed, StringComparison.Ordinal))
                queries.Add(lastSegment);
        }

        string normalizedQuery = NormalizeText(trimmed);
        // For the bone-name-only secondary query, use a shorter normalized form for MatchesSearchType
        string normalizedBoneName = queries.Count > 1 ? NormalizeText(queries[1]) : normalizedQuery;

        bool anyNewHit = false;

        for (int q = 0; q < queries.Count; q++)
        {
            var raw = LiveAvatarControllerService.Instance.SearchAllAnimationBindings(_avatarRoot, queries[q]);
            if (raw == null) continue;

            // Use the bone-name normalized query for secondary searches so MatchesSearchType works
            string effectiveNormalized = q == 0 ? normalizedQuery : normalizedBoneName;

            for (int i = 0; i < raw.Count; i++)
            {
                var hit = raw[i];
                if (hit == null) continue;
                if (!MatchesSearchType(hit, _searchType, effectiveNormalized)) continue;

                // Build a dedup key for the accumulated set
                string key = (hit.animatorPath ?? string.Empty) + "|" +
                             hit.layerIndex + "|" +
                             (hit.statePath ?? string.Empty) + "|" +
                             (hit.clipPath ?? string.Empty) + "|" +
                             (hit.bindingPath ?? string.Empty) + "|" +
                             (hit.bindingProperty ?? string.Empty);
                if (!_searchResultKeys.Add(key)) continue;

                _searchResults.Add(hit);
                anyNewHit = true;

                // Update bone-found indicator
                if (!_boneFound)
                {
                    _boneFound = true;
                    _boneFoundPath = hit.bindingPath ?? string.Empty;
                }
            }
        }

        if (anyNewHit)
        {
            _searchResults = _searchResults
                .OrderBy(x => x.animatorPath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(x => x.layerIndex)
                .ThenBy(x => x.statePath ?? string.Empty, StringComparer.Ordinal)
                .ThenByDescending(x => x.clipActivation)
                .ToList();
        }
    }

    private static bool MatchesSearchType(LiveAvatarControllerService.BindingSearchHit hit, SearchType type,
        string normalizedQuery)
    {
        string property = hit.bindingProperty ?? string.Empty;
        string path = hit.bindingPath ?? string.Empty;

        switch (type)
        {
            case SearchType.Blendshape:
                if (!property.StartsWith("blendShape.", StringComparison.Ordinal)) return false;
                return NormalizeText(property).Contains(normalizedQuery);

            case SearchType.BonePath:
                // Match transform path OR humanoid muscle property names (empty path, e.g. "LeftEye Down-Up")
                return NormalizeText(path).Contains(normalizedQuery) || NormalizeText(property).Contains(normalizedQuery);

            default:
                return false;
        }
    }

    private static string BuildRelativePath(Transform root, Transform target)
    {
        if (root == null || target == null) return string.Empty;
        var stack = new System.Collections.Generic.Stack<string>();
        var cur = target;
        while (cur != null && cur != root)
        {
            stack.Push(cur.name);
            cur = cur.parent;
        }
        return string.Join("/", stack.ToArray());
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }
}
#endif
