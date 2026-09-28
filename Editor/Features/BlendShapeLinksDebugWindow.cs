#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// Live view of the BlendShape Link system while the avatar plays: which animators were found, which states the links
// rewrote and how strongly they play, and a search across every animation binding. Dense on purpose: it is a technical
// tool, so every row is one or two lines and details live in tooltips.
public class BlendShapeLinksDebugWindow : EditorWindow
{
    private enum SearchType { Blendshape, BonePath }

    private const string MenuPath = "Tools/My Custom Base (MCB)/Blendshape Links";
    private static readonly Color Good = new Color32(0x00, 0xDA, 0x6D, 0xff), Warn = new Color32(0xff, 0xb0, 0x3a, 0xff),
        Bad = new Color32(0xff, 0x5c, 0x5c, 0xff), Idle = new Color32(0x6a, 0x6a, 0x6a, 0xff), Param = new Color32(0xff, 0xb3, 0x1a, 0xff);

    [SerializeField] private GameObject _avatarRoot;
    [SerializeField] private bool _autoRefresh = true;
    [SerializeField] private SearchType _searchType = SearchType.Blendshape;
    [SerializeField] private string _searchText = string.Empty;

    private double _lastAutoRefreshTime;
    private bool _forceRetryGestureManagerOnNextRefresh;
    private bool _isRecording;
    private LiveAvatarControllerService.AvatarControllerSnapshot _snapshot;
    // Accumulated search hits: grows as new states are observed, never shrinks until the query changes.
    private List<LiveAvatarControllerService.BindingSearchHit> _searchResults = new List<LiveAvatarControllerService.BindingSearchHit>();
    private readonly HashSet<string> _searchResultKeys = new HashSet<string>(StringComparer.Ordinal);
    private string _lastSearchQuery = string.Empty;
    private bool _boneFound;
    private string _boneFoundPath = string.Empty;

    private ObjectField _avatarField;
    private Label _sourceChip, _status, _animatorsCount, _statesCount, _resultsCount, _hint, _boneStatus;
    private VisualElement _playNotice, _animators, _states, _results, _columns;
    private Button _record, _auto, _retry, _bones;
    private TextField _query;
    private SegmentedControl _type;
    // Each live section is rebuilt only when what it shows changes, so hovering and tooltips survive the 4 Hz refresh.
    // Unity keeps private fields across script reloads; these must start empty so a reloaded window draws again.
    [NonSerialized] private string _animatorsKey, _statesKey, _resultsKey;

    [MenuItem(MenuPath)]
    public static void OpenWindow()
    {
        var window = GetWindow<BlendShapeLinksDebugWindow>();
        window.titleContent = new GUIContent("Blendshape Links");
        window.minSize = new Vector2(420f, 320f);
        window.Show();
    }

    private void OnEnable()
    {
        if (_avatarRoot == null) _avatarRoot = LiveAvatarControllerService.Instance.ResolveActiveAvatarRoot();
        RefreshData();
        EditorApplication.update += OnEditorUpdate;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
    }

