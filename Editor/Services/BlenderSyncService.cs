#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

public static class BlenderSyncService
{
    private const string MagicSyncKind = "orbiters.mcb.magicSync";
    private const string BlenderOfferKind = "orbiters.mcb.blenderMagicSyncOffer";
    private const string BlenderLaunchKind = "orbiters.mcb.blenderLaunch";
    private const string ExportKind = "orbiters.mcb.blenderExport";
    private const string ReadyKind = "orbiters.mcb.blenderExportReady";
    private const int ProtocolVersion = 1;
    private const double BlenderHeartbeatTimeoutSeconds = 4.0;
    private const double PollIntervalSeconds = 0.5;
    private const double PersistedSessionMaxAgeDays = 2.0;
    private static readonly List<ActiveSession> ActiveSessions = new List<ActiveSession>();
    private static bool pollingHooked;
    private static bool sessionsRestored;
    private static double nextPollTime;
    private static string lastStatus;
    private static MessageType lastStatusType = MessageType.Info;
    private static Texture2D blenderIcon;
    private static readonly List<PreparationCompletion> PendingPreparationCompletions = new List<PreparationCompletion>();
    private static readonly HashSet<string> PreparingProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private class PreparationCompletion
    {
        public string blenderPath;
        public string projectPath;
        public string projectUnityPath;
        public string customBaseName;
        public string logPath;
        public int exitCode;
    }

    private class ActiveSession
    {
        public string sessionId;
        public string token;
        public string inboxPath;
        public string heartbeatPath;
        public string customBaseName;
        public string customBaseGlobalId;
        public MyCustomBase customBase;
        public List<TargetFbxInfo> targetFbxFiles = new List<TargetFbxInfo>();
        public string connectionState = "waiting for Blender";
        public bool useProjectExports;
        public string blenderProjectId;
        public string blenderProjectUnityPath;
        public string blenderProjectAbsolutePath;
        public string blenderExportsUnityPath;
        public string blenderExportsAbsolutePath;
    }

    private class TargetFbxInfo
    {
        public string unityPath;
        public string absolutePath;
        public string name;
        public List<string> meshNames = new List<string>();
        public List<ModelFileSmrPathData> smrPaths = new List<ModelFileSmrPathData>();
        public List<RendererMaterialInfo> materials = new List<RendererMaterialInfo>();
    }

    private class PersistedSession
    {
        public string sessionId;
        public string token;
        public string inboxPath;
        public string heartbeatPath;
        public string customBaseName;
        public string customBaseGlobalId;
        public List<TargetFbxInfo> targetFbxFiles = new List<TargetFbxInfo>();
        public bool useProjectExports;
        public string blenderProjectId;
        public string blenderProjectUnityPath;
        public string blenderProjectAbsolutePath;
        public string blenderExportsUnityPath;
        public string blenderExportsAbsolutePath;
    }

    public class BlenderProjectInfo
    {
        public string projectId;
        public string projectUnityPath;
        public string projectAbsolutePath;
        public string exportsUnityPath;
        public string exportsAbsolutePath;
    }

    private class SyncSessionBuildResult
    {
        public ActiveSession activeSession;
        public string payloadJson;
        public JObject payload;
        public BlenderProjectInfo project;
    }

