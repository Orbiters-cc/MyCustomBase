#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

internal static class MCBImageCache
{
    internal static bool TryGetLive<TKey>(Dictionary<TKey, Texture2D> cache, TKey key, out Texture2D texture)
    {
        if (cache.TryGetValue(key, out texture) && texture != null) return true;
        cache.Remove(key); texture = null; return false;
    }

    internal static Texture2D Retain(Texture2D texture)
    {
        // These are owned UI textures, never scene assets. Keep them through Unity's unused-asset sweep.
        if (texture != null) texture.hideFlags = HideFlags.HideAndDontSave;
        return texture;
    }

    internal static void ReleaseAll<TKey>(Dictionary<TKey, Texture2D> cache)
    {
        foreach (var texture in cache.Values) if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
        cache.Clear();
    }
}

internal sealed class MCBImageRetryGate<TKey>
{
    private readonly Dictionary<TKey, DateTime> retryAt = new Dictionary<TKey, DateTime>();
    private readonly Func<DateTime> now;
    internal MCBImageRetryGate(Func<DateTime> clock = null) { now = clock ?? (() => DateTime.UtcNow); }
    internal bool Contains(TKey key) => retryAt.TryGetValue(key, out var time) && now() < time;
    internal void Add(TKey key) { retryAt[key] = now().AddSeconds(30); }
    internal void Remove(TKey key) { retryAt.Remove(key); }
    internal void Clear() { retryAt.Clear(); }
}
#endif
