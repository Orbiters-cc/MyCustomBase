using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

/// <summary>
/// The custom-version backup's file part (UserCustomVersionService.WriteBackup), without the asset database: it is the
/// user's only copy of a custom model, so no save may overwrite or remove another's.
/// </summary>
public sealed class UserCustomVersionBackupTests
{
    string root, source;

    [SetUp] public void SetUp()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-user-versions-" + Guid.NewGuid().ToString("N"));
        root = Path.Combine(folder, "userVersions");
        source = Path.Combine(folder, "Body.fbx");
        Directory.CreateDirectory(folder);
    }

    [TearDown] public void TearDown()
    {
        string folder = Path.GetDirectoryName(root);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    void Model(string text)
    {
        File.WriteAllText(source, text);
        File.WriteAllText(source + ".meta", "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nModelImporter:\n");
    }

    static string Hash(string text) => MCBHashing.HashBytes(Encoding.UTF8.GetBytes(text));

    [Test] public void TwoSavesInTheSameSecondKeepBothBackups()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        Model("first model");
        var first = UserCustomVersionService.WriteBackup(root, source, Hash("first model"), now);
        Model("second model");
        var second = UserCustomVersionService.WriteBackup(root, source, Hash("second model"), now);

        Assert.AreNotEqual(first.folder, second.folder);
        Assert.AreEqual("first model", File.ReadAllText(first.fbx));
        Assert.AreEqual("second model", File.ReadAllText(second.fbx));
        string firstMeta = File.ReadAllText(first.fbx + ".meta"), secondMeta = File.ReadAllText(second.fbx + ".meta");
        StringAssert.DoesNotContain("0123456789abcdef0123456789abcdef", firstMeta, "The copied import settings get a GUID of their own.");
        Assert.AreNotEqual(firstMeta, secondMeta);
    }

    [Test] public void AFailedVerificationLeavesOlderBackupsUntouched()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        Model("first model");
        var first = UserCustomVersionService.WriteBackup(root, source, Hash("first model"), now);
        var before = Directory.GetFileSystemEntries(root).OrderBy(p => p).ToArray();

        Model("changed model"); // not what was hashed: the copy fails verification
        Assert.Throws<InvalidDataException>(() => UserCustomVersionService.WriteBackup(root, source, Hash("expected model"), now));

        CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(root).OrderBy(p => p).ToArray(), "Only the failed save's own folder went.");
        Assert.AreEqual("first model", File.ReadAllText(first.fbx));
        Assert.IsTrue(File.Exists(first.fbx + ".meta"));
    }

    [Test] public void AFailedSaveKeepsAFileItDidNotWrite()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        Model("changed model");
        // A missing source makes the copy itself fail: the new folder is removed, nothing else is touched.
        File.Delete(source);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "unrelated.txt"), "keep");
        Assert.Throws<FileNotFoundException>(() => UserCustomVersionService.WriteBackup(root, source, Hash("changed model"), now));
        CollectionAssert.AreEqual(new[] { Path.Combine(root, "unrelated.txt") }, Directory.GetFileSystemEntries(root));
    }
}
