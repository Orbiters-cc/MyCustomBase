#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The meshes a version build takes from the avatar. Orbiters Mixer commits its edits (and Blender's, through its link) as
/// mesh assets the base's renderers show, which the FBX files versions are built from do not hold. Before a build, each
/// model whose renderers show one gets a new custom FBX written with Toolkit's <see cref="FbxExport"/>: the model the entry
/// builds from (its custom FBX, its external FBX, else the base's FBX) with every object, mesh, material slot name and
/// default blendshape weight it has, and the committed meshes in place of the meshes they replace. The entry then names
/// that FBX, so this build and the next ones hold what the avatar shows, whichever way the version is delivered (advanced
/// mesh with or without XOR, FBX patches). Not taken: meshes still in a Mixer session (a build refuses them until they are
/// committed or reverted) and committed meshes bound to other bones or another rest pose than the mesh they replace
/// (armature edits), which reach a version through Blender's Export for MCB and the Blender card's import.
/// </summary>
internal static class VersionAvatarMeshes
{
    // The product folder Mixer commits its meshes to (Toolkit's MeshAssets).
    private const string MixerProduct = "Mixer";
    // Part of every written file's name: files written by another version of this code are never taken for the same.
    private const string WriterVersion = "1";
    // In the import settings of each file written (its importer's userData), with the key of the meshes it holds.
    private const string HeldMarker = "MCB avatar meshes ";
    // Relative: bind poses of a model in centimetres hold large numbers.
    private const float BindPoseTolerance = 1e-4f;

    /// <summary>
    /// Whether a renderer is in a Mixer session (registered by MCB's Mixer integration): its working copy, edited in Mixer
    /// or in Blender, is not committed yet.
    /// </summary>
    internal static Func<Renderer, bool> InMixerSession;

    /// <summary>A renderer of the base and the target FBX it stands for.</summary>
    internal sealed class Shown
    {
        public SkinnedMeshRenderer Renderer;
        public string Target;
    }

    /// <summary>The base's renderers that show what its FBX files do not hold.</summary>
    internal sealed class Found
    {
        /// <summary>Meshes Mixer committed: a build takes them in.</summary>
        public readonly List<Shown> Committed = new List<Shown>();
        /// <summary>Mixer working copies: a build waits for their commit or revert.</summary>
        public readonly List<Shown> InSession = new List<Shown>();
    }

    /// <summary>What a build took from the avatar: the renderer (its path from the avatar), its target FBX and the custom FBX written for it.</summary>
    internal sealed class Taken
    {
        public string Renderer;
        public string Target;
        public string CustomFbx;
    }

    internal static bool IsInSession(Renderer renderer) => renderer != null && InMixerSession != null && InMixerSession(renderer);

    /// <summary>Whether the renderer shows a mesh Mixer committed, with no Mixer session open on it.</summary>
    internal static bool IsCommitted(Renderer renderer) =>
        renderer != null && !IsInSession(renderer) && MeshAssets.IsOwnedBy(MeshOf(renderer), MixerProduct);

    /// <summary>
    /// The renderers of <paramref name="customBase"/> standing for its targets (the Blender card's, see
    /// <see cref="BlenderExportImport.TargetRenderers(MyCustomBase, MCBEditor)"/>; <paramref name="editor"/> is the one
    /// showing the base, or null) that show a committed mesh or a Mixer working copy.
    /// </summary>
    internal static Found Find(MyCustomBase customBase, MCBEditor editor)
    {
        var found = new Found();
        if (customBase == null) return found;
        // Most avatars show no Mixer mesh at all: the targets are mapped only when one does.
        if (!AvatarPaths.Root(customBase).GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(r => IsInSession(r) || IsCommitted(r))) return found;
        foreach (var entry in BlenderExportImport.TargetRenderers(customBase, editor))
        {
            if (entry.Renderer == null) continue;
            var shown = new Shown { Renderer = entry.Renderer, Target = MCBUtils.ToUnityPath(entry.TargetUnityPath) };
            if (IsInSession(entry.Renderer)) found.InSession.Add(shown);
            else if (IsCommitted(entry.Renderer)) found.Committed.Add(shown);
        }
        return found;
    }

    /// <summary><see cref="Find(MyCustomBase, MCBEditor)"/> for the editor's base, for what the form shows: nothing when the avatar cannot be looked at (logged).</summary>
    internal static Found Find(MCBEditor editor)
    {
        try { return Find(editor != null ? editor.customBaseTarget : null, editor); }
        catch (Exception ex)
        {
            MCBLogger.LogWarning("[MCB] Could not check the avatar for Mixer edits: " + ex.Message);
            return new Found();
        }
    }

