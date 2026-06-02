#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public partial class VersionManagementModule
{
    private VisualElement versionRoot;
    private IVisualElementScheduledItem versionRefreshSchedule;

    public void AttachUIToolkit(VisualElement root)
    {
        versionRoot = root;
        RefreshUIToolkit();

        versionRefreshSchedule?.Pause();
        versionRefreshSchedule = versionRoot?.schedule.Execute(() =>
        {
            bool downloadOnlyRefresh = editor.isDownloading && !editor.isApplying && !actions.ApplyProgress.IsRunning;
            if (editor.isFetching || editor.isDeleting || downloadOnlyRefresh)
            {
                RefreshUIToolkit();
            }
        }).Every(500);
    }

    public void DetachUIToolkit()
    {
        versionRefreshSchedule?.Pause();
        versionRefreshSchedule = null;
        versionRoot = null;
    }

    public void RefreshUIToolkit()
    {
        if (versionRoot == null)
        {
            return;
        }

        versionRoot.Clear();
        versionRoot.AddToClassList("mcb-version");
        versionRoot.EnableInClassList("mcb-version--asset-view", editor.GetSelectedAsset() != null);

        if (!ShouldShowVersionUIToolkit())
        {
            versionRoot.style.display = DisplayStyle.None;
            return;
        }

        versionRoot.style.display = DisplayStyle.Flex;
        editor.serializedObject.Update();
        try
        {
            BuildUIToolkit(versionRoot);
        }
        finally
        {
            editor.serializedObject.ApplyModifiedProperties();
        }
    }

    private bool ShouldShowVersionUIToolkit()
    {
        bool hasMajorUpdateLockout = MCBPackageVersionService.RequiresMajorUpdate;
        bool showOfflineSavedVersionsUi = !editor.HasServerAccess &&
                                          editor.importedVersions != null &&
                                          editor.importedVersions.Count > 0;

        if (hasMajorUpdateLockout)
        {
            return showOfflineSavedVersionsUi;
        }

        if (editor.HasServerAccess)
        {
            return !editor.ShouldShowGalleryOnly();
        }

        return showOfflineSavedVersionsUi;
    }

    private void BuildUIToolkit(VisualElement root)
    {
        if (editor.HasServerAccess && editor.GetSelectedAsset() == null)
        {
            BuildFetchUpdatesButtonUIToolkit(root);
        }

        if (editor.HasServerAccess && !string.IsNullOrEmpty(editor.accessDeniedAssetId))
        {
            root.Add(CreateVersionHelpBox(
                "You currently don't have access to the MCB. Click the button to get to the asset page with instructions to get it:",
                HelpBoxMessageType.Warning));

            var getButton = CreateVersionTextButton("Get the MCB", () =>
            {
                string url = MCBUtils.getWebsiteUrl() + "assets/" + editor.accessDeniedAssetId;
                Application.OpenURL(url);
            });
            getButton.AddToClassList("mcb-version-access__button");
            root.Add(getButton);

            if (!editor.GetAllVersions().Any())
            {
                return;
            }

            root.Add(CreateVersionHelpBox("Local saved versions are still available below.", HelpBoxMessageType.Info));
        }

        versionListDrawer.BuildUIToolkit(root);
        versionListDrawer.BuildUpdateNotificationUIToolkit(root);
        BuildActionButtonsUIToolkit(root);
    }

    private void BuildFetchUpdatesButtonUIToolkit(VisualElement root)
    {
        bool noAssetSelected = editor.isAuthenticated && editor.GetSelectedAsset() == null;
        var button = CreateVersionTextButton(editor.isFetching ? "Fetching..." : "Check for Updates", () =>
        {
            ResetMissingVersionWarning();
            actions.StartVersionFetch();
            editor.RefreshUiToolkitSections();
            editor.Repaint();
        });
        button.AddToClassList("mcb-version-fetch");
        button.SetEnabled(!editor.isFetching && !editor.isDownloading && !editor.isDeleting && !noAssetSelected);
        root.Add(button);
    }

    private void BuildActionButtonsUIToolkit(VisualElement root)
    {
        bool canInteract = !editor.isFetching && !editor.isDownloading && !editor.isDeleting && !editor.isApplying;

        if (!FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION))
        {
            editor.selectedCustomVersionForAction = null;
        }

        var selectedVersion = editor.selectedVersionForAction;
        var availableVersions = editor.GetAllVersions();
        bool hasSelectedReset = selectedVersion == VersionListDrawer.RESET_VERSION;
        bool hasSelectedAvailableVersion = selectedVersion != null && availableVersions.Exists(v => v.Equals(selectedVersion));
        if (selectedVersion != null && !hasSelectedReset && !hasSelectedAvailableVersion)
        {
            selectedVersion = null;
            editor.selectedVersionForAction = null;
        }

        bool recommendedIsNull = editor.recommendedVersion == null;
        if (editor.selectedVersionForAction != lastSelectionForWarning || recommendedIsNull != lastRecommendedWasNull)
        {
            hasShownMissingVersionWarning = false;
            lastSelectionForWarning = editor.selectedVersionForAction;
            lastRecommendedWasNull = recommendedIsNull;
        }

        if (selectedVersion == null)
        {
            if (FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) && editor.selectedCustomVersionForAction != null)
            {
                // The action resolves as SWITCH_TO_CUSTOM below.
            }
            else if (editor.recommendedVersion == null)
            {
                if (!editor.HasServerAccess && availableVersions.Count > 0)
                {
                    selectedVersion = availableVersions[0];
                    editor.selectedVersionForAction = selectedVersion;
                    lastSelectionForWarning = editor.selectedVersionForAction;
                }
                else
                {
                    if (!hasShownMissingVersionWarning)
                    {
                        MCBLogger.Log("[MCBEditor] No recommended version available. Please select a version from the list.");
                        hasShownMissingVersionWarning = true;
                    }

                    return;
                }
            }
            else
            {
                selectedVersion = editor.recommendedVersion;
                editor.selectedVersionForAction = selectedVersion;
                lastSelectionForWarning = editor.selectedVersionForAction;
            }
        }

        bool selectionIsValid = selectedVersion != null &&
                                (selectedVersion == VersionListDrawer.RESET_VERSION || availableVersions.Exists(v => v.Equals(selectedVersion)));
        bool isResetSelected = selectedVersion == VersionListDrawer.RESET_VERSION;
        var action = GetActionType();

        bool canReset = fileManagerService.BackupExists(actions.GetCurrentFBXPath()) || editor.isCustomBase;
        bool buttonDisabled;
        if (action == ActionType.SWITCH_TO_CUSTOM)
        {
            buttonDisabled = editor.selectedCustomVersionForAction == null;
        }
        else
        {
            buttonDisabled = !selectionIsValid ||
                             (!isResetSelected && selectedVersion.Equals(editor.customBaseTarget.appliedCustomBaseVersion)) ||
                             (isResetSelected && !canReset);
        }

        bool isInstalledAction = IsInstalledActionState(action, selectedVersion);
        string buttonText = GetActionButtonText(action, selectedVersion);
        Color buttonColor = GetActionButtonColor(action, isInstalledAction);
        bool buttonEnabled = canInteract && !buttonDisabled;
        var button = new MCBProgressButtonElement(() =>
        {
            actions.ConfigureApplyProgressColor(buttonColor);
            HandleMainActionButton(action, selectedVersion);
        }, () =>
        {
            var progress = actions.ApplyProgress;
            bool isRunning = editor.isApplying || progress.IsRunning;
            return new MCBProgressButtonData
            {
                text = isRunning && progress.IsRunning && !string.IsNullOrWhiteSpace(progress.StepText)
                    ? progress.StepText
                    : buttonText,
                enabled = buttonEnabled && !isRunning,
                isRunning = isRunning,
                progress = isRunning ? progress.Progress : 1f,
                fillColor = progress.IsRunning ? progress.FillColor : buttonColor,
                trackColor = GetActionProgressTrackColor()
            };
        });
        button.AddToClassList("mcb-button");
        button.AddToClassList("mcb-version-main-action");
        button.EnableInClassList("mcb-button--primary", action != ActionType.RESET && action != ActionType.DOWNGRADE && action != ActionType.SWITCH_TO_CUSTOM);
        button.EnableInClassList("mcb-version-main-action--downgrade", action == ActionType.DOWNGRADE);
        button.EnableInClassList("mcb-version-main-action--custom", action == ActionType.SWITCH_TO_CUSTOM);
        button.EnableInClassList("mcb-version-main-action--reset", action == ActionType.RESET);
        button.EnableInClassList("mcb-version-main-action--installed", isInstalledAction);
        button.SetEnabled(buttonEnabled);
        root.Add(button);
    }

    private void HandleMainActionButton(ActionType action, CustomBaseVersion selectedVersion)
    {
        if (action == ActionType.RESET)
        {
            if (ShouldResetWithoutConfirmation() || EditorUtility.DisplayDialog("Confirm Reset", "This will restore the original FBX from its backup and reapply the default avatar configuration.", "Reset", "Cancel"))
            {
                actions.StartReset();
                editor.RefreshUiToolkitSections();
                editor.Repaint();
            }

            return;
        }

        if (action == ActionType.SWITCH_TO_CUSTOM)
        {
            var cv = editor.selectedCustomVersionForAction;
            if (cv != null && EditorUtility.DisplayDialog("Apply Custom Version", $"This will replace your current FBX with your saved custom version from {cv.detectionDate}.", "Proceed", "Cancel"))
            {
                actions.StartApplyCustomVersion();
                editor.RefreshUiToolkitSections();
                editor.Repaint();
            }

            return;
        }

        if (selectedVersion == null)
        {
            return;
        }

        bool isDownloaded = MCBUtils.IsVersionDownloaded(selectedVersion);
        string assetName = editor.GetSelectedAssetDisplayName();

        bool shouldApply = ShouldSkipApplyConfirmation(selectedVersion) ||
                           EditorUtility.DisplayDialog("Confirm Transformation", $"This will modify your base FBX file using {assetName} version '{selectedVersion.version}'.\nA backup will be created.", "Proceed", "Cancel");
        if (!shouldApply)
        {
            return;
        }

        if (isDownloaded)
        {
            actions.StartApplyVersion();
        }
        else
        {
            actions.StartVersionDownload(selectedVersion, true);
        }

        editor.RefreshUiToolkitSections();
        editor.Repaint();
    }

    private static Button CreateVersionTextButton(string text, System.Action onClick)
    {
        var button = new Button(onClick) { text = text ?? string.Empty };
        button.AddToClassList("mcb-button");
        return button;
    }

    private static VisualElement CreateVersionHelpBox(string message, HelpBoxMessageType messageType)
    {
        var box = new VisualElement();
        box.AddToClassList("mcb-version-helpbox");
        switch (messageType)
        {
            case HelpBoxMessageType.Warning:
                box.AddToClassList("mcb-version-helpbox--warning");
                break;
            case HelpBoxMessageType.Error:
                box.AddToClassList("mcb-version-helpbox--error");
                break;
            case HelpBoxMessageType.Info:
                box.AddToClassList("mcb-version-helpbox--info");
                break;
            default:
                box.AddToClassList("mcb-version-helpbox--none");
                break;
        }

        var icon = new Label(GetVersionHelpBoxIcon(messageType));
        icon.AddToClassList("mcb-version-helpbox__icon");
        box.Add(icon);

        var label = new Label(message ?? string.Empty);
        label.AddToClassList("mcb-version-helpbox__text");
        box.Add(label);
        return box;
    }

    private static string GetVersionHelpBoxIcon(HelpBoxMessageType messageType)
    {
        switch (messageType)
        {
            case HelpBoxMessageType.Warning:
                return "!";
            case HelpBoxMessageType.Error:
                return "x";
            case HelpBoxMessageType.Info:
                return "i";
            default:
                return string.Empty;
        }
    }
}
#endif
