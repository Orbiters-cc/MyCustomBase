#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class MCBImageCacheTests
{
    [Test]
    public void DestroyedUnityObjectIsEvictedAndDiskAvatarIsReloaded()
    {
        const int userId = 987654320;
        var cache = new Dictionary<int, Texture2D>();
        var texture = new Texture2D(2, 2);
        string folder = Path.Combine(MCBUtils.GetMCBDataFolder(), "avatars");
        string path = Path.Combine(folder, "avatar_" + userId + ".png");
        Assert.That(File.Exists(path), Is.False, "The test must not overwrite an existing avatar.");
        try
        {
            cache[userId] = texture;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            Assert.That(MCBImageCache.TryGetLive(cache, userId, out _), Is.False);
            Assert.That(cache.ContainsKey(userId), Is.False);

            var first = UserService.GetUserAvatar(userId);
            Assert.That(first != null, Is.True);
            Assert.That(first.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
            UnityEngine.Object.DestroyImmediate(first);
            var reloaded = UserService.GetUserAvatar(userId);
            Assert.That(reloaded != null, Is.True, "A stale native handle must not prevent disk reload.");
            Assert.That(ReferenceEquals(first, reloaded), Is.False);
        }
        finally
        {
            if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            UserService.ClearUserCache(userId);
        }
    }

    [Test]
    public void TransientFailuresBecomeEligibleForRetryWithoutClearingTheWholeCache()
    {
        var now = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        var gate = new MCBImageRetryGate<string>(() => now);
        gate.Add("failed");
        Assert.That(gate.Contains("failed"), Is.True);
        Assert.That(gate.Contains("other"), Is.False);
        now = now.AddSeconds(29);
        Assert.That(gate.Contains("failed"), Is.True);
        now = now.AddSeconds(1);
        Assert.That(gate.Contains("failed"), Is.False);
    }

    [Test]
    public void DevelopmentImagesUseLocalApiAndPreserveIndependentCdnOrigins()
    {
        bool previous = MCBUtils.isDevEnvironment;
        try
        {
            MCBUtils.isDevEnvironment = true;
            Assert.That(MCBUtils.ResolveImageUrl("https://dev.api.orbiters.cc/files/serve/9?format=webp&v=2"),
                Is.EqualTo("http://localhost:4100/files/serve/9?format=png&v=2"));
            Assert.That(MCBUtils.ResolveImageUrl("/files/serve/9?v=2"),
                Is.EqualTo("http://localhost:4100/files/serve/9?v=2&format=png"));
            const string cdn = "https://dev.files.orbiters.cc/public/files/60/png";
            Assert.That(MCBUtils.ResolveImageUrl(cdn), Is.EqualTo(cdn));
            const string discord = "https://cdn.discordapp.com/avatars/example/image.png?size=128";
            Assert.That(MCBUtils.ResolveImageUrl(discord), Is.EqualTo(discord));
            MCBUtils.isDevEnvironment = false;
            Assert.That(MCBUtils.ResolveImageUrl("/files/serve/9"),
                Is.EqualTo("https://api.orbiters.cc/files/serve/9?format=png"));
            Assert.That(MCBUtils.ResolveApiUrl("https://dev.api.orbiters.cc/files/serve/9"),
                Is.EqualTo("https://dev.api.orbiters.cc/files/serve/9"));
        }
        finally { MCBUtils.isDevEnvironment = previous; }
    }
}
#endif
