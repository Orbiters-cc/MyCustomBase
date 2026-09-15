#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;

public static class MCBBannerRecoveryTests
{
    public static IEnumerator RunOrThrow()
    {
        var window = ScriptableObject.CreateInstance<MCBAdvancedModeWindow>();
        try
        {
            window.CreateGUI();
            Check(window.rootVisualElement.Q<Button>("mcb-reload-versions-banners") != null,
                "Advanced settings reload button is missing.");
            Check(window.rootVisualElement.Q<IMGUIContainer>() != null,
                "Existing advanced controls were lost.");
        }
        finally { UnityEngine.Object.DestroyImmediate(window); }

        var versions = AsyncVersionService.Instance;
        var instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var inflight = (Dictionary<string, Task>)typeof(AsyncVersionService).GetField("inflightFetches", instanceFlags).GetValue(versions);
        var queued = (HashSet<string>)typeof(AsyncVersionService).GetField("pendingForcedRefreshes", instanceFlags).GetValue(versions);
        string fakePath = System.IO.Path.GetFullPath("banner-validation.fbx");
        string fetchKey = fakePath + "|test-token|987654319";
        inflight.Add(fetchKey, new TaskCompletionSource<bool>().Task);
        try
        {
            versions.StartVersionFetchInBackground(fakePath, "test-token", 987654319, false);
            versions.StartVersionFetchInBackground(fakePath, "test-token", 987654319, false);
            Check(queued.Contains(fetchKey), "Manual reload was dropped behind an in-flight version request.");
        }
        finally { inflight.Remove(fetchKey); queued.Remove(fetchKey); }

        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        string url = "http://127.0.0.1:" + port + "/";
        var listener = new HttpListener(); listener.Prefixes.Add(url); listener.Start();
        var image = new Texture2D(2, 2); image.SetPixels(new[] { Color.red, Color.red, Color.red, Color.red }); image.Apply();
        byte[] bytes = image.EncodeToPNG();
        int requests = 0;
        var releaseSlow = new TaskCompletionSource<bool>();
        var slowStarted = new TaskCompletionSource<bool>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    int number = Interlocked.Increment(ref requests);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (context.Request.Url.AbsolutePath == "/slow")
                            { slowStarted.TrySetResult(true); await releaseSlow.Task; context.Response.StatusCode = 503; }
                            else if (number == 1) context.Response.StatusCode = 503;
                            else { context.Response.ContentType = "image/png"; await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); }
                            context.Response.Close();
                        }
                        catch (Exception) { }
                    });
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        });
        var asset = new AvatarDiscoveredAsset { id = 987654319, bannerUrl = url + "banner" };
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var service = typeof(AvatarAssetDiscoveryService);
        try
        {
            AvatarAssetDiscoveryService.ReloadImages();
            AvatarAssetDiscoveryService.GetBanner(asset);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!AvatarAssetDiscoveryService.IsBannerRetryPending(asset) && DateTime.UtcNow < deadline) yield return null;
            Check(AvatarAssetDiscoveryService.IsBannerRetryPending(asset), "Failed banner was not marked retryable.");
            Check(AvatarAssetDiscoveryService.GetBanner(asset) == null && requests == 1, "Cooldown did not suppress retry traffic.");
            var gate = service.GetField("FailedImageDownloads", flags).GetValue(null);
            var retryAt = (Dictionary<string, DateTime>)gate.GetType().GetField("retryAt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(gate);
            foreach (var key in new List<string>(retryAt.Keys)) retryAt[key] = DateTime.UtcNow.AddSeconds(-1);
            Texture2D recovered = null;
            deadline = DateTime.UtcNow.AddSeconds(15);
            while ((recovered = AvatarAssetDiscoveryService.GetBanner(asset)) == null && DateTime.UtcNow < deadline) yield return null;
            Check(recovered != null && requests == 2, "Banner did not recover after cooldown.");
            Check(recovered.hideFlags == HideFlags.HideAndDontSave, "Recovered banner is not retained.");

            image.SetPixels(new[] { Color.blue, Color.blue, Color.blue, Color.blue }); image.Apply(); bytes = image.EncodeToPNG();
            AvatarAssetDiscoveryService.ReloadImages();
            Texture2D fresh = null;
            deadline = DateTime.UtcNow.AddSeconds(15);
            while ((fresh = AvatarAssetDiscoveryService.GetBanner(asset)) == null && DateTime.UtcNow < deadline) yield return null;
            Check(fresh != null && requests == 3 && fresh.GetPixel(0, 0).b > 0.9f, "Reload reused the previous disk image.");

            var slow = new AvatarDiscoveredAsset { id = 987654318, bannerUrl = url + "slow" };
            AvatarAssetDiscoveryService.GetBanner(slow);
            deadline = DateTime.UtcNow.AddSeconds(15);
            while (!slowStarted.Task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Check(slowStarted.Task.IsCompleted, "Slow request did not start.");
            AvatarAssetDiscoveryService.ReloadImages(); releaseSlow.SetResult(true);
            // Let the superseded response finish; it must not populate the failure gate.
            var until = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < until) yield return null;
            Check(!AvatarAssetDiscoveryService.IsBannerRetryPending(slow), "Superseded failure poisoned the new cache generation.");
        }
        finally
        {
            releaseSlow.TrySetResult(true); listener.Close();
            AvatarAssetDiscoveryService.ReloadImages(); UnityEngine.Object.DestroyImmediate(image);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
