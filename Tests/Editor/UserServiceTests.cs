#if UNITY_EDITOR
using NUnit.Framework;

public class UserServiceTests
{
    [Test]
    public void CachedUserInfoNeverInvokesCompletionInline()
    {
        const int userId = 987654321;
        bool completionCalled = false;
        try
        {
            UserService.UpdateUserInfo(userId, "Cached User", null);

            UserService.RequestUserInfo(userId, () => completionCalled = true);

            Assert.That(completionCalled, Is.False);
        }
        finally
        {
            UserService.ClearUserCache(userId);
        }
    }
}

public sealed class NativeMeshPayloadPathTests
{
    [Test]
    public void GeneratedMeshPathExposesAppliedVersionIdentity()
    {
        bool parsed = NativeMeshPayloadService.TryParseGeneratedMeshAssetPath(
            "Assets/MCB/generated/advancedMeshPayloads/14/0.4.0/body_hash.asset",
            out int assetId,
            out string version);

        Assert.That(parsed, Is.True);
        Assert.That(assetId, Is.EqualTo(14));
        Assert.That(version, Is.EqualTo("0.4.0"));
    }

    [Test]
    public void UnrelatedMeshPathIsNotAdvancedMeshProvenance()
    {
        bool parsed = NativeMeshPayloadService.TryParseGeneratedMeshAssetPath(
            "Assets/MasculineCanine/FX/MasculineCanine.v1.5.fbx",
            out _,
            out _);

        Assert.That(parsed, Is.False);
    }
}
#endif
