using HyperBackup.Core.Model;
using HyperBackup.Core.Restore;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core.Archive;

public enum TierAction { KeepHot, Archive }

/// <summary>Per-blob archive recommendation.</summary>
public sealed record BlobPlan(string Path, long Size, AccessTier CurrentTier, bool IsData, TierAction Action, string Reason);

/// <summary>The result of planning which blobs can move to the Archive tier (task 3).</summary>
public sealed record ArchivePlan(IReadOnlyList<BlobPlan> Blobs)
{
    public IEnumerable<BlobPlan> Archivable => Blobs.Where(b => b.Action == TierAction.Archive);
    public long TotalBytes => Blobs.Sum(b => b.Size);
    public long DataBytes => Blobs.Where(b => b.IsData).Sum(b => b.Size);
    public long ArchivableBytes => Archivable.Sum(b => b.Size);
    public long HotMetadataBytes => Blobs.Where(b => !b.IsData).Sum(b => b.Size);
}

/// <summary>One blob that must be rehydrated before a file can be restored.</summary>
public sealed record RehydrationItem(string BlobPath, long Size, AccessTier CurrentTier, bool NeedsRehydration);

/// <summary>The result of planning a single-file rehydration (task 4).</summary>
public sealed record RehydrationPlan(FileEntry File, IReadOnlyList<RehydrationItem> Items, string? Note)
{
    public IEnumerable<RehydrationItem> ToRehydrate => Items.Where(i => i.NeedsRehydration);
}

/// <summary>
/// Plans access-tier moves for the repository.
///
/// Policy: HyperBackup keeps all the metadata needed to browse versions and locate
/// chunks (SQLite DBs, binary indexes, counters, guard DBs) in a handful of small
/// blobs, while the bulk lives in chunk buckets (".bucket.2") and the file pool
/// (".file.2"). Keeping metadata Hot means you can always list files and *plan* a
/// restore instantly; the data blobs — needed only when actually restoring content
/// — are the archive candidates. A specific data blob can be kept Hot if it backs a
/// version you want instantly restorable ("hot versions").
/// </summary>
public sealed class ArchivePlanner
{
    private readonly BackupRepository _repo;
    private readonly FileRestorer _restorer;

    public ArchivePlanner(BackupRepository repo)
    {
        _repo = repo;
        _restorer = new FileRestorer(repo);
    }

    public static bool IsDataBlob(string path) => RepoPaths.IsDataBlob(path);

    /// <summary>
    /// Build an archive plan. Data blobs referenced by any version in
    /// <paramref name="hotVersionIds"/> are kept Hot; all other data blobs are
    /// archive candidates. Metadata is always kept Hot.
    /// </summary>
    public ArchivePlan Plan(IReadOnlyCollection<int>? hotVersionIds = null)
    {
        hotVersionIds ??= [];
        var mustStayHot = BlobsNeededByVersions(hotVersionIds);

        var plans = new List<BlobPlan>();
        foreach (var info in _repo.Storage.ListInfos("").OrderBy(i => i.Path, StringComparer.Ordinal))
        {
            var path = info.Path;
            var size = info.Size;
            var tier = info.Tier;
            var isData = IsDataBlob(path);

            TierAction action;
            string reason;
            if (!isData)
            {
                action = TierAction.KeepHot;
                reason = "metadata — required to browse versions and locate chunks";
            }
            else if (mustStayHot.Contains(path))
            {
                action = TierAction.KeepHot;
                reason = "data — referenced by a hot version";
            }
            else
            {
                action = TierAction.Archive;
                reason = hotVersionIds.Count == 0
                    ? "data — only needed when restoring content"
                    : "data — not referenced by any hot version";
            }

            plans.Add(new BlobPlan(path, size, tier, isData, action, reason));
        }
        return new ArchivePlan(plans);
    }

    /// <summary>The set of data blob paths needed to restore the given versions in full.</summary>
    public HashSet<string> BlobsNeededByVersions(IReadOnlyCollection<int> versionIds)
    {
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vid in versionIds)
        {
            foreach (var file in _repo.ListFiles(vid))
            {
                if (file.IsDirectory || file.Size == 0)
                    continue;
                foreach (var blob in _restorer.Locate(file).BlobPaths)
                    needed.Add(blob);
            }
        }
        return needed;
    }

    /// <summary>
    /// Plan what must be rehydrated to restore a single file (task 4): map the file
    /// to its data blob(s) and check each one's tier.
    /// </summary>
    public RehydrationPlan PlanFileRehydration(FileEntry file)
    {
        var location = _restorer.Locate(file);
        if (!location.IsResolved)
            return new RehydrationPlan(file, [], location.Note);

        var items = new List<RehydrationItem>();
        foreach (var blob in location.BlobPaths)
        {
            var tier = _repo.Storage.GetTier(blob);
            var pending = _repo.Storage.IsRehydratePending(blob);
            var needs = tier == AccessTier.Archive || pending;
            items.Add(new RehydrationItem(blob, _repo.Storage.GetSize(blob), tier, needs));
        }
        return new RehydrationPlan(file, items, null);
    }

    /// <summary>Execute a rehydration plan by moving the archived blobs to a hotter tier.</summary>
    public void ExecuteRehydration(RehydrationPlan plan, AccessTier target = AccessTier.Hot,
        RehydratePriority priority = RehydratePriority.Standard)
    {
        foreach (var item in plan.ToRehydrate)
            _repo.Storage.SetTier(item.BlobPath, target, priority);
    }
}
