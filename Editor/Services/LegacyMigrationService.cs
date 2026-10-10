#if UNITY_EDITOR
using System;
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
/// release's own setup (objects, FX layers, parameters) that MCB versions replace is removed. The user's FX controller,
/// expression parameters and menus are copied, never edited.
/// </summary>
public static class LegacyMigrationService
{
    public const string MigratedFolder = "Assets/MCB/migrated";

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
    public static async Task<List<Match>> FindAsync(Transform avatar, string token)
    {
        var paths = ModelPaths(avatar);
        if (paths.Count == 0) return new List<Match>();
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in paths.Take(32))
        {
            string hash = await AsyncHashService.Instance.CalculateFileHashAsync(Path.GetFullPath(path), null, true);
            if (!string.IsNullOrEmpty(hash) && !byHash.ContainsKey(hash)) byHash.Add(hash, path);
        }
        if (byHash.Count == 0) return new List<Match>();
        var response = await MCBJsonRequest.SendAsync<JObject>("POST", MCBUtils.getApiUrl() + "/custom-bases/identify", token,
            new { hashes = byHash.Keys.ToArray() }, "Identify avatar models");
        var matches = (response["matches"] as JArray ?? new JArray()).OfType<JObject>().Where(m => m.Value<string>("kind") == "legacy")
            .Select(m => m.ToObject<Match>()).Where(m => m?.legacyRelease != null && byHash.ContainsKey(m.hash)).ToList();
        foreach (var match in matches) match.legacyModelPath = byHash[match.hash];
        return matches;
    }

    /// <summary>
    /// Migrates the avatar from a legacy release, as one Undo step: renderers from the legacy model use the original base
    /// model's meshes and bones again (blendshape weights kept by name), the release's objects are removed, and copies of
    /// the FX controller, expression parameters and menus without its layers and parameters replace the originals on the
    /// avatar descriptor. The original base model must be imported in the project.
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
            if (missing >= 0) throw new InvalidOperationException("The avatar has no bone '" + source.bones[missing].name + "' for " + path + ".");
            plans.Add((renderer, source, bones, source.rootBone == null ? renderer.rootBone : Resolve(avatar, original.transform, source.rootBone),
                target.Split('/').Last()));
        }
        if (plans.Count == 0) throw new InvalidOperationException("No renderer of this avatar uses the legacy model with a matching original renderer.");

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Migrate " + match.legacyRelease.label + " to MCB");
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
            // An object a prefab instance owns cannot be removed from the instance: unpack it first (part of the step).
            while (PrefabUtility.IsPartOfPrefabInstance(target.gameObject) && !PrefabUtility.IsAddedGameObjectOverride(target.gameObject))
                PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(target.gameObject), PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);
            Undo.DestroyObjectImmediate(target.gameObject);
            removedObjects.Add(path);
        }
        var assets = CleanDescriptor(avatar, cleanup, match.legacyRelease.label);
        Undo.CollapseUndoOperations(group);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
        AvatarAssetDiscoveryService.InvalidateDiscoveryCache();
        return new { release = match.legacyRelease.label, original = originalPath, renderers = plans.Select(p => AnimationUtility.CalculateTransformPath(p.renderer.transform, avatar)).ToArray(),
            unmatched, removedObjects, assets };
    }

    /// <summary>
    /// The project's copy of one of the original base models: preferably one the avatar already uses (its other renderers,
    /// its Animator's avatar), then files named like an original, then any other model. Each file is hashed once, and
    /// files far larger than an avatar base are skipped.
    /// </summary>
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
                hashes[path] = hash = info.Exists && info.Length <= MaxBytes ? MCBUtils.CalculateFileHash(path)?.ToLowerInvariant() : null;
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
    private static object CleanDescriptor(Transform avatar, Cleanup cleanup, string label)
    {
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        // Names are compared trimmed: authored layer names can carry stray spaces the release list does not keep.
        var layers = new HashSet<string>((cleanup.layers ?? new List<string>()).Select(n => n.Trim()), StringComparer.Ordinal);
        var parameters = new HashSet<string>((cleanup.parameters ?? new List<string>()).Select(n => n.Trim()), StringComparer.Ordinal);
        if (descriptor == null || layers.Count == 0 && parameters.Count == 0) return null;
        string folder = MCBUtils.ToUnityPath(Path.Combine(MigratedFolder, Safe(avatar.name) + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        EnsureFolder(folder);
        var copies = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
        T Copy<T>(T asset) where T : UnityEngine.Object
        {
            if (asset == null) return null;
            if (copies.TryGetValue(asset, out var known)) return (T)known;
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
