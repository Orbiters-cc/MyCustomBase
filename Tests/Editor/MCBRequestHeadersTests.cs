#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.Networking;
using UnityEngine.TestTools;

public class MCBRequestHeadersTests
{
    [Test]
    public void CreatedIdempotencyKeyUsesTheBackendContract()
    {
        string key = MCBRequestHeaders.CreateIdempotencyKey();

        Assert.That(key, Has.Length.EqualTo(32));
        Assert.That(Guid.TryParseExact(key, "N", out _), Is.True);
        Assert.That(key, Is.EqualTo(key.ToLowerInvariant()));
    }

    [Test]
    public void SetIdempotencyKeyWritesNormalizedHeader()
    {
        using (var request = new UnityWebRequest("http://localhost.invalid", UnityWebRequest.kHttpVerbPOST))
        {
            MCBRequestHeaders.SetIdempotencyKey(request, " AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ");

            Assert.That(
                request.GetRequestHeader(MCBRequestHeaders.IdempotencyKeyHeader),
                Is.EqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
    }

    [Test]
    public void AuthorizationTravelsInTheHeaderNeverInTheUrl()
    {
        using (var request = new UnityWebRequest("http://localhost.invalid", UnityWebRequest.kHttpVerbGET))
        {
            Assert.That(MCBRequestHeaders.SetAuthorization(request, "orbit-token"), Is.True);
            Assert.That(request.GetRequestHeader(MCBRequestHeaders.AuthorizationHeader), Is.EqualTo("Bearer orbit-token"));
        }

        using (var request = new UnityWebRequest("http://localhost.invalid", UnityWebRequest.kHttpVerbGET))
        {
            Assert.That(MCBRequestHeaders.SetAuthorization(request, null), Is.False);
            Assert.That(request.GetRequestHeader(MCBRequestHeaders.AuthorizationHeader), Is.Null.Or.Empty);
        }

        foreach (string url in new[]
                 {
                     MCBUtils.GetAssetModelTrustUrl(14, "1.0", new string('a', 64), "key"),
                     ConnectivityDiagnosticsService.BuildConnectivityCheckUrl(),
                     OriginalBaseSupportService.Url(14, "/source-versions")
                 })
            Assert.That(Regex.IsMatch(url, "[?&]t="), Is.False, url);
    }

    // The backend hands downloads to signed storage URLs: the credential reaches the backend, never the storage host.
    [UnityTest]
    public IEnumerator AuthorizedDownloadsFollowRedirectsWithoutTheCredential()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        string root = "http://127.0.0.1:" + port + "/";
        var listener = new HttpListener();
        listener.Prefixes.Add(root);
        listener.Start();
        var seen = new ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    seen.Enqueue(context.Request.Url.AbsolutePath + " " + (context.Request.Headers["Authorization"] ?? "anonymous"));
                    if (context.Request.Url.AbsolutePath == "/model")
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = root + "storage?signature=signed";
                    }
                    else
                    {
                        byte[] body = Encoding.UTF8.GetBytes("payload");
                        context.Response.OutputStream.Write(body, 0, body.Length);
                    }
                    context.Response.Close();
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        });

        try
        {
            var download = new NetworkService().DownloadBytesAsync(root + "model?version=1", authToken: "orbit-secret");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!download.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(download.IsCompleted, Is.True, "The download did not finish.");
            if (download.Result.error?.IndexOf("Insecure connection", StringComparison.OrdinalIgnoreCase) >= 0)
                Assert.Ignore("This project does not allow HTTP downloads, even from the local test server.");
            Assert.That(download.Result.success, Is.True, download.Result.error);
            Assert.That(Encoding.UTF8.GetString(download.Result.data), Is.EqualTo("payload"));
            Assert.That(seen.ToArray(), Is.EqualTo(new[] { "/model Bearer orbit-secret", "/storage anonymous" }));
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-guid")]
    public void SetIdempotencyKeyRejectsInvalidValues(string key)
    {
        using (var request = new UnityWebRequest("http://localhost.invalid", UnityWebRequest.kHttpVerbPOST))
        {
            Assert.Throws<ArgumentException>(() => MCBRequestHeaders.SetIdempotencyKey(request, key));
        }
    }
}
#endif
