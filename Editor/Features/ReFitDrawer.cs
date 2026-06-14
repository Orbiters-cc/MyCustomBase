#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// "ReFit" avatar option frame (same style as Custom Veins / Sliders / Blendshapes), shown at the top of the
/// version options when a custom version is applied and the optional ReFit package is installed. Lists the
/// avatar's asset meshes (everything that is not part of the version's targeted FBX list) as toggles: tick the
/// ones that should follow this custom base and press Apply. Apply re-fits newly-ticked assets from the default
/// base body to the version body (with the version's exposed blendshapes) and restores assets that were
/// unticked. Re-fitted meshes are tracked on the component and restored when resetting to the default base.
/// </summary>
public class ReFitDrawer
{
    private static readonly Color32 ReFitGreen = new Color32(0, 218, 109, 255);
    private static readonly Color TrackColor = new Color(0.22f, 0.22f, 0.22f);

    private readonly MCBEditor editor;
    private readonly VersionApplyProgressState progress = new VersionApplyProgressState();
    private readonly HashSet<string> selectedPaths = new HashSet<string>();
    private bool initializedSelection;
    private bool isRunning;
    private bool lastFailed;
    private string lastMessage;

    public ReFitDrawer(MCBEditor editor)
    {
        this.editor = editor;
    }

