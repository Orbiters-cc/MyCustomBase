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
/// positions; Positions may end with the shape key they are (u16 length + UTF-8, absent or empty for the base shape).
///   <para>Mesh id: the renderer Blender knows the mesh as (its <c>mcb_target_renderer_name</c>, set when Unity opened the
///   project), else its object name, which is the FBX node name. Unity looks it up in the session's renderer mapping by
///   renderer name, FBX node name, then mesh name.</para>
///   <para>Positions: the mesh's own local coordinates, which is what MCB's FBX export writes (no modifiers, no
///   space-transform baking). Unity's import of that FBX holds them as (-x, y, z); the vertex map finds this from the
///   Rest positions, so Blender never converts. The positions are those of the layer being edited: the active shape key
///   while it is edited (Edit or Sculpt mode on a key other than the reference one), named after them, else the base
///   shape. Unity's blendshapes apply on top as they are: positions of a key are the base plus that key, so Unity takes
///   its blendshape of that name off them first (else the key would show twice).</para>
///   <para>Rest: the positions Unity's mesh was made from, as of Blender's last export of the mesh or, before any, as
///   when Blender connected. Sent for every target mesh after Hello and again after each export. Positions: about 200 ms
///   after the last edit, only when they changed; after a (re)connection also when they differ from the rest.</para></item>
/// <item>Topology (Blender to Unity): mesh id, u32 the new vertex count, sent once when the vertex count differs from the
/// rest. Blender sends no Positions for that mesh until its next Rest; Unity shows "Commit to update".</item>
/// <item>Status (Unity to Blender): UTF-8 JSON <c>{"meshId","live":true|false,"message","differs":true|false}</c>, when
/// a mesh's state changes. "differs": the avatar's mesh isn't Blender's (its vertices aren't at Blender's places), so
/// Blender's next export takes it even if it wasn't edited since the file opened.</item>
/// <item>Commit (Unity to Blender): empty. Blender runs its usual export (as "Sync with Unity") of the meshes edited since
/// their rest, all target meshes when none was, then sends their Rest. Unity takes the live meshes off before applying
/// the export.</item>
/// <item>Bye (either way): empty; the connection closes.</item>
/// <item>Texture (Unity to Blender): UTF-8 JSON <c>{"meshId","slot","channel","path"}</c>, an image painted for a material
/// slot of that mesh (Substance 3D Painter's, live or saved; channels basecolor, normal, roughness, metallic, emissive,
/// ambientocclusion). Blender loads the newest of each channel into that slot's material.</item>
/// </list>
/// </summary>
internal static class BlenderLiveProtocol
{
    public const uint Magic = 0x4C42434D;
    public const byte Version = 1;
    public const int HeaderBytes = Orbiters.Toolkit.Editor.LiveLink.LiveLinkFormat.HeaderBytes;
    public const int MaxTokenBytes = Orbiters.Toolkit.Editor.LiveLink.LiveLinkFormat.MaxTokenBytes;
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
        Bye = 7,
        Texture = 8
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
        // Positions: the shape key they are, or empty for the base shape.
        public string ShapeKey = string.Empty;
        // Hello and Status:
        public string Json;
    }

    // The frame layout is Toolkit's live link format, under this protocol's magic and version.
    internal static readonly Orbiters.Toolkit.Editor.LiveLink.LiveLinkFormat Format =
        new Orbiters.Toolkit.Editor.LiveLink.LiveLinkFormat(Magic, Version, MaxPayloadBytes, type => Enum.IsDefined(typeof(FrameType), type));

    public static byte[] Encode(FrameType type, string token, byte[] payload) => Format.Encode((byte)type, token, payload);

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
        bool valid = Format.TryReadHeader(header, out byte raw, out tokenLength, out payloadLength);
        type = (FrameType)raw;
        return valid;
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
                        if (frame.Payload.Length < expected) throw new InvalidDataException("expected " + expected + " bytes, got " + frame.Payload.Length);
                        var positions = new float[frame.VertexCount * 3];
                        Buffer.BlockCopy(frame.Payload, 2 + idLength + 4, positions, 0, positions.Length * 4);
                        if (!BitConverter.IsLittleEndian) throw new InvalidDataException("big-endian editors are not supported");
                        frame.Positions = positions;
                        if (frame.Type == FrameType.Positions && frame.Payload.Length > expected)
                        {
                            reader.BaseStream.Position = expected;
                            int keyLength = reader.ReadUInt16();
                            frame.ShapeKey = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
                            if (reader.BaseStream.Position != frame.Payload.Length) throw new InvalidDataException("unexpected bytes after the shape key");
                        }
                        else if (frame.Payload.Length != expected) throw new InvalidDataException("expected " + expected + " bytes, got " + frame.Payload.Length);
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
    public static bool SameToken(string a, string b) => Orbiters.Toolkit.Editor.LiveLink.LiveLinkFormat.SameToken(a, b);
}
#endif
