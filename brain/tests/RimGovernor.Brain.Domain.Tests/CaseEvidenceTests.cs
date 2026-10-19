using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.RemoteAccept;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3437: a case's output carries the brain's OTLP log and trace lines written since the case started and its
/// need_methods / need_transitions / transitions rows as bounded .jsonl, which the remote exporter accepts as public
/// text; the failure digest reads the same rows (#3601) and the run's source revision (#3594).
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

    private const long SinceMs = Since / 1_000_000;

    private static DateTime SinceTime => DateTime.UnixEpoch.AddTicks(Since / 100);

    // A kept game's store: the earlier case ran load L1 (food/cook, rest/sleep), the later one, from SinceMs, load L2
    // (food/cook, and joy/walk, which no transition names), whose review now stands. Need verdicts: the earlier case's
    // refused food, then the later case's shelter refused twice on granite and joy refused on capacity. Plan
    // transitions: L1's unsuccessful observe, a receipt before L2's first snapshot, then L2's prepare, refused receipt
    // (with native's refusal) and unsuccessful observe.
    private static string WriteStore(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Checkpoints.CheckpointStoreFile);
        PostmortemStore.EnsureProvider();
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE rounds(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload TEXT NOT NULL) STRICT;
            INSERT INTO rounds VALUES(1,'{"Revision":3,"Snapshot":{"Load":"L2","Native":4},"Tick":20,"Enabled":true}');
            CREATE TABLE plans(id TEXT PRIMARY KEY, retired INTEGER NOT NULL) STRICT;
            INSERT INTO plans VALUES('p1',0),('p2',0),('p3',0),('p4',0);
            CREATE TABLE need_methods(load_token TEXT NOT NULL, need_id TEXT NOT NULL, method_id TEXT NOT NULL, attempt INTEGER NOT NULL, plan_id TEXT NOT NULL, reason TEXT) STRICT;
            INSERT INTO need_methods VALUES('L1','food','cook',1,'p1',NULL),('L1','rest','sleep',1,'p2',NULL),('L2','food','cook',1,'p3',NULL),('L2','joy','walk',1,'p4',NULL);
            CREATE TABLE need_transitions(sequence INTEGER PRIMARY KEY AUTOINCREMENT, wall_ms INTEGER NOT NULL, tick INTEGER NOT NULL, need TEXT NOT NULL, verdict TEXT NOT NULL, cause TEXT NOT NULL, subject TEXT NOT NULL, method TEXT NOT NULL) STRICT;
            INSERT INTO need_transitions(wall_ms,tick,need,verdict,cause,subject,method) VALUES
              ($early,10,'food','refused','earlier case','','cook'),($early,10,'rest','earlier case','','','sleep'),($since,10,'food','open','','','cook'),
              ($since,12,'shelter','refused','shared_admission_refused','materials_short:BlocksGranite','build'),($since+1,14,'shelter','pursuing','','','build'),
              ($since+2,16,'shelter','refused','shared_admission_refused','materials_short:BlocksGranite','build'),($since+3,18,'joy','refused','unknown_needs_capacity','','walk'),
              ($since+5,20,'food','met','','','cook');
            CREATE TABLE actions(id TEXT PRIMARY KEY, kind TEXT NOT NULL) STRICT;
            CREATE TABLE transitions(sequence INTEGER PRIMARY KEY, action_id TEXT NOT NULL, payload TEXT NOT NULL) STRICT;
            INSERT INTO transitions VALUES
              (1,'a1','{"Kind":"observe","Snapshot":{"Load":"L1","Native":2},"Tick":5,"Observation":{"Effect":"unsuccessful","UnsuccessfulReason":"earlier case"}}'),
              (2,'a1','{"Kind":"receipt","Receipt":"refused","Tick":6}'),
              (3,'a3','{"Kind":"prepare","Snapshot":{"Load":"L2","Native":4},"Tick":7}'),
              (4,'a3','{"Kind":"receipt","Receipt":"refused","Tick":7,"Refusal":{"Code":"target_claimed","Reason":"cell/55/51","Class":"transient"}}'),
              (5,'a3','{"Kind":"observe","Snapshot":{"Load":"L2","Native":4},"Tick":9,"Observation":{"Effect":"unsuccessful","UnsuccessfulReason":"blocked"}}');
            """;
        cmd.Parameters.AddWithValue("$early", SinceMs - 1);
        cmd.Parameters.AddWithValue("$since", SinceMs);
        cmd.ExecuteNonQuery();
        return path;
    }

    private static List<string> Column(string output, string table, string column) =>
        [.. File.ReadAllLines(Path.Combine(output, CaseEvidence.StoreDir, table + ".jsonl")).Select(l => (string)JsonNode.Parse(l)![column]!)];

    // need_transitions are the case's by wall_ms; need_methods by load: the standing review's, so L2's joy/walk is
    // kept and L1's food/cook and rest/sleep are not.
    [Fact]
    public void StoreTablesDumpAsJsonLinesWindowedToTheCase()
    {
        var output = Path.Combine(root, "case");
        var store = WriteStore(output);

        CaseEvidence.DumpStore(new CaseRows(store, SinceTime), output);

        Assert.Equal(["L2", "L2"], Column(output, "need_methods", "load_token"));
        Assert.Equal(["cook", "walk"], Column(output, "need_methods", "method_id"));
        Assert.Equal(["open", "refused", "pursuing", "refused", "refused", "met"], Column(output, "need_transitions", "verdict"));

        // transitions are the case's from L2's first snapshot: its refused receipt keeps its tick and native's reason.
        var stages = File.ReadAllLines(Path.Combine(output, CaseEvidence.StoreDir, "transitions.jsonl")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal([3L, 4L, 5L], stages.Select(row => (long)row["sequence"]!));
        var receipt = JsonNode.Parse((string)stages[1]["payload"]!)!;
        Assert.Equal(7, (long)receipt["Tick"]!);
        Assert.Equal("target_claimed", (string)receipt["Refusal"]!["Code"]!);
        foreach (var table in new[] { "need_methods", "need_transitions", "transitions" })
        {
            RemoteExport.PublicDiagnostic($"store/{table}.jsonl", File.ReadAllBytes(Path.Combine(output, CaseEvidence.StoreDir, table + ".jsonl")));
        }
    }

    // A case whose brain filed nothing in its window cannot tell its review from an earlier case's: no methods.
    [Fact]
    public void ACaseWithNoFilingInItsWindowDumpsNoMethods()
    {
        var output = Path.Combine(root, "case");
        var store = WriteStore(output);

        CaseEvidence.DumpStore(new CaseRows(store, SinceTime.AddSeconds(1)), output);

        Assert.False(File.Exists(Path.Combine(output, CaseEvidence.StoreDir, "need_methods.jsonl")));
        Assert.False(File.Exists(Path.Combine(output, CaseEvidence.StoreDir, "need_transitions.jsonl")));
        Assert.False(File.Exists(Path.Combine(output, CaseEvidence.StoreDir, "transitions.jsonl")));
    }

    // A kept game writes the store of the earlier case that launched it: the later case dumps from that live path,
    // and a ring with nothing of the case beside a store the brain wrote is reported, not passed.
    [Fact]
    public void CaptureDumpsTheLiveStoreAndReportsAnEmptyLogSlice()
    {
        var store = WriteStore(Path.Combine(root, "earlier-case"));
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(game);
        File.WriteAllLines(OtlpFiles.RingPath(game, OtlpSignal.Logs), [LogLine(Since - 5, "earlier case")]);
        var output = Path.Combine(root, "later-case");
        Directory.CreateDirectory(output);

        var error = CaseEvidence.Capture(game, new CaseRows(store, SinceTime), output);

        Assert.Contains("Logs: no line since the case started", error, StringComparison.Ordinal);
        Assert.Equal(["open", "refused", "pursuing", "refused", "refused", "met"], Column(output, "need_transitions", "verdict"));
        Assert.False(File.Exists(Path.Combine(output, Checkpoints.CheckpointStoreFile)));
    }

    // The failure digest of the later case reads the same live store and the same case rows: L2's live methods, and
    // the plan transitions from L2's first snapshot on (its refused receipt included, L1's observe and receipt not).
    [Fact]
    public void DigestReadsTheCasesRowsOfTheLiveStore()
    {
        var store = WriteStore(Path.Combine(root, "earlier-case"));
        var output = Path.Combine(root, "later-case");
        Directory.CreateDirectory(output);

        var digest = Postmortems.Collect(output, [], new CaseRows(store, SinceTime), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["review revision 3 at tick 20 enabled=true", "need food: 1 live methods", "need joy: 1 live methods"],
            Assert.Single(digest.Sections, s => s.Name == "rounds").Lines.Select(l => l.Text));
        Assert.Equal(
            ["need shelter refused: shared_admission_refused materials_short:BlocksGranite x2, ticks 12..16", "need joy refused: unknown_needs_capacity at tick 18"],
            Assert.Single(digest.Sections, s => s.Name == "refused needs").Lines.Select(l => l.Text));
        var stages = Assert.Single(digest.Sections, s => s.Name == "unsuccessful plan stages").Lines;
        Assert.Equal(["service.sqlite transitions#4", "service.sqlite transitions#5"], stages.Select(l => l.Evidence));
        Assert.Equal("a3 () receipt refused at tick 7: target_claimed cell/55/51", stages[0].Text);
        Assert.Contains(
            Assert.Single(digest.Sections, s => s.Name == "authority generations").Lines,
            l => l.Text == "generation 4 first, 4 last, 0 flips across transitions");
    }

    // #3601: the store journals a receipt at its attempt's dispatch tick, so a refused receipt reads at that tick, with
    // native's reason, in the digest and in store/transitions.jsonl (never "at tick 0").
    [Fact]
    public void ARefusedReceiptKeepsItsDispatchTickAndReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = Path.Combine(root, "case");
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, Checkpoints.CheckpointStoreFile);
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
        var plan = new PlanSpec(new("refused"), new(1), [DomainAction.NewPawnActivitiesAction(new("refused-action"), new PawnActivities(1,
            [new("pawn", "assignment", "method", 0, 1, false, [new(PawnActivityKind.Hold, "", "", "", 0, 500)])]))]);
        using (var journal = RimGovernor.Brain.Store.Store.Open(path, ct))
        {
            IntentActionRules.RegisterIntentKinds();
            _ = journal.ReviewRounds(new RoundsRequest { Current = world, Tick = new(100), Enabled = true,
                Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
            _ = TestNeed.Commit(journal, world, new("method"), plan, ct);
            var scope = world with { Plan = plan.Id };
            var action = plan.Actions[0].Id;
            _ = journal.Prepare(plan.Id, action, scope, new(140), ct);
            var dispatched = journal.Dispatch(plan.Id, action, scope, new(150), ct);
            var recorded = Assert.Single(journal.RecordReceipts(
                [new BatchReceipt(plan.Id, action, dispatched.View.Attempt, Receipt.Refused) { Refusal = new("target_claimed", "cell/55/51", RefusalClass.Transient) }], ct));
            Assert.Null(recorded.Error);
        }

        SqliteConnection.ClearAllPools();
        var store = CaseRows.Whole(path);
        CaseEvidence.DumpStore(store, output);
        var digest = Postmortems.Collect(output, [], store, ct);

        Assert.Contains(
            Assert.Single(digest.Sections, s => s.Name == "unsuccessful plan stages").Lines,
            l => l.Text.EndsWith("receipt refused at tick 150: target_claimed cell/55/51", StringComparison.Ordinal));
        var receipt = File.ReadAllLines(Path.Combine(output, CaseEvidence.StoreDir, "transitions.jsonl"))
            .Select(l => JsonNode.Parse((string)JsonNode.Parse(l)!["payload"]!)!)
            .Single(p => (string)p["Kind"]! == "receipt");
        Assert.Equal(150, (long)receipt["Tick"]!);
        Assert.Equal("cell/55/51", (string)receipt["Refusal"]!["Reason"]!);
    }

    // #3594: a failed case's digest runs before Finalize, so it stamps the run's installed_package itself and the
    // revision section names the source revision.
    [Fact]
    public void AFailedCasesDigestShowsItsSourceRevision()
    {
        var output = Path.Combine(root, "case");
        Directory.CreateDirectory(output);
        var report = Acceptance.Report.Create("case", headless: true);
        report["error"] = "failed";
        var installed = RunState.InstalledPackage;
        RunState.InstalledPackage = new JsonObject { ["source_revision"] = "0123456789abcdef0123" };
        try
        {
            var digest = CaseRunner.Diagnose(output, report, CaseRows.Whole(Path.Combine(output, Checkpoints.CheckpointStoreFile)), TestContext.Current.CancellationToken);

            Assert.Equal("run binary built from 0123456789ab", Assert.Single(digest.Sections, s => s.Name == "revision").Lines[0].Text);
            Assert.Contains("run binary built from 0123456789ab", File.ReadAllText(Path.Combine(output, "diagnosis.txt")), StringComparison.Ordinal);
        }
        finally
        {
            RunState.InstalledPackage = installed;
        }
    }
}
