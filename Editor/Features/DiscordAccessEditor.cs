#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The Discord role rules of an asset, as on Orbiters' Access tab: members holding the ownership role get an access
/// level and receive the destination role in a server the creator manages. A saved asset changes its rules at once;
/// before the asset exists the rules are kept in <c>pending</c> and created with it.
/// </summary>
internal sealed class DiscordAccessEditor : VisualElement
{
    private static readonly string[] Scopes = { "public", "beta", "alpha" };
    private static readonly string[] ScopeLabels = { "Public", "Beta", "Alpha" };
    private static readonly Dictionary<string, DiscordRoleOption[]> RoleCache = new Dictionary<string, DiscordRoleOption[]>();
    private static DiscordServerOption[] serverCache;

    private readonly Func<string> token;
    private readonly int assetId;
    private readonly List<DiscordAccessRule> pending;
    private readonly Action<int> rulesChanged;
    private readonly VisualElement list = new VisualElement();
    private readonly VisualElement form = new VisualElement();
    private readonly Label status;
    private DiscordAccessRule[] saved;
    private string ownerGuild, targetGuild;
    private int ownerRole, targetRole;
    private int scopeIndex;
    private bool both;
    private bool busy;

    public int RuleCount => (assetId > 0 ? saved?.Length : pending?.Count) ?? 0;

