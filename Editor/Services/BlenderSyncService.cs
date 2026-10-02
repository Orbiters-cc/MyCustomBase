#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Processes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

public static class BlenderSyncService
{
    private const string MagicSyncKind = "orbiters.mcb.magicSync";
    private const string BlenderOfferKind = "orbiters.mcb.blenderMagicSyncOffer";
    private const string BlenderLaunchKind = "orbiters.mcb.blenderLaunch";
    private const string ExportKind = "orbiters.mcb.blenderExport";
    private const string ReadyKind = "orbiters.mcb.blenderExportReady";
    private const string HeartbeatKind = "orbiters.mcb.blenderHeartbeat";
    // Every payload exchanged with the Blender extension uses this version, and only this one is accepted.
    private const int ProtocolVersion = 2;
    private const double BlenderHeartbeatTimeoutSeconds = 4.0;
    private const double PollIntervalSeconds = 0.5;
    private const double PersistedSessionMaxAgeDays = 2.0;
    private const string AdvancedBlenderExportFolder = "Assets/MCB/generated/blenderAdvancedExports~";
    private static readonly List<ActiveSession> ActiveSessions = new List<ActiveSession>();
    private static bool pollingHooked;
    private static bool sessionsRestored;
    private static double nextPollTime;
    private static string lastStatus;
    private static MessageType lastStatusType = MessageType.Info;
    private static Texture2D blenderIcon;
    private static readonly List<PreparationCompletion> PendingPreparationCompletions = new List<PreparationCompletion>();
    private static readonly HashSet<string> PreparingProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WaitingExportsReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        public bool useAdvancedMeshBlenderLink;
        public BlenderEnvironment blenderEnvironment;
        public DateTime heartbeatReadWriteUtc;
        public BlenderLiveSession live;
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
        public bool useAdvancedMeshBlenderLink;
        // The live link listens on this port again after a script reload (when it is still free).
        public int livePort;
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

    /// <summary>What the connected Blender reports in its heartbeat.</summary>
    public class BlenderEnvironment
    {
        public string blenderVersion;
        /// <summary>Version of the MCB Blender extension (see <see cref="BlenderAddonService.BlenderAddonVersion"/>).</summary>
        public string addonVersion;
        /// <summary>The X-Muscle System extension (xmusclesystem).</summary>
        public BlenderAddonStatus xmuscles;
        /// <summary>XMuscle Orbit Helper (see <see cref="BlenderAddonService.XMuscleToolkitVersion"/>).</summary>
        public BlenderAddonStatus xmuscleToolkit;
    }

    public class BlenderAddonStatus
    {
        public bool installed;
        public bool enabled;
        public string version;
    }

