using HyperBackup.Core.Storage;
using Microsoft.Data.Sqlite;

namespace HyperBackup.Core.Sqlite;

/// <summary>
/// Opens HyperBackup's SQLite databases (which carry unusual ".db.1"/".db.2"
/// extensions but are ordinary SQLite files) read-only. SQLite needs a file path,
/// so for remote storage the blob is first downloaded via
/// <see cref="IBackupStorage.GetLocalCopy"/>.
/// </summary>
public static class SqliteStore
{
    public static SqliteConnection OpenReadOnly(IBackupStorage storage, string repoPath)
    {
        var local = storage.GetLocalCopy(repoPath);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = local,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    /// <summary>Run a query and project each row. Convenience for the small reads we do.</summary>
    public static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(map(reader));
        return results;
    }

    /// <summary>Read a BLOB column or null.</summary>
    public static byte[]? GetBlobOrNull(this SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return (byte[])reader.GetValue(ordinal);
    }

    public static string? GetStringOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long GetInt64OrDefault(this SqliteDataReader reader, int ordinal, long fallback = 0) =>
        reader.IsDBNull(ordinal) ? fallback : reader.GetInt64(ordinal);
}
