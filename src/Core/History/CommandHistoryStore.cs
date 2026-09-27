using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Resesh.Core.Models;

namespace Resesh.Core.History;

/// <summary>Everything the history view needs before its first search: how many commands
/// are kept, which sessions they came from, and how many ran on each local day.</summary>
public sealed record CommandHistorySummary(
    int Count,
    IReadOnlyList<(Guid Id, string Name, string Target)> Sessions,
    IReadOnlyDictionary<DateOnly, int> CommandsPerDay);

/// <summary>
/// Command history in one SQLite database (<c>history.db</c>). A trigram FTS5 index over
/// the command, output, session, target, and folder answers any-substring searches without
/// loading history into memory; triggers keep it in step with the entry table. Deleted
/// rows are overwritten on disk (secure_delete) and the index is re-merged afterwards so
/// removed text does not linger in old index segments. Monthly <c>*.jsonl</c> files from
/// the first version of this store are imported once and then deleted.
/// </summary>
public sealed class CommandHistoryStore
{
    private const int SchemaVersion = 1;

    /// <summary>The trigram tokenizer needs at least three characters. Shorter terms are
    /// matched with a scan instead.</summary>
    private const int MinimumIndexedTermLength = 3;

    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private bool _initialized;

    /// <summary>Raised after entries are added, deleted, or cleared. May run on any thread.</summary>
    public event Action? Changed;

    public CommandHistoryStore(string directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _databasePath = Path.Combine(directory, "history.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resesh", "history");

    public string Directory => _directory;

    /// <summary>Failures a caller should report rather than crash on: the disk, permissions,
    /// a damaged database, or history written by a newer version.</summary>
    public static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException;

    public void Append(CommandHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = entry.Normalized();
        if (entry.Command.Length == 0)
            return;

        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            Insert(command, entry);
        }
        Changed?.Invoke();
    }

    /// <summary>Counts, sessions, and busy days, for filters and the calendar.</summary>
    public CommandHistorySummary Summary()
    {
        using var connection = OpenExisting();
        if (connection is null)
            return new CommandHistorySummary(0, [], new Dictionary<DateOnly, int>());

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM entry";
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        var sessions = new List<(Guid, string, string)>();
        using (var query = connection.CreateCommand())
        {
            // The newest row per session carries its current name.
            query.CommandText = """
                SELECT session_id, session_name, target FROM entry
                WHERE id IN (SELECT max(id) FROM entry WHERE session_id IS NOT NULL GROUP BY session_id)
                """;
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                    sessions.Add((id, reader.GetString(1), reader.GetString(2)));
            }
        }

