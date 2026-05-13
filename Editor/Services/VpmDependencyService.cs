#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using VRC.PackageManagement.Core;
using VRC.PackageManagement.Core.Types;
using VRC.PackageManagement.Core.Types.Packages;
using VRC.PackageManagement.Core.Types.Providers;

public class VpmDependencyStatus
{
    public string Id;
    public string DisplayName;
    public string VersionRange;
    public string RepositoryUrl;
    public string Reason;
    public bool IsRequired;
    public bool IsInstalled;
    public string InstalledVersion;
    public bool IsRepositoryConfigured;
    public bool IsPackageAvailable;
    public string Error;
}

public class VpmDependencyInstallResult
{
    public bool Success;
    public List<string> InstalledPackageIds = new List<string>();
    public List<string> AddedRepositoryUrls = new List<string>();
    public List<string> Errors = new List<string>();

    public string ErrorMessage
    {
        get { return Errors == null || Errors.Count == 0 ? null : string.Join("\n", Errors); }
    }
}

public class VpmDependencyService
{
    private const string PackageManifestPath = "Packages/orbiters.mcb/package.json";
    private static readonly TimeSpan DependencyStatusCacheDuration = TimeSpan.FromSeconds(60);

    private static VpmDependencyService instance;
    private McbPackageManifest packageManifest;
    private DateTime manifestLoadedAtUtc;
    private readonly Dictionary<string, CachedDependencyStatus> dependencyStatusCache =
        new Dictionary<string, CachedDependencyStatus>(StringComparer.OrdinalIgnoreCase);

    public static VpmDependencyService Instance
    {
        get { return instance ?? (instance = new VpmDependencyService()); }
    }

    public bool IsInstalling { get; private set; }
    public event Action StatusChanged;

    public List<VpmDependencyStatus> GetRequiredDependencyStatuses()
    {
        var manifest = LoadPackageManifest();
        var dependencies = manifest.vpmDependencies ?? new Dictionary<string, string>();
        return dependencies
            .Select(pair => BuildStatus(pair.Key, pair.Value, true, null))
            .ToList();
    }

    public List<VpmDependencyStatus> GetMissingRequiredDependencyStatuses()
    {
        return GetRequiredDependencyStatuses()
            .Where(status => status != null && !status.IsInstalled)
            .ToList();
    }

    public bool HasMissingRequiredDependencies()
    {
        return GetMissingRequiredDependencyStatuses().Count > 0;
    }

    public VpmDependencyStatus GetOptionalDependencyStatus(string packageId)
    {
        return GetOptionalDependencyStatus(packageId, false);
    }

    private VpmDependencyStatus GetOptionalDependencyStatus(string packageId, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var manifest = LoadPackageManifest();
        if (manifest.mcb == null || manifest.mcb.optionalVpmDependencies == null ||
            !manifest.mcb.optionalVpmDependencies.TryGetValue(packageId, out var optional))
        {
            return null;
        }

        string version = optional != null ? optional.version : null;
        return GetCachedStatus(
            "optional:" + packageId + ":" + (version ?? string.Empty),
            forceRefresh,
            () => BuildStatus(packageId, version, false, optional));
    }

    public VpmDependencyInstallResult InstallMissingRequiredDependencies()
    {
        return InstallDependencies(GetMissingRequiredDependencyStatuses());
    }

    public VpmDependencyInstallResult InstallOptionalDependency(string packageId)
    {
        var status = GetOptionalDependencyStatus(packageId, true);
        if (status == null)
        {
            return new VpmDependencyInstallResult
            {
                Success = false,
                Errors = new List<string> { "Optional dependency metadata was not found for " + packageId + "." }
            };
        }

        if (status.IsInstalled)
        {
            return new VpmDependencyInstallResult { Success = true };
        }

        return InstallDependencies(new[] { status });
    }

    public VpmDependencyInstallResult InstallDependencies(IEnumerable<VpmDependencyStatus> dependencyStatuses)
    {
        var result = new VpmDependencyInstallResult();
        var dependencies = (dependencyStatuses ?? Enumerable.Empty<VpmDependencyStatus>())
            .Where(status => status != null && !status.IsInstalled)
            .GroupBy(status => status.Id)
            .Select(group => group.First())
            .ToList();

        if (dependencies.Count == 0)
        {
            result.Success = true;
            return result;
        }

        if (IsInstalling)
        {
            result.Errors.Add("A dependency install is already running.");
            return result;
        }

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            result.Errors.Add("Unity is compiling or importing assets. Wait for it to finish, then try again.");
            return result;
        }

        IsInstalling = true;
        NotifyStatusChanged();

