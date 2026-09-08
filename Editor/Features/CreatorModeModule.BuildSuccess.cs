#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CreatorModeModule
{
    [Serializable]
    private class WindowDraft
    {
        public int major, minor, patch;
        public Scope scope;
        public string title, changelog, parentVersion, parentDefault;
    }

    internal string SaveWindowDraft()
    {
        if (compatibleParentVersions == null) PopulateParentVersionDropdown();
        return JsonUtility.ToJson(new WindowDraft {
            major = newVersionMajor, minor = newVersionMinor, patch = newVersionPatch,
            scope = newVersionScope, title = newVersionTitle, changelog = newChangelog,
            parentVersion = selectedParentVersionObject?.version, parentDefault = selectedParentVersionObject?.defaultAviVersion });
    }

    internal void LoadWindowDraft(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        var draft = JsonUtility.FromJson<WindowDraft>(json);
        PopulateParentVersionDropdown();
        newVersionMajor = draft.major; newVersionMinor = draft.minor; newVersionPatch = draft.patch;
        newVersionScope = draft.scope; newVersionTitle = draft.title; newChangelog = draft.changelog;
        var parent = compatibleParentVersions.FirstOrDefault(v => v.version == draft.parentVersion && v.defaultAviVersion == draft.parentDefault);
        if (parent != null) { selectedParentVersionObject = parent; selectedParentVersionIndex = compatibleParentVersions.IndexOf(parent); }
    }

    private void BuildSuccessUIToolkit()
    {
        editor.LoadUnsubmittedVersions();
        var pending = editor.unsubmittedVersions.Where(v => v != null && v.assetId == editor.GetSelectedAsset().id)
            .OrderByDescending(v => editor.ParseVersion(v.version)).ToList();
        var built = pending.FirstOrDefault(v => v.version == creatorWindow.builtVersion && v.defaultAviVersion == creatorWindow.builtDefaultVersion);
        var heading = new Label(built != null ? "Version built successfully" : "Build complete");
        heading.AddToClassList("mcb-build-success__title");
        creatorRoot.Add(heading);
        var description = new Label(built != null
            ? $"Version {built.version} is saved locally and ready to upload."
            : $"Version {creatorWindow.builtVersion} is no longer awaiting upload.");
        description.AddToClassList("mcb-build-success__description");
        creatorRoot.Add(description);
        if (built != null)
        {
            var upload = CreateSavedBuildUploadButton(built, $"Upload version {built.version}");
            upload.AddToClassList("mcb-build-success__upload");
            creatorRoot.Add(upload);
        }
        var create = CreateTextButton("Create new version", null, () =>
        {
            creatorWindow.showBuildSuccess = false;
            ClearBuiltPendingVersion();
            newVersionTitle = ""; newChangelog = "";
            previouslySelectedVersion = editor.selectedVersionForAction;
            PopulateParentVersionDropdown();
            var latest = editor.GetAllVersions().Concat(pending).OrderByDescending(v => editor.ParseVersion(v.version)).FirstOrDefault();
            SetDefaultVersionNumbers(latest);
            RefreshUIToolkit();
        });
        create.SetEnabled(!editor.isSubmitting);
        create.AddToClassList("mcb-build-success__create");
        creatorRoot.Add(create);
        creatorRoot.Add(CreateSectionLabel("Unpublished versions"));
        if (pending.Count == 0) creatorRoot.Add(CreateMutedLabel("All saved versions for this asset have been uploaded."));
        foreach (var version in pending)
        {
            var row = CreateCreatorRow();
            row.AddToClassList("mcb-build-success__row");
            var label = new Label(version.version + "  " + version.title);
            label.AddToClassList("mcb-build-success__version");
            row.Add(label);
            row.Add(CreateSavedBuildUploadButton(version, "Upload"));
            creatorRoot.Add(row);
        }
        if (!string.IsNullOrEmpty(editor.submitError))
            creatorRoot.Add(CreateHelpBox(editor.submitError, HelpBoxMessageType.Error));
    }

    private Button CreateSavedBuildUploadButton(CustomBaseVersion version, string label)
    {
        var button = CreateTextButton(editor.isSubmitting ? "Uploading…" : label, "Upload this saved build without rebuilding it.", () =>
        {
            if (editor.isSubmitting) return;
            EditorCoroutineUtility.StartCoroutineOwnerless(UploadUnsubmittedVersionCoroutine(version));
            RefreshUIToolkit();
        });
        button.AddToClassList("mcb-button--primary");
        button.SetEnabled(!editor.isSubmitting);
        return button;
    }
}
#endif