        var days = new Dictionary<DateOnly, int>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT started_at FROM entry";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var day = DateOnly.FromDateTime(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).ToLocalTime().DateTime);
                days[day] = days.GetValueOrDefault(day) + 1;
            }
        }
        return new CommandHistorySummary(total, sessions, days);
    }

    /// <summary>Matching entries, most recently recorded first. Terms of three or more characters use the
    /// full-text index; shorter terms and the host:/in: filters scan.</summary>
    public CommandHistoryResults Search(
        CommandHistoryQuery query,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parsed = CommandHistorySearch.Parse(query.Text);
        using var connection = OpenExisting();
        if (connection is null)
            return new CommandHistoryResults([], 0, parsed.Terms);

        var where = new List<string>();
        var parameters = new List<SqliteParameter>();
        string Parameter(object value)
        {
            var name = "$p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new SqliteParameter(name, value));
            return name;
        }

        var status = parsed.Status != CommandHistoryStatus.Any ? parsed.Status : query.Status;
        if (status == CommandHistoryStatus.Failed)
            where.Add("e.exit_code IS NOT NULL AND e.exit_code <> 0");
        else if (status == CommandHistoryStatus.Succeeded)
            where.Add("e.exit_code = 0");
        if (parsed.ExitCode is { } exit)
            where.Add($"e.exit_code = {Parameter(exit)}");
        if (query.SessionId is { } sessionId)
            where.Add($"e.session_id = {Parameter(sessionId.ToString("D"))}");
        if (query.Since is { } since)
            where.Add($"e.started_at >= {Parameter(since.ToUnixTimeMilliseconds())}");
        if (query.Until is { } until)
            where.Add($"e.started_at < {Parameter(until.ToUnixTimeMilliseconds())}");
        foreach (var host in parsed.Hosts)
        {
            var value = Parameter(host);
            where.Add($"({Contains("e.session_name", value)} OR {Contains("e.target", value)})");
        }
        foreach (var directory in parsed.Directories)
            where.Add(Contains("e.directory", Parameter(directory)));

        var searchedColumns = query.SearchOutput
            ? new[] { "command", "output", "session_name", "target", "directory" }
            : new[] { "command", "session_name", "target", "directory" };
        var indexed = new List<string>();
        foreach (var term in parsed.Terms)
        {
            if (term.Length >= MinimumIndexedTermLength)
            {
                indexed.Add($"{{{string.Join(' ', searchedColumns)}}} : \"{term.Replace("\"", "\"\"")}\"");
            }
            else
            {
                var value = Parameter(term);
                where.Add("(" + string.Join(" OR ", searchedColumns.Select(column => Contains("e." + column, value))) + ")");
            }
        }

        // Newest first means most recently recorded: rowid order, which the full-text index
        // returns natively, so a LIMIT stops early instead of sorting every match.
        var from = "entry e";
        var order = "e.id DESC";
        var countSql = "";
        if (indexed.Count > 0)
        {
            var match = Parameter(string.Join(" AND ", indexed));
            // Without other filters the index alone can count, with no row lookups.
            if (where.Count == 0)
                countSql = $"SELECT count(*) FROM entry_fts WHERE entry_fts MATCH {match}";
            from = "entry_fts f JOIN entry e ON e.id = f.rowid";
            order = "f.rowid DESC";
            where.Insert(0, $"entry_fts MATCH {match}");
        }
        var whereClause = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        if (countSql.Length == 0)
            countSql = $"SELECT count(*) FROM {from} {whereClause}";

        cancellationToken.ThrowIfCancellationRequested();
        using var count = connection.CreateCommand();
        count.CommandText = countSql;
        count.Parameters.AddRange(parameters.Select(Clone));
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        var hits = new List<CommandHistoryHit>(Math.Min(total, limit));
        if (total > 0 && limit > 0)
        {
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT {EntryColumns("e")} FROM {from} {whereClause} ORDER BY {order} LIMIT {limit}";
            select.Parameters.AddRange(parameters.Select(Clone));
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                hits.Add(CommandHistorySearch.CreateHit(ReadEntry(reader), parsed.Terms, query.SearchOutput));
            }
        }
        return new CommandHistoryResults(hits, total, parsed.Terms);
    }

    /// <summary>Removes entries by id. Returns how many were removed.</summary>
    public int Delete(IEnumerable<string> ids)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
            return 0;
        var removed = 0;
        using (var connection = OpenExisting())
        {
            if (connection is null)
                return 0;
            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM entry WHERE uid = $uid";
                var uid = command.Parameters.Add("$uid", SqliteType.Text);
                foreach (var id in list)
                {
                    uid.Value = id;
                    removed += command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            if (removed > 0)
            {
                // The view refreshes now; the slower on-disk cleanup follows.
                Changed?.Invoke();
                Scrub(connection);
            }
        }
        return removed;
    }

    /// <summary>Deletes the whole history, database files included.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            SqliteConnection.ClearAllPools();
            _initialized = false;
            if (System.IO.Directory.Exists(_directory))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "history.db*")
                    .Concat(System.IO.Directory.EnumerateFiles(_directory, "*.jsonl*")).ToList())
                    File.Delete(file);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes entries that started more than <paramref name="retentionDays"/> ago.
    /// Returns whether anything was deleted.</summary>
    public bool Prune(int retentionDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        var cutoff = _clock().AddDays(-retentionDays).ToUnixTimeMilliseconds();
        int removed;
        using (var connection = OpenExisting())
        {
            if (connection is null)
                return false;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM entry WHERE started_at < $cutoff";
                command.Parameters.AddWithValue("$cutoff", cutoff);
                removed = command.ExecuteNonQuery();
            }
            if (removed > 0)
            {
                Changed?.Invoke();
                Scrub(connection);
            }
        }
        return removed > 0;
    }

    /// <summary>Total size of the history files on disk, in bytes.</summary>
    public long SizeOnDisk()
    {
        if (!System.IO.Directory.Exists(_directory))
            return 0;
        return System.IO.Directory.EnumerateFiles(_directory, "history.db*")
            .Concat(System.IO.Directory.EnumerateFiles(_directory, "*.jsonl"))
            .Sum(file => new FileInfo(file).Length);
    }

    /// <summary>Every entry, in the order recorded. For tests and diagnostics; the view searches instead.</summary>
    internal IReadOnlyList<CommandHistoryEntry> Snapshot()
    {
        using var connection = OpenExisting();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {EntryColumns("e")} FROM entry e ORDER BY e.id";
        using var reader = command.ExecuteReader();
        var entries = new List<CommandHistoryEntry>();
        while (reader.Read())
            entries.Add(ReadEntry(reader));
        return entries;
    }

    // ---- connection and schema ----

    /// <summary>Opens the database, creating it (and importing legacy files) on first use.</summary>
    private SqliteConnection Open()
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                System.IO.Directory.CreateDirectory(_directory);
                using var setup = Connect();
                CreateSchema(setup);
                ImportLegacyFiles(setup);
                _initialized = true;
            }
        }
        return Connect();
    }

    /// <summary>Opens the database only if history exists, so reads never create files.</summary>
    private SqliteConnection? OpenExisting()
    {
        if (!_initialized && !File.Exists(_databasePath) && !HasLegacyFiles())
            return null;
        return Open();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragmas = connection.CreateCommand();
        // secure_delete overwrites removed rows, so deleted history does not stay readable
        // in free pages. WAL lets a search read while a finished command is written.
        pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA secure_delete=ON;";
        pragmas.ExecuteNonQuery();
        return connection;
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current == SchemaVersion)
            return;
        if (current > SchemaVersion)
            throw new InvalidDataException("Command history was written by a newer version of resesh.");

        using var transaction = connection.BeginTransaction();
        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE entry (
                id INTEGER PRIMARY KEY,
                uid TEXT NOT NULL UNIQUE,
                session_id TEXT,
                session_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                target TEXT NOT NULL,
                directory TEXT,
                command TEXT NOT NULL,
                exit_code INTEGER,
                started_at INTEGER NOT NULL,
                offset_minutes INTEGER NOT NULL,
                ended_at INTEGER,
                output TEXT NOT NULL,
                output_truncated INTEGER NOT NULL,
                output_lost INTEGER NOT NULL,
                exact INTEGER NOT NULL
            );
            CREATE INDEX entry_started ON entry(started_at);
            CREATE INDEX entry_session ON entry(session_id, started_at);
            CREATE VIRTUAL TABLE entry_fts USING fts5(
                command, output, session_name, target, directory,
                content='entry', content_rowid='id', tokenize='trigram');
            CREATE TRIGGER entry_ai AFTER INSERT ON entry BEGIN
                INSERT INTO entry_fts(rowid, command, output, session_name, target, directory)
                VALUES (new.id, new.command, new.output, new.session_name, new.target, new.directory);
            END;
            CREATE TRIGGER entry_ad AFTER DELETE ON entry BEGIN
                INSERT INTO entry_fts(entry_fts, rowid, command, output, session_name, target, directory)
                VALUES ('delete', old.id, old.command, old.output, old.session_name, old.target, old.directory);
            END;
            CREATE TRIGGER entry_au AFTER UPDATE ON entry BEGIN
                INSERT INTO entry_fts(entry_fts, rowid, command, output, session_name, target, directory)
                VALUES ('delete', old.id, old.command, old.output, old.session_name, old.target, old.directory);
                INSERT INTO entry_fts(rowid, command, output, session_name, target, directory)
                VALUES (new.id, new.command, new.output, new.session_name, new.target, new.directory);
            END;
            PRAGMA user_version = 1;
            """;
        create.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>After deletes: merge the index so removed text leaves old segments, and
    /// fold the write-ahead log into the database so it holds no deleted pages either.</summary>
    private static void Scrub(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO entry_fts(entry_fts) VALUES('optimize'); PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    // ---- rows ----

    private static string EntryColumns(string alias) =>
        string.Join(", ", new[]
        {
            "uid", "session_id", "session_name", "kind", "target", "directory", "command", "exit_code",
            "started_at", "offset_minutes", "ended_at", "output", "output_truncated", "output_lost", "exact",
        }.Select(column => alias + "." + column));

    private static void Insert(SqliteCommand command, CommandHistoryEntry entry)
    {
        command.CommandText = """
            INSERT OR IGNORE INTO entry (uid, session_id, session_name, kind, target, directory, command, exit_code,
                started_at, offset_minutes, ended_at, output, output_truncated, output_lost, exact)
            VALUES ($uid, $session_id, $session_name, $kind, $target, $directory, $command, $exit_code,
                $started_at, $offset_minutes, $ended_at, $output, $output_truncated, $output_lost, $exact)
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$uid", entry.Id);
        command.Parameters.AddWithValue("$session_id", (object?)entry.SessionId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$session_name", entry.SessionName);
        command.Parameters.AddWithValue("$kind", entry.Kind.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$target", entry.Target);
        command.Parameters.AddWithValue("$directory", (object?)entry.WorkingDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$command", entry.Command);
        command.Parameters.AddWithValue("$exit_code", (object?)entry.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$started_at", entry.StartedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$offset_minutes", (int)entry.StartedAt.Offset.TotalMinutes);
        command.Parameters.AddWithValue("$ended_at", (object?)entry.EndedAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", entry.Output);
        command.Parameters.AddWithValue("$output_truncated", entry.OutputTruncated);
        command.Parameters.AddWithValue("$output_lost", entry.OutputLost);
        command.Parameters.AddWithValue("$exact", entry.Exact);
        command.ExecuteNonQuery();
    }

    private static CommandHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        var offset = TimeSpan.FromMinutes(reader.GetInt32(9));
        DateTimeOffset At(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(offset);
        return new CommandHistoryEntry
        {
            Id = reader.GetString(0),
            SessionId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            SessionName = reader.GetString(2),
            Kind = Enum.TryParse<SessionKind>(reader.GetString(3), ignoreCase: true, out var kind) ? kind : SessionKind.Ssh,
            Target = reader.GetString(4),
            WorkingDirectory = reader.IsDBNull(5) ? null : reader.GetString(5),
            Command = reader.GetString(6),
            ExitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            StartedAt = At(reader.GetInt64(8)),
            EndedAt = reader.IsDBNull(10) ? null : At(reader.GetInt64(10)),
            Output = reader.GetString(11),
            OutputTruncated = reader.GetBoolean(12),
            OutputLost = reader.GetBoolean(13),
            Exact = reader.GetBoolean(14),
        };
    }

    /// <summary>Case-insensitive substring test for short terms and filters. SQLite's lower()
    /// folds ASCII only; the full-text index handles longer terms with Unicode folding.</summary>
    private static string Contains(string column, string parameter) =>
        $"instr(lower(coalesce({column}, '')), lower({parameter})) > 0";

    private static SqliteParameter Clone(SqliteParameter parameter) => new(parameter.ParameterName, parameter.Value);

    // ---- one-time import of the JSON-lines store ----

    private bool HasLegacyFiles() =>
        System.IO.Directory.Exists(_directory) && LegacyFiles().Any();

    private IEnumerable<string> LegacyFiles() =>
        System.IO.Directory.EnumerateFiles(_directory, "*.jsonl")
            .Where(file => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    /// <summary>Moves monthly JSON-lines files into the database, then deletes them. Damaged
    /// lines (a torn write) are skipped; ids make a repeated import harmless.</summary>
    private void ImportLegacyFiles(SqliteConnection connection)
    {
        var files = LegacyFiles().ToList();
        if (files.Count == 0)
            return;

        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    CommandHistoryEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<CommandHistoryEntry>(line, LegacyJsonOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (entry is { Command.Length: > 0 })
                        Insert(command, entry.Normalized());
                }
            }
            transaction.Commit();
        }
        foreach (var file in files)
            File.Delete(file);
    }
}