        try
        {
            AddMissingRepositories(dependencies, result);
            RefreshProviders();

            string projectPath = GetProjectPath();
            var project = new UnityProject(projectPath);

            for (int i = 0; i < dependencies.Count; i++)
            {
                var dependency = dependencies[i];
                float progress = Mathf.Lerp(0.35f, 0.8f, dependencies.Count == 1 ? 1f : i / (float)(dependencies.Count - 1));
                EditorUtility.DisplayProgressBar("MCB Dependencies", "Installing " + dependency.DisplayName + "...", progress);

                string versionRange = string.IsNullOrWhiteSpace(dependency.VersionRange) ? string.Empty : dependency.VersionRange;
                var package = GetAvailablePackage(dependency.Id, versionRange);
                if (package == null)
                {
                    result.Errors.Add("Could not find " + dependency.DisplayName + " in the configured VPM repositories.");
                    continue;
                }

                if (!project.AddVPMPackage(dependency.Id, versionRange, Repos.GetAll))
                {
                    result.Errors.Add("VPM could not install " + dependency.DisplayName + ".");
                    continue;
                }

                result.InstalledPackageIds.Add(dependency.Id);
            }

            if (result.Errors.Count == 0)
            {
                EditorUtility.DisplayProgressBar("MCB Dependencies", "Resolving project packages...", 0.9f);
                if (!VPMProjectManifest.Resolve(projectPath, Repos.GetAll))
                {
                    result.Errors.Add("VPM resolver could not finish resolving the project packages.");
                }
            }

            if (result.Errors.Count == 0)
            {
                UnityEditor.PackageManager.Client.Resolve();
                AssetDatabase.Refresh();
            }

            result.Success = result.Errors.Count == 0;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add(ex.Message);
            MCBLogger.LogError("[MCB] Dependency installation failed: " + ex);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            IsInstalling = false;
            ClearDependencyStatusCache();
            NotifyStatusChanged();
        }

