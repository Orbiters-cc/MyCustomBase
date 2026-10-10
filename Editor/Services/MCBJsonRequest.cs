#if UNITY_EDITOR
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

/// <summary>Authenticated JSON requests to the Orbiters backend; a refusal throws with the server's error message.</summary>
public static class MCBJsonRequest
{
    public static async Task<T> SendAsync<T>(string method, string url, string token, object body, string context)
    {
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