    private void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.EnteredPlayMode && change != PlayModeStateChange.EnteredEditMode) return;
        RefreshData();
        Render();
    }

    private void OnEditorUpdate()
    {
        if (!_autoRefresh || !EditorApplication.isPlaying) return;
        if (EditorApplication.timeSinceStartup - _lastAutoRefreshTime < 0.25d) return;
        _lastAutoRefreshTime = EditorApplication.timeSinceStartup;
        RefreshData(false);
        if (_isRecording && !string.IsNullOrWhiteSpace(_searchText)) RunSearch();
        Render();
    }

    // ---------------------------------------------------------------- Layout

    public void CreateGUI()
    {
        var root = rootVisualElement;
        foreach (var path in new[] { "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss", "Packages/orbiters.mcb/Editor/Styles/mcb-blendshape-links.uss" })
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (sheet) root.styleSheets.Add(sheet);
        }
        root.AddToClassList("bsl");

        var bar = Row("bsl-bar"); root.Add(bar);
        _avatarField = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true, value = _avatarRoot, tooltip = "Avatar root to inspect." };
        _avatarField.AddToClassList("bsl-avatar");
        _avatarField.RegisterValueChangedCallback(evt => { _avatarRoot = evt.newValue as GameObject; RefreshData(); Render(); });
        bar.Add(_avatarField);
        _sourceChip = Text("", "bsl-chip"); bar.Add(_sourceChip);
        bar.Add(SmallButton("Active", "Use the avatar that is active in the scene (or in Gesture Manager).", () =>
        {
            _avatarRoot = LiveAvatarControllerService.Instance.ResolveActiveAvatarRoot();
            RefreshData(); Render();
        }));
        bar.Add(SmallButton("Refresh", "Scan the animators again.", () => { RefreshData(); Render(); }));
        _retry = SmallButton("Retry GM", "Try attaching to Gesture Manager again.", () => { _forceRetryGestureManagerOnNextRefresh = true; RefreshData(); Render(); });
        bar.Add(_retry);
        _auto = SmallButton("Live", "Refresh 4 times a second in Play Mode.", () => { _autoRefresh = !_autoRefresh; Render(); });
        bar.Add(_auto);

        var statusRow = Row("bsl-statusbar"); root.Add(statusRow);
        _status = Text("", "bsl-status"); statusRow.Add(_status);
        _playNotice = Text("Enter Play Mode to see live states and parameters.", "bsl-notice"); statusRow.Add(_playNotice);

        var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
        scroll.AddToClassList("bsl-scroll"); scroll.contentContainer.AddToClassList("bsl-content"); root.Add(scroll);

        var animatorsCard = Section(scroll, "Animators", out _animatorsCount);
        _animators = animatorsCard;

        _columns = Row("bsl-columns"); scroll.Add(_columns);
        _columns.RegisterCallback<GeometryChangedEvent>(evt => _columns.EnableInClassList("bsl-columns--stacked", evt.newRect.width < 720f));
        var left = new VisualElement(); left.AddToClassList("bsl-column"); _columns.Add(left);
        var right = new VisualElement(); right.AddToClassList("bsl-column"); right.AddToClassList("bsl-column--last"); _columns.Add(right);
        _states = Section(left, "Edited states", out _statesCount);
        var search = Section(right, "Binding search", out _resultsCount);
        BuildSearch(search);
        Render();
    }

    private void BuildSearch(VisualElement card)
    {
        _type = new SegmentedControl(new[] { "Blendshape", "Bone path" }, index =>
        {
            _searchType = (SearchType)index;
            ClearSearchHistory(); _isRecording = false;
            Render();
        });
        _type.AddToClassList("orb-segmented--compact");
        _type.SetIndex((int)_searchType);
        card.Add(_type);

        var row = Row("bsl-query"); card.Add(row);
        _query = new TextField { value = _searchText };
        _query.AddToClassList("bsl-query__field");
        _query.RegisterValueChangedCallback(evt => { _searchText = evt.newValue; ClearSearchHistory(); _isRecording = false; Render(); });
        _query.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            ClearSearchHistory(); RunSearch(); Render();
        });
        row.Add(_query);
        _bones = SmallButton("Bones ▾", "Pick a bone of the avatar.", ShowBoneMenu); row.Add(_bones);
        row.Add(SmallButton("Search", "Search every animation binding now.", () => { ClearSearchHistory(); RunSearch(); Render(); }));
        _record = SmallButton("Record", "Keep searching while the avatar plays and collect every hit.", () =>
        {
            if (_isRecording) _isRecording = false;
            else { ClearSearchHistory(); RunSearch(); _isRecording = true; }
            Render();
        });
        _record.AddToClassList("bsl-record");
        row.Add(_record);
        row.Add(SmallButton("Clear", "Forget the collected hits.", () => { ClearSearchHistory(); _isRecording = false; Render(); }));

        _hint = Text("", "bsl-meta"); card.Add(_hint);
        _boneStatus = Text("", "bsl-bone"); card.Add(_boneStatus);
        _results = new VisualElement(); card.Add(_results);
    }

    // ---------------------------------------------------------------- Render

    private void Render()
    {
        if (_avatarField == null) return;
        bool playing = EditorApplication.isPlaying;
        var source = _snapshot?.attachmentSource ?? LiveAvatarControllerService.AttachmentSource.None;
        bool gm = source == LiveAvatarControllerService.AttachmentSource.GestureManager;

        _avatarField.SetValueWithoutNotify(_avatarRoot);
        _avatarField.SetEnabled(!gm);
        _avatarField.tooltip = gm ? "Locked to the avatar Gesture Manager is driving." : "Avatar root to inspect.";
        _sourceChip.text = gm ? "Gesture Manager" : source == LiveAvatarControllerService.AttachmentSource.VrcDescriptor ? "Descriptor"
            : source == LiveAvatarControllerService.AttachmentSource.AnimatorFallback ? "Animator" : "No source";
        _sourceChip.tooltip = _snapshot?.attachmentStatus ?? "No attachment status.";
        Tint(_sourceChip, gm ? Good : source == LiveAvatarControllerService.AttachmentSource.VrcDescriptor ? Warn
            : source == LiveAvatarControllerService.AttachmentSource.AnimatorFallback ? Bad : Idle);
        _retry.style.display = gm ? DisplayStyle.None : DisplayStyle.Flex;
        _auto.EnableInClassList("bsl-toggle--on", _autoRefresh);
        _playNotice.style.display = playing ? DisplayStyle.None : DisplayStyle.Flex;

        int animatorCount = _snapshot?.animators?.Count ?? 0, stateCount = _snapshot?.blendShapeLinkStates?.Count ?? 0;
        _status.text = _avatarRoot == null ? "No avatar selected." : $"{animatorCount} animator{S(animatorCount)} · {stateCount} live link state{S(stateCount)}";

        RenderAnimators();
        RenderStates();
        RenderSearch();
    }

    private void RenderAnimators()
    {
        var animators = _snapshot?.animators ?? new List<LiveAvatarControllerService.AnimatorLinkInfo>();
        _animatorsCount.text = animators.Count.ToString();
        var key = new StringBuilder();
        key.Append(string.Join(",", _snapshot?.coveredLayers ?? new List<string>())).Append('|').Append(string.Join(",", _snapshot?.missingLayers ?? new List<string>()));
        foreach (var a in animators.Where(a => a != null))
            key.Append('|').Append(a.animator ? a.animator.GetInstanceID() : 0).Append(a.animatorPath).Append(string.Join(",", a.matchedLayers)).Append(a.runtimeControllerName);
        if (!Changed(ref _animatorsKey, key)) return;

        _animators.Clear();
        if (_snapshot != null && (_snapshot.coveredLayers.Count > 0 || _snapshot.missingLayers.Count > 0))
        {
            var layers = Row("bsl-chips"); _animators.Add(layers);
            layers.Add(Text("Layers", "bsl-meta"));
            foreach (var layer in _snapshot.coveredLayers) layers.Add(Chip(layer, Good, "Covered by a found animator."));
            foreach (var layer in _snapshot.missingLayers) layers.Add(Chip(layer, Idle, "No found animator plays this layer."));
        }
        if (animators.Count == 0) { _animators.Add(Message("No animators found under this avatar.", Warn)); return; }
        for (int i = 0; i < animators.Count; i++)
        {
            var a = animators[i];
            if (a == null) continue;
            var row = Row("bsl-row"); _animators.Add(row);
            row.Add(Text("#" + i, "bsl-index"));
            var name = Link(a.animator ? a.animator.gameObject.name : a.animatorPath, a.animator, "Path: " + a.animatorPath + "\nFound by: " + (string.IsNullOrWhiteSpace(a.discoverySource) ? "avatar-root" : a.discoverySource));
            name.AddToClassList("bsl-strong"); row.Add(name);
            if (a.isDescriptorAnimator) row.Add(Chip("descriptor", Idle));
            if (a.isOnAvatarRoot) row.Add(Chip("root", Idle));
            if (!a.isDescriptorAnimator && !a.isOnAvatarRoot) row.Add(Chip("linked", Idle));
            row.Add(Text(a.matchedLayers.Count > 0 ? string.Join(", ", a.matchedLayers) : "no layers", "bsl-meta bsl-grow"));
            var controller = a.animator ? a.animator.runtimeAnimatorController : null;
            row.Add(controller != null ? Link(controller.name, controller, a.runtimeControllerAssetPath)
                : Text(string.IsNullOrWhiteSpace(a.runtimeControllerName) ? "PlayableGraph" : a.runtimeControllerName, "bsl-meta", "No controller asset: driven by a PlayableGraph."));
        }
    }

    private void RenderStates()
    {
        var records = _snapshot?.appliedLinkRecords ?? new List<BlendShapeLinkService.AppliedLinkRecord>();
        var notFound = _snapshot?.appliedLinksNotFoundInControllers ?? new List<string>();
        var states = (_snapshot?.blendShapeLinkStates ?? new List<LiveAvatarControllerService.LiveStateInfo>()).Where(s => s != null).ToList();
        _statesCount.text = $"{states.Count} live · {records.Count} applied";

        // Everything this section shows, values rounded as displayed.
        var key = new StringBuilder();
        foreach (var r in records) key.Append(r.sourceLabel).Append(r.toFixName).Append(r.fixedByName).Append(r.factorParameterName).Append(r.controllerName).Append('|');
        foreach (var n in notFound) key.Append(n).Append('|');
        foreach (var s in states)
        {
            key.Append(s.layerIndex).Append(s.fullPathName).Append(F(s.layerWeight)).Append(s.avatarMaskName);
            foreach (var p in s.usedParameters) key.Append(p.name).Append(Value(p));
            foreach (var c in s.clips.Where(c => c != null && c.isBlendShapeLinkVariant)) key.Append(c.clipName).Append(F(c.activation)).Append(F(c.clipSampledBlendshapeValue)).Append(F(Smr(c)));
            key.Append('|');
        }
        if (!Changed(ref _statesKey, key)) return;

        _states.Clear();
        if (records.Count > 0)
        {
            _states.Add(Text("Applied links", "bsl-subtitle"));
            foreach (var r in records)
            {
                var row = Row("bsl-row"); _states.Add(row);
                row.Add(Chip(r.sourceLabel, Idle));
                row.Add(Text(r.toFixName + "  →  " + r.fixedByName, "bsl-strong bsl-grow", r.controllerAssetPath));
                row.Add(Text(r.factorParameterName, "bsl-param", "Factor parameter"));
                row.Add(Text(r.controllerName, "bsl-meta", r.controllerAssetPath));
            }
        }
        foreach (var n in notFound) _states.Add(Message(n, Warn));

        _states.Add(Text("Live states", "bsl-subtitle"));
        if (states.Count == 0)
        {
            _states.Add(Message(records.Count > 0
                ? $"None of the {records.Count} applied link{S(records.Count)} matched a playing state. The controllers may be wrapped by an AnimatorOverrideController or a PlayableGraph."
                : EditorApplication.isPlaying ? "No state rewritten by a link is playing, and no link was applied this session." : "Nothing is playing yet.", EditorApplication.isPlaying ? Warn : Idle));
            return;
        }
        foreach (var s in states)
        {
            var card = new VisualElement(); card.AddToClassList("bsl-item"); _states.Add(card);
            var head = Row("bsl-row"); card.Add(head);
            head.Add(Text(s.layerName + " / " + s.fullPathName, "bsl-strong bsl-grow", "Animator: " + s.animatorPath + (string.IsNullOrWhiteSpace(s.avatarMaskName) ? "" : "\nMask: " + s.avatarMaskName)));
            head.Add(Bar(s.layerWeight, s.layerWeight > 0.001f ? new Color(0.3f, 0.6f, 1f) : Idle, "Layer weight"));
            head.Add(Text(F(s.layerWeight), "bsl-value"));
            foreach (var p in s.usedParameters)
            {
                var row = Row("bsl-row bsl-row--nested"); card.Add(row);
                row.Add(Text(p.name, "bsl-param bsl-grow"));
                row.Add(Text(p.type.ToString(), "bsl-meta"));
                if (p.type == AnimatorControllerParameterType.Float) row.Add(Bar(p.floatValue, Param));
                row.Add(Text(Value(p), "bsl-value"));
            }
            var variants = s.clips.Where(c => c != null && c.isBlendShapeLinkVariant).ToList();
            if (variants.Count == 0) { card.Add(Text($"{s.clips.Count} clip{S(s.clips.Count)}, none of them a link variant", "bsl-meta bsl-row--nested")); continue; }
            foreach (var c in variants)
            {
                var row = Row("bsl-row bsl-row--nested"); card.Add(row);
                var clipLink = Link(c.clipName, c.clip, c.clipAssetPath); clipLink.AddToClassList("bsl-grow"); row.Add(clipLink);
                float smr = Smr(c);
                row.Add(Text((float.IsNaN(c.clipSampledBlendshapeValue) ? "" : "clip " + c.clipSampledBlendshapeValue.ToString("0.0", CultureInfo.InvariantCulture)) +
                             (float.IsNaN(smr) ? "" : "  mesh " + smr.ToString("0.0", CultureInfo.InvariantCulture)), "bsl-meta", "Clip value at the current time, and the value on the mesh right now."));
                row.Add(Bar(c.activation, c.activation > 0.001f ? Good : Idle, "Activation"));
                row.Add(Text(F(c.activation), "bsl-value"));
            }
        }
    }

    private void RenderSearch()
    {
        bool bones = _searchType == SearchType.BonePath;
        if (_type.Index != (int)_searchType) _type.SetIndex((int)_searchType);
        _query.SetValueWithoutNotify(_searchText);
        _bones.style.display = bones ? DisplayStyle.Flex : DisplayStyle.None;
        _hint.text = bones ? "Matches a transform path or a humanoid muscle, e.g. “LeftEye”." : "Matches blendShape.* properties, e.g. “LeftEye”.";
        _record.text = _isRecording ? "● Stop" : "● Record";
        _record.EnableInClassList("bsl-record--on", _isRecording);
        _resultsCount.text = _isRecording ? $"{_searchResults.Count} · recording" : _searchResults.Count.ToString();
        RenderBoneStatus(bones);

        var key = new StringBuilder();
        foreach (var hit in _searchResults) { var live = Live(hit); key.Append(hit.clipPath).Append(hit.bindingPath).Append(hit.bindingProperty).Append(F(live.activation)).Append(F(live.sampled)).Append(F(SmrFor(hit))).Append('|'); }
        if (!Changed(ref _resultsKey, key)) return;

        _results.Clear();
        if (_searchResults.Count == 0 && !string.IsNullOrWhiteSpace(_lastSearchQuery)) _results.Add(Message("No binding matches “" + _lastSearchQuery + "” yet.", Idle));
        foreach (var hit in _searchResults)
        {
            var live = Live(hit);
            var card = new VisualElement(); card.AddToClassList("bsl-item"); _results.Add(card);
            var head = Row("bsl-row"); card.Add(head);
            head.Add(Text(hit.layerName + " / " + hit.statePath, "bsl-strong bsl-grow", "Animator: " + hit.animatorPath));
            head.Add(Bar(live.activation, live.activation > 0.001f ? Good : Idle, "Live activation"));
            head.Add(Text(F(live.activation), "bsl-value"));
            var clip = Row("bsl-row"); card.Add(clip);
            var clipAsset = string.IsNullOrWhiteSpace(hit.clipPath) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip>(hit.clipPath);
            // One line: clip · property · path · values; the full asset path and binding type are in tooltips.
            var clipLink = Link(hit.clipName, clipAsset, hit.clipPath); clipLink.AddToClassList("bsl-clip"); clip.Add(clipLink);
            clip.Add(Text(hit.bindingProperty, "bsl-code bsl-code--property", hit.bindingTypeName));
            clip.Add(Text(string.IsNullOrEmpty(hit.bindingPath) ? "(root)" : hit.bindingPath, "bsl-code bsl-code--path", "Binding path"));
            if (!string.IsNullOrWhiteSpace(live.mask)) clip.Add(Chip(live.mask, Idle, "Avatar mask"));
            float smr = SmrFor(hit);
            if (!float.IsNaN(live.sampled) || !float.IsNaN(smr))
                clip.Add(Text((float.IsNaN(live.sampled) ? "" : "clip " + live.sampled.ToString("0.0", CultureInfo.InvariantCulture)) + (float.IsNaN(smr) ? "" : "  mesh " + smr.ToString("0.0", CultureInfo.InvariantCulture)), "bsl-meta",
                    "Clip value at the current time, and the value on the mesh right now."));
        }
    }

    private void RenderBoneStatus(bool bones)
    {
        _boneStatus.style.display = bones && !string.IsNullOrWhiteSpace(_lastSearchQuery) ? DisplayStyle.Flex : DisplayStyle.None;
        if (_boneStatus.style.display == DisplayStyle.None) return;
        if (_boneFound)
        {
            _boneStatus.text = "Animated: " + (string.IsNullOrWhiteSpace(_boneFoundPath) ? "humanoid muscle (no path)" : _boneFoundPath);
            _boneStatus.style.color = Good;
            return;
        }
        var found = FindBone(_lastSearchQuery);
        _boneStatus.text = found.bone != null ? "In the hierarchy, but no found clip animates it: " + found.path : "No bone matches in the hierarchy.";
        _boneStatus.style.color = found.bone != null ? Warn : Bad;
    }

    // ---------------------------------------------------------------- Data

    private void RefreshData(bool rerunSearch = true)
    {
        // The scene was reloaded (Play Mode, tests, reopening it): the old object is gone, follow the active avatar again.
        if (!ReferenceEquals(_avatarRoot, null) && _avatarRoot == null)
            _avatarRoot = LiveAvatarControllerService.Instance.ResolveActiveAvatarRoot();
        if (_avatarRoot == null)
        {
            _snapshot = new LiveAvatarControllerService.AvatarControllerSnapshot();
            ClearSearchHistory();
            return;
        }
        bool forceRetry = _forceRetryGestureManagerOnNextRefresh;
        _forceRetryGestureManagerOnNextRefresh = false;
        _snapshot = LiveAvatarControllerService.Instance.CaptureSnapshot(_avatarRoot, forceRetry);
        // Attached to Gesture Manager: follow the avatar it drives.
        if (_snapshot != null && _snapshot.attachmentSource == LiveAvatarControllerService.AttachmentSource.GestureManager && _snapshot.avatarRoot != null)
            _avatarRoot = _snapshot.avatarRoot;
        if (rerunSearch && !string.IsNullOrWhiteSpace(_searchText)) RunSearch();
    }

    private void ClearSearchHistory()
    {
        _searchResults.Clear();
        _searchResultKeys.Clear();
        _lastSearchQuery = string.Empty;
        _boneFound = false;
        _boneFoundPath = string.Empty;
    }

    private void RunSearch()
    {
        if (_avatarRoot == null || string.IsNullOrWhiteSpace(_searchText)) return;
        string trimmed = _searchText.Trim();
        _lastSearchQuery = trimmed;

        // A bone path also searches its last segment: humanoid muscle bindings have an empty path and names like "LeftEye Down-Up".
        var queries = new List<string> { trimmed };
        if (_searchType == SearchType.BonePath && trimmed.Contains("/"))
        {
            string lastSegment = trimmed.Substring(trimmed.LastIndexOf('/') + 1);
            if (!string.IsNullOrWhiteSpace(lastSegment)) queries.Add(lastSegment);
        }

        bool anyNewHit = false;
        foreach (var query in queries)
        {
            var raw = LiveAvatarControllerService.Instance.SearchAllAnimationBindings(_avatarRoot, query);
            if (raw == null) continue;
            string normalized = NormalizeText(query);
            foreach (var hit in raw)
            {
                if (hit == null || !MatchesSearchType(hit, _searchType, normalized)) continue;
                string key = hit.animatorPath + "|" + hit.layerIndex + "|" + hit.statePath + "|" + hit.clipPath + "|" + hit.bindingPath + "|" + hit.bindingProperty;
                if (!_searchResultKeys.Add(key)) continue;
                _searchResults.Add(hit);
                anyNewHit = true;
                if (!_boneFound) { _boneFound = true; _boneFoundPath = hit.bindingPath ?? string.Empty; }
            }
        }
        if (anyNewHit)
            _searchResults = _searchResults
                .OrderBy(x => x.animatorPath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(x => x.layerIndex)
                .ThenBy(x => x.statePath ?? string.Empty, StringComparer.Ordinal)
                .ThenByDescending(x => x.clipActivation)
                .ToList();
    }

    private static bool MatchesSearchType(LiveAvatarControllerService.BindingSearchHit hit, SearchType type, string normalizedQuery)
    {
        string property = hit.bindingProperty ?? string.Empty;
        string path = hit.bindingPath ?? string.Empty;
        if (type == SearchType.Blendshape)
            return property.StartsWith("blendShape.", StringComparison.Ordinal) && NormalizeText(property).Contains(normalizedQuery);
        return NormalizeText(path).Contains(normalizedQuery) || NormalizeText(property).Contains(normalizedQuery);
    }

    // Clip paths are relative to the Animator, not the avatar root.
    private Transform PathRoot()
    {
        if (_avatarRoot == null) return null;
        var animator = _avatarRoot.GetComponentInChildren<Animator>(true);
        return animator != null ? animator.transform : _avatarRoot.transform;
    }

    private (Transform bone, string path) FindBone(string query)
    {
        var root = PathRoot();
        if (root == null) return (null, null);
        var exact = root.Find(query);
        if (exact != null) return (exact, query);
        string normalized = NormalizeText(query);
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            string path = AnimationUtility.CalculateTransformPath(t, root);
            if (NormalizeText(path).Contains(normalized)) return (t, path);
        }
        return (null, null);
    }

    // Bone paths as a nested menu: "Armature/Hips/Spine" opens level by level, like the hierarchy.
    private void ShowBoneMenu()
    {
        var root = PathRoot();
        if (root == null) return;
        var menu = new GenericMenu();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            string path = AnimationUtility.CalculateTransformPath(t, root);
            // A bone with children gets its own entry inside its submenu, so both can be picked.
            string item = t.childCount > 0 ? path + "/" + t.name : path;
            menu.AddItem(new GUIContent(item), path == _searchText, () =>
            {
                _searchText = path;
                ClearSearchHistory(); RunSearch(); Render();
            });
        }
        menu.DropDown(_bones.worldBound);
    }

    private (float activation, float sampled, string mask) Live(LiveAvatarControllerService.BindingSearchHit hit)
    {
        float activation = hit.clipActivation, sampled = float.NaN;
        string mask = hit.avatarMaskName;
        if (_snapshot?.blendShapeLinkStates == null) return (activation, sampled, mask);
        foreach (var state in _snapshot.blendShapeLinkStates)
        {
            if (state == null || state.layerIndex != hit.layerIndex) continue;
            if (!string.IsNullOrWhiteSpace(state.avatarMaskName)) mask = state.avatarMaskName;
            var clip = state.clips?.FirstOrDefault(c => c != null && string.Equals(c.clipName, hit.clipName, StringComparison.Ordinal));
            if (clip != null) { activation = clip.activation; sampled = clip.clipSampledBlendshapeValue; }
        }
        return (activation, sampled, mask);
    }

    private float Smr(LiveAvatarControllerService.LiveClipActivation clip) =>
        _snapshot?.avatarRoot == null || string.IsNullOrWhiteSpace(clip.blendshapeBindingPath) || string.IsNullOrWhiteSpace(clip.blendshapeBindingProperty)
            ? float.NaN : LiveAvatarControllerService.ReadSmrBlendshapeValue(_snapshot.avatarRoot, clip.blendshapeBindingPath, clip.blendshapeBindingProperty);

    private float SmrFor(LiveAvatarControllerService.BindingSearchHit hit) =>
        _snapshot?.avatarRoot == null || string.IsNullOrWhiteSpace(hit.bindingPath) || hit.bindingProperty == null || !hit.bindingProperty.StartsWith("blendShape.", StringComparison.Ordinal)
            ? float.NaN : LiveAvatarControllerService.ReadSmrBlendshapeValue(_snapshot.avatarRoot, hit.bindingPath, hit.bindingProperty);

    private static string NormalizeText(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // ---------------------------------------------------------------- Elements

    private static bool Changed(ref string last, StringBuilder key)
    {
        string current = key.ToString();
        if (current == last) return false;
        last = current;
        return true;
    }

    private static string F(float value) => float.IsNaN(value) ? "" : value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string S(int count) => count == 1 ? "" : "s";

    private static string Value(LiveAvatarControllerService.LiveParameterValue p) =>
        p.type == AnimatorControllerParameterType.Float ? F(p.floatValue) : p.type == AnimatorControllerParameterType.Int ? p.intValue.ToString() : p.boolValue ? "true" : "false";

    private static VisualElement Row(string classes)
    {
        var row = new VisualElement();
        foreach (var c in classes.Split(' ')) row.AddToClassList(c);
        return row;
    }

    private static Label Text(string text, string classes, string tooltip = null)
    {
        var label = new Label(text) { tooltip = tooltip };
        foreach (var c in classes.Split(' ')) label.AddToClassList(c);
        return label;
    }

    private static Label Chip(string text, Color color, string tooltip = null)
    {
        var chip = Text(text, "bsl-chip", tooltip);
        Tint(chip, color);
        return chip;
    }

    private static void Tint(VisualElement chip, Color color)
    {
        chip.style.color = color;
        chip.style.backgroundColor = new Color(color.r, color.g, color.b, 0.14f);
    }

    private static VisualElement Message(string text, Color color)
    {
        var message = Text(text, "bsl-message");
        message.style.borderLeftColor = color;
        return message;
    }

    // A name that selects and pings its object on click.
    private static Label Link(string text, UnityEngine.Object target, string tooltip)
    {
        var link = Text(text, target != null ? "bsl-link" : "bsl-meta", tooltip);
        if (target != null)
            link.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                EditorGUIUtility.PingObject(target);
                if (evt.clickCount == 2) Selection.activeObject = target;
            });
        return link;
    }

    private static VisualElement Bar(float value, Color color, string tooltip = null)
    {
        var bar = new VisualElement { tooltip = tooltip }; bar.AddToClassList("bsl-bar-track");
        var fill = new VisualElement(); fill.AddToClassList("bsl-bar-fill");
        fill.style.width = Length.Percent(Mathf.Clamp01(value) * 100f);
        fill.style.backgroundColor = color;
        bar.Add(fill);
        return bar;
    }

    private static Button SmallButton(string text, string tooltip, Action action)
    {
        var button = new Button(action) { text = text, tooltip = tooltip };
        button.AddToClassList("bsl-button");
        // Immediate feedback on press; the click still follows Unity's normal release behaviour.
        button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("bsl-button--pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("bsl-button--pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("bsl-button--pressed"));
        return button;
    }

    private static VisualElement Section(VisualElement parent, string title, out Label count)
    {
        var header = Row("bsl-section"); parent.Add(header);
        header.Add(Text(title, "bsl-title"));
        count = Text("", "bsl-count"); header.Add(count);
        var body = new VisualElement(); body.AddToClassList("bsl-card"); parent.Add(body);
        return body;
    }
}
#endif
