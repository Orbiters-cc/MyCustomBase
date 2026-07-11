#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

public static class McbInstanceHistoryClient
{
    private static readonly HashSet<string> PendingRequests = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> PromptedRecoveries = new HashSet<string>(StringComparer.Ordinal);

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class InstanceIdentityPayload
    {
        [JsonProperty] public string lineageId;
        [JsonProperty] public string componentId;
        [JsonProperty] public string installationId;
        [JsonProperty] public string projectId;
        [JsonProperty] public string workspaceId;
        [JsonProperty] public string claimedServerInstanceId;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class BindingPayload
    {
        [JsonProperty] public int sourceModelFileId;
        [JsonProperty] public string referenceSourcePath;
        [JsonProperty] public string referenceHash;
        [JsonProperty] public string localTargetPath;
        [JsonProperty] public string preMcbBackupToken;
        [JsonProperty] public string sourceImportKind;
        [JsonProperty] public string sourcePackageName;
        [JsonProperty] public bool exists;
        [JsonProperty] public string acceptedFromBindingId;
    }

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class SyncRequest
    {
        [JsonProperty] public InstanceIdentityPayload identity;
        [JsonProperty] public int customBaseAssetId;
        [JsonProperty] public string mcbVersion;
        [JsonProperty] public string unityVersion;
        [JsonProperty] public List<BindingPayload> bindings;
        [JsonProperty] public List<string> availablePaths;
        [JsonProperty] public bool requestSuggestions;
    }

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class SyncResponse
    {
        [JsonProperty] public string instanceId;
        [JsonProperty] public List<RecoverySuggestion> suggestions = new List<RecoverySuggestion>();
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class RecoverySuggestion
    {
        [JsonProperty] public string bindingId;
        [JsonProperty] public int sourceModelFileId;
        [JsonProperty] public string referenceSourcePath;
        [JsonProperty] public string referenceHash;
        [JsonProperty] public string localTargetPath;
        [JsonProperty] public string preMcbBackupToken;
        [JsonProperty] public string sourceImportKind;
        [JsonProperty] public string sourcePackageName;
        [JsonProperty] public string provenance;
    }

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class RecoveryDecisionRequest
    {
        [JsonProperty] public bool accepted;
        [JsonProperty] public List<BindingPayload> bindings;
    }

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class ErrorResponse
    {
        [JsonProperty] public string error;
        [JsonProperty] public string code;
    }

    public static IEnumerator SyncAndRecoverCoroutine(
        string authToken,
        MyCustomBase target,
        AvatarDiscoveredAsset asset,
        IEnumerable<string> availablePaths,
        bool requestSuggestions,
        Action<bool> onComplete = null)
    {
        if (string.IsNullOrWhiteSpace(authToken) || target == null || asset == null || asset.id <= 0)
        {
            onComplete?.Invoke(false);
            yield break;
        }

        McbInstanceIdentityService.EnsureIdentity(target);
        string requestKey = target.mcbComponentId + ":" + asset.id;
        if (!PendingRequests.Add(requestKey))
        {
            onComplete?.Invoke(false);
            yield break;
        }

        bool recovered = false;
        try
        {
            var normalizedAvailablePaths = NormalizeAvailablePaths(availablePaths);
            SyncResponse response = null;
            string errorCode = null;
            yield return SendSyncRequest(
                authToken,
                BuildSyncRequest(target, asset, normalizedAvailablePaths, requestSuggestions),
                (result, code) => { response = result; errorCode = code; });

            if (response == null && string.Equals(errorCode, "MCB_COMPONENT_ID_COLLISION", StringComparison.Ordinal))
            {
                McbInstanceIdentityService.RotateComponentId(target);
                yield return SendSyncRequest(
                    authToken,
                    BuildSyncRequest(target, asset, normalizedAvailablePaths, requestSuggestions),
                    (result, code) => response = result);
            }

            if (response == null || string.IsNullOrWhiteSpace(response.instanceId)) yield break;
            if (!string.Equals(target.mcbServerInstanceId, response.instanceId, StringComparison.OrdinalIgnoreCase))
            {
                target.mcbServerInstanceId = response.instanceId;
                EditorUtility.SetDirty(target);
            }

            if (!requestSuggestions) yield break;
            var validated = ValidateCompleteRecovery(target, asset.sourceFiles, normalizedAvailablePaths, response.suggestions);
            if (validated.Count == 0) yield break;

            string promptKey = response.instanceId + ":" + string.Join(",", validated.Select(binding => binding.acceptedFromBindingId).OrderBy(id => id));
            if (!PromptedRecoveries.Add(promptKey)) yield break;

            bool accepted = EditorUtility.DisplayDialog(
                "Use previous MCB paths?",
                BuildRecoveryMessage(validated),
                "Use mapping",
                "Not this project");
            if (accepted)
            {
                foreach (var binding in validated)
                {
                    var source = asset.sourceFiles.First(file =>
                        file != null &&
                        file.id == binding.sourceModelFileId &&
                        SameSource(file, binding.referenceSourcePath, binding.referenceHash));
                    AvatarPathOverrideService.UpsertOverride(
                        target,
                        source,
                        binding.localTargetPath,
                        binding.preMcbBackupToken,
                        binding.sourceImportKind,
                        binding.sourcePackageName);
                }
                recovered = true;
            }

            yield return SendRecoveryDecision(authToken, response.instanceId, accepted, validated);
        }
        finally
        {
            PendingRequests.Remove(requestKey);
            onComplete?.Invoke(recovered);
        }
    }

    public static List<BindingPayload> ValidateCompleteRecovery(
        MyCustomBase target,
        IEnumerable<ModelFileData> sourceFiles,
        IEnumerable<string> availablePaths,
        IEnumerable<RecoverySuggestion> suggestions)
    {
        var sources = FilterSourceFiles(sourceFiles);
        var unresolved = sources
            .Where(source => string.IsNullOrWhiteSpace(AvatarPathOverrideService.ResolveLocalTargetPath(target, source)))
            .ToList();
        if (unresolved.Count == 0) return new List<BindingPayload>();

        var allowedPaths = new HashSet<string>(NormalizeAvailablePaths(availablePaths), StringComparer.OrdinalIgnoreCase);
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BindingPayload>();
        foreach (var suggestion in suggestions ?? Enumerable.Empty<RecoverySuggestion>())
        {
            if (suggestion == null || string.IsNullOrWhiteSpace(suggestion.bindingId)) continue;
            var source = unresolved.FirstOrDefault(candidate =>
                candidate.id == suggestion.sourceModelFileId &&
                SameSource(candidate, suggestion.referenceSourcePath, suggestion.referenceHash));
            string targetPath = AvatarPathOverrideService.NormalizeUnityPath(suggestion.localTargetPath);
            if (source == null || !allowedPaths.Contains(targetPath) || !IsMatchingProjectFbx(targetPath, source.hash)) continue;

            string sourceKey = source.path.ToLowerInvariant() + ":" + source.hash.ToLowerInvariant();
            if (!usedSources.Add(sourceKey) || !usedTargets.Add(targetPath)) return new List<BindingPayload>();
            result.Add(new BindingPayload
            {
                sourceModelFileId = source.id,
                referenceSourcePath = source.path,
                referenceHash = source.hash,
                localTargetPath = targetPath,
                preMcbBackupToken = suggestion.preMcbBackupToken,
                sourceImportKind = suggestion.sourceImportKind,
                sourcePackageName = suggestion.sourcePackageName,
                exists = true,
                acceptedFromBindingId = suggestion.bindingId
            });
        }

        return result.Count == unresolved.Count ? result : new List<BindingPayload>();
    }

    private static SyncRequest BuildSyncRequest(
        MyCustomBase target,
        AvatarDiscoveredAsset asset,
        List<string> availablePaths,
        bool requestSuggestions)
    {
        return new SyncRequest
        {
            identity = new InstanceIdentityPayload
            {
                lineageId = target.mcbInstanceId,
                componentId = target.mcbComponentId,
                installationId = McbInstanceIdentityService.InstallationId,
                projectId = McbInstanceIdentityService.ProjectId,
                workspaceId = McbInstanceIdentityService.WorkspaceId,
                claimedServerInstanceId = target.mcbServerInstanceId
            },
            customBaseAssetId = asset.id,
            mcbVersion = PackageInfo.FindForAssembly(typeof(MyCustomBase).Assembly)?.version,
            unityVersion = Application.unityVersion,
            bindings = BuildCurrentBindings(target, asset.sourceFiles),
            availablePaths = availablePaths,
            requestSuggestions = requestSuggestions
        };
    }

    private static List<BindingPayload> BuildCurrentBindings(MyCustomBase target, IEnumerable<ModelFileData> sourceFiles)
    {
        var result = new List<BindingPayload>();
        foreach (var source in FilterSourceFiles(sourceFiles))
        {
            var entry = AvatarPathOverrideService.FindOverride(target, source);
            if (entry == null) continue;

            string resolvedPath = AvatarPathOverrideService.ResolveLocalTargetPath(target, source);
            string storedPath = AvatarPathOverrideService.NormalizeUnityPath(entry.localTargetPath);
            string localPath = !string.IsNullOrWhiteSpace(resolvedPath) ? resolvedPath : storedPath;
            if (!MCBUtils.TryResolveProjectAssetPath(localPath, out localPath, out string fullPath)) continue;
            result.Add(new BindingPayload
            {
                sourceModelFileId = source.id,
                referenceSourcePath = source.path,
                referenceHash = source.hash,
                localTargetPath = localPath,
                preMcbBackupToken = entry.preMcbBackupToken,
                sourceImportKind = entry.sourceImportKind,
                sourcePackageName = string.IsNullOrWhiteSpace(entry.sourcePackagePath) ? null : Path.GetFileName(entry.sourcePackagePath),
                exists = File.Exists(fullPath)
            });
        }
        return result;
    }

    private static List<ModelFileData> FilterSourceFiles(IEnumerable<ModelFileData> sourceFiles)
    {
        return (sourceFiles ?? Enumerable.Empty<ModelFileData>())
            .Where(file => file != null &&
                           file.id > 0 &&
                           string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase) &&
                           IsSha256(file.hash) &&
                           MCBUtils.TryResolveProjectAssetPath(file.path, out _, out _))
            .ToList();
    }

    private static List<string> NormalizeAvailablePaths(IEnumerable<string> paths)
    {
        return (paths ?? Enumerable.Empty<string>())
            .Select(AvatarPathOverrideService.NormalizeUnityPath)
            .Where(path => MCBUtils.TryResolveProjectAssetPath(path, out _, out _) && AssetImporter.GetAtPath(path) is ModelImporter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsMatchingProjectFbx(string targetPath, string expectedHash)
    {
        if (!MCBUtils.TryResolveProjectAssetPath(targetPath, out _, out string targetFullPath) ||
            !File.Exists(targetFullPath) ||
            !(AssetImporter.GetAtPath(targetPath) is ModelImporter))
        {
            return false;
        }

        if (HashMatches(targetFullPath, expectedHash)) return true;
        string originalBasePath = FileManagerService.GetOriginalBasePath(targetPath);
        return MCBUtils.TryResolveProjectAssetPath(originalBasePath, out _, out string originalBaseFullPath) &&
               File.Exists(originalBaseFullPath) &&
               HashMatches(originalBaseFullPath, expectedHash);
    }

    private static bool HashMatches(string fullPath, string expectedHash)
    {
        try
        {
            return string.Equals(MCBUtils.CalculateFileHash(fullPath), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool SameSource(ModelFileData source, string path, string hash)
    {
        return source != null &&
               string.Equals(AvatarPathOverrideService.NormalizeUnityPath(source.path), AvatarPathOverrideService.NormalizeUnityPath(path), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(source.hash, hash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static string BuildRecoveryMessage(IEnumerable<BindingPayload> bindings)
    {
        var builder = new StringBuilder("MCB found a path mapping previously used by your account and verified it against this avatar's original base files.\n\n");
        foreach (var binding in bindings)
        {
            builder.Append(Path.GetFileName(binding.referenceSourcePath));
            builder.Append(" -> ");
            builder.AppendLine(binding.localTargetPath);
        }
        builder.Append("\nUse this mapping for this MCB component?");
        return builder.ToString();
    }

    private static IEnumerator SendSyncRequest(string authToken, SyncRequest payload, Action<SyncResponse, string> onComplete)
    {
        string url = $"{MCBUtils.getApiUrl()}/instances/sync?t={UnityWebRequest.EscapeURL(authToken)}";
        using (var request = CreateJsonPost(url, payload))
        {
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Sync MCB instance"));
            if (request.result != UnityWebRequest.Result.Success)
            {
                var error = TryDeserialize<ErrorResponse>(request.downloadHandler.text);
                MCBLogger.LogWarning($"[MCB Instance] Sync failed: {error?.error ?? request.error}");
                onComplete?.Invoke(null, error?.code);
                yield break;
            }
            onComplete?.Invoke(TryDeserialize<SyncResponse>(request.downloadHandler.text), null);
        }
    }

    private static IEnumerator SendRecoveryDecision(
        string authToken,
        string instanceId,
        bool accepted,
        List<BindingPayload> bindings)
    {
        string url = $"{MCBUtils.getApiUrl()}/instances/{UnityWebRequest.EscapeURL(instanceId)}/recovery-decision?t={UnityWebRequest.EscapeURL(authToken)}";
        using (var request = CreateJsonPost(url, new RecoveryDecisionRequest { accepted = accepted, bindings = bindings }))
        {
            yield return MCBManagedRequest.SendUnityWebRequest(request, url, MCBRequestPolicy.Backend("Save MCB path recovery decision"));
            if (request.result != UnityWebRequest.Result.Success)
            {
                MCBLogger.LogWarning($"[MCB Instance] Could not save path recovery decision: {request.error}");
            }
        }
    }

    private static UnityWebRequest CreateJsonPost(string url, object payload)
    {
        var request = new UnityWebRequest(url, "POST");
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = NetworkService.GetTimeoutSeconds(NetworkRequestType.AssetDiscovery);
        return request;
    }

    private static T TryDeserialize<T>(string json) where T : class
    {
        try { return JsonConvert.DeserializeObject<T>(json); }
        catch { return null; }
    }
}
#endif
