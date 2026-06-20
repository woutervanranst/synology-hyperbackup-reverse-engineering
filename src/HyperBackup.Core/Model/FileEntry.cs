namespace HyperBackup.Core.Model;

/// <summary>How a file's content is stored, which determines the restore path.</summary>
public enum StorageKind
{
    /// <summary>A directory (no content).</summary>
    Directory,

    /// <summary>Regular file with a content tag -> one or more Pool chunks (resolved via
    /// the file_chunk index; handles deduplicated/non-contiguous files).</summary>
    InlineTag,

    /// <summary>Large file (off_virtual_file == -1) -> an XZ blob in the file pool.</summary>
    FilePool,

    /// <summary>A tagless entry (Synology system files) located only via virtual_file.index. Not supported.</summary>
    VirtualFileIndex,

    /// <summary>Empty file (size 0) — nothing to restore.</summary>
    Empty,
}

/// <summary>
/// A node in a backed-up share's file tree, from the version_list table. Identity
/// and parent links use the 20-byte name_id_v2 / pname_id_v2 SHA-1 blobs (which
/// are never encrypted); the display name is decrypted when keys are available.
/// </summary>
public sealed class FileEntry
{
    public required string ShareName { get; init; }
    public required byte[] NameId { get; init; }
    public required byte[]? ParentId { get; init; }
    public required long OffVirtualFile { get; init; }

    /// <summary>The raw name from the DB (base64 ciphertext for encrypted backups).</summary>
    public required string RawName { get; init; }

    /// <summary>The decrypted/plaintext name (equals RawName for unencrypted backups).</summary>
    public required string Name { get; set; }

    public required long Size { get; init; }
    public required long Mode { get; init; }
    public required byte[]? Tag { get; init; }
    public required long MtimeSec { get; init; }
    public required long Inode { get; init; }

    /// <summary>Repository-relative path within the share, set during tree reconstruction.</summary>
    public string FullPath { get; set; } = "";

    public bool IsDirectory => (Mode & 0xF000) == 0x4000;

    /// <summary>True for Synology extended-attribute metadata entries (skippable for plain restore).</summary>
    public bool IsExtendedAttributeMeta =>
        Name.Contains("@SynoEAStream", StringComparison.Ordinal) ||
        Name.Equals("@eaDir", StringComparison.Ordinal) ||
        FullPath.Contains("@eaDir/", StringComparison.Ordinal);

    public string NameIdHex => Convert.ToHexString(NameId);

    /// <summary>MD5 of the content, from the inline tag (first 16 bytes), or null.</summary>
    public byte[]? TagMd5 => Tag is { Length: >= 16 } ? Tag[..16] : null;

    public StorageKind Kind
    {
        get
        {
            if (IsDirectory)
                return StorageKind.Directory;
            if (Size == 0)
                return StorageKind.Empty;
            if (Tag is { Length: >= 16 })
                return StorageKind.InlineTag;
            if (OffVirtualFile == -1)
                return StorageKind.FilePool;
            return StorageKind.VirtualFileIndex;
        }
    }
}
