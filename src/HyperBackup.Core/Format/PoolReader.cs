using System.Security.Cryptography;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core.Format;

/// <summary>Where a chunk physically lives: which bucket blob and where inside it.</summary>
public sealed record ChunkLocation(string BucketPath, ChunkEntry Entry);

/// <summary>One chunk in global write order, with the values needed to resolve files.</summary>
public sealed record OrderedChunk(ChunkLocation Location, uint DedupSize, byte[] Md5);

/// <summary>
/// Reads the Pool's chunk store: the per-bucket chunk indexes, raw chunk bytes, and
/// a (bucket id, entry index) → chunk resolver used by <see cref="FileChunkIndex"/>.
///
/// Also offers <see cref="ResolveFileChunks"/>, a contiguous-run heuristic kept as a
/// last-resort fallback: most files are written as a contiguous run of whole chunks in
/// global write order (buckets by id, chunks by offset) whose <c>dedup_size</c>s sum to
/// the file size, with version_list <c>tag</c> = MD5(concatenated chunk content-MD5s)
/// disambiguating (a single chunk reduces to MD5(MD5(content))). The authoritative
/// mapping — which also covers deduplicated, non-contiguous files — is
/// <see cref="FileChunkIndex"/>; this heuristic only runs when that lookup cannot.
/// </summary>
public sealed class PoolReader
{
    private readonly IBackupStorage _storage;
    private readonly RepoPaths _paths;

    private Dictionary<int, Bucket>? _buckets;
    private List<OrderedChunk>? _ordered;
    private long[]? _prefix;                          // prefix[k] = sum of dedup sizes of chunks [0,k)
    private Dictionary<long, List<int>>? _prefixIndex; // prefix value -> indices

    public PoolReader(IBackupStorage storage, RepoPaths paths)
    {
        _storage = storage;
        _paths = paths;
    }

    private sealed record Bucket(string DataPath, IReadOnlyList<ChunkEntry> Entries);

    /// <summary>All buckets keyed by numeric id, each with its parsed chunk index.</summary>
    private Dictionary<int, Bucket> Buckets()
    {
        if (_buckets is not null)
            return _buckets;

        var map = new Dictionary<int, Bucket>();
        foreach (var bucketPath in _paths.All.Where(RepoPaths.IsBucketData))
        {
            var indexPath = bucketPath.Replace(".bucket.", ".index.");
            if (!_storage.Exists(indexPath))
                continue;
            map[BucketId(bucketPath)] = new Bucket(bucketPath, BucketIndex.Parse(ReadAll(indexPath)));
        }
        _buckets = map;
        return map;
    }

    /// <summary>Resolve a chunk by its bucket id and entry index, or null if out of range.</summary>
    public ChunkLocation? ResolveBucketEntry(int bucketId, int entryIndex)
    {
        if (!Buckets().TryGetValue(bucketId, out var b))
            return null;
        if (entryIndex < 0 || entryIndex >= b.Entries.Count)
            return null;
        return new ChunkLocation(b.DataPath, b.Entries[entryIndex]);
    }

    /// <summary>All chunks across all buckets, in global write order.</summary>
    public IReadOnlyList<OrderedChunk> OrderedChunks()
    {
        if (_ordered is not null)
            return _ordered;

        var list = new List<OrderedChunk>();
        foreach (var id in Buckets().Keys.OrderBy(x => x))
            foreach (var entry in Buckets()[id].Entries)
                list.Add(new OrderedChunk(new ChunkLocation(Buckets()[id].DataPath, entry), entry.DedupSize, entry.Md5));
        _ordered = list;
        return list;
    }

    /// <summary>
    /// Resolve a file (by size and tag) to its ordered chunk list, or null if it
    /// cannot be resolved from metadata (e.g. a deduplicated, non-contiguous file).
    /// </summary>
    public IReadOnlyList<ChunkLocation>? ResolveFileChunks(long size, byte[] tag)
    {
        EnsureResolver();
        var oc = _ordered!;
        var prefix = _prefix!;

        var candidates = new List<(int Start, int End)>();
        for (var j = 1; j <= oc.Count; j++)
        {
            var need = prefix[j] - size;
            if (need < 0)
                continue;
            if (!_prefixIndex!.TryGetValue(need, out var starts))
                continue;
            foreach (var i in starts)
            {
                if (i >= j)
                    continue;
                if (TagMatches(oc, i, j, tag))
                    return Slice(oc, i, j); // confident: size + tag both match
                candidates.Add((i, j));
            }
        }

        // Large files use a tiered tag the run doesn't reproduce; their run is unique.
        return candidates.Count == 1 ? Slice(oc, candidates[0].Start, candidates[0].End) : null;
    }

    /// <summary>Read the raw stored bytes of a chunk (still encrypted/compressed if the repo is).</summary>
    public byte[] ReadRawChunk(ChunkLocation loc) =>
        _storage.ReadRange(loc.BucketPath, loc.Entry.BucketOffset, (int)loc.Entry.ChunkSize);

    // --- internals ---

    private void EnsureResolver()
    {
        if (_prefix is not null)
            return;
        var oc = OrderedChunks();
        var prefix = new long[oc.Count + 1];
        var index = new Dictionary<long, List<int>>();
        for (var k = 0; k <= oc.Count; k++)
        {
            if (k > 0)
                prefix[k] = prefix[k - 1] + oc[k - 1].DedupSize;
            if (!index.TryGetValue(prefix[k], out var l))
                index[prefix[k]] = l = [];
            l.Add(k);
        }
        _prefix = prefix;
        _prefixIndex = index;
    }

    private static bool TagMatches(IReadOnlyList<OrderedChunk> oc, int start, int end, byte[] tag)
    {
        using var md5 = MD5.Create();
        for (var k = start; k < end; k++)
            md5.TransformBlock(oc[k].Md5, 0, oc[k].Md5.Length, null, 0);
        md5.TransformFinalBlock([], 0, 0);
        return md5.Hash!.AsSpan().SequenceEqual(tag);
    }

    private static List<ChunkLocation> Slice(IReadOnlyList<OrderedChunk> oc, int start, int end)
    {
        var run = new List<ChunkLocation>(end - start);
        for (var k = start; k < end; k++)
            run.Add(oc[k].Location);
        return run;
    }

    /// <summary>Numeric bucket id from a path like "Pool/0/0/12.bucket.3" -> 12.</summary>
    private static int BucketId(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.IndexOf('.');
        return dot > 0 && int.TryParse(name[..dot], out var id) ? id : 0;
    }

    private byte[] ReadAll(string path)
    {
        // Index files are metadata and never archived; read directly.
        var size = _storage.GetSize(path);
        return _storage.ReadRange(path, 0, (int)size);
    }
}
