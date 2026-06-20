namespace HyperBackup.Core.Storage;

/// <summary>Storage access tiers, mirroring Azure Blob Storage's tiers.</summary>
public enum AccessTier
{
    Hot,
    Cool,
    Cold,
    Archive,
    Unknown,
}

/// <summary>Rehydration priority when bringing a blob back from <see cref="AccessTier.Archive"/>.</summary>
public enum RehydratePriority
{
    Standard,
    High,
}

/// <summary>Metadata about a single blob/object in the repository.</summary>
public sealed record BlobInfo(string Path, long Size, AccessTier Tier, bool RehydratePending);

/// <summary>
/// Abstraction over a HyperBackup repository's backing store.
///
/// Paths are repository-relative and use '/' separators, e.g.
/// "Pool/0/0/1.bucket.2" or "Config/version_info.db.2".
///
/// Implemented by <see cref="LocalDirectoryStorage"/> (a folder on disk) and by
/// an Azure Blob Storage backend. The interface deliberately exposes Azure's
/// access-tier concept so the archive/rehydrate features (tasks 3 and 4) work
/// uniformly; the local backend simulates tiers via a sidecar state file.
/// </summary>
public interface IBackupStorage
{
    /// <summary>A short human-readable description of where this repository lives.</summary>
    string Description { get; }

    bool Exists(string path);

    long GetSize(string path);

    /// <summary>Enumerate repository-relative paths under the given prefix (recursive).</summary>
    IEnumerable<string> List(string prefix);

    /// <summary>
    /// Enumerate blobs under a prefix together with size and tier in a single pass.
    /// For Azure this reads everything from one listing (no per-blob calls).
    /// </summary>
    IEnumerable<BlobInfo> ListInfos(string prefix);

    /// <summary>Open a seekable read stream for the whole blob. Throws if the blob is archived.</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// Read a byte range [offset, offset+length) without downloading the whole blob.
    /// Throws <see cref="BlobArchivedException"/> if the blob is in the Archive tier.
    /// </summary>
    byte[] ReadRange(string path, long offset, int length);

    /// <summary>
    /// Return a path to a local copy of the blob: the blob itself for local
    /// storage, or a downloaded temp file for remote storage. Used to hand a
    /// file path to SQLite, which cannot open a stream directly.
    /// </summary>
    string GetLocalCopy(string path);

    // --- Access tier operations ---

    AccessTier GetTier(string path);

    bool IsRehydratePending(string path);

    /// <summary>
    /// Change a blob's access tier. Moving from <see cref="AccessTier.Archive"/> to a
    /// hotter tier starts rehydration (which on real Azure takes hours for Standard
    /// priority, under an hour for High).
    /// </summary>
    void SetTier(string path, AccessTier tier, RehydratePriority priority = RehydratePriority.Standard);
}
