using Microsoft.Data.Sqlite;

namespace TattleTail.Core;

/// <summary>Where finished minutes of traffic history are kept.</summary>
public interface IHistoryStore
{
    void Write(IReadOnlyList<HistoryRow> rows);

    /// <summary>Total bytes per time bucket, oldest first. Buckets with no traffic are left out.</summary>
    List<(long BucketMinute, long Sent, long Received)> GetSeries(long fromMinute, long toMinute, int bucketMinutes, string? program);

    /// <summary>
    /// Totals per program and remote address over a time window. <see cref="HistoryRow.Minute"/>
    /// holds the last minute that address was seen.
    /// </summary>
    List<HistoryRow> GetRemoteTotals(long fromMinute, long toMinute, string? program);

    int DeleteOlderThan(long minute);

    void DeleteAll();
}

/// <summary>
/// Saves per-minute traffic history in a small SQLite database file, so charts survive restarts.
/// </summary>
public sealed class HistoryStore : IHistoryStore, IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    public HistoryStore(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();

        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS traffic (
                minute   INTEGER NOT NULL,
                program  TEXT    NOT NULL,
                ip       TEXT    NOT NULL,
                host     TEXT    NOT NULL,
                sent     INTEGER NOT NULL,
                received INTEGER NOT NULL,
                PRIMARY KEY (minute, program, ip)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_traffic_program_minute ON traffic (program, minute);
            """);
    }

    /// <summary>The default location: %LOCALAPPDATA%\TattleTail\history.db</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TattleTail", "history.db");

    public void Write(IReadOnlyList<HistoryRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO traffic (minute, program, ip, host, sent, received)
                VALUES ($minute, $program, $ip, $host, $sent, $received)
                ON CONFLICT (minute, program, ip) DO UPDATE SET
                    sent = sent + excluded.sent,
                    received = received + excluded.received,
                    host = excluded.host;
                """;
            var minute = command.Parameters.Add("$minute", SqliteType.Integer);
            var program = command.Parameters.Add("$program", SqliteType.Text);
            var ip = command.Parameters.Add("$ip", SqliteType.Text);
            var host = command.Parameters.Add("$host", SqliteType.Text);
            var sent = command.Parameters.Add("$sent", SqliteType.Integer);
            var received = command.Parameters.Add("$received", SqliteType.Integer);

            foreach (var row in rows)
            {
                minute.Value = row.Minute;
                program.Value = row.Program;
                ip.Value = row.Ip;
                host.Value = row.Host;
                sent.Value = row.Sent;
                received.Value = row.Received;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Total bytes per time bucket, oldest first. Buckets with no traffic are left out.</summary>
    public List<(long BucketMinute, long Sent, long Received)> GetSeries(
        long fromMinute, long toMinute, int bucketMinutes, string? program)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT (minute / $bucket) * $bucket AS bucket, SUM(sent), SUM(received)
                FROM traffic
                WHERE minute >= $from AND minute < $to {(program is null ? "" : "AND program = $program")}
                GROUP BY bucket
                ORDER BY bucket;
                """;
            command.Parameters.AddWithValue("$bucket", Math.Max(1, bucketMinutes));
            command.Parameters.AddWithValue("$from", fromMinute);
            command.Parameters.AddWithValue("$to", toMinute);
            if (program is not null)
            {
                command.Parameters.AddWithValue("$program", program);
            }

            var result = new List<(long, long, long)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
            }

            return result;
        }
    }

    /// <summary>
    /// Totals per program and remote address over a time window. <see cref="HistoryRow.Minute"/>
    /// holds the last minute that address was seen.
    /// </summary>
    public List<HistoryRow> GetRemoteTotals(long fromMinute, long toMinute, string? program)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();

            // One pass over the window. When a query has a single MAX(), SQLite takes the other
            // plain columns from the row that held the maximum, so "host" is the name on the
            // newest row (https://sqlite.org/lang_select.html#bareagg). Looking the name up
            // separately for every server made a month of history take seconds to read.
            command.CommandText = $"""
                SELECT MAX(minute), program, ip, host, SUM(sent), SUM(received)
                FROM traffic
                WHERE minute >= $from AND minute < $to {(program is null ? "" : "AND program = $program")}
                GROUP BY program, ip;
                """;
            command.Parameters.AddWithValue("$from", fromMinute);
            command.Parameters.AddWithValue("$to", toMinute);
            if (program is not null)
            {
                command.Parameters.AddWithValue("$program", program);
            }

            var result = new List<HistoryRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new HistoryRow(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), reader.GetInt64(5)));
            }

            return result;
        }
    }

    public int DeleteOlderThan(long minute)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM traffic WHERE minute < $minute;";
            command.Parameters.AddWithValue("$minute", minute);
            return command.ExecuteNonQuery();
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            Execute("DELETE FROM traffic;");
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
