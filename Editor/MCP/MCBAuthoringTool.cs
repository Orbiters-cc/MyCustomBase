using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

[McpForUnityTool("mcb_authoring", Description = "Create and configure MCB custom bases, import FBX, build and apply local versions. inspect lists scene target IDs and data. configure accepts MCBAuthoringService.Draft; create_base accepts Registration. Build/apply/reset/import return pending jobs. Protection belongs to the asset: a trusted creator sets it with create_base (registration.protection) or set_asset_protection {assetId, protection:{xor, discordRole}}; with xor false, builds are one unencrypted package for every original. set_mode switches a version mode (genre, dog ears) on the applied avatar. Publication requires preview_publish followed by explicit human approval of its immutable artifact and confirm_publish with the returned code. Never infer approval from build/apply. Tokens are read from MCB's existing login and never returned.", RequiresPolling = true, PollAction = "status", MaxPollSeconds = 120)]
public static class MCBAuthoringTool
{
    public sealed class Parameters
    {
        [ToolParameter("inspect, configure, create_base, import_fbx, build, apply, reset, set_mode, set_asset_protection, preview_publish, confirm_publish, status")] public string action { get; set; }
        [ToolParameter("Scene MyCustomBase component or GameObject ID", Required = false)] public int target_id { get; set; }
        [ToolParameter("Typed draft, registration, or version identity; inspect describes current data", Required = false)] public object data { get; set; }
        [ToolParameter("FBX source on disk for import_fbx", Required = false)] public string source_path { get; set; }
        [ToolParameter("New FBX asset path below Assets/", Required = false)] public string destination_path { get; set; }
        [ToolParameter("Mode stable ID for set_mode", Required = false)] public string mode { get; set; }
        [ToolParameter("set_mode: turn the mode on (default) or off; an exclusive category (genre) switches by turning another option on", Required = false)] public bool? enabled { get; set; }
        [ToolParameter("Job ID returned by an operation", Required = false)] public string job_id { get; set; }
        [ToolParameter("Single-use code from preview_publish; only pass after explicit user approval", Required = false)] public string confirmation_code { get; set; }
    }
    private sealed class Job
    {
        public string Id, Action;
        public IEnumerator Work;
        public object Waiting, Result;
        public double Started;
    }
    private sealed class Approval
    {
        public string Fingerprint;
        public CustomBaseVersion Version;
        public int Target;
        public double Expires;
    }
    private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
    private static readonly Dictionary<string, Approval> Approvals = new Dictionary<string, Approval>();
    private static Job active;
    private static string latest;

