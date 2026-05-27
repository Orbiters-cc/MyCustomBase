#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public partial class VersionListDrawer
{
    public void BuildUIToolkit(VisualElement root)
    {
        if (root == null)
        {
            return;
        }

        root.Clear();
        root.AddToClassList("mcb-version-list");

        if (FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) &&
            editor.currentIsCustom &&
            !editor.customWarningShown)
        {
            root.Add(CreateHelpBox(
                "The current custom version of your avatar base is not supported. If you update it to one of the custom base versions, you may lose some custom features or blendshapes of your custom base.",
                HelpBoxMessageType.Warning));
            editor.customWarningShown = true;
        }

        var allVersions = editor.GetAllVersions() ?? new List<CustomBaseVersion>();
        bool showCreateVersionItem = ShouldShowCreateNewVersionItem();
        if (showCreateVersionItem || allVersions.Any() || editor.isFetching)
        {
            if (editor.isFetching)
            {
                var fetching = CreateLabel("Fetching version list...", 12, FontStyle.Normal, new Color(0.7f, 0.7f, 0.7f));
                fetching.AddToClassList("mcb-version-list__fetching");
                root.Add(fetching);
                return;
            }

            UserService.PreloadUserInfo(allVersions);

            bool hasPreviousTimelineItem = false;
            if (showCreateVersionItem)
            {
                root.Add(CreateCreateNewVersionItemUIToolkit(true));
                hasPreviousTimelineItem = true;
            }

            if (allVersions.Count > 0)
            {
                root.Add(CreateVersionListItemUIToolkit(allVersions[0], !hasPreviousTimelineItem, false));
                hasPreviousTimelineItem = true;

                if (allVersions.Count > 1)
                {
                    root.Add(CreateCollapseExpandButtonUIToolkit());
                }

                if (!isListCollapsed && allVersions.Count > 1)
                {
                    for (int i = 1; i < allVersions.Count; i++)
                    {
                        root.Add(CreateVersionListItemUIToolkit(allVersions[i], false, false));
                        hasPreviousTimelineItem = true;
                    }
                }
            }

            bool hasCustomRows = FeatureFlags.IsEnabled(FeatureFlags.SUPPORT_USER_UNKNOWN_VERSION) &&
                                 editor.userCustomVersions != null &&
                                 editor.userCustomVersions.Count > 0;
            if (hasCustomRows)
            {
                BuildUserCustomRowsUIToolkit(root, hasPreviousTimelineItem);
                hasPreviousTimelineItem = true;
            }

            bool resetIsFirst = !hasPreviousTimelineItem;
            root.Add(CreateResetVersionItemUIToolkit(resetIsFirst));
            return;
        }

        if (string.IsNullOrEmpty(editor.currentBaseFbxHash))
        {
            root.Add(CreateHelpBox("Assign or detect a Base FBX first.", HelpBoxMessageType.Info));
        }
        else if (editor.fetchAttempted)
        {
            root.Add(CreateHelpBox("No compatible versions found for the detected Base FBX hash.", HelpBoxMessageType.Warning));
        }
        else
        {
            root.Add(CreateHelpBox("Click 'Check for Updates' to find compatible versions.", HelpBoxMessageType.Info));
        }
    }

    public void BuildUpdateNotificationUIToolkit(VisualElement root)
    {
        if (root == null)
        {
            return;
        }

        var recommended = editor.recommendedVersion;
        var applied = editor.customBaseTarget.appliedCustomBaseVersion;
        if (recommended == null || applied == null || editor.CompareVersions(recommended.version, applied.version) <= 0)
        {
            return;
        }

        var panel = new VisualElement();
        panel.AddToClassList("mcb-version-update");
        var title = CreateLabel($"New version released: v{recommended.version}!", 13, FontStyle.Bold, new Color(0.35f, 1f, 0.25f));
        title.AddToClassList("mcb-version-update__title");
        panel.Add(title);

        if (!string.IsNullOrEmpty(recommended.changelog))
        {
            var changelog = CreateLabel(recommended.changelog, 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
            changelog.AddToClassList("mcb-version-update__body");
            panel.Add(changelog);
        }

        root.Add(panel);
    }

    private VisualElement CreateCreateNewVersionItemUIToolkit(bool isFirst)
    {
        var item = new VisualElement();
        item.AddToClassList("mcb-version-create");

        item.Add(CreateTimeline(isFirst, false, false, false));

        var body = new VisualElement();
        body.AddToClassList("mcb-version-create__body");

        var button = CreateTextButton("Create new version", () => editor.StartCreateNewVersion());
        button.AddToClassList("mcb-version-create__button");
        button.SetEnabled(!editor.isDownloading && !editor.isDeleting && !editor.isApplying && !editor.isSubmitting);
        body.Add(button);

        item.Add(body);
        return item;
    }

    private bool ShouldShowCreateNewVersionItem()
    {
        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset == null ||
            !editor.isAuthenticated ||
            !editor.HasServerAccess ||
            MCBPackageVersionService.RequiresMajorUpdate ||
            editor.isCreatorModeProp == null ||
            editor.isCreatorModeProp.boolValue ||
            !selectedAsset.ownerId.HasValue)
        {
            return false;
        }

        int currentUserId = GetCurrentUserId();
        return currentUserId > 0 && selectedAsset.ownerId.Value == currentUserId;
    }

    private void BuildHeaderUIToolkit(VisualElement root)
    {
        var header = new VisualElement();
        header.AddToClassList("mcb-version-list__header");

        var title = CreateLabel($"{editor.GetSelectedAssetDisplayName()} Versions", 14, FontStyle.Bold, Color.white);
        title.AddToClassList("mcb-version-list__title");
        header.Add(title);

        var changelogToggle = new Toggle("Display all changelogs") { value = displayAllChangelogs };
        changelogToggle.AddToClassList("mcb-version-toggle");
        changelogToggle.AddToClassList("mcb-version-list__changelog-toggle");
        changelogToggle.RegisterValueChangedCallback(evt =>
        {
            displayAllChangelogs = evt.newValue;
            individualChangelogStates.Clear();
            RefreshVersionUi();
        });
        header.Add(changelogToggle);

        root.Add(header);
    }

    private VisualElement CreateCollapseExpandButtonUIToolkit()
    {
        var item = new VisualElement();
        item.AddToClassList("mcb-version-collapse");

        item.Add(CreateConnectorTimeline(isListCollapsed));

        var button = new Button();
        RegisterImmediateClick(button, () =>
        {
            isListCollapsed = !isListCollapsed;
            RefreshVersionUi();
        });
        button.AddToClassList("mcb-version-collapse__button");

        Texture2D iconToUse = isListCollapsed ? expandIcon : collapseIcon;
        if (iconToUse != null)
        {
            var icon = new Image { image = iconToUse, scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("mcb-version-collapse__icon");
            button.Add(icon);
        }

        string labelText = isListCollapsed ? "expand all versions" : "collapse all versions";
        var label = CreateLabel(labelText, 11, FontStyle.Normal, new Color(0.74f, 0.74f, 0.74f));
        label.AddToClassList("mcb-version-collapse__label");
        button.Add(label);

        var line = new VisualElement();
        line.AddToClassList("mcb-version-collapse__line");
        button.Add(line);

        item.Add(button);
        return item;
    }

    private void BuildUserCustomRowsUIToolkit(VisualElement root, bool hasPreviousTimelineItem)
    {
        var entries = editor.userCustomVersions;
        var allVersions = editor.GetAllVersions();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool isApplied = editor.currentIsCustom &&
                             !string.IsNullOrEmpty(editor.currentAppliedFbxHash) &&
                             string.Equals(editor.currentAppliedFbxHash, entry.appliedUserAviHash, StringComparison.OrdinalIgnoreCase);
            bool isSelected = editor.selectedCustomVersionForAction == entry;
            bool isFirst = !hasPreviousTimelineItem && (allVersions == null || allVersions.Count == 0) && i == 0;

            Action onSelected = () =>
            {
                if (isSelected)
                {
                    editor.selectedCustomVersionForAction = null;
                }
                else
                {
                    editor.selectedCustomVersionForAction = entry;
                    editor.selectedVersionForAction = null;
                }
            };

            root.Add(CreateVersionItemUIToolkit(
                RESET_VERSION,
                isFirst,
                false,
                isSelected,
                isApplied,
                row =>
                {
                    var title = CreateLabel($"Custom {entry.detectionDate}", 12, FontStyle.Normal, Color.white);
                    title.AddToClassList("mcb-version-item__title");
                    title.AddToClassList("mcb-version-item__title--custom");
                    row.Add(title);

                    row.Add(CreateSpacer());

                    var chips = CreateChipRow();
                    if (isApplied)
                    {
                        chips.Add(CreateChip("Installed", AccentGreen));
                        chips.Add(CreateChip("Current", AccentGreen));
                    }
                    chips.Add(CreateChip("Custom", Color.red));
                    row.Add(chips);

                    var actionsRow = CreateActionRow();
                    var deleteButton = CreateInteractionIconButton(MCBInteractionIconKind.Delete, "Delete unknown version", () =>
                    {
                        bool confirm = EditorUtility.DisplayDialog(
                            "Delete Unknown Version",
                            "Are you sure you want to delete this unknown version. This might be another unsupported edit that you might want to backup. This action is irreversible.",
                            "Delete",
                            "Cancel");
                        if (!confirm)
                        {
                            return;
                        }

                        bool ok = UserCustomVersionService.Instance.Delete(entry);
                        if (ok)
                        {
                            if (editor.selectedCustomVersionForAction == entry)
                            {
                                editor.selectedCustomVersionForAction = null;
                            }

                            editor.userCustomVersions = UserCustomVersionService.Instance.GetAll();
                            RefreshVersionUi();
                        }
                    });
                    deleteButton.SetEnabled(!editor.isDownloading && !editor.isDeleting);
                    actionsRow.Add(deleteButton);
                    ApplyActionRowCountClass(actionsRow);
                    row.Add(actionsRow);
                },
                null,
                onSelected,
                string.Empty,
                string.Empty));
        }
    }

    private VisualElement CreateVersionListItemUIToolkit(CustomBaseVersion ver, bool isFirst, bool isLast)
    {
        string versionFolderPath = MCBUtils.GetVersionDataPath(ver);
        bool hasLocalContent = !string.IsNullOrEmpty(versionFolderPath) && Directory.Exists(Path.GetFullPath(versionFolderPath));
        bool isSelected = ver.Equals(editor.selectedVersionForAction);
        bool isApplied = ver.Equals(editor.customBaseTarget.appliedCustomBaseVersion);
        string stateKey = GetVersionStateKey(ver);
        bool hasVersionDetails = HasVersionDetails(ver);
        bool detailsExpanded = hasVersionDetails && IsVersionDetailsExpanded(ver);
        bool isEditing = IsEditingVersion(ver);

        var item = new VisualElement();
        item.AddToClassList("mcb-version-item");
        item.EnableInClassList("mcb-version-item--selected", isSelected);
        item.EnableInClassList("mcb-version-item--expanded", detailsExpanded);
        item.EnableInClassList("mcb-version-item--compact", !detailsExpanded);

        item.Add(CreateTimeline(isFirst, isLast, isSelected, false));

        var body = new VisualElement();
        body.AddToClassList("mcb-version-item__body");
        item.Add(body);

        var card = new VisualElement();
        card.AddToClassList("mcb-version-item__card");
        body.Add(card);

        var shell = new VisualElement();
        shell.AddToClassList("mcb-version-item__shell");
        card.Add(shell);

        shell.Add(CreateVersionBlock(ver.version));

        var main = new VisualElement();
        main.AddToClassList("mcb-version-item__main");
        shell.Add(main);

        var titleRow = new VisualElement();
        titleRow.AddToClassList("mcb-version-item__title-row");

        var title = CreateLabel(GetVersionTitle(ver), 12, FontStyle.Normal, Color.white);
        title.AddToClassList("mcb-version-item__title");
        titleRow.Add(title);

        if (hasVersionDetails)
        {
            titleRow.Add(CreateDetailsToggleButton(detailsExpanded, () =>
            {
                individualChangelogStates[stateKey] = !detailsExpanded;
                if (!individualChangelogStates[stateKey] && IsEditingVersion(ver))
                {
                    CancelEditVersion();
                }
                else
                {
                    RefreshVersionUi();
                }
            }));
        }

        titleRow.Add(CreateSpacer());

        var chips = CreateChipRow();
        if (ver.isUnsubmitted)
        {
            chips.Add(CreateChip("Unsubmitted", Color.gray));
        }
        if (isApplied)
        {
            chips.Add(CreateChip("Installed", AccentGreen));
        }
        chips.Add(CreateChip(ver.isImported ? "Imported" : ver.scope.ToString(), ver.isImported ? new Color(0.45f, 0.85f, 1f) : GetColorForScope(ver.scope)));
        titleRow.Add(chips);
        main.Add(titleRow);

        if (detailsExpanded)
        {
            main.Add(isEditing ? CreateVersionEditForm(ver) : CreateVersionDetails(ver));
        }

        shell.Add(CreateActionIconsUIToolkit(ver, hasLocalContent, detailsExpanded));

        item.RegisterCallback<MouseDownEvent>(evt =>
        {
            if (evt.button != 0 || IsEventFromButton(evt.target))
            {
                return;
            }

            if (isSelected)
            {
                editor.selectedVersionForAction = null;
                editor.selectedCustomVersionForAction = null;
            }
            else
            {
                editor.selectedVersionForAction = ver;
                editor.selectedCustomVersionForAction = null;
            }

            evt.StopPropagation();
            evt.PreventDefault();
            RefreshVersionUi();
        });

        return item;
    }

    private VisualElement CreateResetVersionItemUIToolkit(bool isFirst)
    {
        bool isSelected = RESET_VERSION.Equals(editor.selectedVersionForAction);
        var fileManagerService = new FileManagerService();
        bool canReset = fileManagerService.BackupExists(actions.GetCurrentFBXPath()) || editor.isCustomBase;
        bool isApplied = !editor.isCustomBase && !editor.currentIsCustom;

        string resetTitle = string.IsNullOrWhiteSpace(editor.GetSelectedAssetDisplayName())
            ? "Base Default"
            : $"Default {editor.GetSelectedAssetDisplayName()}";

        return CreateVersionItemUIToolkit(
            RESET_VERSION,
            isFirst,
            true,
            isSelected,
            isApplied,
            row =>
            {
                row.Add(CreateVersionBlock("base"));

                var title = CreateLabel(resetTitle, 12, FontStyle.Normal, Color.white);
                title.AddToClassList("mcb-version-item__title");
                title.AddToClassList("mcb-version-item__title--reset");
                row.Add(title);

                row.Add(CreateSpacer());

                var chips = CreateChipRow();
                if (isApplied)
                {
                    chips.Add(CreateChip("Current", AccentGreen));
                }
                row.Add(chips);

                row.Add(CreateActionRow());
            },
            canReset ? null : "No backup available",
            null,
            null,
            null);
    }

    private VisualElement CreateVersionItemUIToolkit(
        CustomBaseVersion ver,
        bool isFirst,
        bool isLast,
        bool isSelected,
        bool isApplied,
        Action<VisualElement> buildContent,
        string disabledReason,
        Action onSelected,
        string changelogKeyOverride,
        string changelogTextOverride)
    {
        bool isDisabled = !string.IsNullOrEmpty(disabledReason);

        var item = new VisualElement();
        item.AddToClassList("mcb-version-item");
        item.AddToClassList("mcb-version-item--compact");
        item.EnableInClassList("mcb-version-item--selected", isSelected && !isDisabled);
        item.EnableInClassList("mcb-version-item--disabled", isDisabled);

        item.Add(CreateTimeline(isFirst, isLast, isSelected && !isDisabled, isDisabled));

        var body = new VisualElement();
        body.AddToClassList("mcb-version-item__body");
        item.Add(body);

        var card = new VisualElement();
        card.AddToClassList("mcb-version-item__card");
        card.SetEnabled(!isDisabled);
        body.Add(card);

        var content = new VisualElement();
        content.AddToClassList("mcb-version-item__content");
        buildContent?.Invoke(content);
        card.Add(content);

        string effectiveChangelog = changelogTextOverride ?? ver?.changelog;
        bool shouldShowChangelog = false;
        if (displayAllChangelogs)
        {
            shouldShowChangelog = !string.IsNullOrEmpty(effectiveChangelog);
        }
        else
        {
            string changelogKey = changelogKeyOverride ?? (ver == RESET_VERSION ? "reset_base" : $"{ver?.version}_{ver?.scope}");
            shouldShowChangelog = !string.IsNullOrEmpty(changelogKey) &&
                                  individualChangelogStates.ContainsKey(changelogKey) &&
                                  individualChangelogStates[changelogKey] &&
                                  !string.IsNullOrEmpty(effectiveChangelog);
        }

        if (shouldShowChangelog)
        {
            var changelog = CreateLabel(effectiveChangelog, 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
            changelog.AddToClassList("mcb-version-item__changelog");
            body.Add(changelog);
        }

        if (isDisabled && !string.IsNullOrEmpty(disabledReason))
        {
            var reason = CreateLabel(disabledReason, 11, FontStyle.Normal, new Color(0.62f, 0.62f, 0.62f));
            reason.AddToClassList("mcb-version-item__disabled-reason");
            body.Add(reason);
        }

        if (!isDisabled)
        {
            item.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0 || IsEventFromButton(evt.target))
                {
                    return;
                }

                if (onSelected != null)
                {
                    onSelected();
                }
                else
                {
                    if (isSelected)
                    {
                        editor.selectedVersionForAction = null;
                        editor.selectedCustomVersionForAction = null;
                    }
                    else
                    {
                        editor.selectedVersionForAction = ver;
                        editor.selectedCustomVersionForAction = null;
                    }
                }

                evt.StopPropagation();
                evt.PreventDefault();
                RefreshVersionUi();
            });
        }

        return item;
    }

    private VisualElement CreateActionIconsUIToolkit(CustomBaseVersion ver, bool hasLocalContent, bool detailsExpanded)
    {
        var actionsRow = CreateActionRow();
        actionsRow.EnableInClassList("mcb-version-actions--stacked", detailsExpanded);
        actionsRow.SetEnabled(!editor.isDownloading && !editor.isDeleting);

        if (ver.isUnsubmitted)
        {
            actionsRow.Add(CreateIconButton("CloudConnect", "Upload version", () =>
            {
                if (EditorUtility.DisplayDialog("Confirm Upload", $"Upload version {ver.version} to the server?\n\nThis action is irreversible.", "Upload", "Cancel"))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(editor.creatorModule.UploadUnsubmittedVersionCoroutine(ver));
                    RefreshVersionUi();
                }
            }));
        }

        if (hasLocalContent && editor.customBaseTarget != null && editor.customBaseTarget.isCreatorMode)
        {
            actionsRow.Add(CreateInteractionIconButton(MCBInteractionIconKind.Save, "Export offline version", () =>
            {
                actions.ExportOfflineVersion(ver);
                RefreshVersionUi();
            }));
        }

        bool showTrashIcon = hasLocalContent || ver.isUnsubmitted;
        Button localActionButton;
        if (showTrashIcon)
        {
            localActionButton = CreateInteractionIconButton(MCBInteractionIconKind.Delete, "Delete local version files", () =>
            {
                string confirmMsg = ver.isUnsubmitted
                    ? $"Delete unsubmitted version {ver.version}? This will remove it from the list."
                    : $"Delete local files for version {ver.version}?";
                if (EditorUtility.DisplayDialog("Confirm Delete", confirmMsg, "Delete", "Cancel"))
                {
                    actions.StartVersionDelete(ver);
                    RefreshVersionUi();
                }
            });
        }
        else
        {
            localActionButton = CreateIconButton("Download-Available", "Download version", () =>
            {
                actions.StartVersionDownload(ver, false);
                RefreshVersionUi();
            });
        }
        actionsRow.Add(localActionButton);

        if (detailsExpanded && CanEditVersion(ver) && !IsEditingVersion(ver))
        {
            var editButton = CreateInteractionIconButton(MCBInteractionIconKind.Edit, "Edit version title and changelog", () => BeginEditVersion(ver));
            editButton.SetEnabled(!isUpdatingVersionMetadata);
            actionsRow.Add(editButton);
        }

        ApplyActionRowCountClass(actionsRow);
        return actionsRow;
    }

    private static void ApplyActionRowCountClass(VisualElement actionsRow)
    {
        if (actionsRow == null)
        {
            return;
        }

        int count = actionsRow.childCount;
        actionsRow.EnableInClassList("mcb-version-actions--one", count == 1);
        actionsRow.EnableInClassList("mcb-version-actions--two", count == 2);
        actionsRow.EnableInClassList("mcb-version-actions--three", count >= 3);
    }

    private VisualElement CreateVersionBlock(string version)
    {
        var block = new VisualElement();
        block.AddToClassList("mcb-version-item__version-block");

        var label = CreateLabel(version ?? string.Empty, 12, FontStyle.Normal, Color.white);
        label.AddToClassList("mcb-version-item__version-label");
        block.Add(label);
        return block;
    }

    private Button CreateDetailsToggleButton(bool detailsExpanded, Action onClick)
    {
        var button = new Button
        {
            text = detailsExpanded ? "-" : "+",
            tooltip = detailsExpanded ? "Collapse version details" : "Expand version details"
        };
        RegisterImmediateClick(button, onClick);
        button.AddToClassList("mcb-version-details-toggle");
        return button;
    }

    private VisualElement CreateVersionDetails(CustomBaseVersion ver)
    {
        var details = new VisualElement();
        details.AddToClassList("mcb-version-item__details");

        if (!string.IsNullOrWhiteSpace(ver?.changelog))
        {
            var changelog = CreateLabel(ver.changelog.Trim(), 12, FontStyle.Normal, new Color(0.86f, 0.86f, 0.86f));
            changelog.AddToClassList("mcb-version-item__changelog");
            details.Add(changelog);
        }

        if (ver != null && ver.uploaderId > 0)
        {
            details.Add(CreateUserInfoUIToolkit(ver.uploaderId, false));
        }

        return details;
    }

    private VisualElement CreateVersionEditForm(CustomBaseVersion ver)
    {
        var edit = new VisualElement();
        edit.AddToClassList("mcb-version-edit");

        Button saveButton = null;
        var titleField = new TextField { value = editingVersionTitleDraft ?? string.Empty };
        titleField.AddToClassList("mcb-version-edit__title");
        titleField.RegisterValueChangedCallback(evt =>
        {
            editingVersionTitleDraft = evt.newValue ?? string.Empty;
            saveButton?.SetEnabled(!isUpdatingVersionMetadata && !string.IsNullOrWhiteSpace(editingVersionTitleDraft));
        });
        titleField.SetEnabled(!isUpdatingVersionMetadata);
        edit.Add(titleField);

        var changelogField = new TextField { multiline = true, value = editingVersionChangelogDraft ?? string.Empty };
        changelogField.AddToClassList("mcb-version-edit__changelog");
        changelogField.RegisterValueChangedCallback(evt =>
        {
            editingVersionChangelogDraft = evt.newValue ?? string.Empty;
        });
        changelogField.SetEnabled(!isUpdatingVersionMetadata);
        edit.Add(changelogField);

        if (!string.IsNullOrWhiteSpace(versionMetadataUpdateError))
        {
            var error = CreateLabel(versionMetadataUpdateError, 11, FontStyle.Normal, new Color(1f, 0.45f, 0.45f));
            error.AddToClassList("mcb-version-edit__error");
            edit.Add(error);
        }

        var actions = new VisualElement();
        actions.AddToClassList("mcb-version-edit__actions");

        saveButton = CreateTextButton(isUpdatingVersionMetadata ? "Saving..." : "Save", () => SaveVersionMetadata(ver));
        saveButton.SetEnabled(!isUpdatingVersionMetadata && !string.IsNullOrWhiteSpace(editingVersionTitleDraft));
        actions.Add(saveButton);

        var cancelButton = CreateTextButton("Cancel", CancelEditVersion);
        cancelButton.SetEnabled(!isUpdatingVersionMetadata);
        actions.Add(cancelButton);

        edit.Add(actions);
        return edit;
    }

    private bool IsVersionDetailsExpanded(CustomBaseVersion ver)
    {
        string key = GetVersionStateKey(ver);
        return !string.IsNullOrEmpty(key) &&
               individualChangelogStates.TryGetValue(key, out bool expanded) &&
               expanded;
    }

    private static bool HasVersionDetails(CustomBaseVersion ver)
    {
        return !string.IsNullOrWhiteSpace(ver?.changelog);
    }

    private bool IsEditingVersion(CustomBaseVersion ver)
    {
        string key = GetVersionStateKey(ver);
        return !string.IsNullOrEmpty(key) && string.Equals(editingVersionKey, key, StringComparison.Ordinal);
    }

    private string GetVersionStateKey(CustomBaseVersion ver)
    {
        if (ver == null)
        {
            return string.Empty;
        }

        if (ver.id > 0)
        {
            return $"version:{ver.id}";
        }

        return $"{ver.assetId}:{ver.version}:{ver.defaultAviVersion}:{ver.scope}";
    }

    private string GetVersionTitle(CustomBaseVersion ver)
    {
        if (!string.IsNullOrWhiteSpace(ver?.title))
        {
            return ver.title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ver?.changelog))
        {
            string firstLine = ver.changelog
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrEmpty(line));
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine;
            }
        }

        string assetName = editor.GetSelectedAssetDisplayName();
        return string.IsNullOrWhiteSpace(assetName) ? $"Version {ver?.version}" : $"{assetName} {ver?.version}";
    }

    private bool CanEditVersion(CustomBaseVersion ver)
    {
        if (ver == null || ver.id <= 0 || ver.isImported || ver.isUnsubmitted)
        {
            return false;
        }

        int currentUserId = GetCurrentUserId();
        if (currentUserId <= 0)
        {
            return false;
        }

        if (ver.uploaderId == currentUserId)
        {
            return true;
        }

        var selectedAsset = editor.GetSelectedAsset();
        return selectedAsset != null && selectedAsset.ownerId.HasValue && selectedAsset.ownerId.Value == currentUserId;
    }

    private static int GetCurrentUserId()
    {
        var auth = AuthenticationService.GetAuth();
        if (auth == null || string.IsNullOrWhiteSpace(auth.user))
        {
            return 0;
        }

        return int.TryParse(auth.user, out int userId) ? userId : 0;
    }

    private void BeginEditVersion(CustomBaseVersion ver)
    {
        if (!CanEditVersion(ver))
        {
            return;
        }

        editingVersionKey = GetVersionStateKey(ver);
        editingVersionTitleDraft = GetVersionTitle(ver);
        editingVersionChangelogDraft = ver.changelog ?? string.Empty;
        versionMetadataUpdateError = null;
        individualChangelogStates[editingVersionKey] = true;
        RefreshVersionUi();
    }

    private void CancelEditVersion()
    {
        editingVersionKey = null;
        editingVersionTitleDraft = string.Empty;
        editingVersionChangelogDraft = string.Empty;
        versionMetadataUpdateError = null;
        RefreshVersionUi();
    }

    private async void SaveVersionMetadata(CustomBaseVersion ver)
    {
        if (!CanEditVersion(ver) || !IsEditingVersion(ver) || isUpdatingVersionMetadata)
        {
            return;
        }

        string nextTitle = (editingVersionTitleDraft ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(nextTitle))
        {
            versionMetadataUpdateError = "Version title is required.";
            RefreshVersionUi();
            return;
        }

        isUpdatingVersionMetadata = true;
        versionMetadataUpdateError = null;
        RefreshVersionUi();

        string token = editor.authToken;
        string url = $"{MCBUtils.getApiUrl("creator")}/assets/{ver.assetId}/versions/{ver.id}?t={token}";
        var result = await networkService.UpdateCreatorVersionMetadataAsync(url, token, nextTitle, editingVersionChangelogDraft ?? string.Empty);

        isUpdatingVersionMetadata = false;
        if (!result.success)
        {
            versionMetadataUpdateError = result.error;
            RefreshVersionUi();
            return;
        }

        ApplyVersionMetadata(ver, result.version);
        editingVersionKey = null;
        editingVersionTitleDraft = string.Empty;
        editingVersionChangelogDraft = string.Empty;
        versionMetadataUpdateError = null;
        RefreshVersionUi();
    }

    private void ApplyVersionMetadata(CustomBaseVersion original, CustomBaseVersion updated)
    {
        if (updated == null)
        {
            return;
        }

        ApplyVersionMetadataFields(original, updated);
        ApplyVersionMetadataToList(editor.serverVersions, updated);
        ApplyVersionMetadataToList(editor.importedVersions, updated);
        if (editor.selectedVersionForAction != null &&
            (editor.selectedVersionForAction.id == updated.id || editor.selectedVersionForAction.Equals(updated)))
        {
            ApplyVersionMetadataFields(editor.selectedVersionForAction, updated);
        }
    }

    private static void ApplyVersionMetadataToList(List<CustomBaseVersion> versions, CustomBaseVersion updated)
    {
        if (versions == null || updated == null)
        {
            return;
        }

        foreach (var version in versions)
        {
            if (version == null)
            {
                continue;
            }

            if ((updated.id > 0 && version.id == updated.id) || version.Equals(updated))
            {
                ApplyVersionMetadataFields(version, updated);
            }
        }
    }

    private static void ApplyVersionMetadataFields(CustomBaseVersion target, CustomBaseVersion updated)
    {
        if (target == null || updated == null)
        {
            return;
        }

        target.id = updated.id;
        target.title = updated.title;
        target.changelog = updated.changelog;
    }

    private VisualElement CreateUserInfoUIToolkit(int uploaderId, bool includeBy = true)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-version-user");
        row.EnableInClassList("mcb-version-user--author", !includeBy);

        if (includeBy)
        {
            var by = CreateLabel("by", 11, FontStyle.Normal, new Color(0.62f, 0.62f, 0.62f));
            by.AddToClassList("mcb-version-user__by");
            row.Add(by);
        }

        var userInfo = UserService.GetUserInfo(uploaderId);
        var userAvatar = UserService.GetUserAvatar(uploaderId);
        bool requestedThisFrame = false;

        if (userInfo == null)
        {
            UserService.RequestUserInfo(uploaderId, RefreshVersionUi);
            requestedThisFrame = true;
        }

        var avatar = new VisualElement();
        avatar.AddToClassList("mcb-version-user__avatar");
        if (userAvatar != null)
        {
            var image = new Image { image = userAvatar, scaleMode = ScaleMode.ScaleAndCrop };
            image.AddToClassList("mcb-version-user__avatar-image");
            avatar.Add(image);
        }
        else
        {
            if (userInfo != null && !string.IsNullOrEmpty(userInfo.username))
            {
                var initials = CreateLabel(GetInitials(userInfo.username), 9, FontStyle.Bold, Color.white);
                initials.AddToClassList("mcb-version-user__avatar-initials");
                avatar.Add(initials);
            }

            if (!requestedThisFrame)
            {
                UserService.RequestUserInfo(uploaderId, RefreshVersionUi);
            }
        }
        row.Add(avatar);

        string displayName;
        if (userInfo != null && !string.IsNullOrEmpty(userInfo.username))
        {
            displayName = userInfo.username;
        }
        else if (userInfo != null)
        {
            displayName = $"User {uploaderId}";
        }
        else
        {
            displayName = "...";
        }

        var name = CreateLabel(displayName, 11, FontStyle.Normal, new Color(0.78f, 0.78f, 0.78f));
        name.AddToClassList("mcb-version-user__name");
        row.Add(name);
        return row;
    }

    private Button CreateChangelogButton(string changelogKey)
    {
        return CreateIconButton("UnityEditor.ConsoleWindow", "Toggle changelog", () =>
        {
            bool showingChangelog = individualChangelogStates.ContainsKey(changelogKey) && individualChangelogStates[changelogKey];
            individualChangelogStates[changelogKey] = !showingChangelog;
            RefreshVersionUi();
        });
    }

    private static VisualElement CreateTimeline(bool isFirst, bool isLast, bool isSelected, bool isDisabled)
    {
        var timeline = new VisualElement();
        timeline.AddToClassList("mcb-version-timeline");
        timeline.EnableInClassList("mcb-version-timeline--disabled", isDisabled);

        var topLine = CreateTimelineLine(isFirst);
        timeline.Add(topLine);

        var markerShell = new VisualElement();
        markerShell.AddToClassList("mcb-version-timeline__marker-shell");
        markerShell.EnableInClassList("mcb-version-timeline__marker-shell--selected", isSelected);

        var marker = new VisualElement();
        marker.AddToClassList("mcb-version-timeline__marker");
        markerShell.Add(marker);
        timeline.Add(markerShell);

        var bottomLine = CreateTimelineLine(isLast);
        timeline.Add(bottomLine);
        return timeline;
    }

    private static VisualElement CreateConnectorTimeline(bool isCollapsed)
    {
        var timeline = new VisualElement();
        timeline.AddToClassList("mcb-version-timeline");
        timeline.AddToClassList("mcb-version-timeline--connector");
        timeline.EnableInClassList("mcb-version-timeline--collapsed", isCollapsed);

        if (isCollapsed)
        {
            var dots = new VisualElement();
            dots.AddToClassList("mcb-version-timeline__connector-dots");
            for (int i = 0; i < 3; i++)
            {
                var dot = new VisualElement();
                dot.AddToClassList("mcb-version-timeline__connector-dot");
                dots.Add(dot);
            }

            timeline.Add(dots);
        }
        else
        {
            var line = new VisualElement();
            line.AddToClassList("mcb-version-timeline__connector-line");
            timeline.Add(line);
        }
        return timeline;
    }

    private static VisualElement CreateTimelineLine(bool hidden)
    {
        var line = new VisualElement();
        line.AddToClassList("mcb-version-timeline__line");
        line.EnableInClassList("mcb-version-timeline__line--hidden", hidden);
        return line;
    }

    private static VisualElement CreateChipRow()
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-version-chip-row");
        return row;
    }

    private static Label CreateChip(string text, Color textColor)
    {
        var label = CreateLabel((text ?? string.Empty).ToLowerInvariant(), 11, FontStyle.Bold, textColor);
        label.AddToClassList("mcb-version-chip");
        label.style.borderTopColor = textColor;
        label.style.borderRightColor = textColor;
        label.style.borderBottomColor = textColor;
        label.style.borderLeftColor = textColor;
        return label;
    }

    private static VisualElement CreateActionRow()
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-version-actions");
        return row;
    }

    private static Button CreateIconButton(string iconName, string tooltip, Action onClick)
    {
        var button = new Button { tooltip = tooltip ?? string.Empty };
        button.AddToClassList("mcb-button");
        button.AddToClassList("mcb-button--icon-only");
        button.AddToClassList("mcb-version-action-button");
        RegisterImmediateClick(button, onClick);

        var iconContent = EditorGUIUtility.IconContent(iconName);
        if (iconContent?.image != null)
        {
            var icon = new Image { image = iconContent.image, scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("mcb-button-icon");
            button.Add(icon);
        }
        else if (!string.IsNullOrEmpty(tooltip))
        {
            button.text = tooltip.Substring(0, 1);
        }

        return button;
    }

    private static Button CreateInteractionIconButton(MCBInteractionIconKind iconKind, string tooltip, Action onClick)
    {
        var button = new Button { tooltip = tooltip ?? string.Empty };
        button.AddToClassList("mcb-button");
        button.AddToClassList("mcb-button--icon-only");
        button.AddToClassList("mcb-version-action-button");
        RegisterImmediateClick(button, onClick);

        var icon = new MCBInteractionIconElement(iconKind);
        icon.AddToClassList("mcb-button-icon");
        button.Add(icon);
        return button;
    }

    private static Button CreateTextButton(string text, Action onClick)
    {
        var button = new Button { text = text ?? string.Empty };
        button.AddToClassList("mcb-button");
        RegisterImmediateClick(button, onClick);
        return button;
    }

    private static void RegisterImmediateClick(Button button, Action onClick)
    {
        if (button == null || onClick == null)
        {
            return;
        }

        bool suppressNextClicked = false;
        long lastImmediateTicks = 0L;

        void activateImmediate(EventBase evt)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!button.enabledInHierarchy ||
                now - lastImmediateTicks < TimeSpan.TicksPerMillisecond * 25L)
            {
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                return;
            }

            lastImmediateTicks = now;
            suppressNextClicked = true;
            button.schedule.Execute(() => suppressNextClicked = false).StartingIn(1000);
            evt.StopImmediatePropagation();
            evt.PreventDefault();
            onClick();
        }

        button.clicked += () =>
        {
            if (suppressNextClicked)
            {
                suppressNextClicked = false;
                return;
            }

            onClick();
        };

        button.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0)
            {
                return;
            }

            activateImmediate(evt);
        }, TrickleDown.TrickleDown);
        button.RegisterCallback<MouseDownEvent>(evt =>
        {
            if (evt.button != 0)
            {
                return;
            }

            activateImmediate(evt);
        }, TrickleDown.TrickleDown);
        button.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode != KeyCode.Return &&
                evt.keyCode != KeyCode.KeypadEnter &&
                evt.keyCode != KeyCode.Space)
            {
                return;
            }

            activateImmediate(evt);
        }, TrickleDown.TrickleDown);
    }

    private static VisualElement CreateSpacer()
    {
        var spacer = new VisualElement();
        spacer.AddToClassList("mcb-version-spacer");
        return spacer;
    }

    private static VisualElement CreateHelpBox(string message, HelpBoxMessageType messageType)
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

        var icon = CreateLabel(GetHelpBoxIcon(messageType), 14, FontStyle.Bold, Color.white);
        icon.AddToClassList("mcb-version-helpbox__icon");
        box.Add(icon);

        var label = CreateLabel(message, 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
        label.AddToClassList("mcb-version-helpbox__text");
        box.Add(label);
        return box;
    }

    private static string GetHelpBoxIcon(HelpBoxMessageType messageType)
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

    private static Label CreateLabel(string text, int fontSize, FontStyle fontStyle, Color color)
    {
        var label = new Label(text ?? string.Empty);
        label.AddToClassList("mcb-label");
        label.style.fontSize = fontSize;
        label.style.unityFontStyleAndWeight = fontStyle;
        label.style.color = color;
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        return label;
    }

    private static bool IsEventFromButton(IEventHandler target)
    {
        var element = target as VisualElement;
        while (element != null)
        {
            if (element is Button || element is TextField)
            {
                return true;
            }

            element = element.parent;
        }

        return false;
    }

    private void RefreshVersionUi()
    {
        editor.versionModule?.RefreshUIToolkit();
        editor.Repaint();
    }
}
#endif
