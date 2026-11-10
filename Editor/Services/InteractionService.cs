#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit;

public static class InteractionTypes
{
    public const string LIKE = "LIKE";
    public const string COMMENT = "COMMENT";
}

[JsonObject(MemberSerialization.OptIn)]
public class InteractionRecord
{
    [JsonProperty] public int id;
    [JsonProperty("from")] public int fromUserId;
    [JsonProperty] public int? toUser;
    [JsonProperty] public int? toComment;
    [JsonProperty] public int? toAsset;
    [JsonProperty] public string type;
    [JsonProperty] public string content;
    [JsonProperty] public string createdAt;
    [JsonProperty] public string fromUsername;
    [JsonProperty] public string fromUserAvatarUrl;
}

[JsonObject(MemberSerialization.OptIn)]
public class AssetInteractionsResponse
{
    [JsonProperty] public List<InteractionRecord> interactions = new List<InteractionRecord>();
    [JsonProperty] public int likeCount;
    [JsonProperty] public int commentCount;
    [JsonProperty] public bool likedByCurrentUser;
    [JsonProperty] public int? currentUserLikeId;

    public void NormalizeForViewer(int currentUserId)
    {
        interactions = interactions ?? new List<InteractionRecord>();

        int likesFromList = interactions.Count(IsLike);
        int commentsFromList = interactions.Count(IsComment);
        if (likesFromList > 0 || likeCount == 0)
        {
            likeCount = likesFromList;
        }
        if (commentsFromList > 0 || commentCount == 0)
        {
            commentCount = commentsFromList;
        }

        if (currentUserId > 0)
        {
            var viewerLike = interactions.FirstOrDefault(item => IsLike(item) && item.fromUserId == currentUserId);
            likedByCurrentUser = viewerLike != null || likedByCurrentUser;
            if (viewerLike != null && viewerLike.id > 0)
            {
                currentUserLikeId = viewerLike.id;
            }
        }

        foreach (var interaction in interactions)
        {
            if (interaction == null || interaction.fromUserId <= 0)
            {
                continue;
            }

            UserService.UpdateUserInfo(interaction.fromUserId, interaction.fromUsername, interaction.fromUserAvatarUrl);
        }
    }

    private static bool IsLike(InteractionRecord interaction)
    {
        return interaction != null && string.Equals(interaction.type, InteractionTypes.LIKE, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsComment(InteractionRecord interaction)
    {
        return interaction != null && string.Equals(interaction.type, InteractionTypes.COMMENT, StringComparison.OrdinalIgnoreCase);
    }
}

[JsonObject(MemberSerialization.OptIn)]
public class CreateInteractionRequest
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? toUser;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? toComment;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? toAsset;
    [JsonProperty] public string type;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string content;
}

[JsonObject(MemberSerialization.OptIn)]
public class CreateInteractionResponse
{
    [JsonProperty] public InteractionRecord interaction;
}

public static class InteractionService
{
    public static IEnumerator LoadAssetInteractionsCoroutine(
        int assetId,
        string authToken,
        int currentUserId,
        Action<AssetInteractionsResponse, string> onComplete)
    {
        if (assetId <= 0)
        {
            onComplete?.Invoke(null, "Missing asset id.");
            yield break;
        }

        string url = $"{MCBUtils.getApiUrl()}/assets/{assetId}/interactions";
        var call = SendAsync<AssetInteractionsResponse>(HttpMethod.Get, url, authToken, null, "Load interactions", "Failed to load interactions");
        while (!call.IsCompleted) yield return null;
        var reply = call.Result;
        if (reply.Error != null)
        {
            onComplete?.Invoke(null, reply.Error);
            yield break;
        }

        try
        {
            if (reply.ParseError != null) throw reply.ParseError;
            var response = reply.Value ?? new AssetInteractionsResponse();
            response.NormalizeForViewer(currentUserId);
            onComplete?.Invoke(response, null);
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(null, $"Failed to parse interactions: {ex.Message}");
        }
    }

