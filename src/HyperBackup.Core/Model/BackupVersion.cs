namespace HyperBackup.Core.Model;

/// <summary>A single backup version (restore point) from version_info.db.2.</summary>
public sealed record BackupVersion(
    int Id,
    long Timestamp,
    string? Name,
    string? Status,
    IReadOnlyList<string> Shares,
    long TagThreshold,
    string? Statistics)
{
    public DateTimeOffset TimestampUtc => DateTimeOffset.FromUnixTimeSeconds(Timestamp);
}
