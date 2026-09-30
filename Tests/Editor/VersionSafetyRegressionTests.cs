using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class VersionSafetyRegressionTests
{
    private string folder;
    [SetUp] public void SetUp() { folder = Path.Combine(Path.GetTempPath(), "mcb-safety-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
    [TearDown] public void TearDown() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    [TestCase("../escape")]
    [TestCase("..\\escape")]
    [TestCase("/absolute")]
    [TestCase("C:\\absolute")]
    [TestCase("..")]
    [TestCase("trailing.")]
    [TestCase("label ")]
    public void VersionLabelsCannotBecomePaths(string label)
    {
        Assert.Throws<ArgumentException>(() => MCBUtils.GetVersionDataPath(1, label, "base"));
        Assert.Throws<ArgumentException>(() => MCBUtils.GetVersionDataPath(1, "version", label));
    }

    [Test] public void ValidVersionLabelKeepsCanonicalPath()
    {
        StringAssert.EndsWith("/1/versions/uRelease 1.2dbase-v3", MCBUtils.GetVersionDataPath(1, "Release 1.2", "base-v3"));
    }

    [TestCase("../escape")]
    [TestCase("..\\escape")]
    [TestCase("C:/escape")]
    [TestCase(".. /escape")]
    [TestCase("dir./escape")]
    [TestCase("NUL.txt")]
    public void PayloadPathsCannotEscape(string path) => Assert.Throws<InvalidDataException>(() => VersionStorage.ContainedPath(folder, path));

    [Test] public void CorruptMemoryArchivePreservesExistingDestination()
    {
        File.WriteAllText(Path.Combine(folder, "old.txt"), "previous");
        Assert.Throws<InvalidDataException>(() => new FileManagerService().UnzipAndMoveFromMemory(new byte[] { 1, 2, 3 }, folder));
        Assert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "old.txt")));
    }

    [TestCase("../escape.txt")]
    [TestCase("root/../../escape.txt")]
    public void ArchiveTraversalPreservesExistingDestination(string entry)
    {
        File.WriteAllText(Path.Combine(folder, "old.txt"), "previous");
        Assert.Throws<InvalidDataException>(() => new FileManagerService().UnzipAndMoveFromMemory(Archive(entry), folder));
        Assert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "old.txt")));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ValidationFailurePreservesOldCacheForBothExtractionPaths(bool memory)
    {
        File.WriteAllText(Path.Combine(folder, "old.txt"), "previous");
        var manager = new FileManagerService();
        Action<string> fail = _ => throw new InvalidDataException("Missing payload");
        if (memory) Assert.Throws<InvalidDataException>(() => manager.UnzipAndMoveFromMemory(Archive("root/new.txt"), folder, null, fail));
        else
        {
            string archive = Path.Combine(folder, "download.zip"); File.WriteAllBytes(archive, Archive("root/new.txt"));
            Assert.Throws<InvalidDataException>(() => manager.UnzipAndMove(archive, folder + "-unused", folder, fail));
        }
        Assert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "old.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "new.txt")));
    }

    [Test] public void ValidArchiveCommitsOnlyAfterValidation()
    {
        File.WriteAllText(Path.Combine(folder, "old.txt"), "previous");
        bool validated = false;
        new FileManagerService().UnzipAndMoveFromMemory(Archive("root/new.txt"), folder, null, staged =>
        {
            Assert.True(File.Exists(Path.Combine(folder, "old.txt")));
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(staged, "new.txt")));
            validated = true;
        });
        Assert.True(validated);
        Assert.False(File.Exists(Path.Combine(folder, "old.txt")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(folder, "new.txt")));
    }

    [Test] public void CacheNeedsMetadataManifestAndEveryRequiredPayload()
    {
        var version = new CustomBaseVersion { assetId = 7, version = "v1", defaultAviVersion = "base", versionFiles = new[] { new ModelFileData { path = "mesh.bin" } } };
        File.WriteAllText(Path.Combine(folder, "README"), "not a download");
        Assert.False(VersionStorage.IsComplete(folder, version));
        VersionRepository.SaveVersionJson(folder, version);
        File.WriteAllText(Path.Combine(folder, "mesh.bin"), "payload");
        var manifest = VersionRepository.CreateManifestFromFolder(folder, version, false, null, null);
        manifest.Save(folder);
        Assert.True(VersionStorage.IsComplete(folder, version));
        File.WriteAllText(Path.Combine(folder, "mesh.bin"), "truncated");
        Assert.False(VersionStorage.IsComplete(folder, version));
        File.Delete(Path.Combine(folder, "mesh.bin"));
        Assert.False(VersionStorage.IsComplete(folder, version));
    }

    [Test] public void FailedDirectoryCommitRestoresPreviousDirectory()
    {
        File.WriteAllText(Path.Combine(folder, "old.txt"), "previous");
        Assert.Throws<DirectoryNotFoundException>(() => VersionStorage.ReplaceDirectory(folder + "-missing", folder));
        Assert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "old.txt")));
    }

    [Test] public void RollbackRestoresAllPreviousCustomFilesAndTheirMetadata()
    {
        // Text assets exercise the same byte snapshot without invoking the FBX importer on synthetic data.
        string assetFolder = "Assets/MCB_RollbackTest_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(assetFolder);
        try
        {
            string[] paths = { assetFolder + "/body.txt", assetFolder + "/clothing.txt" };
            foreach (var path in paths) { File.WriteAllText(path, "version B"); File.WriteAllText(path + FileManagerService.OriginalSuffix, "original A"); AssetDatabase.ImportAsset(path); }
            var metas = paths.Select(path => File.ReadAllText(path + ".meta")).ToArray();
            var type = typeof(VersionActions).GetNestedType("VersionTransitionRollbackSnapshot", BindingFlags.NonPublic);
            var snapshot = type.GetMethod("Capture").Invoke(null, new object[] { paths, null, null });
            foreach (var path in paths) File.WriteAllText(path, "partial version C");
            type.GetMethod("Rollback").Invoke(snapshot, null);
            for (int i = 0; i < paths.Length; i++)
            {
                Assert.AreEqual("version B", File.ReadAllText(paths[i]));
                Assert.AreEqual(metas[i], File.ReadAllText(paths[i] + ".meta"));
                Assert.AreEqual("original A", File.ReadAllText(paths[i] + FileManagerService.OriginalSuffix));
            }
        }
        finally { AssetDatabase.DeleteAsset(assetFolder); if (Directory.Exists(assetFolder)) Directory.Delete(assetFolder, true); }
    }

    // ---- Archive expansion budget: counted as entries decompress, never from the sizes the archive declares

    [Test] public void ArchiveWithMoreFilesThanTheBudgetFailsAndKeepsTheCache()
    {
        File.WriteAllText(Path.Combine(folder, "old.txt"), "previous");
        byte[] archive = Archive(("root/a.txt", new byte[1]), ("root/b.txt", new byte[1]), ("root/c.txt", new byte[1]));
        var ex = Assert.Throws<InvalidDataException>(() =>
            new FileManagerService().UnzipAndMoveFromMemory(archive, folder, budget: new VersionArchiveBudget(maxEntries: 2)));
        StringAssert.Contains("files", ex.Message);
        Assert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "old.txt")));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExpandedBytesAreCountedWhileExtracting(bool total)
    {
        File.WriteAllText(Path.Combine(folder, "old.txt"), "previous");
        // A megabyte of zeros compresses to about a kilobyte: only what actually decompresses counts.
        byte[] archive = Archive(("root/zeros.bin", new byte[1024 * 1024]));
        Assert.Less(archive.Length, 64 * 1024);
        var budget = total ? new VersionArchiveBudget(maxTotalBytes: 512 * 1024) : new VersionArchiveBudget(maxEntryBytes: 512 * 1024);
        Assert.Throws<InvalidDataException>(() => new FileManagerService().UnzipAndMoveFromMemory(archive, folder, budget: budget));
        Assert.AreEqual("previous", File.ReadAllText(Path.Combine(folder, "old.txt")));
        Assert.LessOrEqual(budget.TotalBytes, 512 * 1024);
    }

    [Test] public void CapturesThatDoNotFitInMemoryAreOnlyWrittenToDisk()
    {
        byte[] archive = Archive(("root/small.bin", new byte[16]), ("root/large.bin", new byte[4096]));
        var captured = new FileManagerService().UnzipAndMoveFromMemory(archive, folder,
            new HashSet<string> { "small.bin", "large.bin" }, null, new VersionArchiveBudget(maxCapturedBytes: 1024));
        CollectionAssert.AreEquivalent(new[] { "small.bin" }, captured.Keys);
        Assert.AreEqual(16, captured["small.bin"].Length);
        Assert.AreEqual(4096, new FileInfo(Path.Combine(folder, "large.bin")).Length);
    }

    // ---- Native mesh payloads: counts are checked against the payload before anything is allocated

    [Test] public void WellFormedNativeMeshPayloadParses()
    {
        var prepared = (NativeMeshPayloadService.PreparedPayloadAssetData)ParsePayload(NativePayload());
        var mesh = prepared.renderers.Single().mesh;
        Assert.AreEqual(3, mesh.vertices.Length);
        var frame = mesh.blendShapes.Single().frames.Single();
        Assert.AreEqual(3, frame.deltaVertices.Length);
        Assert.AreEqual(Vector3.up, frame.deltaVertices[0]);
        Assert.AreEqual(Vector3.zero, frame.deltaVertices[2]);
    }

    [TestCase("vertices")]
    [TestCase("sparse length")]
    [TestCase("sparse count")]
    [TestCase("sparse index")]
    [TestCase("string")]
    [TestCase("bones")]
    public void CraftedNativeMeshPayloadCountsFailBeforeAllocating(string corrupt)
    {
        var ex = Assert.Throws<TargetInvocationException>(() => ParsePayload(NativePayload(corrupt)));
        Assert.IsInstanceOf<InvalidDataException>(ex.InnerException, ex.InnerException?.ToString());
    }

    private static object ParsePayload(byte[] payload)
    {
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
        return typeof(NativeMeshPayloadService).GetMethod("ReadPreparedPayloadAsset", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { payload, "Test", hash, NativeMeshPayloadService.PayloadCompressionNone,
                new NativeMeshPayloadService.NativeMeshPayloadPreparationStatus(), 0f, 1f });
    }

    // One renderer, one triangle, one blendshape frame: the payload format NativeMeshPayloadService writes.
    private static byte[] NativePayload(string corrupt = null)
    {
        using (var memory = new MemoryStream())
        {
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write("MCB_NATIVE_MESH_PAYLOAD"); writer.Write(1); writer.Write("source.fbx"); writer.Write("source-hash");
                writer.Write(1);
                foreach (string name in new[] { "Body", "Body", "Body", "Body" }) writer.Write(name);
                foreach (float value in new[] { 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f }) writer.Write(value);
                if (corrupt == "string") writer.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }); // a 268 MB root bone name
                else writer.Write("Hips");
                writer.Write(0);
                writer.Write("Body"); writer.Write(0);
                writer.Write(corrupt == "vertices" ? int.MaxValue : 3);
                foreach (float value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }) writer.Write(value);
                writer.Write(0); writer.Write(0); writer.Write(0);
                for (int channel = 0; channel < 8; channel++) writer.Write(0);
                writer.Write(0); writer.Write(0);
                writer.Write(1); writer.Write(0); writer.Write(3); writer.Write(0); writer.Write(1); writer.Write(2);
                for (int i = 0; i < 6; i++) writer.Write(0f);
                writer.Write(1); writer.Write("Smile"); writer.Write(1); writer.Write(100f);
                writer.Write(corrupt == "sparse length" ? int.MaxValue : 3);
                writer.Write(corrupt == "sparse count" ? int.MaxValue : 1);
                writer.Write(corrupt == "sparse index" ? 7 : 0); writer.Write(0f); writer.Write(1f); writer.Write(0f);
                writer.Write(false); writer.Write(false);
                writer.Write(corrupt == "bones" ? int.MaxValue : 0);
                writer.Write(0);
            }
            return memory.ToArray();
        }
    }

    // ---- Persistent cache: no credentials, and hashes tied to what was hashed

    [Test] public void VersionCacheIsKeyedByAnAccountKeyNeverTheToken()
    {
        var cache = PersistentCache.Instance;
        string key = cache.AccountKey("orbit-secret-token");
        Assert.That(key, Is.Not.Empty.And.Not.Contains("orbit-secret-token"));
        Assert.AreEqual(key, cache.AccountKey("orbit-secret-token"));
        Assert.AreNotEqual(key, cache.AccountKey("orbit-other-token"));
        Assert.IsNull(typeof(VersionCacheEntry).GetField("authToken"));
        var entry = new VersionCacheEntry("model-hash", new List<CustomBaseVersion>(), null, key, 14);
        StringAssert.DoesNotContain("orbit-secret-token", JsonUtility.ToJson(entry));
        Assert.IsTrue(entry.IsValid("model-hash", key, 14, TimeSpan.FromHours(1)));
        Assert.IsFalse(entry.IsValid("model-hash", cache.AccountKey("orbit-other-token"), 14, TimeSpan.FromHours(1)));
        Assert.IsFalse(new VersionCacheEntry("model-hash", null, null, null, 14).IsValid("model-hash", null, 14, TimeSpan.FromHours(1)),
            "Entries without an account key (written with the token) are discarded.");
    }

    [Test] public void CachedHashIsOnlyValidForTheContentsThatWereHashed()
    {
        string file = Path.Combine(folder, "model.fbx");
        File.WriteAllText(file, "version A");
        Assert.IsTrue(CachedFileStamp.TryRead(file, out var hashed));
        var entry = new HashCacheEntry(file, "hash of A", hashed);
        Assert.IsTrue(entry.IsValid());
        Assert.IsFalse(new HashCacheEntry { filePath = file, hash = "hash of A" }.IsValid(), "Entries without a stamp are stale.");

        DateTime written = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "version B, longer");
        File.SetLastWriteTimeUtc(file, written);
        Assert.IsFalse(entry.IsValid(), "Same write time, different length.");
        File.WriteAllText(file, "version C");
        File.SetLastWriteTimeUtc(file, written.AddSeconds(5));
        Assert.IsFalse(entry.IsValid(), "Same length, different write time.");

        // A hash computed before the file changed is never cached against the new contents.
        PersistentCache.Instance.CacheHash(file, "hash of A", hashed);
        Assert.IsNull(PersistentCache.Instance.GetCachedHash(file));
        Assert.IsTrue(CachedFileStamp.TryRead(file, out var current));
        PersistentCache.Instance.CacheHash(file, "hash of C", current);
        try { Assert.AreEqual("hash of C", PersistentCache.Instance.GetCachedHash(file)); }
        finally { PersistentCache.Instance.InvalidateHash(file); }
    }

    private static byte[] Archive(string name) => Archive((name, Encoding.UTF8.GetBytes("new")));

    private static byte[] Archive(params (string name, byte[] bytes)[] entries)
    {
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var entry in entries)
                    using (var stream = archive.CreateEntry(entry.name).Open())
                        stream.Write(entry.bytes, 0, entry.bytes.Length);
            return memory.ToArray();
        }
    }
}
