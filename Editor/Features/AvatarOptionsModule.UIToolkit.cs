#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
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
            hasContent |= BuildModeOptions(avatarOptionsRoot);
            hasContent |= refitDrawer.BuildUIToolkit(avatarOptionsRoot);
            hasContent |= customVeinsDrawer.BuildUIToolkit(avatarOptionsRoot);
            foreach (var kind in PhysicService.Kind.All) hasContent |= BuildChainOption(avatarOptionsRoot, kind);
            hasContent |= blendshapeDrawer.BuildUIToolkit(avatarOptionsRoot);
            hasContent |= slidersDrawer.BuildUIToolkit(avatarOptionsRoot);
        }
        finally
        {
            editor.serializedObject.ApplyModifiedProperties();
        }

        avatarOptionsRoot.style.display = hasContent ? DisplayStyle.Flex : DisplayStyle.None;
    }

    // Modes: tiles per category. A tile reacts on pointer down; the avatar changes on the next frame.
    private bool BuildModeOptions(VisualElement root)
    {
        var owner = editor.customBaseTarget;
        var modes = owner?.appliedCustomization?.modes;
        if (modes?.options == null || modes.options.Count == 0) return false;
        var card = CreateOptionCard("mcb-modes");
        var header = new VisualElement(); header.AddToClassList("mcb-modes__header");
        var badge = new VisualElement(); badge.AddToClassList("mcb-modes__badge");
        badge.Add(new VectorIcon(IconGlyph.Sparkle));
        header.Add(badge);
        var titles = new VisualElement(); titles.AddToClassList("mcb-modes__titles");
        titles.Add(CreateOptionTitle("Modes"));
        var caption = new Label("Switch parts of your avatar. Your choice stays fixed in the built avatar.");
        caption.AddToClassList("mcb-modes__caption");
        titles.Add(caption);
        header.Add(titles);
        card.Add(header);
        var message = new Label(); message.AddToClassList("mcb-modes__error"); message.style.display = DisplayStyle.None;

        var enabled = new HashSet<string>(ModeService.EnabledIds(owner));
        var tiles = new Dictionary<string, Button>();
        void Show(HashSet<string> on)
        {
            foreach (var pair in tiles)
            {
                pair.Value.EnableInClassList("mcb-mode-tile--on", on.Contains(pair.Key));
                pair.Value.Q<ToggleSwitch>()?.SetValueWithoutNotify(on.Contains(pair.Key));
            }
        }
        foreach (var category in modes.categories)
        {
            var members = modes.options.Where(o => o.category == category.id).ToList();
            if (members.Count == 0) continue;
            var group = new VisualElement(); group.AddToClassList("mcb-modes__group");
            var groupHeader = new VisualElement(); groupHeader.AddToClassList("mcb-modes__group-header");
            var name = new Label(category.label.ToUpperInvariant()); name.AddToClassList("mcb-modes__group-name");
            var hint = new Label(category.exclusive ? "Pick one" : "Mix and match"); hint.AddToClassList("mcb-modes__group-hint");
            groupHeader.Add(name); groupHeader.Add(hint);
            group.Add(groupHeader);
            var row = new VisualElement(); row.AddToClassList("mcb-modes__tiles");
            foreach (var option in members)
            {
                var tile = new Button(); tile.AddToClassList("mcb-mode-tile");
                tile.EnableInClassList("mcb-mode-tile--exclusive", category.exclusive);
                tile.tooltip = category.exclusive ? "Use " + option.label : (enabled.Contains(option.id) ? "Turn off " : "Turn on ") + option.label;
                var bubble = new VisualElement(); bubble.AddToClassList("mcb-mode-tile__bubble");
                bubble.Add(new VectorIcon(ModeIcon(option, category)));
                tile.Add(bubble);
                var label = new Label(option.label); label.AddToClassList("mcb-mode-tile__label");
                tile.Add(label);
                if (category.exclusive)
                {
                    var check = new VisualElement(); check.AddToClassList("mcb-mode-tile__check");
                    check.Add(new VectorIcon(IconGlyph.Check));
                    tile.Add(check);
                }
                else
                {
                    var toggle = new ToggleSwitch(enabled.Contains(option.id), _ => { }) { pickingMode = PickingMode.Ignore };
                    toggle.Query().ForEach(e => e.pickingMode = PickingMode.Ignore);
                    toggle.AddToClassList("mcb-mode-tile__switch");
                    tile.Add(toggle);
                }
                string id = option.id;
                bool exclusive = category.exclusive;
                ButtonInteraction.RegisterImmediateClick(tile, () =>
                {
                    bool turnOn = exclusive || !enabled.Contains(id);
                    if (exclusive && enabled.Contains(id)) return;
                    var previous = new HashSet<string>(enabled);
                    if (exclusive) enabled.RemoveWhere(e => members.Any(m => m.id == e));
                    if (turnOn) enabled.Add(id); else enabled.Remove(id);
                    Show(enabled);
                    tile.AddToClassList("mcb-mode-tile--pulse");
                    tile.schedule.Execute(() => tile.RemoveFromClassList("mcb-mode-tile--pulse")).StartingIn(260);
                    // Optimistic: the tile already shows the new state; the avatar follows on the next frame.
                    card.schedule.Execute(() =>
                    {
                        try
                        {
                            ModeService.Set(owner, id, turnOn);
                            message.style.display = DisplayStyle.None;
                            editor.serializedObject.Update();
                            SceneView.RepaintAll();
                        }
                        catch (System.Exception ex)
                        {
                            enabled.Clear(); enabled.UnionWith(previous); Show(enabled);
                            message.text = ex.Message; message.style.display = DisplayStyle.Flex;
                        }
                    });
                });
                tiles[id] = tile;
                row.Add(tile);
            }
            group.Add(row);
            card.Add(group);
        }
        Show(enabled);
        card.Add(message);
        root.Add(card);
        return true;
    }

    private static readonly string[] MaleNames = { "male", "man", "masculine", "boy" };
    private static readonly string[] FemaleNames = { "female", "woman", "feminine", "girl" };

    /// <summary>Male and female modes get their figure; other genres a person, other modes a sparkle.</summary>
    private static IconGlyph ModeIcon(ModeOption option, ModeCategory category)
    {
        bool Is(string[] names) => names.Contains((option.id ?? "").Trim().ToLowerInvariant()) || names.Contains((option.label ?? "").Trim().ToLowerInvariant());
        if (Is(MaleNames)) return IconGlyph.Male;
        if (Is(FemaleNames)) return IconGlyph.Female;
        return category.id == ModeCategory.GenreId ? IconGlyph.Person : IconGlyph.Sparkle;
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