    // ---- What the form and the card say -----------------------------------------------------------------------------

    /// <summary>Why a build does not start while these renderers are in a Mixer session; null when there are none.</summary>
    internal static string InSessionMessage(IEnumerable<Renderer> renderers)
    {
        var names = Names(renderers);
        if (names.Count == 0) return null;
        bool one = names.Count == 1;
        return $"{string.Join(", ", names)} {(one ? "is" : "are")} being edited in Mixer or Blender: commit or revert {(one ? "it" : "them")} on the Blender card " +
               "first, so the version holds what the avatar shows.";
    }

    /// <summary>What a build does with these renderers' committed meshes; null when there are none.</summary>
    internal static string CommittedMessage(IEnumerable<Renderer> renderers)
    {
        var names = Names(renderers);
        if (names.Count == 0) return null;
        bool one = names.Count == 1;
        return $"{string.Join(", ", names)} {(one ? "shows a mesh" : "show meshes")} committed in Mixer that the FBX files do not hold: building a version " +
               $"writes {(one ? "it" : "them")} into a new custom FBX of {(one ? "its" : "their")} model, so the version holds what the avatar shows.";
    }

    /// <summary>What a build the creator asked to make without these renderers' committed meshes leaves out; null when there are none.</summary>
    internal static string LeftOutMessage(IEnumerable<Renderer> renderers)
    {
        var names = Names(renderers);
        if (names.Count == 0) return null;
        bool one = names.Count == 1;
        return $"{string.Join(", ", names)} {(one ? "shows a mesh" : "show meshes")} committed in Mixer that this version does not hold: it was built " +
               "from the FBX files as they are, as asked.";
    }

