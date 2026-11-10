using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class AnimationClipLookupTests
{
    private const string TestFolder = "Assets/__MCBAnimationClipLookupTests";

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.CreateFolder("Assets", "__MCBAnimationClipLookupTests");
        AssetDatabase.CreateFolder(TestFolder, "B");
        AssetDatabase.CreateAsset(new AnimationClip(), TestFolder + "/McbLookupFlex.anim");
        AssetDatabase.CreateAsset(new AnimationClip(), TestFolder + "/B/McbLookupFlex.anim");
        AssetDatabase.CreateAsset(new AnimationClip(), TestFolder + "/McbLookupFlexOther.anim");
        AnimationClipLookup.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(TestFolder);
        AnimationClipLookup.Clear();
    }

    // A clip asset whose serialized name is not its file name, as the authoring clips "fa9011af_Spine_Corrective.anim"
    // (m_Name: Spine_Corrective) are.
    private static string CreateRenamedClip(string fileName, string clipName)
    {
        string path = TestFolder + "/" + fileName + ".anim";
        AssetDatabase.CreateAsset(new AnimationClip(), path);
        string text = File.ReadAllText(path);
        Assert.That(text, Does.Contain("m_Name: " + fileName), "the project saves assets as text");
        File.WriteAllText(path, text.Replace("m_Name: " + fileName, "m_Name: " + clipName));
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(path).name, Is.EqualTo(clipName));
        return path;
    }

    [Test]
    public void FindsEveryClipWithThatExactNameOrderedByPath()
    {
        var clips = AnimationClipLookup.Find("McbLookupFlex");
        Assert.That(clips.Count, Is.EqualTo(2));
        Assert.That(AssetDatabase.GetAssetPath(clips[0]), Is.EqualTo(TestFolder + "/B/McbLookupFlex.anim"));
        Assert.That(AssetDatabase.GetAssetPath(clips[1]), Is.EqualTo(TestFolder + "/McbLookupFlex.anim"));
        Assert.That(AnimationClipLookup.Find("McbLookupFlex"), Is.SameAs(clips), "answered from memory the second time");
    }

    [Test]
    public void AClipIsFoundByItsSerializedNameNotItsFileName()
    {
        string path = CreateRenamedClip("fa9011af_McbLookupSpine", "McbLookupSpine_Corrective");
        AnimationClipLookup.Clear();
        Assert.That(AnimationClipLookup.ReadSerializedName(path), Is.EqualTo("McbLookupSpine_Corrective"));
        var clips = AnimationClipLookup.Find("McbLookupSpine_Corrective");
        Assert.That(clips.Select(AssetDatabase.GetAssetPath), Is.EqualTo(new[] { path }));
        Assert.That(AnimationClipLookup.Find("fa9011af_McbLookupSpine"), Is.Empty);
    }

    [Test]
    public void BlankOrUnknownNamesFindNothing()
    {
        Assert.That(AnimationClipLookup.Find(" "), Is.Empty);
        Assert.That(AnimationClipLookup.Find("McbLookupNoSuchClip"), Is.Empty);
        Assert.That(AnimationClipLookup.Find("mcblookupflex"), Is.Empty, "names are compared exactly");
    }

    [Test]
    public void TheIndexFollowsImportsMovesAndDeletesWithoutBeingCleared()
    {
        Assert.That(AnimationClipLookup.Find("McbLookupLate"), Is.Empty);

        AssetDatabase.CreateAsset(new AnimationClip(), TestFolder + "/McbLookupLate.anim");
        Assert.That(AnimationClipLookup.Find("McbLookupLate").Count, Is.EqualTo(1), "an imported clip is indexed");

        string renamed = CreateRenamedClip("McbLookupRenamedFile", "McbLookupRenamedClip");
        Assert.That(AnimationClipLookup.Find("McbLookupRenamedClip").Count, Is.EqualTo(1), "a reimport reads the name again");

        Assert.That(AssetDatabase.MoveAsset(renamed, TestFolder + "/B/McbLookupRenamedFile.anim"), Is.Empty);
        Assert.That(AnimationClipLookup.Find("McbLookupRenamedClip").Select(AssetDatabase.GetAssetPath),
            Is.EqualTo(new[] { TestFolder + "/B/McbLookupRenamedFile.anim" }), "a moved clip is found at its new path");

        AssetDatabase.DeleteAsset(TestFolder + "/McbLookupLate.anim");
        Assert.That(AnimationClipLookup.Find("McbLookupLate"), Is.Empty, "a deleted clip is gone");
    }

    // The lookup must answer what loading every clip and grouping by name answered before (MCBF-36), for clip assets
    // and for clips inside model files alike.
    [Test]
    public void AnswersAsLoadingEveryClipOfTheProjectDoes()
    {
        CreateRenamedClip("fa9011af_McbLookupCompare", "McbLookupCompare_Corrective");
        var reference = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        bool sawModelClip = false;
        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null || string.IsNullOrWhiteSpace(clip.name)) continue;
            sawModelClip |= AssetImporter.GetAtPath(path) is ModelImporter;
            if (!reference.TryGetValue(clip.name, out var paths)) reference[clip.name] = paths = new List<string>();
            paths.Add(path);
        }

        AnimationClipLookup.Clear();
        foreach (var pair in reference)
        {
            pair.Value.Sort(string.CompareOrdinal);
            Assert.That(AnimationClipLookup.Find(pair.Key).Select(AssetDatabase.GetAssetPath), Is.EqualTo(pair.Value), pair.Key);
        }
        Assert.That(reference.ContainsKey("McbLookupCompare_Corrective"));
        if (!sawModelClip) Assert.Inconclusive("No model file with takes in this project: only clip assets were compared.");
    }
}
