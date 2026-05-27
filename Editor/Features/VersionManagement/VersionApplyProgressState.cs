#if UNITY_EDITOR
using UnityEngine;

public sealed class VersionApplyProgressState
{
    private const string DefaultStepText = "Preparing version switch...";

    public bool IsRunning { get; private set; }
    public float Progress { get; private set; } = 1f;
    public string StepText { get; private set; } = DefaultStepText;
    public Color FillColor { get; private set; } = new Color32(0, 218, 109, 255);

    public void SetFillColor(Color color)
    {
        FillColor = color;
    }

    public void Begin(string stepText = null, Color? fillColor = null)
    {
        if (fillColor.HasValue)
        {
            FillColor = fillColor.Value;
        }

        IsRunning = true;
        Progress = 0f;
        StepText = string.IsNullOrWhiteSpace(stepText) ? DefaultStepText : stepText;
    }

    public void Report(float progress, string stepText)
    {
        Progress = Mathf.Clamp01(progress);
        if (!string.IsNullOrWhiteSpace(stepText))
        {
            StepText = stepText;
        }
    }

    public void Complete()
    {
        Progress = 1f;
        StepText = null;
        IsRunning = false;
    }

    public void Fail(string stepText = null)
    {
        Progress = 1f;
        StepText = string.IsNullOrWhiteSpace(stepText) ? null : stepText;
        IsRunning = false;
    }
}
#endif
