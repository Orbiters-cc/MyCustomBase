#if UNITY_EDITOR
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The note at the bottom of MCB's inspector that says its warnings and errors are sent to Orbiters (EditorIssueReporter),
/// shown on this computer until the person answers it: Got it keeps sending, Turn off stops it. What is sent opens the
/// details in place. Nothing is sent before it was laid out in a window once. The switch stays under Advanced Options.
/// </summary>
internal static class DiagnosticsNotice
{
    // The card fades and slides away before it leaves the page (mcb-diagnostics-notice--leaving).
    private const long LeaveMilliseconds = 200;
    private static readonly Color Accent = new Color32(0, 218, 109, 255);

    /// <summary>Until the person chose, here or with the switch.</summary>
    public static bool Needed => !EditorIssueReporter.HasChoice;

    public static void Fill(VisualElement host)
    {
        if (host == null) return;
        host.Clear();
        host.style.display = Needed ? DisplayStyle.Flex : DisplayStyle.None;
        if (!Needed) return;

        var notice = new VisualElement();
        notice.AddToClassList("mcb-diagnostics-notice");
        notice.Add(Icon());

        var body = new VisualElement();
        body.AddToClassList("mcb-diagnostics-notice__body");
        var title = new Label("Error reports are on");
        title.AddToClassList("mcb-diagnostics-notice__title");
        body.Add(title);
        var text = new Label("MCB sends its own warnings and errors to Orbiters so they get fixed. Personal details are removed first.");
        text.AddToClassList("mcb-diagnostics-notice__text");
        body.Add(text);

        var details = new Label(EditorIssueReporter.ConsentText + " You can change it any time under Advanced Options.");
        details.AddToClassList("mcb-diagnostics-notice__details");
        details.style.display = DisplayStyle.None;

        var links = new VisualElement();
        links.AddToClassList("mcb-diagnostics-notice__links");
        var whatIsSent = Link("What is sent?", null);
        ButtonInteraction.RegisterImmediateClick(whatIsSent, () =>
        {
            bool open = details.style.display == DisplayStyle.None;
            details.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            whatIsSent.text = open ? "Hide details" : "What is sent?";
        });
        links.Add(whatIsSent);
        var dot = new Label("·");
        dot.AddToClassList("mcb-diagnostics-notice__dot");
        links.Add(dot);
        links.Add(Link("Turn off", () => Answer(false, notice, host)));
        body.Add(links);
        body.Add(details);
        notice.Add(body);

        var gotIt = new Button { text = "Got it" };
        gotIt.AddToClassList("mcb-diagnostics-notice__confirm");
        ButtonInteraction.RegisterImmediateClick(gotIt, () => Answer(true, notice, host));
        notice.Add(gotIt);

        // Told once it really showed: a card built for a window that never lays it out does not count.
        notice.RegisterCallback<GeometryChangedEvent>(e => { if (notice.panel != null && e.newRect.height > 0f) EditorIssueReporter.NoticeShown = true; });
        host.Add(notice);
    }

    // The choice is saved at once; the card then leaves smoothly.
    private static void Answer(bool share, VisualElement notice, VisualElement host)
    {
        EditorIssueReporter.ShareIssueLogs = share;
        EditorIssueReporter.RefreshListener();
        notice.SetEnabled(false);
        notice.AddToClassList("mcb-diagnostics-notice--leaving");
        notice.schedule.Execute(() => Fill(host)).StartingIn(LeaveMilliseconds);
    }

    private static Button Link(string label, System.Action onClick)
    {
        var button = new Button { text = label };
        button.AddToClassList("mcb-diagnostics-notice__link");
        if (onClick != null) ButtonInteraction.RegisterImmediateClick(button, onClick);
        return button;
    }

    // A small shield with a check, in MCB's green, on a soft tile.
    private static VisualElement Icon()
    {
        var tile = new VisualElement();
        tile.AddToClassList("mcb-diagnostics-notice__icon");
        var shield = new VisualElement();
        shield.AddToClassList("mcb-diagnostics-notice__shield");
        shield.generateVisualContent += context =>
        {
            var rect = shield.contentRect;
            float w = rect.width, h = rect.height;
            if (w <= 0f || h <= 0f) return;
            var painter = context.painter2D;
            painter.lineWidth = 1.5f;
            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;
            painter.strokeColor = Accent;
            painter.fillColor = new Color(Accent.r, Accent.g, Accent.b, 0.16f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(w * 0.5f, 1f));
            painter.LineTo(new Vector2(w - 1.5f, h * 0.2f));
            painter.LineTo(new Vector2(w - 1.5f, h * 0.48f));
            painter.BezierCurveTo(new Vector2(w - 1.5f, h * 0.76f), new Vector2(w * 0.74f, h * 0.92f), new Vector2(w * 0.5f, h - 1f));
            painter.BezierCurveTo(new Vector2(w * 0.26f, h * 0.92f), new Vector2(1.5f, h * 0.76f), new Vector2(1.5f, h * 0.48f));
            painter.LineTo(new Vector2(1.5f, h * 0.2f));
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
            painter.BeginPath();
            painter.MoveTo(new Vector2(w * 0.31f, h * 0.5f));
            painter.LineTo(new Vector2(w * 0.45f, h * 0.64f));
            painter.LineTo(new Vector2(w * 0.7f, h * 0.36f));
            painter.Stroke();
        };
        tile.Add(shield);
        return tile;
    }
}
#endif
