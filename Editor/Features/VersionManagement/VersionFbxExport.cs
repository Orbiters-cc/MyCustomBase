using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Export FBX on a version's details: the version's model as an FBX file the user picks, without applying it. A version
/// that replaces the model (XOR or HDiff) gives the very file its creator published, decoded from the original model and
/// checked against its hash. A version of advanced meshes ships meshes, not a file: the avatar's meshes from its models
/// are written as the version makes them (its meshes on their renderers, bound to the bones the version names), through
/// Toolkit's FBX export. Nothing in the scene or the project changes.
/// </summary>
internal static class VersionFbxExport
{
    private const string Title = "Export FBX";

    internal static void Export(VersionActions actions, MCBEditor editor, CustomBaseVersion version)
    {
        // On the next update: the press paints first, and the save panel never opens inside a UI event.
        EditorApplication.CallbackFunction once = null;
        once = () =>
        {
            EditorApplication.update -= once;
            try
            {
                Run(actions, editor, version);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                EditorUtility.DisplayDialog(Title, $"Version {version?.version} could not be exported: {ex.Message}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        };
        EditorApplication.update += once;
    }

    private static void Run(VersionActions actions, MCBEditor editor, CustomBaseVersion version)
    {
        var patches = actions.ResolveModelPatches(version);
        if (patches.Count == 0) throw new InvalidOperationException("this version changes no mesh, so it has no model of its own.");
        var replacements = patches.Where(p => !p.AdvancedMesh).ToList();
        var advanced = patches.Where(p => p.AdvancedMesh).ToList();
        string versionName = MeshAssets.Clean(version.version ?? "version");

        if (replacements.Count > 0)
        {
            // One file per model the version replaces, under the model's own name.
            string folder = null, single = null;
            if (replacements.Count == 1 && advanced.Count == 0)
            {
                single = FbxExportDialog.AskPath(MeshAssets.Clean(Path.GetFileNameWithoutExtension(replacements[0].TargetFbxPath)) + " " + versionName + ".fbx");
                if (single == null) return;
            }
            else
            {
                folder = EditorUtility.SaveFolderPanel(Title + ": a folder for version " + version.version + "'s models", "", "");
                if (string.IsNullOrEmpty(folder)) return;
            }
            string last = null;
            for (int i = 0; i < replacements.Count; i++)
            {
                var patch = replacements[i];
                EditorUtility.DisplayProgressBar(Title, "Unpacking " + Path.GetFileName(patch.TargetFbxPath) + "…", (i + 0.5f) / replacements.Count);
                byte[] bytes = Decode(patch);
                last = single ?? Path.Combine(folder, MeshAssets.Clean(Path.GetFileNameWithoutExtension(patch.TargetFbxPath)) + " " + versionName + ".fbx");
                File.WriteAllBytes(last, bytes);
                MCBLogger.Log($"[MCB] Exported version {version.version}'s model {Path.GetFileName(patch.TargetFbxPath)} to {last} ({EditorUtility.FormatBytes(bytes.LongLength)}).");
            }
            if (advanced.Count == 0)
            {
                FbxExportDialog.Show(new FbxExportResult { Path = last, Bytes = new FileInfo(last).Length });
                return;
            }
            FbxExportDialog.Show(WriteAdvanced(AvatarRoot(editor), version, advanced, Path.Combine(folder, MeshAssets.Clean(AvatarName(editor)) + " " + versionName + ".fbx")));
            return;
        }

        string path = FbxExportDialog.AskPath(MeshAssets.Clean(AvatarName(editor)) + " " + versionName + ".fbx");
        if (path == null) return;
        FbxExportDialog.Show(WriteAdvanced(AvatarRoot(editor), version, advanced, path));
    }

    private static string AvatarName(MCBEditor editor) =>
        editor?.customBaseTarget != null ? AvatarPaths.Root(editor.customBaseTarget).name : "Avatar";

    private static Transform AvatarRoot(MCBEditor editor) =>
        editor?.customBaseTarget != null ? AvatarPaths.Root(editor.customBaseTarget) : throw new InvalidOperationException("the avatar is no longer in the scene.");

    // The model the creator published for this patch: decoded from the original model, as an apply does.
    internal static byte[] Decode(VersionActions.VersionModelPatch patch)
    {
        if (string.IsNullOrWhiteSpace(patch.OriginalFbxPath) || !File.Exists(patch.OriginalFbxPath))
            throw new FileNotFoundException($"the original model of '{Path.GetFileName(patch.TargetFbxPath)}' is missing (it is what the version is decoded from).");
        if (string.IsNullOrWhiteSpace(patch.BinPath) || !File.Exists(patch.BinPath))
            throw new FileNotFoundException("this version's files are not downloaded: download it, then export again.");
        byte[] bytes;
        if (patch.Hdiff)
        {
            string output = HdiffService.CreateTempWorkPath(".fbx");
            try
            {
                HdiffService.ApplyXorEncryptedPatchToTempFbx(patch.OriginalFbxPath, patch.BinPath, output, new FileManagerService());
                bytes = File.ReadAllBytes(output);
            }
            finally
            {
                try { if (File.Exists(output)) File.Delete(output); }
                catch (IOException) { }
            }
        }
        else
        {
            bytes = MCBXor.Transform(File.ReadAllBytes(patch.OriginalFbxPath), File.ReadAllBytes(patch.BinPath));
        }
        string expected = patch.Patch?.outputHash;
        if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(Sha256(bytes), expected.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{Path.GetFileName(patch.TargetFbxPath)}' did not decode to the file the creator published: the avatar's original model is not the one this version was made for (reset the avatar to its original model), or the download is damaged (download it again).");
        return bytes;
    }

    private static string Sha256(byte[] bytes)
    {
        using (var sha = MCBHashing.CreateSha256())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    // The avatar's meshes from the version's models, as the version makes them: its meshes on the renderers it names (read
    // from the payload cached by an apply, else decoded here), the others as the avatar shows them. What was written, or why not.
    internal static FbxExportResult WriteAdvanced(Transform root, CustomBaseVersion version, List<VersionActions.VersionModelPatch> patches, string path)
    {
        var made = new List<Mesh>();
        try
        {
            var shipped = new Dictionary<string, (Mesh mesh, List<string> bones)>(StringComparer.Ordinal);
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < patches.Count; i++)
            {
                var patch = patches[i];
                if (!string.IsNullOrWhiteSpace(patch.TargetFbxPath)) models.Add(MCBUtils.ToUnityPath(patch.TargetFbxPath));
                if (!string.IsNullOrWhiteSpace(patch.OriginalFbxPath)) models.Add(MCBUtils.ToUnityPath(patch.OriginalFbxPath));
                EditorUtility.DisplayProgressBar(Title, "Reading version " + version.version + "'s meshes…", (i + 0.5f) / patches.Count * 0.6f);
                var cached = NativeMeshPayloadService.FindCachedPayload(version, patch.Patch);
                if (cached != null)
                {
                    foreach (var record in cached.renderers.Where(r => r != null && r.mesh != null && !string.IsNullOrEmpty(r.avatarPath)))
                        shipped[record.avatarPath] = (record.mesh, record.bonePaths);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(patch.BinPath) || !File.Exists(patch.BinPath))
                    throw new FileNotFoundException("this version's files are not downloaded: download it, then export again.");
                var data = NativeMeshPayloadService.ReadPayloadForPreview(patch.Patch, patch.BinPath, patch.OriginalFbxPath);
                foreach (var record in data.renderers.Where(r => r?.mesh != null && !string.IsNullOrEmpty(r.avatarPath)))
                {
                    var mesh = NativeMeshPayloadService.CreateMeshFromPreparedData(record.mesh);
                    made.Add(mesh);
                    shipped[record.avatarPath] = (mesh, record.bonePaths);
                }
            }

            var items = new List<FbxExportMesh>();
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string avatarPath = SmrPathService.GetRelativeTransformPath(root, renderer.transform);
                if (shipped.TryGetValue(avatarPath, out var ships))
                {
                    var item = FbxExportMesh.From(renderer, ships.mesh);
                    if (ships.bones != null && ships.bones.Count > 0)
                    {
                        var bones = new Transform[ships.bones.Count];
                        for (int b = 0; b < bones.Length; b++)
                        {
                            bones[b] = string.IsNullOrEmpty(ships.bones[b]) ? null : root.Find(ships.bones[b]);
                            if (bones[b] == null && !string.IsNullOrEmpty(ships.bones[b]))
                                throw new InvalidOperationException($"version {version.version} binds {renderer.name} to '{ships.bones[b]}', which the avatar does not have yet: install the version, then export it.");
                        }
                        item.Bones = bones;
                    }
                    items.Add(item);
                    continue;
                }
                // The rest of the version's models, as the avatar shows them (clothing and props from elsewhere stay out).
                string meshPath = renderer.sharedMesh != null ? AssetDatabase.GetAssetPath(renderer.sharedMesh) : null;
                if (!string.IsNullOrEmpty(meshPath) && models.Contains(MCBUtils.ToUnityPath(meshPath))) items.Add(FbxExportMesh.From(renderer));
            }
            if (items.Count == 0) throw new InvalidOperationException("none of the avatar's renderers shows this version's meshes or its models.");

            EditorUtility.DisplayProgressBar(Title, "Writing " + Path.GetFileName(path) + "…", 0.8f);
            var result = FbxExport.Write(path, items, root);
            EditorUtility.ClearProgressBar();
            if (result.Success)
                MCBLogger.Log($"[MCB] Exported version {version.version}'s meshes to {result.Path} ({result.Meshes} meshes, {result.Bones} objects, {result.BlendShapes} blendshapes, {EditorUtility.FormatBytes(result.Bytes)}).");
            return result;
        }
        finally
        {
            foreach (var mesh in made) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
