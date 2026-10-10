#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Gallery notice for avatars built from a release a creator distributed before MCB: one press migrates the avatar
/// (LegacyMigrationService) so that the custom base's MCB versions are offered for it.
/// </summary>
public partial class AssetGalleryModule
{
    // Per avatar and model files: every inspector of the avatar shares one lookup.
    private static readonly Dictionary<string, List<LegacyMigrationService.Match>> LegacyLookups = new Dictionary<string, List<LegacyMigrationService.Match>>();
    private string legacySignature;
    private bool legacyLoading, legacyMigrating;
    private List<LegacyMigrationService.Match> legacyMatches = new List<LegacyMigrationService.Match>();
    private string legacyError;

    private void BuildLegacyMigrationUIToolkit(VisualElement root)
    {
        var owner = editor.customBaseTarget;
        if (owner == null || owner.appliedCustomBaseAssetId > 0) return;
        var avatar = AvatarPaths.Root(owner);
        string signature = avatar.GetInstanceID() + "|" + string.Join("|", LegacyMigrationService.ModelPaths(avatar));
        if (signature != legacySignature && !legacyLoading)
        {
            legacySignature = signature;
            legacyError = null;
            if (LegacyLookups.TryGetValue(signature, out var known)) legacyMatches = known;
            else { legacyMatches = new List<LegacyMigrationService.Match>(); DetectLegacy(avatar, signature); }
        }
        foreach (var release in legacyMatches.GroupBy(m => (m.assetId, m.legacyRelease.label))) root.Add(CreateLegacyNotice(avatar, release.ToList()));
    }

    private async void DetectLegacy(Transform avatar, string signature)
    {
        legacyLoading = true;
        try { legacyMatches = await LegacyMigrationService.FindAsync(avatar, editor.authToken); LegacyLookups[signature] = legacyMatches; }
        catch (Exception ex) { MCBLogger.Log("[MCB] Could not check for a release made before MCB: " + ex.Message); }
        finally
        {
            legacyLoading = false;
            if (legacyMatches.Count > 0) editor.RefreshUiToolkitSections();
        }
    }

    private VisualElement CreateLegacyNotice(Transform avatar, List<LegacyMigrationService.Match> release)
    {
        var match = release[0];
        var notice = new VisualElement();
        notice.AddToClassList("mcb-legacy-notice");
        var title = CreateLabel(match.legacyRelease.label + " detected", 12, FontStyle.Bold, new Color(0.88f, 0.88f, 0.88f));
        title.AddToClassList("mcb-legacy-notice__title");
        notice.Add(title);
        var text = CreateLabel("This avatar was made with " + match.legacyRelease.label + ", released before MCB. Migrate it to receive the "
            + match.assetName + " versions: its body goes back to the original base model, and the old setup that the versions replace "
            + "is removed (your FX controller, parameters and menus are copied first). Undo restores everything.", 11, FontStyle.Normal, new Color(0.72f, 0.72f, 0.72f));
        text.AddToClassList("mcb-legacy-notice__text");
        notice.Add(text);
        var row = CreateRow();
        row.AddToClassList("mcb-legacy-notice__actions");
        var status = CreateLabel(legacyError ?? "", 11, FontStyle.Normal, new Color(1f, 0.55f, 0.35f));
        status.AddToClassList("mcb-legacy-notice__status");
        Button button = null;
        button = CreateToolbarButton(legacyMigrating ? "Migrating…" : "Migrate avatar", () =>
        {
            if (legacyMigrating) return;
            legacyMigrating = true;
            button.text = "Migrating…";
            button.SetEnabled(false);
            status.text = "";
            // Let the pressed state paint before the synchronous migration runs.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                try
                {
                    LegacyMigrationService.Migrate(avatar, release);
                    legacyError = null;
                    legacyMatches.RemoveAll(release.Contains);
                    LegacyLookups.Clear();
                    legacySignature = null;
                    editor.InvalidateDetectedAvatarFbxCache();
                    StartDiscovery(filterOnlyCompatible: !showNonMatchingAssets);
                }
                catch (Exception ex)
                {
                    legacyError = ex.Message;
                    MCBLogger.LogWarning("[MCB] Migration failed: " + ex.Message);
                }
                finally
                {
                    legacyMigrating = false;
                    editor.RefreshUiToolkitSections();
                }
            };
        });
        button.SetEnabled(!legacyMigrating);
        row.Add(button);
        row.Add(status);
        notice.Add(row);
        return notice;
    }
}
#endif
