#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;

/// <summary>
/// The live link between Blender and Unity: Blender streams the vertex positions of the meshes being edited, Unity shows
/// them on the avatar at once (nothing written to assets) until the edit is committed through the usual FBX export.
///
/// Transport: TCP on 127.0.0.1. The port Unity listens on is in the Magic Sync payload (<c>"live": {"port",
/// "protocolVersion"}</c>) and in <c>live.json</c> of the session's inbox folder (<c>{"kind":"orbiters.mcb.blenderLive",
/// "protocolVersion","sessionId","host","port"}</c>, rewritten when Unity listens again after a script reload, where the
/// port can change). Blender reconnects with a backoff and reads <c>live.json</c> again each time. Every frame carries
/// the session token; a frame with another token closes the connection and nothing it sent is used. This protocol has
/// its own version (<see cref="Version"/>), apart from the Magic Sync protocol.
///
/// Frame (little-endian):
/// <code>
///   u32 magic   0x4C42434D ("MCBL")
///   u8  version 1
///   u8  type    (FrameType)
///   u16 token length, then the token (UTF-8)
///   u32 payload length, then the payload
/// </code>
/// Payloads:
/// <list type="bullet">
/// <item>Hello (Blender to Unity, first frame): UTF-8 JSON <c>{"sessionId","blender","addon"}</c>.</item>
/// <item>Rest and Positions (Blender to Unity): mesh id (u16 length + UTF-8), u32 vertex count, then count × 3 float32
/// positions.
///   <para>Mesh id: the renderer Blender knows the mesh as (its <c>mcb_target_renderer_name</c>, set when Unity opened the
///   project), else its object name, which is the FBX node name. Unity looks it up in the session's renderer mapping by
///   renderer name, FBX node name, then mesh name.</para>
///   <para>Positions: the mesh's own local coordinates, which is what MCB's FBX export writes (no modifiers, no
///   space-transform baking). Unity's import of that FBX holds them as (-x, y, z); the vertex map finds this from the
///   Rest positions, so Blender never converts. The positions are those of the layer being edited: the active shape key
///   while it is edited (Edit or Sculpt mode on a key other than the reference one), else the base shape. Shape key
///   values are ignored: Unity's blendshapes apply on top as they are.</para>
///   <para>Rest: the positions Unity's mesh was made from, as of Blender's last export of the mesh or, before any, as
///   when Blender connected. Sent for every target mesh after Hello and again after each export. Positions: about 200 ms
///   after the last edit, only when they changed; after a (re)connection also when they differ from the rest.</para></item>
/// <item>Topology (Blender to Unity): mesh id, u32 the new vertex count, sent once when the vertex count differs from the
/// rest. Blender sends no Positions for that mesh until its next Rest; Unity shows "Commit to update".</item>
/// <item>Status (Unity to Blender): UTF-8 JSON <c>{"meshId","live":true|false,"message"}</c>, when a mesh's state
/// changes.</item>
/// <item>Commit (Unity to Blender): empty. Blender runs its usual export (as "Sync with Unity") of the meshes edited since
/// their rest, all target meshes when none was, then sends their Rest. Unity takes the live meshes off before applying
/// the export.</item>
/// <item>Bye (either way): empty; the connection closes.</item>
/// </list>
/// </summary>
internal static class BlenderLiveProtocol
{
    public const uint Magic = 0x4C42434D;
    public const byte Version = 1;
    public const int HeaderBytes = 12;
    public const int MaxTokenBytes = 256;
    // A million vertices with their 12 bytes, and room for the mesh id.
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public enum FrameType : byte
    {
        Hello = 1,
        Rest = 2,
        Positions = 3,
        Topology = 4,
        Status = 5,
        Commit = 6,
        Bye = 7
    }

    internal sealed class Frame
    {
        public FrameType Type;
        public string Token = string.Empty;
        public byte[] Payload = Array.Empty<byte>();
        // Rest, Positions and Topology:
        public string MeshId = string.Empty;
        public int VertexCount;
        public float[] Positions;
        // Hello and Status:
        public string Json;
    }

