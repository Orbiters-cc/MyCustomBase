#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class BlenderInstallService
{
    private const string BlenderExecutablePrefsKey = "MCB_BlenderExecutablePath";
    private const double CacheDurationSeconds = 30.0d;

    private static List<BlenderInstallation> cachedInstallations;
    private static double nextRefreshTime;

    public class BlenderInstallation
    {
        public string executablePath;
        public string version;
        public string source;

        public string DisplayLabel
        {
            get
            {
                string versionText = string.IsNullOrWhiteSpace(version) ? "Unknown version" : version;
                return versionText + " - " + executablePath;
            }
        }
    }

    public static List<BlenderInstallation> GetInstallations(bool forceRefresh = false)
    {
        double now = EditorApplication.timeSinceStartup;
        if (!forceRefresh && cachedInstallations != null && now < nextRefreshTime)
        {
            return new List<BlenderInstallation>(cachedInstallations);
        }

        nextRefreshTime = now + CacheDurationSeconds;
        cachedInstallations = ScanInstallations();
        return new List<BlenderInstallation>(cachedInstallations);
    }

    public static string GetSelectedExecutablePath()
    {
        string configured = NormalizeExecutablePath(EditorPrefs.GetString(BlenderExecutablePrefsKey, ""));
        if (IsExecutableFile(configured))
        {
            return configured;
        }

        var installations = GetInstallations();
        return installations.FirstOrDefault()?.executablePath ?? "";
    }

    public static void SetSelectedExecutablePath(string path)
    {
        string normalized = NormalizeExecutablePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            EditorPrefs.DeleteKey(BlenderExecutablePrefsKey);
        }
        else
        {
            EditorPrefs.SetString(BlenderExecutablePrefsKey, normalized);
        }
    }

    public static void ClearSelectedExecutablePath()
    {
        EditorPrefs.DeleteKey(BlenderExecutablePrefsKey);
    }

    public static string PickExecutable()
    {
        string extension = Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "";
        string selected = EditorUtility.OpenFilePanel("Select Blender executable", "", extension);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return "";
        }

        string normalized = NormalizeExecutablePath(selected);
        if (!IsExecutableFile(normalized))
        {
            EditorUtility.DisplayDialog("Invalid Blender Path", "The selected file does not exist.", "OK");
            return "";
        }

        SetSelectedExecutablePath(normalized);
        GetInstallations(forceRefresh: true);
        return normalized;
    }

    public static string NormalizeExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            fullPath = Path.Combine(fullPath, Application.platform == RuntimePlatform.WindowsEditor ? "blender.exe" : "blender");
        }

        return fullPath;
    }

    private static List<BlenderInstallation> ScanInstallations()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfExecutable(paths, EditorPrefs.GetString(BlenderExecutablePrefsKey, ""));
        AddIfExecutable(paths, FindExecutableOnPath(Application.platform == RuntimePlatform.WindowsEditor ? "blender.exe" : "blender"));

        foreach (string candidate in GetStandardCandidates())
        {
            AddIfExecutable(paths, candidate);
        }

        return paths
            .Select(path => new BlenderInstallation
            {
                executablePath = path,
                version = ReadBlenderVersion(path),
                source = ""
            })
            .OrderByDescending(item => ParseVersionSortKey(item.version))
            .ThenBy(item => item.executablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddIfExecutable(HashSet<string> paths, string path)
    {
        string normalized = NormalizeExecutablePath(path);
        if (IsExecutableFile(normalized))
        {
            paths.Add(normalized);
        }
    }

    private static bool IsExecutableFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string FindExecutableOnPath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return "";
        }

        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private static IEnumerable<string> GetStandardCandidates()
    {
        if (Application.platform == RuntimePlatform.WindowsEditor)
        {
            foreach (string candidate in GetWindowsCandidates())
            {
                yield return candidate;
            }
        }
        else if (Application.platform == RuntimePlatform.OSXEditor)
        {
            yield return "/Applications/Blender.app/Contents/MacOS/Blender";
        }
        else
        {
            yield return "/usr/bin/blender";
            yield return "/usr/local/bin/blender";
            yield return "/snap/bin/blender";
        }
    }

    private static IEnumerable<string> GetWindowsCandidates()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (string root in roots.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string foundation = Path.Combine(root, "Blender Foundation");
            foreach (string directory in SafeGetDirectories(foundation).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.Combine(directory, "blender.exe");
                foreach (string child in SafeGetDirectories(directory).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    yield return Path.Combine(child, "blender.exe");
                }
            }

            string steamCommon = Path.Combine(root, "Steam", "steamapps", "common");
            foreach (string blenderDirectory in SafeGetDirectories(steamCommon).Where(path => Path.GetFileName(path).IndexOf("Blender", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                yield return Path.Combine(blenderDirectory, "blender.exe");
            }
        }

        foreach (string steamRoot in GetSteamLibraryRoots())
        {
            string common = Path.Combine(steamRoot, "steamapps", "common");
            foreach (string blenderDirectory in SafeGetDirectories(common).Where(path => Path.GetFileName(path).IndexOf("Blender", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                yield return Path.Combine(blenderDirectory, "blender.exe");
            }
        }
    }

    private static IEnumerable<string> GetSteamLibraryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string baseRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                 })
        {
            if (string.IsNullOrWhiteSpace(baseRoot)) continue;
            string steamRoot = Path.Combine(baseRoot, "Steam");
            if (Directory.Exists(steamRoot))
            {
                roots.Add(steamRoot);
            }
        }

        foreach (string root in roots.ToList())
        {
            string libraryFoldersPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath)) continue;
            try
            {
                foreach (string line in File.ReadAllLines(libraryFoldersPath))
                {
                    int pathIndex = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                    if (pathIndex < 0) continue;
                    string[] parts = line.Split('"');
                    if (parts.Length < 4) continue;
                    string path = parts[3].Replace("\\\\", "\\");
                    if (Directory.Exists(path))
                    {
                        roots.Add(path);
                    }
                }
            }
            catch
            {
                // Steam library discovery is best-effort.
            }
        }

        return roots;
    }

    private static IEnumerable<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string ReadBlenderVersion(string executablePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    return "";
                }

                if (!process.WaitForExit(2500))
                {
                    try { process.Kill(); } catch { }
                    return VersionFromPath(executablePath);
                }

                string output = process.StandardOutput.ReadToEnd();
                string firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    return firstLine.Trim();
                }
            }
        }
        catch
        {
            // Fall through to folder-name version guessing.
        }

        return VersionFromPath(executablePath);
    }

    private static string VersionFromPath(string executablePath)
    {
        string directory = Path.GetDirectoryName(executablePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string name = Path.GetFileName(directory);
            if (!string.IsNullOrWhiteSpace(name) && name.IndexOf("Blender", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return name;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return "";
    }

    private static Version ParseVersionSortKey(string versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return new Version(0, 0);
        }

        string digits = new string(versionText.Where(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
        if (Version.TryParse(digits, out var version))
        {
            return version;
        }

        return new Version(0, 0);
    }
}
#endif
