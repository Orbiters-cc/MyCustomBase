#if UNITY_EDITOR
using System;
using Orbiters.Toolkit.Editor;
using UnityEngine.UIElements;

/// <summary>Builders for the creator's filled section cards (mcb-sections.uss): pills, chips and setting rows.</summary>
internal static class McbSectionUi
{
    public static VisualElement Section(string title, string caption, out VisualElement body, params VisualElement[] headerActions)
    {
        var section = new VisualElement();
        section.AddToClassList("mcb-section");
        var header = new VisualElement();
        header.AddToClassList("mcb-section__header");
        var titles = new VisualElement();
        titles.AddToClassList("mcb-section__titles");
        titles.Add(Text(title, "mcb-section__title"));
        if (!string.IsNullOrEmpty(caption)) titles.Add(Text(caption, "mcb-section__caption"));
        header.Add(titles);
        foreach (var action in headerActions) if (action != null) header.Add(action);
        section.Add(header);
        body = new VisualElement();
        body.AddToClassList("mcb-section__body");
        section.Add(body);
        return section;
    }

    /// <summary>A section whose header folds its body: the caption summarizes it while folded.</summary>
    public static VisualElement FoldableSection(string title, string caption, bool expanded, Action<bool> toggled, out VisualElement body, params VisualElement[] headerActions)
    {
        var chevron = new VectorIcon(IconGlyph.Chevron);
        chevron.AddToClassList("mcb-section__chevron");
        chevron.style.rotate = new Rotate(new Angle(expanded ? 90 : 0, AngleUnit.Degree));
        var section = Section(title, caption, out body, headerActions);
        section.AddToClassList("mcb-section--foldable");
        section.EnableInClassList("mcb-section--folded", !expanded);
        var header = section.Q(className: "mcb-section__header");
        header.Insert(0, chevron);
        header.tooltip = expanded ? "Fold " + title : "Show " + title;
        // Fold on pointer down for immediate feedback; header actions keep their own clicks.
        header.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0 || evt.target is Button || (evt.target as VisualElement)?.GetFirstAncestorOfType<Button>() != null) return;
            toggled(!expanded);
            evt.StopPropagation();
        });
        if (!expanded) body.style.display = DisplayStyle.None;
        return section;
    }

    public static Button Pill(string text, Action onClick, params string[] variants)
    {
        var button = new Button { text = text };
        button.AddToClassList("mcb-pill");
        foreach (var variant in variants) if (!string.IsNullOrEmpty(variant)) button.AddToClassList("mcb-pill--" + variant);
        ButtonInteraction.RegisterImmediateClick(button, onClick);
        return button;
    }

    public static VisualElement IconAction(IconGlyph glyph, string tooltip, Action onClick)
    {
        var action = new VisualElement { focusable = true, tooltip = tooltip };
        action.AddToClassList("mcb-icon-action");
        action.Add(new VectorIcon(glyph));
        action.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0 || !action.enabledInHierarchy) return;
            evt.StopPropagation();
            onClick();
        });
        return action;
    }

    public static VisualElement Setting(string title, string detail, VisualElement control, bool first = false)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-setting");
        if (first) row.AddToClassList("mcb-setting--first");
        var text = new VisualElement();
        text.AddToClassList("mcb-setting__text");
        text.Add(Text(title, "mcb-setting__title"));
        if (!string.IsNullOrEmpty(detail)) text.Add(Text(detail, "mcb-setting__detail"));
        row.Add(text);
        if (control != null) row.Add(control);
        return row;
    }

    public static VisualElement Chip(string text, Action onRemove = null, bool accent = false, string tooltip = null)
    {
        var chip = new VisualElement { tooltip = tooltip ?? text };
        chip.AddToClassList("mcb-chip");
        if (accent) chip.AddToClassList("mcb-chip--accent");
        chip.Add(Text(text, "mcb-chip__label"));
        if (onRemove != null) chip.Add(IconAction(IconGlyph.Close, "Remove " + text, onRemove));
        else chip.AddToClassList("mcb-chip--plain");
        return chip;
    }

    public static VisualElement Row(params string[] classes)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-inline");
        foreach (var name in classes) row.AddToClassList(name);
        return row;
    }

    public static Label Text(string text, params string[] classes)
    {
        var label = new Label(text);
        foreach (var name in classes) label.AddToClassList(name);
        return label;
    }

    public static Label Note(string text, string variant = null)
    {
        var label = Text(text, "mcb-section__note");
        if (variant != null) label.AddToClassList("mcb-section__note--" + variant);
        return label;
    }
}
#endif
