#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

/// <summary>Downloads that stall, drop or fail; the version cache's isolation; the mesh blob cache's pruning.</summary>
public class MCBDeliveryRobustnessTests
{
    [Test]
    public void CachedVersionsShareNothingWithTheirCallers()
    {
        var cache = PersistentCache.Instance;
        string hash = Guid.NewGuid().ToString("N"), token = "test-" + Guid.NewGuid().ToString("N");
        const int assetId = 987654;
        var versions = new List<CustomBaseVersion> { new CustomBaseVersion { assetId = assetId, version = "1.0.0", defaultAviVersion = "1", title = "First" } };
        try
        {
            cache.CacheVersions(hash, versions, versions[0], token, assetId);
            // An inspector rewriting and clearing its own list.
            versions[0].title = "Changed";
            versions.Clear();

            var first = cache.GetCachedVersions(hash, token, assetId);
            Assert.That(first.serverVersions.Select(v => v.title), Is.EqualTo(new[] { "First" }));
            Assert.That(first.recommendedVersion, Is.SameAs(first.serverVersions[0]));
            first.serverVersions[0].title = "Mutated";
            first.serverVersions.Clear();

            Assert.That(cache.GetCachedVersions(hash, token, assetId).serverVersions.Select(v => v.title), Is.EqualTo(new[] { "First" }));
        }
        finally
        {
            cache.RemoveCachedVersions(hash, token, assetId);
        }
        Assert.That(cache.GetCachedVersions(hash, token, assetId), Is.Null);
    }

    [Test]
    public void OnlyTheAccessDeniedAnswerOpensTheStoreLink()
    {
        Assert.That(NetworkService.TryReadAccessDenied("{\"error\":\"You do not have access to this asset\",\"assetId\":14,\"code\":\"ACCESS_DENIED\"}", out string assetId), Is.True);
        Assert.That(assetId, Is.EqualTo("14"));
        Assert.That(NetworkService.TryReadAccessDenied("{\"error\":\"Join the creator's Discord server to download this version.\"}", out _), Is.False);
        Assert.That(NetworkService.TryReadAccessDenied("{\"code\":\"ACCESS_DENIED\"}", out _), Is.False);
        Assert.That(NetworkService.TryReadAccessDenied("<html>Forbidden</html>", out _), Is.False);
        Assert.That(NetworkService.TryReadAccessDenied(null, out _), Is.False);
    }

