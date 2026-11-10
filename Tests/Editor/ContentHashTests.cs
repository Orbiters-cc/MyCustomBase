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
            Assert.AreEqual(Reference(bytes), ContentHash.Proof(path), "length " + length);
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
        string a = ContentHash.Proof(path);
        Assert.AreEqual(a, ContentHash.GetIfCached(path), "A settled file's hash is cached.");

        DateTime written = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "version B, longer");
        File.SetLastWriteTimeUtc(path, written);
        Assert.IsNull(ContentHash.GetIfCached(path), "Same write time, different length.");
        Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("version B, longer")), ContentHash.Proof(path));

        File.WriteAllText(path, "version C");
        File.SetLastWriteTimeUtc(path, written.AddSeconds(5));
        Assert.IsNull(ContentHash.GetIfCached(path), "Same length as A, different write time.");
        Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("version C")), ContentHash.Proof(path));
    }

    [Test] public void AJustWrittenFileIsHashedButNotCached()
    {
        string path = Path.Combine(folder, "fresh.bin");
        File.WriteAllText(path, "fresh");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
        Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("fresh")), ContentHash.Proof(path));
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

    // ---- Advisory hashes never stand in for a proof (review of MCBS-4)

    // Replaces the file's bytes with others of the same length and gives it back its write time: what a timestamp-keeping
    // copy, an archive extraction or a coarse clock can do.
    static void ReplaceSilently(string path, byte[] bytes)
    {
        DateTime written = File.GetLastWriteTimeUtc(path);
        Assert.AreEqual(new FileInfo(path).Length, bytes.Length, "The replacement must keep the length.");
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
    }

    static readonly byte[] A = Encoding.UTF8.GetBytes("original bytes A"), B = Encoding.UTF8.GetBytes("replaced bytes B");

    [Test] public void SameLengthSameTimestampReplacementIsCaughtByProofs()
    {
        string path = Settled("model.fbx", A), copy = Settled("copy.fbx", A, 120);
        Assert.AreEqual(Reference(A), ContentHash.Advisory(path));
        Assert.IsTrue(ContentHash.SameContent(path, copy));
        ReplaceSilently(path, B);
        Assert.AreEqual(Reference(A), ContentHash.GetIfCached(path), "The advisory level cannot see it: that is why it only advises.");
        Assert.AreEqual(Reference(B), ContentHash.Proof(path));
        Assert.IsFalse(ContentHash.Verify(path, Reference(A)));
        Assert.IsFalse(ContentHash.SameContent(path, copy));
    }

    [Test] public void DeleteAndRecreateEvictsAndIsCaught()
    {
        string path = Settled("model.fbx", A);
        Assert.AreEqual(Reference(A), ContentHash.Advisory(path));
        DateTime written = File.GetLastWriteTimeUtc(path);
        File.Delete(path);
        Assert.IsNull(ContentHash.GetIfCached(path), "A missing file has no hash.");
        File.WriteAllBytes(path, B);
        File.SetLastWriteTimeUtc(path, written);
        Assert.IsNull(ContentHash.GetIfCached(path), "The missing file's entry was evicted.");
        Assert.IsFalse(ContentHash.Verify(path, Reference(A)));
        Assert.AreEqual(Reference(B), ContentHash.Proof(path));
    }

    [Test] public void PersistentHitsAreAdvisoryOnly()
    {
        string path = Settled("model.fbx", A);
        Assert.IsNotNull(PersistentCache.Instance); // created on the main thread, so the worker below can store into it
        var hashed = ContentHash.GetAsync(path);
        Assert.IsTrue(hashed.Wait(TimeSpan.FromSeconds(30)));
        try
        {
            ContentHash.ClearMemory(); // as after a domain reload: only the persistent store remembers it
            ReplaceSilently(path, B);
            Assert.AreEqual(Reference(A), ContentHash.GetIfCached(path), "The persistent store answers the advisory level.");
            Assert.IsFalse(ContentHash.Verify(path, Reference(A)));
            Assert.AreEqual(Reference(B), ContentHash.Proof(path));
        }
        finally { ContentHash.Forget(path); }
    }

    [Test] public void RememberFeedsTheAdvisoryLevelOnly()
    {
        string path = Settled("written.bin", A);
        ContentHash.Remember(path, Reference(B));
        Assert.AreEqual(Reference(B), ContentHash.GetIfCached(path));
        Assert.IsFalse(ContentHash.Verify(path, Reference(B)));
        Assert.AreEqual(Reference(A), ContentHash.Proof(path));
    }

    [Test] public void OperationsReuseTheirOwnProofsOnly()
    {
        string path = Settled("model.fbx", A);
        string hash;
        using (var first = new ContentHash.Operation())
        {
            first.Record(path, Reference(B)); // what this operation says it wrote
            Assert.AreEqual(Reference(B), ContentHash.Proof(path, first));
            using (var second = new ContentHash.Operation())
                Assert.AreEqual(Reference(A), ContentHash.Proof(path, second), "Another operation reads the bytes.");
            hash = ContentHash.Proof(path, first);
        }
        Assert.AreEqual(Reference(B), hash);
        Assert.AreEqual(Reference(A), ContentHash.Proof(path), "Without an operation, the bytes.");
        var disposed = new ContentHash.Operation();
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ContentHash.Proof(path, disposed));

        // Inside one operation a change of length or write time is read again.
        using (var operation = new ContentHash.Operation())
        {
            Assert.AreEqual(Reference(A), ContentHash.Proof(path, operation));
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("longer replacement bytes"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-30));
            Assert.AreEqual(Reference(Encoding.UTF8.GetBytes("longer replacement bytes")), ContentHash.Proof(path, operation));
        }
    }

    [Test] public void UndoNoticesASilentlyReplacedFile()
    {
        string path = Settled("model.fbx", A);
        Assert.AreEqual(Reference(A), ContentHash.Advisory(path));
        var state = VersionSwitchFileUndo.Save(path, Path.Combine(folder, "undo"), "before");
        Assert.IsTrue(VersionSwitchFileUndo.IsAt(path, state));
        ReplaceSilently(path, B);
        Assert.IsFalse(VersionSwitchFileUndo.IsAt(path, state), "Undo would overwrite bytes it never saved.");
        var again = VersionSwitchFileUndo.Save(path, Path.Combine(folder, "undo"), "after", state);
        Assert.AreNotEqual(state.copy, again.copy, "A changed file gets its own copy.");
        CollectionAssert.AreEqual(B, File.ReadAllBytes(again.copy));
    }

    [Test] public void BackupChecksNoticeASilentlyReplacedFile()
    {
        string target = Settled("base.fbx", A);
        string backup = target + FileManagerService.OriginalSuffix;
        File.Copy(target, backup);
        var manager = new FileManagerService();
        Assert.AreEqual(Reference(A), ContentHash.Advisory(target));
        Assert.AreEqual(Reference(A), ContentHash.Advisory(backup));
        Assert.IsTrue(manager.FbxMatchesBackupAtPath(target));
        Assert.AreEqual(FileManagerService.OriginalBackupState.MatchesBackup, manager.RefreshStaleOriginalBackup(target, null));

        ReplaceSilently(target, B);
        Assert.IsFalse(manager.FbxMatchesBackupAtPath(target), "Reset would skip restoring the original.");
        Assert.AreNotEqual(FileManagerService.OriginalBackupState.MatchesBackup, manager.RefreshStaleOriginalBackup(target, null));
        CollectionAssert.AreEqual(A, File.ReadAllBytes(backup));
    }

    [Test] public void VerifiedCopiesRefuseASourceReplacedAfterItWasHashed()
    {
        string source = Settled("source.fbx", A), destination = Path.Combine(folder, "destination.fbx");
        string hash = ContentHash.Proof(source);
        ReplaceSilently(source, B);
        Assert.Throws<InvalidDataException>(() => FileManagerService.WriteVerifiedCopy(source, destination, hash, replaceExisting: false));
        Assert.IsFalse(File.Exists(destination));
    }

    [Test] public void BlobAndManifestChecksNoticeASilentlyReplacedFile()
    {
        string blob = Settled("blob.bin", A);
        Assert.IsTrue(ContentHash.Verify(blob, Reference(A), A.Length));
        ReplaceSilently(blob, B);
        Assert.IsFalse(ContentHash.Verify(blob, Reference(A), A.Length), "Reused blobs and manifest outputs are read again.");
    }

    [Test] public void AFailedReadDropsEveryCachedHashOfTheFile()
    {
        string path = Settled("locked.bin", A);
        Assert.AreEqual(Reference(A), ContentHash.Advisory(path));
        using (var operation = new ContentHash.Operation())
        {
            operation.Record(path, Reference(B));
            DateTime written = File.GetLastWriteTimeUtc(path);
            File.SetLastWriteTimeUtc(path, written.AddSeconds(-1)); // the recorded stamp no longer matches: a read is due
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => ContentHash.Proof(path, operation));
                Assert.IsNull(ContentHash.GetIfCached(path), "The advisory entry went with the failed read.");
            }
            File.SetLastWriteTimeUtc(path, written);
            Assert.AreEqual(Reference(A), ContentHash.Proof(path, operation), "The operation's entry went too: the bytes are read.");
        }
    }
}
