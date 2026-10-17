using HomeBridge.BridgeTools;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

// #3415: native emits one row per journalled clock event, and the speed-matrix stop summary builds its legs from them.
[Collection(SpanWriters.Name)]
public sealed class ClockEventRowTests
{
    private static Common.ObservationContext Context(long tick) =>
        new() { Identity = new Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = tick, NativeGeneration = 3 };

    private static Clock.ClockEvent Hold(long cursor, long tick, long observedAt) => new()
    {
        Cursor = cursor, Context = Context(tick), ObservedAtUnixMs = observedAt, Detail = "Colonist downed.",
        Event = new Clock.HoldEntered
        {
            Reason = Clock.HoldReason.ColonistDowned, DetectedTick = tick - 4, OccurrenceTick = tick - 10,
            Evidence = new Clock.PawnEvent { PawnId = "Human12", Name = "Ann", Position = new Common.Cell { X = 5, Z = 6 } },
        },
    };

    private static Clock.ClockEvent Resume(long cursor, long tick, long observedAt) => new()
    {
        Cursor = cursor, Context = Context(tick), ObservedAtUnixMs = observedAt, Detail = "Clock resumed at Fast.",
        Event = new Clock.Resumed { Speed = Clock.Speed.Fast, From = Clock.HoldReason.ColonistDowned },
    };

    [Fact]
    public async Task HoldAndResumeEachWriteOneRowThatTheStopSummaryPairs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clock-event-rows-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = OtlpFileWriter.Start(new() { Dir = directory }))
            {
                var emitter = new TelemetryEmitter(writer);
                ClockEventRows.Write(emitter, Hold(1, 1000, 50_000), stopAtMs: 49_990);
                ClockEventRows.Write(emitter, Resume(2, 1000, 50_240), stopAtMs: null);
            }

            var rows = OtlpTimeline.Read(Path.Combine(directory, "logs.jsonl"));
            Assert.Equal([TelemetryKinds.ClockHold.Name, TelemetryKinds.ClockResume.Name], rows.Select(r => r.Kind));
            var hold = rows[0].Payload;
            Assert.Equal("ColonistDowned", hold[ClockEventTelemetry.ReasonKey]);
            Assert.Equal("pawn", hold["evidence"]);
            Assert.Equal("Human12", hold["pawn"]);
            Assert.Equal(996d, hold[ClockEventTelemetry.DetectedTickKey]);
            Assert.Equal(990d, hold[ClockEventTelemetry.OccurrenceTickKey]);
            Assert.Equal(49_990d, hold[ClockEventTelemetry.StopAtMsKey]);
            Assert.Equal("Colonist downed.", hold["msg"]);
            var resume = rows[1].Payload;
            Assert.Equal("ColonistDowned", resume[ClockEventTelemetry.FromKey]);
            Assert.Equal("Fast", resume["speed"]);
            Assert.Equal(50_240d, resume[ClockEventTelemetry.ObservedAtKey]);

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
        Clock.ClockEvent Of(Clock.ClockEventEvent e) => new() { Cursor = 1, Context = Context(5), ObservedAtUnixMs = 1, Detail = "", Event = e };
        var cases = new (Clock.ClockEventEvent Event, TelemetryKind Kind)[]
        {
            (new Clock.AuthorityChanged { Generation = 4, Reason = "acquired", Active = true }, TelemetryKinds.ClockAuthority),
            (new Clock.Alert { Key = "Alert_LowFood", Label = "Low food" }, TelemetryKinds.ClockAlert),
            (new Clock.Notification { Source = new Clock.Letter { Id = "Letter1", Label = "Raid" } }, TelemetryKinds.ClockNotification),
            (new Clock.Injury { Pawn = new Clock.PawnEvent { PawnId = "Human1", Name = "Bo" } }, TelemetryKinds.ClockInjury),
            (new Clock.HostilesCleared(), TelemetryKinds.ClockHostilesCleared),
            (new Clock.ObservationInvalidated { Families = [Clock.FactFamily.Research], Reason = "research" }, TelemetryKinds.ClockInvalidation),
            (new Clock.ForcePauseWaiting { Pause = new Clock.PauseEvidence() }, TelemetryKinds.ClockLongEvent),
            (new Clock.ForcePauseCleared(), TelemetryKinds.ClockLongEvent),
            (new Clock.PauseFailed { Pending = new Clock.HoldEntered { Reason = Clock.HoldReason.LetterPause } }, TelemetryKinds.ClockPauseFailed),
            (new Clock.HoldEntered { Reason = Clock.HoldReason.CombatEvent, Evidence = new Clock.CombatEventHold { Event = Clock.CombatEvent.Downed, ThingId = "Human1" } }, TelemetryKinds.ClockHold),
        };
        foreach (var (e, kind) in cases)
        {
            Assert.Same(kind, ClockEventRows.Row(Of(e), null).Kind);
        }

        var invalidation = ClockEventRows.Row(Of(new Clock.ObservationInvalidated { Families = [Clock.FactFamily.Research, Clock.FactFamily.Definitions], Reason = "research" }), null).Payload;
        Assert.Equal(["Research", "Definitions"], (List<object?>)invalidation["families"]!);
    }
}
