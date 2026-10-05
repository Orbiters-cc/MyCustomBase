using System.Collections.Generic;
using UnityEngine.Networking;

/// <summary>Shared multipart registration endpoint for creator UI and automation.</summary>
public static class CustomBaseRegistration
{
    public static List<IMultipartFormSection> Form(string metadata) => new List<IMultipartFormSection> { new MultipartFormDataSection("metadata", metadata) };
    public static string Url => MCBUtils.getApiUrl() + "/assets/custom-base";
    public static UnityWebRequest Request(List<IMultipartFormSection> form, string authToken, string requestId)
    {
        var request = UnityWebRequest.Post(Url, form);
        MCBRequestHeaders.SetAuthorization(request, authToken);
        MCBRequestHeaders.SetIdempotencyKey(request, requestId);
        request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.Upload);
        return request;
    }
}
