#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

[JsonObject(MemberSerialization.OptIn)]
public sealed class DiscordServerOption
{
    [JsonProperty] public string guildId;
    [JsonProperty] public string name;
    public string Label => string.IsNullOrWhiteSpace(name) ? "Unnamed server" : name;
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class DiscordRoleOption
{
    [JsonProperty] public int id;
    [JsonProperty] public string discordRoleId;
    [JsonProperty] public string name;
    [JsonProperty] public string color;
    // A role imported without its label only has its snowflake: it cannot be chosen until it is named.
    public bool IsNamed => !string.IsNullOrWhiteSpace(name) && name != discordRoleId;
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class DiscordAccessRule
{
    [JsonProperty] public int id;
    [JsonProperty] public int roleId;
    [JsonProperty] public int targetRoleId;
    [JsonProperty] public string scope = "public";
    [JsonProperty] public string direction = "import";
    [JsonProperty] public string error;
    [JsonProperty] public DiscordRoleOption role;
    [JsonProperty] public DiscordRoleOption targetRole;
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class DiscordAccessRules
{
    [JsonProperty] public DiscordAccessRule[] rules = Array.Empty<DiscordAccessRule>();
}

/// <summary>
/// Discord role rules of a creator's asset: members holding the ownership role get an access level to the asset and
/// receive the destination role. The same rules Orbiters shows under the asset's Access tab.
/// </summary>
public static class DiscordAccessService
{
    public static Task<DiscordServerOption[]> ServersAsync(string token) =>
        SendAsync<DiscordServerOption[]>("GET", "/discord-servers", token, null, "Load Discord servers");

    public static Task<DiscordRoleOption[]> RolesAsync(string token, string guildId) =>
        SendAsync<DiscordRoleOption[]>("GET", "/discord-servers/" + Uri.EscapeDataString(guildId) + "/roles", token, null, "Load Discord roles");

    public static Task<DiscordAccessRules> RulesAsync(string token, int assetId) =>
        SendAsync<DiscordAccessRules>("GET", "/" + assetId + "/discord-access", token, null, "Load Discord role rules");

    public static Task<DiscordAccessRules> AddRuleAsync(string token, int assetId, DiscordAccessRule rule) =>
        SendAsync<DiscordAccessRules>("POST", "/" + assetId + "/discord-access", token,
            new { roleId = rule.roleId, targetRoleId = rule.targetRoleId, scope = rule.scope, direction = rule.direction }, "Add Discord role rule");

    public static Task<DiscordAccessRules> RemoveRuleAsync(string token, int assetId, int ruleId) =>
        SendAsync<DiscordAccessRules>("DELETE", "/" + assetId + "/discord-access/" + ruleId, token, null, "Remove Discord role rule");

    /// <summary>Trusted creators: whether the asset's versions verify a Discord role, and whether they are XOR-encrypted.</summary>
    public static async Task<VersionProtection> SaveProtectionAsync(string token, int assetId, VersionProtection protection) =>
        (await SendAsync<JObject>("PUT", "/" + assetId + "/protection", token, new { xor = protection.xor, discordRole = protection.discordRole }, "Save protection"))
            ["protection"]?.ToObject<VersionProtection>() ?? throw new IOException("The server returned no protection.");

    private static async Task<T> SendAsync<T>(string method, string path, string token, object body, string context)
    {
        string url = MCBUtils.getApiUrl() + path;
        using (var request = new UnityWebRequest(url, method))
        {
            MCBRequestHeaders.SetAuthorization(request, token);
            request.downloadHandler = new DownloadHandlerBuffer();
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.UserInfo);
            await MCBManagedRequest.SendUnityWebRequestAsync(request, url, MCBRequestPolicy.Backend(context));
            string text = request.downloadHandler?.text;
            if (request.result != UnityWebRequest.Result.Success)
            {
                string message = null;
                try { message = JObject.Parse(text ?? "{}")["error"]?.ToString(); } catch (JsonException) { }
                throw new IOException(string.IsNullOrWhiteSpace(message) ? context + " failed (HTTP " + request.responseCode + ")." : message);
            }
            return JsonConvert.DeserializeObject<T>(text);
        }
    }
}
#endif
