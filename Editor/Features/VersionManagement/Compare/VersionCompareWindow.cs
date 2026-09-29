using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// What a version changes on the avatar's meshes, before applying it: the avatar turning in 3D with the changes glowing,
/// in clay or with the creator's own materials, compared with how it is now or with its original model. Opened from the
/// "See differences" button of a version that ships meshes; downloads the version first when needed.
/// </summary>
internal sealed class VersionCompareWindow : EditorWindow
{
    private const string StyleSheetPath = "Packages/orbiters.mcb/Editor/Styles/mcb-version-compare.uss";
    private static readonly string[] LookNames = { "Changes", "Clay", "Textured" };
    private static readonly string[] LookTips =
    {
        "The surface that moves glows: amber a little, magenta the most.",
        "The shape alone, in soft clay.",
        "With your avatar's own materials: how it will look."
    };
    private static readonly string[] LayoutNames = { "Slider", "Side by side", "Overlay" };
    private static readonly string[] LayoutTips =
    {
        "Drag the handle to wipe between before and after.",
        "Before and after next to each other, turning together.",
        "After, with before as a ghost: see exactly where the shape moved."
    };

    [NonSerialized] private MCBEditor editor;
    [NonSerialized] private MyCustomBase target;
    [NonSerialized] private CustomBaseVersion version;
    [NonSerialized] private VersionMeshComparison comparison;
    [NonSerialized] private IEnumerator loading;
    [NonSerialized] private bool downloadRequested, firstShow;
    [NonSerialized] private CompareReference reference = CompareReference.AvatarNow;

    private VersionCompareStage stage;
    private VisualElement overlay, spinner, progressFill, side, legend, legendKinds, toolbar, layoutBar, retry;
    private Label overlayStep, overlayDetail, title, subtitle, badge, legendMax;
    private ScrollView sideScroll;
    private Button applyButton, contextButton, turntableButton;
    private Label applyCaption;
    private SegmentedControl lookControl, layoutControl, referenceControl;
    private readonly Dictionary<ComparedPart, VisualElement> partRows = new Dictionary<ComparedPart, VisualElement>();
    private float spin;

    public static void Open(MCBEditor editor, CustomBaseVersion version)
    {
        if (editor == null || version == null) return;
        bool existed = HasOpenInstances<VersionCompareWindow>();
        var window = GetWindow<VersionCompareWindow>(false, "Version differences", true);
        window.minSize = new Vector2(880f, 560f);
        if (!existed)
        {
            var main = EditorGUIUtility.GetMainWindowPosition();
            var size = new Vector2(Mathf.Min(1220f, main.width - 80f), Mathf.Min(760f, main.height - 80f));
            window.position = new Rect(main.x + (main.width - size.x) * 0.5f, main.y + (main.height - size.y) * 0.5f, size.x, size.y);
        }
        window.Begin(editor, version);
    }

    private void OnEnable()
    {
        // After a script reload the scene objects and services this window was given are gone: open it again from the list.
        if (version == null) EditorApplication.delayCall += () => { if (this != null && version == null) Close(); };
    }

    private void OnDisable()
    {
        stage?.Dispose();
        comparison?.Dispose();
        comparison = null;
        loading = null;
    }

    private void Begin(MCBEditor owner, CustomBaseVersion shown)
    {
        comparison?.Dispose();
        comparison = null;
        loading = null;
        editor = owner;
        target = owner.customBaseTarget;
        version = shown;
        reference = CompareReference.AvatarNow;
        downloadRequested = false;
        firstShow = true;
        titleContent = new GUIContent($"{shown.version} differences");
        Build();
        if (MCBUtils.IsVersionDownloaded(version)) StartLoading();
        else RequestDownload();
    }

    private VersionActions Actions => LiveEditor()?.versionModule?.actions;

    // The inspector that opened the window may have been rebuilt since (selection changes): use the live one.
    private MCBEditor LiveEditor()
    {
        if (editor != null) return editor;
        editor = Resources.FindObjectsOfTypeAll<MCBEditor>().FirstOrDefault(e => e != null && e.customBaseTarget == target);
        return editor;
    }

