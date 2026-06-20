using System.Buffers.Binary;

namespace HyperBackup.Core.Format;

/// <summary>
/// The common 60-byte header shared by every HyperBackup ".index"/".idx" file,
/// identified by magic 0x7053a86e (big-endian). Only the fields the reader needs
/// are surfaced; the full header layout is documented in the README
/// ("Binary Index Format (7053 a86e)").
/// </summary>
public readonly struct BinaryIndexHeader
{
    public const uint Magic = 0x7053_a86e;

    /// <summary>0 = paged record table, 1 = B+-tree (TLV nodes), 2 = bucket chunk index.</summary>
    public uint FormatType { get; init; }

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
        };
    }
}
