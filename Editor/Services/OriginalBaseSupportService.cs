#if UNITY_EDITOR
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

/// <summary>Creator-only registration and historical version support, using immutable downloaded payloads.</summary>
public static class OriginalBaseSupportService
{
    public static string Url(int assetId, string suffix, string token) => MCBUtils.getApiUrl() + "/" + assetId + suffix + "?t=" + Uri.EscapeDataString(token);
    public static async Task<OriginalBaseVersionData[]> Load(int assetId, string token)
    {
        var result = await new NetworkService().DownloadBytesAsync(Url(assetId, "/source-versions", token));
        if (!result.success) throw new IOException(result.error);
        return JObject.Parse(System.Text.Encoding.UTF8.GetString(result.data))["sourceVersions"].ToObject<OriginalBaseVersionData[]>();
    }
    public static async Task<OriginalBaseVersionData[]> Register(int assetId, string token, OriginalBaseVersionData[] versions)
    {
        string url = Url(assetId, "/source-versions", token);
        using (var request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { sourceVersions = versions })));
            request.downloadHandler = new DownloadHandlerBuffer(); request.SetRequestHeader("Content-Type", "application/json");
            await MCBManagedRequest.SendUnityWebRequestAsync(request, url, MCBRequestPolicy.Backend("Register original base versions"));
            if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.downloadHandler.text);
            return JObject.Parse(request.downloadHandler.text)["sourceVersions"].ToObject<OriginalBaseVersionData[]>();
        }
    }
    public static async Task AddHistoricalSupport(int assetId, string token, CustomBaseVersion historical, OriginalBaseVersionData[] targets, Action<string> progress)
    {
        var supported = historical.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>();
        targets = targets.Where(t => !supported.Any(s => s.key == t.key)).ToArray();
        if (targets.Length == 0) return;
        var version = JsonConvert.DeserializeObject<CustomBaseVersion>(JsonConvert.SerializeObject(historical));
        var readable = supported.FirstOrDefault(s => s.sourceFiles.All(CanResolve));
        if (readable != null) { version.sourceFiles = readable.sourceFiles; version.versionFiles = readable.versionFiles; version.deliveryVariants = readable.deliveryVariants; version.meshDelivery = readable.meshDelivery; version.sourceVersionKey = readable.key; }
        // Historical listings include every registered key. Only the version's referenced originals qualify.
        version.sourceFiles = version.sourceFiles.Where(s => version.versionFiles.Any(f => f.sourceModelFileId == s.id ||
            (!(f.sourceModelFileId > 0) && Convert.ToString(f.metadata?["sourcePath"]) == s.path && (!f.metadata.ContainsKey("sourceHash") || Convert.ToString(f.metadata["sourceHash"]) == s.hash)))).ToArray();
        if (!version.sourceFiles.Any()) throw new InvalidDataException("Historical version has no original-source bindings.");
        foreach (var source in version.sourceFiles) OriginalBaseLibrary.Resolve(source);
        string staging = Path.Combine(Path.GetTempPath(), "mcb-source-support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string url = Url(assetId, "/model", token) + "&version=" + Uri.EscapeDataString(version.version) + "&d=" + version.sourceFiles[0].hash + "&sourceKey=" + version.sourceVersionKey;
            var network = new NetworkService(); string zip = Path.Combine(staging, "download.zip"); string folder = Path.Combine(staging, "content");
            progress?.Invoke("Downloading saved version " + version.version + "…");
            var download = await network.DownloadFileAsync(url + (version.meshDelivery > 0 ? "&meshCommon=1" : ""), zip);
            if (!download.success) throw new IOException(download.error);
            ZipFile.ExtractToDirectory(zip, folder);
            if (version.meshDelivery > 0)
                foreach (var patch in version.versionFiles.Where(p => p.transform == NativeMeshPayloadService.TransformName))
                    foreach (var codec in MCBVersionDelivery.GetVariants(patch))
                    {
                        string destination = SafePath(folder, codec.path);
                        var blob = await network.DownloadFileAsync(url + "&meshBlob=" + codec.hash, destination);
                        if (!blob.success) throw new IOException(blob.error);
                        if (MCBUtils.CalculateFileHash(destination) != codec.hash) throw new InvalidDataException("Downloaded historical mesh failed verification.");
                    }
            else if (File.Exists(Path.Combine(folder, MCBVersionDelivery.ManifestName)))
                MCBVersionDelivery.ApplyManifest(version, File.ReadAllText(Path.Combine(folder, MCBVersionDelivery.ManifestName)));
            progress?.Invoke("Encrypting " + version.version + " for " + targets.Length + " original version(s)…");
            var variants = targets.Select(target => OriginalBaseVariantBuilder.Build(folder, version, target)).ToArray();
            foreach (string local in new[] { "version.json", "manifest.json", MCBVersionDelivery.ManifestName })
                if (File.Exists(Path.Combine(folder, local))) File.Delete(Path.Combine(folder, local));
            string uploadZip = Path.Combine(staging, "support.zip"); ZipFile.CreateFromDirectory(folder, uploadZip);
            var upload = await network.SubmitNewVersionStreamingAsync(Url(assetId, "/versions/" + Uri.EscapeDataString(version.version) + "/source-support", token), token,
                uploadZip, JsonConvert.SerializeObject(new { originalBaseVersions = variants }), (value, bytes) => progress?.Invoke("Uploading " + version.version + "… " + Math.Round(value * 100) + "%"), CancellationToken.None);
            if (!upload.success) throw new IOException(upload.error);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    static bool CanResolve(ModelFileData source) { try { OriginalBaseLibrary.Resolve(source); return true; } catch { return false; } }
    static string SafePath(string folder, string relative)
    {
        if (Path.GetFileName(relative) != relative || relative.Contains(":")) throw new InvalidDataException("Unsafe payload path.");
        return Path.Combine(folder, relative);
    }
}
#endif