    // ---- Building ---------------------------------------------------------------------------------

    private void Build()
    {
        var root = rootVisualElement;
        root.Clear();
        partRows.Clear();
        var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
        if (sheet && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
        root.AddToClassList("mcb-cmp");

        var header = new VisualElement();
        header.AddToClassList("mcb-cmp__header");
        root.Add(header);
        badge = new Label(version.version);
        badge.AddToClassList("mcb-cmp__badge");
        header.Add(badge);
        var titles = new VisualElement();
        titles.AddToClassList("mcb-cmp__titles");
        header.Add(titles);
        title = new Label(string.IsNullOrWhiteSpace(version.title) ? $"Version {version.version}" : version.title);
        title.AddToClassList("mcb-cmp__title");
        titles.Add(title);
        subtitle = new Label("Getting the version ready…");
        subtitle.AddToClassList("mcb-cmp__subtitle");
        titles.Add(subtitle);

        var body = new VisualElement();
        body.AddToClassList("mcb-cmp__body");
        root.Add(body);

        stage = new VersionCompareStage();
        body.Add(stage);
        BuildStageControls();
        BuildOverlay();

        side = new VisualElement();
        side.AddToClassList("mcb-cmp__side");
        body.Add(side);
        sideScroll = new ScrollView(ScrollViewMode.Vertical);
        sideScroll.AddToClassList("mcb-cmp__side-scroll");
        side.Add(sideScroll);
        var footer = new VisualElement();
        footer.AddToClassList("mcb-cmp__footer");
        side.Add(footer);
        AddSkeleton();
        applyButton = CreateButton($"Apply {version.version}", Apply, "mcb-cmp__apply");
        footer.Add(applyButton);
        applyCaption = new Label();
        applyCaption.AddToClassList("mcb-cmp__caption");
        applyCaption.AddToClassList("mcb-cmp__apply-caption");
        footer.Add(applyCaption);

        root.schedule.Execute(Tick).Every(16);
        RefreshApply();
    }

    private void BuildStageControls()
    {
        layoutBar = new VisualElement();
        layoutBar.AddToClassList("mcb-cmp-stage__layouts");
        layoutControl = new SegmentedControl(LayoutNames.Select((name, i) => new SegmentedControl.Option(name, null, LayoutTips[i])), index =>
        {
            stage.SetLayout((CompareLayout)index);
            RefreshLabels();
        });
        layoutControl.SetIndex(0);
        layoutBar.Add(layoutControl);
        stage.Add(layoutBar);

        legend = new VisualElement { pickingMode = PickingMode.Ignore };
        legend.AddToClassList("mcb-cmp-stage__legend");
        var legendTitle = new Label("How far the surface moves");
        legendTitle.AddToClassList("mcb-cmp-stage__legend-title");
        legend.Add(legendTitle);
        var ramp = new VisualElement();
        ramp.AddToClassList("mcb-cmp-stage__ramp");
        ramp.style.backgroundImage = RampTexture();
        legend.Add(ramp);
        var scale = new VisualElement();
        scale.AddToClassList("mcb-cmp-stage__legend-scale");
        var zero = new Label("a little");
        zero.AddToClassList("mcb-cmp-stage__legend-label");
        scale.Add(zero);
        legendMax = new Label();
        legendMax.AddToClassList("mcb-cmp-stage__legend-label");
        scale.Add(legendMax);
        legend.Add(scale);
        legendKinds = new VisualElement();
        legendKinds.AddToClassList("mcb-cmp-stage__legend-kinds");
        legend.Add(legendKinds);
        stage.Add(legend);

        toolbar = new VisualElement();
        toolbar.AddToClassList("mcb-cmp-stage__toolbar");
        lookControl = new SegmentedControl(LookNames.Select((name, i) => new SegmentedControl.Option(name, null, LookTips[i])), index =>
        {
            stage.SetLook((CompareLook)index);
            RefreshLegend();
        });
        lookControl.SetIndex(0);
        toolbar.Add(lookControl);
        stage.Add(toolbar);

        var tools = new VisualElement();
        tools.AddToClassList("mcb-cmp-stage__tools");
        contextButton = ToolButton(new VectorIcon(IconGlyph.Clothes), "Show your clothes and accessories around the body.", () =>
        {
            stage.SetContext(!stage.ShowContext);
            contextButton.EnableInClassList("mcb-cmp-tool--on", stage.ShowContext);
        });
        tools.Add(contextButton);
        turntableButton = ToolButton(new CompareGlyph(CompareGlyph.Kind.Turntable), "Turn the avatar slowly on its own.", () =>
        {
            stage.SetTurntable(!stage.Turntable);
            turntableButton.EnableInClassList("mcb-cmp-tool--on", stage.Turntable);
        });
        tools.Add(turntableButton);
        tools.Add(ToolButton(new CompareGlyph(CompareGlyph.Kind.Frame), "Frame the whole avatar again (R, or double-click).", () =>
        {
            stage.ResetView();
            SelectPart(null);
        }));
        stage.Add(tools);
    }

    private Button ToolButton(VisualElement icon, string tip, Action onClick)
    {
        var button = CreateButton(null, onClick, "mcb-cmp-tool");
        button.tooltip = tip;
        icon.AddToClassList("mcb-cmp-tool__icon");
        button.Add(icon);
        return button;
    }

    private void BuildOverlay()
    {
        overlay = new VisualElement();
        overlay.AddToClassList("mcb-cmp-stage__overlay");
        var card = new VisualElement();
        card.AddToClassList("mcb-cmp-stage__loading");
        overlay.Add(card);
        spinner = new VisualElement();
        spinner.AddToClassList("mcb-cmp-stage__spinner");
        card.Add(spinner);
        overlayStep = new Label();
        overlayStep.AddToClassList("mcb-cmp-stage__step");
        card.Add(overlayStep);
        var track = new VisualElement();
        track.AddToClassList("mcb-cmp-stage__progress");
        progressFill = new VisualElement();
        progressFill.AddToClassList("mcb-cmp-stage__progress-fill");
        track.Add(progressFill);
        card.Add(track);
        overlayDetail = new Label();
        overlayDetail.AddToClassList("mcb-cmp-stage__detail");
        card.Add(overlayDetail);
        retry = CreateButton("Try again", () =>
        {
            if (MCBUtils.IsVersionDownloaded(version)) StartLoading();
            else { downloadRequested = false; RequestDownload(); }
        }, "mcb-cmp__pill");
        retry.style.display = DisplayStyle.None;
        card.Add(retry);
        stage.Add(overlay);
    }

    // ---- Flow: download, load, show -------------------------------------------------------------

    private void RequestDownload()
    {
        var live = LiveEditor();
        if (live == null) { Fail("Select your avatar in the scene, then open the differences again."); return; }
        SetOverlay("Downloading the version…", 0f, "It is needed to show its meshes, and to apply it later.");
        if (live.isDownloading) return;
        downloadRequested = true;
        live.versionModule.actions.StartVersionDownload(version, false);
    }

    private void StartLoading()
    {
        var actions = Actions;
        if (actions == null || target == null) { Fail("Select your avatar in the scene, then open the differences again."); return; }
        comparison?.Dispose();
        comparison = new VersionMeshComparison(actions, target, version);
        loading = comparison.Load(reference);
        SetOverlay("Reading the version…", 0f, null);
        subtitle.text = "Reading the version…";
    }

    private void Tick()
    {
        spin = (spin + 7f) % 360f;
        if (spinner != null) spinner.style.rotate = new Rotate(spin);

        if (loading != null)
        {
            try
            {
                if (!loading.MoveNext())
                {
                    loading = null;
                    ShowResults();
                    return;
                }
                SetOverlay(comparison.Step, comparison.Progress, null);
            }
            catch (Exception ex)
            {
                loading = null;
                Debug.LogException(ex);
                Fail(ex.Message);
            }
            return;
        }

        if (comparison == null && overlay.style.display != DisplayStyle.None && retry.style.display == DisplayStyle.None)
        {
            var live = LiveEditor();
            if (MCBUtils.IsVersionDownloaded(version)) { StartLoading(); return; }
            if (live == null) return;
            if (live.isDownloading)
            {
                float progress = live.versionModule.actions.DownloadProgress;
                SetOverlay(downloadRequested ? $"Downloading the version… {Mathf.RoundToInt(progress * 100f)}%" : "Waiting for another download…", downloadRequested ? progress : 0f,
                    "It is needed to show its meshes, and to apply it later.");
            }
            else if (downloadRequested) Fail("The download did not finish. Check your connection, then try again.");
            else RequestDownload();
        }

        RefreshApply();
    }

    private void SetOverlay(string step, float progress, string detail)
    {
        overlay.style.display = DisplayStyle.Flex;
        overlay.RemoveFromClassList("mcb-cmp-stage__overlay--hidden");
        overlay.RemoveFromClassList("mcb-cmp-stage__overlay--error");
        spinner.style.display = DisplayStyle.Flex;
        retry.style.display = DisplayStyle.None;
        overlayStep.text = step;
        progressFill.style.width = Length.Percent(Mathf.Clamp01(progress) * 100f);
        overlayDetail.text = detail ?? string.Empty;
        overlayDetail.style.display = string.IsNullOrEmpty(detail) ? DisplayStyle.None : DisplayStyle.Flex;
    }

    private void Fail(string message)
    {
        SetOverlay("The differences cannot be shown", 0f, message);
        overlay.AddToClassList("mcb-cmp-stage__overlay--error");
        spinner.style.display = DisplayStyle.None;
        retry.style.display = DisplayStyle.Flex;
        subtitle.text = "Something went wrong";
    }

    private void ShowResults()
    {
        overlay.AddToClassList("mcb-cmp-stage__overlay--hidden");
        overlay.schedule.Execute(() => { if (comparison != null && comparison.Ready) overlay.style.display = DisplayStyle.None; }).StartingIn(260);
        stage.Show(comparison, firstShow);
        firstShow = false;
        if (stage.ShowContext) stage.SetContext(true);
        RefreshLabels();
        RefreshSummary();
        RefreshSide();
        RefreshApply();
        stage.Focus();
    }

    private void SwitchReference(CompareReference value)
    {
        if (comparison == null || value == reference || loading != null) return;
        reference = value;
        SelectPart(null);
        loading = comparison.Run(reference);
        overlay.style.display = DisplayStyle.Flex;
        SetOverlay("Comparing…", 0.7f, null);
    }

    // ---- Presenting -------------------------------------------------------------------------------

    private string BeforeName => reference == CompareReference.Original ? "Original" : "Your avatar now";

    private void RefreshLabels()
    {
        stage.BeforeLabel = BeforeName;
        stage.AfterLabel = $"Version {version.version}";
    }

    private void RefreshSummary()
    {
        var changed = comparison.Parts.Where(p => p.Changed).ToList();
        var reshaped = changed.Where(p => p.Change == PartChange.Reshaped || p.Change == PartChange.Added || p.Change == PartChange.Removed).ToList();
        string against = reference == CompareReference.Original ? "the original model" : "your avatar now";
        if (reshaped.Count == 0)
        {
            subtitle.text = changed.Count == 0
                ? $"Same meshes as {against}"
                : $"Same shape as {against}; {Count(changed.Count, "mesh", "meshes")} {(changed.Count == 1 ? "has" : "have")} other blendshapes";
        }
        else
        {
            var biggest = reshaped.OrderByDescending(p => p.MaxDistance).First();
            subtitle.text = $"{Count(reshaped.Count, "mesh changes", "meshes change")} shape compared with {against}" +
                            (biggest.MaxDistance > 0f ? $"  ·  up to {MeshComparison.Length(biggest.MaxDistance)} on {biggest.Name}" : string.Empty);
        }

        legendMax.text = MeshComparison.Length(comparison.MaxDistance);
        legendKinds.Clear();
        if (comparison.Parts.Any(p => p.Change == PartChange.Added)) legendKinds.Add(LegendKind("New part", "mcb-cmp-swatch--added"));
        if (comparison.Parts.Any(p => p.Change == PartChange.Removed)) legendKinds.Add(LegendKind("Removed part", "mcb-cmp-swatch--removed"));
        RefreshLegend();
    }

    // The heat scale only means something in the Changes look, and when something moved.
    private void RefreshLegend()
    {
        bool moved = comparison != null && comparison.Ready && comparison.Parts.Any(p => p.Change == PartChange.Reshaped || p.Change == PartChange.Added || p.Change == PartChange.Removed);
        legend.EnableInClassList("mcb-cmp-stage__legend--hidden", !moved || lookControl.Index != (int)CompareLook.Changes);
    }

    private static VisualElement LegendKind(string text, string swatchClass)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-cmp-stage__legend-kind");
        var swatch = new VisualElement();
        swatch.AddToClassList("mcb-cmp-swatch");
        swatch.AddToClassList(swatchClass);
        row.Add(swatch);
        var label = new Label(text);
        label.AddToClassList("mcb-cmp-stage__legend-label");
        row.Add(label);
        return row;
    }

