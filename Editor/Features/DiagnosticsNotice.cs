#if UNITY_EDITOR
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The card of MCB's inspector that says its warnings and errors are sent to Orbiters (EditorIssueReporter), shown on
/// this computer until the person answers it: Got it keeps sending, Turn off stops it. Nothing is sent before it was
/// laid out in a window once. The switch stays under Advanced Settings.
/// </summary>
internal static class DiagnosticsNotice
{
    /// <summary>Until the person chose, here or with the switch.</summary>
    public static bool Needed => !EditorIssueReporter.HasChoice;

    public static void Fill(VisualElement host)
    {
        if (host == null) return;
        host.Clear();
        host.style.display = Needed ? DisplayStyle.Flex : DisplayStyle.None;
        if (!Needed) return;

        var notice = new VisualElement();
        notice.AddToClassList("mcb-legacy-notice");
        notice.AddToClassList("mcb-diagnostics-notice");
        var title = Text("MCB reports its own errors to Orbiters", 12, FontStyle.Bold, new Color(0.88f, 0.88f, 0.88f));
        title.AddToClassList("mcb-legacy-notice__title");
        notice.Add(title);
        var text = Text("When MCB logs a warning or an error, it is sent to Orbiters with its stack trace so it can be fixed. Tokens, e-mail addresses, "
            + "VRChat ids, account and computer names and folder paths are removed first, and nothing from Unity or from other tools is sent. "
            + "You can change this any time under Advanced Settings.", 11, FontStyle.Normal, new Color(0.72f, 0.72f, 0.72f));
        text.AddToClassList("mcb-legacy-notice__text");
        notice.Add(text);
        var row = new VisualElement();
        row.AddToClassList("mcb-row");
        row.AddToClassList("mcb-legacy-notice__actions");
        row.Add(Choice("Got it", true, host));
        row.Add(Choice("Turn off", false, host));
        notice.Add(row);
        // Told once it really showed: a card built for a window that never lays it out does not count.
        notice.RegisterCallback<GeometryChangedEvent>(e => { if (notice.panel != null && e.newRect.height > 0f) EditorIssueReporter.NoticeShown = true; });
        host.Add(notice);
    }

    private static Button Choice(string label, bool share, VisualElement host)
    {
        var button = new Button { text = label };
        button.AddToClassList("mcb-button");
        button.AddToClassList("mcb-button--toolbar");
        ButtonInteraction.RegisterImmediateClick(button, () =>
        {
            EditorIssueReporter.ShareIssueLogs = share;
            EditorIssueReporter.RefreshListener();
            Fill(host);
        });
        return button;
    }

    private static Label Text(string value, int size, FontStyle style, Color color)
    {
        var label = new Label(value);
        label.AddToClassList("mcb-label");
        label.style.fontSize = size;
        label.style.unityFontStyleAndWeight = style;
        label.style.color = color;
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        return label;
    }
}
#endif
