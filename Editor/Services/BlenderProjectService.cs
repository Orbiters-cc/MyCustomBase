#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

public static class BlenderProjectService
{
    public static BlenderSyncService.BlenderProjectInfo CreateProjectInfo(MCBEditor editor)
    {
        Transform root = editor.customBaseTarget != null ? editor.customBaseTarget.transform.root : null;
        string avatarName = root != null ? root.name : editor.GetSelectedAssetDisplayName();
        string globalId = editor.customBaseTarget != null
            ? GlobalObjectId.GetGlobalObjectIdSlow(editor.customBaseTarget).ToString()
            : Guid.NewGuid().ToString("N");
        string projectId = SanitizeFileName(avatarName) + "_" + ShortHash(globalId);
        return Info(projectId, SanitizeFileName(avatarName) + ".blend");
    }

    /// <summary>
    /// A project's paths: its folder under blenderProjects, the exports Unity imports from it, and the .blend in a "blend~"
    /// folder Unity leaves alone (each save in Blender would make it import the .blend and its backup again, through
    /// Blender, about 45 seconds each).
    /// </summary>
    private static BlenderSyncService.BlenderProjectInfo Info(string projectId, string blendName)
    {
        string folder = MCBUtils.CombineUnityPath(MCBUtils.ASSETS_BASE_FOLDER, "blenderProjects", projectId);
        string projectUnityPath = MCBUtils.CombineUnityPath(folder, "blend~", blendName);
        string exportsUnityPath = MCBUtils.CombineUnityPath(folder, "exports");
        return new BlenderSyncService.BlenderProjectInfo
        {
            projectId = projectId,
            projectUnityPath = projectUnityPath,
            projectAbsolutePath = UnityPathToAbsolute(projectUnityPath),
            exportsUnityPath = exportsUnityPath,
            exportsAbsolutePath = UnityPathToAbsolute(exportsUnityPath)
        };
    }

    /// <summary>A project's folder (where its exports and MCB's files are), from its .blend.</summary>
    public static string ProjectFolder(string projectAbsolutePath) => Path.GetDirectoryName(Path.GetDirectoryName(projectAbsolutePath));

    /// <summary>
    /// The avatar's Blender project made from these meshes (<paramref name="fingerprint"/>, see BlenderSourceFbx.Fingerprint):
    /// the one bound to them (made from them, or whose export made the avatar show them), else a new one. A project made
    /// from other meshes (another version, or one from before projects were bound) is never opened for these: its .blend
    /// stays as it is, beside the new one.
    /// </summary>
    public static BlenderSyncService.BlenderProjectInfo ProjectFor(MCBEditor editor, string fingerprint)
    {
        var first = CreateProjectInfo(editor);
        BlenderSyncService.BlenderProjectInfo Numbered(int n) => n == 1 ? first : WithId(first, first.projectId + "_" + n);
        const int MaxProjects = 999;
        for (int n = 1; n <= MaxProjects; n++)
        {
            var info = Numbered(n);
            if (ReadBinding(info.projectAbsolutePath)?.fingerprints.Contains(fingerprint) == true) return info;
            if (!Directory.Exists(ProjectFolder(info.projectAbsolutePath))) break;
        }
        for (int n = 1; n <= MaxProjects; n++)
        {
            var info = Numbered(n);
            if (File.Exists(info.projectAbsolutePath) || ReadBinding(info.projectAbsolutePath) != null) continue;
            Bind(info.projectAbsolutePath, fingerprint);
            return info;
        }
        throw new InvalidOperationException("Too many Blender projects for " + first.projectId + ".");
    }

    /// <summary>The project also stands for these meshes from now on (an export of it made the avatar show them).</summary>
    public static void Bind(string projectAbsolutePath, string fingerprint)
    {
        if (string.IsNullOrEmpty(projectAbsolutePath) || string.IsNullOrEmpty(fingerprint)) return;
        var binding = ReadBinding(projectAbsolutePath) ?? new Binding();
        if (binding.fingerprints.Contains(fingerprint)) return;
        binding.fingerprints.Add(fingerprint);
        string path = BindingPath(projectAbsolutePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonConvert.SerializeObject(binding, Formatting.Indented));
    }

    /// <summary>
    /// MCB's own files of a project, out of Unity's import (a folder ending with "~"): the FBX files Blender started from
    /// and the binding.
    /// </summary>
    public static string SourceFolder(string projectAbsolutePath) => Path.Combine(ProjectFolder(projectAbsolutePath), "source~");

    private sealed class Binding
    {
        [JsonProperty] public List<string> fingerprints = new List<string>();
    }

    private static string BindingPath(string projectAbsolutePath) => Path.Combine(SourceFolder(projectAbsolutePath), "binding.json");

    private static Binding ReadBinding(string projectAbsolutePath)
    {
        string path = BindingPath(projectAbsolutePath);
        if (!File.Exists(path)) return null;
        try { return JsonConvert.DeserializeObject<Binding>(File.ReadAllText(path)) ?? new Binding(); }
        catch (JsonException) { return new Binding(); }
    }

    private static BlenderSyncService.BlenderProjectInfo WithId(BlenderSyncService.BlenderProjectInfo first, string projectId) =>
        Info(projectId, Path.GetFileName(first.projectUnityPath));

    public static void EnsureProjectFolders(BlenderSyncService.BlenderProjectInfo projectInfo)
    {
        if (projectInfo == null)
        {
            return;
        }

        string projectDirectory = Path.GetDirectoryName(projectInfo.projectAbsolutePath);
        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            Directory.CreateDirectory(projectDirectory);
        }

        Directory.CreateDirectory(projectInfo.exportsAbsolutePath);
    }

    public static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "mcb";
        string cleaned = value;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(c, '_');
        }
        cleaned = cleaned.Replace("/", "_").Replace("\\", "_").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "mcb" : cleaned;
    }

    private static string ShortHash(string value)
    {
        using (var sha1 = SHA1.Create())
        {
            byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            var builder = new StringBuilder();
            for (int i = 0; i < 6 && i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }
            return builder.ToString();
        }
    }

    private static string UnityPathToAbsolute(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath)) return unityPath;
        if (Path.IsPathRooted(unityPath)) return Path.GetFullPath(unityPath);
        return Path.GetFullPath(Path.Combine(GetProjectRoot(), unityPath));
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }
}
#endif