    [Test]
    public void ErrorAnswersWrittenToDiskKeepTheServerMessage()
    {
        string path = Path.Combine(Path.GetTempPath(), "mcb-test-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, new byte[100]);
            File.AppendAllText(path, "{\"errorMessage\":\"The model file in your project doesn't match with any supported version\"}");
            string body = NetworkService.ReadErrorBody(path, 100);
            Assert.That(NetworkService.ServerError(400, "HTTP/1.1 400 Bad Request", body),
                Is.EqualTo("The model file in your project doesn't match with any supported version"));
            Assert.That(NetworkService.ServerError(502, "HTTP/1.1 502 Bad Gateway", "<Error><Code>BadGateway</Code></Error>"),
                Is.EqualTo("Download failed: HTTP 502 HTTP/1.1 502 Bad Gateway"));
            Assert.That(NetworkService.ReadErrorBody(path, new FileInfo(path).Length), Is.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void StallsAndResumedRangesAreRecognised()
    {
        Assert.That(NetworkService.IsStalled(10, 10 + NetworkService.DownloadStallSeconds - 1), Is.False);
        Assert.That(NetworkService.IsStalled(10, 10 + NetworkService.DownloadStallSeconds + 1), Is.True);
        Assert.That(NetworkService.TryParseContentRange("bytes 500-999/1000", 500, out long total), Is.True);
        Assert.That(total, Is.EqualTo(1000));
        Assert.That(NetworkService.TryParseContentRange("bytes 500-999/*", 500, out total), Is.True);
        Assert.That(total, Is.EqualTo(-1));
        Assert.That(NetworkService.TryParseContentRange("bytes 0-999/1000", 500, out _), Is.False, "A range that does not start at the bytes on disk.");
        Assert.That(NetworkService.TryParseContentRange(null, 0, out _), Is.False);
    }

    [Test]
    public void BlobPruningKeepsUsedAndRecentBlobsAndEvictsTheLeastRecentlyUsed()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        MCBMeshDelivery.CachedBlob Blob(string hash, long bytes, int daysAgo) =>
            new MCBMeshDelivery.CachedBlob { hash = hash, path = hash, bytes = bytes, lastUsedUtc = now.AddDays(-daysAgo) };
        var blobs = new[] { Blob("used", 3, 400), Blob("stale", 1, 90), Blob("lru", 2, 30), Blob("newer", 2, 10), Blob("fresh", 4, 0) };
        var used = new HashSet<string> { "used" };
        TimeSpan maxAge = TimeSpan.FromDays(60), grace = TimeSpan.FromDays(1);

        // 12 units over a cap of 9: the blob past the age limit, then the least recently used until under the cap.
        Assert.That(MCBMeshDelivery.SelectPrunable(blobs, used, now, 9, maxAge, grace).Select(b => b.hash), Is.EqualTo(new[] { "stale", "lru" }));
        // Under the cap only the age limit applies; a blob a local version uses stays however old.
        Assert.That(MCBMeshDelivery.SelectPrunable(blobs, used, now, 100, maxAge, grace).Select(b => b.hash), Is.EqualTo(new[] { "stale" }));
        // Blobs used within the grace period stay even over the cap: another editor may be reading them.
        Assert.That(MCBMeshDelivery.SelectPrunable(new[] { Blob("fresh", 50, 0) }, used, now, 1, maxAge, grace), Is.Empty);
    }

    [Test]
    public void LocalVersionsProtectEveryCodecOfTheirMeshBlobs()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcb-test-" + Guid.NewGuid().ToString("N"));
        string applied = new string('a', 64), zstd = new string('b', 64), lz4 = new string('c', 64);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "14", "1.0.0"));
            File.WriteAllText(Path.Combine(root, "14", "1.0.0", "version.json"),
                "{\"assetId\":14,\"version\":\"1.0.0\",\"defaultAviVersion\":\"1\",\"versionFiles\":[{\"path\":\"Body.bin\",\"hash\":\"" + applied +
                "\",\"metadata\":{\"deliveryVariants\":[{\"codec\":\"zstd\",\"hash\":\"" + zstd + "\",\"bytes\":1},{\"codec\":\"lz4\",\"hash\":\"" + lz4 + "\",\"bytes\":2}]}}]}");
            File.WriteAllText(Path.Combine(root, "14", "broken.json"), "not a version");
            Assert.That(MCBMeshDelivery.UsedBlobHashes(root), Is.EquivalentTo(new[] { applied, zstd, lz4 }));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void OnlyVersionFolderChangesScanImportedVersionsAgain()
    {
        Assert.That(MCBEditor.IsInVersionsFolder(MCBUtils.ASSET_VERSIONS_FOLDER + "/14/1.0.0/version.json"), Is.True);
        Assert.That(MCBEditor.IsInVersionsFolder(MCBUtils.ASSETS_BASE_FOLDER), Is.True, "Deleting the MCB folder removes the versions too.");
        Assert.That(MCBEditor.IsInVersionsFolder(MCBUtils.ASSET_VERSIONS_FOLDER + "x/file.asset"), Is.False);
        Assert.That(MCBEditor.IsInVersionsFolder("Assets/VRCFury/temp/fx.controller"), Is.False);
    }

