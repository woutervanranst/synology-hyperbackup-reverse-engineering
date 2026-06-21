using System.Buffers.Binary;
using System.Security.Cryptography;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core.Format;

/// <summary>
/// The authoritative file → chunk mapping, reverse-engineered from Synology's own
/// on-disk indexes. Unlike the contiguous-run heuristic in <see cref="PoolReader"/>,
/// this resolves deduplicated files correctly — files whose chunks are non-contiguous
/// or reused (a chunk shared by several files, or repeated within one file).
///
/// Format (big-endian; verified byte-for-byte against real backups):
///  - Every "*.idx"/"*.index" blob is a generic B+-tree container: a 0x40-byte header
///    (magic 0x7053a86e) followed by TLV nodes. Each node is framed by a 4-byte marker
///    {0xa9, 0x3b, kind, sub} + a 4-byte big-endian length + that many payload bytes.
///    kind 1 = key, kind 2 = value.
///  - In "Config/file_chunk{1..4}.index", a leaf VALUE is one file's ordered chunk
///    list: a packed array of 8-byte big-endian offsets into "Pool/chunk_index",
///    terminated by an 8-byte trailer (0x74000000 ‖ crc). Files are spread across the
///    four tiers by size; the tier is irrelevant to resolution.
///  - "Pool/chunk_index" holds fixed 29-byte chunk records. The record at byte offset
///    <c>o</c> is { pad[1], bucket_id:u32, index_offset:u32, zero[15], refcount[1],
///    fp[4] }, where index_offset is the byte offset of that chunk's 32-byte entry
///    inside the bucket's ".index" blob, and refcount &gt; 1 marks a shared chunk.
///
/// A file is matched to its leaf by total (uncompressed) size; when several leaves
/// share a size, by the reproducible content tag = MD5 of the concatenated chunk MD5s.
/// </summary>
public sealed class FileChunkIndex
{
    public const int ChunkRecordSize = 29;
    private const uint TrailerHi = 0x7400_0000;

    private readonly IBackupStorage _storage;
    private readonly RepoPaths _paths;
    private readonly PoolReader _pool;

    private byte[]? _chunkIndex;
    private List<Leaf>? _leaves;
    private readonly Dictionary<int, byte[]?> _tierBlobs = new();

    public FileChunkIndex(IBackupStorage storage, RepoPaths paths, PoolReader pool)
    {
        _storage = storage;
        _paths = paths;
        _pool = pool;
    }

    /// <summary>One file's resolved chunk list, with the identity used to match it.</summary>
    private sealed record Leaf(long TotalSize, byte[] ConcatMd5, IReadOnlyList<ChunkLocation> Chunks);

    /// <summary>
    /// Resolve a file's ordered chunk list from the file_chunk index, or null if the
    /// index has no unambiguous match. Handles deduplicated/non-contiguous files.
    /// </summary>
    public IReadOnlyList<ChunkLocation>? Lookup(long size, byte[]? tagMd5)
    {
        var sameSize = Leaves().Where(l => l.TotalSize == size).ToList();
        if (sameSize.Count == 1)
            return sameSize[0].Chunks;
        if (sameSize.Count == 0)
            return null;

        // Several leaves of identical size: disambiguate by the reproducible tag
        // (the simple concatenated-MD5 hash; large files use a tiered tag we don't
        // reproduce, but those have unique sizes and never reach this branch).
        if (tagMd5 is not null)
        {
            var exact = sameSize.FirstOrDefault(l => l.ConcatMd5.AsSpan().SequenceEqual(tagMd5));
            if (exact is not null)
                return exact.Chunks;
        }
        return null;
    }

