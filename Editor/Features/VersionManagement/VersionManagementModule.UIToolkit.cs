#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public partial class VersionManagementModule
{
    private readonly MCBEditor editor;
    public readonly VersionActions actions;
    public readonly FileConfigurationDrawer fileConfigDrawer;
    private readonly VersionListDrawer versionListDrawer;
    private readonly FileManagerService fileManagerService;
    
    private enum ActionType { INSTALL, UPDATE, DOWNGRADE, RESET, SWITCH_TO_CUSTOM, UNAVAILABLE }
    
    private bool hasShownMissingVersionWarning;
    private CustomBaseVersion lastSelectionForWarning;
    private bool lastRecommendedWasNull;
    private static readonly Color AccentGreen = new Color32(0, 218, 109, 255);
    private static readonly Color DisabledAccentGreen = new Color32(0, 120, 60, 255);

    public VersionManagementModule(MCBEditor editor, NetworkService network, FileManagerService files)
    {
        this.editor = editor;
        actions = new VersionActions(editor, network, files);
        fileManagerService = files;
        fileConfigDrawer = new FileConfigurationDrawer(editor, actions);
        versionListDrawer = new VersionListDrawer(editor, actions);
    }

    public void OnEnable()
    {
        fileConfigDrawer.OnEnable();
        ResetMissingVersionWarning();
    }

    public void ResetMissingVersionWarning()
    {
        hasShownMissingVersionWarning = false;
        lastSelectionForWarning = editor.selectedVersionForAction;
        lastRecommendedWasNull = editor.recommendedVersion == null;
    }

    private ActionType GetActionType()
    {
        // Custom switch has priority when selected and feature enabled
        if (FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) && editor.selectedCustomVersionForAction != null)
            return ActionType.SWITCH_TO_CUSTOM;

        if (editor.selectedVersionForAction == null) return ActionType.UNAVAILABLE;
        
        if (editor.selectedVersionForAction == VersionListDrawer.RESET_VERSION) return ActionType.RESET;
        if (IsDiscordRoleBlocked(editor.selectedVersionForAction)) return ActionType.UNAVAILABLE;

        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;

        if (actions.IsVersionCurrentlyApplied(editor.selectedVersionForAction))
        {
            return ActionType.UNAVAILABLE;
        }

        if (!actions.HasAppliedCustomBaseEvidence())
        {
            if (appliedVersion != null)
            {
                var compareApplied = editor.CompareVersions(editor.selectedVersionForAction.version, appliedVersion.version);
                if (compareApplied > 0) return ActionType.UPDATE;
                if (compareApplied < 0) return ActionType.DOWNGRADE;
            }
            return ActionType.INSTALL;
        }
        
        var compare = editor.CompareVersions(editor.selectedVersionForAction.version, appliedVersion?.version ?? "0.0.0");
        if (compare > 0) return ActionType.UPDATE;
        if (compare < 0) return ActionType.DOWNGRADE;
        return ActionType.UNAVAILABLE;
    }

    // The server only lists the role requirement; a version already on disk can still be applied.
    private static bool IsDiscordRoleBlocked(CustomBaseVersion version) =>
        VersionProtection.IsDiscordRoleDenied(version) && !version.isUnsubmitted && !MCBUtils.IsVersionDownloaded(version);

    private static bool ShouldSkipApplyConfirmation(CustomBaseVersion version)
    {
        return NativeMeshPayloadService.VersionUsesAdvancedMesh(version);
    }

    private bool ShouldResetWithoutConfirmation()
    {
        bool canSkipConfirmation = actions.CanResetToDefaultBaseWithoutFbxImport();
        if (canSkipConfirmation)
        {
            MCBLogger.Log("[VersionManagement] Skipping reset confirmation for fast advanced mesh default-base swap.");
        }

        return canSkipConfirmation;
    }

    private static Color GetActionProgressTrackColor()
    {
        return new Color(0.46f, 0.46f, 0.46f, 1f);
    }

    private bool IsInstalledActionState(ActionType action, CustomBaseVersion selectedVersion)
    {
        return action == ActionType.UNAVAILABLE &&
               selectedVersion != null &&
               actions.IsVersionCurrentlyApplied(selectedVersion);
    }

    private static Color GetActionButtonColor(ActionType action, bool isInstalledAction)
    {
        if (isInstalledAction)
        {
            return DisabledAccentGreen;
        }

        switch (action)
        {
            case ActionType.DOWNGRADE:
                return new Color(0.780f, 0.455f, 0.173f, 1f);
            case ActionType.SWITCH_TO_CUSTOM:
                return new Color(0.725f, 0.329f, 0.329f, 1f);
            case ActionType.RESET:
            case ActionType.UNAVAILABLE:
                return new Color(0.349f, 0.349f, 0.349f, 1f);
            default:
                return AccentGreen;
        }
    }

    private string GetActionButtonText(ActionType action, CustomBaseVersion selectedVersion)
    {
        string assetName = editor.GetSelectedAssetDisplayName();
        if (action == ActionType.SWITCH_TO_CUSTOM)
        {
            var cv = editor.selectedCustomVersionForAction;
            return cv != null ? $"Turn into Custom {cv.detectionDate}" : "Turn into Custom";
        }
        
        if (selectedVersion == null) return "Select a Version";
        
        if (action == ActionType.RESET) return "Reset to Original Avatar";
        if (IsDiscordRoleBlocked(selectedVersion))
        {
            var roles = selectedVersion.protection.discordRoles;
            return roles != null && roles.Length > 0 ? "Requires the " + string.Join(" or ", roles) + " Discord role" : "Requires a Discord role";
        }
        
        bool isDownloaded = MCBUtils.IsVersionDownloaded(selectedVersion);
        string downloadPrefix = isDownloaded || selectedVersion.isUnsubmitted ? "" : "Download and ";
        
        return action switch
        {
            ActionType.INSTALL => $"{downloadPrefix}Apply {assetName}",
            ActionType.UPDATE => $"{downloadPrefix}Update {assetName} to v{selectedVersion.version}",
            ActionType.DOWNGRADE => $"{downloadPrefix}Downgrade {assetName} to v{selectedVersion.version}",
            _ => $"Installed (v{editor.customBaseTarget.appliedCustomBaseVersion?.version})"
        };
    }

    private VisualElement versionRoot;

    // The section is rebuilt when a fetch, download, delete or switch starts or ends (the avatar's view model announces
    // it, MCBEditor.OnViewModelChanged), instead of every half second while one runs.
    public void AttachUIToolkit(VisualElement root)
    {
        versionRoot = root;
    }

    public void DetachUIToolkit()
    {
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

        bool canReset = fileManagerService.BackupExists(actions.GetCurrentFBXPath()) || actions.HasAppliedCustomBaseEvidence();
        bool buttonDisabled;
        if (action == ActionType.SWITCH_TO_CUSTOM)
        {
            buttonDisabled = editor.selectedCustomVersionForAction == null;
        }
        else
        {
            buttonDisabled = !selectionIsValid ||
                             (!isResetSelected && actions.IsVersionCurrentlyApplied(selectedVersion)) ||
                             IsDiscordRoleBlocked(selectedVersion) ||
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

        RequestApply(selectedVersion);
    }

    /// <summary>
    /// Applies a version after the usual confirmation, downloading it first when needed. Returns false when nothing
    /// started (declined, or another download or apply is running).
    /// </summary>
    public bool RequestApply(CustomBaseVersion version)
    {
        if (version == null || editor.isDownloading || editor.isApplying)
        {
            return false;
        }

        bool isDownloaded = MCBUtils.IsVersionDownloaded(version);
        string assetName = editor.GetSelectedAssetDisplayName();

        bool shouldApply = ShouldSkipApplyConfirmation(version) ||
                           EditorUtility.DisplayDialog("Confirm Transformation", $"This will modify your base FBX file using {assetName} version '{version.version}'.\nA backup will be created.", "Proceed", "Cancel");
        if (!shouldApply)
        {
            return false;
        }

        editor.selectedVersionForAction = version;
        editor.selectedCustomVersionForAction = null;
        if (isDownloaded)
        {
            actions.StartApplyVersion();
        }
        else
        {
            actions.StartVersionDownload(version, true);
        }

        editor.RefreshUiToolkitSections();
        editor.Repaint();
        return true;
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
