#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using MCBEditorUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class SceneMeshDotEffectService
{
    private const int InfiniteLoops = 0;
    private const float MinimumBoundsHeight = 0.01f;
    private const double RepaintIntervalSeconds = 1d / 30d;

    private static readonly List<ActiveSession> ActiveSessions = new List<ActiveSession>();
    private static int nextSessionId = 1;
    private static bool callbacksRegistered;
    private static double lastRepaintAt;

    public sealed class SweepSettings
    {
        public int loopCount = 1;
        public float loopDurationSeconds = 1.15f;
        public float bandHeight = 0.36f;
        public float normalOffset = 0.05f;
        public float dotSize = 0.012f;
        public float lineWidth = 1.3f;
        public int vertexStride = 2;
        public int maxDotsPerRenderer = 240;
        public Color dotColor = new Color(0.06f, 0.95f, 0.84f, 0.76f);
        public Color lineColor = new Color(0.06f, 0.95f, 0.84f, 0.22f);

        public SweepSettings Clone()
        {
            return new SweepSettings
            {
                loopCount = loopCount,
                loopDurationSeconds = loopDurationSeconds,
                bandHeight = bandHeight,
                normalOffset = normalOffset,
                dotSize = dotSize,
                lineWidth = lineWidth,
                vertexStride = vertexStride,
                maxDotsPerRenderer = maxDotsPerRenderer,
                dotColor = dotColor,
                lineColor = lineColor
            };
        }
    }

    public sealed class Session : IDisposable
    {
        private readonly int id;
        private bool disposed;

        internal Session(int id)
        {
            this.id = id;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Stop(id);
        }
    }

    private sealed class ActiveSession
    {
        public int id;
        public double startTime;
        public SweepSettings settings;
        public List<MeshTarget> targets;
        public Bounds bounds;

        public bool IsExpired(double now)
        {
            return settings.loopCount > InfiniteLoops &&
                   now - startTime >= settings.loopDurationSeconds * settings.loopCount;
        }
    }

    private sealed class MeshTarget
    {
        public Renderer renderer;
        public SkinnedMeshRenderer skinnedMeshRenderer;
        public MeshFilter meshFilter;
        public Mesh bakedMesh;

        public bool IsValid
        {
            get
            {
                if (renderer == null || renderer.gameObject == null || !renderer.gameObject.activeInHierarchy)
                {
                    return false;
                }

                return skinnedMeshRenderer != null ||
                       meshFilter != null && meshFilter.sharedMesh != null;
            }
        }

        public Mesh GetMesh()
        {
            if (skinnedMeshRenderer != null)
            {
                if (bakedMesh == null)
                {
                    bakedMesh = new Mesh
                    {
                        name = "MCB Scene Mesh Dot Effect Snapshot",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }

                skinnedMeshRenderer.BakeMesh(bakedMesh);
                return bakedMesh;
            }

            return meshFilter != null ? meshFilter.sharedMesh : null;
        }

        public Vector3 TransformPoint(Vector3 point)
        {
            return renderer.transform.TransformPoint(point);
        }

        public Vector3 TransformNormal(Vector3 normal)
        {
            return renderer.transform.TransformDirection(normal).normalized;
        }

        public void Dispose()
        {
            if (bakedMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(bakedMesh);
                bakedMesh = null;
            }
        }
    }

    public static Session PlayAvatarSweep(Transform avatarRoot, int loopCount)
    {
        return PlaySweep(GetAvatarRenderers(avatarRoot), new SweepSettings { loopCount = Mathf.Max(1, loopCount) });
    }

    public static Session StartAvatarSweepLoop(Transform avatarRoot)
    {
        return PlaySweep(GetAvatarRenderers(avatarRoot), new SweepSettings { loopCount = InfiniteLoops });
    }

    public static Session PlaySweep(Renderer renderer, int loopCount = 1)
    {
        return PlaySweep(renderer != null ? new[] { renderer } : Array.Empty<Renderer>(), new SweepSettings { loopCount = Mathf.Max(1, loopCount) });
    }

    public static Session PlaySweep(MeshFilter meshFilter, int loopCount = 1)
    {
        return PlaySweep(meshFilter != null ? meshFilter.GetComponent<Renderer>() : null, loopCount);
    }

    public static Session StartSweepLoop(Renderer renderer)
    {
        return StartSweepLoop(renderer != null ? new[] { renderer } : Array.Empty<Renderer>());
    }

    public static Session StartSweepLoop(MeshFilter meshFilter)
    {
        return StartSweepLoop(meshFilter != null ? meshFilter.GetComponent<Renderer>() : null);
    }

    public static Session PlaySweep(IEnumerable<Renderer> renderers, int loopCount = 1)
    {
        return PlaySweep(renderers, new SweepSettings { loopCount = Mathf.Max(1, loopCount) });
    }

    public static Session StartSweepLoop(IEnumerable<Renderer> renderers)
    {
        return PlaySweep(renderers, new SweepSettings { loopCount = InfiniteLoops });
    }

    public static Session PlaySweep(IEnumerable<Renderer> renderers, SweepSettings settings)
    {
        var targets = BuildTargets(renderers);
        if (targets.Count == 0)
        {
            return new Session(0);
        }

        var sessionSettings = (settings ?? new SweepSettings()).Clone();
        sessionSettings.loopDurationSeconds = Mathf.Max(0.1f, sessionSettings.loopDurationSeconds);
        sessionSettings.bandHeight = Mathf.Clamp(sessionSettings.bandHeight, 0.02f, 1f);
        sessionSettings.normalOffset = Mathf.Max(0f, sessionSettings.normalOffset);
        sessionSettings.dotSize = Mathf.Max(0.001f, sessionSettings.dotSize);
        sessionSettings.lineWidth = Mathf.Max(0.1f, sessionSettings.lineWidth);
        sessionSettings.vertexStride = Mathf.Max(1, sessionSettings.vertexStride);
        sessionSettings.maxDotsPerRenderer = Mathf.Max(8, sessionSettings.maxDotsPerRenderer);

        var session = new ActiveSession
        {
            id = nextSessionId++,
            startTime = EditorApplication.timeSinceStartup,
            settings = sessionSettings,
            targets = targets
        };
        UpdateBounds(session);

        ActiveSessions.Add(session);
        RegisterCallbacks();
        SceneView.RepaintAll();
        return new Session(session.id);
    }

    private static IEnumerable<Renderer> GetAvatarRenderers(Transform avatarRoot)
    {
        if (avatarRoot == null)
        {
            return Array.Empty<Renderer>();
        }

        var renderers = new List<Renderer>();
        renderers.AddRange(MeshFinder.GetAllSkinnedMeshRenderers(avatarRoot));
        renderers.AddRange(avatarRoot.GetComponentsInChildren<MeshRenderer>(true));
        return renderers;
    }

    private static List<MeshTarget> BuildTargets(IEnumerable<Renderer> renderers)
    {
        if (renderers == null)
        {
            return new List<MeshTarget>();
        }

        var seen = new HashSet<int>();
        var targets = new List<MeshTarget>();
        foreach (var renderer in renderers)
        {
            if (renderer == null || !seen.Add(renderer.GetInstanceID()))
            {
                continue;
            }

            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null && skinned.sharedMesh != null)
            {
                targets.Add(new MeshTarget
                {
                    renderer = renderer,
                    skinnedMeshRenderer = skinned
                });
                continue;
            }

            var meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                targets.Add(new MeshTarget
                {
                    renderer = renderer,
                    meshFilter = meshFilter
                });
            }
        }

        return targets;
    }

    private static void RegisterCallbacks()
    {
        if (callbacksRegistered)
        {
            return;
        }

        callbacksRegistered = true;
        SceneView.duringSceneGui += OnSceneGui;
        EditorApplication.update += OnEditorUpdate;
    }

    private static void UnregisterCallbacksIfIdle()
    {
        if (!callbacksRegistered || ActiveSessions.Count > 0)
        {
            return;
        }

        callbacksRegistered = false;
        SceneView.duringSceneGui -= OnSceneGui;
        EditorApplication.update -= OnEditorUpdate;
    }

    private static void OnEditorUpdate()
    {
        double now = EditorApplication.timeSinceStartup;
        RemoveExpiredSessions(now);

        if (ActiveSessions.Count == 0)
        {
            UnregisterCallbacksIfIdle();
            return;
        }

        if (now - lastRepaintAt >= RepaintIntervalSeconds)
        {
            lastRepaintAt = now;
            SceneView.RepaintAll();
        }
    }

    private static void OnSceneGui(SceneView sceneView)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        RemoveExpiredSessions(now);
        if (ActiveSessions.Count == 0)
        {
            return;
        }

        var previousColor = Handles.color;
        var previousZTest = Handles.zTest;
        Handles.zTest = CompareFunction.Always;

        try
        {
            foreach (var session in ActiveSessions.ToArray())
            {
                DrawSession(session, now);
            }
        }
        finally
        {
            Handles.color = previousColor;
            Handles.zTest = previousZTest;
        }
    }

    private static void DrawSession(ActiveSession session, double now)
    {
        if (session == null || session.targets == null || session.targets.Count == 0)
        {
            return;
        }

        UpdateBounds(session);
        float height = Mathf.Max(MinimumBoundsHeight, session.bounds.size.y);
        float elapsed = (float)(now - session.startTime);
        float loopProgress = Mathf.Repeat(elapsed / session.settings.loopDurationSeconds, 1f);
        float sweepPosition = 1f - loopProgress;
        float bandRadius = session.settings.bandHeight * 0.5f;

        foreach (var target in session.targets)
        {
            if (target == null || !target.IsValid)
            {
                continue;
            }

            Mesh mesh = target.GetMesh();
            if (mesh == null || mesh.vertexCount == 0)
            {
                continue;
            }

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int stride = Mathf.Max(
                session.settings.vertexStride,
                Mathf.CeilToInt(vertices.Length / (float)session.settings.maxDotsPerRenderer));

            for (int i = 0; i < vertices.Length; i += stride)
            {
                Vector3 world = target.TransformPoint(vertices[i]);
                float normalizedHeight = Mathf.Clamp01((world.y - session.bounds.min.y) / height);
                float distanceToBand = Mathf.Abs(normalizedHeight - sweepPosition);
                if (distanceToBand > bandRadius)
                {
                    continue;
                }

                float intensity = 1f - distanceToBand / Mathf.Max(0.0001f, bandRadius);
                intensity = Mathf.SmoothStep(0f, 1f, intensity);
                Vector3 normal = GetWorldNormal(target, normals, i, world, session.bounds.center);
                Vector3 dot = world + normal * session.settings.normalOffset;
                float size = HandleUtility.GetHandleSize(dot) * session.settings.dotSize * Mathf.Lerp(0.65f, 1.35f, intensity);

                Handles.color = WithAlpha(session.settings.lineColor, session.settings.lineColor.a * intensity);
                Handles.DrawAAPolyLine(session.settings.lineWidth, world, dot);
                Handles.color = WithAlpha(session.settings.dotColor, session.settings.dotColor.a * intensity);
                Handles.DotHandleCap(0, dot, Quaternion.identity, size, EventType.Repaint);
            }
        }
    }

    private static Vector3 GetWorldNormal(MeshTarget target, Vector3[] normals, int index, Vector3 world, Vector3 boundsCenter)
    {
        if (normals != null && index < normals.Length && normals[index].sqrMagnitude > 0.0001f)
        {
            Vector3 normal = target.TransformNormal(normals[index]);
            if (normal.sqrMagnitude > 0.0001f)
            {
                return normal;
            }
        }

        Vector3 fromCenter = world - boundsCenter;
        if (fromCenter.sqrMagnitude > 0.0001f)
        {
            return fromCenter.normalized;
        }

        return Vector3.up;
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = Mathf.Clamp01(alpha);
        return color;
    }

    private static void UpdateBounds(ActiveSession session)
    {
        bool hasBounds = false;
        Bounds bounds = default;
        for (int i = session.targets.Count - 1; i >= 0; i--)
        {
            var target = session.targets[i];
            if (target == null || !target.IsValid)
            {
                target?.Dispose();
                session.targets.RemoveAt(i);
                continue;
            }

            if (!hasBounds)
            {
                bounds = target.renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(target.renderer.bounds);
            }
        }

        if (!hasBounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.one);
        }

        if (bounds.size.y < MinimumBoundsHeight)
        {
            bounds.Expand(new Vector3(0f, MinimumBoundsHeight, 0f));
        }

        session.bounds = bounds;
    }

    private static void RemoveExpiredSessions(double now)
    {
        for (int i = ActiveSessions.Count - 1; i >= 0; i--)
        {
            var session = ActiveSessions[i];
            if (session == null || session.targets == null || session.targets.Count == 0 || session.IsExpired(now))
            {
                DisposeSession(session);
                ActiveSessions.RemoveAt(i);
            }
        }
    }

    private static void Stop(int id)
    {
        if (id == 0)
        {
            return;
        }

        for (int i = ActiveSessions.Count - 1; i >= 0; i--)
        {
            if (ActiveSessions[i].id != id)
            {
                continue;
            }

            DisposeSession(ActiveSessions[i]);
            ActiveSessions.RemoveAt(i);
            break;
        }

        SceneView.RepaintAll();
        UnregisterCallbacksIfIdle();
    }

    private static void DisposeSession(ActiveSession session)
    {
        if (session?.targets == null)
        {
            return;
        }

        foreach (var target in session.targets)
        {
            target?.Dispose();
        }
    }
}
#endif