    public static IEnumerator CreateInteractionCoroutine(
        CreateInteractionRequest payload,
        string authToken,
        Action<InteractionRecord, string> onComplete)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.type))
        {
            onComplete?.Invoke(null, "Missing interaction payload.");
            yield break;
        }

        string url = $"{MCBUtils.getApiUrl()}/interactions";
        var call = SendAsync<CreateInteractionResponse>(HttpMethod.Post, url, authToken, payload, "Create interaction", "Failed to create interaction");
        while (!call.IsCompleted) yield return null;
        var reply = call.Result;
        if (reply.Error != null)
        {
            onComplete?.Invoke(null, reply.Error);
            yield break;
        }

        try
        {
            if (reply.ParseError != null) throw reply.ParseError;
            if (reply.Value?.interaction == null || reply.Value.interaction.id <= 0)
            {
                throw new InvalidOperationException("The server did not return an interaction.");
            }

            onComplete?.Invoke(reply.Value.interaction, null);
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(null, $"Failed to parse created interaction: {ex.Message}");
        }
    }

    public static IEnumerator DeleteInteractionCoroutine(int interactionId, string authToken, Action<string> onComplete)
    {
        if (interactionId <= 0)
        {
            onComplete?.Invoke("Missing interaction id.");
            yield break;
        }

        string url = $"{MCBUtils.getApiUrl()}/interactions/{interactionId}";
        var call = SendAsync<JToken>(HttpMethod.Delete, url, authToken, null, "Delete interaction", "Failed to delete interaction");
        while (!call.IsCompleted) yield return null;
        // The answer's body is not read: deleted is deleted.
        onComplete?.Invoke(call.Result.Error);
    }

    public static IEnumerator UpdateInteractionCoroutine(
        int interactionId,
        string content,
        string authToken,
        Action<InteractionRecord, string> onComplete)
    {
        if (interactionId <= 0)
        {
            onComplete?.Invoke(null, "Missing interaction id.");
            yield break;
        }

        var payload = new CreateInteractionRequest
        {
            type = InteractionTypes.COMMENT,
            content = content
        };

        string url = $"{MCBUtils.getApiUrl()}/interactions/{interactionId}";
        var call = SendAsync<CreateInteractionResponse>(HttpMethod.Put, url, authToken, payload, "Update interaction", "Failed to update interaction");
        while (!call.IsCompleted) yield return null;
        var reply = call.Result;
        if (reply.Error != null)
        {
            onComplete?.Invoke(null, reply.Error);
            yield break;
        }

        try
        {
            if (reply.ParseError != null) throw reply.ParseError;
            if (reply.Value?.interaction == null || reply.Value.interaction.id <= 0)
            {
                throw new InvalidOperationException("The server did not return an interaction.");
            }

            onComplete?.Invoke(reply.Value.interaction, null);
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(null, $"Failed to parse updated interaction: {ex.Message}");
        }
    }

    // A request's outcome: the answer, or the error to show (Error), or an answer that is not what was asked (ParseError).
    private sealed class Reply<T>
    {
        public T Value;
        public string Error;
        public Exception ParseError;
    }

    // Never throws: the coroutines above read the outcome.
    private static async Task<Reply<T>> SendAsync<T>(HttpMethod method, string url, string authToken, object payload, string context, string failure)
    {
        try
        {
            return new Reply<T>
            {
                Value = await McbHttp.JsonAsync<T>(method, url, authToken, payload, MCBRequestPolicy.Backend(context),
                    NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery))
            };
        }
        catch (OrbitersHttpException ex) { return new Reply<T> { Error = BuildRequestError(failure, ex) }; }
        catch (JsonException ex) { return new Reply<T> { ParseError = ex }; }
        catch (Exception ex) when (ex is UriFormatException || ex is ArgumentException) { return new Reply<T> { Error = $"{failure}: {ex.Message}" }; }
    }

    private static string BuildRequestError(string prefix, OrbitersHttpException error)
    {
        string message = null;
        try
        {
            var payload = JsonConvert.DeserializeObject<JObject>(error.Body ?? "{}");
            message = payload?.Value<string>("error") ?? payload?.Value<string>("message");
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is FormatException)
        {
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return $"{prefix}: {message}";
        }

        return $"{prefix}: {error.Message}";
    }
}
#endif
