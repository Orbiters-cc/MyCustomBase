#if UNITY_EDITOR
using System;
using NUnit.Framework;
using UnityEngine.Networking;

public class MCBRequestHeadersTests
{
    [Test]
    public void CreatedIdempotencyKeyUsesTheBackendContract()
    {
        string key = MCBRequestHeaders.CreateIdempotencyKey();

        Assert.That(key, Has.Length.EqualTo(32));
        Assert.That(Guid.TryParseExact(key, "N", out _), Is.True);
        Assert.That(key, Is.EqualTo(key.ToLowerInvariant()));
    }

    [Test]
    public void SetIdempotencyKeyWritesNormalizedHeader()
    {
        using (var request = new UnityWebRequest("http://localhost.invalid", UnityWebRequest.kHttpVerbPOST))
        {
            MCBRequestHeaders.SetIdempotencyKey(request, " AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ");

            Assert.That(
                request.GetRequestHeader(MCBRequestHeaders.IdempotencyKeyHeader),
                Is.EqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-guid")]
    public void SetIdempotencyKeyRejectsInvalidValues(string key)
    {
        using (var request = new UnityWebRequest("http://localhost.invalid", UnityWebRequest.kHttpVerbPOST))
        {
            Assert.Throws<ArgumentException>(() => MCBRequestHeaders.SetIdempotencyKey(request, key));
        }
    }
}
#endif
