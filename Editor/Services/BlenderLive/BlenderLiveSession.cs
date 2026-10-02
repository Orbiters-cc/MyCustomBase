#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// The live preview of one Magic Sync session: its <see cref="BlenderLiveChannel"/>, and for each mesh Blender streams the
/// rest positions, the vertex map to the avatar's renderer and the temporary mesh shown on it. Blender is told whether each
/// mesh is live (Status frames). Live meshes never reach assets: they are taken off around scene saves and script reloads,
/// and before an export of this session is applied.
/// </summary>
internal sealed class BlenderLiveSession : IDisposable
{
    private const double CommitTimeoutSeconds = 120.0;

    private readonly Func<string, SkinnedMeshRenderer> resolveRenderer;
    private readonly BlenderLiveChannel channel;
    private readonly Dictionary<string, LiveMesh> meshes = new Dictionary<string, LiveMesh>(StringComparer.Ordinal);
    private double commitStartedAt = -1;
    private bool applyingExport;
    private bool disposed;

    private sealed class LiveMesh
    {
        public float[] rest;
        public float[] latest;
        public BlenderVertexMap map;
        public Mesh mapMesh;
        public string mapFailureKey;
        public BlenderLivePreview preview;
        public bool topologyChanged;
        public bool live;
        public string message = "Waiting for an edit in Blender";
        public string sentStatus;
    }

    /// <summary>Something the UI shows changed (on the main thread).</summary>
    public event Action Changed;

    /// <param name="resolveRenderer">The avatar's renderer for a Blender mesh id, or null.</param>
    /// <param name="port">The port to listen on again after a script reload; 0 for any free port.</param>
    public BlenderLiveSession(string token, string sessionId, string inboxPath, Func<string, SkinnedMeshRenderer> resolveRenderer, int port = 0)
    {
        this.resolveRenderer = resolveRenderer;
        channel = new BlenderLiveChannel(token, port);
        channel.Received += OnFrame;
        channel.ConnectionChanged += OnConnectionChanged;
        channel.WriteEndpoint(inboxPath, sessionId);
        EditorSceneManager.sceneSaving += OnSceneSaving;
        EditorSceneManager.sceneSaved += OnSceneSaved;
        AssemblyReloadEvents.beforeAssemblyReload += Dispose;
    }

