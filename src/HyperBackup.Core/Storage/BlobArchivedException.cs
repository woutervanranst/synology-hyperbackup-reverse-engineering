namespace HyperBackup.Core.Storage;

/// <summary>
/// Thrown when content of a blob is requested while it is in the Archive tier.
/// On real Azure such a read fails with HTTP 409; the caller must rehydrate first.
/// </summary>
public sealed class BlobArchivedException(string path)
    : Exception($"Blob '{path}' is in the Archive tier and cannot be read until it is rehydrated.")
{
    public string Path { get; } = path;
}