    public static byte[] Encode(FrameType type, string token, byte[] payload)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token ?? string.Empty);
        payload ??= Array.Empty<byte>();
        if (tokenBytes.Length > MaxTokenBytes) throw new ArgumentException("The token is too long.");
        if (payload.Length > MaxPayloadBytes) throw new ArgumentException("The frame is too big.");
        using (var stream = new MemoryStream(HeaderBytes + tokenBytes.Length + payload.Length))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((byte)type);
            writer.Write((ushort)tokenBytes.Length);
            writer.Write(payload.Length);
            writer.Write(tokenBytes);
            writer.Write(payload);
            return stream.ToArray();
        }
    }

    public static byte[] MeshPayload(string meshId, float[] positions)
    {
        byte[] id = Encoding.UTF8.GetBytes(meshId ?? string.Empty);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)id.Length);
            writer.Write(id);
            writer.Write(positions.Length / 3);
            foreach (float value in positions) writer.Write(value);
            return stream.ToArray();
        }
    }

    public static byte[] TopologyPayload(string meshId, int vertexCount)
    {
        byte[] id = Encoding.UTF8.GetBytes(meshId ?? string.Empty);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)id.Length);
            writer.Write(id);
            writer.Write(vertexCount);
            return stream.ToArray();
        }
    }

    /// <summary>Reads the 12-byte header: false when it is not a frame of this protocol (or announces too much).</summary>
    public static bool TryReadHeader(byte[] header, out FrameType type, out int tokenLength, out int payloadLength)
    {
        type = 0;
        tokenLength = payloadLength = 0;
        if (header == null || header.Length < HeaderBytes) return false;
        if (BitConverter.ToUInt32(header, 0) != Magic || header[4] != Version) return false;
        type = (FrameType)header[5];
        tokenLength = BitConverter.ToUInt16(header, 6);
        payloadLength = BitConverter.ToInt32(header, 8);
        return Enum.IsDefined(typeof(FrameType), type) && tokenLength <= MaxTokenBytes && payloadLength >= 0 && payloadLength <= MaxPayloadBytes;
    }

    /// <summary>Decodes a frame's payload by its type; false (with a reason) when it does not hold together.</summary>
    public static bool TryDecodePayload(Frame frame, out string error)
    {
        error = null;
        try
        {
            switch (frame.Type)
            {
                case FrameType.Hello:
                case FrameType.Status:
                    frame.Json = Encoding.UTF8.GetString(frame.Payload);
                    return true;
                case FrameType.Rest:
                case FrameType.Positions:
                case FrameType.Topology:
                    using (var reader = new BinaryReader(new MemoryStream(frame.Payload)))
                    {
                        int idLength = reader.ReadUInt16();
                        frame.MeshId = Encoding.UTF8.GetString(reader.ReadBytes(idLength));
                        frame.VertexCount = reader.ReadInt32();
                        if (frame.VertexCount < 0) throw new InvalidDataException("negative vertex count");
                        if (frame.Type == FrameType.Topology) return true;
                        long expected = 2 + idLength + 4 + (long)frame.VertexCount * 12;
                        if (expected != frame.Payload.Length) throw new InvalidDataException("expected " + expected + " bytes, got " + frame.Payload.Length);
                        var positions = new float[frame.VertexCount * 3];
                        Buffer.BlockCopy(frame.Payload, 2 + idLength + 4, positions, 0, positions.Length * 4);
                        if (!BitConverter.IsLittleEndian) throw new InvalidDataException("big-endian editors are not supported");
                        frame.Positions = positions;
                        return true;
                    }
                default:
                    return true;
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException || ex is ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Compares tokens in constant time (a wrong token never tells how much of it was right).</summary>
    public static bool SameToken(string a, string b)
    {
        if (a == null || b == null) return false;
        int difference = a.Length ^ b.Length;
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            difference |= (i < a.Length ? a[i] : 0) ^ (i < b.Length ? b[i] : 0);
        return difference == 0;
    }
}
#endif
