using System.Security.Cryptography;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core.Format;

/// <summary>Where a chunk physically lives: which bucket blob and where inside it.</summary>
public sealed record ChunkLocation(string BucketPath, ChunkEntry Entry);

/// <summary>
/// Indexes all bucket chunks in the Pool and resolves a content MD5 to its
/// physical location, so a chunk can be read (and the owning blob identified for
/// archive/rehydrate decisions).
///
/// Buckets live as paired files "{N}.bucket.2" + "{N}.index.2" under "Pool/"
/// (typically "Pool/0/0/"). We enumerate every ".index.2" beneath Pool/ so the
/// reader works regardless of the group/subgroup layout.
/// </summary>
public sealed class PoolReader
{
    private readonly IBackupStorage _storage;
    private readonly RepoPaths _paths;
    private Dictionary<string, ChunkLocation>? _byContentMd5;
    private Dictionary<string, ChunkLocation>? _byTag;

    public PoolReader(IBackupStorage storage, RepoPaths paths)
    {
        _storage = storage;
        _paths = paths;
    }

    /// <summary>All bucket data blob paths ("Pool/.../{N}.bucket.&lt;gen&gt;").</summary>
    public IReadOnlyList<string> BucketPaths() =>
        _paths.All.Where(RepoPaths.IsBucketData).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private void EnsureIndex()
    {
        if (_byContentMd5 is not null)
            return;

        var byContent = new Dictionary<string, ChunkLocation>(StringComparer.OrdinalIgnoreCase);
        var byTag = new Dictionary<string, ChunkLocation>(StringComparer.OrdinalIgnoreCase);
        foreach (var indexPath in _paths.BucketIndexes())
        {
            // The chunk_index/ global dedup table is "*.idx.N"; the per-bucket index
            // is "*.index.N" with a sibling "*.bucket.N" (same generation).
            var bucketPath = RepoPaths.BucketDataForIndex(indexPath);
            if (!_storage.Exists(bucketPath))
                continue;

            var indexBytes = ReadAll(indexPath);
            foreach (var entry in BucketIndex.Parse(indexBytes))
            {
                var loc = new ChunkLocation(bucketPath, entry);
                byContent[entry.Md5Hex] = loc;
                // The version_list "tag" key is MD5(content_md5); index by it too so
                // files can be resolved to chunks without reading any chunk data.
                byTag[Convert.ToHexString(MD5.HashData(entry.Md5))] = loc;
            }
        }
        _byContentMd5 = byContent;
        _byTag = byTag;
    }

    /// <summary>Locate a chunk by its content MD5 (the inner MD5 stored in the bucket index).</summary>
    public bool TryLocate(byte[] contentMd5, out ChunkLocation location)
    {
        EnsureIndex();
        return _byContentMd5!.TryGetValue(Convert.ToHexString(contentMd5), out location!);
    }

    /// <summary>
    /// Locate a chunk by the version_list tag value, which equals MD5(content_md5).
    /// This is how small files reference their single chunk.
    /// </summary>
    public bool TryLocateByTag(byte[] tagMd5, out ChunkLocation location)
    {
        EnsureIndex();
        return _byTag!.TryGetValue(Convert.ToHexString(tagMd5), out location!);
    }

    /// <summary>Read the raw stored bytes of a chunk (still encrypted/compressed if the repo is).</summary>
    public byte[] ReadRawChunk(ChunkLocation loc) =>
        _storage.ReadRange(loc.BucketPath, loc.Entry.BucketOffset, (int)loc.Entry.ChunkSize);

    private byte[] ReadAll(string path)
    {
        // Index files are metadata and never archived; read directly.
        var size = _storage.GetSize(path);
        return _storage.ReadRange(path, 0, (int)size);
    }
}
