#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

/// <summary>
/// MCB in other views of the avatar (Mixer's Hierarchy): the version the server recommends for the avatar's custom base,
/// when it is newer than the applied one, and updating to it. The avatar's session applies it with its usual confirmation
/// and progress, as its version list does, whether or not an Inspector shows the avatar.
/// </summary>
[InitializeOnLoad]
internal sealed class McbBaseUpdates : IAvatarObjectUpdates
{
    // Seconds after which an update the session could not start (signed out, its versions not loaded) is given up.
    private const double GiveUpAfter = 20;
    // Views ask on every refresh: what the cache said is kept until a version list is read or a version applied (each
    // read checks files and logs), at most this long, and a custom base with no versions cached is read in the background
    // at most this often.
    private const double KeepFor = 600, ReadAgainAfter = 600;

    private sealed class Request
    {
        public MyCustomBase Target;
        public string Version;
        public double Asked;
    }

    private static readonly List<Request> Waiting = new List<Request>();
    // The update each custom base could not start, and why: its pill says so (and retries) until a version is applied or
    // another one is offered.
    private static readonly Dictionary<int, (string version, string why)> Failures = new Dictionary<int, (string, string)>();
    private static readonly Dictionary<int, (string version, double at)> Recommended = new Dictionary<int, (string, double)>();
    private static readonly Dictionary<int, double> LastRead = new Dictionary<int, double>();
    // Version lists may be read off the main thread: the views hear of it from the next editor update.
    private static volatile bool versionsRead;
    private static bool listening;

    static McbBaseUpdates()
    {
        AvatarObjectTools.RegisterUpdates(new McbBaseUpdates());
        CustomBases.Changed += _ => { Recommended.Clear(); Failures.Clear(); AvatarObjectTools.NotifyChanged(); };
        EditorApplication.update += () =>
        {
            if (!versionsRead) return;
            versionsRead = false;
            Recommended.Clear();
            AvatarObjectTools.NotifyChanged();
        };
    }

    // The version service (and the cache it loads) starts with the first avatar a view asks about, not at every load.
    private static AsyncVersionService Versions()
    {
        if (!listening)
        {
            listening = true;
            AsyncVersionService.Instance.OnVersionsUpdated += (request, versions, recommended) => versionsRead = true;
        }
        return AsyncVersionService.Instance;
    }

    public AvatarObjectUpdate UpdateOf(GameObject go)
    {
        if (go == null || go.GetComponent<VRCAvatarDescriptor>() == null) return null;
        var target = go.GetComponentInChildren<MyCustomBase>(true);
        if (target == null || EditorUtility.IsPersistent(target) || target.appliedCustomBaseAssetId <= 0 || AvatarPaths.Root(target) != go.transform) return null;
        string applied = !string.IsNullOrWhiteSpace(target.appliedCustomBaseVersionString) ? target.appliedCustomBaseVersionString : target.appliedCustomBaseVersion?.version;
        if (string.IsNullOrEmpty(applied)) return null;
        var editor = McbSession.Find(target);
        string newer = RecommendedFor(target, editor);
        if (newer == null || McbSession.VersionNumber(newer).CompareTo(McbSession.VersionNumber(applied)) <= 0) return null;
        bool asked = Waiting.Any(r => r.Target == target);
        bool running = asked || (editor != null && (editor.isDownloading || editor.isApplying));
        bool failed = !running && Failures.TryGetValue(target.GetInstanceID(), out var failure) && failure.version == newer;
        return new AvatarObjectUpdate
        {
            Name = string.IsNullOrEmpty(target.appliedCustomBaseName) ? "the custom base" : target.appliedCustomBaseName,
            From = applied,
            To = newer,
            Running = running,
            Failed = failed,
            Status = running ? (asked ? "Opening it in MCB…" : editor.isDownloading ? "Downloading " + newer + "…" : "Applying " + newer + "…")
                : failed ? Failures[target.GetInstanceID()].why : null,
            Start = () => Ask(target, newer),
        };
    }

    // The version the custom base has now (its Inspector may have applied one since the view asked).
    private static string Applied(MyCustomBase target) =>
        !string.IsNullOrWhiteSpace(target.appliedCustomBaseVersionString) ? target.appliedCustomBaseVersionString : target.appliedCustomBaseVersion?.version;

    // Whether the custom base already has this version or a later one: updating to it would do nothing.
    private static bool AlreadyAt(MyCustomBase target, string version)
    {
        string applied = Applied(target);
        return string.IsNullOrEmpty(version) || !string.IsNullOrEmpty(applied) && McbSession.VersionNumber(version).CompareTo(McbSession.VersionNumber(applied)) <= 0;
    }

