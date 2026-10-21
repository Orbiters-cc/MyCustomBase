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
/// when it is newer than the applied one, and updating to it. The custom base's Inspector applies it with its usual
/// confirmation and progress, as its version list does.
/// </summary>
[InitializeOnLoad]
internal sealed class McbBaseUpdates : IAvatarObjectUpdates
{
    // Seconds after which the custom base opens in an Inspector of its own (the selection showed it in none), and after
    // which an update its Inspector could not start (signed out, its versions not loaded) is given up.
    private const double OwnInspectorAfter = 1.5, GiveUpAfter = 20;
    // Views ask on every refresh: what the cache said is kept until a version list is read or a version applied (each
    // read checks files and logs), at most this long, and a custom base with no versions cached is read in the background
    // at most this often.
    private const double KeepFor = 600, ReadAgainAfter = 600;

    private sealed class Request
    {
        public MyCustomBase Target;
        public string Version;
        public double Asked;
        public bool OwnInspector;
    }

    private static readonly List<Request> Waiting = new List<Request>();
    private static readonly Dictionary<int, (string version, double at)> Recommended = new Dictionary<int, (string, double)>();
    private static readonly Dictionary<int, double> LastRead = new Dictionary<int, double>();
    // Version lists may be read off the main thread: the views hear of it from the next editor update.
    private static volatile bool versionsRead;
    private static bool listening;

    static McbBaseUpdates()
    {
        AvatarObjectTools.RegisterUpdates(new McbBaseUpdates());
        CustomBases.Changed += _ => { Recommended.Clear(); AvatarObjectTools.NotifyChanged(); };
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
        var editor = LiveEditor(target);
        string newer = RecommendedFor(target, editor);
        if (newer == null || MCBEditor.VersionNumber(newer).CompareTo(MCBEditor.VersionNumber(applied)) <= 0) return null;
        bool asked = Waiting.Any(r => r.Target == target);
        bool running = asked || (editor != null && (editor.isDownloading || editor.isApplying));
        return new AvatarObjectUpdate
        {
            Name = string.IsNullOrEmpty(target.appliedCustomBaseName) ? "the custom base" : target.appliedCustomBaseName,
            From = applied,
            To = newer,
            Running = running,
            Status = !running ? null : asked ? "Opening it in MCB…" : editor.isDownloading ? "Downloading " + newer + "…" : "Applying " + newer + "…",
            Start = () => Ask(target, newer),
        };
    }

    // The live Inspector's recommendation when it shows this custom base's asset; else the version list cached last, as the
    // Inspector reads it when it opens (and read in the background when there is none).
    private static string RecommendedFor(MyCustomBase target, MCBEditor editor)
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

    private static MCBEditor LiveEditor(MyCustomBase target) =>
        Resources.FindObjectsOfTypeAll<MCBEditor>().FirstOrDefault(e => e != null && e.customBaseTarget == target && e.versionModule != null);

    // The custom base's Inspector applies it once it shows that version (selecting the avatar opens it there; an Inspector
    // of its own when none did).
    private static void Ask(MyCustomBase target, string version)
    {
        if (target == null || Waiting.Any(r => r.Target == target)) return;
        Waiting.Add(new Request { Target = target, Version = version, Asked = EditorApplication.timeSinceStartup });
        if (LiveEditor(target) == null) Selection.activeGameObject = target.gameObject;
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
            var editor = LiveEditor(target);
            // Ready once it shows the custom base's asset with its version list read.
            var version = editor != null && !editor.isFetching && editor.GetSelectedAsset()?.id == target.appliedCustomBaseAssetId
                ? editor.GetAllVersions()?.FirstOrDefault(v => v != null && v.version == request.Version)
                : null;
            if (version != null)
            {
                Waiting.Remove(request);
                changed = true;
                editor.versionModule.RequestApply(version);
                continue;
            }
            if (editor == null && !request.OwnInspector && now - request.Asked >= OwnInspectorAfter)
            {
                request.OwnInspector = true;
                EditorUtility.OpenPropertyEditor(target);
            }
            if (now - request.Asked >= GiveUpAfter)
            {
                Waiting.Remove(request);
                changed = true;
                Debug.LogWarning("[MCB] The update of " + (string.IsNullOrEmpty(target.appliedCustomBaseName) ? "the custom base" : target.appliedCustomBaseName) +
                                 " to " + request.Version + " did not start: MCB's Inspector did not show that version within " + GiveUpAfter +
                                 " seconds (signed out, or its versions not loaded). Select the avatar and apply it from MCB's version list.");
            }
        }
        if (Waiting.Count == 0) EditorApplication.update -= Pump;
        if (changed) AvatarObjectTools.NotifyChanged();
    }
}
#endif
