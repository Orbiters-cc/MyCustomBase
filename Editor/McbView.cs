#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

/// <summary>The parts of MCB's views that a change can make stale.</summary>
[Flags]
public enum McbViewParts
{
    None = 0,
    Banner = 1 << 0,
    Dependencies = 1 << 1,
    Connectivity = 1 << 2,
    /// <summary>The gallery, the selected asset's actions and its comments.</summary>
    Gallery = 1 << 3,
    Creator = 1 << 4,
    Versions = 1 << 5,
    AvatarOptions = 1 << 6,
    Status = 1 << 7,
    AdjustMaterial = 1 << 8,
    /// <summary>A download, delete, build or switch of the avatar started or ended.</summary>
    Busy = 1 << 9,
    /// <summary>Nothing to rebuild: the views draw again (an IMGUI part, a progress).</summary>
    Repaint = 1 << 10,
    All = ~0
}

/// <summary>
/// Collects what became stale in one view and refreshes it once, on the next editor frame, however many changes asked for
/// it in between. Changes ask for parts instead of rebuilding: several in a row (a click that selects an asset, then
/// loads its versions) cost one rebuild.
/// </summary>
/// <remarks>The frame is an <see cref="EditorApplication.update"/> tick: unlike <c>delayCall</c> it also runs while
/// Unity is in the background.</remarks>
public sealed class McbViewRefresh
{
    private readonly Action<McbViewParts> refresh;
    private McbViewParts pending;
    private bool scheduled;

    public McbViewRefresh(Action<McbViewParts> refresh)
    {
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
    }

    public McbViewParts Pending => pending;

    public void Request(McbViewParts parts)
    {
        if (parts == McbViewParts.None) return;
        pending |= parts;
        if (scheduled) return;
        scheduled = true;
        EditorApplication.update += OnUpdate;
    }

    /// <summary>Refreshes what is pending now (a view being built for the first time), instead of next frame.</summary>
    public void Flush()
    {
        Unschedule();
        var parts = pending;
        pending = McbViewParts.None;
        if (parts != McbViewParts.None) refresh(parts);
    }

    /// <summary>Forgets what is pending (the view closed).</summary>
    public void Cancel()
    {
        Unschedule();
        pending = McbViewParts.None;
    }

    private void OnUpdate() => Flush();

    private void Unschedule()
    {
        if (!scheduled) return;
        scheduled = false;
        EditorApplication.update -= OnUpdate;
    }
}

#endif
