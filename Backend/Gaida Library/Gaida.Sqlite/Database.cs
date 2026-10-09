using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Gaida.Sqlite;

/// <summary>
///     What every service with a database shares: one file, a connection per operation from
///     Microsoft.Data.Sqlite's pool, and schema changes keyed off <c>PRAGMA user_version</c>.
/// </summary>
/// <remarks>
///     A connection per operation rather than one held open, because a <see cref="SqliteConnection" />
///     is not safe to share between threads and the pool makes opening one nearly free. Two writers
///     in one process wait on each other inside the provider — its command timeout is the busy timeout.
/// </remarks>
public static class Database
{
    static Database()
    {
        // Microsoft.Data.Sqlite writes all three as text, and Dapper has no way back from text on its own.
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new TimeSpanHandler());
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    /// <summary>
    ///     The connection string for the file at <paramref name="path" />, creating its directory. Foreign
    ///     keys are on, which SQLite leaves off unless every connection asks.
    /// </summary>
    public static string At(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        return new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
    }

    public static SqliteConnection Open(string connectionString)
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    /// <summary>
    ///     Runs <paramref name="step" /> when the file is older than <paramref name="version" />, and
    ///     stamps it with that version. Both commit together, so a crash halfway leaves the old version
    ///     and the next start runs the whole step again.
    /// </summary>
    /// <returns>Whether the step ran.</returns>
    public static bool Upgrade(SqliteConnection db, int version, Action<SqliteTransaction> step)
    {
        if (db.ExecuteScalar<int>("PRAGMA user_version") >= version) return false;

        // Persistent, and not allowed inside a transaction. Readers stop waiting on the writer.
        db.Execute("PRAGMA journal_mode = WAL");

        using var transaction = db.BeginTransaction();
        step(transaction);
        db.Execute($"PRAGMA user_version = {version}", transaction: transaction);
        transaction.Commit();
        return true;
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.Value = value;
        }

        public override DateTimeOffset Parse(object value)
        {
            return DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture);
        }
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.Value = value;
        }

        public override DateOnly Parse(object value)
        {
            return DateOnly.Parse((string)value, CultureInfo.InvariantCulture);
        }
    }

    private sealed class TimeSpanHandler : SqlMapper.TypeHandler<TimeSpan>
    {
        public override void SetValue(IDbDataParameter parameter, TimeSpan value)
        {
            parameter.Value = value;
        }

        public override TimeSpan Parse(object value)
        {
            return TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
        }
    }
}
