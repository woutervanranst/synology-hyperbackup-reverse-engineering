using System.Buffers.Binary;

namespace HyperBackup.Core.Format;

/// <summary>One chunk's entry inside a bucket index (Type 2). 32 bytes, big-endian.</summary>
/// <param name="ChunkSize">Size of the stored chunk in the .bucket.2 file, excluding the 4-byte
/// trailing integrity tag present only on encrypted chunks.</param>
/// <param name="BucketOffset">Cumulative physical byte offset of the chunk within the .bucket.2 file.</param>
/// <param name="DedupSize">Original (uncompressed, pre-encryption) size of the data.</param>
/// <param name="Md5">MD5 of the original plaintext content (the dedup/content key).</param>
public readonly record struct ChunkEntry(uint ChunkSize, uint BucketOffset, uint DedupSize, byte[] Md5, uint Fingerprint)
{
    public string Md5Hex => Convert.ToHexString(Md5);
}

/// <summary>
/// Parser for a bucket chunk index ("Pool/.../{N}.index.2", format_type = 2).
/// Chunk entries are 32 bytes each starting at offset 0x40; entry count is
/// derived from the file length. Verified against the sample data (bucket 1 ->
/// md5("hello"), md5("world")).
/// </summary>
public static class BucketIndex
{
    public const int EntriesOffset = 0x40;
    public const int EntrySize = 32;

    public static IReadOnlyList<ChunkEntry> Parse(ReadOnlySpan<byte> data)
    {
        var header = BinaryIndexHeader.Parse(data);
        if (header.FormatType != 2)
            throw new InvalidDataException($"Not a bucket chunk index (format_type={header.FormatType}).");

        var entries = new List<ChunkEntry>();
        for (var off = EntriesOffset; off + EntrySize <= data.Length; off += EntrySize)
        {
            var span = data.Slice(off, EntrySize);
            var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(span[0..]);
            var bucketOffset = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
            var dedupSize = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
            var md5 = span.Slice(12, 16).ToArray();
            var fingerprint = BinaryPrimitives.ReadUInt32BigEndian(span[28..]);

            // A trailing all-zero entry would mean the index has slack; skip empties.
            if (chunkSize == 0 && dedupSize == 0 && IsAllZero(md5))
                continue;

            entries.Add(new ChunkEntry(chunkSize, bucketOffset, dedupSize, md5, fingerprint));
        }
        return entries;
    }

    private static bool IsAllZero(byte[] b)
    {
        foreach (var x in b)
            if (x != 0)
                return false;
        return true;
    }
}
