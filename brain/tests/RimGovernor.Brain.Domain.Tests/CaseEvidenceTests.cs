using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.RemoteAccept;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3437: a case's output carries the brain's OTLP log and trace lines written since the case started and its
/// need_methods / need_transitions rows as bounded .jsonl, which the remote exporter accepts as public text.
/// </summary>
public sealed class CaseEvidenceTests : IDisposable
{
    private const long Since = 1_700_000_000_000_000_000;

    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-caseevidence-" + Guid.NewGuid().ToString("N"));

    public CaseEvidenceTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string LogLine(long nanos, string body) =>
        $"{{\"resourceLogs\":[{{\"scopeLogs\":[{{\"logRecords\":[{{\"timeUnixNano\":\"{nanos}\",\"body\":{{\"stringValue\":\"{body}\"}}}}]}}]}}]}}";

    [Fact]
    public void SliceKeepsOnlyTheCasesLinesAcrossRotatedSegments()
    {
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        var ring = OtlpFiles.RingPath(game, OtlpSignal.Logs);
        File.WriteAllLines(ring + ".1", [LogLine(Since - 5, "earlier case"), LogLine(Since + 1, "rotated")]);
        File.WriteAllLines(ring, [LogLine(Since + 2, "active")]);
        var target = Path.Combine(root, "out", "otlp", OtlpFiles.Logs);

        CaseEvidence.SliceRing(ring, target, Since, DateTime.UnixEpoch, CaseEvidence.LogCapBytes);

        var bodies = new List<string>();
        OtlpReader.ReadLogs(target, log => bodies.Add(log.Body));
        Assert.Equal(["rotated", "active"], bodies);
        RemoteExport.PublicDiagnostic("otlp/logs.jsonl", File.ReadAllBytes(target));
    }

    [Fact]
    public void AnOverCapSliceKeepsTheNewestLinesAndSaysSo()
    {
        var ring = Path.Combine(root, OtlpFiles.Logs);
        File.WriteAllLines(ring, [.. Enumerable.Range(1, 10).Select(i => LogLine(Since + i, "row" + i))]);
        var target = Path.Combine(root, "out", OtlpFiles.Logs);
        var lineBytes = LogLine(Since + 10, "row10").Length + 1;

        CaseEvidence.SliceRing(ring, target, Since, DateTime.UnixEpoch, 3 * lineBytes);

        var lines = File.ReadAllLines(target);
        var marker = JsonNode.Parse(lines[0])![CaseEvidence.TruncatedKey]!;
        Assert.Equal(7, (long)marker["dropped_lines"]!);
        var bodies = new List<string>();
        OtlpReader.ReadLogs(target, log => bodies.Add(log.Body));
        Assert.Equal(["row8", "row9", "row10"], bodies);
    }

    private static DateTime SinceTime => DateTime.UnixEpoch.AddTicks(Since / 100);

    // A store file holding one need_methods row and need_transitions rows written at the given wall_ms.
    private static string WriteStore(string dir, params (long WallMs, string Verdict)[] transitions)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Checkpoints.CheckpointStoreFile);
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE need_methods(need_id TEXT NOT NULL, method_id TEXT NOT NULL, attempt INTEGER NOT NULL, reason TEXT) STRICT;
            INSERT INTO need_methods VALUES('food','cook',1,NULL);
            CREATE TABLE need_transitions(sequence INTEGER PRIMARY KEY AUTOINCREMENT, wall_ms INTEGER NOT NULL, tick INTEGER NOT NULL, need TEXT NOT NULL, verdict TEXT NOT NULL) STRICT;
            """;
        cmd.ExecuteNonQuery();
        foreach (var (wallMs, verdict) in transitions)
        {
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO need_transitions(wall_ms,tick,need,verdict) VALUES($w,10,'food',$v)";
            insert.Parameters.AddWithValue("$w", wallMs);
            insert.Parameters.AddWithValue("$v", verdict);
            insert.ExecuteNonQuery();
        }

        return path;
    }

    [Fact]
    public void StoreTablesDumpAsJsonLinesWithTransitionsWindowedToTheCase()
    {
        const long sinceMs = Since / 1_000_000;
        var output = Path.Combine(root, "case");
        var store = WriteStore(output, (sinceMs - 1, "earlier case"), (sinceMs, "open"), (sinceMs + 5, "met"));

        CaseEvidence.DumpStore(store, output, SinceTime);

        var methods = File.ReadAllLines(Path.Combine(output, CaseEvidence.StoreDir, "need_methods.jsonl"));
        Assert.Equal("cook", (string)JsonNode.Parse(Assert.Single(methods))!["method_id"]!);
        var transitionsPath = Path.Combine(output, CaseEvidence.StoreDir, "need_transitions.jsonl");
        var transitions = File.ReadAllLines(transitionsPath).Select(l => (string)JsonNode.Parse(l)!["verdict"]!).ToList();
        Assert.Equal(["open", "met"], transitions);
        RemoteExport.PublicDiagnostic("store/need_transitions.jsonl", File.ReadAllBytes(transitionsPath));
    }

    // A kept game writes the store of the earlier case that launched it: the later case dumps from that live path,
    // and a ring with nothing of the case beside a store the brain wrote is reported, not passed.
    [Fact]
    public void CaptureDumpsTheLiveStoreAndReportsAnEmptyLogSlice()
    {
        var store = WriteStore(Path.Combine(root, "earlier-case"), (Since / 1_000_000, "open"));
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllLines(OtlpFiles.RingPath(game, OtlpSignal.Logs), [LogLine(Since - 5, "earlier case")]);
        var output = Path.Combine(root, "later-case");
        Directory.CreateDirectory(output);

        var error = CaseEvidence.Capture(game, store, output, SinceTime);

        Assert.Contains("Logs: no line since the case started", error, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(Path.Combine(output, CaseEvidence.StoreDir, "need_transitions.jsonl")));
        Assert.False(File.Exists(Path.Combine(output, Checkpoints.CheckpointStoreFile)));
    }
}
