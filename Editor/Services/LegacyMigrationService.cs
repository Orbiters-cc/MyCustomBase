#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

/// <summary>
/// Releases a creator distributed before MCB (a custom FBX in an avatar project) and the migration of avatars built from
/// them: the avatar's renderers go back to the original base model so MCB versions apply as on any default base, and the
/// release's own setup that MCB versions replace is taken out (objects switched off, FX layers and parameters removed
/// from copies of the user's FX controller, expression parameters and menus, which are never edited). What the avatar
/// had is recorded on its MCB components: it is the person's original base, and <see cref="RestoreOriginal"/> brings it
/// back when they go back to the original base after a version.
/// </summary>
public static class LegacyMigrationService
{
    public const string MigratedFolder = "Assets/MCB/migrated";
    private static readonly Dictionary<string, Task<List<Match>>> Lookups = new Dictionary<string, Task<List<Match>>>();

    internal static string LookupKey(Transform avatar, string token, int assetId = 0) => MCBUtils.getApiUrl() + "|" + token + "|"
        + avatar.GetInstanceID() + "|" + assetId + "|" + string.Join("|", FxLayerNames(avatar)) + "|" + string.Join("|", ModelPaths(avatar).Select(p => p + ":" + File.GetLastWriteTimeUtc(p).Ticks));

    /// <summary>Gallery and apply share both completed and in-flight identification; failed requests can be retried.</summary>
    public static Task<List<Match>> LookupAsync(Transform avatar, string token, int assetId = 0)
    {
        string key = LookupKey(avatar, token, assetId);
        if (!Lookups.TryGetValue(key, out var task) || task.IsFaulted || task.IsCanceled)
            Lookups[key] = task = FindAsync(avatar, token, assetId);
        return task;
    }

    internal static IEnumerator PrepareApply(McbSession session, int assetId, Action migrated, CustomBaseVersion requested = null)
    {
        var owner = session.customBaseTarget;
        if (owner == null) throw new InvalidOperationException("The avatar is no longer available.");
        if (owner.appliedCustomBaseAssetId > 0 || owner.legacyOriginal?.migrated == true) yield break;
        var avatar = AvatarPaths.Root(owner);
        if (!avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(r => r.sharedMesh != null)) yield break;
        string token = session.authToken;
        string key = LookupKey(avatar, token, assetId);
        var lookup = session.HasServerAccess ? LookupAsync(avatar, token, assetId) : Task.FromResult(new List<Match>());
        while (!lookup.IsCompleted) yield return null;
        if (owner == null || session.authToken != token || LookupKey(avatar, token, assetId) != key)
            throw new InvalidOperationException("The avatar or signed-in account changed; apply again.");
        if (owner.appliedCustomBaseAssetId > 0 || owner.legacyOriginal?.migrated == true) yield break;
        var asset = session.SelectedAsset;
        var originals = asset != null && asset.id == assetId
            ? OriginalBaseLibrary.Versions(asset).SelectMany(v => v.sourceFiles ?? Array.Empty<ModelFileData>()).ToList()
            : new List<ModelFileData>();
        originals.AddRange(requested?.sourceFiles ?? Array.Empty<ModelFileData>());
        var matches = PlanCustomBody(avatar, assetId, originals, lookup.GetAwaiter().GetResult());
        if (matches.Count == 0) yield break;
        if (!session.HasServerAccess)
            throw new InvalidOperationException("Sign in before installing a version on your custom body so its compatible versions can be refreshed.");
        Migrate(avatar, matches);
        session.RedetectBaseFbx();
        migrated();
    }

