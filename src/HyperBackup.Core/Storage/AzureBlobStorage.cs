using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzTier = Azure.Storage.Blobs.Models.AccessTier;
using AzRehydrate = Azure.Storage.Blobs.Models.RehydratePriority;

namespace HyperBackup.Core.Storage;

/// <summary>
/// Reads a HyperBackup repository directly from an Azure Blob Storage container,
/// and uses Azure's native Hot/Cool/Cold/Archive access tiers for the archive and
/// rehydrate features. An optional prefix locates the repository when it lives in a
/// subfolder of the container (e.g. "synology_1.hbk").
/// </summary>
public sealed class AzureBlobStorage : IBackupStorage
{
    private readonly BlobContainerClient _container;
    private readonly string _prefix; // normalized: "" or ends with '/'
    private readonly Dictionary<string, string> _localCopies = new(StringComparer.Ordinal);
    private readonly string _tempDir;

    public AzureBlobStorage(BlobContainerClient container, string prefix = "")
    {
        _container = container;
        _prefix = string.IsNullOrEmpty(prefix) ? "" : prefix.TrimEnd('/') + "/";
        _tempDir = Path.Combine(Path.GetTempPath(), "hbk-cache", container.Name);
        Directory.CreateDirectory(_tempDir);
    }

    public static AzureBlobStorage FromConnectionString(string connectionString, string container, string prefix = "")
        => new(new BlobServiceClient(connectionString).GetBlobContainerClient(container), prefix);

    public static AzureBlobStorage FromAccountKey(string account, string key, string container, string prefix = "")
    {
        var cred = new StorageSharedKeyCredential(account, key);
        var service = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
        return new AzureBlobStorage(service.GetBlobContainerClient(container), prefix);
    }

    /// <summary>A container-scoped SAS URL (https://acct.blob.core.windows.net/container?sv=...).</summary>
    public static AzureBlobStorage FromContainerSasUrl(string sasUrl, string prefix = "")
        => new(new BlobContainerClient(new Uri(sasUrl)), prefix);

    public string Description => $"azure:{_container.AccountName}/{_container.Name}/{_prefix}";

    private string BlobName(string path) => _prefix + path;

    private BlobClient Blob(string path) => _container.GetBlobClient(BlobName(path));

    public bool Exists(string path) => Blob(path).Exists();

    public long GetSize(string path) => Blob(path).GetProperties().Value.ContentLength;

    public IEnumerable<string> List(string prefix)
    {
        foreach (var item in _container.GetBlobs(BlobTraits.None, BlobStates.None, _prefix + prefix, CancellationToken.None))
            yield return item.Name[_prefix.Length..];
    }

    public IEnumerable<BlobInfo> ListInfos(string prefix)
    {
        foreach (var item in _container.GetBlobs(BlobTraits.None, BlobStates.None, _prefix + prefix, CancellationToken.None))
        {
            var name = item.Name[_prefix.Length..];
            var size = item.Properties.ContentLength ?? 0;
            var tier = MapTier(item.Properties.AccessTier?.ToString());
            var pending = item.Properties.ArchiveStatus?.ToString()
                ?.Contains("pending", StringComparison.OrdinalIgnoreCase) ?? false;
            yield return new BlobInfo(name, size, tier, pending);
        }
    }

    public Stream OpenRead(string path)
    {
        try
        {
            return Blob(path).OpenRead();
        }
        catch (RequestFailedException ex) when (IsArchived(ex))
        {
            throw new BlobArchivedException(path);
        }
    }

    public byte[] ReadRange(string path, long offset, int length)
    {
        try
        {
            var resp = Blob(path).DownloadStreaming(new BlobDownloadOptions { Range = new HttpRange(offset, length) });
            using var ms = new MemoryStream(length);
            resp.Value.Content.CopyTo(ms);
            return ms.ToArray();
        }
        catch (RequestFailedException ex) when (IsArchived(ex))
        {
            throw new BlobArchivedException(path);
        }
    }

    public string GetLocalCopy(string path)
    {
        if (_localCopies.TryGetValue(path, out var existing) && File.Exists(existing))
            return existing;
        var dest = Path.Combine(_tempDir, path.Replace('/', '_'));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Blob(path).DownloadTo(dest);
        _localCopies[path] = dest;
        return dest;
    }

    public AccessTier GetTier(string path)
    {
        var props = Blob(path).GetProperties().Value;
        return MapTier(props.AccessTier);
    }

    public bool IsRehydratePending(string path)
    {
        var status = Blob(path).GetProperties().Value.ArchiveStatus;
        return !string.IsNullOrEmpty(status) &&
               status.Contains("rehydrate-pending", StringComparison.OrdinalIgnoreCase);
    }

    public void SetTier(string path, AccessTier tier, RehydratePriority priority = RehydratePriority.Standard)
    {
        var az = tier switch
        {
            AccessTier.Hot => AzTier.Hot,
            AccessTier.Cool => AzTier.Cool,
            AccessTier.Cold => AzTier.Cold,
            AccessTier.Archive => AzTier.Archive,
            _ => AzTier.Hot,
        };
        var rp = priority == RehydratePriority.High ? AzRehydrate.High : AzRehydrate.Standard;
        Blob(path).SetAccessTier(az, rehydratePriority: rp);
    }

    private static bool IsArchived(RequestFailedException ex) =>
        ex.Status == 409 &&
        (ex.ErrorCode == "BlobArchived" || (ex.ErrorCode?.Contains("Archive", StringComparison.OrdinalIgnoreCase) ?? false));

    private static AccessTier MapTier(string? tier) => tier switch
    {
        "Hot" => AccessTier.Hot,
        "Cool" => AccessTier.Cool,
        "Cold" => AccessTier.Cold,
        "Archive" => AccessTier.Archive,
        _ => AccessTier.Unknown,
    };
}
