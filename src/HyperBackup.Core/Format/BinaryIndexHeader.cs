using System.Buffers.Binary;

namespace HyperBackup.Core.Format;

/// <summary>
/// The common 60-byte header shared by every HyperBackup ".index.2"/".idx.2"
/// file, identified by magic bytes 0x7053a86e. All multi-byte integers are
/// big-endian. See README "Binary Index Format (7053 a86e)".
/// </summary>
public readonly struct BinaryIndexHeader
{
    public const uint Magic = 0x7053_a86e;

    public uint FormatType { get; init; }   // 0 = B-tree, 1 = flat/hash, 2 = bucket chunk index
    public uint ParamA { get; init; }        // tree depth (type 0) / entry version (type 2)
    public uint DataOffset { get; init; }
    public uint DeclaredSize { get; init; }
    public uint Flags { get; init; }         // 0x08000000 plain, 0x0A000000 when encrypted+compressed
    public uint PageSize { get; init; }
    public uint HeaderCrc { get; init; }

    /// <summary>True when bit 1 of the flags is set, indicating compression+encryption.</summary>
    public bool EncryptedCompressed => (Flags & 0x0200_0000) != 0;

    public static BinaryIndexHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40)
            throw new InvalidDataException("Index file too small for a 60-byte header.");
        var magic = BinaryPrimitives.ReadUInt32BigEndian(data[0x00..]);
        if (magic != Magic)
            throw new InvalidDataException($"Bad index magic: 0x{magic:x8} (expected 0x{Magic:x8}).");
        return new BinaryIndexHeader
        {
            FormatType = BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]),
            ParamA = BinaryPrimitives.ReadUInt32BigEndian(data[0x08..]),
            DataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]),
            DeclaredSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x18..]),
            Flags = BinaryPrimitives.ReadUInt32BigEndian(data[0x1C..]),
            PageSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x20..]),
            HeaderCrc = BinaryPrimitives.ReadUInt32BigEndian(data[0x3C..]),
        };
    }
}
