namespace HyperBackup.Cli;

/// <summary>Minimal "--key value" / "--flag" argument parser for the PoC CLI.</summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _opts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _repeatedVersions = [];

    public string Command { get; }

    private Args(string command) => Command = command;

    public static Args Parse(string[] args)
    {
        var parsed = new Args(args.Length > 0 ? args[0] : "help");
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                continue;
            var key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var value = args[++i];
                if (key.Equals("hot-version", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var v))
                    parsed._repeatedVersions.Add(v);
                parsed._opts[key] = value;
            }
            else
            {
                parsed._flags.Add(key);
            }
        }
        return parsed;
    }

    public string? Get(string key, string? envVar = null)
    {
        if (_opts.TryGetValue(key, out var v))
            return v;
        return envVar is null ? null : Environment.GetEnvironmentVariable(envVar);
    }

    public string Require(string key)
        => Get(key) ?? throw new ArgumentException($"Missing required option --{key}");

    public bool Flag(string key) => _flags.Contains(key);

    public int? GetInt(string key) => int.TryParse(Get(key), out var v) ? v : null;

    public IReadOnlyList<int> HotVersions => _repeatedVersions;
}
