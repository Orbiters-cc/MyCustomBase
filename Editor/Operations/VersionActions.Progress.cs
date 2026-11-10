#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCBEditorUtils;

// What the version buttons show while a download, switch or reset runs: the apply progress and its pacing.
public partial class VersionActions
{
    private void BeginApplyProgress(string stepText, float progress)
    {
        session.isApplying = true;
        if (!ApplyProgress.IsRunning)
        {
            applyProgressGeneration++;
            ApplyProgress.Begin(stepText);
        }

        ApplyProgress.Report(MapApplyProgress(progress), stepText);
        session.Repaint();
    }

    private void ReportApplyProgress(float progress, string stepText)
    {
        if (!ApplyProgress.IsRunning)
        {
            applyProgressGeneration++;
            session.isApplying = true;
            ApplyProgress.Begin(stepText);
        }

        ApplyProgress.Report(MapApplyProgress(progress), stepText);
        session.Repaint();
    }

    private void FinishApplyProgress(bool success)
    {
        if (success)
        {
            int generation = ++applyProgressGeneration;
            ApplyProgress.Report(1f, "Apply complete...");
            ResetApplyProgressRange();
            session.Repaint();
            EditorCoroutineUtility.StartCoroutineOwnerless(CompleteApplyProgressAfterVisualHold(generation));
            // Other tools (My Avatar) check the avatar's clothing against the new custom base.
            if (session.customBaseTarget != null) MCBReFitIntegration.NotifyCustomBaseChanged(session.customBaseTarget);
        }
        else
        {
            applyProgressGeneration++;
            session.isApplying = false;
            ResetApplyProgressRange();
            ApplyProgress.Fail();
            session.Repaint();
            session.RequestRefresh(McbViewParts.All);
        }
    }

    private int StartApplyProgress(string stepText, float progress)
    {
        ResetApplyProgressRange();
        applyProgressGeneration++;
        session.isApplying = true;
        ApplyProgress.Begin(stepText);
        ApplyProgress.Report(MapApplyProgress(progress), stepText);
        session.Repaint();
        return applyProgressGeneration;
    }

    private void SetApplyProgressRange(float progressBase, float progressScale)
    {
        applyProgressBase = Mathf.Clamp01(progressBase);
        applyProgressScale = Mathf.Clamp01(progressScale);
    }

    private void ResetApplyProgressRange()
    {
        applyProgressBase = 0f;
        applyProgressScale = 1f;
    }

    private float MapApplyProgress(float progress)
    {
        return Mathf.Clamp01(applyProgressBase + Mathf.Clamp01(progress) * applyProgressScale);
    }

    private static float GetVisibleDownloadApplyProgress(double downloadStartedAt, float downloadProgress, float previousProgress)
    {
        float realProgress = Mathf.Lerp(DownloadApplyProgressStart, DownloadApplyProgressComplete, Mathf.Clamp01(downloadProgress));
        float syntheticProgress = DownloadApplyProgressStart +
            (float)(EditorApplication.timeSinceStartup - downloadStartedAt) * DownloadApplySyntheticProgressPerSecond;
        syntheticProgress = Mathf.Min(syntheticProgress, DownloadApplySyntheticProgressMax);

        return Mathf.Clamp01(Mathf.Max(previousProgress, Mathf.Max(realProgress, syntheticProgress)));
    }

    private void StartApplyOrResetWithIntro(CustomBaseVersion version, bool isReset, string stepText)
    {
        if (session.isApplying || ApplyProgress.IsRunning || RefitBlocksSwitch())
        {
            return;
        }

        int generation = StartApplyProgress(stepText, 0f);
        EditorCoroutineUtility.StartCoroutineOwnerless(ApplyOrResetAfterIntroCoroutine(version, isReset, stepText, generation));
    }

    private IEnumerator ApplyOrResetAfterIntroCoroutine(CustomBaseVersion version, bool isReset, string stepText, int generation)
    {
        double startTime = EditorApplication.timeSinceStartup;
        while (applyProgressGeneration == generation &&
               EditorApplication.timeSinceStartup - startTime < ApplyProgressIntroSeconds)
        {
            float localProgress = (float)((EditorApplication.timeSinceStartup - startTime) / ApplyProgressIntroSeconds);
            ApplyProgress.Report(Mathf.Lerp(0f, 0.04f, Mathf.Clamp01(localProgress)), stepText);
            session.Repaint();
            yield return null;
        }

        if (applyProgressGeneration != generation)
        {
            yield break;
        }

        yield return ApplyOrResetCoroutine(version, isReset);
    }

    private IEnumerator CompleteApplyProgressAfterVisualHold(int generation)
    {
        double startTime = EditorApplication.timeSinceStartup;
        while (applyProgressGeneration == generation &&
               EditorApplication.timeSinceStartup - startTime < ApplyProgressCompletionHoldSeconds)
        {
            session.Repaint();
            yield return null;
        }

        if (applyProgressGeneration != generation)
        {
            yield break;
        }

        session.isApplying = false;
        ApplyProgress.Complete();
        session.Repaint();
        session.RequestRefresh(McbViewParts.All);
    }
}
#endif
