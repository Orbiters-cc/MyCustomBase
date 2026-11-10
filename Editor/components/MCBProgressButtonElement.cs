#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public struct MCBProgressButtonData
{
    public string text;
    public bool enabled;
    public bool isRunning;
    public float progress;
    public Color fillColor;
    public Color trackColor;
}

public sealed class MCBProgressButtonElement : Button
{
    private readonly Func<MCBProgressButtonData> dataProvider;
    private readonly VisualElement track;
    private readonly VisualElement fill;
    private readonly Label label;
    private IVisualElementScheduledItem animation;
    private float displayedProgress = 1f;
    private Color displayedFillColor;
    private Color displayedTrackColor;
    private bool wasRunning;
    private double lastUpdateTime;
    // What the element shows now: a tick that changes nothing writes nothing (no style change, no relayout).
    private bool shown;
    private string shownText;
    private bool shownEnabled;
    private Color shownTrackColor, shownFillColor;
    private float shownWidth;

    public MCBProgressButtonElement(Action clicked, Func<MCBProgressButtonData> dataProvider)
        : base(clicked)
    {
        this.dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        text = string.Empty;
        AddToClassList("mcb-progress-button");

        track = new VisualElement();
        track.AddToClassList("mcb-progress-button__track");
        Add(track);

        fill = new VisualElement();
        fill.AddToClassList("mcb-progress-button__fill");
        track.Add(fill);

        label = new Label();
        label.AddToClassList("mcb-progress-button__label");
        Add(label);

        RegisterCallback<AttachToPanelEvent>(_ =>
        {
            lastUpdateTime = EditorApplication.timeSinceStartup;
            var data = this.dataProvider();
            displayedFillColor = data.fillColor;
            displayedTrackColor = data.isRunning ? data.trackColor : data.fillColor;
            displayedProgress = data.isRunning ? Mathf.Clamp01(data.progress) : 1f;
            wasRunning = data.isRunning;
            UpdateVisuals();
            animation = schedule.Execute(UpdateVisuals).Every(16);
        });

        RegisterCallback<DetachFromPanelEvent>(_ =>
        {
            animation?.Pause();
            animation = null;
        });
    }

    private void UpdateVisuals()
    {
        var data = dataProvider();
        bool isRunning = data.isRunning;
        float targetProgress = isRunning ? Mathf.Clamp01(data.progress) : 1f;
        Color targetFillColor = data.fillColor;
        Color targetTrackColor = isRunning ? data.trackColor : data.fillColor;

        if (isRunning && !wasRunning)
        {
            displayedProgress = 0f;
            displayedTrackColor = targetTrackColor;
        }

        double now = EditorApplication.timeSinceStartup;
        float deltaTime = lastUpdateTime > 0d ? Mathf.Clamp((float)(now - lastUpdateTime), 0f, 0.1f) : 0.016f;
        lastUpdateTime = now;

        float progressSmoothing = 1f - Mathf.Exp(-deltaTime * 10f);
        float colorSmoothing = 1f - Mathf.Exp(-deltaTime * 7f);
        displayedProgress = Mathf.Lerp(displayedProgress, targetProgress, progressSmoothing);
        displayedFillColor = Color.Lerp(displayedFillColor, targetFillColor, colorSmoothing);
        displayedTrackColor = Color.Lerp(displayedTrackColor, targetTrackColor, colorSmoothing);

        string nextText = data.text ?? string.Empty;
        float width = Mathf.Clamp01(displayedProgress) * 100f;
        if (!shown || !string.Equals(shownText, nextText, StringComparison.Ordinal)) label.text = shownText = nextText;
        if (!shown || shownEnabled != data.enabled) SetEnabled(shownEnabled = data.enabled);
        if (!shown || !Same(shownTrackColor, displayedTrackColor)) track.style.backgroundColor = shownTrackColor = displayedTrackColor;
        if (!shown || !Same(shownFillColor, displayedFillColor)) fill.style.backgroundColor = shownFillColor = displayedFillColor;
        if (!shown || Mathf.Abs(shownWidth - width) > 0.01f) fill.style.width = Length.Percent(shownWidth = width);
        shown = true;
        wasRunning = isRunning;
    }

    // Below what a colour channel can show (1/255).
    private static bool Same(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.001f && Mathf.Abs(a.g - b.g) < 0.001f && Mathf.Abs(a.b - b.b) < 0.001f && Mathf.Abs(a.a - b.a) < 0.001f;
}
#endif