    private class ReadyPayload
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
        public string manifestPath;
    }

    private class BlenderExportManifest
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
        public TargetInfo target;
        public List<ModelInfo> models;
    }

    private class TargetInfo
    {
        public string targetFbxPath;
        public string customBaseName;
    }

    private class ModelInfo
    {
        public string role;
        public string path;
        public string targetFbxPath;
        public List<string> meshNames;
        public List<string> shapeKeys;
    }

    private class RendererMaterialInfo
    {
        public string rendererName;
        public string meshName;
        public int slot;
        public string materialName;
        public ColorInfo baseColor;
        public float metallic;
        public float smoothness;
        public float roughness;
        public TextureInfo baseColorTexture;
        public TextureInfo metallicTexture;
        public TextureInfo smoothnessTexture;
        public TextureInfo roughnessTexture;
        public TextureInfo normalTexture;
    }

    private class TextureInfo
    {
        public string unityPath;
        public string absolutePath;
        public string name;
    }

    private class ColorInfo
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    private class BlenderMagicSyncOffer
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
        public string responsePath;
    }

    [InitializeOnLoadMethod]
    private static void InitializePollingOnLoad()
    {
        EnsurePolling();
    }

    public static void DrawCreatorModeSection(MCBEditor editor)
    {
        if (editor == null || editor.customBaseTarget == null) return;

        EditorGUILayout.Space();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            DrawConnectorHeader();
            EditorGUILayout.HelpBox(
                "Connects Unity MCB with the Blender MCB addon. You can click this first or start Magic Sync in Blender first; the tools exchange target FBX and renderer path data so Blender exports the right meshes and Unity refreshes the intended renderers.",
                MessageType.Info);

            var targetFbxPaths = GetTargetFbxPaths(editor);
            if (targetFbxPaths.Count == 0)
            {
                EditorGUILayout.HelpBox("No target FBX was detected for this avatar. Assign or detect the base FBX before syncing.", MessageType.Warning);
            }

            string blenderPath = DrawBlenderInstallationSelector();
            if (string.IsNullOrWhiteSpace(blenderPath))
            {
                EditorGUILayout.HelpBox("Blender was not detected. Use Advanced Mode to browse to the Blender executable.", MessageType.Warning);
            }

            var session = GetSessionForEditor(editor);
            if (session != null)
            {
                UpdateConnectionState(session);
            }

            bool isConnected = session != null && string.Equals(session.connectionState, "connected", StringComparison.Ordinal);
            bool isPreparing = IsEditorProjectPreparing(editor);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(targetFbxPaths.Count == 0 || string.IsNullOrWhiteSpace(blenderPath) || isConnected || isPreparing))
                {
                    if (GUILayout.Button("Modify with Blender", GUILayout.Height(28f)))
                    {
                        OpenWithBlender(editor, targetFbxPaths);
                    }
                }

                using (new EditorGUI.DisabledScope(targetFbxPaths.Count == 0))
                {
                    if (GUILayout.Button("Sync with Blender", GUILayout.Height(28f)))
                    {
                        StartSync(editor, targetFbxPaths);
                    }
                }
            }

            DrawBlenderConnectionState(editor);

            if (!string.IsNullOrEmpty(lastStatus))
            {
                EditorGUILayout.HelpBox(lastStatus, lastStatusType);
            }
        }
    }

    private static void DrawConnectorHeader()
    {
        if (blenderIcon == null)
        {
            blenderIcon = LoadBlenderIcon();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (blenderIcon != null)
            {
                GUILayout.Label(blenderIcon, GUILayout.Width(28f), GUILayout.Height(28f));
            }

            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Space(4f);
                EditorGUILayout.LabelField("Blender connector", EditorStyles.boldLabel);
            }
        }
    }

    private static Texture2D LoadBlenderIcon()
    {
        string assetPath = MCBUtils.CombineUnityPath(MCBUtils.PACKAGE_BASE_FOLDER, "Editor", "blender.png");
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        if (texture != null)
        {
            return texture;
        }

        try
        {
            string absolutePath = Path.Combine(MCBUtils.PACKAGE_BASE_FOLDER_FULL_PATH, "Editor", "blender.png");
            if (!File.Exists(absolutePath))
            {
                return null;
            }

            var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            loaded.name = "MCB Blender Icon";
            return loaded.LoadImage(File.ReadAllBytes(absolutePath)) ? loaded : null;
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning("[BlenderSync] Failed to load Blender connector icon: " + ex.Message);
            return null;
        }
    }

    private static string DrawBlenderInstallationSelector()
    {
        var installations = BlenderInstallService.GetInstallations();
        if (installations.Count == 0)
        {
            return "";
        }

        string selectedPath = BlenderInstallService.GetSelectedExecutablePath();
        int selectedIndex = installations.FindIndex(item =>
            string.Equals(item.executablePath, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
            selectedPath = installations[0].executablePath;
        }

        string[] options = installations.Select(item => item.DisplayLabel).ToArray();
        EditorGUI.BeginChangeCheck();
        int nextIndex = EditorGUILayout.Popup("Blender installation", selectedIndex, options);
        if (EditorGUI.EndChangeCheck() && nextIndex >= 0 && nextIndex < installations.Count)
        {
            BlenderInstallService.SetSelectedExecutablePath(installations[nextIndex].executablePath);
            selectedPath = installations[nextIndex].executablePath;
        }

        return selectedPath;
    }

    public static void DrawAdvancedBlenderConnectorSettings()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Blender connector", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh Blender List", GUILayout.Width(150f)))
            {
                BlenderInstallService.GetInstallations(forceRefresh: true);
            }

            if (GUILayout.Button("Browse", GUILayout.Width(100f)))
            {
                BlenderInstallService.PickExecutable();
            }

            if (GUILayout.Button("Clear Selection", GUILayout.Width(120f)))
            {
                BlenderInstallService.ClearSelectedExecutablePath();
            }
        }
    }

    private static bool IsEditorProjectPreparing(MCBEditor editor)
    {
        try
        {
            var projectInfo = BlenderProjectService.CreateProjectInfo(editor);
            return projectInfo != null && IsProjectPreparing(projectInfo.projectAbsolutePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProjectPreparing(string projectPath)
    {
        string normalized = NormalizePreparationPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        lock (PreparingProjectPaths)
        {
            return PreparingProjectPaths.Contains(normalized);
        }
    }

    private static bool TryBeginProjectPreparation(string projectPath)
    {
        string normalized = NormalizePreparationPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        lock (PreparingProjectPaths)
        {
            return PreparingProjectPaths.Add(normalized);
        }
    }

    private static void EndProjectPreparation(string projectPath)
    {
        string normalized = NormalizePreparationPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (PreparingProjectPaths)
        {
            PreparingProjectPaths.Remove(normalized);
        }
    }

    private static string NormalizePreparationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static void StartSync(MCBEditor editor, List<string> targetFbxPaths)
    {
        bool hasBlenderOffer = TryReadBlenderOfferFromClipboard(out var blenderOffer);
        var result = CreateSyncSession(editor, targetFbxPaths, null);
        EditorGUIUtility.systemCopyBuffer = result.payloadJson;
        RegisterActiveSession(result.activeSession);

        if (hasBlenderOffer)
        {
            WritePayloadToBlenderOffer(blenderOffer, result.payloadJson);
            SetStatus($"Magic Sync connected to Blender for {result.activeSession.customBaseName}. Waiting for Blender export in:\n{result.activeSession.inboxPath}", MessageType.Info);
        }
        else
        {
            SetStatus($"Magic Sync copied to clipboard for {result.activeSession.customBaseName}. Waiting for Blender export in:\n{result.activeSession.inboxPath}", MessageType.Info);
        }
    }

    private static void OpenWithBlender(MCBEditor editor, List<string> targetFbxPaths)
    {
        BlenderProjectInfo projectInfo = null;
        bool preparationStarted = false;
        try
        {
            string blenderPath = BlenderInstallService.GetSelectedExecutablePath();
            if (string.IsNullOrWhiteSpace(blenderPath))
            {
                EditorUtility.DisplayDialog("Blender Not Found", "Set the Blender executable path before using one-click editing.", "OK");
                return;
            }

            projectInfo = BlenderProjectService.CreateProjectInfo(editor);
            if (!TryBeginProjectPreparation(projectInfo.projectAbsolutePath))
            {
                SetStatus($"Blender project is already being prepared:\n{projectInfo.projectUnityPath}", MessageType.Info);
                try { InternalEditorUtility.RepaintAllViews(); } catch { }
                return;
            }
            preparationStarted = true;

            BlenderProjectService.EnsureProjectFolders(projectInfo);

            var result = CreateSyncSession(editor, targetFbxPaths, projectInfo);
            RegisterActiveSession(result.activeSession);
            EditorGUIUtility.systemCopyBuffer = result.payloadJson;

            string launchDirectory = Path.Combine(GetProjectRoot(), "Library", "MCB", "BlenderLaunch", result.activeSession.sessionId);
            Directory.CreateDirectory(launchDirectory);

            string launchConfigPath = Path.Combine(launchDirectory, "launch.json");
            string bootstrapScriptPath = Path.Combine(launchDirectory, "launch_blender.py");
            string logPath = Path.Combine(launchDirectory, "prepare.log");
            WriteBlenderLaunchConfig(result, launchConfigPath);
            BlenderAddonService.WriteBootstrapScript(bootstrapScriptPath, launchConfigPath);

            StartBlenderProjectPreparation(blenderPath, projectInfo, bootstrapScriptPath, logPath, result.activeSession.customBaseName);
            preparationStarted = false;

            SetStatus($"Preparing Blender project for {result.activeSession.customBaseName} in the background:\n{projectInfo.projectUnityPath}", MessageType.Info);
            try { InternalEditorUtility.RepaintAllViews(); } catch { }
        }
        catch (Exception ex)
        {
            if (preparationStarted && projectInfo != null)
            {
                EndProjectPreparation(projectInfo.projectAbsolutePath);
            }

            SetStatus("Failed to open Blender: " + ex.Message, MessageType.Error);
            MCBLogger.LogError("[BlenderSync] Failed to open Blender: " + ex);
        }
    }

    private static SyncSessionBuildResult CreateSyncSession(MCBEditor editor, List<string> targetFbxPaths, BlenderProjectInfo projectInfo)
    {
        string projectPath = GetProjectRoot();
        string sessionId = Guid.NewGuid().ToString("N");
        string token = Guid.NewGuid().ToString("N");
        string inboxPath = Path.Combine(projectPath, "Library", "MCB", "BlenderSync", sessionId);
        string heartbeatPath = Path.Combine(inboxPath, "blender_heartbeat.json");
        Directory.CreateDirectory(inboxPath);

        string customBaseName = editor.GetSelectedAssetDisplayName();
        var selectedAsset = editor.GetSelectedAsset();
        var smrPathsByFbx = SmrPathService.CollectSmrPathsByFbx(editor.customBaseTarget.transform.root, targetFbxPaths);
        string customBaseGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(editor.customBaseTarget).ToString();
        var targetFiles = targetFbxPaths.Select(path =>
        {
            string unityPath = MCBUtils.ToUnityPath(path);
            var discoveredEntries = smrPathsByFbx.TryGetValue(unityPath, out var entries)
                ? entries
                : new List<ModelFileSmrPathData>();
            var serverEntries = GetSelectedAssetSmrPaths(selectedAsset, unityPath);
            var mergedEntries = MergeSmrPaths(discoveredEntries, serverEntries);
            return new TargetFbxInfo
            {
                unityPath = unityPath,
                absolutePath = UnityPathToAbsolute(unityPath),
                name = Path.GetFileName(unityPath),
                meshNames = GetFbxMeshNames(unityPath),
                smrPaths = mergedEntries,
                materials = CollectRendererMaterials(editor.customBaseTarget.transform.root, unityPath, mergedEntries)
            };
        }).ToList();

        var capabilities = new List<string>
        {
            "fbxReplace",
            "smrPathRefresh",
            "xmuscleMetadata"
        };
        if (projectInfo != null)
        {
            capabilities.Add("projectExports");
        }

        var payload = new
        {
            kind = MagicSyncKind,
            protocolVersion = ProtocolVersion,
            sessionId = sessionId,
            token = token,
            unityProjectPath = projectPath,
            mcbPackagePath = MCBUtils.PACKAGE_BASE_FOLDER_FULL_PATH,
            mcbVersion = ReadPackageVersion(),
            toolVersion = MCBUtils.SCRIPT_VERSION,
            inboxPath = inboxPath,
            heartbeatPath = heartbeatPath,
            ui = new
            {
                bannerPath = ResolveCurrentBannerPath(editor),
                user = ResolveCurrentUser()
            },
            selectedCustomBase = new
            {
                name = customBaseName,
                assetId = selectedAsset != null ? selectedAsset.id : 0,
                baseFbxFiles = targetFbxPaths.ToArray()
            },
            blenderProject = projectInfo,
            targetFbxFiles = targetFiles,
            capabilities = capabilities.ToArray()
        };

        string payloadJson = JsonConvert.SerializeObject(payload, Formatting.Indented);
        var activeSession = new ActiveSession
        {
            sessionId = sessionId,
            token = token,
            inboxPath = inboxPath,
            heartbeatPath = heartbeatPath,
            customBaseName = customBaseName,
            customBaseGlobalId = customBaseGlobalId,
            customBase = editor.customBaseTarget,
            targetFbxFiles = targetFiles,
            useProjectExports = projectInfo != null,
            blenderProjectId = projectInfo != null ? projectInfo.projectId : null,
            blenderProjectUnityPath = projectInfo != null ? projectInfo.projectUnityPath : null,
            blenderProjectAbsolutePath = projectInfo != null ? projectInfo.projectAbsolutePath : null,
            blenderExportsUnityPath = projectInfo != null ? projectInfo.exportsUnityPath : null,
            blenderExportsAbsolutePath = projectInfo != null ? projectInfo.exportsAbsolutePath : null
        };

        return new SyncSessionBuildResult
        {
            activeSession = activeSession,
            payloadJson = payloadJson,
            payload = JObject.Parse(payloadJson),
            project = projectInfo
        };
    }

    private static void RegisterActiveSession(ActiveSession activeSession)
    {
        if (activeSession == null)
        {
            return;
        }

        ActiveSessions.RemoveAll(x =>
            x == null ||
            x.customBase == null ||
            (activeSession.customBase != null && x.customBase == activeSession.customBase));
        ActiveSessions.Add(activeSession);
        WriteSessionFile(activeSession);
        EnsurePolling();
    }

    private static void WriteBlenderLaunchConfig(SyncSessionBuildResult result, string launchConfigPath)
    {
        var launchPayload = new
        {
            kind = BlenderLaunchKind,
            protocolVersion = ProtocolVersion,
            createdAtUtc = DateTime.UtcNow.ToString("o"),
            syncSession = result.payload,
            project = result.project,
            addon = BlenderAddonService.CreateLaunchPayload()
        };

        File.WriteAllText(launchConfigPath, JsonConvert.SerializeObject(launchPayload, Formatting.Indented));
    }

    private static void StartBlenderProjectPreparation(
        string blenderPath,
        BlenderProjectInfo projectInfo,
        string bootstrapScriptPath,
        string logPath,
        string customBaseName)
    {
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = blenderPath,
            Arguments = "--background --python " + QuoteArgument(bootstrapScriptPath),
            WorkingDirectory = GetProjectRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        object logLock = new object();
        DataReceivedEventHandler appendLog = (_sender, args) =>
        {
            if (args == null || string.IsNullOrEmpty(args.Data)) return;
            lock (logLock)
            {
                File.AppendAllText(logPath, args.Data + Environment.NewLine);
            }
        };

        process.OutputDataReceived += appendLog;
        process.ErrorDataReceived += appendLog;
        process.Exited += (_sender, _args) =>
        {
            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { exitCode = -1; }

            lock (PendingPreparationCompletions)
            {
                PendingPreparationCompletions.Add(new PreparationCompletion
                {
                    blenderPath = blenderPath,
                    projectPath = projectInfo.projectAbsolutePath,
                    projectUnityPath = projectInfo.projectUnityPath,
                    customBaseName = customBaseName,
                    logPath = logPath,
                    exitCode = exitCode
                });
            }

            process.Dispose();
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Blender process did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void ProcessPendingPreparationCompletions()
    {
        List<PreparationCompletion> completions;
        lock (PendingPreparationCompletions)
        {
            if (PendingPreparationCompletions.Count == 0)
            {
                return;
            }

            completions = new List<PreparationCompletion>(PendingPreparationCompletions);
            PendingPreparationCompletions.Clear();
        }

        foreach (var completion in completions)
        {
            EndProjectPreparation(completion.projectPath);

            if (completion.exitCode == 0 && File.Exists(completion.projectPath))
            {
                OpenPreparedBlenderProject(completion.blenderPath, completion.projectPath);
                SetStatus($"Blender project opened for {completion.customBaseName}:\n{completion.projectUnityPath}", MessageType.Info);
            }
            else
            {
                SetStatus($"Blender project preparation failed for {completion.customBaseName}. Log:\n{completion.logPath}", MessageType.Error);
            }

            try { InternalEditorUtility.RepaintAllViews(); } catch { }
        }
    }

    private static void OpenPreparedBlenderProject(string blenderPath, string projectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = blenderPath,
            Arguments = QuoteArgument(projectPath),
            WorkingDirectory = GetProjectRoot(),
            UseShellExecute = false,
            CreateNoWindow = false
        };
        Process.Start(startInfo);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }

    private static bool TryReadBlenderOfferFromClipboard(out BlenderMagicSyncOffer offer)
    {
        offer = null;
        string raw = EditorGUIUtility.systemCopyBuffer;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            var data = JObject.Parse(raw);
            if (!string.Equals(data.Value<string>("kind"), BlenderOfferKind, StringComparison.Ordinal)) return false;

            offer = new BlenderMagicSyncOffer
            {
                kind = data.Value<string>("kind"),
                protocolVersion = data.Value<int?>("protocolVersion") ?? 0,
                sessionId = data.Value<string>("sessionId"),
                token = data.Value<string>("token"),
                responsePath = data.Value<string>("responsePath")
            };
            if (offer == null || offer.protocolVersion > ProtocolVersion) return false;
            return !string.IsNullOrWhiteSpace(offer.responsePath)
                   && !string.IsNullOrWhiteSpace(offer.sessionId)
                   && !string.IsNullOrWhiteSpace(offer.token);
        }
        catch
        {
            return false;
        }
    }

    private static void WritePayloadToBlenderOffer(BlenderMagicSyncOffer offer, string payloadJson)
    {
        if (offer == null || string.IsNullOrWhiteSpace(offer.responsePath) || string.IsNullOrWhiteSpace(payloadJson)) return;

        string responsePath = Path.GetFullPath(offer.responsePath);
        string responseDirectory = Path.GetDirectoryName(responsePath);
        if (!string.IsNullOrWhiteSpace(responseDirectory))
        {
            Directory.CreateDirectory(responseDirectory);
        }

        var payload = JObject.Parse(payloadJson);
        payload["blenderOfferSessionId"] = offer.sessionId;
        payload["blenderOfferToken"] = offer.token;
        File.WriteAllText(responsePath, payload.ToString(Formatting.Indented));
    }

    private static void EnsurePolling()
    {
        if (pollingHooked) return;
        pollingHooked = true;
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        ProcessPendingPreparationCompletions();
        RestorePersistedSessionsIfNeeded();
        if (ActiveSessions.Count == 0) return;

        double now = EditorApplication.timeSinceStartup;
        if (now < nextPollTime)
        {
            return;
        }

        nextPollTime = now + PollIntervalSeconds;

        for (int i = ActiveSessions.Count - 1; i >= 0; i--)
        {
            var session = ActiveSessions[i];
            if (session == null || session.customBase == null)
            {
                ResolveCustomBase(session);
            }

            UpdateConnectionState(session);

            if (string.IsNullOrEmpty(session.inboxPath) || !Directory.Exists(session.inboxPath))
            {
                continue;
            }

            string[] readyFiles;
            try
            {
                readyFiles = Directory.GetFiles(session.inboxPath, "ready.json", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                SetStatus("Failed to scan Blender sync inbox: " + ex.Message, MessageType.Error);
                continue;
            }

            foreach (string readyPath in readyFiles)
            {
                if (File.Exists(readyPath + ".processed") || File.Exists(readyPath + ".failed")) continue;

                try
                {
                    MCBLogger.Log($"[BlenderSync] Detected Blender export marker: {readyPath}");
                    ProcessReadyFile(session, readyPath);
                    File.Move(readyPath, readyPath + ".processed");
                }
                catch (Exception ex)
                {
                    SetStatus("Blender sync import failed: " + ex.Message, MessageType.Error);
                    MCBLogger.LogError($"[BlenderSync] Import failed for {readyPath}: {ex}");
                    MarkReadyFileFailed(readyPath, ex);
                }
            }
        }
    }

    private static void MarkReadyFileFailed(string readyPath, Exception ex)
    {
        if (string.IsNullOrWhiteSpace(readyPath))
        {
            return;
        }

        string failedPath = readyPath + ".failed";
        try
        {
            if (File.Exists(failedPath))
            {
                File.Delete(failedPath);
            }

            File.Move(readyPath, failedPath);
        }
        catch (Exception moveEx)
        {
            try
            {
                File.WriteAllText(failedPath, (ex != null ? ex.ToString() : "Unknown Blender sync import failure") + "\n\nFailed to move ready marker: " + moveEx);
            }
            catch
            {
                // Best-effort failure marker only; the import error above is the actionable log.
            }
        }
    }

    private static void ProcessReadyFile(ActiveSession session, string readyPath)
    {
        var ready = JsonConvert.DeserializeObject<ReadyPayload>(File.ReadAllText(readyPath));
        if (ready == null || ready.kind != ReadyKind)
        {
            throw new InvalidOperationException("ready.json is not a Blender MCB export marker.");
        }
        if (ready.protocolVersion > ProtocolVersion)
        {
            throw new InvalidOperationException("Blender export uses a newer sync protocol.");
        }
        if (!string.Equals(ready.sessionId, session.sessionId, StringComparison.Ordinal) ||
            !string.Equals(ready.token, session.token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Blender export session token does not match this Unity sync session.");
        }

        string exportDir = Path.GetDirectoryName(readyPath);
        string manifestPath = Path.Combine(exportDir, string.IsNullOrWhiteSpace(ready.manifestPath) ? "manifest.json" : ready.manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Blender export manifest was not found.", manifestPath);
        }

        var manifest = JsonConvert.DeserializeObject<BlenderExportManifest>(File.ReadAllText(manifestPath));
        if (manifest == null || manifest.kind != ExportKind)
        {
            throw new InvalidOperationException("manifest.json is not a Blender MCB export.");
        }
        if (!string.Equals(manifest.sessionId, session.sessionId, StringComparison.Ordinal) ||
            !string.Equals(manifest.token, session.token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Manifest session token does not match this Unity sync session.");
        }

        var models = manifest.models?
            .Where(x => x != null && string.Equals(x.role, "CUSTOM_BASE", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<ModelInfo>();
        if (models.Count == 0)
        {
            throw new InvalidOperationException("Blender export manifest has no CUSTOM_BASE model.");
        }

        var updatedTargets = new List<string>();
        int generatedAvatarCount = 0;
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            var model = models[modelIndex];
            string modelRelativePath = model.path;
            if (string.IsNullOrWhiteSpace(modelRelativePath))
            {
                continue;
            }

            string sourceFbxPath = Path.GetFullPath(Path.Combine(exportDir, modelRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(sourceFbxPath))
            {
                throw new FileNotFoundException("Blender exported FBX was not found.", sourceFbxPath);
            }

            string targetFbxPath = ResolveTargetFbxPath(session, manifest, model);
            if (string.IsNullOrWhiteSpace(targetFbxPath))
            {
                throw new InvalidOperationException("Could not resolve the target Unity FBX path.");
            }

            if (session.useProjectExports)
            {
                string exportedUnityPath = CopyModelToProjectExports(session, sourceFbxPath, targetFbxPath, modelIndex);
                AssetDatabase.ImportAsset(exportedUnityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Avatar generatedAvatar = GenerateAvatarForImportedFbx(exportedUnityPath, targetFbxPath, keepImporterConfiguredForEditing: true);
                if (generatedAvatar != null)
                {
                    generatedAvatarCount++;
                }
                RefreshTargetMeshesFromFbx(session, exportedUnityPath, targetFbxPath, model.meshNames);
                AssignCreatorCustomFbx(session, targetFbxPath, exportedUnityPath, generatedAvatar);
                ApplyGeneratedAvatarToPreview(session, exportedUnityPath, generatedAvatar);
                updatedTargets.Add(exportedUnityPath);
                MCBLogger.Log($"[BlenderSync] Imported Blender export. source={sourceFbxPath} projectExport={exportedUnityPath} target={targetFbxPath}");
            }
            else
            {
                ReplaceTargetFbx(sourceFbxPath, targetFbxPath);
                AssetDatabase.ImportAsset(targetFbxPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Avatar generatedAvatar = GenerateAvatarForImportedFbx(targetFbxPath, targetFbxPath, keepImporterConfiguredForEditing: false);
                if (generatedAvatar != null)
                {
                    generatedAvatarCount++;
                }
                RefreshTargetMeshesFromFbx(session, targetFbxPath, targetFbxPath, model.meshNames);
                ApplyGeneratedAvatarToPreview(session, targetFbxPath, generatedAvatar);
                updatedTargets.Add(targetFbxPath);
                MCBLogger.Log($"[BlenderSync] Imported Blender export. source={sourceFbxPath} target={targetFbxPath}");
            }
        }

        if (updatedTargets.Count == 0)
        {
            throw new InvalidOperationException("Blender export manifest did not contain any usable CUSTOM_BASE model paths.");
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        if (session.customBase != null)
        {
            EditorUtility.SetDirty(session.customBase);
        }

        string action = session.useProjectExports ? "Updated avatar from" : "Replaced";
        string avatarMessage = generatedAvatarCount > 0
            ? $"\nGenerated {generatedAvatarCount} Avatar asset(s)."
            : "\nNo Avatar asset was generated.";
        SetStatus($"Blender export imported for {session.customBaseName}.\n{action} {updatedTargets.Count} FBX file(s).{avatarMessage}", MessageType.Info);
    }

    private static void DrawBlenderConnectionState(MCBEditor editor)
    {
        var session = GetSessionForEditor(editor);
        if (session == null)
        {
            return;
        }

        UpdateConnectionState(session);
        EditorGUILayout.Space(4);

        const float dotSize = 8f;
        const float spacing = 6f;
        string displayState = string.IsNullOrWhiteSpace(session.connectionState) ? "waiting for Blender" : session.connectionState;
        GUIContent stateContent = new GUIContent(displayState);
        Vector2 labelSize = EditorStyles.miniLabel.CalcSize(stateContent);
        float rowHeight = Mathf.Max(EditorGUIUtility.singleLineHeight, dotSize);
        float rowWidth = dotSize + spacing + labelSize.x;
        Rect rowRect = GUILayoutUtility.GetRect(rowWidth, rowHeight, GUILayout.ExpandWidth(false));

        Color dotColor = GetConnectionStateColor(displayState);
        Rect dotRect = new Rect(rowRect.x, rowRect.y + (rowHeight - dotSize) * 0.5f, dotSize, dotSize);
        Handles.BeginGUI();
        var oldColor = Handles.color;
        Handles.color = dotColor;
        Handles.DrawSolidDisc(dotRect.center, Vector3.forward, dotSize * 0.5f);
        Handles.color = oldColor;
        Handles.EndGUI();

        Rect labelRect = new Rect(dotRect.xMax + spacing, rowRect.y + (rowHeight - labelSize.y) * 0.5f, labelSize.x, labelSize.y);
        GUI.Label(labelRect, stateContent, EditorStyles.miniLabel);
    }

    private static ActiveSession GetSessionForEditor(MCBEditor editor)
    {
        if (editor == null || editor.customBaseTarget == null)
        {
            return null;
        }

        return ActiveSessions.LastOrDefault(session => session != null && session.customBase == editor.customBaseTarget);
    }

    private static void UpdateConnectionState(ActiveSession session)
    {
        if (session == null)
        {
            return;
        }

        string previous = session.connectionState;
        if (string.IsNullOrWhiteSpace(session.heartbeatPath))
        {
            session.connectionState = "waiting for Blender";
        }
        else if (!File.Exists(session.heartbeatPath))
        {
            session.connectionState = "waiting for Blender";
        }
        else
        {
            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(session.heartbeatPath);
            double ageSeconds = (DateTime.UtcNow - lastWriteUtc).TotalSeconds;
            session.connectionState = ageSeconds <= BlenderHeartbeatTimeoutSeconds ? "connected" : "disconnected";
        }

        if (!string.Equals(previous, session.connectionState, StringComparison.Ordinal))
        {
            try { InternalEditorUtility.RepaintAllViews(); } catch { }
        }
    }

    private static Color GetConnectionStateColor(string state)
    {
        switch (state)
        {
            case "connected": return new Color(0.2f, 0.8f, 0.2f);
            case "disconnected": return new Color(0.9f, 0.2f, 0.2f);
            default: return EditorUIUtils.OrangeColor;
        }
    }

    private static void ReplaceTargetFbx(string sourceFbxPath, string targetUnityPath)
    {
        string targetAbsolutePath = UnityPathToAbsolute(targetUnityPath);
        if (!File.Exists(targetAbsolutePath))
        {
            throw new FileNotFoundException("Target FBX file was not found.", targetAbsolutePath);
        }

        string backupPath = targetAbsolutePath + FileManagerService.OriginalSuffix;
        if (!File.Exists(backupPath))
        {
            File.Copy(targetAbsolutePath, backupPath);
            MCBLogger.Log($"[BlenderSync] Created original FBX backup: {backupPath}");
        }

        File.Copy(sourceFbxPath, targetAbsolutePath, true);
    }

    private static string CopyModelToProjectExports(ActiveSession session, string sourceFbxPath, string targetFbxPath, int modelIndex)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.blenderExportsUnityPath))
        {
            throw new InvalidOperationException("This Blender session does not have a project export folder.");
        }

        string exportsAbsolutePath = !string.IsNullOrWhiteSpace(session.blenderExportsAbsolutePath)
            ? session.blenderExportsAbsolutePath
            : UnityPathToAbsolute(session.blenderExportsUnityPath);
        Directory.CreateDirectory(exportsAbsolutePath);

        string targetName = Path.GetFileNameWithoutExtension(targetFbxPath);
        string fileName = $"{modelIndex + 1:00}_{BlenderProjectService.SanitizeFileName(targetName)}.fbx";
        string destinationAbsolutePath = Path.Combine(exportsAbsolutePath, fileName);
        File.Copy(sourceFbxPath, destinationAbsolutePath, true);

        string exportedUnityPath = MCBUtils.CombineUnityPath(session.blenderExportsUnityPath, fileName);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        return exportedUnityPath;
    }

    private static string ResolveTargetFbxPath(ActiveSession session, BlenderExportManifest manifest, ModelInfo model)
    {
        string requested = !string.IsNullOrWhiteSpace(model?.targetFbxPath)
            ? model.targetFbxPath
            : manifest?.target?.targetFbxPath;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string unityPath = MCBUtils.ToUnityPath(requested);
            if (session.targetFbxFiles.Any(x => string.Equals(MCBUtils.ToUnityPath(x.unityPath), unityPath, StringComparison.OrdinalIgnoreCase)))
            {
                return unityPath;
            }
        }

        return session.targetFbxFiles.FirstOrDefault()?.unityPath;
    }

    private static void RefreshTargetMeshesFromFbx(ActiveSession session, string sourceFbxPath, string targetFbxPath, IEnumerable<string> meshNames)
    {
        if (session?.customBase == null || string.IsNullOrWhiteSpace(sourceFbxPath)) return;
        string targetUnityPath = MCBUtils.ToUnityPath(targetFbxPath);
        var target = session.targetFbxFiles.FirstOrDefault(file =>
            file != null && string.Equals(MCBUtils.ToUnityPath(file.unityPath), targetUnityPath, StringComparison.OrdinalIgnoreCase));
        SmrPathService.RefreshTargetMeshesFromFbx(session.customBase.transform.root, MCBUtils.ToUnityPath(sourceFbxPath), target?.smrPaths, meshNamesToRefresh: meshNames);
    }

    private static Avatar GenerateAvatarForImportedFbx(string customFbxUnityPath, string sourceMappingFbxPath, bool keepImporterConfiguredForEditing)
    {
        try
        {
            var result = AvatarDefinitionGenerationService.GenerateAvatarAsset(
                customFbxUnityPath,
                sourceMappingFbxPath,
                outputPath: null,
                applyGeneratedAvatarToFbx: !keepImporterConfiguredForEditing,
                keepImporterConfiguredForEditing: keepImporterConfiguredForEditing);
            if (result?.avatar != null)
            {
                MCBLogger.Log($"[BlenderSync] Generated Avatar '{result.avatarPath}' for '{customFbxUnityPath}'. {result.message}");
                return result.avatar;
            }
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[BlenderSync] Could not generate Avatar for '{customFbxUnityPath}': {ex.Message}");
        }

        return null;
    }

    private static void ApplyGeneratedAvatarToPreview(ActiveSession session, string customFbxUnityPath, Avatar generatedAvatar)
    {
        if (session?.customBase == null || generatedAvatar == null || string.IsNullOrWhiteSpace(customFbxUnityPath))
        {
            return;
        }

        var customFbx = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(customFbxUnityPath));
        if (customFbx == null)
        {
            return;
        }

        AvatarDefinitionGenerationService.SetRootAnimatorAvatar(session.customBase.transform.root, generatedAvatar);
    }

    private static void AssignCreatorCustomFbx(ActiveSession session, string targetFbxPath, string customFbxUnityPath, Avatar customBaseAvatar = null)
    {
        if (session?.customBase == null || string.IsNullOrWhiteSpace(customFbxUnityPath))
        {
            return;
        }

        var customFbx = AssetDatabase.LoadAssetAtPath<GameObject>(MCBUtils.ToUnityPath(customFbxUnityPath));
        if (customFbx == null)
        {
            return;
        }

        int targetIndex = session.targetFbxFiles.FindIndex(file =>
            file != null &&
            string.Equals(MCBUtils.ToUnityPath(file.unityPath), MCBUtils.ToUnityPath(targetFbxPath), StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            targetIndex = 0;
        }

        var serialized = new SerializedObject(session.customBase);
        var entriesProp = serialized.FindProperty("modelFileBuildEntries");
        if (entriesProp == null)
        {
            return;
        }

        while (entriesProp.arraySize <= targetIndex)
        {
            entriesProp.InsertArrayElementAtIndex(entriesProp.arraySize);
            var newEntry = entriesProp.GetArrayElementAtIndex(entriesProp.arraySize - 1);
            newEntry.FindPropertyRelative("customFbx").objectReferenceValue = null;
            newEntry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        }

        var entry = entriesProp.GetArrayElementAtIndex(targetIndex);
        entry.FindPropertyRelative("customFbx").objectReferenceValue = customFbx;
        if (customBaseAvatar != null)
        {
            entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = customBaseAvatar;
        }
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(session.customBase);
    }

    private static List<string> GetTargetFbxPaths(MCBEditor editor)
    {
        var selectedAsset = editor.GetSelectedAsset();
        var sourceFbxPaths = selectedAsset?.sourceFiles?
            .Where(file => file != null
                           && string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(file.path)
                           && AssetImporter.GetAtPath(MCBUtils.ToUnityPath(file.path)) is ModelImporter)
            .Select(file => MCBUtils.ToUnityPath(file.path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        if (sourceFbxPaths.Count > 0)
        {
            return sourceFbxPaths;
        }

        var detected = editor.GetDetectedAvatarFbxPaths()
            .Where(path => !string.IsNullOrWhiteSpace(path) && AssetImporter.GetAtPath(path) is ModelImporter)
            .Select(MCBUtils.ToUnityPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return detected;
    }

    private static List<string> GetFbxMeshNames(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath))
        {
            return new List<string>();
        }

        return AssetDatabase.LoadAllAssetsAtPath(MCBUtils.ToUnityPath(unityPath))
            .OfType<Mesh>()
            .Where(mesh => mesh != null && !string.IsNullOrWhiteSpace(mesh.name))
            .Select(mesh => mesh.name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static List<RendererMaterialInfo> CollectRendererMaterials(Transform avatarRoot, string targetUnityPath, List<ModelFileSmrPathData> smrPaths)
    {
        var result = new List<RendererMaterialInfo>();
        if (avatarRoot == null || string.IsNullOrWhiteSpace(targetUnityPath))
        {
            return result;
        }

        var renderers = new List<SkinnedMeshRenderer>();
        var seen = new HashSet<int>();
        foreach (var entry in smrPaths ?? new List<ModelFileSmrPathData>())
        {
            var transform = FindAvatarTransformByRelativePath(avatarRoot, entry?.avatarPath);
            var smr = transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
            if (smr == null || !seen.Add(smr.GetInstanceID()))
            {
                continue;
            }

            renderers.Add(smr);
        }

        if (renderers.Count == 0)
        {
            string normalizedTargetPath = MCBUtils.ToUnityPath(targetUnityPath);
            foreach (var smr in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                string meshAssetPath = MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(smr.sharedMesh));
                if (!string.Equals(meshAssetPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(smr.GetInstanceID()))
                {
                    renderers.Add(smr);
                }
            }
        }

        foreach (var smr in renderers)
        {
            var materials = smr.sharedMaterials;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                var material = materials[slot];
                if (material == null)
                {
                    continue;
                }

                result.Add(CreateRendererMaterialInfo(smr, material, slot));
            }
        }

        return result;
    }

    private static RendererMaterialInfo CreateRendererMaterialInfo(SkinnedMeshRenderer smr, Material material, int slot)
    {
        float smoothness = GetMaterialFloat(material, 0.5f, "_Smoothness", "_Glossiness");
        float roughness = GetMaterialFloat(material, 1.0f - smoothness, "_Roughness");
        Color baseColor = GetMaterialColor(material, Color.white, "_BaseColor", "_Color");

        return new RendererMaterialInfo
        {
            rendererName = smr != null ? smr.transform.name : "",
            meshName = smr != null && smr.sharedMesh != null ? smr.sharedMesh.name : "",
            slot = slot,
            materialName = material.name,
            baseColor = new ColorInfo { r = baseColor.r, g = baseColor.g, b = baseColor.b, a = baseColor.a },
            metallic = GetMaterialFloat(material, 0.0f, "_Metallic"),
            smoothness = smoothness,
            roughness = Mathf.Clamp01(roughness),
            baseColorTexture = GetMaterialTextureInfo(material, "_BaseMap", "_MainTex", "_BaseColorMap"),
            metallicTexture = GetMaterialTextureInfo(material, "_MetallicGlossMap", "_MetallicMap", "_MetallicTex"),
            smoothnessTexture = GetMaterialTextureInfo(material, "_SpecGlossMap", "_MetallicGlossMap", "_SmoothnessMap"),
            roughnessTexture = GetMaterialTextureInfo(material, "_RoughnessMap", "_MaskMap"),
            normalTexture = GetMaterialTextureInfo(material, "_BumpMap", "_NormalMap")
        };
    }

    private static TextureInfo GetMaterialTextureInfo(Material material, params string[] propertyNames)
    {
        if (material == null || propertyNames == null)
        {
            return null;
        }

        foreach (string propertyName in propertyNames)
        {
            if (string.IsNullOrWhiteSpace(propertyName) || !material.HasProperty(propertyName))
            {
                continue;
            }

            var texture = material.GetTexture(propertyName);
            if (texture == null)
            {
                continue;
            }

            string unityPath = AssetDatabase.GetAssetPath(texture);
            return new TextureInfo
            {
                unityPath = unityPath,
                absolutePath = string.IsNullOrWhiteSpace(unityPath) ? "" : UnityPathToAbsolute(unityPath),
                name = texture.name
            };
        }

        return null;
    }

    private static float GetMaterialFloat(Material material, float fallback, params string[] propertyNames)
    {
        if (material == null || propertyNames == null)
        {
            return fallback;
        }

        foreach (string propertyName in propertyNames)
        {
            if (!string.IsNullOrWhiteSpace(propertyName) && material.HasProperty(propertyName))
            {
                return material.GetFloat(propertyName);
            }
        }

        return fallback;
    }

    private static Color GetMaterialColor(Material material, Color fallback, params string[] propertyNames)
    {
        if (material == null || propertyNames == null)
        {
            return fallback;
        }

        foreach (string propertyName in propertyNames)
        {
            if (!string.IsNullOrWhiteSpace(propertyName) && material.HasProperty(propertyName))
            {
                return material.GetColor(propertyName);
            }
        }

        return fallback;
    }

    private static Transform FindAvatarTransformByRelativePath(Transform root, string relativePath)
    {
        if (root == null) return null;
        if (string.IsNullOrWhiteSpace(relativePath)) return root;

        Transform current = root;
        foreach (string rawSegment in relativePath.Split('/'))
        {
            string segment = rawSegment.Trim();
            if (string.IsNullOrEmpty(segment)) continue;
            current = current.Find(segment);
            if (current == null) return null;
        }

        return current;
    }

    private static void WriteSessionFile(ActiveSession session)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.inboxPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(session.inboxPath);
            var persisted = new PersistedSession
            {
                sessionId = session.sessionId,
                token = session.token,
                inboxPath = session.inboxPath,
                heartbeatPath = session.heartbeatPath,
                customBaseName = session.customBaseName,
                customBaseGlobalId = session.customBaseGlobalId,
                targetFbxFiles = session.targetFbxFiles,
                useProjectExports = session.useProjectExports,
                blenderProjectId = session.blenderProjectId,
                blenderProjectUnityPath = session.blenderProjectUnityPath,
                blenderProjectAbsolutePath = session.blenderProjectAbsolutePath,
                blenderExportsUnityPath = session.blenderExportsUnityPath,
                blenderExportsAbsolutePath = session.blenderExportsAbsolutePath
            };
            string path = Path.Combine(session.inboxPath, "session.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(persisted, Formatting.Indented));
            MCBLogger.Log($"[BlenderSync] Wrote sync session file: {path}");
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning($"[BlenderSync] Failed to persist sync session: {ex.Message}");
        }
    }

    private static void RestorePersistedSessionsIfNeeded()
    {
        if (sessionsRestored)
        {
            return;
        }

        sessionsRestored = true;
        string root = Path.Combine(GetProjectRoot(), "Library", "MCB", "BlenderSync");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string sessionFile in Directory.GetFiles(root, "session.json", SearchOption.AllDirectories))
        {
            try
            {
                if (IsPersistedSessionExpired(sessionFile))
                {
                    continue;
                }

                var persisted = JsonConvert.DeserializeObject<PersistedSession>(File.ReadAllText(sessionFile));
                if (persisted == null || string.IsNullOrWhiteSpace(persisted.sessionId) || string.IsNullOrWhiteSpace(persisted.token))
                {
                    continue;
                }

                if (ActiveSessions.Any(session => session != null && string.Equals(session.sessionId, persisted.sessionId, StringComparison.Ordinal)))
                {
                    continue;
                }

                var active = new ActiveSession
                {
                    sessionId = persisted.sessionId,
                    token = persisted.token,
                    inboxPath = string.IsNullOrWhiteSpace(persisted.inboxPath) ? Path.GetDirectoryName(sessionFile) : persisted.inboxPath,
                    heartbeatPath = persisted.heartbeatPath,
                    customBaseName = persisted.customBaseName,
                    customBaseGlobalId = persisted.customBaseGlobalId,
                    targetFbxFiles = persisted.targetFbxFiles ?? new List<TargetFbxInfo>(),
                    useProjectExports = persisted.useProjectExports,
                    blenderProjectId = persisted.blenderProjectId,
                    blenderProjectUnityPath = persisted.blenderProjectUnityPath,
                    blenderProjectAbsolutePath = persisted.blenderProjectAbsolutePath,
                    blenderExportsUnityPath = persisted.blenderExportsUnityPath,
                    blenderExportsAbsolutePath = persisted.blenderExportsAbsolutePath
                };
                ResolveCustomBase(active);
                ActiveSessions.Add(active);
                MCBLogger.Log($"[BlenderSync] Restored sync session {active.sessionId} from {sessionFile}");
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[BlenderSync] Failed to restore session file '{sessionFile}': {ex.Message}");
            }
        }
    }

    private static bool IsPersistedSessionExpired(string sessionFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionFile) || !File.Exists(sessionFile))
            {
                return true;
            }

            DateTime newestWrite = File.GetLastWriteTimeUtc(sessionFile);
            string sessionDir = Path.GetDirectoryName(sessionFile);
            if (!string.IsNullOrWhiteSpace(sessionDir))
            {
                string heartbeatPath = Path.Combine(sessionDir, "blender_heartbeat.json");
                if (File.Exists(heartbeatPath))
                {
                    DateTime heartbeatWrite = File.GetLastWriteTimeUtc(heartbeatPath);
                    if (heartbeatWrite > newestWrite)
                    {
                        newestWrite = heartbeatWrite;
                    }
                }
            }

            return (DateTime.UtcNow - newestWrite).TotalDays > PersistedSessionMaxAgeDays;
        }
        catch
        {
            return false;
        }
    }

    private static void ResolveCustomBase(ActiveSession session)
    {
        if (session == null || session.customBase != null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(session.customBaseGlobalId) &&
            GlobalObjectId.TryParse(session.customBaseGlobalId, out var globalId))
        {
            session.customBase = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as MyCustomBase;
        }

        if (session.customBase == null && !string.IsNullOrWhiteSpace(session.customBaseName))
        {
            session.customBase = UnityEngine.Object.FindObjectsOfType<MyCustomBase>(true)
                .FirstOrDefault(customBase => customBase != null && string.Equals(customBase.name, session.customBaseName, StringComparison.Ordinal));
        }
    }

    private static List<ModelFileSmrPathData> GetSelectedAssetSmrPaths(AvatarDiscoveredAsset selectedAsset, string unityPath)
    {
        if (selectedAsset?.sourceFiles == null || string.IsNullOrWhiteSpace(unityPath))
        {
            return new List<ModelFileSmrPathData>();
        }

        var source = selectedAsset.sourceFiles.FirstOrDefault(file =>
            file != null &&
            string.Equals(file.type, "FBX", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.role, "SOURCE", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MCBUtils.ToUnityPath(file.path), MCBUtils.ToUnityPath(unityPath), StringComparison.OrdinalIgnoreCase));

        return source?.smrPaths ?? new List<ModelFileSmrPathData>();
    }

    private static List<ModelFileSmrPathData> MergeSmrPaths(params IEnumerable<ModelFileSmrPathData>[] sources)
    {
        var result = new List<ModelFileSmrPathData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources ?? new IEnumerable<ModelFileSmrPathData>[0])
        {
            foreach (var entry in source ?? Enumerable.Empty<ModelFileSmrPathData>())
            {
                if (entry == null)
                {
                    continue;
                }

                string key = $"{entry.avatarPath}|{entry.fbxMeshPath}|{entry.meshName}|{entry.rendererName}";
                if (seen.Add(key))
                {
                    result.Add(entry);
                }
            }
        }

        return result;
    }

    private static string ResolveCurrentBannerPath(MCBEditor editor)
    {
        string selectedBannerPath = AvatarAssetDiscoveryService.GetBannerLocalPath(editor?.GetSelectedAsset());
        if (!string.IsNullOrWhiteSpace(selectedBannerPath) && File.Exists(selectedBannerPath))
        {
            return selectedBannerPath;
        }

        string packageBannerPath = Path.Combine(MCBUtils.PACKAGE_BASE_FOLDER_FULL_PATH, "Editor", "banner.png");
        return File.Exists(packageBannerPath) ? packageBannerPath : null;
    }

    private static object ResolveCurrentUser()
    {
        var auth = AuthenticationService.GetAuth();
        if (auth == null)
        {
            return null;
        }

        int userId = 0;
        int.TryParse(auth.user, out userId);

        string userName = !string.IsNullOrWhiteSpace(auth.username)
            ? auth.username
            : (!string.IsNullOrWhiteSpace(auth.user) ? auth.user : "Unknown");

        string avatarPath = null;
        if (userId > 0)
        {
            var cachedInfo = UserService.GetUserInfo(userId);
            if (cachedInfo != null && !string.IsNullOrWhiteSpace(cachedInfo.username))
            {
                userName = cachedInfo.username;
            }

            UserService.GetUserAvatar(userId);
            avatarPath = UserService.GetUserAvatarLocalPath(userId);
        }

        return new
        {
            id = userId,
            name = userName,
            avatarPath = avatarPath
        };
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    private static string UnityPathToAbsolute(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath)) return unityPath;
        if (Path.IsPathRooted(unityPath)) return Path.GetFullPath(unityPath);
        return Path.GetFullPath(Path.Combine(GetProjectRoot(), unityPath));
    }

    private static string ReadPackageVersion()
    {
        try
        {
            string packageJsonPath = Path.Combine(MCBUtils.PACKAGE_BASE_FOLDER_FULL_PATH, "package.json");
            if (!File.Exists(packageJsonPath)) return "";
            var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(packageJsonPath));
            if (data != null && data.TryGetValue("version", out object value)) return value?.ToString() ?? "";
        }
        catch
        {
            // Best-effort metadata only.
        }
        return "";
    }

    private static void SetStatus(string message, MessageType type)
    {
        lastStatus = message;
        lastStatusType = type;
        MCBLogger.Log("[BlenderSync] " + message);
    }
}
#endif