    /// <summary>
    /// What the avatar shows that its model entries do not hold, said before a version built earlier is published: meshes
    /// Mixer committed after the build or left out of it, and meshes in a Mixer session. Null when nothing (or when the
    /// avatar cannot be looked at, logged).
    /// </summary>
    internal static string NotInBuildMessage(MyCustomBase customBase, MCBEditor editor)
    {
        if (customBase == null) return null;
        try
        {
            var found = Find(customBase, editor);
            var avatar = AvatarPaths.Root(customBase);
            var notHeld = new List<Renderer>();
            foreach (var model in found.Committed.GroupBy(s => s.Target, StringComparer.OrdinalIgnoreCase))
            {
                var renderers = model.Select(s => s.Renderer).Distinct().ToList();
                int index = customBase.baseFbxFiles.FindIndex(f => f != null &&
                    string.Equals(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(f)), model.Key, StringComparison.OrdinalIgnoreCase));
                var custom = index >= 0 && index < customBase.modelFileBuildEntries.Count ? customBase.modelFileBuildEntries[index]?.customFbx : null;
                string path = custom != null ? AssetDatabase.GetAssetPath(custom) : null;
                if (path != null && AssetImporter.GetAtPath(path) is ModelImporter importer && importer.userData == HeldKey(avatar, model.Key, renderers)) continue;
                notHeld.AddRange(renderers);
            }
            var names = Names(notHeld);
            bool one = names.Count == 1;
            string committed = names.Count == 0 ? null
                : $"{string.Join(", ", names)} {(one ? "shows a mesh" : "show meshes")} committed in Mixer that the model entries do not hold (committed after the " +
                  "build, or left out of it).";
            string said = string.Join(" ", new[] { committed, InSessionMessage(found.InSession.Select(s => s.Renderer)) }.Where(text => text != null));
            return said.Length > 0 ? said : null;
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning("[MCB] Could not check the avatar for Mixer edits: " + ex.Message);
            return null;
        }
    }

    /// <summary>What a build took from the avatar, for its result; null when nothing.</summary>
    internal static string TakenMessage(IEnumerable<Taken> taken)
    {
        var list = (taken ?? Enumerable.Empty<Taken>()).Where(t => t != null).ToList();
        if (list.Count == 0) return null;
        return "Taken from the avatar into this version: " + string.Join("; ", list.GroupBy(t => t.CustomFbx).Select(model =>
                   $"{string.Join(", ", model.Select(t => t.Renderer))} (committed in Mixer), written into {model.Key} for {Path.GetFileName(model.First().Target)}")) +
               ". The model entries name these custom FBX files now.";
    }

    private static List<string> Names(IEnumerable<Renderer> renderers) =>
        (renderers ?? Enumerable.Empty<Renderer>()).Where(r => r != null).Select(r => r.name).Distinct().ToList();

    // ---- Into the entries --------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the committed meshes into a new custom FBX per model and names it in the model's entry, through
    /// <paramref name="serialized"/> (the editor's while one shows the base, so the form keeps the change; its pending
    /// changes are applied with it). Every model is written before an entry changes; a mesh that cannot be taken as the
    /// avatar shows it stops it all with the reason. The files go to <paramref name="folder"/> (by default the base's folder
    /// of imported exports), named after what they are made from: the same meshes give the same file.
    /// </summary>
    internal static List<Taken> TakeIntoEntries(MyCustomBase customBase, SerializedObject serialized, IReadOnlyCollection<Shown> committed, string folder = null)
    {
        var taken = new List<Taken>();
        if (customBase == null || serialized == null || committed == null || committed.Count == 0) return taken;
        var avatar = AvatarPaths.Root(customBase);
        if (folder == null)
        {
            folder = MCBUtils.CombineUnityPath(BlenderExportImport.ExportsFolder, BlenderExportImport.BaseFolderKey(customBase));
            // Ids the base may just have been given go through the SerializedObject too: applying the entries writes its
            // whole copy of the base back.
            foreach (var (name, value) in new[] { (nameof(MyCustomBase.mcbInstanceId), customBase.mcbInstanceId), (nameof(MyCustomBase.mcbComponentId), customBase.mcbComponentId) })
            {
                var property = serialized.FindProperty(name);
                if (property != null && property.stringValue != value) property.stringValue = value;
            }
        }
        var written = new List<(int index, string path)>();
        foreach (var model in committed.Where(s => s != null && s.Renderer != null).GroupBy(s => MCBUtils.ToUnityPath(s.Target), StringComparer.OrdinalIgnoreCase))
        {
            int index = BlenderExportImport.GetCreatorTargetIndex(customBase, model.Key);
            var renderers = model.Select(s => s.Renderer).Distinct().ToList();
            string path = WriteModel(avatar, serialized, index, model.Key, renderers, folder);
            written.Add((index, path));
            taken.AddRange(renderers.Select(r => new Taken { Renderer = SmrPathService.GetRelativeTransformPath(avatar, r.transform), Target = model.Key, CustomFbx = path }));
        }

        // What the entries name now stays for the session: Undo can go back to it.
        BlenderExportImport.RetainForSession(BlenderExportImport.FilesUsedBy(customBase));
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Custom FBX from the avatar's meshes");
        foreach (var (index, path) in written)
        {
            var entry = BlenderExportImport.CreatorEntry(serialized, index);
            entry.FindPropertyRelative("customFbx").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var external = entry.FindPropertyRelative("externalCustomFbxPath");
            if (external != null) external.stringValue = "";
        }
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(customBase);
        BlenderExportImport.RetainForSession(written.Select(w => w.path));
        foreach (var item in taken)
            MCBLogger.Log($"[MCB] The version takes {item.Renderer}'s mesh committed in Mixer: custom FBX {item.CustomFbx} for {item.Target}.");
        return taken;
    }

    // The model's new custom FBX (its Unity path): written and imported, unless the entry's custom FBX or an earlier file holds
    // these very meshes already.
    private static string WriteModel(Transform avatar, SerializedObject serialized, int index, string target, List<SkinnedMeshRenderer> renderers, string folder)
    {
        var entry = BlenderExportImport.CreatorEntry(serialized, index);
        var customFbx = entry.FindPropertyRelative("customFbx").objectReferenceValue as GameObject;
        string external = entry.FindPropertyRelative("externalCustomFbxPath")?.stringValue;
        var sources = serialized.FindProperty("baseFbxFiles");
        var targetModel = sources != null && index < sources.arraySize ? sources.GetArrayElementAtIndex(index).objectReferenceValue as GameObject : null;
        if (targetModel == null) throw new InvalidOperationException($"Target model file {index + 1} is missing its source FBX.");
        RequireUnscaledImport(target);

        string held = HeldKey(avatar, target, renderers);
        // A build that stopped after writing it, or a temporary version, left the entry on the file these meshes made.
        string customPath = customFbx != null ? AssetDatabase.GetAssetPath(customFbx) : null;
        if (customPath != null && AssetImporter.GetAtPath(customPath) is ModelImporter current && current.userData == held) return customPath;

        bool fromExternal = customFbx == null && !string.IsNullOrWhiteSpace(external) && File.Exists(Path.GetFullPath(external));
        string sourcePath = customPath ?? (fromExternal ? Path.GetFullPath(external) : AssetDatabase.GetAssetPath(targetModel));
        var key = new Hash128();
        key.Append(held);
        key.Append(sourcePath);
        key.Append(fromExternal ? MCBUtils.CalculateFileHash(sourcePath) ?? "" : AssetDatabase.GetAssetDependencyHash(sourcePath).ToString());
        string unityPath = MCBUtils.CombineUnityPath(folder, BlenderExportImport.ImportFileStem(index, target, MCBUtils.PathHash(key.ToString())) + ".fbx");
        if (AssetDatabase.LoadAssetAtPath<GameObject>(unityPath) != null) return unityPath;

        string fullPath = Path.GetFullPath(unityPath);
        SafePaths.RequireLength(fullPath + ".meta");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string temporaryImport = null;
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        try
        {
            // The model is read as it is, never instantiated: its own objects are what the file is written from.
            var model = customFbx != null ? customFbx
                : fromExternal ? NativeMeshPayloadService.ImportExternalFbxForPayload(external, out temporaryImport)
                : targetModel;
            var bindings = SmrPathService.CollectModelRendererBindings(avatar, target, targetModel, customFbx);
            var replacements = Replacements(avatar, model.transform, bindings, renderers, target);
            var items = Items(model.transform, replacements, materials, target, out var carrier);
            MeshAssets.EnsureFolder(folder);
            var result = FbxExport.Write(fullPath, items, model.transform);
            if (!result.Success) throw new InvalidOperationException($"The meshes of '{Path.GetFileName(target)}' could not be written to an FBX: {result.Error}");
            // The objects the carrier holds past its bind poses are no skin bones, as meant.
            var warnings = result.Warnings.Where(w => carrier == null || !(w.StartsWith(carrier.DisplayName + " has ", StringComparison.Ordinal) &&
                                                                          w.EndsWith(" bind poses: the extra ones are left out.", StringComparison.Ordinal))).ToList();
            if (warnings.Count > 0) MCBLogger.LogWarning($"[MCB] Writing the avatar's meshes of '{Path.GetFileName(target)}': " + string.Join(" / ", warnings));
            BlenderExportImport.WriteModelMeta(fullPath, held);
            AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceSynchronousImport);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(unityPath) == null)
            {
                AssetDatabase.DeleteAsset(unityPath);
                throw new InvalidOperationException($"The FBX written from the avatar's meshes of '{Path.GetFileName(target)}' did not import: {unityPath}");
            }
            MCBLogger.Log($"[MCB] Wrote {unityPath} from '{sourcePath}' with the meshes the avatar shows ({result.Meshes} meshes, {result.Bones} objects, " +
                          $"{result.BlendShapes} blendshapes, {EditorUtility.FormatBytes(result.Bytes)}) in {timer.Elapsed.TotalSeconds:F1} s.");
            return unityPath;
        }
        finally
        {
            foreach (var material in materials.Values) if (material != null) UnityEngine.Object.DestroyImmediate(material);
            if (temporaryImport != null) NativeMeshPayloadService.DeleteTemporaryImportedFbx(temporaryImport);
        }
    }

    // What a file written from these renderers' meshes for this target says it holds (its importer's userData).
    private static string HeldKey(Transform avatar, string target, IEnumerable<SkinnedMeshRenderer> renderers)
    {
        var meshes = new Hash128();
        meshes.Append(WriterVersion);
        meshes.Append(MCBUtils.ToUnityPath(target));
        foreach (var renderer in renderers.OrderBy(r => SmrPathService.GetRelativeTransformPath(avatar, r.transform), StringComparer.Ordinal))
        {
            string meshPath = AssetDatabase.GetAssetPath(renderer.sharedMesh);
            meshes.Append(SmrPathService.GetRelativeTransformPath(avatar, renderer.transform));
            meshes.Append(AssetDatabase.AssetPathToGUID(meshPath));
            meshes.Append(AssetDatabase.GetAssetDependencyHash(meshPath).ToString());
        }
        return HeldMarker + meshes;
    }

    // Each taken renderer's mesh with the model's objects for its bones, by the model's renderer it stands for (the build's
    // own mapping): only a mesh bound where the model binds it.
    private static Dictionary<Renderer, (Mesh mesh, Transform[] bones)> Replacements(Transform avatar, Transform model, List<ModelFileSmrPathData> bindings,
        IEnumerable<SkinnedMeshRenderer> renderers, string target)
    {
        var replacements = new Dictionary<Renderer, (Mesh, Transform[])>();
        foreach (var renderer in renderers)
        {
            string avatarPath = SmrPathService.GetRelativeTransformPath(avatar, renderer.transform);
            var binding = bindings.FirstOrDefault(b => b.avatarPath == avatarPath);
            var modelRenderer = binding != null ? NativeMeshPayloadService.ResolveCustomRenderer(model, binding) : null;
            if (modelRenderer == null || modelRenderer.sharedMesh == null)
                throw new InvalidOperationException($"{renderer.name} shows a mesh committed in Mixer, but the model '{Path.GetFileName(target)}' builds from has no mesh " +
                                                    "for it: choose the custom FBX that holds it in the model's entry, or revert the edit.");
            var bones = ModelBones(renderer, binding.fbxMeshPath, modelRenderer, model) ?? throw new InvalidOperationException(ArmatureMessage(renderer.name, target));
            replacements[modelRenderer] = (renderer.sharedMesh, bones);
        }
        return replacements;
    }

    /// <summary>
    /// The model's objects for the bones the mesh <paramref name="shown"/> shows binds to, in its order: by their path from the
    /// model's root (where <paramref name="fbxMeshPath"/>, the renderer's own path in the model, places that root on the
    /// avatar), else by a name only one object of the model has. Null when a bone that weights the mesh has none, or when the
    /// mesh binds it elsewhere than <paramref name="model"/>'s mesh does (else than the model's rest pose for a bone that mesh
    /// does not use): an edit of the armature (rest pose, added bones), not only of the mesh.
    /// </summary>
    internal static Transform[] ModelBones(SkinnedMeshRenderer shown, string fbxMeshPath, SkinnedMeshRenderer model, Transform modelRoot)
    {
        if (shown == null || model == null || modelRoot == null || shown.sharedMesh == null || model.sharedMesh == null) return null;
        var bones = shown.bones;
        var poses = shown.sharedMesh.bindposes;
        if (poses.Length != bones.Length) return null;
        var avatarModelRoot = shown.transform;
        int depth = string.IsNullOrEmpty(fbxMeshPath) ? 1 : fbxMeshPath.Split('/').Length;
        for (int i = 0; i < depth && avatarModelRoot != null; i++) avatarModelRoot = avatarModelRoot.parent;
        var byName = modelRoot.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        var weighted = Weighted(shown.sharedMesh, bones.Length);
        var modelBones = model.bones;
        var modelPoses = model.sharedMesh.bindposes;
        var mapped = new Transform[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            Transform found = null;
            if (bone != null && avatarModelRoot != null && bone != avatarModelRoot && bone.IsChildOf(avatarModelRoot))
                found = modelRoot.Find(SmrPathService.GetRelativeTransformPath(avatarModelRoot, bone));
            if (found == null && bone != null) byName.TryGetValue(bone.name, out found);
            // A bone that weights nothing does not matter where it is: the skin leaves out one the model lacks.
            if (found == null && !weighted[i]) continue;
            if (found == null) return null;
            mapped[i] = found;
            if (!weighted[i]) continue;
            int j = Array.IndexOf(modelBones, found);
            var expected = j >= 0 && j < modelPoses.Length ? modelPoses[j] : found.worldToLocalMatrix * model.transform.localToWorldMatrix;
            if (!Close(poses[i], expected)) return null;
        }
        return mapped;
    }

    // Which bones carry a weight of the mesh.
    private static bool[] Weighted(Mesh mesh, int boneCount)
    {
        var weighted = new bool[boneCount];
        var weights = mesh.GetAllBoneWeights();
        for (int i = 0; i < weights.Length; i++)
        {
            var weight = weights[i];
            if (weight.weight > 0f && weight.boneIndex >= 0 && weight.boneIndex < boneCount) weighted[weight.boneIndex] = true;
        }
        return weighted;
    }

    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (int k = 0; k < 16; k++)
            if (Mathf.Abs(a[k] - b[k]) > BindPoseTolerance * Mathf.Max(1f, Mathf.Max(Mathf.Abs(a[k]), Mathf.Abs(b[k])))) return false;
        return true;
    }

    private static string ArmatureMessage(string renderer, string target) =>
        $"{renderer}'s committed mesh binds to other bones or another rest pose than its mesh in the model '{Path.GetFileName(target)}' builds from (armature " +
        "edits in Mixer, or a mesh made for another version's armature): a version build takes mesh edits only. Put armature edits in a version through " +
        "Blender (Export for MCB, then import the export on the Blender card), or revert them.";

    // The FBX is written in metres, as Unity shows the model, and users import the FBX a version patches with the model's own
    // import settings: those must read it at that size.
    private static void RequireUnscaledImport(string target)
    {
        if (!(AssetImporter.GetAtPath(target) is ModelImporter importer)) return;
        if (Mathf.Approximately(importer.globalScale, 1f) && importer.useFileScale) return;
        throw new InvalidOperationException($"The import settings of '{Path.GetFileName(target)}' scale the model (Scale Factor {importer.globalScale}" +
                                            $"{(importer.useFileScale ? "" : ", Convert Units off")}): MCB writes the avatar's meshes in metres, which those settings " +
                                            "would import at another size. Put the Mixer edits in a version through Blender (Export for MCB, then import the export on " +
                                            "the Blender card), or revert them.");
    }

    // Every mesh of the model as FbxExport writes it (the taken ones in place of theirs) with the model's slot names. The
    // model's other objects (leaf bones, empties) ride along as bones past the bind poses of one mesh, which its skin leaves
    // out: the file keeps every object, as the prefabs and avatars made from the model reference them.
    private static List<FbxExportMesh> Items(Transform model, Dictionary<Renderer, (Mesh mesh, Transform[] bones)> replacements,
        Dictionary<string, Material> materials, string target, out FbxExportMesh carrier)
    {
        var items = new List<FbxExportMesh>();
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            var modelMesh = MeshOf(renderer);
            if (modelMesh == null) continue;
            bool replaced = replacements.TryGetValue(renderer, out var replacement);
            var item = FbxExportMesh.From(renderer, replaced ? replacement.mesh : null);
            if (replaced) item.Bones = replacement.bones;
            item.Materials = SlotMaterials(renderer, modelMesh, item.Mesh, materials);
            items.Add(item);
        }
        var written = new HashSet<Transform>();
        foreach (var item in items)
            foreach (var t in new[] { item.Transform }.Concat(item.Bones ?? Array.Empty<Transform>()))
                for (var p = t; p != null && p != model; p = p.parent) written.Add(p);
        var others = model.GetComponentsInChildren<Transform>(true).Where(t => t != model && !written.Contains(t)).ToArray();
        carrier = items.FirstOrDefault(i => i.Mesh != null && i.Bones != null && i.Bones.Length > 0);
        if (others.Length == 0) return items;
        if (carrier == null)
            throw new InvalidOperationException($"'{Path.GetFileName(target)}' has no skinned mesh to keep its other objects ({string.Join(", ", others.Take(5).Select(t => t.name))}) " +
                                                "in the FBX written from the avatar's meshes.");
        carrier.Bones = carrier.Bones.Concat(others).ToArray();
        return items;
    }

    // A material per submesh named as the model names that slot (an importer remap undone), so the file's slots keep their
    // names: users' material remaps and MCB's slot matching go by them. One material per name, shared by the meshes sharing
    // the slot. No textures: the file must not carry paths of the creator's computer.
    private static Material[] SlotMaterials(Renderer renderer, Mesh modelMesh, Mesh written, Dictionary<string, Material> made)
    {
        var declared = MaterialSlotNames.FromModel(modelMesh);
        var current = renderer.sharedMaterials ?? Array.Empty<Material>();
        var result = new Material[written != null ? written.subMeshCount : 0];
        for (int i = 0; i < result.Length; i++)
        {
            string name = declared != null && i < declared.Length && !string.IsNullOrEmpty(declared[i]) ? declared[i]
                : i < current.Length && current[i] != null ? current[i].name : null;
            if (string.IsNullOrEmpty(name)) continue;
            if (!made.TryGetValue(name, out var material))
            {
                material = new Material(Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader")) { name = name, hideFlags = HideFlags.HideAndDontSave };
                made[name] = material;
            }
            result[i] = material;
        }
        return result;
    }

    private static Mesh MeshOf(Renderer renderer)
    {
        if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
        return renderer != null && renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
    }
}
#endif
