#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        string projectDirectoryUnityPath = MCBUtils.CombineUnityPath(MCBUtils.ASSETS_BASE_FOLDER, "blenderProjects", projectId);
        string projectUnityPath = MCBUtils.CombineUnityPath(projectDirectoryUnityPath, SanitizeFileName(avatarName) + ".blend");
        string exportsUnityPath = MCBUtils.CombineUnityPath(projectDirectoryUnityPath, "exports");

        return new BlenderSyncService.BlenderProjectInfo
        {
            projectId = projectId,
            projectUnityPath = projectUnityPath,
            projectAbsolutePath = UnityPathToAbsolute(projectUnityPath),
            exportsUnityPath = exportsUnityPath,
            exportsAbsolutePath = UnityPathToAbsolute(exportsUnityPath)
        };
    }

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
