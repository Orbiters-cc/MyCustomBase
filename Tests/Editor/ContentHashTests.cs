using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

public sealed class ContentHashTests
{
    string folder;

    [SetUp] public void SetUp()
    {
        folder = Path.Combine(Path.GetTempPath(), "mcb-content-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ContentHash.ClearMemory();
    }

    [TearDown] public void TearDown()
    {
        ContentHash.ClearMemory();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static string Reference(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    // Written, then dated in the past: out of the racy window, so its hash may be cached.
    string Settled(string name, byte[] bytes, int secondsAgo = 60)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-secondsAgo));
        return path;
    }

    [Test] public void StreamingHashMatchesTheManagedSha256AcrossBufferBoundaries()
    {
        var random = new Random(7);
        foreach (int length in new[] { 0, 1, 63, 64, MCBHashing.StreamBufferBytes - 1, MCBHashing.StreamBufferBytes, MCBHashing.StreamBufferBytes * 2 + 17 })
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            string path = Settled("file-" + length + ".bin", bytes);
            Assert.AreEqual(Reference(bytes), MCBHashing.HashFile(path), "length " + length);
            Assert.AreEqual(Reference(bytes), ContentHash.Get(path), "length " + length);
            Assert.AreEqual(Reference(bytes), MCBHashing.HashBytes(bytes), "length " + length);
        }
        Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("form")), MCBHashing.HashString("form"));
        Assert.AreEqual(Reference(Array.Empty<byte>()), MCBHashing.HashString(null));
    }

    [Test] public void HexRoundTripsAndRejectsNonHex()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        string hex = MCBHashing.Hex(bytes);
        Assert.AreEqual(BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(), hex);
        CollectionAssert.AreEqual(bytes, MCBHashing.FromHex(hex));
        CollectionAssert.AreEqual(bytes, MCBHashing.FromHex(hex.ToUpperInvariant()));
        Assert.IsNull(MCBHashing.FromHex("abc"));
        Assert.IsNull(MCBHashing.FromHex("zz"));
    }

    [Test] public void CachedHashFollowsLengthAndWriteTime()
    {
        string path = Settled("model.fbx", Encoding.UTF8.GetBytes("version A"));
        string a = ContentHash.Get(path);
        Assert.AreEqual(a, ContentHash.GetIfCached(path), "A settled file's hash is cached.");

        DateTime written = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "version B, longer");
        File.SetLastWriteTimeUtc(path, written);
        Assert.IsNull(ContentHash.GetIfCached(path), "Same write time, different length.");
        Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("version B, longer")), ContentHash.Get(path));

        File.WriteAllText(path, "version C");
        File.SetLastWriteTimeUtc(path, written.AddSeconds(5));
        Assert.IsNull(ContentHash.GetIfCached(path), "Same length as A, different write time.");
        Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("version C")), ContentHash.Get(path));
    }

    [Test] public void AJustWrittenFileIsHashedButNotCached()
    {
        string path = Path.Combine(folder, "fresh.bin");
        File.WriteAllText(path, "fresh");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
        Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("fresh")), ContentHash.Get(path));
        Assert.IsNull(ContentHash.GetIfCached(path), "A write time at or after the hash may hide a second write in the same tick.");
    }

    [Test] public void RememberAndForget()
    {
        string path = Path.Combine(folder, "written.bin");
        File.WriteAllText(path, "written");
        string hash = Reference(Encoding.UTF8.GetBytes("written"));
        ContentHash.Remember(path, hash.ToUpperInvariant());
        Assert.AreEqual(hash, ContentHash.GetIfCached(path), "Remembered hashes are kept lowercase.");
        ContentHash.Forget(path);
        Assert.IsNull(ContentHash.GetIfCached(path));
    }

    [Test] public void VerifyChecksLengthFirstThenContent()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        string path = Settled("payload.bin", bytes);
        string hash = Reference(bytes);
        Assert.IsTrue(ContentHash.Verify(path, hash));
        Assert.IsTrue(ContentHash.Verify(path, hash.ToUpperInvariant(), bytes.Length));
        Assert.IsFalse(ContentHash.Verify(path, hash, bytes.Length + 1));
        Assert.IsFalse(ContentHash.Verify(path, Reference(Encoding.UTF8.GetBytes("other"))));
        Assert.IsFalse(ContentHash.Verify(Path.Combine(folder, "missing.bin"), hash));
        Assert.IsFalse(ContentHash.Verify(path, null));
    }

    [Test] public void SameContentComparesBytes()
    {
        string a = Settled("a.fbx", Encoding.UTF8.GetBytes("same bytes"));
        string b = Settled("b.fbx", Encoding.UTF8.GetBytes("same bytes"), 120);
        string c = Settled("c.fbx", Encoding.UTF8.GetBytes("diff bytes"));
        Assert.IsTrue(ContentHash.SameContent(a, b));
        Assert.IsFalse(ContentHash.SameContent(a, c));
        Assert.IsFalse(ContentHash.SameContent(a, Path.Combine(folder, "missing.fbx")));
    }

    [Test] public void AsyncHashMatchesAndIsShared()
    {
        var bytes = new byte[3 * 1024 * 1024 + 5];
        new Random(3).NextBytes(bytes);
        string path = Settled("async.bin", bytes);
        var first = ContentHash.GetAsync(path);
        var second = ContentHash.GetAsync(path);
        Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(30)) && second.Wait(TimeSpan.FromSeconds(30)));
        Assert.AreEqual(Reference(bytes), first.Result);
        Assert.AreEqual(first.Result, second.Result);
        Assert.AreEqual(first.Result, ContentHash.GetIfCached(path));
        var fresh = ContentHash.GetFreshAsync(path);
        Assert.IsTrue(fresh.Wait(TimeSpan.FromSeconds(30)));
        Assert.AreEqual(Reference(bytes), fresh.Result);
        ContentHash.Forget(path);
    }
}
