#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Brings a Blender export back into a custom base: the manifest.json an MCB export writes next to its FBX files, one
/// CUSTOM_BASE model per target FBX, plus the XMuscles correctives XMuscle Orbit Helper baked. Each model becomes the
/// creator's custom FBX for its target, imported with a generated Avatar, or, for an advanced-mesh export, is kept out of
/// the asset database and shown on the avatar as the version's payload will show it. Live edits of the base go through
/// Orbiters Mixer's link; an export is how topology changes and baked correctives come back, and the only way Mixer or
/// Blender edits reach a version (versions are built from the FBX files).
/// </summary>
internal static partial class BlenderExportImport
{
    public const string ExportKind = "orbiters.mcb.blenderExport";
    /// <summary>The manifest version this reads, and the only one accepted.</summary>
    public const int ProtocolVersion = 2;
    /// <summary>The XMuscle Orbit Helper release whose exports MCB reads.</summary>
    public const string XMuscleOrbitHelperVersion = "0.10.0";
    /// <summary>Imported exports (the creator's custom FBX files and their generated Avatars), one folder per custom base.</summary>
    public const string ExportsFolder = "Assets/MCB/generated/blenderExports";
    /// <summary>Advanced-mesh exports, kept where Unity never imports them, one folder per custom base.</summary>
    public const string AdvancedExportsFolder = "Assets/MCB/generated/blenderAdvancedExports~";
    /// <summary>The avatar previews of advanced-mesh exports (payload assets), one folder per custom base.</summary>
    public const string PreviewsFolder = "Assets/MCB/generated/blenderPreviews";
    // A target's name as it appears in the file names: short, so every path fits Windows' limit.
    private const int TargetLabelLength = 24;

    // What each base's Blender card says: the last import, Blender export or Mixer action of that base.
    private static readonly Dictionary<int, (string message, MessageType type)> statuses = new Dictionary<int, (string, MessageType)>();

    /// <summary>Raised with the base whose Blender card message changed.</summary>
    public static event Action<MyCustomBase> Changed;

    /// <summary>The message the Blender card of <paramref name="customBase"/> shows (its last import, export or Mixer action), or null.</summary>
    public static string StatusOf(MyCustomBase customBase, out MessageType type)
    {
        type = MessageType.Info;
        if (customBase == null || !statuses.TryGetValue(customBase.GetInstanceID(), out var status)) return null;
        type = status.type;
        return status.message;
    }