    /// <param name="rulesChanged">Receives the rule count after loading and after every change.</param>
    public DiscordAccessEditor(Func<string> token, int assetId, List<DiscordAccessRule> pending, Action<int> rulesChanged)
    {
        this.token = token;
        this.assetId = assetId;
        this.pending = pending;
        this.rulesChanged = rulesChanged;
        list.AddToClassList("mcb-discord-rules");
        Add(list);
        form.AddToClassList("mcb-discord-form");
        Add(form);
        status = McbSectionUi.Note(string.Empty);
        Add(status);
        RenderRules();
        BuildForm();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            SetStatus("Loading your Discord servers…");
            var servers = serverCache ?? (serverCache = await DiscordAccessService.ServersAsync(token()));
            if (assetId > 0) saved = (await DiscordAccessService.RulesAsync(token(), assetId)).rules ?? Array.Empty<DiscordAccessRule>();
            SetStatus(servers.Length == 0 ? "Link your Discord account on Orbiters and join a server to choose its roles." : null);
        }
        catch (Exception ex) { SetStatus(ex.Message, "error"); }
        RenderRules();
        BuildForm();
        rulesChanged?.Invoke(RuleCount);
    }

    private void RenderRules()
    {
        list.Clear();
        var rules = assetId > 0 ? saved : pending?.ToArray();
        if (rules == null) return;
        if (rules.Length == 0)
        {
            list.Add(McbSectionUi.Text("No Discord role gives access to this asset yet.", "mcb-muted"));
            return;
        }
        foreach (var rule in rules)
        {
            var row = new VisualElement();
            row.AddToClassList("mcb-discord-rule");
            var text = new VisualElement();
            text.AddToClassList("mcb-discord-rule__text");
            var name = McbSectionUi.Row();
            name.Add(McbSectionUi.Text(rule.role?.name ?? "Role unavailable", "mcb-discord-rule__name"));
            name.Add(McbSectionUi.Text(ScopeLabels[Math.Max(0, Array.IndexOf(Scopes, rule.scope))], "mcb-discord-rule__scope"));
            text.Add(name);
            text.Add(McbSectionUi.Text("Grants " + (rule.targetRole?.name ?? "an unavailable role") + " · " +
                (rule.direction == "both" ? "the destination role also grants access" : "only the ownership role grants access"), "mcb-discord-rule__detail"));
            if (!string.IsNullOrWhiteSpace(rule.error)) text.Add(McbSectionUi.Text(rule.error, "mcb-discord-rule__detail", "mcb-section__note--warning"));
            row.Add(text);
            row.Add(McbSectionUi.IconAction(IconGlyph.Close, "Remove this rule", () => Remove(rule)));
            list.Add(row);
        }
    }

    private void BuildForm()
    {
        form.Clear();
        var servers = (serverCache ?? Array.Empty<DiscordServerOption>()).Select(s => new KeyValuePair<string, string>(s.guildId, s.Label)).ToArray();
        form.Add(McbSectionUi.Text("Add a rule", "mcb-section__group-title", "mcb-section__group-title--first"));
        form.Add(Pair("Ownership", servers, ownerGuild, ownerRole, (guild, role) => { ownerGuild = guild; ownerRole = role; }));
        form.Add(Pair("Destination", servers, targetGuild, targetRole, (guild, role) => { targetGuild = guild; targetRole = role; }));
        form.Add(McbSectionUi.Text("The destination is a server you manage: members receive its role. Orbiters never assigns the ownership role.", "mcb-muted"));
        var scope = new SegmentedControl(ScopeLabels, index => scopeIndex = index);
        scope.SetIndex(scopeIndex);
        scope.tooltip = "Access level the ownership role grants";
        form.Add(scope);
        var direction = new SegmentedControl(new[] { "Ownership role only", "Both roles grant access" }, index => both = index == 1);
        direction.SetIndex(both ? 1 : 0);
        form.Add(direction);
        var actions = McbSectionUi.Row("mcb-actions");
        var add = McbSectionUi.Pill(busy ? "Saving…" : "Add rule", Add, "primary", "first");
        add.SetEnabled(!busy && ownerRole > 0 && targetRole > 0 && ownerRole != targetRole);
        actions.Add(add);
        if (assetId > 0)
            actions.Add(McbSectionUi.Pill("Open Access tab on Orbiters", () =>
                Application.OpenURL(MCBUtils.getWebsiteUrl() + "assets/" + assetId + "/config?tab=access")));
        form.Add(actions);
        form.Add(McbSectionUi.Text("Only named roles appear. Name a server's roles on Orbiters (asset Access tab, \"Missing a role?\").", "mcb-muted"));
    }

    private VisualElement Pair(string label, KeyValuePair<string, string>[] servers, string guild, int role, Action<string, int> chosen)
    {
        var pair = new VisualElement();
        pair.AddToClassList("mcb-discord-form__pair");
        var roleField = new SearchableDropdownField(label + " role", label + " role", Roles(guild), role > 0 ? role.ToString() : null,
            key => { chosen(guild, int.TryParse(key, out int id) ? id : 0); BuildForm(); },
            guild == null ? "Choose a server first" : "Search roles");
        var serverField = new SearchableDropdownField(label + " server", label + " server", servers, guild, key =>
        {
            chosen(key, 0);
            _ = LoadRolesAsync(key);
        }, "Search servers");
        roleField.SetEnabled(guild != null);
        pair.Add(serverField);
        pair.Add(roleField);
        return pair;
    }

    private static IEnumerable<KeyValuePair<string, string>> Roles(string guild) =>
        guild != null && RoleCache.TryGetValue(guild, out var roles)
            ? roles.Where(r => r.IsNamed && r.discordRoleId != guild).Select(r => new KeyValuePair<string, string>(r.id.ToString(), r.name))
            : Enumerable.Empty<KeyValuePair<string, string>>();

    private async Task LoadRolesAsync(string guild)
    {
        BuildForm();
        if (guild == null || RoleCache.ContainsKey(guild)) return;
        try
        {
            SetStatus("Loading roles…");
            RoleCache[guild] = await DiscordAccessService.RolesAsync(token(), guild);
            SetStatus(RoleCache[guild].Any(r => r.IsNamed) ? null : "This server has no named roles yet. Name them on Orbiters first.", "warning");
        }
        catch (Exception ex) { SetStatus(ex.Message, "error"); }
        BuildForm();
    }

    private async void Add()
    {
        var rule = new DiscordAccessRule { roleId = ownerRole, targetRoleId = targetRole, scope = Scopes[scopeIndex], direction = both ? "both" : "import",
            role = FindRole(ownerGuild, ownerRole), targetRole = FindRole(targetGuild, targetRole) };
        if (assetId <= 0)
        {
            if (pending.Any(r => r.roleId == rule.roleId && r.scope == rule.scope)) { SetStatus("This role already has a rule for that access level.", "warning"); return; }
            pending.Add(rule);
            ownerRole = targetRole = 0;
            Changed();
            return;
        }
        await Run(async () => saved = (await DiscordAccessService.AddRuleAsync(token(), assetId, rule)).rules);
        ownerRole = targetRole = 0;
        BuildForm();
    }

    private async void Remove(DiscordAccessRule rule)
    {
        if (assetId <= 0) { pending.Remove(rule); Changed(); return; }
        await Run(async () => saved = (await DiscordAccessService.RemoveRuleAsync(token(), assetId, rule.id)).rules);
    }

    private async Task Run(Func<Task> task)
    {
        if (busy) return;
        busy = true; BuildForm(); SetStatus(null);
        try { await task(); }
        catch (Exception ex) { SetStatus(ex.Message, "error"); }
        finally { busy = false; Changed(); }
    }

    private void Changed()
    {
        RenderRules();
        BuildForm();
        rulesChanged?.Invoke(RuleCount);
    }

    private static DiscordRoleOption FindRole(string guild, int id) =>
        guild != null && RoleCache.TryGetValue(guild, out var roles) ? roles.FirstOrDefault(r => r.id == id) : null;

    private void SetStatus(string text, string variant = null)
    {
        status.text = text ?? string.Empty;
        status.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        status.EnableInClassList("mcb-section__note--error", variant == "error");
        status.EnableInClassList("mcb-section__note--warning", variant == "warning");
    }

    /// <summary>Creates the rules chosen before the asset existed. Throws with the first failure.</summary>
    public static async Task SavePendingAsync(string token, int assetId, IEnumerable<DiscordAccessRule> rules)
    {
        foreach (var rule in rules ?? Enumerable.Empty<DiscordAccessRule>()) await DiscordAccessService.AddRuleAsync(token, assetId, rule);
    }
}
#endif
