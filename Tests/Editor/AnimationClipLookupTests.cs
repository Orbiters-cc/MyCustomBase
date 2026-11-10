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
    public void BlankOrUnknownNamesFindNothing()
    {
        Assert.That(AnimationClipLookup.Find(" "), Is.Empty);
        Assert.That(AnimationClipLookup.Find("McbLookupNoSuchClip"), Is.Empty);
        Assert.That(AnimationClipLookup.Find("mcblookupflex"), Is.Empty, "names are compared exactly");
    }

    [Test]
    public void AClipAddedAfterAProjectChangeIsFound()
    {
        Assert.That(AnimationClipLookup.Find("McbLookupLate"), Is.Empty);
        AssetDatabase.CreateAsset(new AnimationClip(), TestFolder + "/McbLookupLate.anim");
        AnimationClipLookup.Clear();
        Assert.That(AnimationClipLookup.Find("McbLookupLate").Count, Is.EqualTo(1));
    }
}
