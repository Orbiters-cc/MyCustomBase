#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using UnityEditorInternal;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

public enum ApiSimulationMode
{
    Off = 0,
    TransportFailure = 1,
    SslFailure = 2
}

public static class MCBUtils
{
    public const string SCRIPT_VERSION = "0.1";
    public const string PACKAGE_BASE_FOLDER = "Packages/orbiters.mcb";
    public const string ASSETS_BASE_FOLDER = "Assets/MCB";
    public const string ASSET_VERSIONS_FOLDER = ASSETS_BASE_FOLDER + "/assets";
    public const string UNSUBMITTED_VERSIONS_FILE = ASSETS_BASE_FOLDER + "/unsubmittedVersions.json";
    public const string USER_VERSIONS_DIR = ASSETS_BASE_FOLDER + "/userVersions";
    public const string USER_VERSIONS_FILE = ASSETS_BASE_FOLDER + "/userVersions.json";
    public const string DEFAULT_AVATAR_NAME = "default avatar.asset";
    public const string CUSTOM_BASE_AVATAR_NAME = "customBase avatar.asset";

    // EditorPrefs key for Dev Environment setting
    private const string DevEnvironmentPrefKey = "MCB_DevEnvironment";
    private const string ApiSimulationModePrefKey = "MCB_ApiSimulationMode";
    
    // Dev Environment property with persistent storage
    public static bool isDevEnvironment
    {
        get
        {
            try { return EditorPrefs.GetBool(DevEnvironmentPrefKey, false); }
            catch { return false; }
        }
        set
        {
            try { EditorPrefs.SetBool(DevEnvironmentPrefKey, value); } catch { }
        }
    }

    public static ApiSimulationMode apiSimulationMode
    {
        get
        {
            try
            {
                int value = EditorPrefs.GetInt(ApiSimulationModePrefKey, (int)ApiSimulationMode.Off);
                return Enum.IsDefined(typeof(ApiSimulationMode), value)
                    ? (ApiSimulationMode)value
                    : ApiSimulationMode.Off;
            }
            catch
            {
                return ApiSimulationMode.Off;
            }
        }
        set
        {
            try { EditorPrefs.SetInt(ApiSimulationModePrefKey, (int)value); } catch { }
        }
    }
    
    public const string SERVER_BASE_URL = "orbiters.cc/"; // Update with your server URL
    public const string API_BASE_URL = "api." + SERVER_BASE_URL; // Update with your server URL
    public const string VERSION_ENDPOINT = "/:assetId/versions";
    public const string MODEL_ENDPOINT = "/:assetId/model";
    public const string AVATAR_ASSET_DISCOVERY_ENDPOINT = "/assets/by-avatar-base";
    public const string TOKEN_ENDPOINT = "/token"; // Replace with your actual API endpoint
    
    public const string NEW_VERSION_ENDPOINT = "/newVersion";
    public const string CHECK_CONNECTION_ENDPOINT = "/check-connection";
    public static readonly string PACKAGE_BASE_FOLDER_FULL_PATH = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages", "orbiters.mcb");

    public static HttpClient client = new HttpClient() { Timeout = System.TimeSpan.FromSeconds(30) };
    
    public static string getApiUrl(string scope = "mcb")
    {
        switch (apiSimulationMode)
        {
            case ApiSimulationMode.TransportFailure:
                return "https://127.0.0.1:1/" + scope;
            case ApiSimulationMode.SslFailure:
                return "https://wrong.host.badssl.com/" + scope;
        }

        if (isDevEnvironment)
        {
            return "http://localhost:4100/" + scope;
        }
        return "https://" + API_BASE_URL + scope;
    }

    public static string getWebsiteUrl()
    {
        if (isDevEnvironment)
        {
            return "https://dev." + SERVER_BASE_URL;
        }
        return "https://" + SERVER_BASE_URL;
    }

    public static string GetAssetVersionEndpoint(int assetId)
    {
        return VERSION_ENDPOINT.Replace(":assetId", assetId.ToString());
    }

    public static string GetAssetModelEndpoint(int assetId)
    {
        return MODEL_ENDPOINT.Replace(":assetId", assetId.ToString());
    }