    public bool BuildUIToolkit(VisualElement root)
    {
        if (!editor.isCustomBase ||
            editor.customBaseTarget == null ||
            editor.customBaseTarget.appliedCustomBaseVersion == null)
        {
            return false;
        }

        if (!MCBReFitIntegration.IsReFitAvailable)
        {
            BuildInstallPromptUIToolkit(root);
            return true;
        }

        var candidates = MCBReFitIntegration.GetRefitCandidates(editor);
        if (candidates.Count == 0)
        {
            return false;
        }

        var mcb = editor.customBaseTarget;
        var avatarRoot = mcb.transform.root;

        // Seed the selection from the assets already re-fitted (so they stay ticked).
        if (!initializedSelection)
        {
            foreach (var smr in candidates)
            {
                if (MCBReFitIntegration.IsRefitApplied(mcb, smr))
                    selectedPaths.Add(MCBReFitIntegration.GetRendererPath(avatarRoot, smr.transform));
            }
            initializedSelection = true;
        }

        var card = AvatarOptionsModule.CreateOptionCard("mcb-avatar-refit");
        card.Add(AvatarOptionsModule.CreateOptionTitle("ReFit"));

        // Toggle list of asset meshes.
        var pathOf = new Dictionary<SkinnedMeshRenderer, string>();
        foreach (var smr in candidates)
        {
            string path = MCBReFitIntegration.GetRendererPath(avatarRoot, smr.transform);
            pathOf[smr] = path;
            bool selected = selectedPaths.Contains(path);
            bool applied = MCBReFitIntegration.IsRefitApplied(mcb, smr);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            var toggle = new Toggle { value = selected };
            toggle.AddToClassList("mcb-avatar-toggle");
            toggle.SetEnabled(!isRunning);
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue) selectedPaths.Add(path);
                else selectedPaths.Remove(path);
            });
            row.Add(toggle);

            var label = AvatarOptionsModule.CreateOptionLabel(
                applied ? smr.name + "  (re-fitted)" : smr.name, 12, FontStyle.Normal, Color.white);
            label.style.marginLeft = 4;
            row.Add(label);

            card.Add(row);
        }

        // Integrated progress button (same component / look as Apply / Downgrade / Reset).
        var button = new MCBProgressButtonElement(
            () => StartApply(candidates, pathOf),
            () =>
            {
                bool running = isRunning || progress.IsRunning;
                int changes = CountPendingChanges(candidates, pathOf);
                return new MCBProgressButtonData
                {
                    text = running && !string.IsNullOrWhiteSpace(progress.StepText) ? progress.StepText : "Apply",
                    enabled = !running && changes > 0,
                    isRunning = running,
                    progress = running ? progress.Progress : 1f,
                    fillColor = progress.IsRunning ? progress.FillColor : (Color)ReFitGreen,
                    trackColor = TrackColor
                };
            });
        button.AddToClassList("mcb-button");
        button.AddToClassList("mcb-button--primary");
        button.style.marginTop = 10;
        button.style.height = 32;
        card.Add(button);

        // Only surface a message when something went wrong (no info bubble in the nominal case).
        if (lastFailed && !string.IsNullOrEmpty(lastMessage))
        {
            card.Add(AvatarOptionsModule.CreateOptionHelpBox(lastMessage, HelpBoxMessageType.Warning));
        }

        root.Add(card);
        return true;
    }

    private void BuildInstallPromptUIToolkit(VisualElement root)
    {
        var card = AvatarOptionsModule.CreateOptionCard("mcb-avatar-refit");
        card.Add(AvatarOptionsModule.CreateOptionTitle("ReFit"));

        var description = AvatarOptionsModule.CreateOptionLabel(
            "ReFit adapts clothing and accessory meshes made for the original base so they follow the applied custom base version.",
            12,
            FontStyle.Normal,
            new Color(0.82f, 0.82f, 0.82f));
        description.style.whiteSpace = WhiteSpace.Normal;
        card.Add(description);

        var status = VpmDependencyService.Instance.GetOptionalDependencyStatus(VpmDependencyService.ReFitPackageId);
        string apiMessage = MCBReFitIntegration.ReFitAvailabilityMessage;
        bool assumedInstalled = status != null && status.IsAssumedInstalled;
        if (assumedInstalled)
        {
            card.Add(AvatarOptionsModule.CreateOptionHelpBox(
                string.IsNullOrWhiteSpace(apiMessage)
                    ? "Advanced settings currently assume ReFit is installed, but the ReFit editor API was not found. Disable the optional integration bypass to add ReFit through VPM."
                    : apiMessage,
                HelpBoxMessageType.Warning));
        }
        else if (status != null && status.IsInstalled)
        {
            card.Add(AvatarOptionsModule.CreateOptionHelpBox(
                string.IsNullOrWhiteSpace(apiMessage)
                    ? "ReFit is listed as installed, but its editor API was not found. Wait for Unity to finish compiling, or check the Console for ReFit assembly errors."
                    : apiMessage,
                HelpBoxMessageType.Warning));
        }
        else if (status != null && !string.IsNullOrWhiteSpace(status.Reason))
        {
            var reason = AvatarOptionsModule.CreateOptionLabel(
                status.Reason,
                11,
                FontStyle.Normal,
                new Color(0.62f, 0.62f, 0.62f));
            reason.style.whiteSpace = WhiteSpace.Normal;
            reason.style.marginTop = 6;
            card.Add(reason);
        }

        string label = VpmDependencyService.Instance.IsInstalling ? "Adding ReFit..." : "Add ReFit";
        var installButton = AvatarOptionsModule.CreateOptionButton(label, () =>
        {
            var result = VpmDependencyService.Instance.InstallOptionalDependency(VpmDependencyService.ReFitPackageId);
            if (!result.Success)
            {
                EditorUtility.DisplayDialog("Add ReFit Failed", result.ErrorMessage, "Ok");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "ReFit Added",
                    "ReFit was added. Unity may reload assemblies before the ReFit avatar options become available.",
                    "Ok");
            }

            AvatarOptionsModule.RefreshEditorUi(editor);
        });
        installButton.AddToClassList("mcb-button--primary");
        installButton.style.height = 36;
        installButton.style.marginTop = 10;
        installButton.SetEnabled(status != null && !status.IsInstalled && !VpmDependencyService.Instance.IsInstalling);
        card.Add(installButton);

        root.Add(card);
    }

    /// <summary>Number of assets whose applied state differs from the current selection (drives Apply's enabled state).</summary>
    private int CountPendingChanges(List<SkinnedMeshRenderer> candidates, Dictionary<SkinnedMeshRenderer, string> pathOf)
    {
        var mcb = editor.customBaseTarget;
        int count = 0;
        foreach (var smr in candidates)
        {
            if (smr == null || !pathOf.TryGetValue(smr, out var path)) continue;
            bool selected = selectedPaths.Contains(path);
            bool applied = MCBReFitIntegration.IsRefitApplied(mcb, smr);
            if (selected != applied) count++;
        }
        return count;
    }

    private void StartApply(List<SkinnedMeshRenderer> candidates, Dictionary<SkinnedMeshRenderer, string> pathOf)
    {
        if (isRunning) return;
        var mcb = editor.customBaseTarget;

        var toRefit = new List<SkinnedMeshRenderer>();
        var toRestore = new List<string>();
        foreach (var smr in candidates)
        {
            if (smr == null || !pathOf.TryGetValue(smr, out var path)) continue;
            bool selected = selectedPaths.Contains(path);
            bool applied = MCBReFitIntegration.IsRefitApplied(mcb, smr);
            if (selected && !applied) toRefit.Add(smr);
            else if (!selected && applied) toRestore.Add(path);
        }

        // Restores are immediate; re-fits run in the background.
        foreach (var path in toRestore) MCBReFitIntegration.RestoreAsset(mcb, path);

        if (toRefit.Count == 0)
        {
            lastFailed = false;
            lastMessage = null;
            AvatarOptionsModule.RefreshEditorUi(editor);
            return;
        }

        isRunning = true;
        lastMessage = null;
        lastFailed = false;
        EditorCoroutineUtility.StartCoroutineOwnerless(RunCoroutine(toRefit));
    }

    private IEnumerator RunCoroutine(List<SkinnedMeshRenderer> targets)
    {
        yield return MCBReFitIntegration.RunReFitCoroutine(editor, targets, progress, (success, message) =>
        {
            lastFailed = !success;
            lastMessage = success ? null : message;
        });
        isRunning = false;
        AvatarOptionsModule.RefreshEditorUi(editor);
    }
}
#endif