    private void RefreshSide()
    {
        sideScroll.Clear();
        partRows.Clear();

        // Compared with: the avatar now, or its original model (only different while a version is applied).
        var compareCard = Card("Compared with");
        if (comparison.AvatarHasVersion)
        {
            referenceControl = new SegmentedControl(new[]
            {
                new SegmentedControl.Option("Your avatar now", null, "The avatar as it is in your scene."),
                new SegmentedControl.Option("Original", null, "The avatar with its original model, as with no version applied.")
            }, index => SwitchReference((CompareReference)index));
            referenceControl.SetIndex((int)reference);
            compareCard.Add(referenceControl);
        }
        else
        {
            compareCard.Add(Caption("Your avatar, which has its original model."));
        }
        if (IsApplied()) compareCard.Add(Caption("This version is the one on your avatar: compare it with the original to see what it changes."));

        var changed = comparison.Parts.Where(p => p.Changed).ToList();
        var same = comparison.Parts.Where(p => !p.Changed).ToList();
        var partsCard = Card(changed.Count == 0 ? "Nothing changes shape" : Count(changed.Count, "mesh changes", "meshes change"));
        if (changed.Count == 0)
        {
            partsCard.Add(Caption("This version keeps the same meshes. It may still change settings, materials or blendshape defaults."));
            // The applied version may already have what this one changes: the original shows it.
            if (comparison.AvatarHasVersion && reference == CompareReference.AvatarNow)
            {
                var original = CreateButton("Compare with the original instead", () =>
                {
                    referenceControl?.SetIndex((int)CompareReference.Original);
                    SwitchReference(CompareReference.Original);
                }, "mcb-cmp__pill");
                original.AddToClassList("mcb-cmp__pill--inline");
                partsCard.Add(original);
            }
        }
        foreach (var part in changed) partsCard.Add(PartRow(part));
        if (same.Count > 0)
        {
            var rest = new Label($"{string.Join(", ", same.Select(p => p.Name))}  ·  unchanged");
            rest.AddToClassList("mcb-cmp__unchanged");
            partsCard.Add(rest);
        }

        var added = comparison.Parts.SelectMany(p => p.ShapesAdded).Distinct().ToList();
        var removed = comparison.Parts.SelectMany(p => p.ShapesRemoved).Distinct().ToList();
        var edited = comparison.Parts.SelectMany(p => p.ShapesChanged).Distinct().ToList();
        if (added.Count + removed.Count + edited.Count > 0)
        {
            var shapesCard = Card("Blendshapes");
            shapesCard.Add(Caption("Shown with your current values; new ones at the value the creator chose."));
            var chips = new VisualElement();
            chips.AddToClassList("mcb-cmp__chips");
            shapesCard.Add(chips);
            AddChips(chips, added, "+", "mcb-cmp__chip--added", "New");
            AddChips(chips, edited, "~", "mcb-cmp__chip--changed", "Reshaped");
            AddChips(chips, removed, "−", "mcb-cmp__chip--removed", "Removed");
        }

        var tips = Card("Tips");
        tips.Add(Caption("Click a mesh to fly to its change. Hold Space over the preview to see the avatar before, and drag the handle to wipe between both."));
    }

