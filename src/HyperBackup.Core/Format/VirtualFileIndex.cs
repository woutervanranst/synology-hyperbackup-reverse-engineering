using System.Buffers.Binary;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core.Format;

/// <summary>
/// Reads "Config/virtual_file.index", a fixed-stride table of 56-byte per-file
/// metadata records (mtime/ctime, mode, …). The field this restorer needs is the
/// authoritative pointer to the file's chunk list: a record begins with a big-endian
/// u32 whose high 16 bits are the file_chunk tier (1–4) and is followed by a u32 that
/// is the byte offset of that file's chunk-list value inside
/// "Config/file_chunk{tier}.index". A file's <c>off_virtual_file</c> (from
/// version_list) is the byte offset of its record here.
/// </summary>
public sealed class VirtualFileIndex
{
    public const int EntrySize = 56;

    private readonly IBackupStorage _storage;
    private readonly RepoPaths _paths;
    private byte[]? _data;
    private bool _loaded;

    public VirtualFileIndex(IBackupStorage storage, RepoPaths paths)
    {
        _storage = storage;
        _paths = paths;
    }

    /// <summary>The (tier, chunk-list value offset) a file points at, or null if unavailable.</summary>
    public (int Tier, int ValueOffset)? Locate(long offVirtualFile)
    {
        if (offVirtualFile < 0 || Data() is not { } d)
            return null;
        var off = (int)offVirtualFile;
        if (off + EntrySize > d.Length)
            return null;
        var tier = (int)(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(off)) >> 16);
        var valueOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(off + 4));
        return tier is >= 1 and <= 4 ? (tier, valueOffset) : null;
    }

    private byte[]? Data()
    {
        if (_loaded)
            return _data;
        _loaded = true;
        if (_paths.Find("Config/virtual_file.index/0.idx") is { } p)
            _data = _storage.ReadRange(p, 0, (int)_storage.GetSize(p));
        return _data;
    }
}
