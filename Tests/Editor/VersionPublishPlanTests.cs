using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using NUnit.Framework;

public class VersionPublishPlanTests
{
    [Test]
    public void SourcePackagesContainOnlyTheirPayloadAndSharedFiles()
    {
        var artifact = Fixture();
        var parts = VersionPublishPlan.Create(artifact);
        Assert.That(parts.Length, Is.EqualTo(2));
        Assert.That(parts[0].Manifest.outputs.Select(f => f.path), Is.EquivalentTo(new[] { "a.bin", "a.bin.meta", "logic.prefab" }));
        Assert.That(parts[1].Manifest.outputs.Select(f => f.path), Is.EquivalentTo(new[] { "b.bin", "b.bin.meta", "logic.prefab" }));
        Assert.That(parts[1].Metadata.sourceVersionKey, Is.EqualTo("b"));
        Assert.That(artifact.Metadata.originalBaseVersions.Length, Is.EqualTo(2));
        artifact.Manifest.outputs[0].bytes = VersionPublisher.MaxVersionPackageUploadBytes + 1;
        Assert.Throws<InvalidOperationException>(() => VersionPublishPlan.Create(artifact));
    }

    [Test]
    public void LocalSourceViewRequiresADeclaredMatchingVariant()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb-view-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var artifact = Fixture(folder);
            File.WriteAllText(Path.Combine(folder, "version.json"), JsonConvert.SerializeObject(artifact.Metadata));
            foreach (var file in artifact.Manifest.outputs) { File.WriteAllText(Path.Combine(folder, file.path), "x"); file.bytes = 1; }
            artifact.Manifest.Save(folder);
            var view = VersionPublishPlan.Create(artifact)[1].Metadata;
            view.localArtifactSourceVersionKey = "a";
            Assert.That(VersionStorage.IsComplete(folder, view), Is.True);
            view.sourceVersionKey = "undeclared";
            Assert.That(VersionStorage.IsComplete(folder, view), Is.False);
            view.sourceVersionKey = "b";
            File.Delete(Path.Combine(folder, "b.bin"));
            Assert.That(VersionStorage.IsComplete(folder, view), Is.False);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public void ResumeRejectsDifferentVersionSettingsOrPayloads()
    {
        var artifact = Fixture();
        var remote = VersionPublishPlan.Create(artifact)[0].Metadata;
        Assert.DoesNotThrow(() => VersionPublisher.ValidateResume(artifact.Metadata, remote));
        remote.title = "Different";
        Assert.Throws<InvalidOperationException>(() => VersionPublisher.ValidateResume(artifact.Metadata, remote));
        remote.title = artifact.Metadata.title;
        remote.originalBaseVersions[0].versionFiles[0].hash = "changed";
        Assert.Throws<InvalidOperationException>(() => VersionPublisher.ValidateResume(artifact.Metadata, remote));
    }

    [Test]
    public void CancelledPackagingDoesNotProduceAnUpload()
    {
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => new FileManagerService().CreateZipFromManifestOutputs(Fixture(), cancel.Token));
        }
    }

    private static VersionArtifact Fixture(string folder = null)
    {
        var sources = new[] { "a", "b" }.Select(key => new OriginalBaseVersionData {
            key = key, label = key,
            sourceFiles = new[] { new ModelFileData { path = "source.fbx", hash = key } },
            versionFiles = new[] { new ModelFileData { path = key + ".bin", hash = key, role = "PATCH" } }
        }).ToArray();
        var version = new CustomBaseVersion { assetId = 7, version = "1", defaultAviVersion = "1", title = "Test", sourceVersionKey = "a",
            sourceFiles = sources[0].sourceFiles, versionFiles = sources[0].versionFiles, originalBaseVersions = sources };
        var manifest = new VersionManifest { assetId = 7, version = "1", defaultAviVersion = "1" };
        manifest.outputs = new[] { "a.bin", "a.bin.meta", "b.bin", "b.bin.meta", "logic.prefab" }
            .Select(path => new VersionManifestFile { path = path, hash = "verified", bytes = 1 }).ToList();
        return new VersionArtifact(folder ?? Path.GetTempPath(), version, manifest);
    }
}