    private bool IsApplied() => Actions != null && Actions.IsVersionCurrentlyApplied(version);

    // Placeholder cards while the version is read, breathing gently until the real ones replace them.
    private void AddSkeleton()
    {
        foreach (int lines in new[] { 1, 3, 2 })
        {
            var card = new VisualElement();
            card.AddToClassList("mcb-cmp__card");
            card.AddToClassList("mcb-cmp__skeleton");
            var heading = new VisualElement();
            heading.AddToClassList("mcb-cmp__skeleton-bar");
            heading.AddToClassList("mcb-cmp__skeleton-bar--title");
            card.Add(heading);
            for (int i = 0; i < lines; i++)
            {
                var bar = new VisualElement();
                bar.AddToClassList("mcb-cmp__skeleton-bar");
                bar.style.width = Length.Percent(90f - i * 18f);
                card.Add(bar);
            }
            sideScroll.Add(card);
        }
        bool dim = false;
        sideScroll.schedule.Execute(() =>
        {
            dim = !dim;
            foreach (var card in sideScroll.Children().Where(c => c.ClassListContains("mcb-cmp__skeleton")))
                card.EnableInClassList("mcb-cmp__skeleton--dim", dim);
        }).Every(700).Until(() => !sideScroll.Children().Any(c => c.ClassListContains("mcb-cmp__skeleton")));
    }