    // Calculates SHA256 hash of a file
    public static string CalculateFileHash(string filePath)
    {
        if (!File.Exists(filePath))
        {
            MCBLogger.LogError($"[MCBUtils] File not found for hashing: {filePath}");
            return null;
        }

        using (var sha256 = SHA256.Create())
        {
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hashBytes = sha256.ComputeHash(stream);
                StringBuilder builder = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }

    // Validates the token with the server

    // Removes the authentication data file

    public static string GetVersionDataPath(int assetId, string customBaseVersion, string defaultFbxVersion)
    {
        if (assetId <= 0 || string.IsNullOrEmpty(customBaseVersion) || string.IsNullOrEmpty(defaultFbxVersion))
        {
            return null;
        }

        return $"{ASSET_VERSIONS_FOLDER}/{assetId}/versions/u{customBaseVersion}d{defaultFbxVersion}";
    }

    public static string GetVersionDataPath(CustomBaseVersion version)
    {
        if (version == null) return null;
        return GetVersionDataPath(version.assetId, version.version, version.defaultAviVersion);
    }

    public static string GetVersionBinPath(CustomBaseVersion version)
    {
        string dataPath = GetVersionDataPath(version);
        return FindSingleVersionFile(dataPath, "*.bin");
    }

    public static bool IsVersionDownloaded(CustomBaseVersion version)
    {
        string dataPath = GetVersionDataPath(version);
        if (string.IsNullOrEmpty(dataPath)) return false;

        string absoluteDataPath = Path.GetFullPath(dataPath);
        return Directory.Exists(absoluteDataPath) &&
               Directory.GetFiles(absoluteDataPath, "*", SearchOption.TopDirectoryOnly).Length > 0;
    }

    public static string GetVersionAvatarPath(CustomBaseVersion version, string relativeAvatarPath)
    {
        if (string.IsNullOrEmpty(relativeAvatarPath)) return null;
        string dataPath = GetVersionDataPath(version);
        if (dataPath == null) return null;
        return Path.Combine(dataPath, relativeAvatarPath).Replace("\\", "/");
    }

    public static string GetDefaultAvatarPath(CustomBaseVersion version)
    {
        string dataPath = GetVersionDataPath(version);
        if (dataPath == null) return null;

        string defaultAvatarPath = Path.Combine(dataPath, DEFAULT_AVATAR_NAME).Replace("\\", "/");
        return File.Exists(Path.GetFullPath(defaultAvatarPath)) ? defaultAvatarPath : null;
    }

    public static string GetCustomBaseAvatarPath(CustomBaseVersion version)
    {
        string dataPath = GetVersionDataPath(version);
        if (dataPath == null) return null;

        string absoluteDataPath = Path.GetFullPath(dataPath);
        if (!Directory.Exists(absoluteDataPath)) return null;

        string defaultAvatarName = Path.GetFileName(DEFAULT_AVATAR_NAME);
        return Directory.GetFiles(absoluteDataPath, "*avatar.asset", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), defaultAvatarName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(ToUnityPath)
            .FirstOrDefault();
    }

    public static string GetLogicPackagePath(CustomBaseVersion version)
    {
        string dataPath = GetVersionDataPath(version);
        return FindSingleVersionFile(dataPath, "*.unitypackage");
    }

    public static string GetLogicPrefabPath(string versionDataFolderUnity)
    {
        return FindSingleVersionFile(versionDataFolderUnity, "*.prefab");
    }

    private static string FindSingleVersionFile(string versionDataFolderUnity, string searchPattern)
    {
        if (string.IsNullOrEmpty(versionDataFolderUnity)) return null;

        string absoluteDataPath = Path.GetFullPath(versionDataFolderUnity);
        if (!Directory.Exists(absoluteDataPath)) return null;

        string match = Directory.GetFiles(absoluteDataPath, searchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return string.IsNullOrEmpty(match) ? null : ToUnityPath(match);
    }

    public static string GetMCBDataFolder()
    {
        // Get the Unity Editor preferences folder and create MCB subfolder
        string dataFolder = Path.Combine(InternalEditorUtility.unityPreferencesFolder, "MCB", "Data");
        
        // Ensure the directory exists
        if (!Directory.Exists(dataFolder))
        {
            try
            {
                Directory.CreateDirectory(dataFolder);
                MCBLogger.Log($"[MCBUtils] Created MCB data folder: {dataFolder}");
            }
            catch (System.Exception ex)
            {
                MCBLogger.LogError($"[MCBUtils] Failed to create MCB data folder: {ex.Message}");
                // Fallback to temp directory
                dataFolder = Path.Combine(Path.GetTempPath(), "MCB", "Data");
                Directory.CreateDirectory(dataFolder);
            }
        }
        
        return dataFolder;
    }


    // Ensures the directory exists
    public static void EnsureDirectoryExists(string directoryPath, bool canBeFilePath = true)
    {
        // Check if the path is actually a directory path, not a file path
        string directory = directoryPath;

        if (canBeFilePath && !string.IsNullOrEmpty(Path.GetExtension(directoryPath)))
        {
            // If it has an extension and canBeFilePath is true, treat it as a file path
            directory = Path.GetDirectoryName(directoryPath);
        }
        // If canBeFilePath is false, treat the entire path as a directory path regardless of extension

        if (!string.IsNullOrEmpty(directory))
        {
            // Convert Unity relative path to absolute system path
            string absoluteDirectory;
            if (directory.StartsWith("Assets/") || directory.StartsWith("Assets\\"))
            {
                // Convert Unity Assets path to absolute path
                // Remove "Assets/" and combine with Application.dataPath
                string relativePath = directory.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar);
                absoluteDirectory = Path.Combine(Application.dataPath, relativePath);
            }
            else if (directory.StartsWith("Packages/") || directory.StartsWith("Packages\\"))
            {
                // Handle Packages path - go one level up from dataPath then into Packages
                string relativePath = directory.Substring("Packages/".Length).Replace('/', Path.DirectorySeparatorChar);
                absoluteDirectory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages", relativePath);
            }
            else if (Path.IsPathRooted(directory))
            {
                // Already absolute path
                absoluteDirectory = directory;
            }
            else
            {
                // Relative path, make it relative to Application.dataPath
                absoluteDirectory = Path.Combine(Application.dataPath, directory);
            }

            // Normalize the path
            absoluteDirectory = Path.GetFullPath(absoluteDirectory);

            MCBLogger.Log(
                $"[MCBUtils] EnsureDirectoryExists - Input: '{directoryPath}' (canBeFilePath: {canBeFilePath}) -> Directory: '{directory}' -> Absolute: '{absoluteDirectory}'");
            MCBLogger.Log($"[MCBUtils] Directory exists check: {Directory.Exists(absoluteDirectory)}");

            if (!Directory.Exists(absoluteDirectory))
        {
            try
            {
                    MCBLogger.Log($"[MCBUtils] Creating directory: {absoluteDirectory}");
                    Directory.CreateDirectory(absoluteDirectory);

                    // Verify creation
                    if (Directory.Exists(absoluteDirectory))
                    {
                        MCBLogger.Log($"[MCBUtils] Successfully created directory: {absoluteDirectory}");
                        AssetDatabase.Refresh(); // Make Unity aware of the new folder
                    }
                    else
                    {
                        MCBLogger.LogError(
                            $"[MCBUtils] Directory creation appeared to succeed but directory still doesn't exist: {absoluteDirectory}");
                    }
            }
            catch (System.Exception e)
            {
                    MCBLogger.LogError($"[MCBUtils] Failed to create directory '{absoluteDirectory}': {e.Message}");
                    MCBLogger.LogError($"[MCBUtils] Exception details: {e}");
                    throw; // Re-throw to let caller handle if needed
                }
            }
            else
            {
                MCBLogger.Log($"[MCBUtils] Directory already exists: {absoluteDirectory}");
            }
        }
        else
        {
            MCBLogger.LogWarning(
                $"[MCBUtils] EnsureDirectoryExists called with empty or invalid directory path: '{directoryPath}' (canBeFilePath: {canBeFilePath})");
        }
    }
    public static string ToUnityPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Replace("\\", "/");
    }

    public static string CombineUnityPath(params string[] segments)
    {
        if (segments == null || segments.Length == 0) return string.Empty;

        string result = null;
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)) continue;

            if (string.IsNullOrEmpty(result))
            {
                result = segment.TrimEnd('/', '\\');
            }
            else
            {
                result = $"{result.TrimEnd('/', '\\')}/{segment.TrimStart('/', '\\')}";
            }
        }

        return ToUnityPath(result);
    }
}
#endif
