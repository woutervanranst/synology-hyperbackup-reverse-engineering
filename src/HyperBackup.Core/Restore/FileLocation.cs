using HyperBackup.Core.Format;
using HyperBackup.Core.Model;

namespace HyperBackup.Core.Restore;

/// <summary>
/// The physical location of a file's content, resolved from metadata only (no
/// content reads). Used both to restore the file and to decide which blobs must
/// be rehydrated from Archive before a restore.
/// </summary>
public sealed record FileLocation(
    StorageKind Kind,
    IReadOnlyList<string> BlobPaths,
    ChunkLocation? Chunk,
    long? FilePoolId,
    string? Note)
{
    public bool IsResolved => BlobPaths.Count > 0;

    public static FileLocation None(StorageKind kind, string note) =>
        new(kind, [], null, null, note);
}