    // Discovery's sourceFiles can be filtered to the matching set. sourceVersions holds every supported original.
    internal static List<Match> PlanCustomBody(Transform avatar, int assetId, IEnumerable<ModelFileData> sources, IEnumerable<Match> identified)
    {
        var matches = identified.Where(m => m.assetId == assetId).ToList();
        var originals = sources.Concat(matches.SelectMany(m => m.originals)).Where(o => !string.IsNullOrEmpty(o?.hash))
            .GroupBy(o => o.hash, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (originals.Count == 0)
            throw new InvalidOperationException("The original base model information is missing. Refresh the asset gallery and try again.");
        var originalPath = PickOriginal(avatar, originals, Array.Empty<string>());
        if (originalPath == null)
            throw new InvalidOperationException("Import the original base model in this project first (" +
                string.Join(", ", originals.Select(o => Path.GetFileName(o.path)).Distinct()) + ").");
        var original = AssetDatabase.LoadAssetAtPath<GameObject>(originalPath);
        var paths = original.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Select(r => AnimationUtility.CalculateTransformPath(r.transform, original.transform)).ToArray();
        var release = matches.FirstOrDefault()?.legacyRelease ?? new Release { label = "Your custom body" };
        if (matches.Select(m => m.legacyRelease.label).Distinct().Count() > 1)
            throw new InvalidOperationException("This avatar matches more than one legacy release of this asset.");
        var custom = UnregisteredBaseRenderers(avatar, paths, release.cleanup ?? new Cleanup(), Array.Empty<string>(), originalPath, originals);
        if (custom.Count == 0) return new List<Match>();
        foreach (var model in custom.Select(c => c.model).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!matches.Any(m => string.Equals(m.legacyModelPath, model, StringComparison.OrdinalIgnoreCase)))
                matches.Add(new Match { assetId = assetId, legacyModelPath = model, legacyRelease = release, originals = originals });
        return matches;
    }

    internal static IEnumerator PrepareApply(int assetId, Task<List<Match>> lookup, Action validate, Action<IList<Match>> migrate)
    {
        while (!lookup.IsCompleted) yield return null;
        var matches = lookup.GetAwaiter().GetResult().Where(m => m.assetId == assetId).ToList();
        validate();
        if (matches.Count == 0) yield break;
        if (matches.Select(m => m.legacyRelease.label).Distinct().Count() != 1)
            throw new InvalidOperationException("This avatar matches more than one legacy release of this asset; migrate one release first.");
        migrate(matches);
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class Cleanup
    {
        [JsonProperty] public List<string> objects = new List<string>();
        [JsonProperty] public List<string> layers = new List<string>();
        [JsonProperty] public List<string> parameters = new List<string>();
        // Legacy renderer path -> original renderer path.
        [JsonProperty] public Dictionary<string, string> rendererMap = new Dictionary<string, string>();
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class Release
    {
        [JsonProperty] public string label;
        [JsonProperty] public List<string> customModelHashes = new List<string>();
        [JsonProperty] public Cleanup cleanup = new Cleanup();
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class Match
    {
        [JsonProperty] public string hash, kind, assetName;
        [JsonProperty] public int assetId;
        [JsonProperty] public AvatarAssetBaseInfo avatarBase;
        [JsonProperty] public Release legacyRelease;
        [JsonProperty] public List<ModelFileData> originals = new List<ModelFileData>();
        // The avatar's model file with this hash.
        public string legacyModelPath;
    }

    /// <summary>Owner: turns an avatar listing into a custom base (name, description, avatar base by id or name).</summary>
    public static Task<JObject> UpdateListingAsync(string token, int assetId, JObject listing) =>
        MCBJsonRequest.SendAsync<JObject>("PUT", MCBUtils.getApiUrl() + "/custom-bases/" + assetId + "/listing", token, listing, "Update custom base listing");

    /// <summary>
    /// The listing request for <paramref name="listing"/>. The server only creates an avatar base for a name no base has
    /// with what recognises its projects: the project paths of its FBX files ("avatarBasePaths"), or the source files the
    /// custom base uploaded. A base named without an id or paths before any upload gets <paramref name="originalModels"/>
    /// (the avatar's original models); the server ignores them when the base exists.
    /// </summary>
    public static JObject ListingRequest(JObject listing, IEnumerable<string> originalModels)
    {
        var request = (JObject)(listing ?? new JObject()).DeepClone();
        bool namedOnly = !string.IsNullOrWhiteSpace(request.Value<string>("avatarBaseName")) &&
                         (request["avatarBaseId"] == null || request["avatarBaseId"].Type == JTokenType.Null) &&
                         (request["avatarBasePaths"] == null || request["avatarBasePaths"].Type == JTokenType.Null);
        if (!namedOnly) return request;
        var paths = (originalModels ?? Enumerable.Empty<string>())
            .Where(path => path != null && path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
        if (paths.Length > 0) request["avatarBasePaths"] = new JArray(paths.Cast<object>().ToArray());
        return request;
    }

    /// <summary>Owner: replaces the asset's legacy releases.</summary>
    public static async Task<Release[]> SaveReleasesAsync(string token, int assetId, Release[] releases) =>
        (await MCBJsonRequest.SendAsync<JObject>("PUT", MCBUtils.getApiUrl() + "/custom-bases/" + assetId + "/legacy-releases", token,
            new { legacyReleases = releases ?? Array.Empty<Release>() }, "Save legacy releases"))["legacyReleases"]?.ToObject<Release[]>() ?? Array.Empty<Release>();

    /// <summary>The model files under Assets that the avatar's renderers come from.</summary>
    public static List<string> ModelPaths(Transform avatar) =>
        avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null)
            .Select(r => AssetDatabase.GetAssetPath(r.sharedMesh)).Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Legacy releases the avatar's model files belong to (empty when none, offline or unknown).</summary>
    public static async Task<List<Match>> FindAsync(Transform avatar, string token, int assetId = 0)
    {
        var paths = ModelPaths(avatar);
        if (paths.Count == 0 && assetId <= 0) return new List<Match>();
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in paths.Take(32))
        {
            string hash = await ContentHash.GetAsync(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(hash) && !byHash.ContainsKey(hash)) byHash.Add(hash, path);
        }
        if (byHash.Count == 0 && assetId <= 0) return new List<Match>();
        var request = new JObject { ["hashes"] = JArray.FromObject(byHash.Keys.ToArray()) };
        if (assetId > 0) request["assetId"] = assetId;
        var response = await MCBJsonRequest.SendAsync<JObject>("POST", MCBUtils.getApiUrl() + "/custom-bases/identify", token,
            request, "Identify avatar models");
        return ReadIdentification(avatar, response, byHash, assetId);
    }

    internal static List<Match> ReadIdentification(Transform avatar, JObject response, IDictionary<string, string> byHash, int assetId)
    {
        var matches = (response["matches"] as JArray ?? new JArray()).OfType<JObject>().Where(m => m.Value<string>("kind") == "legacy")
            .Select(m => m.ToObject<Match>()).Where(m => m?.legacyRelease != null && (assetId <= 0 || m.assetId == assetId) && byHash.ContainsKey(m.hash)).ToList();
        foreach (var match in matches) match.legacyModelPath = byHash[match.hash];
        var asset = response["asset"] as JObject;
        if (assetId <= 0 || asset?.Value<int>("assetId") != assetId || matches.Any(m => m.assetId == assetId)) return matches;
        var release = RecognizeRelease(FxLayerNames(avatar), asset["legacyReleases"]?.ToObject<List<Release>>());
        if (release == null) return matches;
        var originals = asset["originals"]?.ToObject<List<ModelFileData>>() ?? new List<ModelFileData>();
        var originalHashes = new HashSet<string>(originals.Select(o => o.hash), StringComparer.OrdinalIgnoreCase);
        foreach (var model in byHash.Where(m => !originalHashes.Contains(m.Key)))
            matches.Add(new Match { assetId = assetId, assetName = asset.Value<string>("assetName"),
                avatarBase = asset["avatarBase"]?.ToObject<AvatarAssetBaseInfo>(), kind = "legacy", legacyRelease = release,
                originals = originals, legacyModelPath = model.Value });
        return matches;
    }

    internal static IEnumerable<string> FxLayerNames(Transform avatar)
    {
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        return (descriptor?.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
            .Where(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).Select(l => l.animatorController).OfType<AnimatorController>()
            .SelectMany(c => c.layers).Select(l => l.name).ToArray();
    }

    internal static Release RecognizeRelease(IEnumerable<string> fxLayers, IEnumerable<Release> releases)
    {
        var found = new HashSet<string>((fxLayers ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.Ordinal);
        return (releases ?? Enumerable.Empty<Release>()).Where(r => r != null).Select(r => new {
            release = r, names = (r.cleanup?.layers ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal).ToArray()
        }).Select(r => new { r.release, total = r.names.Length, hits = r.names.Count(found.Contains) })
            .Where(r => r.total > 0 && r.hits >= Math.Min(8, r.total) && r.hits * 5 >= r.total * 3)
            .OrderByDescending(r => r.hits).ThenBy(r => r.total).Select(r => r.release).FirstOrDefault();
    }

    /// <summary>
    /// Migrates the avatar from a legacy release, as one Undo step: renderers from the legacy model use the original base
    /// model's meshes and bones again (blendshape weights kept by name), the release's objects are switched off and left
    /// out of builds (EditorOnly), and copies of the FX controller, expression parameters and menus without its layers and
    /// parameters replace the originals on the avatar descriptor. The original base model must be imported in the project.
    /// </summary>
    public static object Migrate(Transform avatar, IList<Match> matches)
    {
        if (avatar == null || matches == null || matches.Count == 0 || matches.Any(m => m?.legacyRelease == null || string.IsNullOrEmpty(m.legacyModelPath)))
            throw new ArgumentException("Find the avatar's legacy release first.");
        var match = matches[0];
        if (matches.Any(m => m.assetId != match.assetId || m.legacyRelease.label != match.legacyRelease.label))
            throw new ArgumentException("Migrate one legacy release at a time.");
        // Every model file of the release the avatar uses (e.g. a body and a separate tail model) goes in one step.
        var legacyPaths = new HashSet<string>(matches.Select(m => m.legacyModelPath), StringComparer.OrdinalIgnoreCase);
        var originals = matches.SelectMany(m => m.originals).Where(o => o?.hash != null).GroupBy(o => o.hash.ToLowerInvariant()).Select(g => g.First()).ToList();
        var cleanup = match.legacyRelease.cleanup ?? new Cleanup();
        string originalPath = PickOriginal(avatar, originals, legacyPaths)
            ?? throw new InvalidOperationException("Import the original base model in this project first (" +
                string.Join(", ", originals.Select(o => Path.GetFileName(o.path)).Distinct()) + ").");
        var original = AssetDatabase.LoadAssetAtPath<GameObject>(originalPath) ?? throw new InvalidOperationException("The original base model does not load: " + originalPath);
        return Migrate(avatar, matches, original);
    }

    internal static object Migrate(Transform avatar, IList<Match> matches, GameObject original)
    {
        var match = matches[0];
        var legacyPaths = new HashSet<string>(matches.Select(m => m.legacyModelPath), StringComparer.OrdinalIgnoreCase);
        var originals = matches.SelectMany(m => m.originals).Where(o => o?.hash != null).ToList();
        var cleanup = match.legacyRelease.cleanup ?? new Cleanup();
        string originalPath = AssetDatabase.GetAssetPath(original);
        var originalRenderers = original.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .ToDictionary(r => AnimationUtility.CalculateTransformPath(r.transform, original.transform), StringComparer.Ordinal);

        // Plan every renderer first: nothing changes unless every bone resolves.
        var plans = new List<(SkinnedMeshRenderer renderer, SkinnedMeshRenderer source, Transform[] bones, Transform root, string name)>();
        var unmatched = new List<string>();
        foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.sharedMesh == null || !legacyPaths.Contains(AssetDatabase.GetAssetPath(renderer.sharedMesh))) continue;
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar);
            string target = cleanup.rendererMap != null && cleanup.rendererMap.TryGetValue(path, out string mapped) ? mapped : path;
            if (!originalRenderers.TryGetValue(target, out var source)) { unmatched.Add(path); continue; }
            var bones = source.bones.Select(b => Resolve(avatar, original.transform, b)).ToArray();
            int missing = Array.IndexOf(bones, null);
            if (source.rootBone != null && Resolve(avatar, original.transform, source.rootBone) == null)
                throw new InvalidOperationException("The avatar has no root bone for " + path + ".");
            if (missing >= 0) throw new InvalidOperationException("The avatar has no bone '" + source.bones[missing].name + "' for " + path + ".");
            plans.Add((renderer, source, bones, source.rootBone == null ? renderer.rootBone : Resolve(avatar, original.transform, source.rootBone),
                target.Split('/').Last()));
        }
        if (plans.Count == 0) throw new InvalidOperationException("No renderer of this avatar uses the legacy model with a matching original renderer.");
        // A base renderer (Body...) on a model that is neither the original nor this release's (another build of the
        // release the creator did not register) would stay custom while the rest migrates: refuse before changing anything.
        var unknown = UnregisteredBaseRenderers(avatar, originalRenderers.Keys, cleanup, legacyPaths, originalPath, originals);
        if (unknown.Count > 0)
            throw new InvalidOperationException(string.Join(", ", unknown.Select(u => u.renderer + " uses " + Path.GetFileName(u.model)))
                + ", which " + match.legacyRelease.label + " does not list. Migrating the rest would leave a half-converted avatar: "
                + "ask the creator to add " + (unknown.Select(u => u.model).Distinct().Count() == 1 ? "that model" : "those models") + " to the release.");

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Migrate " + match.legacyRelease.label + " to MCB");
        // The legacy model's shape values (its own shapes too: "orbit ears wide"), the body's first: the original model
        // lacks the release's own shapes, so the next apply of the asset sets them back instead of its defaults.
        var legacyWeights = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var plan in plans.OrderByDescending(p => p.name == "Body"))
            for (int i = 0; i < plan.renderer.sharedMesh.blendShapeCount; i++)
            {
                string shape = plan.renderer.sharedMesh.GetBlendShapeName(i);
                if (!legacyWeights.ContainsKey(shape)) legacyWeights[shape] = plan.renderer.GetBlendShapeWeight(i);
            }
        RememberLegacyShapes(avatar, match.assetId, legacyWeights);
        var descriptorBefore = avatar.GetComponent<VRCAvatarDescriptor>();
        var animatorBefore = avatar.GetComponent<Animator>();
        var record = new LegacyOriginal { migrated = true, assetId = match.assetId, label = match.legacyRelease.label,
            animatorAvatar = animatorBefore != null ? animatorBefore.avatar : null,
            animatorController = animatorBefore != null ? animatorBefore.runtimeAnimatorController : null,
            animationLayers = descriptorBefore?.baseAnimationLayers?.ToArray(),
            expressionParameters = descriptorBefore?.expressionParameters, expressionsMenu = descriptorBefore?.expressionsMenu };
        foreach (var plan in plans)
        {
            var state = new LegacyRendererState { renderer = plan.renderer, name = plan.renderer.name, mesh = plan.renderer.sharedMesh, bones = plan.renderer.bones,
                rootBone = plan.renderer.rootBone, bounds = plan.renderer.localBounds, materials = plan.renderer.sharedMaterials };
            for (int i = 0; i < state.mesh.blendShapeCount; i++) { state.shapes.Add(state.mesh.GetBlendShapeName(i)); state.weights.Add(plan.renderer.GetBlendShapeWeight(i)); }
            record.renderers.Add(state);
        }
        var posed = new HashSet<Transform>();
        foreach (var bone in record.renderers.SelectMany(r => r.bones.Append(r.rootBone)))
            for (var t = bone; t != null && t != avatar && t.IsChildOf(avatar) && posed.Add(t); t = t.parent)
                record.bones.Add(new LegacyBonePose { bone = t, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
        foreach (var plan in plans)
        {
            var weights = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < plan.renderer.sharedMesh.blendShapeCount; i++) weights[plan.renderer.sharedMesh.GetBlendShapeName(i)] = plan.renderer.GetBlendShapeWeight(i);
            Undo.RecordObject(plan.renderer, "Migrate renderer");
            var mesh = plan.source.sharedMesh;
            var materials = plan.renderer.sharedMaterials;
            var oldSlots = MaterialSlotNames.Of(plan.renderer).Select(MaterialSlotNames.Normalize).ToArray();
            var newSlots = MaterialSlotNames.Of(plan.source).Select(MaterialSlotNames.Normalize).ToArray();
            plan.renderer.sharedMesh = mesh;
            plan.renderer.bones = plan.bones;
            plan.renderer.rootBone = plan.root;
            plan.renderer.localBounds = plan.source.localBounds;
            // Each original slot keeps the avatar's material of the same slot name (else of the same index). A material
            // stored inside the legacy model file gives way to the original's, so the avatar stops depending on that file.
            plan.renderer.sharedMaterials = Enumerable.Range(0, mesh.subMeshCount).Select(i =>
            {
                int from = i < newSlots.Length && !string.IsNullOrEmpty(newSlots[i]) ? Array.FindIndex(oldSlots, s => string.Equals(s, newSlots[i], StringComparison.OrdinalIgnoreCase)) : -1;
                var material = from >= 0 && from < materials.Length ? materials[from] : materials.ElementAtOrDefault(i);
                if (material == null || legacyPaths.Contains(AssetDatabase.GetAssetPath(material))) material = plan.source.sharedMaterials.ElementAtOrDefault(i) ?? material;
                return material;
            }).ToArray();
            for (int i = 0; i < mesh.blendShapeCount; i++)
                plan.renderer.SetBlendShapeWeight(i, weights.TryGetValue(mesh.GetBlendShapeName(i), out float weight) ? weight : 0f);
            if (plan.renderer.name != plan.name)
            {
                Undo.RecordObject(plan.renderer.gameObject, "Rename migrated renderer");
                plan.renderer.name = plan.name;
            }
            EditorUtility.SetDirty(plan.renderer);
        }

        var removedObjects = new List<string>();
        foreach (string path in cleanup.objects ?? new List<string>())
        {
            var target = avatar.Find(path);
            if (target == null || target == avatar) continue;
            // Kept on the avatar, switched off and out of every build (EditorOnly): they are part of what the person had,
            // and going back to the original base switches them on again with every reference to them intact.
            record.objects.Add(new LegacyObjectState { target = target.gameObject, active = target.gameObject.activeSelf, tag = target.gameObject.tag });
            Undo.RecordObject(target.gameObject, "Switch off " + match.legacyRelease.label + " object");
            target.gameObject.SetActive(false);
            target.gameObject.tag = "EditorOnly";
            removedObjects.Add(path);
        }
        var owners = avatar.GetComponentsInChildren<MyCustomBase>(true);
        // Copies an earlier migration of this avatar made are used again: what the person changed in them since stays.
        var earlier = owners.SelectMany(o => o.legacyOriginal?.assets ?? new List<LegacyAssetSwap>()).Where(s => s != null && s.original != null && s.copy != null).ToList();
        var assets = CleanDescriptor(avatar, cleanup, match.legacyRelease.label, earlier, record.assets);
        foreach (var owner in owners)
        {
            Undo.RecordObject(owner, "Remember " + match.legacyRelease.label);
            owner.legacyOriginal = new LegacyOriginal { migrated = true, assetId = record.assetId, label = record.label, renderers = record.renderers.ToList(),
                animatorAvatar = record.animatorAvatar, animatorController = record.animatorController,
                animationLayers = record.animationLayers, expressionParameters = record.expressionParameters, expressionsMenu = record.expressionsMenu,
                objects = record.objects.ToList(), bones = record.bones.ToList(), assets = record.assets.ToList(), baseModels = (owner.baseFbxFiles ?? new List<GameObject>()).Where(m => m != null).ToList() };
            EditorUtility.SetDirty(owner);
        }
        PointBaseModelsAtOriginal(avatar, legacyPaths, original);
        Undo.CollapseUndoOperations(group);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
        AvatarAssetDiscoveryService.InvalidateDiscoveryCache();
        Lookups.Clear();
        return new { release = match.legacyRelease.label, original = originalPath, renderers = plans.Select(p => AnimationUtility.CalculateTransformPath(p.renderer.transform, avatar)).ToArray(),
            unmatched, removedObjects, assets };
    }

    /// <summary>
    /// Brings back what the avatar had before its migration (the person's original base), once a version is reset: the
    /// legacy model's meshes, bones and materials on its renderers, its objects switched on again, and the avatar
    /// descriptor's own FX controller, expression parameters and menus. Blendshapes keep the person's current value by
    /// name, then the value remembered for the asset (the release's own shapes), then the value from before the migration.
    /// Returns the release's label, or null when the avatar has nothing to restore.
    /// </summary>
    public static string RestoreOriginal(Transform avatar)
    {
        if (avatar == null) return null;
        var owners = avatar.GetComponentsInChildren<MyCustomBase>(true).Where(o => o.legacyOriginal != null && o.legacyOriginal.migrated).ToList();
        if (owners.Count == 0) return null;
        var record = owners[0].legacyOriginal;
        string step = "Restore " + record.label;
        var remembered = new Dictionary<string, float>(StringComparer.Ordinal);
        var memory = owners[0].blendshapeMemory.FirstOrDefault(m => m.assetId == record.assetId);
        for (int i = 0; memory != null && i < memory.names.Count && i < memory.values.Count; i++) remembered[memory.names[i]] = memory.values[i];
        foreach (var state in record.renderers)
        {
            var renderer = state.renderer;
            if (renderer == null || state.mesh == null) continue;
            var current = new Dictionary<string, float>(StringComparer.Ordinal);
            if (renderer.sharedMesh != null)
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++) current[renderer.sharedMesh.GetBlendShapeName(i)] = renderer.GetBlendShapeWeight(i);
            var materials = renderer.sharedMaterials;
            Undo.RecordObject(renderer, step);
            renderer.sharedMesh = state.mesh;
            if (state.bones != null && state.bones.Length > 0) renderer.bones = state.bones;
            if (state.rootBone != null) renderer.rootBone = state.rootBone;
            renderer.localBounds = state.bounds;
            var before = state.materials ?? new Material[0];
            renderer.sharedMaterials = Enumerable.Range(0, state.mesh.subMeshCount)
                .Select(i => i < before.Length && before[i] != null ? before[i] : materials.ElementAtOrDefault(i)).ToArray();
            for (int i = 0; i < state.mesh.blendShapeCount; i++)
            {
                string shape = state.mesh.GetBlendShapeName(i);
                int saved = state.shapes.IndexOf(shape);
                renderer.SetBlendShapeWeight(i, current.TryGetValue(shape, out float now) ? now
                    : remembered.TryGetValue(shape, out float kept) ? kept
                    : saved >= 0 && saved < state.weights.Count ? state.weights[saved] : 0f);
            }
            if (!string.IsNullOrEmpty(state.name) && renderer.name != state.name)
            {
                Undo.RecordObject(renderer.gameObject, step);
                renderer.name = state.name;
            }
            EditorUtility.SetDirty(renderer);
        }
        foreach (var state in record.objects)
        {
            if (state.target == null) continue;
            Undo.RecordObject(state.target, step);
            state.target.SetActive(state.active);
            state.target.tag = string.IsNullOrEmpty(state.tag) ? "Untagged" : state.tag;
        }
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        if (descriptor != null)
        {
            Undo.RecordObject(descriptor, step);
            descriptor.baseAnimationLayers = record.animationLayers;
            descriptor.expressionParameters = record.expressionParameters;
            descriptor.expressionsMenu = record.expressionsMenu;
            EditorUtility.SetDirty(descriptor);
        }
        var animator = avatar.GetComponent<Animator>();
        if (animator != null)
        {
            Undo.RecordObject(animator, step);
            animator.avatar = record.animatorAvatar;
            animator.runtimeAnimatorController = record.animatorController;
            EditorUtility.SetDirty(animator);
        }
        foreach (var pose in record.bones)
        {
            if (pose.bone == null) continue;
            Undo.RecordObject(pose.bone, step);
            pose.bone.localPosition = pose.position;
            pose.bone.localRotation = pose.rotation;
            pose.bone.localScale = pose.scale;
        }
        foreach (var owner in owners)
        {
            var models = owner.legacyOriginal.baseModels.Where(m => m != null).ToList();
            if (models.Count > 0) FileManagerService.SetCreatorSourceFiles(owner, models);
            Undo.RecordObject(owner, step);
            // The copies stay known: the next migration uses them again.
            owner.legacyOriginal = new LegacyOriginal { assetId = record.assetId, label = record.label, assets = owner.legacyOriginal.assets };
            EditorUtility.SetDirty(owner);
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
        AvatarAssetDiscoveryService.InvalidateDiscoveryCache();
        return record.label;
    }

    /// <summary>
    /// The project's copy of one of the original base models: preferably one the avatar already uses (its other renderers,
    /// its Animator's avatar), then files named like an original, then any other model. Each file is hashed once, and
    /// files far larger than an avatar base are skipped.
    /// </summary>
    // Each MCB component of the avatar remembers the legacy shape values for the asset (in the migration's Undo step):
    // VersionActions applies remembered values over the version's defaults when the avatar comes from its original base.
    private static void RememberLegacyShapes(Transform avatar, int assetId, Dictionary<string, float> weights)
    {
        if (assetId <= 0 || weights.Count == 0) return;
        foreach (var owner in avatar.GetComponentsInChildren<MyCustomBase>(true))
        {
            Undo.RecordObject(owner, "Remember legacy blendshapes");
            var memory = owner.blendshapeMemory.FirstOrDefault(m => m.assetId == assetId);
            if (memory == null) owner.blendshapeMemory.Add(memory = new BlendshapeMemory { assetId = assetId });
            foreach (var pair in weights)
            {
                int index = memory.names.IndexOf(pair.Key);
                if (index < 0) { memory.names.Add(pair.Key); memory.values.Add(pair.Value); }
                else if (index < memory.values.Count) memory.values[index] = pair.Value;
            }
            EditorUtility.SetDirty(owner);
        }
    }

    // MCB's base models (the first one is what versions apply to and reset from) named the legacy models: the original
    // takes their place, in the migration's Undo step, so an open inspector never applies from the legacy model.
    private static void PointBaseModelsAtOriginal(Transform avatar, ICollection<string> legacyPaths, GameObject original)
    {
        foreach (var owner in avatar.GetComponentsInChildren<MyCustomBase>(true))
        {
            if (owner.baseFbxFiles == null || !owner.baseFbxFiles.Any(m => m != null && legacyPaths.Contains(AssetDatabase.GetAssetPath(m)))) continue;
            var models = owner.baseFbxFiles.Select(m => m != null && legacyPaths.Contains(AssetDatabase.GetAssetPath(m)) ? original : m)
                .Where(m => m != null).Distinct().ToList();
            FileManagerService.SetCreatorSourceFiles(owner, models);
        }
    }

    // Avatar renderers at an original renderer's path whose model is neither a legacy model of the release nor an
    // original base file (by hash): what a migration would leave custom.
    internal static List<(string renderer, string model)> UnregisteredBaseRenderers(Transform avatar, ICollection<string> originalPaths, Cleanup cleanup,
        ICollection<string> legacyPaths, string originalPath, IEnumerable<ModelFileData> originals)
    {
        const long MaxBytes = 256L << 20;
        var wanted = new HashSet<string>(originals.Where(o => o?.hash != null).Select(o => o.hash.ToLowerInvariant()), StringComparer.Ordinal);
        var known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            string model = renderer.sharedMesh == null ? null : AssetDatabase.GetAssetPath(renderer.sharedMesh);
            if (renderer.sharedMesh == null) continue;
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar);
            string target = cleanup.rendererMap != null && cleanup.rendererMap.TryGetValue(path, out string mapped) ? mapped : path;
            if (!originalPaths.Contains(target)) continue;
            if (string.IsNullOrEmpty(model))
                throw new InvalidOperationException("Save the custom body mesh on '" + path + "' as a project asset before installing this version.");
            if (legacyPaths.Contains(model) || string.Equals(model, originalPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!known.TryGetValue(model, out bool isOriginal))
            {
                var info = new FileInfo(Path.GetFullPath(model));
                string hash = info.Exists && info.Length <= MaxBytes ? ContentHash.Proof(model)?.ToLowerInvariant() : null;
                known[model] = isOriginal = hash != null && wanted.Contains(hash);
            }
            if (!isOriginal) result.Add((path, model));
        }
        return result;
    }

    private static string PickOriginal(Transform avatar, IReadOnlyCollection<ModelFileData> originals, ICollection<string> legacyPaths)
    {
        const long MaxBytes = 256L << 20;
        var wanted = new HashSet<string>(originals.Select(o => o.hash.ToLowerInvariant()), StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool IsOriginal(string path)
        {
            if (string.IsNullOrEmpty(path) || legacyPaths.Contains(path) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return false;
            if (!hashes.TryGetValue(path, out string hash))
            {
                var info = new FileInfo(Path.GetFullPath(path));
                hashes[path] = hash = info.Exists && info.Length <= MaxBytes ? ContentHash.Proof(path)?.ToLowerInvariant() : null;
            }
            return hash != null && wanted.Contains(hash);
        }
        var used = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).Select(r => AssetDatabase.GetAssetPath(r.sharedMesh)).ToList();
        var animator = avatar.GetComponent<Animator>();
        if (animator != null && animator.avatar != null) used.Add(AssetDatabase.GetAssetPath(animator.avatar));
        string found = used.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsOriginal);
        if (found != null) return found;
        var names = new HashSet<string>(originals.Select(o => Path.GetFileName(o.path ?? "")), StringComparer.OrdinalIgnoreCase);
        return AssetDatabase.FindAssets("t:Model").Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => names.Contains(Path.GetFileName(p)) ? 0 : 1).FirstOrDefault(IsOriginal);
    }

