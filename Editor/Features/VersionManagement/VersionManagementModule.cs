#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

public class VersionManagementModule
{
    private readonly MCBEditor editor;
    public readonly VersionActions actions;
    public readonly FileConfigurationDrawer fileConfigDrawer;
    private readonly VersionListDrawer versionListDrawer;
    private readonly FileManagerService fileManagerService;
    
    private enum ActionType { INSTALL, UPDATE, DOWNGRADE, RESET, SWITCH_TO_CUSTOM, UNAVAILABLE }
    
    private bool versionsFoldout = true;
    private bool hasShownMissingVersionWarning;
    private CustomBaseVersion lastSelectionForWarning;
    private bool lastRecommendedWasNull;


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

    public void OnFBXChange()
    {
        ResetMissingVersionWarning();
        actions.UpdateCurrentBaseFbxHash();
        actions.StartVersionFetch();
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
        bool offlineImportedOnly = !editor.isAuthenticated && editor.importedVersions != null && editor.importedVersions.Count > 0;
        if (!offlineImportedOnly)
        {
            DrawFetchUpdatesButton();
        }
        
        // Special UI when user doesn't have access to the asset
        if (!string.IsNullOrEmpty(editor.accessDeniedAssetId))
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
        bool canInteract = !editor.isFetching && !editor.isDownloading && !editor.isDeleting;

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
                    if (!editor.isAuthenticated && availableVersions.Count > 0)
                    {
                        selectedVersion = availableVersions[0];
                        editor.selectedVersionForAction = selectedVersion;
                        lastSelectionForWarning = editor.selectedVersionForAction;
                    }
                    else
                    {
                        if (!hasShownMissingVersionWarning)
                        {
                            MCBLogger.LogWarning("[MCBEditor] No recommended version available. Please select a version from the list.");
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
        
        using (new EditorGUI.DisabledScope(!canInteract))
        {
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
            
            using (new EditorGUI.DisabledScope(buttonDisabled))
            {
                string buttonText = GetActionButtonText(action, selectedVersion);
                
                // Set button color based on action type
                if (action == ActionType.DOWNGRADE)
                {
                    GUI.backgroundColor = EditorUIUtils.OrangeColor;
                }
                else if (action == ActionType.SWITCH_TO_CUSTOM)
                {
                    GUI.backgroundColor = new Color(1.0f, 0.5f, 0.5f); // red tint
                }
                else if (action != ActionType.RESET) // Keep default color for reset, green for others
                {
                    GUI.backgroundColor = Color.green;
                }
                // For RESET, use default button color (no background color change)

                if (GUILayout.Button(buttonText, GUILayout.Height(40)))
                {
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
                        string binPath = MCBUtils.GetVersionBinPath(selectedVersion.version, selectedVersion.defaultAviVersion);
                        bool isDownloaded = !string.IsNullOrEmpty(binPath) && System.IO.File.Exists(binPath);
                        string assetName = editor.GetSelectedAssetDisplayName();
                        
                        if (EditorUtility.DisplayDialog("Confirm Transformation", $"This will modify your base FBX file using {assetName} version '{selectedVersion.version}'.\nA backup will be created.", "Proceed", "Cancel"))
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
                GUI.backgroundColor = Color.white;
            }

            // Note: Reset button has been removed - reset functionality is now handled through the version list
        }
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
        
        string binPath = MCBUtils.GetVersionBinPath(selectedVersion.version, selectedVersion.defaultAviVersion);
        bool isDownloaded = !string.IsNullOrEmpty(binPath) && System.IO.File.Exists(binPath);
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


