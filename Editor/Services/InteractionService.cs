#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

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

        string url = $"{MCBUtils.getApiUrl()}/assets/{assetId}/interactions?t={authToken}";
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery);
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Load interactions"));

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(null, BuildRequestError("Failed to load interactions", request));
                yield break;
            }

            try
            {
                var response = JsonConvert.DeserializeObject<AssetInteractionsResponse>(request.downloadHandler.text);
                response = response ?? new AssetInteractionsResponse();
                response.NormalizeForViewer(currentUserId);
                onComplete?.Invoke(response, null);
            }
            catch (Exception ex)
            {
                onComplete?.Invoke(null, $"Failed to parse interactions: {ex.Message}");
            }
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

        string url = $"{MCBUtils.getApiUrl()}/interactions?t={authToken}";
        string json = JsonConvert.SerializeObject(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        using (var request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery);
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Create interaction"));

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(null, BuildRequestError("Failed to create interaction", request));
                yield break;
            }

            try
            {
                var response = JsonConvert.DeserializeObject<CreateInteractionResponse>(request.downloadHandler.text);
                if (response?.interaction == null || response.interaction.id <= 0)
                {
                    throw new InvalidOperationException("The server did not return an interaction.");
                }

                onComplete?.Invoke(response.interaction, null);
            }
            catch (Exception ex)
            {
                onComplete?.Invoke(null, $"Failed to parse created interaction: {ex.Message}");
            }
        }
    }

    public static IEnumerator DeleteInteractionCoroutine(int interactionId, string authToken, Action<string> onComplete)
    {
        if (interactionId <= 0)
        {
            onComplete?.Invoke("Missing interaction id.");
            yield break;
        }

        string url = $"{MCBUtils.getApiUrl()}/interactions/{interactionId}?t={authToken}";
        using (var request = UnityWebRequest.Delete(url))
        {
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery);
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Delete interaction"));

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(BuildRequestError("Failed to delete interaction", request));
                yield break;
            }

            onComplete?.Invoke(null);
        }
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

        string url = $"{MCBUtils.getApiUrl()}/interactions/{interactionId}?t={authToken}";
        string json = JsonConvert.SerializeObject(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        using (var request = new UnityWebRequest(url, "PUT"))
        {
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery);
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Update interaction"));

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(null, BuildRequestError("Failed to update interaction", request));
                yield break;
            }

            try
            {
                var response = JsonConvert.DeserializeObject<CreateInteractionResponse>(request.downloadHandler.text);
                if (response?.interaction == null || response.interaction.id <= 0)
                {
                    throw new InvalidOperationException("The server did not return an interaction.");
                }

                onComplete?.Invoke(response.interaction, null);
            }
            catch (Exception ex)
            {
                onComplete?.Invoke(null, $"Failed to parse updated interaction: {ex.Message}");
            }
        }
    }

    private static string BuildRequestError(string prefix, UnityWebRequest request)
    {
        string message = null;
        try
        {
            var payload = JsonConvert.DeserializeObject<JObject>(request.downloadHandler?.text ?? "{}");
            message = payload?.Value<string>("error") ?? payload?.Value<string>("message");
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return $"{prefix}: {message}";
        }

        return $"{prefix}: HTTP {request.responseCode} {request.error}";
    }
}
#endif