    internal sealed class BlenderExportManifest
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
        public TargetInfo target;
        public List<ModelInfo> models;
        public bool advancedMeshBlenderLink;
        /// <summary>The XMuscles correctives of the export (XMuscle Orbit Helper's block), or null.</summary>
        public MuscleCorrectiveSet xmuscle;
    }

    internal sealed class TargetInfo
    {
        public string targetFbxPath;
        public string customBaseName;
    }

    internal sealed class ModelInfo
    {
        public string role;
        public string path;
        public string targetFbxPath;
        public string transportFormat;
        public string unityImportMode;
        public string payloadSourceFormat;
        public List<string> meshNames;
        /// <summary>The target's meshes Unity kept out of Blender (Mixer's Export for MCB): they are not in the model's FBX.</summary>
        public List<string> missingUnityTargetMeshNames;
        /// <summary>The shape keys each exported mesh carries (XMuscles' baked shapes among them).</summary>
        public Dictionary<string, List<string>> shapeKeysByMesh;
    }

    /// <summary>A target FBX of the base with the avatar renderers that show its meshes.</summary>
    internal sealed class ExportTarget
    {
        public string UnityPath;
        public List<ModelFileSmrPathData> SmrPaths = new List<ModelFileSmrPathData>();
    }

    // ---- Targets ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The base's target FBX files: the selected asset's SOURCE files, then the creator's source files, then the FBX
    /// files detected on the avatar (after an import the renderers show the custom FBX, which is not a target).
    /// </summary>
    public static List<string> TargetFbxPaths(MCBEditor editor)
    {
        if (editor == null) return new List<string>();
        var paths = new List<string>();
        var selectedAsset = editor.GetSelectedAsset();
        if (selectedAsset?.sourceFiles != null)
            paths.AddRange(selectedAsset.sourceFiles
                .Where(file => file != null
                               && string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase)
                               && !string.IsNullOrWhiteSpace(file.path))
                .Select(file => MCBUtils.ToUnityPath(file.path)));
        var customBase = editor.customBaseTarget;
        if (customBase != null && customBase.baseFbxFiles != null)
            paths.AddRange(customBase.baseFbxFiles.Where(f => f != null).Select(f => MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(f))));
        if (paths.Count == 0) paths.AddRange(editor.GetDetectedAvatarFbxPaths().Where(path => !string.IsNullOrWhiteSpace(path)).Select(MCBUtils.ToUnityPath));
        return paths.Where(path => !string.IsNullOrWhiteSpace(path) && AssetImporter.GetAtPath(path) is ModelImporter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The targets with their renderer mappings: found on the avatar, completed with the asset's records.</summary>
    internal static List<ExportTarget> Targets(MCBEditor editor)
    {
        var paths = TargetFbxPaths(editor);
        if (paths.Count == 0 || editor.customBaseTarget == null) return new List<ExportTarget>();
        var selectedAsset = editor.GetSelectedAsset();
        // From the avatar, as the asset's records and the version builds name its renderers (not from an object above it).
        var discovered = SmrPathService.CollectSmrPathsByFbx(AvatarPaths.Root(editor.customBaseTarget), paths);
        return paths.Select(path => new ExportTarget
        {
            UnityPath = path,
            SmrPaths = MergeSmrPaths(discovered.TryGetValue(path, out var entries) ? entries : null, SelectedAssetSmrPaths(selectedAsset, path)),
        }).ToList();
    }

    /// <summary>
    /// The avatar's renderers that stand for the base's targets, each with its target FBX and the FBX mesh it shows: what
    /// the Blender card lists, and what Mixer's Export for MCB groups by (<see cref="TargetFbxOf"/>).
    /// </summary>
    internal static List<(SkinnedMeshRenderer Renderer, string TargetUnityPath, string FbxMesh)> TargetRenderers(MCBEditor editor) =>
        editor == null || editor.customBaseTarget == null
            ? new List<(SkinnedMeshRenderer, string, string)>()
            : MapTargets(editor.customBaseTarget, editor);

    /// <summary>The avatar's renderers for a target FBX (by the renderer paths), each with the FBX mesh it shows.</summary>
    internal static List<(SkinnedMeshRenderer Renderer, string FbxMesh)> Renderers(Transform avatarRoot, IEnumerable<ModelFileSmrPathData> smrPaths)
    {
        var list = new List<(SkinnedMeshRenderer, string)>();
        if (avatarRoot == null) return list;
        foreach (var entry in smrPaths ?? Enumerable.Empty<ModelFileSmrPathData>())
        {
            if (entry == null || string.IsNullOrEmpty(entry.avatarPath)) continue;
            var found = avatarRoot.Find(entry.avatarPath);
            var renderer = found ? found.GetComponent<SkinnedMeshRenderer>() : null;
            if (!renderer || !renderer.sharedMesh || list.Any(r => r.Item1 == renderer)) continue;
            string mesh = !string.IsNullOrEmpty(entry.fbxMeshPath) ? entry.fbxMeshPath.Split('/').Last() : entry.meshName ?? renderer.name;
            list.Add((renderer, mesh));
        }
        return list;
    }

    private static List<ModelFileSmrPathData> SelectedAssetSmrPaths(AvatarDiscoveredAsset selectedAsset, string unityPath)
    {
        if (selectedAsset?.sourceFiles == null || string.IsNullOrWhiteSpace(unityPath)) return new List<ModelFileSmrPathData>();
        var source = selectedAsset.sourceFiles.FirstOrDefault(file =>
            file != null &&
            string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MCBUtils.ToUnityPath(file.path), MCBUtils.ToUnityPath(unityPath), StringComparison.OrdinalIgnoreCase));
        return source?.smrPaths ?? new List<ModelFileSmrPathData>();
    }

    private static List<ModelFileSmrPathData> MergeSmrPaths(params IEnumerable<ModelFileSmrPathData>[] sources)
    {
        var result = new List<ModelFileSmrPathData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources ?? new IEnumerable<ModelFileSmrPathData>[0])
            foreach (var entry in source ?? Enumerable.Empty<ModelFileSmrPathData>())
            {
                if (entry == null) continue;
                string key = $"{entry.avatarPath}|{entry.fbxMeshPath}|{entry.meshName}|{entry.rendererName}";
                if (seen.Add(key)) result.Add(entry);
            }
        return result;
    }

    // ---- Edits a version does not carry ------------------------------------------------------------------------------

    /// <summary>
    /// Whether a renderer is in a Mixer session (registered by MCB's Mixer integration): its working copy, committed or
    /// not, is not in the FBX files versions are built from.
    /// </summary>
    internal static Func<Renderer, bool> InMixerSession;

    /// <summary>
    /// Whether the renderer shows a mesh a version cannot carry: one Mixer committed (from its own or Blender's edits),
    /// or a Mixer working copy. Versions are built from the creator's FBX entries, never from the renderers.
    /// </summary>
    internal static bool ShowsMeshOutsideVersions(Renderer renderer)
    {
        if (renderer == null) return false;
        if (InMixerSession != null && InMixerSession(renderer)) return true;
        Mesh mesh = null;
        if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
        else
        {
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null) mesh = filter.sharedMesh;
        }
        return mesh != null && MeshAssets.IsOwnedBy(mesh, "Mixer");
    }

    /// <summary>The base's renderers whose meshes a version would leave out (see <see cref="ShowsMeshOutsideVersions"/>).</summary>
    internal static List<SkinnedMeshRenderer> RenderersOutsideVersions(MCBEditor editor) =>
        TargetRenderers(editor).Select(entry => entry.Renderer).Where(ShowsMeshOutsideVersions).ToList();

    /// <summary>What a build would leave out of the version, for its confirmation and warnings; null when nothing.</summary>
    internal static string EditsOutsideVersionsWarning(IEnumerable<Renderer> renderers)
    {
        var names = (renderers ?? Enumerable.Empty<Renderer>()).Where(r => r != null).Select(r => r.name).Distinct().ToList();
        if (names.Count == 0) return null;
        return $"{string.Join(", ", names)} {(names.Count == 1 ? "shows a mesh" : "show meshes")} edited in Mixer or Blender that the version " +
               "will not contain: versions are built from the FBX files. To include the edits, use Export for MCB in Blender and import " +
               "the export from the Blender card first.";
    }

    /// <summary><see cref="EditsOutsideVersionsWarning(IEnumerable{Renderer})"/> for the editor's custom base.</summary>
    internal static string EditsOutsideVersionsWarning(MCBEditor editor)
    {
        try { return EditsOutsideVersionsWarning(RenderersOutsideVersions(editor)); }
        catch (Exception ex)
        {
            MCBLogger.LogWarning("[BlenderExport] Could not check the avatar for Mixer edits: " + ex.Message);
            return null;
        }
    }

    // ---- Mixer ------------------------------------------------------------------------------------------------------

    private static readonly Dictionary<int, (double at, Dictionary<Transform, string> byRenderer)> targetCache = new Dictionary<int, (double, Dictionary<Transform, string>)>();
    private static readonly Dictionary<string, (Hash128 hash, HashSet<string> names)> meshNameCache = new Dictionary<string, (Hash128, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets which target each renderer stands for (the base's targets changed): the next ask maps them again.</summary>
    internal static void ForgetTargets() => targetCache.Clear();

    /// <summary>
    /// The target FBX (a Unity path) a renderer of a custom base stands for, or null: what Mixer tells Blender so its
    /// Export for MCB groups the meshes by target. While an MCB editor shows the base, its targets are the Blender card's
    /// (so an export names targets its import accepts); renderers the asset's records do not name are matched by the FBX
    /// mesh names, one renderer per mesh, active ones first.
    /// </summary>
    public static string TargetFbxOf(Renderer renderer)
    {
        if (renderer == null) return null;
        var customBase = CustomBaseOf(renderer.transform);
        if (customBase == null || customBase.baseFbxFiles == null || customBase.baseFbxFiles.Count == 0) return null;
        // Per base: two avatars under one parent each have their own targets.
        int key = customBase.GetInstanceID();
        double now = EditorApplication.timeSinceStartup;
        if (!targetCache.TryGetValue(key, out var cached) || now - cached.at > 2.0)
        {
            var editor = Resources.FindObjectsOfTypeAll<MCBEditor>().FirstOrDefault(e => e != null && e.customBaseTarget == customBase);
            var byRenderer = new Dictionary<Transform, string>();
            foreach (var entry in MapTargets(customBase, editor)) byRenderer[entry.Renderer.transform] = entry.TargetUnityPath;
            cached = (now, byRenderer);
            targetCache[key] = cached;
        }
        return cached.byRenderer.TryGetValue(renderer.transform, out string target) ? target : null;
    }

    /// <summary>
    /// The custom base a renderer belongs to: on the renderer's parents, else anywhere under its avatar. Without an avatar
    /// above it, only a base whose avatar holds the renderer: a prop beside another avatar is not that avatar's.
    /// </summary>
    internal static MyCustomBase CustomBaseOf(Transform transform)
    {
        if (transform == null) return null;
        var above = transform.GetComponentInParent<MyCustomBase>(true);
        if (above != null) return above;
        var descriptor = transform.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
        if (descriptor != null) return descriptor.GetComponentInChildren<MyCustomBase>(true);
        return transform.root.GetComponentsInChildren<MyCustomBase>(true).FirstOrDefault(b => transform.IsChildOf(AvatarPaths.Root(b)));
    }

    // The renderers the asset's records name first, then, by the FBX's mesh names, one renderer per mesh (active ones first):
    // an applied version's meshes are not the FBX's own, and an inactive outfit copy of "Body" must not go into the base's
    // FBX beside the real one. Only the creator's source files are targets while the base has some, so an export names
    // targets its import accepts. Only the base's own avatar is searched: another avatar under the same parent keeps its
    // renderers.
    private static List<(SkinnedMeshRenderer Renderer, string TargetUnityPath, string FbxMesh)> MapTargets(MyCustomBase customBase, MCBEditor editor)
    {
        var root = AvatarPaths.Root(customBase);
        var result = new List<(SkinnedMeshRenderer, string, string)>();
        var mapped = new HashSet<Transform>();
        var claimed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Claim(SkinnedMeshRenderer renderer, string path, string fbxMesh)
        {
            if (renderer == null || !mapped.Add(renderer.transform)) return;
            result.Add((renderer, path, fbxMesh));
            if (!claimed.TryGetValue(path, out var names)) claimed[path] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(MaterialSlotNames.Normalize(fbxMesh ?? renderer.name));
        }

        var creatorPaths = (customBase.baseFbxFiles ?? new List<GameObject>()).Where(f => f != null)
            .Select(f => MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(f))).Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> paths;
        if (editor != null)
        {
            var all = TargetFbxPaths(editor);
            paths = all.Where(p => creatorPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            if (paths.Count == 0) paths = all;
            foreach (var target in Targets(editor).Where(t => paths.Contains(t.UnityPath, StringComparer.OrdinalIgnoreCase)))
                foreach (var entry in Renderers(root, target.SmrPaths))
                    Claim(entry.Renderer, target.UnityPath, entry.FbxMesh);
        }
        else
        {
            paths = creatorPaths;
            foreach (var pair in SmrPathService.CollectSmrPathsByFbx(root, paths))
                foreach (var entry in Renderers(root, pair.Value))
                    Claim(entry.Renderer, pair.Key, entry.FbxMesh);
        }

        var candidates = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(smr => smr.sharedMesh != null)
            .OrderByDescending(smr => smr.gameObject.activeInHierarchy).ThenBy(Depth).ToList();
        foreach (string path in paths)
        {
            claimed.TryGetValue(path, out var taken);
            foreach (string name in MeshNames(path))
            {
                if (taken != null && taken.Contains(name)) continue;
                var renderer = candidates.FirstOrDefault(smr => !mapped.Contains(smr.transform) &&
                    (string.Equals(MaterialSlotNames.Normalize(smr.name), name, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(MaterialSlotNames.Normalize(smr.sharedMesh.name), name, StringComparison.OrdinalIgnoreCase)));
                if (renderer != null) Claim(renderer, path, name);
            }
        }
        return result;
    }

    private static int Depth(Component component)
    {
        int depth = 0;
        for (var t = component.transform; t != null; t = t.parent) depth++;
        return depth;
    }

    // The FBX's mesh names, read again only once Unity imported it again.
    private static HashSet<string> MeshNames(string path)
    {
        var hash = AssetDatabase.GetAssetDependencyHash(path);
        if (meshNameCache.TryGetValue(path, out var known) && known.hash == hash) return known.names;
        var names = new HashSet<string>(AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Select(m => MaterialSlotNames.Normalize(m.name)), StringComparer.OrdinalIgnoreCase);
        meshNameCache[path] = (hash, names);
        return names;
    }

    // Per project (two projects open on one machine share EditorPrefs).
    private static string LatestExportPref => "MCB.BlenderExport.Latest:" +
        MCBUtils.PathHash(Hash128.Compute(Path.GetFullPath(Application.dataPath).Replace('\\', '/').ToLowerInvariant()).ToString());

    /// <summary>
    /// The manifest of the newest export Blender reported through Mixer, while its file exists, it was not imported yet
    /// and it is for one of <paramref name="targetFbxPaths"/> (an export that names no target, or that cannot be read, is
    /// offered too: its import says what is wrong).
    /// </summary>
    public static string LatestExportFor(ICollection<string> targetFbxPaths)
    {
        string path = EditorPrefs.GetString(LatestExportPref, "");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var named = TargetsNamedBy(path);
        if (named == null) return path;
        var targets = targetFbxPaths ?? Array.Empty<string>();
        return named.Count == 0 || named.Any(p => targets.Contains(p, StringComparer.OrdinalIgnoreCase)) ? path : null;
    }

    // The newest manifest's targets, read again only when the file changed: the creator form asks at every rebuild.
    private static (string path, DateTime written, long length, List<string> named) latestManifest;

    // The target FBX paths a manifest names (none when it names none), or null when it cannot be read.
    private static List<string> TargetsNamedBy(string manifestPath)
    {
        var file = new FileInfo(manifestPath);
        if (latestManifest.path == manifestPath && latestManifest.written == file.LastWriteTimeUtc && latestManifest.length == file.Length)
            return latestManifest.named;
        List<string> named;
        try
        {
            var manifest = ReadExportManifest(manifestPath);
            named = (manifest.models ?? new List<ModelInfo>()).Select(model => model?.targetFbxPath).Append(manifest.target?.targetFbxPath)
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(MCBUtils.ToUnityPath).ToList();
        }
        catch (Exception) { named = null; }
        latestManifest = (manifestPath, file.LastWriteTimeUtc, file.Length, named);
        return named;
    }

    /// <summary>Remembers the export Blender just wrote (its manifest path) and tells the Blender cards of the bases it is for.</summary>
    public static void NoteExport(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) return;
        EditorPrefs.SetString(LatestExportPref, manifestPath);
        LastExportFolder = Path.GetDirectoryName(manifestPath);
        string message = "Blender exported the base: " + manifestPath + "\nImport the newest export from the Blender card to make it the creator's custom FBX.";
        var bases = Resources.FindObjectsOfTypeAll<MCBEditor>()
            .Where(editor => editor != null && editor.customBaseTarget != null && LatestExportFor(TargetFbxPaths(editor)) != null)
            .Select(editor => editor.customBaseTarget).Distinct().ToList();
        if (bases.Count == 0) SetStatus(null, message, MessageType.Info);
        foreach (var customBase in bases) SetStatus(customBase, message, MessageType.Info);
    }

    // An imported export is not offered again.
    private static void ForgetLatestExport(string manifestPath)
    {
        string latest = EditorPrefs.GetString(LatestExportPref, "");
        if (string.IsNullOrEmpty(latest)) return;
        try
        {
            if (string.Equals(Path.GetFullPath(latest), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
                EditorPrefs.DeleteKey(LatestExportPref);
        }
        catch (Exception) { }
    }

    // ---- Import -----------------------------------------------------------------------------------------------------

    /// <summary>Asks for an export's manifest.json and imports it for the editor's custom base. False when the dialog was cancelled.</summary>
    public static bool PickAndImport(MCBEditor editor)
    {
        string path = EditorUtility.OpenFilePanel("Blender export (manifest.json)", LastExportFolder, "json");
        if (string.IsNullOrEmpty(path)) return false;
        LastExportFolder = Path.GetDirectoryName(path);
        Import(editor, path);
        return true;
    }

    private static string LastExportFolder
    {
        get => EditorPrefs.GetString("MCB.BlenderExport.LastFolder", "");
        set => EditorPrefs.SetString("MCB.BlenderExport.LastFolder", value ?? "");
    }

    /// <summary>
    /// Imports an export for the editor's custom base. Failures land in the base's <see cref="StatusOf"/> and the Console,
    /// never thrown. Not while a version is built, downloaded or applied: both change the same renderers and entries.
    /// </summary>
    public static void Import(MCBEditor editor, string manifestPath)
    {
        var customBase = editor != null ? editor.customBaseTarget : null;
        if (VersionOperationGuard.VersionWorkRunning)
        {
            SetStatus(customBase, "The Blender export was not imported: " + (VersionOperationGuard.BusyMessage ??
                "MCB is downloading or applying a version. Wait for it to finish, then import again."), MessageType.Warning);
            return;
        }
        try
        {
            using (VersionOperationGuard.Acquire("importing a Blender export")) Apply(editor, manifestPath);
        }
        catch (Exception ex)
        {
            SetStatus(customBase, "Blender export import failed: " + ex.Message + "\nThe avatar was not changed. Fix the export files or export again from Blender to retry.", MessageType.Error);
            MCBLogger.LogError($"[BlenderExport] Import failed for {manifestPath}: {ex}");
        }
    }

    private static void Apply(MCBEditor editor, string manifestPath)
    {
        if (editor == null || editor.customBaseTarget == null) throw new InvalidOperationException("Select the custom base in the scene first.");
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) throw new FileNotFoundException("Blender export manifest was not found.", manifestPath);
        var customBase = editor.customBaseTarget;
        var manifest = ReadExportManifest(manifestPath);
        var models = manifest.models?
            .Where(x => x != null && string.Equals(x.role, "CUSTOM_BASE", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<ModelInfo>();
        if (models.Count == 0) throw new InvalidOperationException("Blender export manifest has no CUSTOM_BASE model.");
        var targets = Targets(editor);
        if (targets.Count == 0) throw new InvalidOperationException("No target FBX was detected for this avatar: assign or detect the base FBX first.");

        string exportDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        // Check every model before touching the avatar, then apply them as one step: a bad second model must not leave
        // the first one applied.
        var plans = PlanModelUpdates(customBase, targets, manifest, models, exportDir);
        if (plans.Count == 0) throw new InvalidOperationException("Blender export manifest did not contain any usable CUSTOM_BASE model paths.");
        // The base's own folder (its stable component id), and file names made from the export's content: another avatar's
        // import never lands on this one's files, and an import never overwrites an earlier one, so Undo can go back to it.
        string baseKey = BaseFolderKey(customBase);
        // What the base uses now stays for the editor session, whatever the cleanups find: Undo can go back to it.
        RetainForSession(FilesUsedBy(customBase));

        var updatedTargets = new List<string>();
        var warnings = new List<string>();
        int generatedAvatarCount = 0;
        int advancedCount = 0;
        // The inspector applies the editor's SerializedObject later: entries written through another one would be undone.
        var serialized = editor.serializedObject;
        serialized.Update();
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Import Blender export");
        using (var files = new ExportFileRollback())
        {
            try
            {
                foreach (var plan in plans)
                {
                    if (plan.advancedMesh)
                    {
                        string externalFbxPath = CopyModelToAdvancedExports(baseKey, plan, files);
                        ApplyAdvancedMeshPreview(customBase, baseKey, externalFbxPath, plan.targetFbxPath, plan.previewMappings);
                        AssignCreatorExternalCustomFbx(serialized, customBase, plan.targetFbxPath, externalFbxPath);
                        updatedTargets.Add(externalFbxPath);
                        advancedCount++;
                        MCBLogger.Log($"[BlenderExport] Applied native mesh preview and stored the export for submission. source={plan.sourceFbxPath} external={externalFbxPath} target={plan.targetFbxPath}");
                    }
                    else
                    {
                        string exportedUnityPath = CopyModelToExports(baseKey, plan, files, out bool created);
                        Avatar generatedAvatar = AvatarForImportedFbx(exportedUnityPath, plan.targetFbxPath, created, files);
                        if (generatedAvatar != null) generatedAvatarCount++;
                        var missed = new List<string>();
                        if (RefreshTargetMeshesFromFbx(customBase, plan.target, exportedUnityPath, plan.model.meshNames, missed) == 0)
                            warnings.Add($"No renderer of the avatar matched the meshes of '{Path.GetFileName(plan.targetFbxPath)}': the custom FBX is set for the version, but the avatar still shows its previous meshes.");
                        else if (missed.Count > 0)
                            warnings.Add($"{string.Join(", ", missed)} {(missed.Count == 1 ? "still shows its" : "still show their")} previous {(missed.Count == 1 ? "mesh" : "meshes")}: " +
                                         $"the export's FBX for '{Path.GetFileName(plan.targetFbxPath)}' has no mesh for {(missed.Count == 1 ? "it" : "them")}. The custom FBX is set for the version.");
                        AssignCreatorCustomFbx(serialized, customBase, plan.targetFbxPath, exportedUnityPath, generatedAvatar);
                        ApplyGeneratedAvatarToPreview(customBase, exportedUnityPath, generatedAvatar);
                        updatedTargets.Add(exportedUnityPath);
                        MCBLogger.Log($"[BlenderExport] Imported Blender export. source={plan.sourceFbxPath} imported={exportedUnityPath} target={plan.targetFbxPath}");
                    }
                }

                // The XMuscles correctives belong to the same step: a failure leaves the avatar as it was.
                ApplyMuscleCorrectives(customBase, manifest, models);
                EditorUtility.SetDirty(customBase);
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch
            {
                // Runs inside one editor update, so the group holds only this import's changes.
                Undo.RevertAllDownToGroup(undoGroup);
                files.Restore();
                serialized.Update();
                throw;
            }
        }
        serialized.Update();
        // And so does what it uses after the import (Redo).
        RetainForSession(FilesUsedBy(customBase));
        ForgetLatestExport(manifestPath);

        // Only what every renderer shows is "updated": otherwise the warnings say which ones kept their previous meshes.
        string action = advancedCount > 0 ? "Updated the avatar with native meshes from"
            : warnings.Count > 0 ? "Set the creator's custom FBX from" : "Updated the avatar from";
        string avatarMessage = generatedAvatarCount > 0
            ? $"\nGenerated {generatedAvatarCount} Avatar asset(s)."
            : advancedCount > 0 ? "\nThe original FBX files are unchanged; the exports are ready for submission." : "\nNo Avatar asset was generated.";
        string warningMessage = warnings.Count > 0 ? "\n" + string.Join("\n", warnings) : "";
        SetStatus(customBase, $"Blender export imported for {customBase.name}.\n{action} {updatedTargets.Count} FBX file(s).{avatarMessage}{warningMessage}",
            warnings.Count > 0 ? MessageType.Warning : MessageType.Info);
        LogMuscleCorrectives(manifest.xmuscle);
        // What other tools made from these meshes (My Avatar's Substance Painter project) can be made again.
        AvatarMeshEdits.NotifyApplied(MCBReFitIntegration.Root(customBase));
    }

    // Without XMuscle Orbit Helper in Blender there is no xmuscle block, and another API's block is not read: the stored
    // correctives stay as they are.
    private static void ApplyMuscleCorrectives(MyCustomBase customBase, BlenderExportManifest manifest, List<ModelInfo> models)
    {
        if (manifest.xmuscle == null || manifest.xmuscle.apiVersion != MuscleCorrectiveSet.ApiVersion) return;
        var exportedMeshes = models
            .Where(model => model.shapeKeysByMesh != null)
            .SelectMany(model => model.shapeKeysByMesh.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        MuscleCorrectiveStore.Apply(customBase, manifest.xmuscle, exportedMeshes);
    }

    /// <summary>Reads a Blender export manifest; throws unless it is an export of <see cref="ProtocolVersion"/>.</summary>
    internal static BlenderExportManifest ReadExportManifest(string manifestPath)
    {
        var manifest = JsonConvert.DeserializeObject<BlenderExportManifest>(File.ReadAllText(manifestPath));
        if (manifest == null || manifest.kind != ExportKind) throw new InvalidOperationException("manifest.json is not a Blender MCB export.");
        if (manifest.protocolVersion != ProtocolVersion) throw new InvalidOperationException(DescribeProtocolMismatch(manifest.protocolVersion));
        return manifest;
    }

    internal static string DescribeProtocolMismatch(int blenderProtocolVersion)
    {
        string outdated = blenderProtocolVersion > ProtocolVersion ? "the MCB Unity package" : "the Blender side that wrote the export";
        return $"The export uses MCB export protocol {blenderProtocolVersion} and this MCB reads protocol {ProtocolVersion}: update {outdated}.";
    }

    private static void LogMuscleCorrectives(MuscleCorrectiveSet set)
    {
        if (set == null) return;
        if (set.apiVersion != MuscleCorrectiveSet.ApiVersion)
        {
            MCBLogger.LogWarning($"[BlenderExport] XMuscles: the export comes from XMuscle Orbit Helper API {set.apiVersion}, MCB reads API " +
                                 $"{MuscleCorrectiveSet.ApiVersion}: update XMuscle Orbit Helper to {XMuscleOrbitHelperVersion} in Blender and export again.");
            return;
        }
        MCBLogger.Log($"[BlenderExport] The export carries {set.muscles?.Count ?? 0} XMuscle corrective(s) (XMuscle Orbit Helper API {set.apiVersion}).");
        foreach (string warning in set.warnings ?? new List<string>()) MCBLogger.LogWarning("[BlenderExport] XMuscles: " + warning);
    }

    private sealed class ModelUpdatePlan
    {
        public ModelInfo model;
        public int modelIndex;
        public string sourceFbxPath;
        // The export file's content (a short hash): the name of its copy.
        public string contentKey;
        public ExportTarget target;
        public string targetFbxPath => target.UnityPath;
        public bool advancedMesh;
        public List<ModelFileSmrPathData> previewMappings;
    }

    private static List<ModelUpdatePlan> PlanModelUpdates(MyCustomBase customBase, List<ExportTarget> targets, BlenderExportManifest manifest, List<ModelInfo> models, string exportDir)
    {
        var plans = new List<ModelUpdatePlan>();
        int withoutTarget = models.Count(model => !string.IsNullOrWhiteSpace(model.path) && string.IsNullOrWhiteSpace(model.targetFbxPath)
                                                 && string.IsNullOrWhiteSpace(manifest?.target?.targetFbxPath));
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            var model = models[modelIndex];
            if (string.IsNullOrWhiteSpace(model.path)) continue;
            string sourceFbxPath = ExportFile(exportDir, model.path);
            var target = ResolveTarget(customBase, targets, manifest, model, withoutTarget);
            RequireEveryTargetMesh(model, target.UnityPath);
            var other = plans.FirstOrDefault(p => string.Equals(p.targetFbxPath, target.UnityPath, StringComparison.OrdinalIgnoreCase));
            if (other != null)
                throw new InvalidOperationException($"The export has two models for '{target.UnityPath}' ({other.model.path} and {model.path}): export again from Blender.");
            var plan = new ModelUpdatePlan
            {
                model = model,
                modelIndex = modelIndex,
                sourceFbxPath = sourceFbxPath,
                contentKey = MCBUtils.PathHash(MCBUtils.CalculateFileHash(sourceFbxPath)),
                target = target,
                advancedMesh = ShouldUseAdvancedMesh(manifest, model),
            };
            if (plan.advancedMesh) plan.previewMappings = PreviewMappings(target, model.meshNames);
            GetCreatorTargetIndex(customBase, target.UnityPath);
            plans.Add(plan);
        }
        return plans;
    }

    /// <summary>
    /// Refuses a model whose FBX leaves out meshes of its target that Unity kept out of Blender: the FBX becomes the base's
    /// custom FBX, so those meshes would be gone from the base and its versions. The message names them and the way out.
    /// </summary>
    internal static void RequireEveryTargetMesh(ModelInfo model, string targetFbxPath)
    {
        var missing = (model?.missingUnityTargetMeshNames ?? new List<string>()).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return;
        bool one = missing.Count == 1;
        throw new InvalidOperationException(
            $"The export's FBX for '{Path.GetFileName(targetFbxPath)}' leaves out {string.Join(", ", missing)}: Unity keeps {(one ? "it" : "them")} out of " +
            $"Blender, and importing the FBX would remove {(one ? "it" : "them")} from the base. Send {(one ? "it" : "them")} to Blender again " +
            $"(Mixer Hierarchy, right-click: Blender > Send to Blender), wait until {(one ? "it arrives" : "they arrive")}, then use Export for MCB again.");
    }

    /// <summary>
    /// The full path of a model the manifest names: an FBX file inside the export's folder. Absolute paths, "..", links
    /// and device names are refused, so a manifest cannot bring any other file of the computer into the project (or into
    /// the next version's upload).
    /// </summary>
    internal static string ExportFile(string exportDir, string relativePath)
    {
        string path;
        try { path = SafePaths.ContainedPath(exportDir, relativePath); }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException($"The export names '{relativePath}', which is not a file in the export's own folder ({exportDir}).");
        }
        if (!string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The export names '{relativePath}', which is not an FBX file.");
        if (!File.Exists(path)) throw new FileNotFoundException("Blender exported FBX was not found.", path);
        return path;
    }

    // The model's target, or the base's first target in the creator's source files. Several models without a target
    // cannot be told apart: the export must name them.
    internal static ExportTarget ResolveTarget(MyCustomBase customBase, List<ExportTarget> targets, BlenderExportManifest manifest, ModelInfo model, int modelsWithoutTarget)
    {
        string requested = !string.IsNullOrWhiteSpace(model?.targetFbxPath) ? model.targetFbxPath : manifest?.target?.targetFbxPath;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string unityPath = MCBUtils.ToUnityPath(requested);
            var match = targets.FirstOrDefault(t => string.Equals(MCBUtils.ToUnityPath(t.UnityPath), unityPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            throw new InvalidOperationException($"The export targets '{requested}', which is not one of this base's target FBX files ({string.Join(", ", targets.Select(t => t.UnityPath))}).");
        }
        var creatorTargets = targets.Where(t => IsCreatorTarget(customBase, t.UnityPath)).ToList();
        if (creatorTargets.Count == 0)
            throw new InvalidOperationException("None of this base's target FBX files is in the creator's source files. Select the intended custom base before importing.");
        if (modelsWithoutTarget > 1 && creatorTargets.Count > 1)
            throw new InvalidOperationException($"The export has {modelsWithoutTarget} models that name no target FBX: export again with the Mixer extension's Export for MCB, which names each model's target.");
        return creatorTargets[0];
    }

    private static bool IsCreatorTarget(MyCustomBase customBase, string targetFbxPath) =>
        customBase.baseFbxFiles != null && customBase.baseFbxFiles.Any(source => source != null &&
            string.Equals(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(source)), MCBUtils.ToUnityPath(targetFbxPath), StringComparison.OrdinalIgnoreCase));

    private static bool ShouldUseAdvancedMesh(BlenderExportManifest manifest, ModelInfo model)
    {
        if (!FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK)) return false;
        if (manifest?.advancedMeshBlenderLink == true) return true;
        return model != null &&
               (string.Equals(model.unityImportMode, "nativeMeshPayload", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.transportFormat, NativeMeshPayloadService.PayloadFormat, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ModelFileSmrPathData> PreviewMappings(ExportTarget target, IEnumerable<string> meshNames)
    {
        var names = new HashSet<string>(meshNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var mappings = target.SmrPaths
            .Where(entry => entry != null && (names.Count == 0 || names.Contains(entry.meshName) || names.Contains(entry.rendererName)))
            .ToList();
        if (mappings.Count == 0)
            throw new InvalidOperationException($"No avatar renderer shows the meshes the export has for '{target.UnityPath}'. Select the intended custom base and import again.");
        return mappings;
    }

    // The export read straight from Blender's FBX, with the avatar's dynamic normals baked in (as a version's payload has them).
    // The mappings and the dynamic-normal selections both name renderers from the avatar.
    private static void ApplyAdvancedMeshPreview(MyCustomBase customBase, string baseKey, string externalFbxPath, string targetFbxPath, List<ModelFileSmrPathData> mappings)
    {
        NativeMeshPayloadService.ApplyBlenderExportPreview(AvatarPaths.Root(customBase), externalFbxPath,
            MCBUtils.ToUnityPath(targetFbxPath), mappings,
            MCBUtils.CombineUnityPath(PreviewsFolder, baseKey),
            customBase.useDynamicNormals ? DynamicNormalsService.Selections(customBase) : null,
            renderer => DynamicNormalsService.LegSeparation(customBase, renderer));
    }

    // Snapshots the project files an import overwrites or creates, so a failed import can put them back.
    private sealed class ExportFileRollback : IDisposable
    {
        private readonly string folder = Path.Combine(GetProjectRoot(), "Library", "MCB", "BlenderExportRollback", Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly List<KeyValuePair<string, string>> backups = new List<KeyValuePair<string, string>>();
        private bool restoreFailed;

        public void Track(string path)
        {
            string absolutePath = UnityPathToAbsolute(path);
            foreach (string file in new[] { absolutePath, absolutePath + ".meta" })
            {
                if (backups.Any(entry => string.Equals(entry.Key, file, StringComparison.OrdinalIgnoreCase))) continue;
                string backup = null;
                if (File.Exists(file))
                {
                    Directory.CreateDirectory(folder);
                    backup = Path.Combine(folder, backups.Count.ToString());
                    File.Copy(file, backup);
                }
                backups.Add(new KeyValuePair<string, string>(file, backup));
            }
        }

        /// <summary>Puts the tracked files back. Never throws: a file it cannot restore is logged and its copy kept.</summary>
        public void Restore()
        {
            var reimport = new List<string>();
            foreach (var entry in backups)
            {
                string unityPath = MCBUtils.ToUnityPath(entry.Key);
                bool isAsset = !entry.Key.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(unityPath));
                try
                {
                    if (entry.Value != null)
                    {
                        File.Copy(entry.Value, entry.Key, true);
                        if (isAsset) reimport.Add(unityPath);
                    }
                    else if (isAsset) AssetDatabase.DeleteAsset(unityPath);
                    else if (File.Exists(entry.Key)) File.Delete(entry.Key);
                }
                catch (Exception ex)
                {
                    restoreFailed = true;
                    MCBLogger.LogError($"[BlenderExport] Could not restore '{entry.Key}' after a failed import: {ex.Message}");
                }
            }
            foreach (string unityPath in reimport)
            {
                try { AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); }
                catch (Exception ex)
                {
                    // The import's own error is the one the user must see: this one is logged.
                    restoreFailed = true;
                    MCBLogger.LogError($"[BlenderExport] Could not import '{unityPath}' again after a failed import: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (restoreFailed)
            {
                MCBLogger.LogError($"[BlenderExport] Some files could not be restored. Recovery copies were kept at '{folder}'.");
                return;
            }
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) { MCBLogger.LogWarning($"[BlenderExport] Could not delete the import backups '{folder}': {ex.Message}"); }
        }
    }

    /// <summary>
    /// The folder name of a custom base's imports: its stable component id (<see cref="McbInstanceIdentityService"/>), cut
    /// by <see cref="MCBUtils.PathHash"/>. Two avatars of one asset, or bases of the same name, never share it.
    /// </summary>
    internal static string BaseFolderKey(MyCustomBase customBase)
    {
        McbInstanceIdentityService.EnsureIdentity(customBase);
        return MCBUtils.PathHash(customBase.mcbComponentId);
    }

    /// <summary>"NN_&lt;target&gt;-&lt;content&gt;": the model's place in the export, its target's name (short, no spaces) and its content's hash.</summary>
    internal static string ImportFileStem(int modelIndex, string targetFbxPath, string contentKey) =>
        $"{modelIndex + 1:00}_{SafePaths.FileName(Path.GetFileNameWithoutExtension(targetFbxPath), "model", TargetLabelLength).Replace(' ', '_')}-{contentKey}";

    // The same content was copied before when the file of its name has its size: it is used as it is.
    private static bool SameCopy(string destination, string source) =>
        File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length;

    private static string CopyModelToExports(string baseKey, ModelUpdatePlan plan, ExportFileRollback files, out bool created)
    {
        string folderUnityPath = MCBUtils.CombineUnityPath(ExportsFolder, baseKey);
        string unityPath = MCBUtils.CombineUnityPath(folderUnityPath, ImportFileStem(plan.modelIndex, plan.targetFbxPath, plan.contentKey) + ".fbx");
        string destination = UnityPathToAbsolute(unityPath);
        // Its generated Avatar has the longest name.
        SafePaths.RequireLength(UnityPathToAbsolute(AvatarDefinitionGenerationService.GetDefaultAvatarPath(unityPath)) + ".meta");
        created = !SameCopy(destination, plan.sourceFbxPath) || AssetImporter.GetAtPath(unityPath) == null;
        if (!created) return unityPath;
        MeshAssets.EnsureFolder(folderUnityPath);
        files.Track(unityPath);
        File.Copy(plan.sourceFbxPath, destination, true);
        // Import settings before the first import: readable, as a version build needs it (turning Read/Write on afterwards
        // imported the body a second time), without cameras and lights.
        if (!File.Exists(destination + ".meta"))
            File.WriteAllText(destination + ".meta",
                "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\nModelImporter:\n  serializedVersion: 22200\n" +
                "  animations:\n    isReadable: 1\n  meshes:\n    importCameras: 0\n    importLights: 0\n");
        AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceSynchronousImport);
        return unityPath;
    }

    private static string CopyModelToAdvancedExports(string baseKey, ModelUpdatePlan plan, ExportFileRollback files)
    {
        string folder = Path.Combine(UnityPathToAbsolute(AdvancedExportsFolder), baseKey);
        string destination = Path.Combine(folder, ImportFileStem(plan.modelIndex, plan.targetFbxPath, plan.contentKey) + ".fbx");
        SafePaths.RequireLength(destination);
        if (SameCopy(destination, plan.sourceFbxPath)) return Path.GetFullPath(destination);
        Directory.CreateDirectory(folder);
        files.Track(destination);
        File.Copy(plan.sourceFbxPath, destination, true);
        return Path.GetFullPath(destination);
    }

    // Each renderer of the target gets its mesh from the imported FBX: by the asset's records (for the exported meshes,
    // else all of them). The number of renderers updated; <paramref name="missed"/> gets the avatar's renderers the records
    // name that kept their previous mesh (the FBX has none for them).
    private static int RefreshTargetMeshesFromFbx(MyCustomBase customBase, ExportTarget target, string sourceFbxPath, IEnumerable<string> meshNames, List<string> missed)
    {
        if (customBase == null || string.IsNullOrWhiteSpace(sourceFbxPath)) return 0;
        string path = MCBUtils.ToUnityPath(sourceFbxPath);
        var avatar = AvatarPaths.Root(customBase);
        var notRefreshed = new List<string>();
        int refreshed = SmrPathService.RefreshTargetMeshesFromFbx(avatar, path, target?.SmrPaths, meshNamesToRefresh: meshNames, notRefreshed: notRefreshed);
        if (refreshed == 0 && meshNames != null && meshNames.Any())
        {
            notRefreshed.Clear();
            refreshed = SmrPathService.RefreshTargetMeshesFromFbx(avatar, path, target?.SmrPaths, allowNameFallback: false, notRefreshed: notRefreshed);
        }
        missed?.AddRange(notRefreshed);
        return refreshed;
    }

    // The FBX's generated Avatar: kept from an earlier import of the same content, else generated (its importer's .meta is
    // then tracked for the rollback).
    private static Avatar AvatarForImportedFbx(string customFbxUnityPath, string sourceMappingFbxPath, bool created, ExportFileRollback files)
    {
        string avatarPath = AvatarDefinitionGenerationService.GetDefaultAvatarPath(customFbxUnityPath);
        if (!created)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
            if (existing != null) return existing;
            files.Track(customFbxUnityPath);
        }
        files.Track(avatarPath);
        return GenerateAvatarForImportedFbx(customFbxUnityPath, sourceMappingFbxPath);
    }

    private static Avatar GenerateAvatarForImportedFbx(string customFbxUnityPath, string sourceMappingFbxPath)
    {
        try
        {
            var result = AvatarDefinitionGenerationService.GenerateAvatarAsset(customFbxUnityPath, sourceMappingFbxPath, outputPath: null,
                applyGeneratedAvatarToFbx: false, keepImporterConfiguredForEditing: true);
            if (result?.avatar != null)
            {
                MCBLogger.Log($"[BlenderExport] Generated Avatar '{result.avatarPath}' for '{customFbxUnityPath}'. {result.message}");
                return result.avatar;
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[BlenderExport] Could not generate an Avatar for '{customFbxUnityPath}': {ex.Message}");
        }
        return null;
    }

    private static void ApplyGeneratedAvatarToPreview(MyCustomBase customBase, string customFbxUnityPath, Avatar generatedAvatar)
    {
        if (customBase == null || generatedAvatar == null || string.IsNullOrWhiteSpace(customFbxUnityPath)) return;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(customFbxUnityPath)) == null) return;
        // The avatar's own Animator, never one added to an object the avatar is placed under.
        AvatarDefinitionGenerationService.SetRootAnimatorAvatar(AvatarPaths.Root(customBase), generatedAvatar);
    }

    private static SerializedProperty CreatorEntry(SerializedObject serialized, int targetIndex)
    {
        var entriesProp = serialized.FindProperty("modelFileBuildEntries");
        if (entriesProp == null) return null;
        while (entriesProp.arraySize <= targetIndex)
        {
            entriesProp.InsertArrayElementAtIndex(entriesProp.arraySize);
            var newEntry = entriesProp.GetArrayElementAtIndex(entriesProp.arraySize - 1);
            newEntry.FindPropertyRelative("customFbx").objectReferenceValue = null;
            var newExternalPath = newEntry.FindPropertyRelative("externalCustomFbxPath");
            if (newExternalPath != null) newExternalPath.stringValue = "";
            newEntry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        }
        return entriesProp.GetArrayElementAtIndex(targetIndex);
    }

    private static void AssignCreatorCustomFbx(SerializedObject serialized, MyCustomBase customBase, string targetFbxPath, string customFbxUnityPath, Avatar customBaseAvatar)
    {
        if (customBase == null || string.IsNullOrWhiteSpace(customFbxUnityPath)) return;
        var customFbx = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(customFbxUnityPath));
        if (customFbx == null) throw new InvalidOperationException("The imported FBX could not be loaded: " + customFbxUnityPath);
        int targetIndex = GetCreatorTargetIndex(customBase, targetFbxPath);
        var entry = CreatorEntry(serialized, targetIndex);
        if (entry == null) return;
        entry.FindPropertyRelative("customFbx").objectReferenceValue = customFbx;
        var externalPath = entry.FindPropertyRelative("externalCustomFbxPath");
        if (externalPath != null) externalPath.stringValue = "";
        if (customBaseAvatar != null) entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = customBaseAvatar;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(customBase);
    }

    private static void AssignCreatorExternalCustomFbx(SerializedObject serialized, MyCustomBase customBase, string targetFbxPath, string externalFbxPath)
    {
        if (customBase == null || string.IsNullOrWhiteSpace(externalFbxPath)) return;
        int targetIndex = GetCreatorTargetIndex(customBase, targetFbxPath);
        var entry = CreatorEntry(serialized, targetIndex);
        if (entry == null) return;
        entry.FindPropertyRelative("customFbx").objectReferenceValue = null;
        var externalPath = entry.FindPropertyRelative("externalCustomFbxPath");
        if (externalPath != null) externalPath.stringValue = Path.GetFullPath(externalFbxPath);
        entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        var advancedReplacementProp = serialized.FindProperty("useAdvancedMeshReplacementForCreator");
        if (advancedReplacementProp != null && FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR)) advancedReplacementProp.boolValue = true;
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(customBase);
    }

    /// <summary>The index of a target FBX among the creator's source files; throws when it is not one of them.</summary>
    internal static int GetCreatorTargetIndex(MyCustomBase customBase, string targetFbxPath)
    {
        int index = customBase.baseFbxFiles.FindIndex(source => source != null &&
            string.Equals(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(source)), MCBUtils.ToUnityPath(targetFbxPath), StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"The Blender target '{targetFbxPath}' is not in this creator's source files. Select the intended custom base before importing.");
        return index;
    }

    /// <summary>
    /// A file or folder name from any text (<see cref="SafePaths.FileName"/>): invalid characters become underscores, no
    /// trailing dots or spaces, never a device name such as CON; empty becomes "mcb".
    /// </summary>
    public static string SanitizeFileName(string value) => SafePaths.FileName(value, "mcb");

    /// <summary>Shows a message on the Blender card of <paramref name="customBase"/> (logged too; null only logs it).</summary>
    internal static void SetStatus(MyCustomBase customBase, string message, MessageType type)
    {
        if (customBase != null) statuses[customBase.GetInstanceID()] = (message, type);
        MCBLogger.Log("[BlenderExport] " + message);
        if (customBase != null) Changed?.Invoke(customBase);
    }

    private static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static string UnityPathToAbsolute(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath)) return unityPath;
        if (Path.IsPathRooted(unityPath)) return Path.GetFullPath(unityPath);
        return Path.GetFullPath(Path.Combine(GetProjectRoot(), unityPath));
    }
}
#endif
