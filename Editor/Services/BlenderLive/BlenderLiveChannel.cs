#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEditor;

/// <summary>
/// Unity's end of the live link (see <see cref="BlenderLiveProtocol"/>): a listener on 127.0.0.1 that takes one Blender at
/// a time, reads its frames on a background thread and hands them to the editor's main thread through
/// <see cref="EditorApplication.update"/>. Several Positions frames of one mesh waiting at once collapse to the newest.
/// </summary>
internal sealed class BlenderLiveChannel : IDisposable
{
    private readonly string token;
    private readonly TcpListener listener;
    private readonly ConcurrentQueue<BlenderLiveProtocol.Frame> frames = new ConcurrentQueue<BlenderLiveProtocol.Frame>();
    private readonly ConcurrentDictionary<string, BlenderLiveProtocol.Frame> latestPositions = new ConcurrentDictionary<string, BlenderLiveProtocol.Frame>(StringComparer.Ordinal);
    private readonly object sendLock = new object();
    private readonly Thread acceptThread;
    private volatile bool disposed;
    private TcpClient client;
    private NetworkStream stream;
    private int connectedFlag;

    /// <summary>A frame reached the main thread (already checked: this session's token, a payload that holds together).</summary>
    public event Action<BlenderLiveProtocol.Frame> Received;
    /// <summary>Blender connected (true) or went away (false), on the main thread.</summary>
    public event Action<bool> ConnectionChanged;

    public int Port { get; }
    public bool Connected => Volatile.Read(ref connectedFlag) == 1;

    /// <param name="port">0 for any free port; a port to listen on again after a script reload.</param>
    public BlenderLiveChannel(string token, int port = 0)
    {
        this.token = token ?? throw new ArgumentNullException(nameof(token));
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException) when (port != 0)
        {
            // Taken meanwhile: any free port, written again to live.json.
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MCB Blender live link" };
        acceptThread.Start();
        EditorApplication.update += Pump;
        AssemblyReloadEvents.beforeAssemblyReload += Dispose;
    }

    /// <summary>Sends a frame to Blender; false when none is connected or the send failed.</summary>
    public bool Send(BlenderLiveProtocol.FrameType type, byte[] payload = null)
    {
        var current = stream;
        if (current == null) return false;
        byte[] data = BlenderLiveProtocol.Encode(type, token, payload);
        try
        {
            lock (sendLock) current.Write(data, 0, data.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
        {
            return false;
        }
    }

    /// <summary>Writes where Blender finds the port again: <c>live.json</c> in the session's inbox.</summary>
    public void WriteEndpoint(string inboxPath, string sessionId)
    {
        if (string.IsNullOrEmpty(inboxPath)) return;
        Directory.CreateDirectory(inboxPath);
        string json = "{\"kind\":\"orbiters.mcb.blenderLive\",\"protocolVersion\":" + BlenderLiveProtocol.Version +
                      ",\"sessionId\":\"" + sessionId + "\",\"host\":\"127.0.0.1\",\"port\":" + Port + "}";
        string path = Path.Combine(inboxPath, "live.json");
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private void AcceptLoop()
    {
        while (!disposed)
        {
            TcpClient accepted;
            try
            {
                accepted = listener.AcceptTcpClient();
            }
            catch (Exception) when (disposed)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            // One Blender at a time: a new connection replaces the previous one (Blender reconnecting after a crash).
            CloseClient();
            accepted.NoDelay = true;
            client = accepted;
            stream = accepted.GetStream();
            Interlocked.Exchange(ref connectedFlag, 1);
            frames.Enqueue(new BlenderLiveProtocol.Frame { Type = BlenderLiveProtocol.FrameType.Hello, Token = null });
            ReadLoop(accepted, stream);
            if (ReferenceEquals(client, accepted))
            {
                CloseClient();
                frames.Enqueue(new BlenderLiveProtocol.Frame { Type = BlenderLiveProtocol.FrameType.Bye, Token = null });
            }
        }
    }

    private void ReadLoop(TcpClient owner, NetworkStream input)
    {
        var header = new byte[BlenderLiveProtocol.HeaderBytes];
        try
        {
            while (!disposed && owner.Connected)
            {
                if (!ReadExactly(input, header, BlenderLiveProtocol.HeaderBytes)) return;
                if (!BlenderLiveProtocol.TryReadHeader(header, out var type, out int tokenLength, out int payloadLength)) return;
                var tokenBytes = new byte[tokenLength];
                if (!ReadExactly(input, tokenBytes, tokenLength)) return;
                var payload = new byte[payloadLength];
                if (!ReadExactly(input, payload, payloadLength)) return;
                var frame = new BlenderLiveProtocol.Frame
                {
                    Type = type,
                    Token = System.Text.Encoding.UTF8.GetString(tokenBytes),
                    Payload = payload
                };
                // Anything not of this session ends the connection: nothing it sent is used.
                if (!BlenderLiveProtocol.SameToken(frame.Token, token)) return;
                if (!BlenderLiveProtocol.TryDecodePayload(frame, out _)) return;
                if (frame.Type == BlenderLiveProtocol.FrameType.Positions) latestPositions[frame.MeshId] = frame;
                else frames.Enqueue(frame);
                if (frame.Type == BlenderLiveProtocol.FrameType.Bye) return;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
        {
            // Blender went away.
        }
    }

    private static bool ReadExactly(NetworkStream input, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = input.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private void CloseClient()
    {
        var previous = client;
        client = null;
        stream = null;
        Interlocked.Exchange(ref connectedFlag, 0);
        try { previous?.Close(); }
        catch (Exception) { }
    }

    // Main thread: connection changes and frames in arrival order, then the newest positions of each mesh.
    private void Pump()
    {
        while (frames.TryDequeue(out var frame))
        {
            if (frame.Token == null)
            {
                ConnectionChanged?.Invoke(frame.Type == BlenderLiveProtocol.FrameType.Hello);
                continue;
            }
            Received?.Invoke(frame);
        }
        foreach (var key in latestPositions.Keys)
            if (latestPositions.TryRemove(key, out var positions))
                Received?.Invoke(positions);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        EditorApplication.update -= Pump;
        AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
        Send(BlenderLiveProtocol.FrameType.Bye);
        CloseClient();
        try { listener.Stop(); }
        catch (Exception) { }
    }
}
#endif
