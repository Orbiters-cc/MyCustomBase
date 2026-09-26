#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public partial class AvatarOptionsModule
{
    private VisualElement avatarOptionsRoot;
    private IVisualElementScheduledItem avatarOptionsRefreshSchedule;

    public void AttachUIToolkit(VisualElement root)
    {
        avatarOptionsRoot = root;
        RefreshUIToolkit();

        avatarOptionsRefreshSchedule?.Pause();
        avatarOptionsRefreshSchedule = avatarOptionsRoot?.schedule.Execute(() =>
        {
            slidersDrawer?.TickUIToolkit();
            if (slidersDrawer != null && slidersDrawer.NeedsUIToolkitStateRefresh())
            {
                RefreshUIToolkit();
            }
        }).Every(500);
    }

    public void DetachUIToolkit()
    {
        avatarOptionsRefreshSchedule?.Pause();
        avatarOptionsRefreshSchedule = null;
        avatarOptionsRoot = null;
    }

    public void RefreshUIToolkit()
    {
        if (avatarOptionsRoot == null)
        {
            return;
        }

        avatarOptionsRoot.Clear();
        avatarOptionsRoot.AddToClassList("mcb-avatar-options");
        avatarOptionsRoot.EnableInClassList("mcb-avatar-options--asset-view", editor.GetSelectedAsset() != null);

        if (!ShouldShowAvatarOptionsUIToolkit())
        {
            avatarOptionsRoot.style.display = DisplayStyle.None;
            return;
        }

        editor.serializedObject.Update();
        bool hasContent = false;
        try
        {
            hasContent |= refitDrawer.BuildUIToolkit(avatarOptionsRoot);
            hasContent |= customVeinsDrawer.BuildUIToolkit(avatarOptionsRoot);
            hasContent |= slidersDrawer.BuildUIToolkit(avatarOptionsRoot);
            hasContent |= blendshapeDrawer.BuildUIToolkit(avatarOptionsRoot);
        }
        finally
        {
            editor.serializedObject.ApplyModifiedProperties();
        }

        avatarOptionsRoot.style.display = hasContent ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private bool ShouldShowAvatarOptionsUIToolkit()
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

    internal static Label CreateOptionLabel(string text, int fontSize, FontStyle fontStyle, Color color)
    {
        var label = new Label(text ?? string.Empty);
        label.AddToClassList("mcb-label");
        label.style.fontSize = fontSize;
        label.style.unityFontStyleAndWeight = fontStyle;
        label.style.color = color;
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        return label;
    }

    internal static Label CreateOptionTitle(string text)
    {
        var label = CreateOptionLabel(text, 14, FontStyle.Bold, Color.white);
        label.AddToClassList("mcb-avatar-option__title");
        return label;
    }

    internal static Button CreateOptionButton(string text, System.Action onClick)
    {
        var button = new Button(onClick) { text = text ?? string.Empty };
        button.AddToClassList("mcb-button");
        return button;
    }

    internal static VisualElement CreateOptionCard(string extraClass = null)
    {
        var card = new VisualElement();
        card.AddToClassList("mcb-form-card");
        card.AddToClassList("mcb-avatar-option");
        if (!string.IsNullOrEmpty(extraClass))
        {
            card.AddToClassList(extraClass);
        }

        return card;
    }

    internal static VisualElement CreateOptionHelpBox(string message, HelpBoxMessageType messageType)
        => new OrbitersNoticeElement(message, messageType);

    internal static void RefreshEditorUi(MCBEditor editor)
    {
        editor?.RefreshUiToolkitSections();
        editor?.Repaint();
    }

}
#endif