        return result;
    }

    public static string BuildInstallButtonLabel(IReadOnlyList<VpmDependencyStatus> dependencies)
    {
        if (dependencies == null || dependencies.Count == 0)
        {
            return "Install dependencies";
        }

        var names = dependencies
            .Where(status => status != null)
            .Select(status => status.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            return "Install dependencies";
        }

        return "Install " + JoinHumanNames(names);
    }

    private static string JoinHumanNames(IReadOnlyList<string> names)
    {
        if (names.Count == 1)
        {
            return names[0];
        }

        if (names.Count == 2)
        {
            return names[0] + " and " + names[1];
        }

        return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
    }

    private void AddMissingRepositories(IEnumerable<VpmDependencyStatus> dependencies, VpmDependencyInstallResult result)
    {
        var urls = dependencies
            .Select(status => status.RepositoryUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < urls.Count; i++)
        {
            string url = urls[i];
            EditorUtility.DisplayProgressBar("MCB Dependencies", "Adding VPM repository...", 0.1f + 0.2f * ((i + 1f) / urls.Count));

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                result.Errors.Add("Invalid VPM repository URL: " + url);
                continue;
            }

            if (Repos.UserRepoExists(uri))
            {
                continue;
            }

            if (!Repos.AddRepo(uri, new Dictionary<string, string>()))
            {
                result.Errors.Add("VPM could not add repository: " + url);
                continue;
            }

            result.AddedRepositoryUrls.Add(url);
        }
    }

    private VpmDependencyStatus BuildStatus(string packageId, string versionRange, bool isRequired, McbOptionalVpmDependency optional)
    {
        var status = new VpmDependencyStatus
        {
            Id = packageId,
            VersionRange = versionRange,
            RepositoryUrl = FindRepositoryUrl(packageId),
            IsRequired = isRequired,
            Reason = optional != null ? optional.reason : null
        };

        status.DisplayName = ResolveDisplayName(packageId, optional);

        try
        {
            var project = new UnityProject(GetProjectPath());
            var installedPackage = GetInstalledPackage(project, packageId, null);
            var matchingInstalledPackage = GetInstalledPackage(project, packageId, versionRange);

            status.InstalledVersion = installedPackage != null ? installedPackage.Version : null;
            status.IsInstalled = matchingInstalledPackage != null;

            if (installedPackage != null && matchingInstalledPackage == null && !string.IsNullOrWhiteSpace(versionRange))
            {
                status.Error = "Installed version " + installedPackage.Version + " does not satisfy " + versionRange + ".";
            }
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(status.RepositoryUrl) &&
                Uri.TryCreate(status.RepositoryUrl, UriKind.Absolute, out var uri))
            {
                status.IsRepositoryConfigured = Repos.UserRepoExists(uri);
            }

            string range = string.IsNullOrWhiteSpace(versionRange) ? string.Empty : versionRange;
            var availablePackage = GetAvailablePackage(packageId, range);
            status.IsPackageAvailable = availablePackage != null;
            if (availablePackage != null && string.IsNullOrWhiteSpace(status.DisplayName))
            {
                status.DisplayName = availablePackage.Title;
            }
        }
        catch
        {
            status.IsPackageAvailable = false;
        }

        if (string.IsNullOrWhiteSpace(status.DisplayName))
        {
            status.DisplayName = HumanizePackageId(packageId);
        }

        return status;
    }

    private static IVRCPackage GetInstalledPackage(UnityProject project, string packageId, string versionRange)
    {
        if (project == null || project.VPMProvider == null || string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        project.VPMProvider.Refresh();

        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return project.VPMProvider.GetPackage(packageId);
        }

        return project.VPMProvider.GetPackageWithRange(packageId, versionRange);
    }

    private static IVRCPackage GetAvailablePackage(string packageId, string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return Repos.GetLatestPackage(packageId);
        }

        return Repos.GetPackageWithVersionMatch(packageId, versionRange);
    }

    private string ResolveDisplayName(string packageId, McbOptionalVpmDependency optional)
    {
        if (optional != null && !string.IsNullOrWhiteSpace(optional.displayName))
        {
            return optional.displayName;
        }

        var manifest = LoadPackageManifest();
        if (manifest.mcb != null && manifest.mcb.dependencyDisplayNames != null &&
            manifest.mcb.dependencyDisplayNames.TryGetValue(packageId, out var displayName) &&
            !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return HumanizePackageId(packageId);
    }

    private string FindRepositoryUrl(string packageId)
    {
        var manifest = LoadPackageManifest();
        if (manifest.mcb == null || manifest.mcb.repositories == null)
        {
            return null;
        }

        foreach (var repository in manifest.mcb.repositories)
        {
            if (repository == null || string.IsNullOrWhiteSpace(repository.url) || repository.packages == null)
            {
                continue;
            }

            if (repository.packages.Any(id => string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase)))
            {
                return repository.url;
            }
        }

        return null;
    }

    private McbPackageManifest LoadPackageManifest()
    {
        if (packageManifest != null && (DateTime.UtcNow - manifestLoadedAtUtc).TotalSeconds < 10)
        {
            return packageManifest;
        }

        try
        {
            string json = File.ReadAllText(Path.Combine(GetProjectPath(), PackageManifestPath));
            packageManifest = JsonConvert.DeserializeObject<McbPackageManifest>(json) ?? new McbPackageManifest();
        }
        catch (Exception ex)
        {
            MCBLogger.LogError("[MCB] Failed to read VPM dependency metadata: " + ex.Message);
            packageManifest = new McbPackageManifest();
        }

        manifestLoadedAtUtc = DateTime.UtcNow;
        return packageManifest;
    }

    private VpmDependencyStatus GetCachedStatus(string cacheKey, bool forceRefresh, Func<VpmDependencyStatus> buildStatus)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || buildStatus == null)
        {
            return null;
        }

        if (!forceRefresh &&
            dependencyStatusCache.TryGetValue(cacheKey, out var cached) &&
            DateTime.UtcNow - cached.CreatedAtUtc < DependencyStatusCacheDuration)
        {
            return cached.Status;
        }

        var status = buildStatus();
        dependencyStatusCache[cacheKey] = new CachedDependencyStatus
        {
            Status = status,
            CreatedAtUtc = DateTime.UtcNow
        };
        return status;
    }

    private void ClearDependencyStatusCache()
    {
        dependencyStatusCache.Clear();
    }

    private static string GetProjectPath()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    private static void RefreshProviders()
    {
        foreach (var provider in Repos.GetAll)
        {
            var vpmProvider = provider as VPMPackageProvider;
            if (vpmProvider != null)
            {
                vpmProvider.Refresh();
            }
        }
    }

    private static string HumanizePackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return "dependency";
        }

        string lastSegment = packageId.Split('.').LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastSegment))
        {
            lastSegment = packageId;
        }

        lastSegment = lastSegment.Replace("-", " ").Replace("_", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lastSegment);
    }

    private void NotifyStatusChanged()
    {
        var handler = StatusChanged;
        if (handler != null)
        {
            handler();
        }
    }

    private class McbPackageManifest
    {
        [JsonProperty("vpmDependencies")]
        public Dictionary<string, string> vpmDependencies = new Dictionary<string, string>();

        [JsonProperty("mcb")]
        public McbDependencyMetadata mcb = new McbDependencyMetadata();
    }

    private class McbDependencyMetadata
    {
        [JsonProperty("dependencyDisplayNames")]
        public Dictionary<string, string> dependencyDisplayNames = new Dictionary<string, string>();

        [JsonProperty("repositories")]
        public List<McbRepositoryMetadata> repositories = new List<McbRepositoryMetadata>();

        [JsonProperty("optionalVpmDependencies")]
        public Dictionary<string, McbOptionalVpmDependency> optionalVpmDependencies =
            new Dictionary<string, McbOptionalVpmDependency>();
    }

    private class McbRepositoryMetadata
    {
        [JsonProperty("url")]
        public string url;

        [JsonProperty("packages")]
        public List<string> packages = new List<string>();
    }

    private class McbOptionalVpmDependency
    {
        [JsonProperty("version")]
        public string version;

        [JsonProperty("displayName")]
        public string displayName;

        [JsonProperty("reason")]
        public string reason;
    }

    private class CachedDependencyStatus
    {
        public VpmDependencyStatus Status;
        public DateTime CreatedAtUtc;
    }
}
#endif