    // The bone of the original model at the same path in the avatar, else the avatar's only transform with its name.
    private static Transform Resolve(Transform avatar, Transform originalRoot, Transform bone)
    {
        var found = avatar.Find(AnimationUtility.CalculateTransformPath(bone, originalRoot));
        if (found != null) return found;
        var named = avatar.GetComponentsInChildren<Transform>(true).Where(t => t.name == bone.name).Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    // Copies of the FX controller, parameters and menus without the release's layers and parameters, on the descriptor.
    private static object CleanDescriptor(Transform avatar, Cleanup cleanup, string label, List<LegacyAssetSwap> earlier, List<LegacyAssetSwap> swaps)
    {
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        // Names are compared trimmed: authored layer names can carry stray spaces the release list does not keep.
        var layers = new HashSet<string>((cleanup.layers ?? new List<string>()).Select(n => n.Trim()), StringComparer.Ordinal);
        var parameters = new HashSet<string>((cleanup.parameters ?? new List<string>()).Select(n => n.Trim()), StringComparer.Ordinal);
        if (descriptor == null || layers.Count == 0 && parameters.Count == 0) return null;
        string folder = MCBUtils.ToUnityPath(Path.Combine(MigratedFolder, Safe(avatar.name) + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        var copies = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
        T Copy<T>(T asset) where T : UnityEngine.Object
        {
            if (asset == null) return null;
            if (copies.TryGetValue(asset, out var known)) return (T)known;
            var reused = earlier.Select(s => s.original == asset ? s.copy as T : null).FirstOrDefault(c => c != null);
            if (reused != null) { copies[asset] = reused; return reused; }
            EnsureFolder(folder);
            string source = AssetDatabase.GetAssetPath(asset);
            string destination = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileNameWithoutExtension(source) + " (MCB)" + Path.GetExtension(source));
            if (!AssetDatabase.CopyAsset(source, destination)) throw new IOException("Could not copy " + source);
            var copy = AssetDatabase.LoadAssetAtPath<T>(destination);
            copies[asset] = copy;
            return copy;
        }
        Undo.RecordObject(descriptor, "Use migrated avatar assets");
        var removedLayers = new List<string>();
        var removedParameters = new List<string>();
        for (int i = 0; i < descriptor.baseAnimationLayers.Length; i++)
        {
            var layer = descriptor.baseAnimationLayers[i];
            if (layer.type != VRCAvatarDescriptor.AnimLayerType.FX || !(layer.animatorController is AnimatorController controller)) continue;
            // Copied only when something actually goes: a listed parameter other layers still use stays.
            var kept = controller.layers.Where(l => !layers.Contains(l.name.Trim())).ToList();
            var stillUsed = UsedParameters(kept);
            if (kept.Count == controller.layers.Length && !controller.parameters.Any(p => parameters.Contains(p.name.Trim()) && !stillUsed.Contains(p.name))) continue;
            var copy = Copy(controller);
            removedLayers.AddRange(copy.layers.Where(l => layers.Contains(l.name.Trim())).Select(l => l.name));
            copy.layers = copy.layers.Where(l => !layers.Contains(l.name.Trim())).ToArray();
            var used = UsedParameters(copy.layers);
            foreach (var parameter in copy.parameters.Where(p => parameters.Contains(p.name.Trim()) && !used.Contains(p.name)).ToList())
            {
                copy.RemoveParameter(parameter);
                removedParameters.Add(parameter.name);
            }
            EditorUtility.SetDirty(copy);
            layer.animatorController = copy;
            descriptor.baseAnimationLayers[i] = layer;
            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController == controller)
            {
                Undo.RecordObject(animator, "Use migrated FX controller");
                animator.runtimeAnimatorController = copy;
            }
        }
        if (descriptor.expressionParameters != null && descriptor.expressionParameters.parameters.Any(p => p != null && parameters.Contains((p.name ?? "").Trim())))
        {
            var copy = Copy(descriptor.expressionParameters);
            copy.parameters = copy.parameters.Where(p => p == null || !parameters.Contains((p.name ?? "").Trim())).ToArray();
            EditorUtility.SetDirty(copy);
            descriptor.expressionParameters = copy;
        }
        if (descriptor.expressionsMenu != null && Uses(descriptor.expressionsMenu, parameters, new HashSet<VRCExpressionsMenu>()))
        {
            var emptied = new List<VRCExpressionsMenu>();
            descriptor.expressionsMenu = CleanMenu(descriptor.expressionsMenu, parameters, Copy, new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>(), emptied);
            foreach (var menu in emptied)
            {
                foreach (var key in copies.Where(p => p.Value == menu).Select(p => p.Key).ToList()) copies.Remove(key);
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(menu));
            }
        }
        EditorUtility.SetDirty(descriptor);
        AssetDatabase.SaveAssets();
        swaps.AddRange(copies.Where(p => p.Key != null && p.Value != null).Select(p => new LegacyAssetSwap { original = p.Key, copy = p.Value }));
        return new { folder, removedLayers, removedParameters, copies = copies.Values.Select(AssetDatabase.GetAssetPath).ToArray() };
    }

