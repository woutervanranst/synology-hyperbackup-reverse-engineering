using System.Buffers.Binary;
using HyperBackup.Core.Sqlite;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core.Format;

/// <summary>One entry of the file-pool checksum map.</summary>
public sealed record FilePoolEntry(long Id, byte[] Checksum, long Count)
{
    public string ChecksumHex => Convert.ToHexString(Checksum);
}

/// <summary>
/// Reads the file pool: large files stored as a custom-header + XZ-compressed
/// blob ("Pool/file_pool/{id}.file.2"), with a checksum map in
/// "file_pool_map.db.2". See README "File Pool Format (e235 abc8)".
/// </summary>
public sealed class FilePool
{
    public const uint Magic = 0xE235_ABC8;
    public const int DefaultPayloadOffset = 0x14C; // 332

    private readonly IBackupStorage _storage;
    private readonly RepoPaths _paths;
    private List<FilePoolEntry>? _entries;

    public FilePool(IBackupStorage storage, RepoPaths paths)
    {
        _storage = storage;
        _paths = paths;
    }

    public IReadOnlyList<FilePoolEntry> Entries()
    {
        if (_entries is not null)
            return _entries;

        var list = new List<FilePoolEntry>();
        var mapPath = _paths.Find("Pool/file_pool/file_pool_map.db");
        if (mapPath is not null)
        {
            using var conn = SqliteStore.OpenReadOnly(_storage, mapPath);
            list = SqliteStore.Query(conn,
                "SELECT id, checksum, count FROM file_pool_map ORDER BY id",
                r => new FilePoolEntry(r.GetInt64(0), (byte[])r.GetValue(1), r.GetInt64OrDefault(2)));
        }
        _entries = list;
        return _entries;
    }

    public IReadOnlyList<string> BlobPaths() =>
        _paths.All.Where(RepoPaths.IsFilePoolBlob).OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>The blob path for a file-pool id, resolving its generation suffix.</summary>
    public string BlobPath(long id) => _paths.Require($"Pool/file_pool/{id}.file");

    /// <summary>Read the raw payload of a file-pool blob (still encrypted if the repo is; always XZ-compressed).</summary>
    public byte[] ReadRawPayload(long id)
    {
        var path = BlobPath(id);
        using var stream = _storage.OpenRead(path);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var blob = ms.ToArray();

        if (blob.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(blob) != Magic)
            throw new InvalidDataException($"File-pool blob '{path}' has unexpected magic.");

        // The payload normally starts at 0x14C, but be robust: locate the XZ magic
        // (only valid for unencrypted repos; for encrypted repos the payload is AES
        // ciphertext and the fixed offset is used by the caller).
        var xzStart = Xz.FindStreamStart(blob);
        var start = xzStart >= 0 ? xzStart : DefaultPayloadOffset;
        return blob[start..];
    }

    /// <summary>Raw payload starting at the fixed header offset (used for encrypted repos).</summary>
    public byte[] ReadRawPayloadAtFixedOffset(long id)
    {
        var path = BlobPath(id);
        using var stream = _storage.OpenRead(path);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var blob = ms.ToArray();
        if (blob.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(blob) != Magic)
            throw new InvalidDataException($"File-pool blob '{path}' has unexpected magic.");
        return blob[DefaultPayloadOffset..];
    }
}
