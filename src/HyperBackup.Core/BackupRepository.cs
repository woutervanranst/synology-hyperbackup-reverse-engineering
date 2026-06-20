using HyperBackup.Core.Crypto;
using HyperBackup.Core.Format;
using HyperBackup.Core.Model;
using HyperBackup.Core.Sqlite;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core;

/// <summary>
/// Top-level entry point: opens a HyperBackup "cloud image" repository over any
/// <see cref="IBackupStorage"/> backend, exposes its versions and file trees, and
/// provides the readers/keys needed to restore content.
/// </summary>
public sealed class BackupRepository
{
    /// <summary>name_id_v2 value used as the parent of every top-level entry.</summary>
    public const string RootSentinelHex = "5058F1AF5058F1AF8388633F609CADB75A75DC9D";

    private readonly string? _passphrase;
    private readonly byte[]? _externalRsaKey;
    private HyperBackupKeys? _keys;
    private bool _keysAttempted;

    public IBackupStorage Storage { get; }
    public RepoPaths Paths { get; }
    public PoolReader Pool { get; }
    public FilePool FilePool { get; }

    public bool IsEncrypted { get; }
    public bool IsCompressed { get; }
    public bool XattrEnabled { get; }
    public string? Unikey { get; }

    public BackupRepository(IBackupStorage storage, string? passphrase = null, byte[]? rsaPrivateKey = null)
    {
        Storage = storage;
        _passphrase = passphrase;
        _externalRsaKey = rsaPrivateKey;
        Paths = new RepoPaths(storage);
        Pool = new PoolReader(storage, Paths);
        FilePool = new FilePool(storage, Paths);

        var info = ReadBackupInfo();
        IsEncrypted = info.GetValueOrDefault("dataEnc") == "T";
        IsCompressed = info.GetValueOrDefault("dataComp") == "T";
        XattrEnabled = info.GetValueOrDefault("enableXattr") == "T";
        Unikey = ReadUnikey();
    }

    public static BackupRepository OpenLocal(string directory, string? passphrase = null, byte[]? rsaKey = null) =>
        new(new LocalDirectoryStorage(directory), passphrase, rsaKey);

    // --- Versions ---

    public IReadOnlyList<BackupVersion> ListVersions()
    {
        using var conn = SqliteStore.OpenReadOnly(Storage, Paths.Require("Config/version_info.db"));
        return SqliteStore.Query(conn,
            "SELECT id, timestamp, name, status, share, tag_db_file_size_thr, statistics FROM version_info ORDER BY id",
            r => new BackupVersion(
                Id: (int)r.GetInt64(0),
                Timestamp: r.GetInt64OrDefault(1),
                Name: r.GetStringOrNull(2),
                Status: r.GetStringOrNull(3),
                Shares: SplitShares(r.GetStringOrNull(4)),
                TagThreshold: r.GetInt64OrDefault(5, 1024),
                Statistics: r.GetStringOrNull(6)));
    }

    public BackupVersion GetVersion(int id) =>
        ListVersions().FirstOrDefault(v => v.Id == id)
        ?? throw new ArgumentException($"Version {id} not found.");

    // --- File listing ---

    /// <summary>List the full file tree for a version across all its shares.</summary>
    public IReadOnlyList<FileEntry> ListFiles(int versionId)
    {
        var version = GetVersion(versionId);
        var all = new List<FileEntry>();
        foreach (var share in version.Shares)
        {
            var dbPath = Paths.Find($"Config/@Share/{share}/{versionId}.db");
            if (dbPath is null)
                continue;
            var entries = ReadVersionList(share, dbPath);
            DecryptNames(entries);
            BuildPaths(entries);
            all.AddRange(entries);
        }
        return all;
    }

    private List<FileEntry> ReadVersionList(string share, string dbPath)
    {
        using var conn = SqliteStore.OpenReadOnly(Storage, dbPath);
        return SqliteStore.Query(conn,
            "SELECT name_id_v2, pname_id_v2, off_virtual_file, file_name, size, mode, tag, mtime_sec, inode " +
            "FROM version_list",
            r => new FileEntry
            {
                ShareName = share,
                NameId = (byte[])r.GetValue(0),
                ParentId = r.GetBlobOrNull(1),
                OffVirtualFile = r.GetInt64OrDefault(2, -1),
                RawName = r.GetString(3),
                Name = r.GetString(3),
                Size = r.GetInt64OrDefault(4),
                Mode = r.GetInt64OrDefault(5),
                Tag = r.GetBlobOrNull(6),
                MtimeSec = r.GetInt64OrDefault(7),
                Inode = r.GetInt64OrDefault(8),
            });
    }

    private void DecryptNames(List<FileEntry> entries)
    {
        if (!IsEncrypted)
            return;
        if (TryGetKeys() is not { } keys)
            return; // leave names as ciphertext if we have no keys
        var (fkey, fiv) = keys.FilenameKey();
        foreach (var e in entries)
        {
            try { e.Name = Decryptor.DecryptFilename(e.RawName, fkey, fiv); }
            catch { /* keep ciphertext name */ }
        }
    }

    private static void BuildPaths(List<FileEntry> entries)
    {
        var byId = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            byId[e.NameIdHex] = e;

        foreach (var e in entries)
        {
            var segments = new List<string>();
            var current = e;
            var guard = 0;
            while (current is not null && guard++ < 256)
            {
                segments.Add(current.Name);
                var parentHex = current.ParentId is null ? null : Convert.ToHexString(current.ParentId);
                if (parentHex is null || parentHex.Equals(RootSentinelHex, StringComparison.OrdinalIgnoreCase))
                    break;
                byId.TryGetValue(parentHex, out current);
            }
            segments.Reverse();
            e.FullPath = string.Join('/', segments);
        }
    }

