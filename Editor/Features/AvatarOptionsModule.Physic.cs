#if UNITY_EDITOR
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Physic and the avatar budget: what the build adds or removes, and where the avatar stands against VRChat's limits.
public partial class AvatarOptionsModule
{
    private AvatarBudgetPanel budgetPanel;

    // The switch reacts at once; the build applies the choice (PhysBones on the chains, or the chains stripped).
    private bool BuildPhysicOption(VisualElement root)
    {
        var owner = editor.customBaseTarget;
        if (owner == null || !(owner.appliedCustomization?.physic ?? false)) return false;
        var avatar = AvatarPaths.Root(owner);
        var groups = PhysicService.Groups(avatar);
        int chains = groups.Sum(g => g.Chains.Count);
        int bones = PhysicService.Bones(avatar).Count;

        var card = CreateOptionCard("mcb-physic");
        var header = Header(IconGlyph.Bones, "Physic", out var caption);
        card.Add(header);

        var stats = new VisualElement(); stats.AddToClassList("mcb-physic__stats");
        var physBones = Stat(IconGlyph.Sparkle, groups.Count + " PhysBone" + (groups.Count == 1 ? "" : "s") + " · " + chains + " chain" + (chains == 1 ? "" : "s"),
            "Added by the build when physic is on. Plain PhysBones: every player in the world sees the motion.");
        var stripped = Stat(IconGlyph.Broom, bones + " bone" + (bones == 1 ? "" : "s") + " removed",
            "Removed by the build when physic is off: their weights go to the parent bone, so the avatar stays light.");
        stats.Add(physBones); stats.Add(stripped);
        card.Add(stats);

        // The switch flips on pointer down; the scene does not change, only what the build will do.
        ToggleSwitch toggle = null;
        toggle = new ToggleSwitch(owner.physicEnabled, on => SetPhysic(on)) { tooltip = "Secondary motion on the version's physic bones" };
        toggle.AddToClassList("mcb-physic__switch");
        header.Add(toggle);

        void Show(bool on)
        {
            card.EnableInClassList("mcb-physic--on", on);
            toggle.SetValueWithoutNotify(on);
            physBones.EnableInClassList("mcb-physic__stat--active", on);
            stripped.EnableInClassList("mcb-physic__stat--active", !on);
            caption.text = chains == 0 ? "This version supports physic, but this avatar has no physic bones."
                : on ? $"{chains} bone chain{(chains == 1 ? "" : "s")} react to your movement, for everyone in the world."
                : $"Off: the build removes the {bones} physic bone{(bones == 1 ? "" : "s")} to keep your avatar light.";
        }
        Show(owner.physicEnabled);
        toggle.SetEnabled(chains > 0);
        void SetPhysic(bool on)
        {
            if (chains == 0) return;
            Show(on);
            Undo.RecordObject(owner, on ? "Enable physic" : "Disable physic");
            owner.physicEnabled = on;
            EditorUtility.SetDirty(owner);
            budgetPanel?.ScheduleRefresh();
        }
        root.Add(card);
        return true;
    }

    // Parameters, bones, PhysBones and contacts, with the custom base's share: shared with My Avatar.
    private bool BuildBudgetOption(VisualElement root)
    {
        var owner = editor.customBaseTarget;
        if (owner == null) return false;
        var avatar = AvatarPaths.Root(owner).gameObject;
        var card = CreateOptionCard("mcb-budget");
        card.Add(Header(IconGlyph.Gauge, "Avatar budget", out var caption));
        caption.text = "VRChat's limits for PC, with what the custom base adds once built.";
        budgetPanel = new AvatarBudgetPanel(() => AvatarBudget.Estimate(avatar, slidersDrawer.ParameterOptions()));
        card.Add(budgetPanel);
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
