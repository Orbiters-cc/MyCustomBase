#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public class AvatarBaseSourceWorkflowTests
{
    private const string TestRoot = "Assets/__MCBAvatarBaseSourceTests";
    private static readonly string HashA = new string('a', 64);
    private static readonly string HashB = new string('b', 64);
    private static readonly string HashC = new string('c', 64);

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(TestRoot);
        string fullPath = MCBUtils.ResolveProjectAssetFullPath(TestRoot + "/placeholder");
        string directory = Path.GetDirectoryName(fullPath);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }

    [Test]
    public void MatcherRejectsAmbiguousRepeatedHashes()
    {
        var matches = AvatarBaseSourceMatcher.MatchOneToOne(
            new[]
            {
                new AvatarBaseSourceMatcher.SourceFile { path = "Assets/Base/body.fbx", hash = HashA },
                new AvatarBaseSourceMatcher.SourceFile { path = "Assets/Base/head.fbx", hash = HashA }
            },
            new[]
            {
                new AvatarBaseSourceMatcher.LocalFile { path = "Assets/Avatar/one.fbx", hash = HashA },
                new AvatarBaseSourceMatcher.LocalFile { path = "Assets/Avatar/two.fbx", hash = HashA }
            });

        Assert.That(matches, Is.Null);
    }

    [Test]
    public void DetectionMatchesRenamedDefaultFbxByUniqueHash()
    {
        var avatarBases = new List<CreatorAvatarBaseOption>
        {
            new CreatorAvatarBaseOption
            {
                id = 42,
                name = "Winterpaw",
                sourceRevisions = new List<CreatorAvatarBaseSourceRevisionOption>
                {
                    new CreatorAvatarBaseSourceRevisionOption
                    {
                        id = 7,
                        sourceFiles = new List<CreatorAvatarBaseSourceFileOption>
                        {
                            new CreatorAvatarBaseSourceFileOption
                            {
                                canonicalPath = "Assets/MasculineCanine/FX/MasculineCanine.v1.5.fbx",
                                hash = HashA
                            },
                            new CreatorAvatarBaseSourceFileOption
                            {
                                canonicalPath = "Assets/MasculineCanine/FX/MasculineCanineHead.v1.5.fbx",
                                hash = HashB,
                                position = 1
                            }
                        }
                    }
                }
            }
        };
        var localFiles = new List<AvatarBaseSourceMatcher.LocalFile>
        {
            new AvatarBaseSourceMatcher.LocalFile { path = "Assets/ultipaw.fbx", hash = HashA },
            new AvatarBaseSourceMatcher.LocalFile { path = "Assets/ultihead.fbx", hash = HashB },
            new AvatarBaseSourceMatcher.LocalFile { path = "Assets/Clothing/unrelated.fbx", hash = HashC }
        };

        var detection = AvatarBaseDetectionService.DetectUniqueBase(avatarBases, localFiles);

        Assert.That(detection, Is.Not.Null);
        Assert.That(detection.avatarBaseId, Is.EqualTo(42));
        Assert.That(detection.sourceRevisionId, Is.EqualTo(7));
        Assert.That(detection.matches.Count, Is.EqualTo(2));
    }

    [Test]
    public void DetectionPrefersTheMostSpecificCompleteRevision()
    {
        var avatarBase = new CreatorAvatarBaseOption
        {
            id = 42,
            name = "Winterpaw",
            sourceRevisions = new List<CreatorAvatarBaseSourceRevisionOption>
            {
                new CreatorAvatarBaseSourceRevisionOption
                {
                    id = 7,
                    sourceFiles = new List<CreatorAvatarBaseSourceFileOption>
                    {
                        new CreatorAvatarBaseSourceFileOption { canonicalPath = "Assets/Base/body.fbx", hash = HashA }
                    }
                },
                new CreatorAvatarBaseSourceRevisionOption
                {
                    id = 8,
                    sourceFiles = new List<CreatorAvatarBaseSourceFileOption>
                    {
                        new CreatorAvatarBaseSourceFileOption { canonicalPath = "Assets/Base/body.fbx", hash = HashA },
                        new CreatorAvatarBaseSourceFileOption { canonicalPath = "Assets/Base/hoodie.fbx", hash = HashB, position = 1 }
                    }
                }
            }
        };

        var detection = AvatarBaseDetectionService.DetectUniqueRevision(
            avatarBase,
            0,
            new List<AvatarBaseSourceMatcher.LocalFile>
            {
                new AvatarBaseSourceMatcher.LocalFile { path = "Assets/Renamed/body.fbx", hash = HashA },
                new AvatarBaseSourceMatcher.LocalFile { path = "Assets/Renamed/hoodie.fbx", hash = HashB }
            });

        Assert.That(detection, Is.Not.Null);
        Assert.That(detection.sourceRevisionId, Is.EqualTo(8));
        Assert.That(detection.matches.Count, Is.EqualTo(2));
    }

    [Test]
    public void ProjectPathValidationRejectsTraversal()
    {
        Assert.That(MCBUtils.TryResolveProjectAssetPath("Assets/Avatar/base.fbx", out _, out _), Is.True);
        Assert.That(MCBUtils.TryResolveProjectAssetPath("Assets/../../outside.fbx", out _, out _), Is.False);
        Assert.That(MCBUtils.TryResolveProjectAssetPath("C:/outside.fbx", out _, out _), Is.False);
        Assert.That(FileManagerService.GetPreMcbBackupPath("Assets/Avatar/base.fbx", "../../outside"), Is.Null);
    }

    [Test]
    public void OriginalBaseKeyIsImmutableAndHashValidated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MCBUtils.ResolveProjectAssetFullPath(TestRoot + "/placeholder")));
        string targetUnityPath = TestRoot + "/avatar.bytes";
        string targetFullPath = MCBUtils.ResolveProjectAssetFullPath(targetUnityPath);
        string sourcePath = Path.Combine(Path.GetTempPath(), "mcb-original-" + Guid.NewGuid().ToString("N") + ".fbx");
        try
        {
            File.WriteAllBytes(targetFullPath, new byte[] { 9, 9, 9 });
            File.WriteAllBytes(sourcePath, new byte[] { 1, 2, 3, 4 });
            var service = new FileManagerService();
            string sourceHash = MCBUtils.CalculateFileHash(sourcePath);

            Assert.That(service.EnsureOriginalBaseKey(targetUnityPath, sourcePath, sourceHash), Is.True);
            Assert.That(service.EnsureOriginalBaseKey(targetUnityPath, sourcePath, sourceHash), Is.False);

            File.WriteAllBytes(sourcePath, new byte[] { 5, 6, 7, 8 });
            string changedHash = MCBUtils.CalculateFileHash(sourcePath);
            Assert.Throws<InvalidDataException>(() =>
                service.EnsureOriginalBaseKey(targetUnityPath, sourcePath, changedHash));
            Assert.That(MCBUtils.CalculateFileHash(FileManagerService.GetOriginalBasePath(targetFullPath)), Is.EqualTo(sourceHash));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
        }
    }

    [Test]
    public void DirectFbxReplacementStagesOutputAndPreservesTheOriginalKey()
    {
        string directory = Path.GetDirectoryName(MCBUtils.ResolveProjectAssetFullPath(TestRoot + "/placeholder"));
        Directory.CreateDirectory(directory);
        string targetPath = MCBUtils.ResolveProjectAssetFullPath(TestRoot + "/replace-target.bytes");
        string customPath = Path.Combine(Path.GetTempPath(), "mcb-replacement-" + Guid.NewGuid().ToString("N") + ".fbx");
        byte[] original = { 1, 2, 3, 4 };
        byte[] replacement = { 8, 7, 6, 5 };
        try
        {
            File.WriteAllBytes(targetPath, original);
            File.WriteAllBytes(customPath, replacement);
            var service = new FileManagerService();

            Assert.That(service.ReplaceFbxWithCustomCopy(targetPath, customPath), Is.True);
            Assert.That(File.ReadAllBytes(targetPath), Is.EqualTo(replacement));
            Assert.That(File.ReadAllBytes(FileManagerService.GetOriginalBasePath(targetPath)), Is.EqualTo(original));
            Assert.That(Directory.GetFiles(directory, "*.pending-*"), Is.Empty);
        }
        finally
        {
            if (File.Exists(customPath)) File.Delete(customPath);
        }
    }

    [Test]
    public void LocalPathWinsOverGuidAndUnresolvedOverrideFailsClosed()
    {
        string directory = Path.GetDirectoryName(MCBUtils.ResolveProjectAssetFullPath(TestRoot + "/placeholder"));
        Directory.CreateDirectory(directory);
        string referencePath = TestRoot + "/reference.bytes";
        string localPath = TestRoot + "/local.bytes";
        string guidPath = TestRoot + "/guid-target.bytes";
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(referencePath), new byte[] { 1 });
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(localPath), new byte[] { 2 });
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(guidPath), new byte[] { 3 });
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var owner = new GameObject("MCB override test");
        try
        {
            var target = owner.AddComponent<MyCustomBase>();
            target.avatarPathOverrides.Add(new AvatarPathOverrideEntry
            {
                sourceModelFileId = 10,
                referenceSourcePath = referencePath,
                referenceHash = HashA,
                localTargetPath = localPath,
                localTargetGuid = AssetDatabase.AssetPathToGUID(guidPath)
            });

            Assert.That(
                AvatarPathOverrideService.ResolveLocalTargetPath(target, referencePath, 10, HashA),
                Is.EqualTo(localPath));

            File.Delete(MCBUtils.ResolveProjectAssetFullPath(localPath));
            target.avatarPathOverrides[0].localTargetGuid = "missing-guid";
            Assert.That(
                AvatarPathOverrideService.ResolveLocalTargetPath(target, referencePath, 10, HashA),
                Is.Null);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(owner);
        }
    }

    [Test]
    public void GuidFallbackFailsClosedWhenManagedSidecarsCannotMigrate()
    {
        string directory = Path.GetDirectoryName(MCBUtils.ResolveProjectAssetFullPath(TestRoot + "/placeholder"));
        Directory.CreateDirectory(directory);
        string oldPath = TestRoot + "/old-target.bytes";
        string newPath = TestRoot + "/new-target.bytes";
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(oldPath), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(FileManagerService.GetOriginalBasePath(oldPath)), new byte[] { 4 });
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(FileManagerService.GetOriginalBasePath(newPath)), new byte[] { 5 });
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        string guid = AssetDatabase.AssetPathToGUID(oldPath);
        Assert.That(AssetDatabase.MoveAsset(oldPath, newPath), Is.Empty);

        var owner = new GameObject("MCB sidecar conflict test");
        try
        {
            var target = owner.AddComponent<MyCustomBase>();
            target.avatarPathOverrides.Add(new AvatarPathOverrideEntry
            {
                sourceModelFileId = 10,
                referenceSourcePath = TestRoot + "/reference.bytes",
                referenceHash = HashA,
                localTargetPath = oldPath,
                localTargetGuid = guid
            });

            LogAssert.Expect(
                LogType.Error,
                "[AvatarPathOverride] Could not update moved target path: Destination sidecar already exists with different content: " +
                FileManagerService.GetOriginalBasePath(newPath));
            Assert.That(
                AvatarPathOverrideService.ResolveLocalTargetPath(
                    target,
                    target.avatarPathOverrides[0].referenceSourcePath,
                    10,
                    HashA),
                Is.Null);
            Assert.That(target.avatarPathOverrides[0].localTargetPath, Is.EqualTo(oldPath));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(owner);
        }
    }

    [Test]
    public void DefaultBaseCommitCreatesAnImmutableOriginalKey()
    {
        string targetPath = TestRoot + "/default-base.bytes";
        Directory.CreateDirectory(Path.GetDirectoryName(MCBUtils.ResolveProjectAssetFullPath(targetPath)));
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(targetPath), new byte[] { 4, 3, 2, 1 });
        AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        string hash = MCBUtils.CalculateFileHash(MCBUtils.ResolveProjectAssetFullPath(targetPath));

        var owner = new GameObject("MCB default setup test");
        try
        {
            var target = owner.AddComponent<MyCustomBase>();
            using (var setup = new CustomBaseSourceSetupTransaction(target))
            {
                setup.CommitDefaultBase(
                    new[]
                    {
                        new ModelFileData
                        {
                            id = 1,
                            path = targetPath,
                            hash = hash,
                            type = "FBX",
                            role = "SOURCE"
                        }
                    },
                    new[] { targetPath });
                setup.Complete();
            }

            Assert.That(File.Exists(MCBUtils.ResolveProjectAssetFullPath(
                FileManagerService.GetOriginalBasePath(targetPath))), Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(owner);
        }
    }

    [Test]
    public void PreMcbRecoveryRestoresTheAdoptedCustomFile()
    {
        string targetPath = TestRoot + "/adopted-custom.bytes";
        Directory.CreateDirectory(Path.GetDirectoryName(MCBUtils.ResolveProjectAssetFullPath(targetPath)));
        byte[] preMcbBytes = { 9, 7, 5, 3 };
        File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(targetPath), preMcbBytes);
        AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        string sourcePath = Path.Combine(Path.GetTempPath(), "mcb-recovery-source-" + Guid.NewGuid().ToString("N") + ".fbx");
        File.WriteAllBytes(sourcePath, new byte[] { 1, 2, 3, 4 });
        string sourceHash = MCBUtils.CalculateFileHash(sourcePath);

        var owner = new GameObject("MCB recovery test");
        try
        {
            var target = owner.AddComponent<MyCustomBase>();
            var source = new ModelFileData
            {
                id = 5,
                path = "Assets/Base/default.fbx",
                hash = sourceHash,
                type = "FBX",
                role = "SOURCE"
            };
            using (var setup = new CustomBaseSourceSetupTransaction(target))
            {
                setup.CommitAlreadyCustomized(
                    new[] { source },
                    new[]
                    {
                        new CustomBaseSourceSetupTransaction.SourceKeyInstallRequest
                        {
                            referenceSourcePath = source.path,
                            referenceHash = source.hash,
                            localTargetPath = targetPath,
                            externalSourcePath = sourcePath,
                            sourceImportKind = AvatarPathOverrideService.SourceImportKindRawFbx
                        }
                    });
                setup.Complete();
            }

            File.WriteAllBytes(MCBUtils.ResolveProjectAssetFullPath(targetPath), new byte[] { 8, 8, 8, 8 });
            Assert.That(AvatarPathOverrideService.RestorePreMcbBackups(target), Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(MCBUtils.ResolveProjectAssetFullPath(targetPath)), Is.EqualTo(preMcbBytes));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            UnityEngine.Object.DestroyImmediate(owner);
        }
    }

    [Test]
    public void UnityPackageExtractionPreservesPathsAndAvoidsFlatNameCollisions()
    {
        string packagePath = CreateUnityPackage(
            ("aaaaaaaa", "Assets/AvatarA/shared.fbx", new byte[] { 1, 2, 3 }),
            ("bbbbbbbb", "Assets/AvatarB/shared.fbx", new byte[] { 4, 5, 6 }));
        UnityPackageFbxSourceExtractor.ExtractionResult extraction = null;
        try
        {
            extraction = UnityPackageFbxSourceExtractor.ExtractFbxEntries(packagePath);
            Assert.That(extraction.entries.Select(entry => entry.publishedSourcePath), Is.EquivalentTo(new[]
            {
                "Assets/AvatarA/shared.fbx",
                "Assets/AvatarB/shared.fbx"
            }));
            Assert.That(extraction.entries.Select(entry => entry.tempPath).Distinct().Count(), Is.EqualTo(2));
            Assert.That(Directory.Exists(extraction.extractionRoot), Is.True);

            string extractionRoot = extraction.extractionRoot;
            extraction.Dispose();
            extraction = null;
            Assert.That(Directory.Exists(extractionRoot), Is.False);
        }
        finally
        {
            extraction?.Dispose();
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }
    }

    [Test]
    public void UnityPackageExtractionRejectsTraversalAndCleansItsSession()
    {
        string packagePath = CreateUnityPackage(
            ("cccccccc", "Assets/../../outside.fbx", new byte[] { 1, 2, 3 }));
        string extractionParent = Path.Combine(Path.GetTempPath(), "mcb_source_keys");
        var before = Directory.Exists(extractionParent)
            ? new HashSet<string>(Directory.GetDirectories(extractionParent), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                UnityPackageFbxSourceExtractor.ExtractFbxEntries(packagePath));
            var after = Directory.Exists(extractionParent)
                ? new HashSet<string>(Directory.GetDirectories(extractionParent), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.That(after.SetEquals(before), Is.True);
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }
    }

    private static string CreateUnityPackage(params (string objectId, string pathname, byte[] bytes)[] entries)
    {
        string packagePath = Path.Combine(Path.GetTempPath(), "mcb-package-" + Guid.NewGuid().ToString("N") + ".unitypackage");
        using (var file = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        {
            foreach (var entry in entries)
            {
                WriteTarEntry(gzip, entry.objectId + "/pathname", Encoding.UTF8.GetBytes(entry.pathname));
                WriteTarEntry(gzip, entry.objectId + "/asset", entry.bytes);
            }
            gzip.Write(new byte[1024], 0, 1024);
        }
        return packagePath;
    }

    private static void WriteTarEntry(Stream output, string name, byte[] bytes)
    {
        byte[] header = new byte[512];
        WriteAscii(header, 0, 100, name);
        WriteOctal(header, 100, 8, 420);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, bytes?.Length ?? 0);
        WriteOctal(header, 136, 12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        for (int index = 148; index < 156; index++) header[index] = 32;
        header[156] = (byte)'0';
        WriteAscii(header, 257, 6, "ustar");
        int checksum = header.Sum(value => (int)value);
        WriteOctal(header, 148, 8, checksum);
        output.Write(header, 0, header.Length);

        if (bytes != null && bytes.Length > 0) output.Write(bytes, 0, bytes.Length);
        int padding = (512 - ((bytes?.Length ?? 0) % 512)) % 512;
        if (padding > 0) output.Write(new byte[padding], 0, padding);
    }

    private static void WriteAscii(byte[] target, int offset, int length, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        Array.Copy(bytes, 0, target, offset, Math.Min(length, bytes.Length));
    }

    private static void WriteOctal(byte[] target, int offset, int length, long value)
    {
        string text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        WriteAscii(target, offset, length - 1, text);
        target[offset + length - 1] = 0;
    }
}
#endif