    private class HeartbeatPayload
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
    }

    private class ReadyPayload
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
        public string manifestPath;
    }

    internal class BlenderExportManifest
    {
        public string kind;
        public int protocolVersion;
        public string sessionId;
        public string token;
        public TargetInfo target;
        public List<ModelInfo> models;
        public bool advancedMeshBlenderLink;
        /// <summary>XMuscle correctives of the exported meshes; null when Blender has no XMuscle Orbit Helper.</summary>
        public MuscleCorrectiveSet xmuscle;
    }

    internal class TargetInfo
    {
        public string targetFbxPath;
        public string customBaseName;
    }

    internal class ModelInfo
    {
        public string role;
        public string path;
        public string targetFbxPath;
        public string transportFormat;
        public string unityImportMode;
        public string payloadSourceFormat;
        public List<string> meshNames;
        /// <summary>Shape key names by Blender object name (the FBX node name of each exported mesh).</summary>
        public Dictionary<string, List<string>> shapeKeysByMesh;
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

    public static void BuildCreatorModeSectionUIToolkit(VisualElement root, MCBEditor editor, Action refresh)
    {
        if (root == null || editor == null || editor.customBaseTarget == null)
        {
            return;
        }

        var panel = new VisualElement();
        panel.AddToClassList("mcb-form-card");
        panel.AddToClassList("mcb-creator-panel");
        panel.AddToClassList("mcb-blender");
        root.Add(panel);

        BuildConnectorHeaderUIToolkit(panel);
        panel.Add(CreateHelpBox(
            "Connects Unity MCB with the Blender MCB addon. You can click this first or start Magic Sync in Blender first; the tools exchange target FBX and renderer path data so Blender exports the right meshes and Unity refreshes the intended renderers.",
            HelpBoxMessageType.Info));

        var targetFbxPaths = GetTargetFbxPaths(editor);
        if (targetFbxPaths.Count == 0)
        {
            panel.Add(CreateHelpBox("No target FBX was detected for this avatar. Assign or detect the base FBX before syncing.", HelpBoxMessageType.Warning));
        }

        string blenderPath = BuildBlenderInstallationSelectorUIToolkit(panel, refresh);
        if (string.IsNullOrWhiteSpace(blenderPath))
        {
            panel.Add(CreateHelpBox("Blender was not detected. Use Advanced Mode to browse to the Blender executable.", HelpBoxMessageType.Warning));
        }

        var session = GetSessionForEditor(editor);
        if (session != null)
        {
            UpdateConnectionState(session);
        }

        bool isConnected = session != null && string.Equals(session.connectionState, "connected", StringComparison.Ordinal);
        bool isPreparing = IsEditorProjectPreparing(editor);
        var buttonRow = CreateRow();
        buttonRow.AddToClassList("mcb-blender__actions");

        var modifyButton = CreateButton("Modify with Blender", () =>
        {
            OpenWithBlender(editor, targetFbxPaths);
            refresh?.Invoke();
        });
        modifyButton.SetEnabled(targetFbxPaths.Count > 0 && !string.IsNullOrWhiteSpace(blenderPath) && !isConnected && !isPreparing);
        modifyButton.schedule.Execute(() =>
        {
            var current = GetSessionForEditor(editor);
            UpdateConnectionState(current);
            modifyButton.SetEnabled(targetFbxPaths.Count > 0 && !string.IsNullOrWhiteSpace(blenderPath) &&
                current?.connectionState != "connected" && !IsEditorProjectPreparing(editor));
        }).Every(500);
        buttonRow.Add(modifyButton);

        var syncButton = CreateButton("Sync with Blender", () =>
        {
            StartSync(editor, targetFbxPaths);
            refresh?.Invoke();
        });
        syncButton.SetEnabled(targetFbxPaths.Count > 0);
        buttonRow.Add(syncButton);
        panel.Add(buttonRow);

        BuildBlenderConnectionStateUIToolkit(panel, editor);
        BuildBlenderLiveUIToolkit(panel, editor);

        if (!string.IsNullOrEmpty(lastStatus))
        {
            panel.Add(CreateHelpBox(lastStatus, ToHelpBoxMessageType(lastStatusType)));
        }
    }

    // Live preview: Blender's edits show on the avatar until Commit (Blender exports as when the file is saved) or Revert.
    private static void BuildBlenderLiveUIToolkit(VisualElement root, MCBEditor editor)
    {
        var section = new VisualElement();
        section.AddToClassList("mcb-blender__live");
        var header = CreateRow();
        header.AddToClassList("mcb-blender__state");
        var dot = new VisualElement();
        dot.AddToClassList("mcb-blender__state-dot");
        header.Add(dot);
        header.Add(CreateLabel("Live preview", 11, FontStyle.Bold, new Color(0.82f, 0.82f, 0.82f)));
        var summary = CreateLabel("", 11, FontStyle.Normal, new Color(0.62f, 0.62f, 0.62f));
        summary.AddToClassList("mcb-blender__live-summary");
        header.Add(summary);
        section.Add(header);

        var meshList = new VisualElement();
        meshList.AddToClassList("mcb-blender__live-meshes");
        section.Add(meshList);

        var actions = CreateRow();
        actions.AddToClassList("mcb-blender__actions");
        Button commit = null;
        Button revert = null;
        string shownMeshes = null;

        void Refresh()
        {
            var live = GetSessionForEditor(editor)?.live;
            section.style.display = live == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (live == null) return;

            var states = live.MeshStates.ToList();
            bool anyLive = states.Any(state => state.live);
            dot.style.backgroundColor = anyLive ? new Color(0.2f, 0.8f, 0.2f) : live.Connected ? EditorUIUtils.OrangeColor : new Color(0.45f, 0.45f, 0.45f);
            summary.text = live.Committing ? "Blender is exporting the edits..."
                : !live.Connected ? "waiting for Blender"
                : anyLive ? "showing Blender's edits, nothing saved yet"
                : "edit a mesh in Blender to see it here";

            string meshes = string.Join("\n", states.Select(state => (state.live ? "1" : "0") + state.meshId + ": " + state.message));
            if (meshes != shownMeshes)
            {
                shownMeshes = meshes;
                meshList.Clear();
                foreach (var state in states)
                {
                    var line = CreateLabel(state.meshId + ": " + state.message, 11, FontStyle.Normal, state.live ? new Color(0.5f, 0.85f, 0.6f) : new Color(0.66f, 0.66f, 0.66f));
                    line.AddToClassList("mcb-blender__live-mesh");
                    meshList.Add(line);
                }
            }

            commit.text = live.Committing ? "Committing..." : "Commit";
            commit.SetEnabled(live.Connected && !live.Committing && (live.HasLiveMeshes || live.NeedsCommit));
            revert.SetEnabled(live.HasLiveMeshes && !live.Committing);
        }

        commit = CreatePressButton("Commit", "Blender exports these edits as when the file is saved, and Unity applies the export", () =>
        {
            var live = GetSessionForEditor(editor)?.live;
            if (live == null) return;
            commit.text = "Committing...";
            commit.SetEnabled(false);
            if (!live.Commit()) SetStatus("Blender is not connected to the live preview: save or sync in Blender to export the edits.", MessageType.Warning);
            Refresh();
        });
        revert = CreatePressButton("Revert", "Show the avatar's own meshes again (the live preview never changes assets)", () =>
        {
            GetSessionForEditor(editor)?.live?.Revert();
            Refresh();
        });
        actions.Add(commit);
        actions.Add(revert);
        section.Add(actions);

        Refresh();
        section.schedule.Execute(Refresh).Every(300);
        root.Add(section);
    }

    // Acts on pointer down so the press shows at once; a click (keyboard, or a press the pointer callback missed) still works.
    private static Button CreatePressButton(string text, string tooltip, Action action)
    {
        var button = CreateButton(text, null);
        button.tooltip = tooltip;
        bool pressed = false;
        void Activate()
        {
            if (pressed || !button.enabledInHierarchy) return;
            pressed = true;
            button.AddToClassList("is-pressed");
            try
            {
                action();
            }
            finally
            {
                button.schedule.Execute(() =>
                {
                    pressed = false;
                    button.RemoveFromClassList("is-pressed");
                }).StartingIn(150);
            }
        }

        button.clicked += Activate;
        button.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0) return;
            Activate();
            evt.StopImmediatePropagation();
            evt.PreventDefault();
        }, TrickleDown.TrickleDown);
        return button;
    }

    private static void BuildConnectorHeaderUIToolkit(VisualElement root)
    {
        if (blenderIcon == null)
        {
            blenderIcon = LoadBlenderIcon();
        }

        var row = CreateRow();
        row.AddToClassList("mcb-blender__header");
        if (blenderIcon != null)
        {
            var icon = new Image { image = blenderIcon, scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("mcb-blender__icon");
            row.Add(icon);
        }

        var label = CreateLabel("Blender connector", 13, FontStyle.Bold, Color.white);
        row.Add(label);
        root.Add(row);
    }

    private static string BuildBlenderInstallationSelectorUIToolkit(VisualElement root, Action refresh)
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

        var options = installations.Select(item => item.DisplayLabel).ToList();
        var dropdown = new DropdownField("Blender installation", options, selectedIndex);
        dropdown.AddToClassList("mcb-dropdown");
        dropdown.AddToClassList("mcb-blender__dropdown");
        dropdown.RegisterValueChangedCallback(evt =>
        {
            int nextIndex = options.IndexOf(evt.newValue);
            if (nextIndex >= 0 && nextIndex < installations.Count)
            {
                BlenderInstallService.SetSelectedExecutablePath(installations[nextIndex].executablePath);
                refresh?.Invoke();
            }
        });
        root.Add(dropdown);
        return selectedPath;
    }

    private static void BuildBlenderConnectionStateUIToolkit(VisualElement root, MCBEditor editor)
    {
        var session = GetSessionForEditor(editor);
        if (session == null)
        {
            return;
        }

        UpdateConnectionState(session);
        string displayState = string.IsNullOrWhiteSpace(session.connectionState) ? "waiting for Blender" : session.connectionState;
        var row = CreateRow();
        row.AddToClassList("mcb-blender__state");

        var dot = new VisualElement();
        dot.AddToClassList("mcb-blender__state-dot");
        dot.style.backgroundColor = GetConnectionStateColor(displayState);
        row.Add(dot);

        var stateLabel = CreateLabel(displayState, 11, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
        row.Add(stateLabel);
        // RepaintAllViews does not rebuild retained UI Toolkit elements.
        row.schedule.Execute(() =>
        {
            var current = GetSessionForEditor(editor);
            UpdateConnectionState(current);
            string state = current?.connectionState ?? "waiting for Blender";
            stateLabel.text = state;
            dot.style.backgroundColor = GetConnectionStateColor(state);
        }).Every(500);
        root.Add(row);
    }

    private static VisualElement CreateRow()
    {
        var row = new VisualElement();
        row.AddToClassList("mcb-row");
        return row;
    }

    private static Label CreateLabel(string text, int fontSize, FontStyle fontStyle, Color color)
    {
        var label = new Label(text ?? string.Empty);
        label.AddToClassList("mcb-label");
        label.style.fontSize = fontSize;
        label.style.unityFontStyleAndWeight = fontStyle;
        label.style.color = color;
        label.style.unityTextAlign = TextAnchor.MiddleLeft;
        return label;
    }

    private static Button CreateButton(string text, Action onClick)
    {
        var button = new Button(onClick) { text = text ?? string.Empty };
        button.AddToClassList("mcb-button");
        return button;
    }

    private static HelpBox CreateHelpBox(string message, HelpBoxMessageType messageType)
    {
        var helpBox = new HelpBox(message ?? string.Empty, messageType);
        helpBox.AddToClassList("mcb-creator-helpbox");
        return helpBox;
    }

    private static HelpBoxMessageType ToHelpBoxMessageType(MessageType messageType)
    {
        switch (messageType)
        {
            case MessageType.Error:
                return HelpBoxMessageType.Error;
            case MessageType.Warning:
                return HelpBoxMessageType.Warning;
            case MessageType.Info:
                return HelpBoxMessageType.Info;
            default:
                return HelpBoxMessageType.None;
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
        RestorePersistedSessionsIfNeeded();
        var existing = GetSessionForEditor(editor);
        var project = existing != null && existing.useProjectExports
            ? new BlenderProjectInfo
            {
                projectId = existing.blenderProjectId,
                projectUnityPath = existing.blenderProjectUnityPath,
                projectAbsolutePath = existing.blenderProjectAbsolutePath,
                exportsUnityPath = existing.blenderExportsUnityPath,
                exportsAbsolutePath = existing.blenderExportsAbsolutePath
            }
            : null;
        var result = CreateSyncSession(editor, targetFbxPaths, project, existing);
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

    private static SyncSessionBuildResult CreateSyncSession(MCBEditor editor, List<string> targetFbxPaths, BlenderProjectInfo projectInfo, ActiveSession existing = null)
    {
        string projectPath = GetProjectRoot();
        string sessionId = existing?.sessionId ?? Guid.NewGuid().ToString("N");
        string token = existing?.token ?? Guid.NewGuid().ToString("N");
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
            // Preview meshes no longer point at the original FBX. Keep the established
            // mapping when reconnecting so another export still targets those renderers.
            var previousEntries = existing?.targetFbxFiles.FirstOrDefault(file => file != null &&
                string.Equals(MCBUtils.ToUnityPath(file.unityPath), unityPath, StringComparison.OrdinalIgnoreCase))?.smrPaths;
            var mergedEntries = MergeSmrPaths(discoveredEntries, previousEntries, serverEntries);
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
        if (FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK))
        {
            capabilities.Add("nativeMeshPayload");
            capabilities.Add(NativeMeshPayloadService.PayloadFormat);
        }
        bool useAdvancedMeshBlenderLink = FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK);
        // Reconnecting keeps the session's token, and with it its live link.
        var live = existing?.live ?? StartLiveSession(sessionId, token, inboxPath, 0);

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
            advancedMeshBlenderLink = useAdvancedMeshBlenderLink,
            capabilities = capabilities.ToArray(),
            live = live != null ? new { port = live.Port, protocolVersion = (int)BlenderLiveProtocol.Version } : null
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
            blenderExportsAbsolutePath = projectInfo != null ? projectInfo.exportsAbsolutePath : null,
            useAdvancedMeshBlenderLink = useAdvancedMeshBlenderLink,
            live = live
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

        RestorePersistedSessionsIfNeeded();

        Predicate<ActiveSession> replaced = x =>
            x == null ||
            x.customBase == null ||
            (activeSession.customBase != null && x.customBase == activeSession.customBase);
        foreach (var previous in ActiveSessions.Where(x => replaced(x) && x?.live != null && x.live != activeSession.live))
        {
            previous.live.Dispose();
        }
        ActiveSessions.RemoveAll(replaced);
        ActiveSessions.Add(activeSession);
        WriteSessionFile(activeSession);
        EnsurePolling();
    }

    private static BlenderLiveSession StartLiveSession(string sessionId, string token, string inboxPath, int port)
    {
        try
        {
            return new BlenderLiveSession(token, sessionId, inboxPath, meshId => ResolveLiveRenderer(sessionId, meshId), port);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException || ex is IOException || ex is UnauthorizedAccessException)
        {
            // Without the live link Blender still syncs through exports.
            MCBLogger.LogWarning("[BlenderSync] Live preview is unavailable: " + ex.Message);
            return null;
        }
    }

    // A live mesh id (the renderer Blender knows the mesh as, else its object name: see BlenderLiveProtocol) to the
    // renderer of the session's avatar, through the session's renderer mapping.
    private static SkinnedMeshRenderer ResolveLiveRenderer(string sessionId, string meshId)
    {
        var session = ActiveSessions.LastOrDefault(x => x != null && x.sessionId == sessionId);
        if (session == null || string.IsNullOrEmpty(meshId)) return null;
        ResolveCustomBase(session);
        if (session.customBase == null) return null;

        var root = session.customBase.transform.root;
        foreach (string name in new[] { meshId, StripBlenderSuffix(meshId) }.Distinct())
        {
            foreach (var entry in session.targetFbxFiles.Where(file => file != null).SelectMany(file => file.smrPaths ?? new List<ModelFileSmrPathData>()))
            {
                if (entry == null || !(string.Equals(entry.rendererName, name, StringComparison.Ordinal) ||
                                       string.Equals(LastPathSegment(entry.fbxMeshPath), name, StringComparison.Ordinal) ||
                                       string.Equals(entry.meshName, name, StringComparison.Ordinal)))
                {
                    continue;
                }

                var renderer = FindAvatarTransformByRelativePath(root, entry.avatarPath)?.GetComponent<SkinnedMeshRenderer>();
                if (renderer != null) return renderer;
            }
        }

        return null;
    }

    // Blender names a duplicate "Body.001".
    private static string StripBlenderSuffix(string name) => System.Text.RegularExpressions.Regex.Replace(name ?? "", @"\.\d{3}$", "");

    private static string LastPathSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path.Substring(slash + 1) : path;
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
            extensions = BlenderAddonService.CreateLaunchPayload()
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
            // Without --python-exit-code Blender exits with 0 when the script fails (e.g. an extension download).
            Arguments = "--background --python-exit-code 1 --python " + QuoteArgument(bootstrapScriptPath),
            WorkingDirectory = GetProjectRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Only the headless preparation job is owned by Unity; the user's interactive Blender is not.
        Task.Run(() =>
        {
            int exitCode = -1;
            try
            {
                Action<string> appendLog = line => File.AppendAllText(logPath, line + Environment.NewLine);
                var result = EditorProcessRunner.Run(startInfo, 30 * 60 * 1000, stdoutLine: appendLog, stderrLine: appendLog);
                exitCode = result.Success ? 0 : result.ExitCode == 0 ? -1 : result.ExitCode;
                if (result.TimedOut) appendLog("Blender preparation or output capture timed out.");
                if (result.Cancelled) appendLog("Blender preparation interrupted by editor reload or shutdown; retry preparation.");
            }
            catch (Exception ex) { File.AppendAllText(logPath, ex.Message + Environment.NewLine); }
            finally
            {
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
            }
        });
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
            if (offer == null || offer.protocolVersion != ProtocolVersion) return false;
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
        UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += UpdateSessionIdsForSavedScene;
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
            session?.live?.Tick();

            // Leave exports pending while their scene is closed.
            if (session?.customBase == null)
            {
                ReportExportWaitingForScene(session);
                continue;
            }

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
                if (File.Exists(readyPath + ".processed") || IsFailedExportUnchanged(readyPath)) continue;

                try
                {
                    MCBLogger.Log($"[BlenderSync] Detected Blender export marker: {readyPath}");
                    ProcessReadyFile(session, readyPath);
                }
                catch (Exception ex)
                {
                    SetStatus("Blender sync import failed: " + ex.Message + "\nThe avatar was not changed. Fix the export files or export again from Blender to retry.", MessageType.Error);
                    MCBLogger.LogError($"[BlenderSync] Import failed for {readyPath}: {ex}");
                    MarkReadyFileFailed(readyPath, ex);
                    continue;
                }
                finally
                {
                    session.live?.EndExportApply();
                }

                MarkReadyFileProcessed(readyPath);
            }
        }
    }

    private static void MarkReadyFileProcessed(string readyPath)
    {
        try
        {
            if (File.Exists(readyPath + ".failed")) File.Delete(readyPath + ".failed");
            File.Move(readyPath, readyPath + ".processed");
        }
        catch (Exception ex)
        {
            // The marker alone keeps an applied export from being applied again.
            try { File.WriteAllText(readyPath + ".processed", ex.ToString()); } catch { }
        }
    }

    // ready.json stays in place with the export's fingerprint: the export is retried once its files change.
    private static void MarkReadyFileFailed(string readyPath, Exception ex)
    {
        try
        {
            File.WriteAllText(readyPath + ".failed", GetExportFingerprint(readyPath) + "\n\n" + ex);
        }
        catch (Exception writeEx)
        {
            MCBLogger.LogWarning($"[BlenderSync] Could not record the failed Blender export {readyPath}: {writeEx.Message}");
        }
    }

    private static bool IsFailedExportUnchanged(string readyPath)
    {
        string failedPath = readyPath + ".failed";
        if (!File.Exists(failedPath)) return false;
        try
        {
            using (var reader = new StreamReader(failedPath))
            {
                return string.Equals(reader.ReadLine(), GetExportFingerprint(readyPath), StringComparison.Ordinal);
            }
        }
        catch
        {
            return true;
        }
    }

    private static string GetExportFingerprint(string readyPath)
    {
        string exportDir = Path.GetDirectoryName(readyPath);
        var builder = new System.Text.StringBuilder();
        foreach (string path in Directory.GetFiles(exportDir, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".failed", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".processed", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(path);
            builder.Append(path, exportDir.Length, path.Length - exportDir.Length).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
        }

        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-", "");
        }
    }

    private static void ProcessReadyFile(ActiveSession session, string readyPath)
    {
        var ready = JsonConvert.DeserializeObject<ReadyPayload>(File.ReadAllText(readyPath));
        if (ready == null || ready.kind != ReadyKind)
        {
            throw new InvalidOperationException("ready.json is not a Blender MCB export marker.");
        }
        if (ready.protocolVersion != ProtocolVersion)
        {
            throw new InvalidOperationException(DescribeProtocolMismatch(ready.protocolVersion));
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

        var manifest = ReadExportManifest(manifestPath);
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

        ResolveCustomBase(session);
        if (session.customBase == null)
        {
            throw new InvalidOperationException("Open the scene containing this Blender session's avatar before syncing.");
        }

        // Check every model before touching the avatar, then apply them as one step: a bad second model
        // must not leave the first one applied.
        var plans = PlanModelUpdates(session, manifest, models, exportDir);
        if (plans.Count == 0)
        {
            throw new InvalidOperationException("Blender export manifest did not contain any usable CUSTOM_BASE model paths.");
        }

        // Live meshes go before the avatar's meshes are replaced (Poll maps them again to the new ones afterwards).
        session.live?.BeginExportApply();
        var updatedTargets = new List<string>();
        int generatedAvatarCount = 0;
        int advancedQueuedCount = 0;
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Apply Blender Export");
        using (var files = new ExportFileRollback())
        {
            try
            {
                foreach (var plan in plans)
                {
                    if (plan.advancedMesh)
                    {
                        string externalFbxPath = CopyModelToAdvancedIgnoredExports(session, plan.sourceFbxPath, plan.targetFbxPath, plan.modelIndex, files);
                        ApplyAdvancedMeshPreview(session, externalFbxPath, plan.targetFbxPath, plan.previewMappings);
                        AssignCreatorExternalCustomFbx(session, plan.targetFbxPath, externalFbxPath);
                        updatedTargets.Add(externalFbxPath);
                        advancedQueuedCount++;
                        MCBLogger.Log($"[BlenderSync] Applied native mesh preview and stored Blender export for submission. source={plan.sourceFbxPath} external={externalFbxPath} target={plan.targetFbxPath}");
                    }
                    else if (session.useProjectExports)
                    {
                        string exportedUnityPath = CopyModelToProjectExports(session, plan.sourceFbxPath, plan.targetFbxPath, plan.modelIndex, files);
                        AssetDatabase.ImportAsset(exportedUnityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        files.Track(AvatarDefinitionGenerationService.GetDefaultAvatarPath(exportedUnityPath));
                        Avatar generatedAvatar = GenerateAvatarForImportedFbx(exportedUnityPath, plan.targetFbxPath, keepImporterConfiguredForEditing: true);
                        if (generatedAvatar != null)
                        {
                            generatedAvatarCount++;
                        }
                        RefreshTargetMeshesFromFbx(session, exportedUnityPath, plan.targetFbxPath, plan.model.meshNames);
                        AssignCreatorCustomFbx(session, plan.targetFbxPath, exportedUnityPath, generatedAvatar);
                        ApplyGeneratedAvatarToPreview(session, exportedUnityPath, generatedAvatar);
                        updatedTargets.Add(exportedUnityPath);
                        MCBLogger.Log($"[BlenderSync] Imported Blender export. source={plan.sourceFbxPath} projectExport={exportedUnityPath} target={plan.targetFbxPath}");
                    }
                    else
                    {
                        ReplaceTargetFbx(plan.sourceFbxPath, plan.targetFbxPath, files);
                        AssetDatabase.ImportAsset(plan.targetFbxPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        files.Track(AvatarDefinitionGenerationService.GetDefaultAvatarPath(plan.targetFbxPath));
                        Avatar generatedAvatar = GenerateAvatarForImportedFbx(plan.targetFbxPath, plan.targetFbxPath, keepImporterConfiguredForEditing: false);
                        if (generatedAvatar != null)
                        {
                            generatedAvatarCount++;
                        }
                        RefreshTargetMeshesFromFbx(session, plan.targetFbxPath, plan.targetFbxPath, plan.model.meshNames);
                        ApplyGeneratedAvatarToPreview(session, plan.targetFbxPath, generatedAvatar);
                        updatedTargets.Add(plan.targetFbxPath);
                        MCBLogger.Log($"[BlenderSync] Imported Blender export. source={plan.sourceFbxPath} target={plan.targetFbxPath}");
                    }
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EditorUtility.SetDirty(session.customBase);
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch
            {
                // Runs inside one editor update, so the group holds only this export's changes.
                Undo.RevertAllDownToGroup(undoGroup);
                files.Restore();
                throw;
            }
        }

        bool usedAdvancedMeshBlenderLink = advancedQueuedCount > 0;
        string action = usedAdvancedMeshBlenderLink
            ? "Updated avatar with native meshes from"
            : (session.useProjectExports ? "Updated avatar from" : "Replaced");
        string avatarMessage = generatedAvatarCount > 0
            ? $"\nGenerated {generatedAvatarCount} Avatar asset(s)."
            : (usedAdvancedMeshBlenderLink ? "\nOriginal FBX files are unchanged; exports are ready for submission." : "\nNo Avatar asset was generated.");
        string statusVerb = usedAdvancedMeshBlenderLink ? "processed" : "imported";
        SetStatus($"Blender export {statusVerb} for {session.customBaseName}.\n{action} {updatedTargets.Count} FBX file(s).{avatarMessage}", MessageType.Info);
        LogMuscleCorrectives(manifest.xmuscle);
        // Without XMuscle Orbit Helper in Blender there is no xmuscle block: the stored correctives stay as they are.
        if (manifest.xmuscle != null)
        {
            var exportedMeshes = models
                .Where(model => model.shapeKeysByMesh != null)
                .SelectMany(model => model.shapeKeysByMesh.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            MuscleCorrectiveStore.Apply(session.customBase, manifest.xmuscle, exportedMeshes);
        }
    }

    /// <summary>Reads a Blender export manifest; throws unless it is an export of <see cref="ProtocolVersion"/>.</summary>
    internal static BlenderExportManifest ReadExportManifest(string manifestPath)
    {
        var manifest = JsonConvert.DeserializeObject<BlenderExportManifest>(File.ReadAllText(manifestPath));
        if (manifest == null || manifest.kind != ExportKind)
        {
            throw new InvalidOperationException("manifest.json is not a Blender MCB export.");
        }
        if (manifest.protocolVersion != ProtocolVersion)
        {
            throw new InvalidOperationException(DescribeProtocolMismatch(manifest.protocolVersion));
        }

        return manifest;
    }

    private static string DescribeProtocolMismatch(int blenderProtocolVersion)
    {
        string outdated = blenderProtocolVersion > ProtocolVersion
            ? "the MCB Unity package"
            : "the MCB Blender extension (Modify with Blender installs the matching one)";
        return $"Blender uses MCB sync protocol {blenderProtocolVersion} and Unity protocol {ProtocolVersion}: update {outdated}.";
    }

    private static void LogMuscleCorrectives(MuscleCorrectiveSet set)
    {
        if (set == null)
        {
            return;
        }

        MCBLogger.Log($"[BlenderSync] Blender export carries {set.muscles?.Count ?? 0} XMuscle corrective(s) (XMuscle Orbit Helper API {set.apiVersion}).");
        foreach (string warning in set.warnings ?? new List<string>())
        {
            MCBLogger.LogWarning("[BlenderSync] XMuscles: " + warning);
        }
    }

    private sealed class ModelUpdatePlan
    {
        public ModelInfo model;
        public int modelIndex;
        public string sourceFbxPath;
        public string targetFbxPath;
        public bool advancedMesh;
        public List<ModelFileSmrPathData> previewMappings;
    }

    private static List<ModelUpdatePlan> PlanModelUpdates(ActiveSession session, BlenderExportManifest manifest, List<ModelInfo> models, string exportDir)
    {
        var plans = new List<ModelUpdatePlan>();
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            var model = models[modelIndex];
            if (string.IsNullOrWhiteSpace(model.path))
            {
                continue;
            }

            string sourceFbxPath = Path.GetFullPath(Path.Combine(exportDir, model.path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(sourceFbxPath))
            {
                throw new FileNotFoundException("Blender exported FBX was not found.", sourceFbxPath);
            }

            string targetFbxPath = ResolveTargetFbxPath(session, manifest, model);
            if (string.IsNullOrWhiteSpace(targetFbxPath))
            {
                throw new InvalidOperationException("Could not resolve the target Unity FBX path.");
            }

            var plan = new ModelUpdatePlan
            {
                model = model,
                modelIndex = modelIndex,
                sourceFbxPath = sourceFbxPath,
                targetFbxPath = targetFbxPath,
                advancedMesh = ShouldUseAdvancedMeshBlenderLink(session, manifest, model)
            };
            if (plan.advancedMesh || session.useProjectExports)
            {
                if (plan.advancedMesh) plan.previewMappings = GetPreviewMappings(session, targetFbxPath, model.meshNames);
                else if (string.IsNullOrWhiteSpace(session.blenderExportsUnityPath)) throw new InvalidOperationException("This Blender session does not have a project export folder.");
                GetCreatorTargetIndex(session.customBase, targetFbxPath);
            }
            else if (!File.Exists(UnityPathToAbsolute(targetFbxPath)))
            {
                throw new FileNotFoundException("Target FBX file was not found.", UnityPathToAbsolute(targetFbxPath));
            }

            plans.Add(plan);
        }

        return plans;
    }

    // Snapshots the project files an export overwrites or creates, so a failed export can put them back.
    private sealed class ExportFileRollback : IDisposable
    {
        private readonly string folder = Path.Combine(GetProjectRoot(), "Library", "MCB", "BlenderSyncRollback", Guid.NewGuid().ToString("N"));
        private readonly List<KeyValuePair<string, string>> backups = new List<KeyValuePair<string, string>>();

        private bool restoreFailed;

        public void Track(string path)
        {
            string absolutePath = UnityPathToAbsolute(path);
            foreach (string file in new[] { absolutePath, absolutePath + ".meta" })
            {
                if (backups.Any(entry => string.Equals(entry.Key, file, StringComparison.OrdinalIgnoreCase))) continue;
                string backup = null;
                if (File.Exists(file))
                {
                    Directory.CreateDirectory(folder);
                    backup = Path.Combine(folder, backups.Count.ToString());
                    File.Copy(file, backup);
                }
                backups.Add(new KeyValuePair<string, string>(file, backup));
            }
        }

        public void Restore()
        {
            var reimport = new List<string>();
            foreach (var entry in backups)
            {
                string unityPath = MCBUtils.ToUnityPath(entry.Key);
                bool isAsset = !entry.Key.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(unityPath));
                try
                {
                    if (entry.Value != null)
                    {
                        File.Copy(entry.Value, entry.Key, true);
                        if (isAsset) reimport.Add(unityPath);
                    }
                    else if (isAsset) AssetDatabase.DeleteAsset(unityPath);
                    else if (File.Exists(entry.Key)) File.Delete(entry.Key);
                }
                catch (Exception ex)
                {
                    restoreFailed = true;
                    MCBLogger.LogError($"[BlenderSync] Could not restore '{entry.Key}' after a failed Blender export: {ex.Message}");
                }
            }

            foreach (string unityPath in reimport)
            {
                try { AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); }
                catch { restoreFailed = true; throw; }
            }
        }

        public void Dispose()
        {
            if (restoreFailed)
            {
                MCBLogger.LogError($"[BlenderSync] Some export files could not be restored. Recovery copies were retained at '{folder}'.");
                return;
            }
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) { MCBLogger.LogWarning($"[BlenderSync] Could not delete Blender export backups '{folder}': {ex.Message}"); }
        }
    }

    private static bool ShouldUseAdvancedMeshBlenderLink(ActiveSession session, BlenderExportManifest manifest, ModelInfo model)
    {
        if (!FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_MESH_ON_BLENDER_LINK))
        {
            return false;
        }

        if (session?.useAdvancedMeshBlenderLink == true || manifest?.advancedMeshBlenderLink == true)
        {
            return true;
        }

        return model != null &&
               (string.Equals(model.unityImportMode, "nativeMeshPayload", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.transportFormat, NativeMeshPayloadService.PayloadFormat, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ModelFileSmrPathData> GetPreviewMappings(ActiveSession session, string targetFbxPath, IEnumerable<string> meshNames)
    {
        var target = session.targetFbxFiles.FirstOrDefault(file => file != null &&
            string.Equals(MCBUtils.ToUnityPath(file.unityPath), MCBUtils.ToUnityPath(targetFbxPath), StringComparison.OrdinalIgnoreCase));
        var names = new HashSet<string>(meshNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var mappings = (target?.smrPaths ?? new List<ModelFileSmrPathData>())
            .Where(entry => entry != null && (names.Count == 0 || names.Contains(entry.meshName) || names.Contains(entry.rendererName)))
            .ToList();
        if (mappings.Count == 0)
            throw new InvalidOperationException($"No avatar renderer mapping matches the Blender export for '{targetFbxPath}'. Reconnect the intended avatar and sync again.");
        return mappings;
    }

    private static void ApplyAdvancedMeshPreview(ActiveSession session, string externalFbxPath, string targetFbxPath, List<ModelFileSmrPathData> mappings)
    {
        string temporaryPath = null;
        try
        {
            var imported = NativeMeshPayloadService.ImportExternalFbxForPayload(externalFbxPath, out temporaryPath);
            NativeMeshPayloadService.ApplyModelPreview(session.customBase.transform.root, imported,
                MCBUtils.ToUnityPath(targetFbxPath), mappings,
                MCBUtils.CombineUnityPath(MCBUtils.ASSETS_BASE_FOLDER, "generated", "blenderPreviews", session.sessionId));
        }
        finally
        {
            NativeMeshPayloadService.DeleteTemporaryImportedFbx(temporaryPath);
        }
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

    /// <summary>
    /// What the Blender of the editor's sync session last reported (versions, X-Muscle System and XMuscle Orbit Helper),
    /// or null until a heartbeat of this session arrived. Combine with the connection state for whether it is current.
    /// </summary>
    public static BlenderEnvironment GetBlenderEnvironment(MCBEditor editor)
    {
        var session = GetSessionForEditor(editor);
        UpdateConnectionState(session);
        return session?.blenderEnvironment;
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
            ReadHeartbeat(session, lastWriteUtc);
        }

        if (!string.Equals(previous, session.connectionState, StringComparison.Ordinal))
        {
            try { InternalEditorUtility.RepaintAllViews(); } catch { }
        }
    }

    // Reads the heartbeat each time Blender rewrites it; only a heartbeat of this session and protocol counts.
    private static void ReadHeartbeat(ActiveSession session, DateTime lastWriteUtc)
    {
        if (lastWriteUtc == session.heartbeatReadWriteUtc)
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(session.heartbeatPath);
        }
        catch (IOException)
        {
            return; // Blender is replacing the file: read it on the next poll.
        }

        session.heartbeatReadWriteUtc = lastWriteUtc;
        try
        {
            var heartbeat = JsonConvert.DeserializeObject<HeartbeatPayload>(json);
            if (heartbeat != null &&
                heartbeat.kind == HeartbeatKind &&
                heartbeat.protocolVersion == ProtocolVersion &&
                string.Equals(heartbeat.sessionId, session.sessionId, StringComparison.Ordinal) &&
                string.Equals(heartbeat.token, session.token, StringComparison.Ordinal))
            {
                session.blenderEnvironment = JsonConvert.DeserializeObject<BlenderEnvironment>(json);
            }
        }
        catch (JsonException ex)
        {
            MCBLogger.LogWarning("[BlenderSync] Could not read the Blender heartbeat: " + ex.Message);
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

    private static void ReplaceTargetFbx(string sourceFbxPath, string targetUnityPath, ExportFileRollback files)
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

        files.Track(targetAbsolutePath);
        File.Copy(sourceFbxPath, targetAbsolutePath, true);
    }

    private static string CopyModelToProjectExports(ActiveSession session, string sourceFbxPath, string targetFbxPath, int modelIndex, ExportFileRollback files)
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
        files.Track(destinationAbsolutePath);
        File.Copy(sourceFbxPath, destinationAbsolutePath, true);

        string exportedUnityPath = MCBUtils.CombineUnityPath(session.blenderExportsUnityPath, fileName);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        return exportedUnityPath;
    }

    private static string CopyModelToAdvancedIgnoredExports(ActiveSession session, string sourceFbxPath, string targetFbxPath, int modelIndex, ExportFileRollback files)
    {
        if (string.IsNullOrWhiteSpace(sourceFbxPath) || !File.Exists(sourceFbxPath))
        {
            throw new FileNotFoundException("Blender exported FBX was not found.", sourceFbxPath);
        }

        string exportRoot = UnityPathToAbsolute(AdvancedBlenderExportFolder);
        string sessionFolder = Path.Combine(exportRoot, string.IsNullOrWhiteSpace(session?.sessionId) ? "manual" : session.sessionId);
        Directory.CreateDirectory(sessionFolder);

        string targetName = Path.GetFileNameWithoutExtension(targetFbxPath);
        string fileName = $"{modelIndex + 1:00}_{BlenderProjectService.SanitizeFileName(targetName)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.fbx";
        string destination = Path.Combine(sessionFolder, fileName);
        files.Track(destination);
        File.Copy(sourceFbxPath, destination, true);
        return Path.GetFullPath(destination);
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

        int targetIndex = GetCreatorTargetIndex(session.customBase, targetFbxPath);

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
            var newExternalPath = newEntry.FindPropertyRelative("externalCustomFbxPath");
            if (newExternalPath != null) newExternalPath.stringValue = "";
            newEntry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        }

        var entry = entriesProp.GetArrayElementAtIndex(targetIndex);
        entry.FindPropertyRelative("customFbx").objectReferenceValue = customFbx;
        var externalPath = entry.FindPropertyRelative("externalCustomFbxPath");
        if (externalPath != null) externalPath.stringValue = "";
        if (customBaseAvatar != null)
        {
            entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = customBaseAvatar;
        }
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(session.customBase);
    }

    private static void AssignCreatorExternalCustomFbx(ActiveSession session, string targetFbxPath, string externalFbxPath)
    {
        if (session?.customBase == null || string.IsNullOrWhiteSpace(externalFbxPath))
        {
            return;
        }

        int targetIndex = GetCreatorTargetIndex(session.customBase, targetFbxPath);

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
            var newExternalPath = newEntry.FindPropertyRelative("externalCustomFbxPath");
            if (newExternalPath != null) newExternalPath.stringValue = "";
            newEntry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        }

        var entry = entriesProp.GetArrayElementAtIndex(targetIndex);
        entry.FindPropertyRelative("customFbx").objectReferenceValue = null;
        var externalPath = entry.FindPropertyRelative("externalCustomFbxPath");
        if (externalPath != null)
        {
            externalPath.stringValue = Path.GetFullPath(externalFbxPath);
        }
        entry.FindPropertyRelative("customBaseAvatar").objectReferenceValue = null;
        var advancedReplacementProp = serialized.FindProperty("useAdvancedMeshReplacementForCreator");
        if (advancedReplacementProp != null && FeatureFlags.IsEnabled(FeatureFlags.ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR))
        {
            advancedReplacementProp.boolValue = true;
        }
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(session.customBase);
    }

    private static int GetCreatorTargetIndex(MyCustomBase customBase, string targetFbxPath)
    {
        int index = customBase.baseFbxFiles.FindIndex(source => source != null &&
            string.Equals(MCBUtils.ToUnityPath(AssetDatabase.GetAssetPath(source)),
                MCBUtils.ToUnityPath(targetFbxPath), StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"The Blender target '{targetFbxPath}' is not in this creator's source files. Reconnect the intended custom base before syncing.");
        return index;
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
                blenderExportsAbsolutePath = session.blenderExportsAbsolutePath,
                useAdvancedMeshBlenderLink = session.useAdvancedMeshBlenderLink,
                livePort = session.live?.Port ?? 0
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

        // GetSessionForEditor selects the last session; directory enumeration order
        // must not resurrect an older connection after a domain reload.
        var livePorts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string sessionFile in Directory.GetFiles(root, "session.json", SearchOption.AllDirectories)
                     .OrderBy(File.GetLastWriteTimeUtc))
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
                    blenderExportsAbsolutePath = persisted.blenderExportsAbsolutePath,
                    useAdvancedMeshBlenderLink = persisted.useAdvancedMeshBlenderLink
                };
                ResolveCustomBase(active);
                ActiveSessions.Add(active);
                livePorts[active.sessionId] = persisted.livePort;
                MCBLogger.Log($"[BlenderSync] Restored sync session {active.sessionId} from {sessionFile}");
            }
            catch (Exception ex)
            {
                MCBLogger.LogWarning($"[BlenderSync] Failed to restore session file '{sessionFile}': {ex.Message}");
            }
        }

        // The live link of each avatar's current session listens again, on its port when it is still free; live.json
        // tells a reconnecting Blender where it is.
        var avatarsWithLive = new HashSet<string>(StringComparer.Ordinal);
        for (int i = ActiveSessions.Count - 1; i >= 0; i--)
        {
            var session = ActiveSessions[i];
            if (session == null || session.live != null || !livePorts.TryGetValue(session.sessionId, out int port)) continue;
            if (!avatarsWithLive.Add(session.customBaseGlobalId ?? session.sessionId)) continue;
            session.live = StartLiveSession(session.sessionId, session.token, session.inboxPath, port);
            if (session.live != null && session.live.Port != port) WriteSessionFile(session);
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

    // Only the recorded object owns the session: an avatar with the same name in another scene is a different avatar.
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
    }

    // An avatar connected in a scene that was never saved gets its lasting id only when the scene is saved.
    private static void UpdateSessionIdsForSavedScene(UnityEngine.SceneManagement.Scene scene)
    {
        foreach (var session in ActiveSessions)
        {
            if (session?.customBase == null || session.customBase.gameObject.scene != scene) continue;
            string globalId = GlobalObjectId.GetGlobalObjectIdSlow(session.customBase).ToString();
            if (string.Equals(globalId, session.customBaseGlobalId, StringComparison.Ordinal)) continue;
            session.customBaseGlobalId = globalId;
            WriteSessionFile(session);
        }
    }

    private static void ReportExportWaitingForScene(ActiveSession session)
    {
        if (session == null || string.IsNullOrEmpty(session.inboxPath) || !Directory.Exists(session.inboxPath)) return;
        try
        {
            string readyPath = Directory.GetFiles(session.inboxPath, "ready.json", SearchOption.AllDirectories)
                .FirstOrDefault(path => !File.Exists(path + ".processed") && !File.Exists(path + ".failed"));
            if (readyPath == null || !WaitingExportsReported.Add(readyPath)) return;
            SetStatus($"A Blender export for {session.customBaseName} is waiting: the avatar of this Blender session is in another scene. Open that scene to apply it.", MessageType.Warning);
            try { InternalEditorUtility.RepaintAllViews(); } catch { }
        }
        catch (Exception ex)
        {
            MCBLogger.LogWarning("[BlenderSync] Failed to scan a waiting Blender export: " + ex.Message);
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
