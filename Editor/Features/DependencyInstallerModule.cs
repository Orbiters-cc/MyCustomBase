#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class DependencyInstallerModule
{
    private readonly MCBEditor editor;
    private VisualElement dependencyRoot;
    private List<VpmDependencyStatus> missingRequiredDependencies = new List<VpmDependencyStatus>();

    public bool HasBlockingRequiredDependencies
    {
        get { return missingRequiredDependencies != null && missingRequiredDependencies.Count > 0; }
    }

    public DependencyInstallerModule(MCBEditor editor)
    {
        this.editor = editor;
    }

    public void Initialize()
    {
        VpmDependencyService.Instance.StatusChanged += OnDependencyStatusChanged;
        RefreshStatuses();
    }

    public void Dispose()
    {
        VpmDependencyService.Instance.StatusChanged -= OnDependencyStatusChanged;
    }

    public void AttachUIToolkit(VisualElement root)
    {
        dependencyRoot = root;
        RefreshUIToolkit();
    }

    public void DetachUIToolkit()
    {
        dependencyRoot = null;
    }

    public void RefreshUIToolkit()
    {
        RefreshStatuses();

        if (dependencyRoot == null)
        {
            return;
        }

        dependencyRoot.Clear();
        dependencyRoot.AddToClassList("mcb-dependencies");

        if (!HasBlockingRequiredDependencies)
        {
            dependencyRoot.style.display = DisplayStyle.None;
            return;
        }

        dependencyRoot.style.display = DisplayStyle.Flex;

        var panel = new VisualElement();
        panel.AddToClassList("mcb-dependencies__panel");
        dependencyRoot.Add(panel);

        var eyebrow = new Label("missing required dependencies");
        eyebrow.AddToClassList("mcb-dependencies__eyebrow");
        panel.Add(eyebrow);

        var title = new Label(BuildTitle());
        title.AddToClassList("mcb-dependencies__title");
        panel.Add(title);

        var body = new Label("Install the required VPM packages to continue. MCB will add the configured repositories if they are missing, then resolve the project packages.");
        body.AddToClassList("mcb-dependencies__body");
        panel.Add(body);

        var list = new VisualElement();
        list.AddToClassList("mcb-dependencies__list");
        panel.Add(list);

        foreach (var dependency in missingRequiredDependencies)
        {
            list.Add(CreateDependencyRow(dependency));
        }

        var actionRow = new VisualElement();
        actionRow.AddToClassList("mcb-dependencies__actions");
        panel.Add(actionRow);

        var installButton = new Button(InstallMissingRequiredDependencies)
        {
            text = VpmDependencyService.Instance.IsInstalling
                ? "Installing..."
                : VpmDependencyService.BuildInstallButtonLabel(missingRequiredDependencies)
        };
        installButton.SetEnabled(!VpmDependencyService.Instance.IsInstalling);
        installButton.AddToClassList("mcb-button");
        installButton.AddToClassList("mcb-button--primary");
        installButton.AddToClassList("mcb-dependencies__install-button");
        actionRow.Add(installButton);
    }

    public bool DrawFallbackIfBlocked()
    {
        RefreshStatuses();
        if (!HasBlockingRequiredDependencies)
        {
            return false;
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.HelpBox(
            BuildTitle() + "\n\nInstall the required VPM packages to continue. MCB will add the configured repositories if they are missing, then resolve the project packages.",
            MessageType.Error);

        foreach (var dependency in missingRequiredDependencies)
        {
            string line = dependency.DisplayName + " (" + dependency.Id + ")";
            if (!string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                line += " " + dependency.VersionRange;
            }

            EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
        }

        using (new EditorGUI.DisabledScope(VpmDependencyService.Instance.IsInstalling))
        {
            string label = VpmDependencyService.Instance.IsInstalling
                ? "Installing..."
                : VpmDependencyService.BuildInstallButtonLabel(missingRequiredDependencies);
            if (GUILayout.Button(label, GUILayout.Height(30f)))
            {
                InstallMissingRequiredDependencies();
            }
        }

        return true;
    }

    private void RefreshStatuses()
    {
        missingRequiredDependencies = VpmDependencyService.Instance.GetMissingRequiredDependencyStatuses();
    }

    private string BuildTitle()
    {
        if (missingRequiredDependencies == null || missingRequiredDependencies.Count == 0)
        {
            return "MCB is ready";
        }

        if (missingRequiredDependencies.Count == 1)
        {
            return "MCB needs " + missingRequiredDependencies[0].DisplayName + " before it can run";
        }

        return "MCB needs required packages before it can run";
    }

    private static VisualElement CreateDependencyRow(VpmDependencyStatus dependency)
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-dependencies__row");

        var marker = new VisualElement();
        marker.AddToClassList("mcb-dependencies__marker");
        row.Add(marker);

        var text = new VisualElement();
        text.AddToClassList("mcb-dependencies__row-text");
        row.Add(text);

        var name = new Label(dependency.DisplayName);
        name.AddToClassList("mcb-dependencies__name");
        text.Add(name);

        string detailText = dependency.Id;
        if (!string.IsNullOrWhiteSpace(dependency.VersionRange))
        {
            detailText += " " + dependency.VersionRange;
        }

        if (!string.IsNullOrWhiteSpace(dependency.InstalledVersion))
        {
            detailText += " (installed " + dependency.InstalledVersion + ")";
        }

        var detail = new Label(detailText);
        detail.AddToClassList("mcb-dependencies__detail");
        text.Add(detail);

        return row;
    }

    private void InstallMissingRequiredDependencies()
    {
        var result = VpmDependencyService.Instance.InstallMissingRequiredDependencies();
        if (!result.Success)
        {
            EditorUtility.DisplayDialog("Install Required Dependencies Failed", result.ErrorMessage, "Ok");
        }
        else
        {
            EditorUtility.DisplayDialog(
                "Required Dependencies Installed",
                "The required packages were installed. Unity may reload assemblies before MCB becomes available.",
                "Ok");
        }

        EditorApplication.delayCall += () =>
        {
            RefreshUIToolkit();
            editor.RefreshUiToolkitSections();
            editor.Repaint();
        };
    }

    private void OnDependencyStatusChanged()
    {
        EditorApplication.delayCall += () =>
        {
            RefreshUIToolkit();
            editor.RefreshUiToolkitSections();
            editor.Repaint();
        };
    }
}
#endif