    private static bool Controls(VRCExpressionsMenu.Control control, HashSet<string> parameters) =>
        new[] { control.parameter }.Concat(control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
            .Any(p => p != null && !string.IsNullOrEmpty(p.name) && parameters.Contains(p.name.Trim()));

    private static bool Uses(VRCExpressionsMenu menu, HashSet<string> parameters, HashSet<VRCExpressionsMenu> seen)
    {
        if (menu == null || !seen.Add(menu)) return false;
        return menu.controls.Any(c => Controls(c, parameters) || c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && Uses(c.subMenu, parameters, seen));
    }

    // Copies only the menus on a path to a removed control; the others stay shared.
    private static VRCExpressionsMenu CleanMenu(VRCExpressionsMenu menu, HashSet<string> parameters, Func<VRCExpressionsMenu, VRCExpressionsMenu> copy,
        Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> done, List<VRCExpressionsMenu> emptiedMenus)
    {
        if (done.TryGetValue(menu, out var cleaned)) return cleaned;
        if (!Uses(menu, parameters, new HashSet<VRCExpressionsMenu>())) return menu;
        var result = copy(menu);
        done[menu] = result;
        result.controls = result.controls.Where(c => !Controls(c, parameters)).ToList();
        foreach (var control in result.controls.Where(c => c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.subMenu != null))
            control.subMenu = CleanMenu(control.subMenu, parameters, copy, done, emptiedMenus);
        // A submenu the cleanup emptied (e.g. the release's face tracking menu) goes too, with its unused copy.
        foreach (var emptied in result.controls.Where(c => c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.subMenu != null
                     && done.ContainsValue(c.subMenu) && c.subMenu.controls.Count == 0).ToList())
        {
            result.controls.Remove(emptied);
            emptiedMenus.Add(emptied.subMenu);
        }
        EditorUtility.SetDirty(result);
        return result;
    }

    // Parameters the controller's states, transitions, blend trees and parameter drivers still read or write.
    private static HashSet<string> UsedParameters(IEnumerable<AnimatorControllerLayer> layers)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Tree(Motion motion)
        {
            if (!(motion is BlendTree tree)) return;
            used.Add(tree.blendParameter); used.Add(tree.blendParameterY);
            foreach (var child in tree.children) { used.Add(child.directBlendParameter); Tree(child.motion); }
        }
        void Behaviours(IEnumerable<StateMachineBehaviour> behaviours)
        {
            foreach (var driver in behaviours.OfType<VRCAvatarParameterDriver>())
                foreach (var entry in driver.parameters) { used.Add(entry.name); used.Add(entry.source); }
        }
        void Machine(AnimatorStateMachine machine)
        {
            Behaviours(machine.behaviours);
            foreach (var transition in machine.anyStateTransitions.Cast<AnimatorTransitionBase>().Concat(machine.entryTransitions))
                foreach (var condition in transition.conditions) used.Add(condition.parameter);
            foreach (var child in machine.states)
            {
                var state = child.state;
                used.Add(state.timeParameter); used.Add(state.speedParameter); used.Add(state.mirrorParameter); used.Add(state.cycleOffsetParameter);
                Tree(state.motion);
                Behaviours(state.behaviours);
                foreach (var transition in state.transitions)
                    foreach (var condition in transition.conditions) used.Add(condition.parameter);
            }
            foreach (var child in machine.stateMachines) Machine(child.stateMachine);
        }
        foreach (var layer in layers) Machine(layer.stateMachine);
        used.Remove(null); used.Remove("");
        return used;
    }

    private static string Safe(string name) => string.Concat((name ?? "Avatar").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = MCBUtils.ToUnityPath(Path.GetDirectoryName(folder));
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
#endif
