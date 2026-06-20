using System.Text.Json;

namespace HyperBackup.Core.Storage;

/// <summary>
/// Reads a HyperBackup repository from a local directory (e.g. a folder synced
/// down from Azure, or one of the sample backups in this repo).
///
/// Access tiers are simulated via a sidecar JSON file ("<c>.hbk-tierstate.json</c>")
/// at the repository root. This lets the archive/rehydrate features be demonstrated
/// fully offline: marking a blob as Archive makes reads of it throw, exactly as on
/// real Azure, and rehydrating restores access.
/// </summary>
public sealed class LocalDirectoryStorage : IBackupStorage
{
    private const string SidecarName = ".hbk-tierstate.json";

    private readonly string _root;
    private readonly string _sidecarPath;
    private readonly Dictionary<string, TierRecord> _tiers;

    public LocalDirectoryStorage(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"Backup directory not found: {_root}");
        _sidecarPath = Path.Combine(_root, SidecarName);
        _tiers = LoadSidecar();
    }

    public string Description => _root;

    private string FullPath(string path) =>
        Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(string path) => File.Exists(FullPath(path));

    public long GetSize(string path) => new FileInfo(FullPath(path)).Length;

    public IEnumerable<string> List(string prefix)
    {
        if (!Directory.Exists(_root))
            yield break;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (rel == SidecarName)
                continue;
            if (rel.StartsWith(prefix, StringComparison.Ordinal))
                yield return rel;
        }
    }

    public IEnumerable<BlobInfo> ListInfos(string prefix)
    {
        foreach (var p in List(prefix))
            yield return new BlobInfo(p, GetSize(p), GetTier(p), IsRehydratePending(p));
    }

    public Stream OpenRead(string path)
    {
        EnsureReadable(path);
        return File.OpenRead(FullPath(path));
    }

    public byte[] ReadRange(string path, long offset, int length)
    {
        EnsureReadable(path);
        using var fs = File.OpenRead(FullPath(path));
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[length];
        fs.ReadExactly(buf);
        return buf;
    }

    public string GetLocalCopy(string path)
    {
        // Metadata reads (SQLite, indexes) are always allowed; the archive policy
        // keeps all metadata Hot. Content blobs go through OpenRead/ReadRange which
        // enforce the tier check.
        return FullPath(path);
    }

    private void EnsureReadable(string path)
    {
        if (GetTier(path) == AccessTier.Archive)
            throw new BlobArchivedException(path);
    }

    // --- Tier simulation ---

    public AccessTier GetTier(string path) =>
        _tiers.TryGetValue(path, out var rec) ? rec.Tier : AccessTier.Hot;

    public bool IsRehydratePending(string path) =>
        _tiers.TryGetValue(path, out var rec) && rec.RehydratePending;

    public void SetTier(string path, AccessTier tier, RehydratePriority priority = RehydratePriority.Standard)
    {
        // Local simulation completes rehydration instantly (real Azure takes hours).
        // We still record the transition so callers can observe it.
        _tiers[path] = new TierRecord(tier, RehydratePending: false);
        SaveSidecar();
    }

    private Dictionary<string, TierRecord> LoadSidecar()
    {
        if (!File.Exists(_sidecarPath))
            return new(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText(_sidecarPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, TierRecord>>(json);
            return data is null ? new(StringComparer.Ordinal) : new(data, StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void SaveSidecar()
    {
        var json = JsonSerializer.Serialize(_tiers, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_sidecarPath, json);
    }

    private sealed record TierRecord(AccessTier Tier, bool RehydratePending);
}
