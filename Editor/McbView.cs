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
    /// <summary>A download, fetch, delete, build or switch started or ended.</summary>
    Busy = 1 << 9,
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

/// <summary>
/// What one avatar's views show about its version operations, shared by every view of that avatar: each inspector on
/// it, the creator window and the compare window. Changes are announced once per frame with the union of what changed.
/// </summary>
/// <remarks>
/// It holds the busy state of the operations for now; the rest of the session (versions, selection, account) moves here
/// once the operations leave the inspector. Runtime state only: a domain reload ends the operations it describes.
/// </remarks>
public sealed class McbAvatarViewModel
{
    private static readonly Dictionary<int, McbAvatarViewModel> ByTarget = new Dictionary<int, McbAvatarViewModel>();

    private readonly McbViewRefresh announce;
    private bool isFetching, isDownloading, isDeleting, isSubmitting, isApplying;

    private McbAvatarViewModel()
    {
        announce = new McbViewRefresh(parts => Changed?.Invoke(parts));
    }

    /// <summary>The model of this avatar's custom base; a view without a target gets one of its own.</summary>
    public static McbAvatarViewModel For(MyCustomBase target)
    {
        if (target == null) return new McbAvatarViewModel();
        int id = target.GetInstanceID();
        if (!ByTarget.TryGetValue(id, out var model))
        {
            model = new McbAvatarViewModel();
            ByTarget[id] = model;
        }
        return model;
    }

    /// <summary>Raised on the frame after a change, with every part that changed since the last time.</summary>
    public event Action<McbViewParts> Changed;

    public bool IsFetching { get => isFetching; set => Set(ref isFetching, value); }
    public bool IsDownloading { get => isDownloading; set => Set(ref isDownloading, value); }
    public bool IsDeleting { get => isDeleting; set => Set(ref isDeleting, value); }
    public bool IsSubmitting { get => isSubmitting; set => Set(ref isSubmitting, value); }
    public bool IsApplying { get => isApplying; set => Set(ref isApplying, value); }

    public bool IsBusy => isFetching || isDownloading || isDeleting || isSubmitting || isApplying;

    /// <summary>Tells this avatar's views that something they show changed.</summary>
    public void MarkChanged(McbViewParts parts) => announce.Request(parts);

    /// <summary>Announces pending changes now instead of next frame (tests).</summary>
    internal void FlushChanges() => announce.Flush();

    private void Set(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        announce.Request(McbViewParts.Busy);
    }
}
#endif
