#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class MCBLoadingBarElement : VisualElement
{
    private readonly VisualElement fill;
    private IVisualElementScheduledItem updateSchedule;
    private float displayedProgress;
    private float displayedOpacity;
    private double lastUpdateTime;
    private bool isAttached;

    public MCBLoadingBarElement()
    {
        pickingMode = PickingMode.Ignore;
        AddToClassList("mcb-loading-bar");

        var track = new VisualElement();
        track.AddToClassList("mcb-loading-bar__track");
        Add(track);

        fill = new VisualElement();
        fill.AddToClassList("mcb-loading-bar__fill");
        track.Add(fill);

        style.display = DisplayStyle.None;

        updateSchedule = schedule.Execute(UpdateProgress).Every(16);
        updateSchedule.Pause();
        RegisterCallback<AttachToPanelEvent>(_ =>
        {
            isAttached = true;
            lastUpdateTime = EditorApplication.timeSinceStartup;
            ProgressBarManager.Instance.ProgressChanged += Wake;
            Wake();
        });
        RegisterCallback<DetachFromPanelEvent>(_ =>
        {
            isAttached = false;
            ProgressBarManager.Instance.ProgressChanged -= Wake;
            updateSchedule?.Pause();
        });
    }

    private void Wake()
    {
        if (!isAttached)
        {
            return;
        }

        lastUpdateTime = EditorApplication.timeSinceStartup;
        updateSchedule?.Resume();
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        bool hasProgress = ProgressBarManager.Instance.TryGetAverageProgress(out float targetProgress);
        targetProgress = hasProgress ? Mathf.Clamp01(targetProgress) : 1f;

        double now = EditorApplication.timeSinceStartup;
        float deltaTime = lastUpdateTime > 0d ? Mathf.Clamp((float)(now - lastUpdateTime), 0f, 0.1f) : 0.016f;
        lastUpdateTime = now;

        float smoothing = 1f - Mathf.Exp(-deltaTime * 10f);
        displayedProgress = Mathf.Lerp(displayedProgress, targetProgress, smoothing);
        displayedOpacity = Mathf.Lerp(displayedOpacity, hasProgress ? 1f : 0f, 1f - Mathf.Exp(-deltaTime * 7f));

        if (hasProgress && displayedProgress < 0.02f)
        {
            displayedProgress = Mathf.Max(displayedProgress, 0.02f);
        }

        bool shouldDisplay = hasProgress || displayedOpacity > 0.01f;
        style.display = shouldDisplay ? DisplayStyle.Flex : DisplayStyle.None;
        style.opacity = Mathf.Clamp01(displayedOpacity);

        if (!shouldDisplay)
        {
            displayedProgress = 0f;
            updateSchedule?.Pause();
        }

        fill.style.width = Length.Percent(Mathf.Clamp01(displayedProgress) * 100f);
    }
}
#endif