    // A transfer dropped halfway resumes from the bytes on disk at the signed storage URL, never asking the backend again.
    [UnityTest]
    public IEnumerator DroppedDownloadsResumeWithRangeFromSignedStorage()
    {
        byte[] payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31)).ToArray();
        var seen = new ConcurrentQueue<string>();
        string root = null;
        var listener = Serve((path, headers) =>
        {
            headers.TryGetValue("Authorization", out string auth);
            headers.TryGetValue("Range", out string range);
            seen.Enqueue(path + " " + (auth ?? "anonymous") + " " + (range ?? "-"));
            if (path == "/model")
                return new Reply { Head = "HTTP/1.1 302 Found\r\nLocation: " + root + "storage?signature=signed\r\nContent-Length: 0\r\n" };
            if (range == null)
                return new Reply { Head = "HTTP/1.1 200 OK\r\nContent-Length: " + payload.Length + "\r\n", Body = payload, Sent = 120_000 };
            int start = int.Parse(range.Substring("bytes=".Length).TrimEnd('-'));
            byte[] rest = payload.Skip(start).ToArray();
            return new Reply { Head = "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + start + "-" + (payload.Length - 1) + "/" + payload.Length +
                                      "\r\nContent-Length: " + rest.Length + "\r\n", Body = rest };
        }, out root);
        string file = Path.Combine(Path.GetTempPath(), "mcb_dl_test_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var download = new NetworkService().DownloadFileAsync(root + "model?version=1", file, authToken: "orbit-secret");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!download.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(download.IsCompleted, Is.True, "The download did not finish.");
            IgnoreWhenHttpIsBlocked(download.Result.error);
            Assert.That(download.Result.success, Is.True, download.Result.error);
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(payload));
            string[] requests = seen.ToArray();
            Assert.That(requests.Take(2), Is.EqualTo(new[] { "/model Bearer orbit-secret -", "/storage anonymous -" }));
            Assert.That(requests.Length, Is.EqualTo(3), string.Join("\n", requests));
            var resumed = Regex.Match(requests[2], @"^/storage anonymous bytes=(\d+)-$");
            Assert.That(resumed.Success && long.Parse(resumed.Groups[1].Value) > 0, Is.True, requests[2]);
        }
        finally
        {
            listener.Stop();
            if (File.Exists(file)) File.Delete(file);
        }
    }

    // A refused disk download shows the server's own message (here, the update request) and leaves no file behind.
    [UnityTest]
    public IEnumerator RefusedDiskDownloadsShowTheServerMessage()
    {
        const string message = "Update My Custom Base to download this renderer-based version.";
        byte[] body = Encoding.UTF8.GetBytes("{\"errorMessage\":\"" + message + "\"}");
        int requests = 0;
        var listener = Serve((path, headers) =>
        {
            System.Threading.Interlocked.Increment(ref requests);
            return new Reply { Head = "HTTP/1.1 426 Upgrade Required\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\n", Body = body };
        }, out string root);
        string file = Path.Combine(Path.GetTempPath(), "mcb_dl_test_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            LogAssert.ignoreFailingMessages = true;
            var download = new NetworkService().DownloadFileAsync(root + "model?version=1", file, authToken: "orbit-secret");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!download.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.That(download.IsCompleted, Is.True, "The download did not finish.");
            IgnoreWhenHttpIsBlocked(download.Result.error);
            Assert.That(download.Result.success, Is.False);
            Assert.That(download.Result.error, Is.EqualTo(message));
            Assert.That(requests, Is.EqualTo(1), "A refusal is not retried.");
            Assert.That(File.Exists(file), Is.False);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = false;
            listener.Stop();
            if (File.Exists(file)) File.Delete(file);
        }
    }

    static void IgnoreWhenHttpIsBlocked(string error)
    {
        if (error?.IndexOf("Insecure connection", StringComparison.OrdinalIgnoreCase) >= 0)
            Assert.Ignore("This project does not allow HTTP downloads, even from the local test server.");
    }

    sealed class Reply
    {
        public string Head;
        public byte[] Body = Array.Empty<byte>();
        public int Sent = -1; // bytes of the body sent before the connection closes (all when negative)
    }

    // A minimal HTTP/1.1 server that can close a connection halfway through a body, as a dropped transfer does.
    static TcpListener Serve(Func<string, Dictionary<string, string>, Reply> handle, out string root)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        root = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using (var client = await listener.AcceptTcpClientAsync())
                    using (var stream = client.GetStream())
                    {
                        var request = ReadRequest(stream);
                        var reply = handle(request.path, request.headers);
                        byte[] head = Encoding.ASCII.GetBytes(reply.Head + "Connection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length);
                        stream.Write(reply.Body, 0, reply.Sent >= 0 ? reply.Sent : reply.Body.Length);
                        stream.Flush();
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        });
        return listener;
    }

    static (string path, Dictionary<string, string> headers) ReadRequest(NetworkStream stream)
    {
        var data = new List<byte>();
        var one = new byte[1];
        while (stream.Read(one, 0, 1) == 1)
        {
            data.Add(one[0]);
            int n = data.Count;
            if (n >= 4 && data[n - 4] == '\r' && data[n - 3] == '\n' && data[n - 2] == '\r' && data[n - 1] == '\n') break;
        }
        string[] lines = Encoding.ASCII.GetString(data.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }
        string target = lines.Length > 0 && lines[0].Split(' ').Length > 1 ? lines[0].Split(' ')[1] : "/";
        return (new Uri("http://localhost" + target).AbsolutePath, headers);
    }
}
#endif
