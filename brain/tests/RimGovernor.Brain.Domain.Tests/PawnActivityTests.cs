using RimGovernor.Brain.Telemetry;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

[Collection(SpanWriters.Name)]
public sealed class PawnActivityTests
{
    private static PawnActivitySample Sample(long tick, string activity = "travel", int job = 1, string world = "load1") =>
        new(world, "pawn", tick, "Haul", job, activity, activity, "construction", "target", "1,2");

    [Fact]
    public void TravelWorkNeedsAndInterruptedJobsPartitionObservedTime()
    {
        var rows = new List<PawnActivityInterval>();
        var ledger = new PawnActivityLedger(rows.Add);
        ledger.Observe(Sample(0));
        ledger.Observe(Sample(15, "carrying"));
        ledger.End("pawn", 1, 20, "2,2", "InterruptForced");
        ledger.Observe(Sample(20, "needs", 2));
        ledger.Observe(Sample(35, "needs", 2));
        ledger.End("pawn", 2, 40, "2,2", "Succeeded");
        Assert.Equal(40, rows.Sum(r => r.EndTick - r.Start.Tick));
        Assert.Equal(new List<string> { "travel", "carrying", "needs" }, rows.Select(r => r.Start.Activity));
        Assert.Equal("InterruptForced", rows[1].EndReason);
        Assert.Equal(20, rows[2].Start.Tick);
    }

    [Fact]
    public void OldJobEndCannotCloseItsReplacement()
    {
        var rows = new List<PawnActivityInterval>();
        var ledger = new PawnActivityLedger(rows.Add);
        ledger.Observe(Sample(0));
        ledger.Observe(Sample(10, "unknown", 2));
        ledger.End("pawn", 1, 10, "1,2", "Succeeded");
        Assert.Equal(1, ledger.Count);
        ledger.End("pawn", 2, 20, "1,2", "Incompletable");
        Assert.Equal(20, rows.Sum(r => r.EndTick - r.Start.Tick));
    }

    [Fact]
    public void QuietIntervalsNeedNoHeartbeatAndLoadsNeverJoin()
    {
        var rows = new List<PawnActivityInterval>();
        var ledger = new PawnActivityLedger(rows.Add);
        ledger.Observe(Sample(0, "waiting"));
        ledger.Observe(Sample(10000, "waiting"));
        Assert.Empty(rows);
        ledger.Observe(Sample(10001, "work"));
        Assert.Equal(10001, Assert.Single(rows).EndTick);
        ledger.Observe(Sample(5, "unknown", world: "load2"));
        ledger.End("pawn", 1, 10, "", "Succeeded");
        Assert.Equal("load2", rows[^1].Start.World);
        Assert.Equal(5, rows[^1].Start.Tick);
    }

    [Fact]
    public void SameTickNestedTransitionsDoNotDoubleChargeTime()
    {
        var rows = new List<PawnActivityInterval>();
        var ledger = new PawnActivityLedger(rows.Add);
        ledger.Observe(Sample(0));
        ledger.Observe(Sample(7, "unknown"));
        ledger.Observe(Sample(7, "work"));
        ledger.End("pawn", 1, 9, "", "Succeeded");
        Assert.Equal(2, rows.Count);
        Assert.Equal(7, rows[0].EndTick);
        Assert.Equal(7, rows[1].Start.Tick);
        Assert.Equal(9, rows.Sum(r => r.EndTick - r.Start.Tick));
    }
    [Fact]
    public void UnsupportedJobsRetainRawIdentityAndHistoryIsBounded()
    {
        var rows = new List<PawnActivityInterval>();
        var ledger = new PawnActivityLedger(rows.Add, maxPawns: 1);
        ledger.Observe(Sample(0, "unknown") with { Job = "ModdedWork", Phase = "Mod.Driver:7" });
        ledger.Observe(Sample(0) with { Pawn = "other" });
        Assert.Equal(1, ledger.Count);
        Assert.Equal(1, ledger.RefusedPawns);
        ledger.End("pawn", 1, 10, "", "Succeeded");
        Assert.Equal("Mod.Driver:7", Assert.Single(rows).Start.Phase);
        Assert.Null(rows[0].Start.Method);
    }

    [Fact]
    public void ReportAccountsForMissingRecordsAndRejectsOverlap()
    {
        PawnActivityReportRow Row(long start, long end) => new("world", "pawn", start, end, "work", "craft", "bill", "checkpoint", false);
        var covered = PawnActivityReport.Cover(new[] { Row(10, 20), Row(30, 40) }, "world", "pawn", 0, 50);
        Assert.Equal(50, covered.Sum(r => r.End - r.Start));
        Assert.Equal(30, covered.Where(r => r.Gap).Sum(r => r.End - r.Start));
        Assert.Throws<InvalidDataException>(() => PawnActivityReport.Cover(new[] { Row(0, 20), Row(10, 30) }, "world", "pawn", 0, 50));
    }

    [Fact]
    public async Task EmittedIntervalsRoundTripThroughTheRealOtlpReader()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pawn-activity-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = RimGovernor.Brain.Telemetry.Otlp.OtlpFileWriter.Start(new() { Dir = directory }))
            {
                var emitter = new TelemetryEmitter(writer);
                PawnActivityTelemetry.Write(emitter, new(1, Sample(10), 20, "3,4", "Succeeded"));
            }
            var rows = PawnActivityReport.Read(Path.Combine(directory, "logs.jsonl"), "load1", "pawn", 0, 30);
            Assert.Equal(3, rows.Count);
            Assert.Equal("travel", rows[1].Activity);
            Assert.Equal("Succeeded", rows[1].Outcome);
            Assert.Equal(20, rows.Where(r => r.Gap).Sum(r => r.End - r.Start));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public void DespawnAndResetCloseOnlyObservedHistory()
    {
        var rows = new List<PawnActivityInterval>();
        var ledger = new PawnActivityLedger(rows.Add);
        ledger.Observe(Sample(0));
        ledger.Observe(Sample(15));
        ledger.Reset();
        Assert.Equal(15, Assert.Single(rows).EndTick);
        ledger.Observe(Sample(100));
        ledger.Remove("pawn", 110, "1,2", "despawn");
        Assert.Equal("despawn", rows[^1].EndReason);
        Assert.Equal(0, ledger.Count);
    }
}
