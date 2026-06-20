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
                // The tag is MD5(content_md5); the PoolReader indexes chunks by that key.
                var tag = file.TagMd5!;
                if (!_repo.Pool.TryLocateByTag(tag, out var loc))
                    return FileLocation.None(StorageKind.InlineTag,
                        $"chunk for tag {Convert.ToHexString(tag)} not found in any bucket index");
                return new FileLocation(StorageKind.InlineTag, [loc.BucketPath], loc, null, null);
            }

            case StorageKind.FilePool:
            {
                var id = ResolveFilePoolId(file);
                if (id is null)
                    return FileLocation.None(StorageKind.FilePool,
                        "could not map file to a file-pool blob from metadata");
                return new FileLocation(StorageKind.FilePool, [_repo.FilePool.BlobPath(id.Value)], null, id, null);
            }

            default:
                return FileLocation.None(StorageKind.VirtualFileIndex,
                    "content located via virtual_file.index B-tree (not implemented in this PoC)");
        }
    }

    /// <summary>Restore a file's original bytes. Throws if it cannot be located/restored.</summary>
    public byte[] Restore(int versionId, FileEntry file)
    {
        var location = Locate(file);
        return file.Kind switch
        {
            StorageKind.Empty => [],
            StorageKind.InlineTag => RestoreInlineTag(versionId, file, location),
            StorageKind.FilePool => RestoreFilePool(versionId, file, location),
            StorageKind.Directory => throw new InvalidOperationException($"'{file.Name}' is a directory."),
            _ => throw new NotSupportedException(
                $"'{file.Name}' is stored via the virtual_file.index B-tree, which this PoC does not yet parse."),
        };
    }

    private byte[] RestoreInlineTag(int versionId, FileEntry file, FileLocation location)
    {
        if (location.Chunk is not { } loc)
            throw new InvalidDataException(location.Note ?? "chunk not found.");

        var content = DecryptChunkContent(loc);

        // The bucket index stores the content MD5; the version_list tag is MD5(content MD5).
        VerifyMd5(content, loc.Entry.Md5, file.Name);
        if (file.TagMd5 is { } tag)
        {
            var doubleMd5 = MD5.HashData(loc.Entry.Md5);
            if (!doubleMd5.AsSpan().SequenceEqual(tag))
                throw new InvalidDataException($"Tag (double-MD5) mismatch restoring '{file.Name}'.");
        }
        VerifySize(content, file.Size, file.Name);
        return content;
    }

    private byte[] RestoreFilePool(int versionId, FileEntry file, FileLocation location)
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
                var xz = _repo.FilePool.ReadRawPayload(id);
                content = Xz.Decompress(xz);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Could not decompress file-pool blob for '{file.Name}' (Pool/file_pool/{id}.file.2): {ex.Message}. " +
                "The file-pool internal segment format is only partially reverse-engineered.", ex);
        }

        // Validate against the file-pool checksum (MD5 of original content).
        var entry = _repo.FilePool.Entries().FirstOrDefault(e => e.Id == id);
        if (entry is not null)
            VerifyMd5(content, entry.Checksum, file.Name);
        VerifySize(content, file.Size, file.Name);
        return content;
    }

    /// <summary>
    /// Read and (if encrypted) decrypt a chunk into its original bytes. Tries the
    /// candidate version keys, accepting the one whose decrypted content MD5 matches
    /// the bucket index (so the right key is found even for deduplicated chunks).
    /// </summary>
    private byte[] DecryptChunkContent(ChunkLocation loc)
    {
        var raw = _repo.Pool.ReadRawChunk(loc);
        if (!_repo.IsEncrypted)
            return raw;

        Exception? last = null;
        foreach (var vid in _repo.CandidateVersions(loc.BucketPath))
        {
            try
            {
                var content = _repo.GetDecryptor(vid).DecryptChunk(raw, (int)loc.Entry.DedupSize);
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
        if (entries.Count == 1)
            return entries[0].Id;

        return null;
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