    public static object HandleCommand(JObject arguments)
    {
        try
        {
            var p = arguments?.ToObject<Parameters>() ?? throw new ArgumentException("Parameters are required.");
            var data = p.data as JObject;
            if (p.action == "status")
            {
                string id = p.job_id ?? latest;
                if (id == null || !Jobs.TryGetValue(id, out var job)) return new ErrorResponse("Job unavailable after domain reload. Inspect local versions before retrying; registration must reuse its requestId.");
                return job.Result ?? Pending(job);
            }
            if (p.action == "inspect")
                return new SuccessResponse("MCB authoring state.", p.target_id == 0 ? (object)new
                {
                    targets = Resources.FindObjectsOfTypeAll<MyCustomBase>().Where(o => o.gameObject.scene.IsValid()).Select(o => new { target_id = o.GetInstanceID(), avatar = o.transform.root.name }).ToArray(),
                    versions = VersionRepository.Scan().unsubmitted.Concat(VersionRepository.Scan().imported).Select(v => new { v.assetId, v.version, v.defaultAviVersion, v.sourceVersionKey, v.isUnsubmitted }).ToArray(),
                    draftSchema = JObject.FromObject(new MCBAuthoringService.Draft()), registrationSchema = JObject.FromObject(new MCBAuthoringService.Registration())
                } : MCBAuthoringService.Inspect(MCBAuthoringService.Target(p.target_id)));
            if (active != null) throw new InvalidOperationException("An MCB authoring operation is running: " + active.Id);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Wait for Unity to be idle in edit mode.");
            // A creator window may be building or publishing: fail now and keep the publish approval for a retry.
            if ((p.action == "build" || p.action == "confirm_publish") && VersionOperationGuard.IsBusy) throw new InvalidOperationException(VersionOperationGuard.BusyMessage);
            if (p.action == "import_fbx") return Start(p.action, done => MCBAuthoringService.ImportFbx(p.source_path, p.destination_path, path => done(new { path })));
            var target = MCBAuthoringService.Target(p.target_id);
            switch (p.action)
            {
                case "configure": MCBAuthoringService.Configure(target, data?.ToObject<MCBAuthoringService.Draft>()); return new SuccessResponse("Authoring draft saved.", MCBAuthoringService.Inspect(target));
                case "create_base": return Start(p.action, done => MCBAuthoringService.Register(target, data?.ToObject<MCBAuthoringService.Registration>() ?? throw new ArgumentException("Registration data required."), a => done(a)));
                case "build": return Start(p.action, done => MCBAuthoringService.Build(target, a => done(new { folder = a.FolderUnityPath,
                    version = new { a.Metadata.assetId, a.Metadata.version, a.Metadata.defaultAviVersion, a.Metadata.sourceVersionKey },
                    protection = a.Metadata.protection, skeletonBones = a.Metadata.skeleton?.Length ?? 0,
                    supportedOriginals = a.Metadata.originalBaseVersions?.Length ?? 0 })));
                case "apply": return Start(p.action, done => Apply(target, Version(data), done));
                case "reset": return Start(p.action, done => Reset(target, done));
                case "set_asset_protection": return Start(p.action, done => MCBAuthoringService.SetAssetProtection(target, data?.Value<int>("assetId") ?? 0,
                    data?["protection"]?.ToObject<VersionProtection>(), result => done(new { protection = result })));
                case "set_mode": ModeService.Set(target, p.mode, p.enabled ?? true); return new SuccessResponse("Modes updated.", new { modes = ModeService.EnabledIds(target) });
                case "preview_publish":
                {
                    var version = Version(data); var artifact = VersionRepository.GetArtifact(version); var validation = VersionRepository.Validate(artifact);
                    if (!validation.IsPublishable) throw new InvalidOperationException(validation.Describe());
                    string code = Guid.NewGuid().ToString("N");
                    Approvals.Clear(); Approvals.Add(code, new Approval { Fingerprint = Fingerprint(artifact), Version = version, Target = p.target_id, Expires = EditorApplication.timeSinceStartup + 900 });
                    return new SuccessResponse("Awaiting explicit user approval to publish this built artifact.", new { confirmation_code = code, version.assetId, version.version, version.scope, folder = artifact.FolderUnityPath,
                        files = artifact.Manifest.outputs.Select(o => new { o.path, o.hash }).ToArray(), validation = validation.Describe(), expires_in_seconds = 900 });
                }
                case "confirm_publish":
                {
                    if (p.confirmation_code == null || !Approvals.TryGetValue(p.confirmation_code, out var approval) || approval.Target != p.target_id || approval.Expires < EditorApplication.timeSinceStartup)
                        throw new InvalidOperationException("Request a new publication preview and obtain user approval first.");
                    var artifact = VersionRepository.GetArtifact(approval.Version);
                    if (!VersionRepository.Validate(artifact).IsPublishable || Fingerprint(artifact) != approval.Fingerprint) throw new InvalidOperationException("The artifact changed; request a new preview and approval.");
                    Approvals.Remove(p.confirmation_code);
                    return Start(p.action, done => MCBAuthoringService.Publish(target, approval.Version, () => done(new { published = true })));
                }
                default: throw new ArgumentException("Unknown authoring action.");
            }
        }
        catch (Exception ex) { return new ErrorResponse(ex.Message); }
    }
    private static CustomBaseVersion Version(JObject data)
    {
        var requested = data?.ToObject<CustomBaseVersion>() ?? throw new ArgumentException("Provide assetId, version, defaultAviVersion and sourceVersionKey.");
        var matches = VersionRepository.Scan(true).unsubmitted.Concat(VersionRepository.Scan().imported).Where(v => v.assetId == requested.assetId && v.version == requested.version
            && v.defaultAviVersion == requested.defaultAviVersion && v.sourceVersionKey == requested.sourceVersionKey).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Version identity must select exactly one local artifact; inspect lists available versions.");
        return matches[0];
    }
    private static IEnumerator Apply(MyCustomBase target, CustomBaseVersion version, Action<object> done)
    { yield return MCBAuthoringService.Apply(target, version); done(new { applied = version.version, version.assetId }); }
    private static IEnumerator Reset(MyCustomBase target, Action<object> done)
    { yield return MCBAuthoringService.Reset(target); done(new { reset = true }); }
    private static string Fingerprint(VersionArtifact artifact)
    {
        using var hash = SHA256.Create(); return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { artifact.Metadata, artifact.Manifest }))));
    }
    private static object Start(string action, Func<Action<object>, IEnumerator> work)
    {
        foreach (string id in Jobs.Where(p => p.Value.Result != null).Select(p => p.Key).ToArray()) if (Jobs.Count >= 16) Jobs.Remove(id);
        var job = new Job { Id = Guid.NewGuid().ToString("N"), Action = action, Started = EditorApplication.timeSinceStartup };
        job.Work = MCBWork.Flatten(work(result => job.Result = new SuccessResponse(action + " completed.", result)));
        Jobs.Add(job.Id, job); active = job; latest = job.Id; EditorApplication.update += Tick;
        return Pending(job);
    }
    private static PendingResponse Pending(Job job) => new PendingResponse("MCB " + job.Action + " is running.", 1, new { job_id = job.Id, elapsed_seconds = EditorApplication.timeSinceStartup - job.Started });
    private static void Tick()
    {
        var job = active; if (job == null) { EditorApplication.update -= Tick; return; }
        try
        {
            if (job.Waiting is AsyncOperation operation && !operation.isDone) return;
            job.Waiting = null;
            if (job.Work.MoveNext()) job.Waiting = job.Work.Current;
            else if (job.Result == null) job.Result = new ErrorResponse("Operation ended without a result.");
        }
        catch (Exception ex) { job.Result = new ErrorResponse(ex.Message); Debug.LogException(ex); }
        if (job.Result != null) { (job.Work as IDisposable)?.Dispose(); active = null; EditorApplication.update -= Tick; }
    }
}
