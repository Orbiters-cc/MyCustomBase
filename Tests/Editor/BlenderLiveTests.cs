#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// The live Blender preview's pieces: frames, the Unity-to-Blender vertex map, the temporary mesh and the local link.
public class BlenderLiveTests
{
    // A quad of 4 Blender vertices that Unity split into 6 (two triangles with their own corner copies).
    private static readonly float[] BlenderQuad = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };

    private static Vector3[] UnitySplitQuad(Func<Vector3, Vector3> convert) => new[]
    {
        convert(new Vector3(0, 0, 0)), convert(new Vector3(1, 0, 0)), convert(new Vector3(1, 1, 0)),
        convert(new Vector3(0, 0, 0)), convert(new Vector3(1, 1, 0)), convert(new Vector3(0, 1, 0)),
    };

    [Test]
    public void FramesRoundTripAndRejectWhatDoesNotHoldTogether()
    {
        var positions = new float[] { 1, 2, 3, 4, 5, 6 };
        byte[] data = BlenderLiveProtocol.Encode(BlenderLiveProtocol.FrameType.Positions, "tok",
            BlenderLiveProtocol.MeshPayload("Body", positions));
        Assert.That(BlenderLiveProtocol.TryReadHeader(data.Take(12).ToArray(), out var type, out int tokenLength, out int payloadLength), Is.True);
        Assert.That(type, Is.EqualTo(BlenderLiveProtocol.FrameType.Positions));
        Assert.That(tokenLength, Is.EqualTo(3));
        var frame = new BlenderLiveProtocol.Frame
        {
            Type = type,
            Token = "tok",
            Payload = data.Skip(12 + tokenLength).Take(payloadLength).ToArray()
        };
        Assert.That(BlenderLiveProtocol.TryDecodePayload(frame, out string error), Is.True, error);
        Assert.That(frame.MeshId, Is.EqualTo("Body"));
        Assert.That(frame.VertexCount, Is.EqualTo(2));
        Assert.That(frame.Positions, Is.EqualTo(positions));

        frame.Payload = frame.Payload.Take(frame.Payload.Length - 4).ToArray();
        Assert.That(BlenderLiveProtocol.TryDecodePayload(frame, out error), Is.False, "A truncated payload is refused.");
        var bad = (byte[])data.Clone();
        bad[0] = 0;
        Assert.That(BlenderLiveProtocol.TryReadHeader(bad.Take(12).ToArray(), out _, out _, out _), Is.False);
        Assert.That(BlenderLiveProtocol.SameToken("abc", "abc"), Is.True);
        Assert.That(BlenderLiveProtocol.SameToken("abc", "abd"), Is.False);
        Assert.That(BlenderLiveProtocol.SameToken("abc", "abcd"), Is.False);
    }

    [Test]
    public void SplitVerticesMapToTheirBlenderVertexWhateverTheAxesAndUnits()
    {
        // Unity's import of a Blender export: Y up, X mirrored, in centimetres.
        Func<Vector3, Vector3> convert = v => new Vector3(-v.x, v.z, -v.y) * 0.01f;
        var unity = UnitySplitQuad(convert);
        var map = BlenderVertexMap.Build(unity, BlenderQuad, out string error);
        Assert.That(map, Is.Not.Null, error);
        Assert.That(map.BlenderIndex, Is.EqualTo(new[] { 0, 1, 2, 0, 2, 3 }));

        var moved = (float[])BlenderQuad.Clone();
        moved[8] = 0.5f; // vertex 2 pushed along Blender's Z
        var into = new Vector3[unity.Length];
        Assert.That(map.Apply(moved, into), Is.True);
        Assert.That(Vector3.Distance(into[2], convert(new Vector3(1, 1, 0.5f))), Is.LessThan(1e-5f));
        Assert.That(Vector3.Distance(into[4], into[2]), Is.LessThan(1e-6f), "Both copies of a split vertex move together.");
    }

    [Test]
    public void ADifferentMeshIsNotMapped()
    {
        var unity = UnitySplitQuad(v => v);
        unity[5] = new Vector3(5, 5, 5);
        Assert.That(BlenderVertexMap.Build(unity, BlenderQuad, out string error), Is.Null);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void AMirrorImageOfASymmetricMeshIsNotTakenForIt()
    {
        // Symmetric in X: the mirrored convention would match every vertex too, with left and right swapped.
        var blender = new List<float>();
        for (int k = 0; k < 20; k++)
        {
            float x = 1f + k * 0.01f;
            blender.AddRange(new[] { -x, k * 0.1f, 0.3f });
            blender.AddRange(new[] { x, k * 0.1f, 0.3f });
        }
        var rest = blender.ToArray();
        var unity = new Vector3[rest.Length / 3];
        for (int i = 0; i < unity.Length; i++) unity[i] = new Vector3(rest[i * 3], rest[i * 3 + 1], rest[i * 3 + 2]);
        var map = BlenderVertexMap.Build(unity, rest, out string error);
        Assert.That(map, Is.Not.Null, error);
        Assert.That(map.BlenderIndex, Is.EqualTo(Enumerable.Range(0, unity.Length).ToArray()));
    }

    [Test]
    public void ThePreviewShowsBlendersPositionsAndRevertsToTheOriginalMesh()
    {
        var go = new GameObject("BlenderLivePreviewTest");
        var mesh = new Mesh { vertices = UnitySplitQuad(v => v), triangles = new[] { 0, 1, 2, 3, 4, 5 } };
        mesh.RecalculateNormals();
        try
        {
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            var map = BlenderVertexMap.Build(mesh.vertices, BlenderQuad, out string error);
            Assert.That(map, Is.Not.Null, error);
            var preview = new BlenderLivePreview(renderer, map);
            var moved = (float[])BlenderQuad.Clone();
            moved[8] = 0.5f;
            Assert.That(preview.Apply(moved), Is.True);
            Assert.That(renderer.sharedMesh, Is.Not.SameAs(mesh));
            Assert.That(renderer.sharedMesh.vertices[2].z, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(mesh.vertices[2].z, Is.EqualTo(0f), "The original mesh is never changed.");
            Assert.That(Vector3.Distance(renderer.sharedMesh.normals[0], renderer.sharedMesh.normals[3]), Is.LessThan(1e-5f),
                "The two copies of a split vertex share one normal.");
            Assert.That(preview.Apply(new float[] { 1, 2, 3 }), Is.False, "Another vertex count is refused.");

            preview.Revert();
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(preview.Active, Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    [UnityTest]
    public IEnumerator TheLinkTakesFramesOfItsSessionOnly()
    {
        var received = new List<BlenderLiveProtocol.Frame>();
        var connections = new List<bool>();
        using (var channel = new BlenderLiveChannel("session-token"))
        {
            channel.Received += received.Add;
            channel.ConnectionChanged += connections.Add;
            using (var blender = new TcpClient("127.0.0.1", channel.Port))
            {
                var stream = blender.GetStream();
                byte[] rest = BlenderLiveProtocol.Encode(BlenderLiveProtocol.FrameType.Rest, "session-token", BlenderLiveProtocol.MeshPayload("Body", BlenderQuad));
                byte[] first = BlenderLiveProtocol.Encode(BlenderLiveProtocol.FrameType.Positions, "session-token", BlenderLiveProtocol.MeshPayload("Body", BlenderQuad));
                var newer = (float[])BlenderQuad.Clone();
                newer[0] = 9f;
                byte[] second = BlenderLiveProtocol.Encode(BlenderLiveProtocol.FrameType.Positions, "session-token", BlenderLiveProtocol.MeshPayload("Body", newer));
                stream.Write(rest, 0, rest.Length);
                stream.Write(first, 0, first.Length);
                stream.Write(second, 0, second.Length);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (received.Count(f => f.Type == BlenderLiveProtocol.FrameType.Positions && f.Positions[0] == 9f) == 0 && DateTime.UtcNow < deadline)
                    yield return null;
                Assert.That(received.First().Type, Is.EqualTo(BlenderLiveProtocol.FrameType.Rest));
                Assert.That(received.Last().Positions[0], Is.EqualTo(9f), "The newest positions arrive.");
                Assert.That(connections, Does.Contain(true));

                // Another session's token ends the connection; nothing it sent is used.
                int before = received.Count;
                byte[] intruder = BlenderLiveProtocol.Encode(BlenderLiveProtocol.FrameType.Positions, "other-token", BlenderLiveProtocol.MeshPayload("Body", BlenderQuad));
                stream.Write(intruder, 0, intruder.Length);
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (!connections.Contains(false) && DateTime.UtcNow < deadline) yield return null;
                Assert.That(connections.Last(), Is.False);
                Assert.That(received.Count, Is.EqualTo(before));
            }
        }
    }

    // A fake Blender against a session of BlenderSyncService: renderer mapping, live mesh, Status, Commit, Topology,
    // scene-save guard and Revert.
    [UnityTest]
    public IEnumerator AFakeBlenderDrivesTheAvatarThroughItsSyncSession()
    {
        const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
        var service = typeof(BlenderSyncService);
        var scene = EditorSceneManager.NewPreviewScene();
        string inbox = Path.Combine(Path.GetTempPath(), "MCB_BlenderLive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inbox);
        var root = new GameObject("LiveAvatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        // Unity's import of Blender's quad: (-x, y, z) of its local coordinates.
        var mesh = new Mesh { name = "Body", vertices = UnitySplitQuad(v => new Vector3(-v.x, v.y, v.z)), triangles = new[] { 0, 1, 2, 3, 4, 5 } };
        mesh.RecalculateNormals();
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;

        var sessionType = service.GetNestedType("ActiveSession", BindingFlags.NonPublic);
        var targetType = service.GetNestedType("TargetFbxInfo", BindingFlags.NonPublic);
        var session = Activator.CreateInstance(sessionType, true);
        string sessionId = Guid.NewGuid().ToString("N"), token = Guid.NewGuid().ToString("N");
        sessionType.GetField("sessionId").SetValue(session, sessionId);
        sessionType.GetField("token").SetValue(session, token);
        sessionType.GetField("inboxPath").SetValue(session, inbox);
        sessionType.GetField("customBase").SetValue(session, root.AddComponent<MyCustomBase>());
        var target = Activator.CreateInstance(targetType, true);
        targetType.GetField("unityPath").SetValue(target, "Assets/Avatar/Avatar.fbx");
        targetType.GetField("smrPaths").SetValue(target, new List<ModelFileSmrPathData>
        {
            new ModelFileSmrPathData { avatarPath = "Body", fbxMeshPath = "Body", meshName = "Body", rendererName = "Body" }
        });
        ((IList)sessionType.GetField("targetFbxFiles").GetValue(session)).Add(target);
        var sessions = (IList)service.GetField("ActiveSessions", Private).GetValue(null);
        sessions.Add(session);
        var live = (BlenderLiveSession)service.GetMethod("StartLiveSession", Private).Invoke(null, new object[] { sessionId, token, inbox, 0 });
        sessionType.GetField("live").SetValue(session, live);
        var statuses = new List<string>();
        var fromUnity = new List<BlenderLiveProtocol.FrameType>();
        try
        {
            StringAssert.Contains("\"port\":" + live.Port, File.ReadAllText(Path.Combine(inbox, "live.json")), "live.json tells Blender the port.");
            using (var blender = new TcpClient("127.0.0.1", live.Port))
            {
                var stream = blender.GetStream();
                void Send(BlenderLiveProtocol.FrameType type, byte[] payload)
                {
                    byte[] data = BlenderLiveProtocol.Encode(type, token, payload);
                    stream.Write(data, 0, data.Length);
                }
                void Read()
                {
                    while (stream.DataAvailable)
                    {
                        var header = new byte[BlenderLiveProtocol.HeaderBytes];
                        ReadExactly(stream, header);
                        Assert.That(BlenderLiveProtocol.TryReadHeader(header, out var type, out int tokenLength, out int payloadLength), Is.True);
                        var frame = new BlenderLiveProtocol.Frame { Type = type, Token = Encoding.UTF8.GetString(ReadExactly(stream, new byte[tokenLength])), Payload = ReadExactly(stream, new byte[payloadLength]) };
                        Assert.That(frame.Token, Is.EqualTo(token));
                        Assert.That(BlenderLiveProtocol.TryDecodePayload(frame, out string error), Is.True, error);
                        fromUnity.Add(type);
                        if (type == BlenderLiveProtocol.FrameType.Status) statuses.Add(frame.Json);
                    }
                }
                // Keeps the editor updating (the channel hands frames over on EditorApplication.update) until the condition holds.
                Func<bool> Waiting(Func<bool> condition, double seconds = 5)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(seconds);
                    return () =>
                    {
                        Read();
                        return !condition() && DateTime.UtcNow < deadline;
                    };
                }

                Send(BlenderLiveProtocol.FrameType.Hello, Encoding.UTF8.GetBytes("{\"sessionId\":\"" + sessionId + "\",\"blender\":\"5.2.2\",\"addon\":\"0.1.0\"}"));
                Send(BlenderLiveProtocol.FrameType.Rest, BlenderLiveProtocol.MeshPayload("Body", BlenderQuad));
                for (var waiting = Waiting(() => statuses.Count > 0); waiting();) yield return null;
                StringAssert.Contains("\"live\":false", statuses.Last(), "Mapped, waiting for an edit.");
                StringAssert.Contains("Ready", statuses.Last());

                var moved = (float[])BlenderQuad.Clone();
                moved[8] = 0.5f; // Blender vertex 2 along its Z
                Send(BlenderLiveProtocol.FrameType.Positions, BlenderLiveProtocol.MeshPayload("Body", moved));
                for (var waiting = Waiting(() => renderer.sharedMesh != mesh && statuses.Last().Contains("\"live\":true")); waiting();) yield return null;
                Assert.That(renderer.sharedMesh, Is.Not.SameAs(mesh), "The avatar shows Blender's edit.");
                Assert.That(renderer.sharedMesh.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave), "The live mesh is never saved.");
                Assert.That(renderer.sharedMesh.vertices[2].z, Is.EqualTo(0.5f).Within(1e-6f));
                Assert.That(renderer.sharedMesh.vertices[4].z, Is.EqualTo(0.5f).Within(1e-6f), "Split copies move together.");
                Assert.That(mesh.vertices[2].z, Is.EqualTo(0f), "The avatar's mesh is unchanged.");
                Assert.That(live.HasLiveMeshes, Is.True);

                var saving = typeof(BlenderLiveSession).GetMethod("OnSceneSaving", BindingFlags.Instance | BindingFlags.NonPublic);
                var saved = typeof(BlenderLiveSession).GetMethod("OnSceneSaved", BindingFlags.Instance | BindingFlags.NonPublic);
                saving.Invoke(live, new object[] { scene, "Assets/Live.unity" });
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "A saved scene references the avatar's own mesh.");
                saved.Invoke(live, new object[] { scene });
                Assert.That(renderer.sharedMesh.vertices[2].z, Is.EqualTo(0.5f).Within(1e-6f), "The edit shows again after the save.");

                Assert.That(live.Commit(), Is.True);
                Assert.That(live.Committing, Is.True);
                for (var waiting = Waiting(() => fromUnity.Contains(BlenderLiveProtocol.FrameType.Commit)); waiting();) yield return null;
                Assert.That(fromUnity, Does.Contain(BlenderLiveProtocol.FrameType.Commit), "Commit asks Blender to export.");

                // The export arrives: the live mesh goes before the avatar's meshes are replaced.
                live.BeginExportApply();
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
                live.EndExportApply();
                Assert.That(live.Committing, Is.False);

                Send(BlenderLiveProtocol.FrameType.Positions, BlenderLiveProtocol.MeshPayload("Body", moved));
                for (var waiting = Waiting(() => renderer.sharedMesh != mesh); waiting();) yield return null;
                Send(BlenderLiveProtocol.FrameType.Topology, BlenderLiveProtocol.TopologyPayload("Body", 5));
                for (var waiting = Waiting(() => renderer.sharedMesh == mesh && statuses.Last().Contains("Commit to update")); waiting();) yield return null;
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "A topology change stops the live mesh.");
                Assert.That(live.NeedsCommit, Is.True);
                StringAssert.Contains("Commit to update", statuses.Last());
                Send(BlenderLiveProtocol.FrameType.Positions, BlenderLiveProtocol.MeshPayload("Body", moved));
                for (var waiting = Waiting(() => false, 0.5); waiting();) yield return null;
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "Positions wait for the next Rest after a topology change.");

                Send(BlenderLiveProtocol.FrameType.Rest, BlenderLiveProtocol.MeshPayload("Body", BlenderQuad));
                Send(BlenderLiveProtocol.FrameType.Positions, BlenderLiveProtocol.MeshPayload("Body", moved));
                for (var waiting = Waiting(() => renderer.sharedMesh != mesh); waiting();) yield return null;
                live.Revert();
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh), "Revert puts the avatar's mesh back.");
                Assert.That(live.HasLiveMeshes, Is.False);

                Send(BlenderLiveProtocol.FrameType.Rest, BlenderLiveProtocol.MeshPayload("Shirt", BlenderQuad));
                for (var waiting = Waiting(() => statuses.Last().Contains("Shirt")); waiting();) yield return null;
                StringAssert.Contains("No renderer", statuses.Last(), "A mesh the avatar does not have is reported.");
            }
        }
        finally
        {
            live.Dispose();
            sessions.Remove(session);
            EditorSceneManager.ClosePreviewScene(scene);
            UnityEngine.Object.DestroyImmediate(mesh);
            Directory.Delete(inbox, true);
        }
    }

    private static byte[] ReadExactly(NetworkStream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }
}
#endif
