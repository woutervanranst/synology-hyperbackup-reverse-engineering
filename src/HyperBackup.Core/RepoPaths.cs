using System.Text.RegularExpressions;
using HyperBackup.Core.Storage;

namespace HyperBackup.Core;

/// <summary>
/// Resolves repository blob paths whose trailing ".N" generation suffix varies
/// per file (e.g. "version_info.db.4", a share's "2.db.3", "5.bucket.4"). Holds a
/// one-time listing of the repository so lookups don't hit the backend repeatedly.
/// </summary>
public sealed partial class RepoPaths
{
    private readonly List<string> _all;

    public RepoPaths(IBackupStorage storage) => _all = storage.List("").ToList();

    public IReadOnlyList<string> All => _all;

    /// <summary>Find a blob by its base name, ignoring any trailing ".&lt;generation&gt;".</summary>
    public string? Find(string baseNoGen)
    {
        foreach (var p in _all)
            if (p == baseNoGen || IsGenerationOf(p, baseNoGen))
                return p;
        return null;
    }

    public string Require(string baseNoGen) =>
        Find(baseNoGen) ?? throw new FileNotFoundException($"No blob found for '{baseNoGen}(.<gen>)'.");

    public bool Exists(string baseNoGen) => Find(baseNoGen) is not null;

    private static bool IsGenerationOf(string path, string baseNoGen)
    {
        if (!path.StartsWith(baseNoGen + ".", StringComparison.Ordinal))
            return false;
        var suffix = path[(baseNoGen.Length + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsDigit);
    }

    public IEnumerable<string> BucketIndexes() => _all.Where(IsBucketIndex);

    public static string BucketDataForIndex(string indexPath) =>
        indexPath.Replace(".index.", ".bucket.");

    // Generation-agnostic classifiers (suffix ".N" where N is digits).
    public static bool IsBucketIndex(string p) => BucketIndexRx().IsMatch(p);
    public static bool IsBucketData(string p) => BucketDataRx().IsMatch(p);
    public static bool IsFilePoolBlob(string p) =>
        p.StartsWith("Pool/file_pool/", StringComparison.Ordinal) && FilePoolRx().IsMatch(p);
    public static bool IsDataBlob(string p) => IsBucketData(p) || IsFilePoolBlob(p);

    [GeneratedRegex(@"(^|/)\d+\.index\.\d+$")] private static partial Regex BucketIndexRx();
    [GeneratedRegex(@"(^|/)\d+\.bucket\.\d+$")] private static partial Regex BucketDataRx();
    [GeneratedRegex(@"(^|/)\d+\.file\.\d+$")] private static partial Regex FilePoolRx();
}
