#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

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
    private float imguiDisplayedActionProgress = 1f;
    private static readonly Color AccentGreen = new Color32(0, 218, 109, 255);
    private Color imguiDisplayedFillColor = AccentGreen;
    private Color imguiDisplayedTrackColor = new Color(0.46f, 0.46f, 0.46f, 1f);
    private bool imguiWasApplying;
    private double imguiLastProgressUpdateTime;


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

    public void Draw()
    {
        // TODO fileConfigDrawer.Draw();
        if (editor.HasServerAccess)
        {
            DrawFetchUpdatesButton();
        }
        
        // Special UI when user doesn't have access to the asset
        if (editor.HasServerAccess && !string.IsNullOrEmpty(editor.accessDeniedAssetId))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("You currently don't have access to the MCB. Click the button to get to the asset page with instructions to get it:", MessageType.Warning);
            EditorGUILayout.Space();
            if (GUILayout.Button("Get the MCB", GUILayout.Height(30)))
            {
                string url = MCBUtils.getWebsiteUrl() + "assets/" + editor.accessDeniedAssetId;
                Application.OpenURL(url);
            }

            if (!editor.GetAllVersions().Any())
            {
                return; // No local versions to show, keep the simplified access-denied UI.
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Local saved versions are still available below.", MessageType.Info);
        }
        
        versionListDrawer.Draw();
        
        EditorGUILayout.Space();
        versionListDrawer.DrawUpdateNotification();
        DrawActionButtons();
    }

    private void DrawFetchUpdatesButton()
    {
        bool noAssetSelected = editor.isAuthenticated && editor.GetSelectedAsset() == null;
        using (new EditorGUI.DisabledScope(editor.isFetching || editor.isDownloading || editor.isDeleting || noAssetSelected))
        {
            if (GUILayout.Button(editor.isFetching ? "Fetching..." : "Check for Updates"))
            {
                ResetMissingVersionWarning();
                actions.StartVersionFetch();
            }
        }
    }

    private void DrawActionButtons()
    {
        bool canInteract = !editor.isFetching && !editor.isDownloading && !editor.isDeleting && !editor.isApplying;

        // If feature disabled, ensure no custom selection is active
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

        // If no version is selected, select the recommended version by default
        // BUT if a custom version is selected (and feature enabled), do not auto-select recommended
        if (selectedVersion == null)
        {
            if (FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) && editor.selectedCustomVersionForAction != null)
            {
                // Keep null so the main action uses SWITCH_TO_CUSTOM
            }
            else
            {
                if (editor.recommendedVersion == null)
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
        }
        
        bool selectionIsValid = selectedVersion != null &&
                                (selectedVersion == VersionListDrawer.RESET_VERSION || availableVersions.Exists(v => v.Equals(selectedVersion)));
        bool isResetSelected = selectedVersion == VersionListDrawer.RESET_VERSION;
        
        var action = GetActionType();

        // Main Apply/Update/Downgrade/Reset/SWITCH_TO_CUSTOM Button
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

        string buttonText = GetActionButtonText(action, selectedVersion);
        Color buttonColor = GetActionButtonColor(action);
        bool actionInProgress = editor.isApplying || actions.ApplyProgress.IsRunning;
        bool buttonEnabled = canInteract && !buttonDisabled;
        if (DrawMainActionProgressButton(buttonText, buttonColor, buttonEnabled, actionInProgress))
        {
            actions.ConfigureApplyProgressColor(buttonColor);
            if (action == ActionType.RESET)
            {
                if (EditorUtility.DisplayDialog("Confirm Reset", "This will restore the original FBX from its backup and reapply the default avatar configuration.", "Reset", "Cancel"))
                {
                    actions.StartReset();
                }
            }
            else if (action == ActionType.SWITCH_TO_CUSTOM)
            {
                var cv = editor.selectedCustomVersionForAction;
                if (cv != null && EditorUtility.DisplayDialog("Apply Custom Version", $"This will replace your current FBX with your saved custom version from {cv.detectionDate}.", "Proceed", "Cancel"))
                {
                    actions.StartApplyCustomVersion();
                }
            }
            else
            {
                bool isDownloaded = MCBUtils.IsVersionDownloaded(selectedVersion);
                string assetName = editor.GetSelectedAssetDisplayName();

                bool shouldApply = ShouldSkipApplyConfirmation(selectedVersion) ||
                                   EditorUtility.DisplayDialog("Confirm Transformation", $"This will modify your base FBX file using {assetName} version '{selectedVersion.version}'.\nA backup will be created.", "Proceed", "Cancel");
                if (shouldApply)
                {
                    if (isDownloaded)
                    {
                        actions.StartApplyVersion();
                    }
                    else
                    {
                        actions.StartVersionDownload(selectedVersion, true);
                    }
                }
            }
        }

        // Note: Reset button has been removed - reset functionality is now handled through the version list
    }

    private ActionType GetActionType()
    {
        // Custom switch has priority when selected and feature enabled
        if (FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) && editor.selectedCustomVersionForAction != null)
            return ActionType.SWITCH_TO_CUSTOM;

        if (editor.selectedVersionForAction == null) return ActionType.UNAVAILABLE;
        
        if (editor.selectedVersionForAction == VersionListDrawer.RESET_VERSION) return ActionType.RESET;

        var appliedVersion = editor.customBaseTarget.appliedCustomBaseVersion;

        if (!editor.isCustomBase)
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

    private static bool ShouldSkipApplyConfirmation(CustomBaseVersion version)
    {
        return NativeMeshPayloadService.VersionUsesAdvancedMesh(version);
    }

    private bool DrawMainActionProgressButton(string buttonText, Color fillColor, bool enabled, bool actionInProgress)
    {
        var progress = actions.ApplyProgress;
        Rect rect = GUILayoutUtility.GetRect(0f, 40f, GUILayout.ExpandWidth(true), GUILayout.Height(40f));
        string displayText = actionInProgress && progress.IsRunning && !string.IsNullOrWhiteSpace(progress.StepText)
            ? progress.StepText
            : buttonText;
        float targetProgress = actionInProgress ? progress.Progress : 1f;
        Color targetTrackColor = actionInProgress ? GetActionProgressTrackColor() : fillColor;

        double now = EditorApplication.timeSinceStartup;
        float deltaTime = imguiLastProgressUpdateTime > 0d
            ? Mathf.Clamp((float)(now - imguiLastProgressUpdateTime), 0f, 0.1f)
            : 0.016f;
        imguiLastProgressUpdateTime = now;

        if (actionInProgress && !imguiWasApplying)
        {
            imguiDisplayedActionProgress = 0f;
            imguiDisplayedTrackColor = targetTrackColor;
        }

        float progressSmoothing = 1f - Mathf.Exp(-deltaTime * 10f);
        float colorSmoothing = 1f - Mathf.Exp(-deltaTime * 7f);
        imguiDisplayedActionProgress = Mathf.Lerp(imguiDisplayedActionProgress, Mathf.Clamp01(targetProgress), progressSmoothing);
        imguiDisplayedFillColor = Color.Lerp(imguiDisplayedFillColor, fillColor, colorSmoothing);
        imguiDisplayedTrackColor = Color.Lerp(imguiDisplayedTrackColor, targetTrackColor, colorSmoothing);
        imguiWasApplying = actionInProgress;

        if (Event.current.type == EventType.Repaint)
        {
            EditorGUI.DrawRect(rect, imguiDisplayedTrackColor);
            var fillRect = new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(imguiDisplayedActionProgress), rect.height);
            if (fillRect.width > 0.5f)
            {
                EditorGUI.DrawRect(fillRect, imguiDisplayedFillColor);
            }

            var textStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = enabled || actionInProgress ? Color.white : new Color(1f, 1f, 1f, 0.55f) },
                wordWrap = true,
                clipping = TextClipping.Clip
            };
            GUI.Label(rect, displayText, textStyle);
        }

        if (actionInProgress)
        {
            editor.Repaint();
            return false;
        }

        if (!enabled)
        {
            return false;
        }

        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        if (Event.current.type == EventType.MouseDown &&
            Event.current.button == 0 &&
            rect.Contains(Event.current.mousePosition))
        {
            Event.current.Use();
            return true;
        }

        return false;
    }

    private static Color GetActionProgressTrackColor()
    {
        return new Color(0.46f, 0.46f, 0.46f, 1f);
    }

    private static Color GetActionButtonColor(ActionType action)
    {
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
        
        bool isDownloaded = MCBUtils.IsVersionDownloaded(selectedVersion);
        string downloadPrefix = isDownloaded ? "" : "Download and ";
        
        return action switch
        {
            ActionType.INSTALL => $"{downloadPrefix}Apply {assetName}",
            ActionType.UPDATE => $"{downloadPrefix}Update {assetName} to v{selectedVersion.version}",
            ActionType.DOWNGRADE => $"{downloadPrefix}Downgrade {assetName} to v{selectedVersion.version}",
            _ => $"Installed (v{editor.customBaseTarget.appliedCustomBaseVersion?.version})"
        };
    }
}
#endif


