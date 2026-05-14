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

        label.text = data.text ?? string.Empty;
        SetEnabled(data.enabled);
        track.style.backgroundColor = displayedTrackColor;
        fill.style.backgroundColor = displayedFillColor;
        fill.style.width = Length.Percent(Mathf.Clamp01(displayedProgress) * 100f);
        wasRunning = isRunning;
    }
}
#endif
