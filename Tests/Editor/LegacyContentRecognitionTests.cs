using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class LegacyContentRecognitionTests
{
    static LegacyMigrationService.Release Release(string label, int count) => new LegacyMigrationService.Release {
        label = label, cleanup = new LegacyMigrationService.Cleanup { layers = Enumerable.Range(0, count).Select(i => "Layer " + i).ToList() }
    };
    [Test] public void TieSelectsShorterV4List()
    {
        var v4 = Release("V4", 10); var later = Release("Later", 14);
        Assert.That(LegacyMigrationService.RecognizeRelease(v4.cleanup.layers, new[] { later, v4 }), Is.SameAs(v4));
    }
    [Test] public void MostNamesWinsBeforeListLength()
    {
        var earlier = Release("Earlier", 8); var later = Release("Later", 14);
        Assert.That(LegacyMigrationService.RecognizeRelease(later.cleanup.layers.Take(9), new[] { earlier, later }), Is.SameAs(later));
    }
    [Test] public void SixtyPercentThresholdIsInclusive()
    {
        var r = Release("Release", 15);
        Assert.That(LegacyMigrationService.RecognizeRelease(r.cleanup.layers.Take(8), new[] { r }), Is.Null);
        Assert.That(LegacyMigrationService.RecognizeRelease(r.cleanup.layers.Take(9), new[] { r }), Is.SameAs(r));
    }
    [Test] public void EightNamesRequiredEvenAboveSixtyPercent()
    {
        var r = Release("Release", 10);
        Assert.That(LegacyMigrationService.RecognizeRelease(r.cleanup.layers.Take(7), new[] { r }), Is.Null);
        Assert.That(LegacyMigrationService.RecognizeRelease(r.cleanup.layers.Take(8), new[] { r }), Is.SameAs(r));
    }
    [Test] public void ShortListRequiresEveryNameAndTrimsNames()
    {
        var r = Release("Release", 5);
        Assert.That(LegacyMigrationService.RecognizeRelease(r.cleanup.layers.Take(4), new[] { r }), Is.Null);
        Assert.That(LegacyMigrationService.RecognizeRelease(r.cleanup.layers.Select(n => " " + n + " "), new[] { r }), Is.SameAs(r));
    }
    [Test] public void EmptyReleaseNeverMatches() => Assert.That(LegacyMigrationService.RecognizeRelease(new[] { "Anything" }, new[] { Release("Empty", 0) }), Is.Null);
    [Test] public void NoFxControllerRecognisesNone()
    {
        var avatar = new GameObject("No FX");
        try { Assert.That(LegacyMigrationService.RecognizeRelease(LegacyMigrationService.FxLayerNames(avatar.transform), new[] { Release("R", 1) }), Is.Null); }
        finally { UnityEngine.Object.DestroyImmediate(avatar); }
    }

    [Test] public void IdentifyWithoutAssetDefinitionsHasNoContentMatch()
    {
        var avatar = new GameObject("No definitions");
        try { Assert.That(LegacyMigrationService.ReadIdentification(avatar.transform,
            Newtonsoft.Json.Linq.JObject.Parse("{\"matches\":[]}"), new System.Collections.Generic.Dictionary<string,string> { ["hash"] = "custom.fbx" }, 7), Is.Empty); }
        finally { UnityEngine.Object.DestroyImmediate(avatar); }
    }
    [Test] public void AssetScopedHashMatchesExcludeOtherAssets()
    {
        var avatar = new GameObject("Scoped");
        try {
            var response = Newtonsoft.Json.Linq.JObject.Parse("{\"matches\":[{\"hash\":\"hash\",\"kind\":\"legacy\",\"assetId\":9,\"legacyRelease\":{\"label\":\"Other\"}},{\"hash\":\"hash\",\"kind\":\"legacy\",\"assetId\":7,\"legacyRelease\":{\"label\":\"Hash release\"}}]}");
            var result = LegacyMigrationService.ReadIdentification(avatar.transform, response, new System.Collections.Generic.Dictionary<string,string> { ["hash"] = "custom.fbx" }, 7);
            Assert.That(result.Count, Is.EqualTo(1)); Assert.That(result[0].legacyRelease.label, Is.EqualTo("Hash release"));
        } finally { UnityEngine.Object.DestroyImmediate(avatar); }
    }
}