    private VisualElement PartRow(ComparedPart part)
    {
        var row = new VisualElement { focusable = true, tooltip = "Fly to this change" };
        row.AddToClassList("mcb-cmp__part");
        row.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0) return;
            row.AddToClassList("mcb-cmp__part--pressed");
            SelectPart(stage.Focused == part ? null : part);
            evt.StopPropagation();
        });
        row.RegisterCallback<PointerUpEvent>(_ => row.RemoveFromClassList("mcb-cmp__part--pressed"));
        row.RegisterCallback<PointerLeaveEvent>(_ => row.RemoveFromClassList("mcb-cmp__part--pressed"));

        var dot = new VisualElement();
        dot.AddToClassList("mcb-cmp__dot");
        dot.style.backgroundColor = PartColor(part);
        row.Add(dot);

        var text = new VisualElement();
        text.AddToClassList("mcb-cmp__part-text");
        row.Add(text);
        var name = new Label(part.Name);
        name.AddToClassList("mcb-cmp__part-name");
        text.Add(name);
        var detail = new Label(Describe(part));
        detail.AddToClassList("mcb-cmp__part-detail");
        text.Add(detail);
        if (part.Change == PartChange.Reshaped)
        {
            var track = new VisualElement();
            track.AddToClassList("mcb-cmp__bar");
            var fill = new VisualElement();
            fill.AddToClassList("mcb-cmp__bar-fill");
            fill.style.width = Length.Percent(Mathf.Clamp(part.ChangedShare * 100f, 2f, 100f));
            fill.style.backgroundColor = PartColor(part);
            track.Add(fill);
            text.Add(track);
        }

        var go = new Label("›");
        go.AddToClassList("mcb-cmp__part-go");
        row.Add(go);
        partRows[part] = row;
        return row;
    }

    private void SelectPart(ComparedPart part)
    {
        stage.FocusPart(part);
        foreach (var pair in partRows) pair.Value.EnableInClassList("mcb-cmp__part--selected", pair.Key == part);
    }

    private static string Describe(ComparedPart part)
    {
        switch (part.Change)
        {
            case PartChange.Added: return "New in this version";
            case PartChange.Removed: return "Removed by this version";
            case PartChange.Reshaped:
                int share = Mathf.Max(1, Mathf.RoundToInt(part.ChangedShare * 100f));
                return $"Up to {MeshComparison.Length(part.MaxDistance)}  ·  {share}% of its surface";
            default:
                int shapes = part.ShapesAdded.Count + part.ShapesRemoved.Count + part.ShapesChanged.Count;
                return $"Same shape  ·  {Count(shapes, "blendshape differs", "blendshapes differ")}";
        }
    }

    private Color PartColor(ComparedPart part)
    {
        if (part.Change == PartChange.Added) return new Color(0.2f, 0.9f, 0.6f);
        if (part.Change == PartChange.Removed) return new Color(1f, 0.36f, 0.38f);
        if (part.Change != PartChange.Reshaped) return new Color(0.36f, 0.78f, 1f);
        float max = Mathf.Max(comparison.MaxDistance, 1e-6f);
        return Ramp(Mathf.Pow(Mathf.Clamp01(part.MaxDistance / max), 0.6f));
    }

    private static void AddChips(VisualElement chips, List<string> names, string sign, string modifier, string tip)
    {
        const int Shown = 14;
        foreach (string name in names.Take(Shown))
        {
            var chip = new Label($"{sign} {name}") { tooltip = tip };
            chip.AddToClassList("mcb-cmp__chip");
            chip.AddToClassList(modifier);
            chips.Add(chip);
        }
        if (names.Count > Shown)
        {
            var more = new Label($"+{names.Count - Shown} more") { tooltip = string.Join("\n", names.Skip(Shown)) };
            more.AddToClassList("mcb-cmp__chip");
            chips.Add(more);
        }
    }

    private void RefreshApply()
    {
        if (applyButton == null) return;
        var live = LiveEditor();
        bool applied = IsApplied();
        bool busy = live != null && (live.isDownloading || live.isApplying);
        applyButton.text = applied ? "Installed on your avatar" : $"Apply {version.version}";
        applyButton.EnableInClassList("mcb-cmp__apply--installed", applied);
        applyButton.SetEnabled(live != null && !applied && !busy);
        applyCaption.text = live == null ? "Select your avatar in the scene to apply this version."
            : applied ? "You can go back to another version from the version list."
            : busy ? "Waiting for the current download or switch to finish…"
            : "MCB keeps a backup of your model, so you can switch back any time.";
    }

    private void Apply()
    {
        var live = LiveEditor();
        if (live?.versionModule == null) return;
        if (live.versionModule.RequestApply(version)) Close();
    }

    // ---- Small helpers ----------------------------------------------------------------------------

    private VisualElement Card(string heading)
    {
        var card = new VisualElement();
        card.AddToClassList("mcb-cmp__card");
        var label = new Label(heading);
        label.AddToClassList("mcb-cmp__card-title");
        card.Add(label);
        sideScroll.Add(card);
        return card;
    }

    private static Label Caption(string text)
    {
        var label = new Label(text);
        label.AddToClassList("mcb-cmp__caption");
        return label;
    }

    private static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    private static Button CreateButton(string text, Action onClick, string className)
    {
        var button = new Button { text = text ?? string.Empty };
        button.AddToClassList(className);
        button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("mcb-cmp--pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("mcb-cmp--pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("mcb-cmp--pressed"));
        ButtonInteraction.RegisterImmediateClick(button, onClick);
        return button;
    }

    // The heat colours of the stage shader, for the legend and the parts list.
    private static Color Ramp(float t)
    {
        var amber = new Color(1f, 0.8f, 0.28f);
        var orange = new Color(1f, 0.46f, 0.2f);
        var magenta = new Color(0.96f, 0.18f, 0.5f);
        return t < 0.5f ? Color.Lerp(amber, orange, t * 2f) : Color.Lerp(orange, magenta, t * 2f - 1f);
    }

    private static Texture2D rampTexture;

    private static Texture2D RampTexture()
    {
        if (rampTexture != null) return rampTexture;
        rampTexture = new Texture2D(128, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        for (int x = 0; x < 128; x++) rampTexture.SetPixel(x, 0, Ramp(x / 127f));
        rampTexture.Apply();
        return rampTexture;
    }
}