    /// <summary>
    /// Read the chunk list at an exact tier + value offset (the authoritative pointer
    /// from virtual_file.index). A whole file's chunk list is one flat value here,
    /// however large (verified on an 8635-chunk / 67 MB file). Returns null if the
    /// offset does not name a chunk-list value.
    /// </summary>
    public IReadOnlyList<ChunkLocation>? LeafAt(int tier, int valueOffset)
    {
        if (TierBlob(tier) is not { } blob)
            return null;
        if (valueOffset < 8 || valueOffset > blob.Length)
            return null;
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(valueOffset - 4));
        if (len < 16 || len % 8 != 0 || valueOffset + len > blob.Length)
            return null;
        return TryReadChunkList(blob[valueOffset..(valueOffset + len)]);
    }

    // --- internals ---

    private List<Leaf> Leaves()
    {
        if (_leaves is not null)
            return _leaves;

        var leaves = new List<Leaf>();
        for (var tier = 1; tier <= 4; tier++)
        {
            if (TierBlob(tier) is not { } data)
                continue;
            foreach (var (kind, value) in ParseNodes(data))
            {
                if (kind != 2)
                    continue;
                if (TryReadChunkList(value) is not { } chunks)
                    continue;
                var total = chunks.Sum(c => (long)c.Entry.DedupSize);
                leaves.Add(new Leaf(total, ConcatMd5(chunks), chunks));
            }
        }
        _leaves = leaves;
        return leaves;
    }

    /// <summary>
    /// Read a leaf value as a chunk list, or null if it is not one (e.g. a B+-tree
    /// internal node). The 8-byte trailer (high word 0x74000000) terminates the list;
    /// every preceding 8-byte offset must resolve to a valid chunk record.
    /// </summary>
    private IReadOnlyList<ChunkLocation>? TryReadChunkList(byte[] value)
    {
        if (value.Length < 16 || value.Length % 8 != 0)
            return null;
        var span = value.AsSpan();
        var count = value.Length / 8;
        var trailerHi = BinaryPrimitives.ReadUInt32BigEndian(span[((count - 1) * 8)..]);
        if (trailerHi != TrailerHi)
            return null;

        var chunks = new List<ChunkLocation>(count - 1);
        for (var i = 0; i < count - 1; i++)
        {
            var off = (long)BinaryPrimitives.ReadUInt64BigEndian(span[(i * 8)..]);
            if (ResolveChunkRef(off) is not { } loc)
                return null;
            chunks.Add(loc);
        }
        return chunks.Count > 0 ? chunks : null;
    }

    /// <summary>Resolve an 8-byte chunk_index offset to a physical chunk, or null.</summary>
    private ChunkLocation? ResolveChunkRef(long off)
    {
        var ci = ChunkIndex();
        if (off < BinaryIndexHeaderSize || off + ChunkRecordSize > ci.Length)
            return null;
        var rec = ci.AsSpan((int)off, ChunkRecordSize);
        var bucketId = (int)BinaryPrimitives.ReadUInt32BigEndian(rec[1..]);
        var indexOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(rec[5..]);
        if (indexOffset < BucketIndex.EntriesOffset)
            return null;
        var entryIndex = (indexOffset - BucketIndex.EntriesOffset) / BucketIndex.EntrySize;
        return _pool.ResolveBucketEntry(bucketId, entryIndex);
    }

    private const int BinaryIndexHeaderSize = 0x40;

    /// <summary>Walk the TLV nodes of a B+-tree blob starting after the 0x40 header.</summary>
    private static IEnumerable<(int Kind, byte[] Value)> ParseNodes(byte[] b)
    {
        var o = BinaryIndexHeaderSize;
        while (o + 8 <= b.Length)
        {
            if (b[o] != 0xa9 || b[o + 1] != 0x3b)
            {
                o++;
                continue;
            }
            var kind = b[o + 2];
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 4));
            if (len < 0 || o + 8 + len > b.Length)
            {
                o++;
                continue;
            }
            yield return (kind, b[(o + 8)..(o + 8 + len)]);
            o += 8 + len;
        }
    }

    private static byte[] ConcatMd5(IReadOnlyList<ChunkLocation> chunks)
    {
        using var md5 = MD5.Create();
        foreach (var c in chunks)
            md5.TransformBlock(c.Entry.Md5, 0, c.Entry.Md5.Length, null, 0);
        md5.TransformFinalBlock([], 0, 0);
        return md5.Hash!;
    }

    private byte[] ChunkIndex() => _chunkIndex ??= ReadAll(
        ChunkIndexPath() ?? throw new FileNotFoundException("No Pool/chunk_index blob in this repository."));

    private string? ChunkIndexPath() => _paths.Find("Pool/chunk_index/0.idx");

    /// <summary>Read (and cache) the file_chunk index blob for a tier, or null if absent.</summary>
    private byte[]? TierBlob(int tier)
    {
        if (_tierBlobs.TryGetValue(tier, out var cached))
            return cached;
        var blob = _paths.Find($"Config/file_chunk{tier}.index/0.idx") is { } p ? ReadAll(p) : null;
        _tierBlobs[tier] = blob;
        return blob;
    }

    private byte[] ReadAll(string path) => _storage.ReadRange(path, 0, (int)_storage.GetSize(path));
}
