using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

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

    private static byte[] Archive(string name)
    {
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("new");
            return memory.ToArray();
        }
    }
}