    // The session's recommendation when it shows this custom base's asset; else the version list cached last, as the
    // session reads it when a view opens (and read in the background when there is none).
    private static string RecommendedFor(MyCustomBase target, McbSession editor)
    {
        if (editor != null && editor.recommendedVersion != null && editor.GetSelectedAsset()?.id == target.appliedCustomBaseAssetId)
            return editor.recommendedVersion.version;
        int id = target.GetInstanceID();
        double now = EditorApplication.timeSinceStartup;
        if (Recommended.TryGetValue(id, out var known) && now - known.at < KeepFor) return known.version;
        string version = null;
        string model = target.baseFbxFiles?.Where(m => m != null).Select(AssetDatabase.GetAssetPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (model != null)
        {
            model = MCBUtils.ToUnityPath(model);
            string token = AuthenticationService.GetAuth()?.token;
            using (MCBLogger.QuietIssues())
            {
                var cached = Versions().GetCachedVersions(model, token, target.appliedCustomBaseAssetId, target.appliedCustomBaseSourceVersionKey);
                version = cached.recommended?.version;
                if (cached.versions.Count == 0 && MCBPackageVersionService.HasServerAccess(token) && (!LastRead.TryGetValue(id, out var at) || now - at >= ReadAgainAfter))
                {
                    LastRead[id] = now;
                    Versions().StartVersionFetchInBackground(model, token, target.appliedCustomBaseAssetId, true, target.appliedCustomBaseSourceVersionKey);
                }
            }
        }
        Recommended[id] = (version, now);
        return version;
    }

    // The avatar's session applies it once it lists that version (finding the applied asset and its versions first).
    private static void Ask(MyCustomBase target, string version)
    {
        if (target == null || Waiting.Any(r => r.Target == target)) return;
        Failures.Remove(target.GetInstanceID());
        // Asked from a view drawn before the custom base was updated elsewhere: nothing to do.
        if (AlreadyAt(target, version)) { AvatarObjectTools.NotifyChanged(); return; }
        Waiting.Add(new Request { Target = target, Version = version, Asked = EditorApplication.timeSinceStartup });
        // A retry looks for the asset again.
        McbSession.For(target)?.LookForAppliedAssetAgain();
        EditorApplication.update -= Pump;
        EditorApplication.update += Pump;
        AvatarObjectTools.NotifyChanged();
        Pump();
    }

    private static void Pump()
    {
        double now = EditorApplication.timeSinceStartup;
        bool changed = false;
        foreach (var request in Waiting.ToList())
        {
            var target = request.Target;
            if (target == null) { Waiting.Remove(request); changed = true; continue; }
            var editor = McbSession.For(target);
            // Updated meanwhile (from its Inspector): nothing left to apply.
            if (AlreadyAt(target, request.Version)) { Waiting.Remove(request); changed = true; continue; }
            // The session may show another asset (the one picked last): the custom base's own is shown, with its versions.
            var shown = editor.ShowAppliedAsset();
            // Ready once it shows the custom base's asset with its version list read.
            var version = shown == McbSession.AppliedAssetState.Shown && !editor.isFetching
                ? editor.GetAllVersions()?.FirstOrDefault(v => v != null && v.version == request.Version)
                : null;
            if (version != null)
            {
                Waiting.Remove(request);
                changed = true;
                editor.RequestApply(version);
                continue;
            }
            if (shown == McbSession.AppliedAssetState.NotFound || now - request.Asked >= GiveUpAfter)
            {
                Waiting.Remove(request);
                changed = true;
                Failures[target.GetInstanceID()] = (request.Version, shown != McbSession.AppliedAssetState.Shown
                    ? "MCB did not find " + (string.IsNullOrEmpty(target.appliedCustomBaseName) ? "its asset" : target.appliedCustomBaseName) + " among the avatar's assets (signed out?)"
                    : "MCB's version list did not show " + request.Version + " (signed out, or its versions not loaded)");
                Debug.LogWarning("[MCB] The update of " + (string.IsNullOrEmpty(target.appliedCustomBaseName) ? "the custom base" : target.appliedCustomBaseName) +
                                 " to " + request.Version + " did not start: MCB did not list that version within " + GiveUpAfter +
                                 " seconds (signed out, or its versions not loaded). Select the avatar and apply it from MCB's version list.");
            }
        }
        if (Waiting.Count == 0) EditorApplication.update -= Pump;
        if (changed) AvatarObjectTools.NotifyChanged();
    }
}
#endif
