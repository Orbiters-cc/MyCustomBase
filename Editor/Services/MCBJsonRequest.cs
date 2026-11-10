#if UNITY_EDITOR
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit;

/// <summary>Authenticated JSON requests to the Orbiters backend; a refusal throws with the server's error message.</summary>
public static class MCBJsonRequest
{
    /// <exception cref="IOException">The server refused (its "error" message) or did not answer.</exception>
    public static async Task<T> SendAsync<T>(string method, string url, string token, object body, string context)
    {
        try
        {
            return await McbHttp.JsonAsync<T>(new HttpMethod(method), url, token, body, MCBRequestPolicy.Backend(context),
                NetworkService.GetTimeoutSeconds(NetworkRequestType.UserInfo));
        }
        catch (OrbitersHttpException ex)
        {
            string message = ServerError(ex.Body);
            throw new IOException(string.IsNullOrWhiteSpace(message) ? context + " failed (HTTP " + ex.StatusCode + ")." : message, ex);
        }
    }

    /// <summary>The "error" message of a refused request's JSON body, or null.</summary>
    public static string ServerError(string body)
    {
        try { return JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body)["error"]?.ToString(); }
        catch (JsonException) { return null; }
    }
}
#endif
