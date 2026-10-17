#if UNITY_EDITOR
using System;
using System.IO;
using Orbiters.Toolkit.Editor.LiveLink;

/// <summary>
/// Unity's end of the live link (see <see cref="BlenderLiveProtocol"/>): Toolkit's <see cref="LiveLinkServer"/> on
/// 127.0.0.1, taking one Blender at a time, its frames handed to the editor's main thread. Several Positions frames of one
/// mesh waiting at once collapse to the newest.
/// </summary>
internal sealed class BlenderLiveChannel : IDisposable
{
    private readonly LiveLinkServer server;

    /// <summary>A frame reached the main thread (already checked: this session's token, a payload that holds together).</summary>
    public event Action<BlenderLiveProtocol.Frame> Received;
    /// <summary>Blender connected (true) or went away (false), on the main thread.</summary>
    public event Action<bool> ConnectionChanged;

    public int Port => server.Port;
    public bool Connected => server.Connected;

    /// <param name="port">0 for any free port; a port to listen on again after a script reload.</param>
    public BlenderLiveChannel(string token, int port = 0)
    {
        if (token == null) throw new ArgumentNullException(nameof(token));
        server = new LiveLinkServer(BlenderLiveProtocol.Format, token, port, singleClient: true, accept: Decode,
            collapseKey: frame => frame.Type == (byte)BlenderLiveProtocol.FrameType.Positions ? ((BlenderLiveProtocol.Frame)frame.Decoded).MeshId : null,
            closingType: (byte)BlenderLiveProtocol.FrameType.Bye, threadName: "MCB Blender live link");
        server.Received += frame => Received?.Invoke((BlenderLiveProtocol.Frame)frame.Decoded);
        server.ConnectionChanged += (_, up) => ConnectionChanged?.Invoke(up);
    }

    private static bool Decode(LiveLinkFrame raw)
    {
        var frame = new BlenderLiveProtocol.Frame { Type = (BlenderLiveProtocol.FrameType)raw.Type, Token = raw.Token, Payload = raw.Payload };
        if (!BlenderLiveProtocol.TryDecodePayload(frame, out _)) return false;
        raw.Decoded = frame;
        return true;
    }

    /// <summary>Sends a frame to Blender; false when none is connected or the send failed.</summary>
    public bool Send(BlenderLiveProtocol.FrameType type, byte[] payload = null) => server.Broadcast((byte)type, payload) > 0;

    /// <summary>Writes where Blender finds the port again: <c>live.json</c> in the session's inbox.</summary>
    public void WriteEndpoint(string inboxPath, string sessionId)
    {
        if (string.IsNullOrEmpty(inboxPath)) return;
        string json = "{\"kind\":\"orbiters.mcb.blenderLive\",\"protocolVersion\":" + BlenderLiveProtocol.Version +
                      ",\"sessionId\":\"" + sessionId + "\",\"host\":\"127.0.0.1\",\"port\":" + Port + "}";
        LiveLinkServer.WriteAtomically(Path.Combine(inboxPath, "live.json"), json);
    }

    public void Dispose() => server.Dispose();
}
#endif
