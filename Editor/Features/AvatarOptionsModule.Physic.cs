#if UNITY_EDITOR
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Physic and squishy: secondary-motion chains users turn on, or the build strips.
public partial class AvatarOptionsModule
{
    // What a chain kind is called in the options, and what its chains do.
    private sealed class ChainOption
    {
        public string Title, Noun, Motion, Tooltip;
        public IconGlyph Glyph;
    }

    private static ChainOption Describe(PhysicService.Kind kind) => kind == PhysicService.Kind.Squishy
        ? new ChainOption { Title = "Squishy", Noun = "interaction", Glyph = IconGlyph.People, Tooltip = "Players squash the version's interaction bones by touching them",
            Motion = "squash when players touch them, and spring back" }
        : new ChainOption { Title = "Physic", Noun = "physic", Glyph = IconGlyph.Bones, Tooltip = "Secondary motion on the version's physic bones",
            Motion = "react to your movement, for everyone in the world" };

    // The switch reacts at once; the build applies the choice (PhysBones on the chains, or the chains stripped).
    private bool BuildChainOption(VisualElement root, PhysicService.Kind kind)
    {
        var owner = editor.customBaseTarget;
        if (owner == null || !kind.SupportedBy(owner.appliedCustomization)) return false;
        var text = Describe(kind);
        var avatar = AvatarPaths.Root(owner);
        var groups = PhysicService.Groups(avatar, kind);
        int chains = groups.Sum(g => g.Chains.Count);
        int bones = PhysicService.Bones(avatar, kind).Count;

        var card = CreateOptionCard("mcb-physic");
        var header = Header(text.Glyph, text.Title, out var caption);
        card.Add(header);

        var stats = new VisualElement(); stats.AddToClassList("mcb-physic__stats");
        var physBones = Stat(IconGlyph.Sparkle, groups.Count + " PhysBone" + (groups.Count == 1 ? "" : "s") + " · " + chains + " chain" + (chains == 1 ? "" : "s"),
            $"Added by the build when {text.Title.ToLowerInvariant()} is on. Plain PhysBones: every player in the world sees them.");
        var stripped = Stat(IconGlyph.Broom, bones + " bone" + (bones == 1 ? "" : "s") + " removed",
            $"Removed by the build when {text.Title.ToLowerInvariant()} is off: their weights go to the parent bone, so the avatar stays light.");
        stats.Add(physBones); stats.Add(stripped);
        card.Add(stats);

        // The switch flips on pointer down; the scene does not change, only what the build will do.
        ToggleSwitch toggle = null;
        toggle = new ToggleSwitch(kind.EnabledOn(owner), on => Set(on)) { tooltip = text.Tooltip };
        toggle.AddToClassList("mcb-physic__switch");
        header.Add(toggle);

        void Show(bool on)
        {
            card.EnableInClassList("mcb-physic--on", on);
            toggle.SetValueWithoutNotify(on);
            physBones.EnableInClassList("mcb-physic__stat--active", on);
            stripped.EnableInClassList("mcb-physic__stat--active", !on);
            caption.text = chains == 0 ? $"This version supports {text.Title.ToLowerInvariant()}, but this avatar has no {text.Noun} bones."
                : on ? $"{chains} bone chain{(chains == 1 ? "" : "s")} {text.Motion}."
                : $"Off: the build removes the {bones} {text.Noun} bone{(bones == 1 ? "" : "s")} to keep your avatar light.";
        }
        Show(kind.EnabledOn(owner));
        toggle.SetEnabled(chains > 0);
        void Set(bool on)
        {
            if (chains == 0) return;
            Show(on);
            Undo.RecordObject(owner, (on ? "Enable " : "Disable ") + text.Title.ToLowerInvariant());
            kind.SetEnabled(owner, on);
            EditorUtility.SetDirty(owner);
        }
        root.Add(card);
        return true;
    }

    private static VisualElement Header(IconGlyph glyph, string title, out Label caption)
    {
        var header = new VisualElement(); header.AddToClassList("mcb-modes__header");
        var badge = new VisualElement(); badge.AddToClassList("mcb-modes__badge");
        badge.Add(new VectorIcon(glyph));
        header.Add(badge);
        var titles = new VisualElement(); titles.AddToClassList("mcb-modes__titles");
        titles.Add(CreateOptionTitle(title));
        caption = new Label(); caption.AddToClassList("mcb-modes__caption");
        titles.Add(caption);
        header.Add(titles);
        return header;
    }

    private static VisualElement Stat(IconGlyph glyph, string text, string tooltip)
    {
        var stat = new VisualElement { tooltip = tooltip }; stat.AddToClassList("mcb-physic__stat");
        stat.Add(new VectorIcon(glyph));
        var label = new Label(text); label.AddToClassList("mcb-physic__stat-label");
        stat.Add(label);
        return stat;
    }
}
#endif
