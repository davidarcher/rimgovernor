using HomeBridge.BridgeTools;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Domain.Tests;

// #3415: native emits one row per colony event, and the speed-matrix stop summary builds its legs from them.
[Collection(SpanWriters.Name)]
public sealed class ColonyEventRowTests
{
    private static ColonyEventOrigin Origin(long tick, long observedAt) => new("colony", "load", 0, tick, observedAt);

    private static HoldEntered Hold(long tick, long observedAt) => new(Origin(tick, observedAt), "Colonist downed.", Clock.HoldReason.ColonistDowned,
        new PawnEvidence(new EventPawn("Human12", "Ann", 5, 6, null)), DetectedTick: tick - 4, OccurrenceTick: tick - 10, StopAtUnixMs: 49_990);

    private static Resumed Resume(long tick, long observedAt) => new(Origin(tick, observedAt), "Clock resumed at Fast.", Clock.Speed.Fast, Clock.HoldReason.ColonistDowned);

    [Fact]
    public async Task HoldAndResumeEachWriteOneRowThatTheStopSummaryPairs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "colony-event-rows-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = OtlpFileWriter.Start(new() { Dir = directory }))
            {
                var emitter = new TelemetryEmitter(writer);
                ColonyEventRows.Write(emitter, Hold(1000, 50_000));
                ColonyEventRows.Write(emitter, Resume(1000, 50_240));
            }

            var rows = OtlpTimeline.Read(Path.Combine(directory, "logs.jsonl"));
            Assert.Equal([TelemetryKinds.ClockHold.Name, TelemetryKinds.ClockResume.Name], rows.Select(r => r.Kind));
            var hold = rows[0].Payload;
            Assert.Equal("ColonistDowned", hold[ColonyEventTelemetry.ReasonKey]);
            Assert.Equal("pawn", hold["evidence"]);
            Assert.Equal("Human12", hold["pawn"]);
            Assert.Equal(996d, hold[ColonyEventTelemetry.DetectedTickKey]);
            Assert.Equal(990d, hold[ColonyEventTelemetry.OccurrenceTickKey]);
            Assert.Equal(49_990d, hold[ColonyEventTelemetry.StopAtMsKey]);
            Assert.Equal("Colonist downed.", hold["msg"]);
            var resume = rows[1].Payload;
            Assert.Equal("ColonistDowned", resume[ColonyEventTelemetry.FromKey]);
            Assert.Equal("Fast", resume["speed"]);
            Assert.Equal(50_240d, resume[ColonyEventTelemetry.ObservedAtKey]);

            var summary = StopSummaries.SummarizeHolds(rows, 0);
            Assert.Equal(1, summary.Holds);
            Assert.Equal(1, summary.Reasons["colonist_downed"]);
            var latency = Assert.Single(summary.Latencies);
            Assert.Equal(6, latency.DetectTicks);
            Assert.Equal(4, latency.HoldTicks);
            Assert.Equal(250, latency.ResumeMs);
            Assert.False(summary.ToJson().ContainsKey("mean_observe_ms"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void EveryEventFamilyHasItsRegisteredKind()
    {
        var o = Origin(5, 1);
        var pawn = new EventPawn("Human1", "Bo", 0, 0, null);
        var cases = new (ColonyEvent Event, TelemetryKind Kind)[]
        {
            (new AuthorityChanged(o, "", 4, "acquired", true), TelemetryKinds.ClockAuthority),
            (new AlertRaised(o, "", "Alert_LowFood", "Low food", null), TelemetryKinds.ClockAlert),
            (new LetterNotified(o, "", new EventLetter("Letter1", "Raid", null)), TelemetryKinds.ClockNotification),
            (new MessageNotified(o, "", "Message1", "Visitors"), TelemetryKinds.ClockNotification),
            (new InjuryObserved(o, "", new EventInjury(pawn, 0, 1)), TelemetryKinds.ClockInjury),
            (new HostilesCleared(o, ""), TelemetryKinds.ClockHostilesCleared),
            (new ObservationInvalidated(o, "", [Clock.FactFamily.Research], "research", [], null), TelemetryKinds.ClockInvalidation),
            (new ForcePauseWaiting(o, ""), TelemetryKinds.ClockLongEvent),
            (new ForcePauseCleared(o, ""), TelemetryKinds.ClockLongEvent),
            (new PauseFailed(o, "", Clock.HoldReason.LetterPause), TelemetryKinds.ClockPauseFailed),
            (new HoldEntered(o, "", Clock.HoldReason.CombatEvent, new CombatEvidence(Clock.CombatEvent.Downed, "Human1", "downed"), 5, null, 1), TelemetryKinds.ClockHold),
        };
        foreach (var (e, kind) in cases)
        {
            Assert.Same(kind, ColonyEventRows.Row(e).Kind);
        }

        var invalidation = ColonyEventRows.Row(new ObservationInvalidated(o, "", [Clock.FactFamily.Research, Clock.FactFamily.Definitions], "research", [], null)).Payload;
        Assert.Equal(["Research", "Definitions"], (List<object?>)invalidation["families"]!);
    }
}
