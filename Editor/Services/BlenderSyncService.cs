#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
    private const string ExportKind = "orbiters.mcb.blenderExport";
    private const string ReadyKind = "orbiters.mcb.blenderExportReady";
    private const int ProtocolVersion = 1;
    private const double BlenderHeartbeatTimeoutSeconds = 4.0;

    private static readonly List<ActiveSession> ActiveSessions = new List<ActiveSession>();
    private static bool pollingHooked;
    private static bool sessionsRestored;
    private static string lastStatus;
    private static MessageType lastStatusType = MessageType.Info;

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
    }

    private class TargetFbxInfo
    {
        public string unityPath;
        public string absolutePath;
        public string name;
        public List<string> meshNames = new List<string>();
        public List<ModelFileSmrPathData> smrPaths = new List<ModelFileSmrPathData>();
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
        public List<string> shapeKeys;
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
            EditorGUILayout.LabelField("Blender Magic Sync", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Connects Unity MCB with the Blender MCB addon. You can click this first or start Magic Sync in Blender first; the tools exchange target FBX and renderer path data so Blender exports the right meshes and Unity refreshes the intended renderers.",
                MessageType.Info);

            var targetFbxPaths = GetTargetFbxPaths(editor);
            if (targetFbxPaths.Count == 0)
            {
                EditorGUILayout.HelpBox("No target FBX was detected for this avatar. Assign or detect the base FBX before syncing.", MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(targetFbxPaths.Count == 0))
            {
                if (GUILayout.Button("Sync with Blender", GUILayout.Height(26f)))
                {
                    StartSync(editor, targetFbxPaths);
                }
            }

            DrawBlenderConnectionState(editor);

            if (!string.IsNullOrEmpty(lastStatus))
            {
                EditorGUILayout.HelpBox(lastStatus, lastStatusType);
            }
        }
    }

    private static void StartSync(MCBEditor editor, List<string> targetFbxPaths)
    {
        bool hasBlenderOffer = TryReadBlenderOfferFromClipboard(out var blenderOffer);
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
            return new TargetFbxInfo
            {
                unityPath = unityPath,
                absolutePath = UnityPathToAbsolute(unityPath),
                name = Path.GetFileName(unityPath),
                meshNames = GetFbxMeshNames(unityPath),
                smrPaths = MergeSmrPaths(discoveredEntries, serverEntries)
            };
        }).ToList();

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
            targetFbxFiles = targetFiles,
            capabilities = new[]
            {
                "fbxReplace",
                "smrPathRefresh",
                "xmuscleMetadata"
            }
        };

        string payloadJson = JsonConvert.SerializeObject(payload, Formatting.Indented);
        EditorGUIUtility.systemCopyBuffer = payloadJson;

        ActiveSessions.RemoveAll(x => x == null || x.customBase == null);
        var activeSession = new ActiveSession
        {
            sessionId = sessionId,
            token = token,
            inboxPath = inboxPath,
            heartbeatPath = heartbeatPath,
            customBaseName = customBaseName,
            customBaseGlobalId = customBaseGlobalId,
            customBase = editor.customBaseTarget,
            targetFbxFiles = targetFiles
        };
        ActiveSessions.Add(activeSession);
        WriteSessionFile(activeSession);

        EnsurePolling();

        if (hasBlenderOffer)
        {
            WritePayloadToBlenderOffer(blenderOffer, payloadJson);
            SetStatus($"Magic Sync connected to Blender for {customBaseName}. Waiting for Blender export in:\n{inboxPath}", MessageType.Info);
        }
        else
        {
            SetStatus($"Magic Sync copied to clipboard for {customBaseName}. Waiting for Blender export in:\n{inboxPath}", MessageType.Info);
        }
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
        RestorePersistedSessionsIfNeeded();
        if (ActiveSessions.Count == 0) return;

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
                if (File.Exists(readyPath + ".processed")) continue;

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
                }
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

        var replacedTargets = new List<string>();
        foreach (var model in models)
        {
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

            ReplaceTargetFbx(sourceFbxPath, targetFbxPath);
            AssetDatabase.ImportAsset(targetFbxPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            RefreshTargetMeshesFromFbx(session, targetFbxPath);
            replacedTargets.Add(targetFbxPath);
            MCBLogger.Log($"[BlenderSync] Imported Blender export. source={sourceFbxPath} target={targetFbxPath}");
        }

        if (replacedTargets.Count == 0)
        {
            throw new InvalidOperationException("Blender export manifest did not contain any usable CUSTOM_BASE model paths.");
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        if (session.customBase != null)
        {
            EditorUtility.SetDirty(session.customBase);
        }

        SetStatus($"Blender export imported for {session.customBaseName}.\nReplaced {replacedTargets.Count} target FBX file(s).", MessageType.Info);
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

    private static void RefreshTargetMeshesFromFbx(ActiveSession session, string targetFbxPath)
    {
        if (session?.customBase == null || string.IsNullOrWhiteSpace(targetFbxPath)) return;
        string unityPath = MCBUtils.ToUnityPath(targetFbxPath);
        var target = session.targetFbxFiles.FirstOrDefault(file =>
            file != null && string.Equals(MCBUtils.ToUnityPath(file.unityPath), unityPath, StringComparison.OrdinalIgnoreCase));
        SmrPathService.RefreshTargetMeshesFromFbx(session.customBase.transform.root, unityPath, target?.smrPaths);
    }

    private static List<string> GetTargetFbxPaths(MCBEditor editor)
    {
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
                targetFbxFiles = session.targetFbxFiles
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
                    targetFbxFiles = persisted.targetFbxFiles ?? new List<TargetFbxInfo>()
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
