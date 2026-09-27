using Resesh.Core.History;
using Resesh.Core.Models;

namespace Resesh.Core.Tests;

public sealed class CommandHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "resesh-history-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        // Pooled connections keep the database open; release them before deleting it.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private CommandHistoryStore NewStore() => new(_directory, () => _now);

    private static CommandHistoryEntry Entry(
        string command,
        DateTimeOffset startedAt,
        string output = "",
        int? exit = 0,
        string session = "web01",
        string target = "deploy@10.0.0.5",
        string? cwd = null,
        Guid? sessionId = null) => new()
        {
            SessionId = sessionId,
            SessionName = session,
            Kind = SessionKind.Ssh,
            Target = target,
            WorkingDirectory = cwd,
            Command = command,
            ExitCode = exit,
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(2),
            Output = output,
            Exact = true,
        };

    [Fact]
    public void AppendedEntriesRoundTripThroughANewStore()
    {
        var store = NewStore();
        var sessionId = Guid.NewGuid();
        store.Append(Entry("ls -la", _now, "total 0\nfile.txt", cwd: "/srv/app", sessionId: sessionId));
        store.Append(Entry("false", _now.AddMinutes(1), exit: 1));

        var loaded = NewStore().Snapshot();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("ls -la", loaded[0].Command);
        Assert.Equal(sessionId, loaded[0].SessionId);
        Assert.Equal("/srv/app", loaded[0].WorkingDirectory);
        Assert.Equal("total 0\nfile.txt", loaded[0].Output);
        Assert.Equal(SessionKind.Ssh, loaded[0].Kind);
        Assert.True(loaded[0].Exact);
        Assert.True(loaded[1].Failed);
        Assert.Null(loaded[1].SessionId);
        Assert.Equal(TimeSpan.FromSeconds(2), loaded[0].Duration);
    }

    [Fact]
    public void StartTimesKeepTheirOffset()
    {
        var store = NewStore();
        var started = new DateTimeOffset(2026, 9, 1, 8, 30, 15, 250, TimeSpan.FromHours(10));
        store.Append(Entry("date", started));

        var loaded = NewStore().Snapshot().Single();

        Assert.Equal(started, loaded.StartedAt);
        Assert.Equal(TimeSpan.FromHours(10), loaded.StartedAt.Offset);
    }

    [Fact]
    public void EmptyCommandsAreNotKeptAndReadsCreateNoFiles()
    {
        var store = NewStore();
        store.Append(Entry("", _now));

        Assert.Empty(store.Snapshot());
        Assert.Equal(0, store.Summary().Count);
        Assert.Empty(store.Search(new CommandHistoryQuery()).Hits);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void OversizedOutputKeepsItsStartAndIsMarkedTruncated()
    {
        var store = NewStore();
        store.Append(Entry("cat big", _now, new string('x', CommandHistoryEntry.MaxOutputLength + 10)));

        var entry = NewStore().Snapshot().Single();

        Assert.Equal(CommandHistoryEntry.MaxOutputLength, entry.Output.Length);
        Assert.True(entry.OutputTruncated);
    }

    [Fact]
    public void MonthlyJsonFilesAreImportedOnceThenRemoved()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "2026-09.jsonl"),
            "{\"id\":\"a1\",\"sessionName\":\"web01\",\"kind\":\"ssh\",\"target\":\"root@web01\",\"command\":\"uptime\","
            + "\"exitCode\":0,\"startedAt\":\"2026-09-27T10:00:00+10:00\",\"output\":\"up 3 days\",\"exact\":true}\n"
            + "{\"command\":\"torn\n");
        File.WriteAllText(Path.Combine(_directory, "notes.jsonl"), "not history");

        var loaded = NewStore().Snapshot();

        var imported = Assert.Single(loaded);
        Assert.Equal("uptime", imported.Command);
        Assert.Equal("up 3 days", imported.Output);
        Assert.Equal(TimeSpan.FromHours(10), imported.StartedAt.Offset);
        Assert.False(File.Exists(Path.Combine(_directory, "2026-09.jsonl")));
        Assert.True(File.Exists(Path.Combine(_directory, "notes.jsonl")));
        Assert.Single(NewStore().Snapshot());
    }

    [Fact]
    public void DeletedTextIsGoneFromEveryFileOnDisk()
    {
        var store = NewStore();
        var drop = Entry("export TOKEN=hunter2-secret-value", _now, "token set: hunter2-secret-value");
        store.Append(Entry("keep", _now.AddMonths(-1)));
        store.Append(drop);
        store.Append(Entry("also kept", _now.AddSeconds(5)));

        Assert.Equal(1, store.Delete([drop.Id]));
        Assert.Equal(0, store.Delete([drop.Id]));

        Assert.Equal(["keep", "also kept"], NewStore().Snapshot().Select(e => e.Command));
        Assert.Empty(NewStore().Search(new CommandHistoryQuery { Text = "hunter2" }).Hits);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_directory))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(Contains(bytes, System.Text.Encoding.UTF8.GetBytes("hunter2-secret")), Path.GetFileName(file));
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void ClearDeletesEverything()
    {
        var store = NewStore();
        store.Append(Entry("a", _now));
        store.Append(Entry("b", _now.AddMonths(-2)));

        store.Clear();

        Assert.Empty(store.Snapshot());
        Assert.Empty(NewStore().Snapshot());
        Assert.Empty(Directory.GetFiles(_directory));

        store.Append(Entry("after clear", _now));
        Assert.Equal(["after clear"], NewStore().Snapshot().Select(e => e.Command));
    }

    [Fact]
    public void PruneDeletesEntriesOlderThanTheRetentionPeriod()
    {
        var store = NewStore();
        store.Append(Entry("ancient", _now.AddDays(-200)));
        store.Append(Entry("just too old", _now.AddDays(-90).AddMinutes(-1)));
        store.Append(Entry("just recent enough", _now.AddDays(-90).AddMinutes(1)));
        store.Append(Entry("recent", _now));
        var changed = 0;
        store.Changed += () => changed++;

        Assert.True(store.Prune(retentionDays: 90));
        Assert.False(store.Prune(retentionDays: 90));

        Assert.Equal(1, changed);
        Assert.Equal(["just recent enough", "recent"], NewStore().Snapshot().Select(e => e.Command));
    }

    [Fact]
    public void SummaryCountsDaysAndNamesSessionsByTheirNewestEntry()
    {
        var store = NewStore();
        var web = Guid.NewGuid();
        store.Append(Entry("a", _now.AddDays(-1), sessionId: web, session: "web-old"));
        store.Append(Entry("b", _now, sessionId: web, session: "web01"));
        store.Append(Entry("c", _now.AddMinutes(1)));

        var summary = store.Summary();

        Assert.Equal(3, summary.Count);
        var session = Assert.Single(summary.Sessions);
        Assert.Equal((web, "web01", "deploy@10.0.0.5"), session);
        Assert.Equal(3, summary.CommandsPerDay.Values.Sum());
        Assert.Equal(DateOnly.FromDateTime(_now.ToLocalTime().DateTime), summary.CommandsPerDay.Keys.Max());
    }

    public static TheoryData<string, bool> ParityQueries => new()
    {
        { "", true },
        { "nginx", true },
        { "NGINX systemctl", true },
        { "address", true },
        { "address", false },
        { "in", true },            // short term: scanned, not indexed
        { "ls db", true },
        { "\"already in use\"", true },
        { "host:web01", true },
        { "host:postgres@ in:postgresql", true },
        { "exit:fail", true },
        { "exit:ok nginx", true },
        { "exit:2", true },
        { "10.0.4.17", true },
        { "4.17", true },
        { "nginx -t", true },
        { "a\"b", true },
        { "AND OR NOT", true },
        { "*", true },
        { "zzz-not-there", true },
    };

    [Theory]
    [MemberData(nameof(ParityQueries))]
    public void StoreSearchMatchesTheReferenceSearch(string text, bool searchOutput)
    {
        var store = NewStore();
        var entries = new[]
        {
            Entry("systemctl status nginx", _now.AddMinutes(-9), "Active: active (running)", cwd: "/etc/nginx"),
            Entry("systemctl restart nginx", _now.AddMinutes(-8), "", exit: 1),
            Entry("journalctl -u nginx", _now.AddMinutes(-7), "bind() failed: Address already in use", exit: 2),
            Entry("nginx -t", _now.AddMinutes(-6), "syntax is ok"),
            Entry("ls", _now.AddMinutes(-5), "base\nglobal", session: "db-02", target: "postgres@db-02.lan", cwd: "/var/lib/postgresql"),
            Entry("ping -c1 10.0.4.17", _now.AddMinutes(-4), "64 bytes from 10.0.4.17", exit: null),
            Entry("echo 'a\"b'", _now.AddMinutes(-3), "a\"b"),
            Entry("uptime", _now.AddMinutes(-2), "load average: 0.10 AND 0.20 OR NOT"),
        };
        foreach (var entry in entries)
            store.Append(entry);
        var query = new CommandHistoryQuery { Text = text, SearchOutput = searchOutput };

        var expected = CommandHistorySearch.Search(store.Snapshot(), query);
        var actual = store.Search(query);

        Assert.Equal(expected.TotalMatches, actual.TotalMatches);
        Assert.Equal(expected.Hits.Select(h => h.Entry.Id), actual.Hits.Select(h => h.Entry.Id));
        Assert.Equal(expected.Hits.Select(h => h.OutputMatches), actual.Hits.Select(h => h.OutputMatches));
    }

    [Fact]
    public void StoreSearchAppliesSessionTimeAndLimit()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        for (var i = 0; i < 12; i++)
            store.Append(Entry($"echo {i}", _now.AddMinutes(i), sessionId: i % 2 == 0 ? id : null));

        var results = store.Search(new CommandHistoryQuery
        {
            Text = "echo",
            SessionId = id,
            Since = _now.AddMinutes(2),
            Until = _now.AddMinutes(10),
        }, limit: 2);

        Assert.Equal(4, results.TotalMatches); // 2, 4, 6, 8
        Assert.Equal(["echo 8", "echo 6"], results.Hits.Select(h => h.Entry.Command));
    }

    [Fact]
    public void RunsFindTheSameCommandOnTheSameHostOnly()
    {
        var store = NewStore();
        var core = Guid.NewGuid();
        var edge = Guid.NewGuid();
        var first = Entry("show ip route", _now, "r1", sessionId: core);
        var other = Entry("show ip route", _now.AddMinutes(1), "e1", sessionId: edge);
        var second = Entry("show  ip   route ", _now.AddMinutes(2), "r2", sessionId: core);
        var unrelated = Entry("show version", _now.AddMinutes(3), sessionId: core);
        foreach (var entry in new[] { first, other, second, unrelated })
            store.Append(entry);

        Assert.Equal([second.Id, first.Id], store.Runs(second).Select(e => e.Id));
        Assert.Equal(first.Id, store.PreviousRun(second)!.Id);
        Assert.Null(store.PreviousRun(first));
        Assert.Equal([other.Id], store.Runs(other).Select(e => e.Id));
    }

    [Fact]
    public void RunsOfUnsavedConnectionsMatchByTarget()
    {
        var store = NewStore();
        var a = Entry("uptime", _now, target: "root@10.0.0.5");
        var b = Entry("uptime", _now.AddMinutes(1), target: "root@10.0.0.6");
        var c = Entry("uptime", _now.AddMinutes(2), target: "root@10.0.0.5");
        foreach (var entry in new[] { a, b, c })
            store.Append(entry);

        Assert.Equal(a.Id, store.PreviousRun(c)!.Id);
    }

    [Fact]
    public void AVersionOneDatabaseIsUpgradedInPlace()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE entry (id INTEGER PRIMARY KEY, uid TEXT NOT NULL UNIQUE, session_id TEXT, session_name TEXT NOT NULL,
                    kind TEXT NOT NULL, target TEXT NOT NULL, directory TEXT, command TEXT NOT NULL, exit_code INTEGER,
                    started_at INTEGER NOT NULL, offset_minutes INTEGER NOT NULL, ended_at INTEGER, output TEXT NOT NULL,
                    output_truncated INTEGER NOT NULL, output_lost INTEGER NOT NULL, exact INTEGER NOT NULL);
                CREATE VIRTUAL TABLE entry_fts USING fts5(command, output, session_name, target, directory,
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
                INSERT INTO entry VALUES (1, 'old-1', NULL, 'web01', 'ssh', 'deploy@web01', NULL, 'df  -h', 0, 1790000000000, 600, NULL, 'sda1 40G', 0, 0, 1);
                PRAGMA user_version = 1;
                """;
            create.ExecuteNonQuery();
        }

        var store = NewStore();
        var next = Entry("df -h", _now, "sda1 41G", session: "web01", target: "deploy@web01");
        store.Append(next);

        Assert.Equal("old-1", store.PreviousRun(next)!.Id);
        Assert.Single(store.Search(new CommandHistoryQuery { Text = "sda1 40G" }).Hits);
        Assert.Equal(2, store.Search(new CommandHistoryQuery { Text = "sda1" }).TotalMatches);
    }

    [Fact]
    public void ParseSeparatesFiltersFromTerms()
    {
        var parsed = CommandHistorySearch.Parse("nginx host:web01 in:/etc exit:fail \"connection refused\" http://x");

        Assert.Equal(["nginx", "connection refused", "http://x"], parsed.Terms);
        Assert.Equal(["web01"], parsed.Hosts);
        Assert.Equal(["/etc"], parsed.Directories);
        Assert.Equal(CommandHistoryStatus.Failed, parsed.Status);
    }

    [Fact]
    public void ParseAcceptsQuotedFilterValuesAndExactExitCodes()
    {
        var parsed = CommandHistorySearch.Parse("host:\"core switch\" exit:127 exit:ok");

        Assert.Empty(parsed.Terms);
        Assert.Equal(["core switch"], parsed.Hosts);
        Assert.Equal(127, parsed.ExitCode);
        Assert.Equal(CommandHistoryStatus.Succeeded, parsed.Status);
    }

    [Fact]
    public void SearchReturnsNewestFirstAndRequiresEveryTerm()
    {
        var entries = new[]
        {
            Entry("systemctl status nginx", _now, "Active: active (running)"),
            Entry("systemctl restart nginx", _now.AddMinutes(1), "", exit: 1),
            Entry("journalctl -u nginx", _now.AddMinutes(2), "bind() failed: Address already in use"),
            Entry("uptime", _now.AddMinutes(3)),
        };

        var results = CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "nginx systemctl" });

        Assert.Equal(["systemctl restart nginx", "systemctl status nginx"], results.Hits.Select(h => h.Entry.Command));
        Assert.Equal(2, results.TotalMatches);
    }

    [Fact]
    public void SearchFindsOutputAndReportsASnippet()
    {
        var entries = new[]
        {
            Entry("journalctl -u nginx", _now, "line one\nnginx: bind() failed: Address already in use\nline three"),
        };

        var hit = CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "address" }).Hits.Single();

        Assert.Empty(hit.CommandSpans);
        Assert.Equal(1, hit.OutputMatches);
        Assert.Equal("nginx: bind() failed: Address already in use", hit.Snippet);
        Assert.Equal([new TextSpan(22, 7)], hit.SnippetSpans);
    }

    [Fact]
    public void SearchCanSkipOutput()
    {
        var entries = new[] { Entry("journalctl", _now, "Address already in use") };

        var results = CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "address", SearchOutput = false });

        Assert.Empty(results.Hits);
    }

    [Fact]
    public void SearchMatchesSessionAndDirectory()
    {
        var entries = new[]
        {
            Entry("ls", _now, session: "db-02", target: "postgres@db-02.lan", cwd: "/var/lib/postgresql"),
            Entry("ls", _now, session: "web01"),
        };

        Assert.Single(CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "ls db-02" }).Hits);
        Assert.Single(CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "host:postgres@" }).Hits);
        Assert.Single(CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "in:postgresql" }).Hits);
    }

    [Fact]
    public void SearchAppliesStatusSessionAndTimeFilters()
    {
        var id = Guid.NewGuid();
        var entries = new[]
        {
            Entry("old", _now.AddDays(-10), sessionId: id),
            Entry("failed", _now, exit: 2, sessionId: id),
            Entry("unknown", _now, exit: null, sessionId: id),
            Entry("elsewhere", _now, exit: 2),
        };

        var failed = CommandHistorySearch.Search(entries, new CommandHistoryQuery
        {
            SessionId = id,
            Status = CommandHistoryStatus.Failed,
            Since = _now.AddDays(-1),
        });
        var succeeded = CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "exit:ok" });
        var exact = CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "exit:2" });

        Assert.Equal(["failed"], failed.Hits.Select(h => h.Entry.Command));
        Assert.Equal(["old"], succeeded.Hits.Select(h => h.Entry.Command));
        Assert.Equal(2, exact.TotalMatches);
    }

    [Fact]
    public void SearchLimitsHitsButCountsEveryMatch()
    {
        var entries = Enumerable.Range(0, 30).Select(i => Entry($"echo {i}", _now.AddSeconds(i))).ToArray();

        var results = CommandHistorySearch.Search(entries, new CommandHistoryQuery { Text = "echo" }, limit: 5);

        Assert.Equal(5, results.Hits.Count);
        Assert.Equal(30, results.TotalMatches);
        Assert.Equal("echo 29", results.Hits[0].Entry.Command);
    }

    [Fact]
    public void FindAllMergesOverlappingTerms()
    {
        var spans = CommandHistorySearch.FindAll("systemctl restart", ["system", "stemctl", "restart"]);

        Assert.Equal([new TextSpan(0, 9), new TextSpan(10, 7)], spans);
    }

    [Fact]
    public void LongSnippetLinesAreCutAroundTheMatch()
    {
        var line = new string('a', 300) + " NEEDLE " + new string('b', 300);
        var hit = CommandHistorySearch.Search(
            [Entry("cat", _now, line)], new CommandHistoryQuery { Text = "needle" }).Hits.Single();

        Assert.StartsWith("…", hit.Snippet);
        Assert.EndsWith("…", hit.Snippet);
        Assert.Contains("NEEDLE", hit.Snippet);
        Assert.Single(hit.SnippetSpans);
        Assert.Equal("NEEDLE", hit.Snippet!.Substring(hit.SnippetSpans[0].Start, hit.SnippetSpans[0].Length));
    }
    [Fact]
    public void TargetNamesWhereEachKindOfSessionRuns()
    {
        Assert.Equal("deploy@web01", CommandHistoryEntry.TargetOf(new Session { Host = "web01", Username = "deploy" }));
        Assert.Equal("deploy@web01:2222", CommandHistoryEntry.TargetOf(new Session { Host = "web01", Username = "deploy", Port = 2222 }));
        Assert.Equal("console:2003", CommandHistoryEntry.TargetOf(new Session { Kind = SessionKind.Telnet, Host = "console", Port = 2003 }));
        Assert.Equal("console", CommandHistoryEntry.TargetOf(new Session { Kind = SessionKind.Telnet, Host = "console", Port = 23 }));
        Assert.Equal("pwsh.exe", CommandHistoryEntry.TargetOf(new Session
        {
            Kind = SessionKind.Local,
            Local = new LocalTarget { Executable = @"C:\Program Files\PowerShell\pwsh.exe" },
        }));
    }
    [Fact]
    public void UntilIsAnExclusiveEndForPickingOneDay()
    {
        var day = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            Entry("before", day.AddSeconds(-1)),
            Entry("start", day),
            Entry("end", day.AddDays(1).AddTicks(-1)),
            Entry("after", day.AddDays(1)),
        };

        var results = CommandHistorySearch.Search(entries, new CommandHistoryQuery { Since = day, Until = day.AddDays(1) });

        Assert.Equal(["end", "start"], results.Hits.Select(h => h.Entry.Command));
    }
}