    // --- Keys / decryption ---

    /// <summary>Returns the key set, or null if encryption keys are unavailable.</summary>
    public HyperBackupKeys? TryGetKeys()
    {
        if (_keysAttempted)
            return _keys;
        _keysAttempted = true;
        if (!IsEncrypted)
            return null;
        try { _keys = BuildKeys(); }
        catch { _keys = null; }
        return _keys;
    }

    /// <summary>Returns the key set, throwing a descriptive error if unavailable.</summary>
    public HyperBackupKeys RequireKeys() =>
        TryGetKeys() ?? throw BuildKeysError();

    private HyperBackupKeys BuildKeys()
    {
        if (Unikey is null)
            throw new InvalidOperationException("Could not determine 'unikey' from _Syno_TaskConfig.");

        if (_externalRsaKey is not null)
            return HyperBackupKeys.FromExportedKey(Unikey, _externalRsaKey);

        var encKeysPath = Paths.Find("Config/encKeys");
        if (_passphrase is not null && encKeysPath is not null)
        {
            using var s = Storage.OpenRead(encKeysPath);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return HyperBackupKeys.FromEncKeys(Unikey, ms.ToArray(), _passphrase);
        }

        throw BuildKeysError();
    }

    private InvalidOperationException BuildKeysError() => new(
        "Encryption keys are unavailable. Provide the passphrase (for a local-format encKeys.1) " +
        "or the RSA private key exported from the NAS (cloud-format encKeys.1 holds no key material).");

    private readonly Dictionary<int, Decryptor> _decryptorCache = new();

    /// <summary>Build (and cache) a per-version chunk/file decryptor (encrypted repos only).</summary>
    public Decryptor GetDecryptor(int versionId)
    {
        if (_decryptorCache.TryGetValue(versionId, out var cached))
            return cached;

        var vk = GetVersionKey(versionId);
        var decryptor = new Decryptor(vk, IsCompressed);
        _decryptorCache[versionId] = decryptor;
        return decryptor;
    }

    /// <summary>Unwrap the per-version AES key/IV (encrypted repos only).</summary>
    public VersionKey GetVersionKey(int versionId)
    {
        var keys = RequireKeys();
        using var conn = SqliteStore.OpenReadOnly(Storage, Paths.Require("Pool/vkey.db"));
        var rows = SqliteStore.Query(conn,
            $"SELECT rsa_vkey, rsa_vkey_iv, checksum FROM vkey WHERE version_id = {versionId}",
            r => (Key: (byte[])r.GetValue(0), Iv: (byte[])r.GetValue(1), Cksum: r.GetBlobOrNull(2)));
        if (rows.Count == 0)
            throw new InvalidDataException($"No vkey row for version {versionId}.");
        return keys.UnwrapVersionKey(rows[0].Key, rows[0].Iv, rows[0].Cksum);
    }

    /// <summary>
    /// A data blob's trailing generation ".N" maps to the version that wrote it
    /// (version_id = N - 1). Chunks are encrypted with the writing version's key, so
    /// deduplicated chunks must be decrypted with the key of the bucket that holds
    /// them — not the version being restored.
    /// </summary>
    public static int VersionForBlobGeneration(string blobPath)
    {
        var lastDot = blobPath.LastIndexOf('.');
        if (lastDot < 0 || !int.TryParse(blobPath[(lastDot + 1)..], out var generation))
            throw new InvalidDataException($"Cannot determine generation from blob path '{blobPath}'.");
        return generation - 1;
    }

    /// <summary>The decryptor for the version that wrote the given data blob.</summary>
    public Decryptor GetDecryptorForBlob(string blobPath) =>
        GetDecryptor(VersionForBlobGeneration(blobPath));

    /// <summary>
    /// Candidate version ids to try when decrypting a chunk in the given blob: the
    /// blob's own generation first (the common case), then every other version (to
    /// cover deduplicated chunks whose key differs). The correct key is confirmed by
    /// the chunk's content MD5.
    /// </summary>
    public IEnumerable<int> CandidateVersions(string blobPath)
    {
        int primary;
        try { primary = VersionForBlobGeneration(blobPath); }
        catch { primary = -1; }
        if (primary >= 0)
            yield return primary;
        foreach (var v in ListVersions())
            if (v.Id != primary)
                yield return v.Id;
    }

    // --- Low-level metadata reads ---

    private Dictionary<string, string> ReadBackupInfo()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Paths.Find("synobkpinfo.db");
        if (path is null)
            return dict;
        using var conn = SqliteStore.OpenReadOnly(Storage, path);
        foreach (var (k, v) in SqliteStore.Query(conn,
                     "SELECT info_name, info_value FROM backup_info_tb",
                     r => (r.GetString(0), r.GetStringOrNull(1) ?? "")))
            dict[k] = v;
        return dict;
    }

    private string? ReadUnikey()
    {
        var path = Paths.Find("_Syno_TaskConfig");
        if (path is null)
            return null;
        using var s = Storage.OpenRead(path);
        using var reader = new StreamReader(s);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("unikey=", StringComparison.Ordinal))
                return trimmed["unikey=".Length..].Trim().Trim('"');
        }
        return null;
    }

    private static IReadOnlyList<string> SplitShares(string? share) =>
        (share ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
