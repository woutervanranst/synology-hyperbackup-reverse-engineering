using System.Security.Cryptography;
using HyperBackup.Core.Format;
using HyperBackup.Core.Model;

namespace HyperBackup.Core.Restore;

/// <summary>
/// Restores a file's bytes, and (separately) resolves where a file's content lives
/// so callers can reason about archive tiers without reading the data.
/// </summary>
public sealed class FileRestorer
{
    private readonly BackupRepository _repo;

    public FileRestorer(BackupRepository repo) => _repo = repo;

    /// <summary>
    /// Resolve the data blob(s) backing a file, using metadata only. Returns an
    /// unresolved location (with a note) rather than throwing, so the archive/
    /// rehydrate planner can report gracefully.
    /// </summary>
    public FileLocation Locate(FileEntry file)
    {
        switch (file.Kind)
        {
            case StorageKind.Directory:
                return FileLocation.None(StorageKind.Directory, "directory (no content)");

            case StorageKind.Empty:
                return FileLocation.None(StorageKind.Empty, "empty file (no content)");

            case StorageKind.InlineTag:
            {
                var run = ResolveChunks(file);
                if (run is null || run.Count == 0)
                    return FileLocation.None(StorageKind.InlineTag,
                        "could not resolve the file's chunk list (very large files whose chunk " +
                        "list spans multiple B+-tree nodes are not yet supported)");
                var blobs = run.Select(c => c.BucketPath).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
                return new FileLocation(StorageKind.InlineTag, blobs, run, null, null);
            }

            case StorageKind.FilePool:
            {
                var id = ResolveFilePoolId(file);
                if (id is null)
                    return FileLocation.None(StorageKind.FilePool,
                        "could not map file to a file-pool blob from metadata");
                return new FileLocation(StorageKind.FilePool, [_repo.FilePool.BlobPath(id.Value)], [], id, null);
            }

            default:
                return FileLocation.None(StorageKind.VirtualFileIndex,
                    "content located via virtual_file.index (no inline tag — Synology system files only)");
        }
    }

    /// <summary>
    /// Resolve a file's ordered chunk list. Layered, most-authoritative first:
    ///  1. the virtual_file.index pointer → the exact file_chunk leaf (deterministic,
    ///     disambiguates files of identical size), validated by total size;
    ///  2. the file_chunk index matched by size + reproducible content tag;
    ///  3. the contiguous-run heuristic over the global chunk order (covers files not
    ///     present in the file_chunk index, and large contiguous files).
    /// </summary>
    private IReadOnlyList<ChunkLocation>? ResolveChunks(FileEntry file)
    {
        if (_repo.VirtualFiles.Locate(file.OffVirtualFile) is { } ptr
            && _repo.FileChunks.LeafAt(ptr.Tier, ptr.ValueOffset) is { } byPointer
            && byPointer.Sum(c => (long)c.Entry.DedupSize) == file.Size)
            return byPointer;

        return _repo.FileChunks.Lookup(file.Size, file.TagMd5)
               ?? _repo.Pool.ResolveFileChunks(file.Size, file.TagMd5!);
    }

    /// <summary>Restore a file's original bytes. Throws if it cannot be located/restored.</summary>
    public byte[] Restore(FileEntry file)
    {
        var location = Locate(file);
        return file.Kind switch
        {
            StorageKind.Empty => [],
            StorageKind.InlineTag => RestoreChunked(file, location),
            StorageKind.FilePool => RestoreFilePool(file, location),
            StorageKind.Directory => throw new InvalidOperationException($"'{file.Name}' is a directory."),
            _ => throw new NotSupportedException(
                $"'{file.Name}' has no inline tag (located via virtual_file.index); not supported in this PoC."),
        };
    }

    private byte[] RestoreChunked(FileEntry file, FileLocation location)
    {
        if (location.Chunks.Count == 0)
            throw new InvalidDataException(location.Note ?? "chunk run not resolved.");

        using var ms = new MemoryStream();
        foreach (var loc in location.Chunks)
            ms.Write(DecryptChunkContent(loc));

        var content = ms.ToArray();
        VerifySize(content, file.Size, file.Name);
        return content;
    }

    private byte[] RestoreFilePool(FileEntry file, FileLocation location)
    {
        if (location.FilePoolId is not { } id)
            throw new InvalidDataException(location.Note ?? "file-pool blob not found.");

        byte[] content;
        try
        {
            if (_repo.IsEncrypted)
            {
                var cipher = _repo.FilePool.ReadRawPayloadAtFixedOffset(id);
                var xz = _repo.GetDecryptorForBlob(_repo.FilePool.BlobPath(id)).DecryptFilePoolPayload(cipher);
                content = Xz.Decompress(xz);
            }
            else
            {
                content = Xz.Decompress(_repo.FilePool.ReadRawPayload(id));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Could not decompress file-pool blob for '{file.Name}' ({_repo.FilePool.BlobPath(id)}): {ex.Message}. " +
                "The file-pool internal segment format is only partially reverse-engineered.", ex);
        }

        var entry = _repo.FilePool.Entries().FirstOrDefault(e => e.Id == id);
        if (entry is not null)
            VerifyMd5(content, entry.Checksum, file.Name);
        VerifySize(content, file.Size, file.Name);
        return content;
    }

    /// <summary>
    /// Read and (if encrypted) decrypt one chunk into its original bytes, verifying
    /// the content MD5 against the bucket index. For encrypted repos this tries the
    /// candidate version keys and accepts the one that matches (handles deduplicated
    /// chunks whose key belongs to a different version than the bucket's generation).
    /// </summary>
    private byte[] DecryptChunkContent(ChunkLocation loc)
    {
        var raw = _repo.Pool.ReadRawChunk(loc);
        if (!_repo.IsEncrypted)
        {
            VerifyMd5(raw, loc.Entry.Md5, "chunk");
            return raw;
        }

        Exception? last = null;
        foreach (var vid in _repo.CandidateVersions(loc.BucketPath))
        {
            try
            {
                foreach (var content in _repo.GetDecryptor(vid).DecryptChunkCandidates(raw, (int)loc.Entry.DedupSize))
                    if (MD5.HashData(content).AsSpan().SequenceEqual(loc.Entry.Md5))
                        return content;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidDataException(
            $"No version key could decrypt chunk {loc.Entry.Md5Hex} in {loc.BucketPath}.", last);
    }

    private long? ResolveFilePoolId(FileEntry file)
    {
        var entries = _repo.FilePool.Entries();

        // Preferred: a content MD5 in the inline tag matches a file-pool checksum.
        if (file.TagMd5 is { } md5)
        {
            var hex = Convert.ToHexString(md5);
            var match = entries.FirstOrDefault(e => e.ChecksumHex.Equals(hex, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.Id;
        }

        // Fallback for the common single-large-file case.
        return entries.Count == 1 ? entries[0].Id : null;
    }

    private static void VerifyMd5(byte[] content, byte[] expected, string name)
    {
        var actual = MD5.HashData(content);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException(
                $"MD5 mismatch restoring '{name}': got {Convert.ToHexString(actual)}, expected {Convert.ToHexString(expected)}.");
    }

    private static void VerifySize(byte[] content, long expected, string name)
    {
        if (content.Length != expected)
            throw new InvalidDataException(
                $"Size mismatch restoring '{name}': got {content.Length}, expected {expected}.");
    }
}
