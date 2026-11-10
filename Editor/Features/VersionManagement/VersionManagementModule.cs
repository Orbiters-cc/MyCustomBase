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
}
#endif