    public int Port => channel.Port;
    public bool Connected => channel.Connected;
    public bool Committing => commitStartedAt >= 0;
    public bool HasLiveMeshes => meshes.Values.Any(mesh => mesh.preview != null && mesh.preview.Active);
    /// <summary>A mesh changed in Blender in a way only a commit (FBX export) can show.</summary>
    public bool NeedsCommit => meshes.Values.Any(mesh => mesh.topologyChanged);
    /// <summary>Each streamed mesh: whether it is live in Unity, and why not.</summary>
    public IEnumerable<(string meshId, bool live, string message)> MeshStates =>
        meshes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value.live, pair.Value.message));

    /// <summary>Asks Blender to export, as when the file is saved; false when Blender is not connected.</summary>
    public bool Commit()
    {
        if (!channel.Send(BlenderLiveProtocol.FrameType.Commit)) return false;
        commitStartedAt = EditorApplication.timeSinceStartup;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The avatar's own meshes again; the next edit in Blender shows live again.</summary>
    public void Revert()
    {
        foreach (var pair in meshes)
        {
            var mesh = pair.Value;
            if (mesh.preview == null) continue;
            mesh.preview.Revert();
            mesh.preview = null;
            mesh.latest = null;
            SetStatus(pair.Key, mesh, false, "Reverted in Unity; the next edit in Blender shows live again");
        }
        Changed?.Invoke();
    }

    /// <summary>Before an export of this session is applied: the live meshes go, the maps are for the meshes it replaces.</summary>
    public void BeginExportApply()
    {
        applyingExport = true;
        foreach (var mesh in meshes.Values)
        {
            mesh.preview?.Revert();
            mesh.preview = null;
            mesh.map = null;
            mesh.mapMesh = null;
            mesh.mapFailureKey = null;
            mesh.latest = null;
        }
    }

    /// <summary>After an export of this session was applied (or failed): the meshes are mapped again to what Unity shows now.</summary>
    public void EndExportApply()
    {
        if (!applyingExport) return;
        applyingExport = false;
        commitStartedAt = -1;
        foreach (var pair in meshes)
        {
            var mesh = pair.Value;
            mesh.topologyChanged = false;
            if (mesh.rest != null && TryMap(pair.Key, mesh))
                SetStatus(pair.Key, mesh, false, "Up to date; the next edit in Blender shows live");
        }
        Changed?.Invoke();
    }

    /// <summary>Called regularly: a commit Blender never answered stops waiting.</summary>
    public void Tick()
    {
        if (!Committing || EditorApplication.timeSinceStartup - commitStartedAt < CommitTimeoutSeconds) return;
        commitStartedAt = -1;
        Changed?.Invoke();
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected)
        {
            // Today's file-based sync keeps working: what is live stays until reverted or an export arrives.
            foreach (var mesh in meshes.Values)
                mesh.message = mesh.preview != null && mesh.preview.Active
                    ? "Blender disconnected: export from Blender to keep this edit, or Revert"
                    : "Blender disconnected";
            commitStartedAt = -1;
        }
        Changed?.Invoke();
    }

    private void OnFrame(BlenderLiveProtocol.Frame frame)
    {
        switch (frame.Type)
        {
            case BlenderLiveProtocol.FrameType.Rest:
                OnRest(frame.MeshId, frame.Positions);
                break;
            case BlenderLiveProtocol.FrameType.Positions:
                OnPositions(frame.MeshId, frame.Positions);
                break;
            case BlenderLiveProtocol.FrameType.Topology:
                OnTopology(frame.MeshId, frame.VertexCount);
                break;
        }
        Changed?.Invoke();
    }

    private LiveMesh Get(string meshId)
    {
        if (!meshes.TryGetValue(meshId, out var mesh)) meshes[meshId] = mesh = new LiveMesh();
        return mesh;
    }

    // What the avatar shows for this mesh as of Blender's last export (or connection). The live mesh stays when it shows
    // exactly these positions: an export of them is on its way.
    private void OnRest(string meshId, float[] rest)
    {
        var mesh = Get(meshId);
        bool showsRest = mesh.preview != null && mesh.preview.Active && mesh.latest != null && mesh.latest.SequenceEqual(rest);
        if (!showsRest)
        {
            mesh.preview?.Revert();
            mesh.preview = null;
            mesh.latest = null;
        }
        mesh.rest = rest;
        mesh.topologyChanged = false;
        mesh.map = null;
        mesh.mapMesh = null;
        mesh.mapFailureKey = null;
        if (!showsRest && TryMap(meshId, mesh))
            SetStatus(meshId, mesh, false, "Ready; the next edit in Blender shows live");
    }

    private void OnPositions(string meshId, float[] positions)
    {
        var mesh = Get(meshId);
        if (mesh.topologyChanged) return;
        if (mesh.rest == null)
        {
            SetStatus(meshId, mesh, false, "No rest positions from Blender yet");
            return;
        }
        if (!TryMap(meshId, mesh)) return;

        var renderer = resolveRenderer(meshId);
        if (mesh.preview == null || mesh.preview.Renderer != renderer)
        {
            mesh.preview?.Revert();
            mesh.preview = new BlenderLivePreview(renderer, mesh.map);
        }
        if (!mesh.preview.Apply(positions))
        {
            SetStatus(meshId, mesh, false, $"Blender sent {positions.Length / 3} vertices, the mesh has {mesh.map.BlenderVertexCount}: Commit to update");
            return;
        }
        mesh.latest = positions;
        SetStatus(meshId, mesh, true, "Live");
    }

    private void OnTopology(string meshId, int vertexCount)
    {
        var mesh = Get(meshId);
        mesh.preview?.Revert();
        mesh.preview = null;
        mesh.latest = null;
        mesh.topologyChanged = true;
        SetStatus(meshId, mesh, false, $"Vertices were added or removed in Blender ({vertexCount} now): Commit to update");
    }

    // The map from the avatar's current mesh to Blender's rest positions; a failure is reported once per mesh and rest.
    private bool TryMap(string meshId, LiveMesh mesh)
    {
        var renderer = resolveRenderer(meshId);
        if (renderer == null)
        {
            SetStatus(meshId, mesh, false, "No renderer of the avatar matches this mesh (is the avatar's scene open?)");
            return false;
        }
        var current = mesh.preview != null && mesh.preview.Active ? mesh.preview.Original : renderer.sharedMesh;
        if (current == null)
        {
            SetStatus(meshId, mesh, false, "The avatar's renderer has no mesh");
            return false;
        }
        if (mesh.map != null && mesh.mapMesh == current) return true;

        string key = current.GetInstanceID() + ":" + mesh.rest.Length;
        if (mesh.mapFailureKey == key) return false;
        var map = BlenderVertexMap.For(current, mesh.rest, out string error);
        if (map == null)
        {
            mesh.mapFailureKey = key;
            SetStatus(meshId, mesh, false, "Not live: " + error + ". Commit to update");
            return false;
        }
        mesh.map = map;
        mesh.mapMesh = current;
        mesh.mapFailureKey = null;
        return true;
    }

    private void SetStatus(string meshId, LiveMesh mesh, bool live, string message)
    {
        mesh.live = live;
        mesh.message = message;
        string status = JsonConvert.SerializeObject(new { meshId, live, message });
        if (status == mesh.sentStatus) return;
        if (channel.Send(BlenderLiveProtocol.FrameType.Status, System.Text.Encoding.UTF8.GetBytes(status))) mesh.sentStatus = status;
    }

    // A saved scene must never reference a temporary mesh: the avatar's own meshes are put back for the save.
    private void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
    {
        foreach (var mesh in meshes.Values)
        {
            if (mesh.preview == null || !mesh.preview.Active || mesh.preview.Renderer.gameObject.scene != scene) continue;
            mesh.preview.Revert();
        }
    }

    private void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
    {
        foreach (var mesh in meshes.Values)
        {
            if (mesh.preview == null || mesh.preview.Active || mesh.latest == null || mesh.preview.Renderer == null) continue;
            if (mesh.preview.Renderer.gameObject.scene == scene) mesh.preview.Apply(mesh.latest);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        EditorSceneManager.sceneSaving -= OnSceneSaving;
        EditorSceneManager.sceneSaved -= OnSceneSaved;
        AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
        foreach (var mesh in meshes.Values) mesh.preview?.Revert();
        meshes.Clear();
        channel.Dispose();
    }
}
#endif
