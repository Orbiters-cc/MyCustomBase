#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using UnityEngine.UIElements;

/// <summary>
/// An asset's protection and Discord role access. Trusted creators choose whether downloads verify a Discord role and,
/// with it, whether versions skip XOR encryption: one unencrypted package then serves every original base. Its versions
/// inherit the choice. A saved asset changes at once; before the asset exists the choice is kept for its creation.
/// </summary>
internal sealed class AssetProtectionEditor : VisualElement
{
    private readonly Func<string> token;
    private readonly int assetId;
    private readonly bool trusted;
    private readonly Action<VersionProtection> changed;
    private readonly VisualElement settings = new VisualElement();
    private readonly Label status;
    private VersionProtection protection;
    private int ruleCount = -1;
    private bool saving;

    /// <param name="assetId">0 while creating the asset: <paramref name="protection"/> and <paramref name="pendingRules"/> are edited in place.</param>
    public AssetProtectionEditor(Func<string> token, int assetId, bool trusted, VersionProtection protection, List<DiscordAccessRule> pendingRules,
        Action<VersionProtection> changed)
    {
        this.token = token;
        this.assetId = assetId;
        this.trusted = trusted;
        this.protection = protection ?? new VersionProtection();
        this.changed = changed;
        AddToClassList("mcb-asset-protection");
        Add(settings);
        status = McbSectionUi.Note(string.Empty);
        status.style.display = DisplayStyle.None;
        Add(status);
        var rulesTitle = McbSectionUi.Text("Discord role access", "mcb-section__group-title");
        Add(rulesTitle);
        Add(McbSectionUi.Text("Members holding an ownership role get an access level to this custom base and receive the destination role in your server.", "mcb-muted"));
        Add(new DiscordAccessEditor(token, assetId, pendingRules, count => { ruleCount = count; Render(); }));
        Render();
    }

    private void Render()
    {
        settings.Clear();
        if (!trusted) return;
        var verify = new ToggleSwitch(protection.discordRole, value => Change(new VersionProtection { discordRole = value, xor = !value || protection.xor }));
        verify.SetEnabled(!saving);
        string verifyText = !protection.discordRole ? "Only members holding one of this asset's Discord role rules can download its versions."
            : ruleCount == 0 ? "Add a Discord role rule below: versions cannot be published before one exists."
            : "Only members holding one of this asset's Discord role rules can download its versions.";
        settings.Add(McbSectionUi.Setting("Verify a Discord role", verifyText, verify, first: true));
        var xor = new ToggleSwitch(protection.xor, value => Change(new VersionProtection { discordRole = protection.discordRole, xor = value }));
        xor.SetEnabled(protection.discordRole && !saving);
        settings.Add(McbSectionUi.Setting("Protect with XOR encryption", protection.xor
            ? "Each supported original receives its own copy, encrypted with that original model: only its owners can use it." +
              (protection.discordRole ? " Turn off to publish one package for every original." : " Verify a Discord role to turn this off.")
            : "One unencrypted package works on any avatar with the version's skeleton, including other custom bases. The Discord role is the only download check: choose an ownership role that proves the original was bought.",
            xor));
        if (protection.discordRole && ruleCount == 0) settings.Q<Label>(className: "mcb-setting__detail")?.AddToClassList("mcb-section__note--warning");
    }

    private async void Change(VersionProtection next)
    {
        var previous = protection;
        protection = next;
        Render();
        if (assetId <= 0) { changed?.Invoke(protection); return; }
        // Optimistic: the switches already show the new state; a failed save puts the previous one back.
        saving = true;
        SetStatus(null);
        try
        {
            protection = await DiscordAccessService.SaveProtectionAsync(token(), assetId, next);
            changed?.Invoke(protection);
        }
        catch (Exception ex)
        {
            protection = previous;
            SetStatus(ex.Message);
        }
        saving = false;
        Render();
    }

    private void SetStatus(string message)
    {
        status.text = message ?? string.Empty;
        status.EnableInClassList("mcb-section__note--error", !string.IsNullOrEmpty(message));
        status.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
    }
}

internal static class AssetProtectionExtensions
{
    /// <summary>Versions of this asset ship one unencrypted package for every original instead of per-original XOR copies.</summary>
    public static bool UsesPlainPackages(this AvatarDiscoveredAsset asset) => asset?.protection != null && !asset.protection.xor;
}
#endif
