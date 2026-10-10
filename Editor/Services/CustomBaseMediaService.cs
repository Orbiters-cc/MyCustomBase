#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

/// <summary>An owned custom base's gallery thumbnail and banner (the photoshoot panel and MCP authoring).</summary>
public static class CustomBaseMediaService
{
    public static string Url(int assetId) => MCBUtils.getApiUrl() + "/assets/" + assetId + "/media";

    /// <summary>Uploads PNG/JPEG files as the asset's thumbnail and/or banner; the server answers with the updated asset.</summary>
    public static async Task<JObject> UploadAsync(string token, int assetId, string thumbnailPath, string bannerPath)
    {
        var form = new List<IMultipartFormSection>();
        void Add(string field, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("Image not found.", path);
            string extension = Path.GetExtension(full).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") throw new ArgumentException("Use a PNG or JPEG image: " + path);
            form.Add(new MultipartFormFileSection(field, File.ReadAllBytes(full), Path.GetFileName(full), extension == ".png" ? "image/png" : "image/jpeg"));
        }
        Add("thumbnail", thumbnailPath);
        Add("banner", bannerPath);
        if (form.Count == 0) throw new ArgumentException("Provide thumbnailPath and/or bannerPath.");
        string url = Url(assetId);
        using (var request = UnityWebRequest.Post(url, form))
        {
            MCBRequestHeaders.SetAuthorization(request, token);
            request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.Upload);
            await MCBManagedRequest.SendUnityWebRequestAsync(request, url, MCBRequestPolicy.Backend("Update asset media"));
            if (request.result != UnityWebRequest.Result.Success)
            {
                string message = null;
                try { message = JObject.Parse(request.downloadHandler?.text ?? "{}")["error"]?.ToString(); } catch (Newtonsoft.Json.JsonException) { }
                throw new IOException(message ?? "Update asset media failed (HTTP " + request.responseCode + ").");
            }
            AvatarAssetDiscoveryService.InvalidateDiscoveryCache();
            return JObject.Parse(request.downloadHandler.text);
        }
    }
}
#endif
