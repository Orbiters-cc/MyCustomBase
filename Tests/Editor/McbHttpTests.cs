#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit;
using UnityEngine.TestTools;

// MCB's requests on Toolkit's OrbitersHttp, each call shape against a server on this computer: what reaches the server
// (method, credential, body) is what the UnityWebRequest code sent, and the answers are read as before.
public sealed class McbHttpTests
{
    private sealed class Seen
    {
        public string Method, Path, Authorization, ContentType;
        public byte[] Body;
    }

    private sealed class Server : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        public readonly string Root;
        public readonly ConcurrentQueue<Seen> Requests = new ConcurrentQueue<Seen>();

        public Server(Func<HttpListenerContext, Task> answer)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Root = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(Root);
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var context = await listener.GetContextAsync();
                        var memory = new MemoryStream();
                        await context.Request.InputStream.CopyToAsync(memory);
                        Requests.Enqueue(new Seen
                        {
                            Method = context.Request.HttpMethod, Path = context.Request.Url.AbsolutePath,
                            Authorization = context.Request.Headers["Authorization"], ContentType = context.Request.ContentType, Body = memory.ToArray(),
                        });
                        _ = Task.Run(async () =>
                        {
                            try { await answer(context); }
                            catch (Exception) { }
                            finally { try { context.Response.Close(); } catch (Exception) { } }
                        });
                    }
                }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
            });
        }

        public void Dispose()
        {
            listener.Stop();
            listener.Close();
        }
    }

    private static async Task Write(HttpListenerContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static IEnumerator Wait(Task task, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
        Assert.That(task.IsCompleted, Is.True, "the request did not end");
    }

    private sealed class Echo { public string name; public int count; }

    // MCBJsonRequest: the JSON body and the bearer token reach the server; the answer is read; a refusal throws its
    // server message as an IOException, as before.
    [UnityTest]
    public IEnumerator JsonRequestsSendTheirBodyAndReadTheAnswerOrTheRefusal()
    {
        using (var server = new Server(context => context.Request.Url.AbsolutePath == "/ok"
                   ? Write(context, 200, "{\"name\":\"answer\",\"count\":3}")
                   : Write(context, 409, "{\"error\":\"This listing is locked.\"}")))
        {
            var ok = MCBJsonRequest.SendAsync<Echo>("PUT", server.Root + "ok", "orbit-secret", new { name = "sent", count = 2 }, "Test");
            yield return Wait(ok);
            Assert.That(ok.Status, Is.EqualTo(TaskStatus.RanToCompletion), ok.Exception?.ToString());
            Assert.That(ok.Result.name, Is.EqualTo("answer"));
            Assert.That(ok.Result.count, Is.EqualTo(3));
            Assert.That(server.Requests.TryDequeue(out var seen), Is.True);
            Assert.That(seen.Method, Is.EqualTo("PUT"));
            Assert.That(seen.Authorization, Is.EqualTo("Bearer orbit-secret"));
            StringAssert.StartsWith("application/json", seen.ContentType);
            Assert.That(Encoding.UTF8.GetString(seen.Body), Is.EqualTo("{\"name\":\"sent\",\"count\":2}"));

            var refused = MCBJsonRequest.SendAsync<Echo>("POST", server.Root + "locked", "orbit-secret", null, "Test");
            yield return Wait(refused);
            var error = refused.Exception?.GetBaseException();
            Assert.That(error, Is.InstanceOf<IOException>());
            Assert.That(error.Message, Is.EqualTo("This listing is locked."));
            Assert.That(server.Requests.TryDequeue(out seen), Is.True);
            Assert.That(seen.Body, Is.Empty, "no body when none is given");
            Assert.That(server.Requests.IsEmpty, Is.True, "a refused POST is not sent again");
        }
    }

    // FetchVersionsAsync: a GET with the token; the versions are read; an access denial names its asset.
    [UnityTest]
    public IEnumerator VersionFetchesReadTheVersionsOrTheAccessDenial()
    {
        using (var server = new Server(context => context.Request.Url.AbsolutePath == "/versions"
                   ? Write(context, 200, "{\"versions\":[]}")
                   : Write(context, 403, "{\"error\":\"You do not have access to this asset\",\"assetId\":14,\"code\":\"ACCESS_DENIED\"}")))
        {
            var fetch = new NetworkService().FetchVersionsAsync(server.Root + "versions", "orbit-secret");
            yield return Wait(fetch);
            Assert.That(fetch.Result.success, Is.True, fetch.Result.error);
            Assert.That(server.Requests.TryDequeue(out var seen), Is.True);
            Assert.That((seen.Method, seen.Authorization), Is.EqualTo(("GET", "Bearer orbit-secret")));

            var denied = new NetworkService().FetchVersionsAsync(server.Root + "denied", "orbit-secret");
            yield return Wait(denied);
            Assert.That(denied.Result.success, Is.False);
            Assert.That(denied.Result.error, Is.EqualTo(NetworkService.AccessDeniedPrefix + "14"));
        }
    }

    // SubmitNewVersionStreamingAsync below the staging size: one multipart POST, "metadata" then "packageFile", streamed
    // from the zip; the answer's body comes back and the progress ends full.
    [UnityTest]
    public IEnumerator UploadsSendTheMetadataAndThePackageInOneForm()
    {
        string zip = Path.Combine(Path.GetTempPath(), "mcb-http-test-" + Guid.NewGuid().ToString("N") + ".zip");
        var package = new byte[300_000];
        new Random(5).NextBytes(package);
        File.WriteAllBytes(zip, package);
        float lastProgress = -1;
        using (var server = new Server(context => Write(context, 201, "{\"version\":\"1.0.0\"}")))
        {
            try
            {
                var upload = new NetworkService().SubmitNewVersionStreamingAsync(server.Root + "custom-bases/14/versions", "orbit-secret", zip, "{\"version\":\"1.0.0\"}",
                    (progress, _) => lastProgress = progress);
                yield return Wait(upload);
                Assert.That(upload.Result.success, Is.True, upload.Result.error);
                Assert.That(upload.Result.serverResponse, Is.EqualTo("{\"version\":\"1.0.0\"}"));
                Assert.That(lastProgress, Is.EqualTo(1f));
                Assert.That(server.Requests.TryDequeue(out var seen), Is.True);
                Assert.That((seen.Method, seen.Authorization), Is.EqualTo(("POST", "Bearer orbit-secret")));
                StringAssert.StartsWith("multipart/form-data; boundary=", seen.ContentType);
                string boundary = seen.ContentType.Substring("multipart/form-data; boundary=".Length);

                // The body the previous uploader wrote to its file first (OrbitersTransfer.WriteMultipart), for this boundary.
                string expectedPath = zip + ".body";
                try
                {
                    Orbiters.Toolkit.Editor.Net.OrbitersTransfer.WriteMultipart(expectedPath, boundary, new[]
                    {
                        Orbiters.Toolkit.Editor.Net.OrbitersTransfer.Part.Field("metadata", "{\"version\":\"1.0.0\"}"),
                        Orbiters.Toolkit.Editor.Net.OrbitersTransfer.Part.File("packageFile", zip, Path.GetFileName(zip), "application/zip"),
                    });
                    Assert.That(seen.Body, Is.EqualTo(File.ReadAllBytes(expectedPath)));
                }
                finally { File.Delete(expectedPath); }
            }
            finally { File.Delete(zip); }
        }
    }

    // MCB's own time limit ends a call OrbitersHttp would still wait for: no answer within it is a timed-out failure.
    [UnityTest]
    public IEnumerator MCBsTimeLimitEndsACallTheServerDoesNotAnswer()
    {
        var release = new TaskCompletionSource<bool>();
        using (var server = new Server(async context => { await Task.WhenAny(release.Task, Task.Delay(15000)); await Write(context, 200, "{}"); }))
        {
            try
            {
                var started = DateTime.UtcNow;
                var call = McbHttp.TextAsync(server.Root + "slow", null, MCBRequestPolicy.Diagnostics("Test"), 1);
                yield return Wait(call, 10);
                var error = call.Exception?.GetBaseException() as OrbitersHttpException;
                Assert.That(error, Is.Not.Null, "a timeout is an OrbitersHttpException");
                Assert.That(error.TimedOut, Is.True);
                Assert.That(error.Offline, Is.True);
                Assert.That((DateTime.UtcNow - started).TotalSeconds, Is.LessThan(8), "ended at MCB's limit, not OrbitersHttp's 90 s");
            }
            finally { release.TrySetResult(true); }
        }
    }

    // No moved call gets a shorter limit than it had: OrbitersHttp's own limit for the kind McbHttp picks is never below MCB's.
    [Test]
    public void OrbitersHttpNeverEndsACallBeforeMCBsLimit()
    {
        foreach (NetworkRequestType type in Enum.GetValues(typeof(NetworkRequestType)))
        {
            int seconds = NetworkService.GetTimeoutSeconds(type);
            var inner = OrbitersHttp.TimeoutOf(McbHttp.KindFor(seconds));
            Assert.That(inner == System.Threading.Timeout.InfiniteTimeSpan || inner >= TimeSpan.FromSeconds(seconds), Is.True, type + ": " + seconds + " s");
        }
        Assert.That(McbHttp.KindFor(NetworkService.GetTimeoutSeconds(NetworkRequestType.Upload)), Is.EqualTo(OrbitersHttpKind.Transfer), "the 300 s metadata update");
    }

    // A stalled upload never took MCB offline; one whose connection failed or broke off did. Read from the upload's own result.
    [Test]
    public void OnlyAStalledUploadIsNotAConnectivityFailure()
    {
        OrbitersTransferResult Failed(long status, string error, bool cutOff = false, bool cancelled = false) =>
            new OrbitersTransferResult { StatusCode = status, Error = error, CutOff = cutOff, Cancelled = cancelled };
        Assert.That(NetworkService.UploadStalled(Failed(0, $"The upload timed out: nothing moved for {OrbitersHttp.StallSeconds:0} seconds.")), Is.True);
        Assert.That(NetworkService.UploadStalled(Failed(0, OrbitersHttp.StatusMessage(0))), Is.False);
        Assert.That(NetworkService.UploadStalled(Failed(0, "The upload broke off: connection reset")), Is.False);
        Assert.That(NetworkService.UploadStalled(Failed(502, "Orbiters answered HTTP 502.")), Is.False);
        Assert.That(NetworkService.UploadStalled(Failed(0, "The upload was cancelled.", cancelled: true)), Is.False);
    }

    // The connectivity monitor hears an MCB request through OrbitersHttp as it heard one through UnityWebRequest.
    [Test]
    public void AnswersAreReportedUnderTheRequestsPolicyAsBefore()
    {
        const string key = "mcb-http-test-warning";
        try
        {
            MCBConnectivityMonitor.ReportAnswer("https://example.invalid/a.png", 404, "Not Found", MCBRequestPolicy.ExternalResource("Test picture", key, "Test"));
            Assert.That(MCBConnectivityMonitor.HasRequestWarning(key), Is.True, "an external resource that failed is a warning");
            MCBConnectivityMonitor.ReportAnswer("https://example.invalid/a.png", 200, null, MCBRequestPolicy.ExternalResource("Test picture", key, "Test"));
            Assert.That(MCBConnectivityMonitor.HasRequestWarning(key), Is.False, "and its success clears it");
        }
        finally { MCBConnectivityMonitor.ClearRequestWarning(key); }
    }
}
#endif
