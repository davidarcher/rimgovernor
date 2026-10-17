using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/stops</c> (Go <c>stops.go</c>).</summary>
internal static class CombatLabStops
{
    // The longest the raiders may take to close in and be fought, in holds: each armed event holds the clock once.
    private const int MaxHolds = 20;

    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(90);

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/stops",
        Scope = "Event-triggered combat holds (#849): on lab-open a combat run armed with downed and entered-range holds the clock on the exact tick a raider's weapon reaches a colonist or a colonist's reaches a raider (the hold event's tick equals its occurrence tick), once per raider and direction per combat, and a colonist going down holds on its tick.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken).ConfigureAwait(false);
        var grant = await Authority.GrantAutoAsync(h.WireFunc(), "combat-stops-acquire", identity, cancellationToken).ConfigureAwait(false);
        var report = s.Report;
        var clock = new ScenarioClock(h, identity, new Report())
        {
            Grant = grant,

            // Acknowledged, the raiders' approach is not a hostile hold; the health thresholds are off so only armed
            // events hold the clock.
            CombatTargets = staged.Hostiles(),
            PolicyEdit = policy =>
            {
                policy["combatStopEvents"] = new JsonArray("COMBAT_EVENT_DOWNED", "COMBAT_EVENT_ENTERED_RANGE");
                policy["healthDropFraction"] = 1;
                policy["minHealthFraction"] = 0.01;
            },
        };
        var stops = new JsonArray();
        var sawRange = false;
        var sawDowned = false;
        var entered = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index <= MaxHolds && !sawDowned; index++)
        {
            try
            {
                _ = await clock.RunAsync("Superfast", cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"hold {CaseSupport.Tag(index)} run: {e.Message}", e);
            }

            JsonObject stop;
            try
            {
                stop = await AwaitHoldAsync(clock, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"hold {CaseSupport.Tag(index)}: {e.Message}", e);
            }

            stop["hold"] = index;
            stops.Add(stop.DeepClone());
            report["stops"] = stops;
            var tick = stop["tick"].AsUInt64() ?? 0;
            if (stop["kind"].AsString() != "combat_event")
            {
                throw new AcceptanceException($"hold {CaseSupport.Tag(index)}: unexpected hold: {stop.ToJson()}");
            }

            if (tick != (stop["occurrenceTick"].AsUInt64() ?? 0) || tick != (stop["detectedTick"].AsUInt64() ?? 0))
            {
                throw new AcceptanceException($"hold {CaseSupport.Tag(index)}: combat hold off its event's tick: {stop.ToJson()}");
            }

            switch (stop["event"].AsString())
            {
                case nameof(Clock.CombatEvent.EnteredRange):
                    // First time only: the combat remembers a raider across holds. Keyed by hostile and direction
                    // ("reaches" or "reached by").
                    var key = stop["thingId"].AsString() + (stop["detail"].AsString().Contains("reached by", StringComparison.Ordinal) ? "true" : "false");
                    if (!entered.Add(key))
                    {
                        throw new AcceptanceException($"hold {CaseSupport.Tag(index)}: {key} entered range a second time: {stop.ToJson()}");
                    }

                    sawRange = true;
                    break;
                case nameof(Clock.CombatEvent.Downed):
                    sawDowned = true;
                    break;
                default:
                    throw new AcceptanceException($"hold {CaseSupport.Tag(index)}: unarmed combat event held the clock: {stop.ToJson()}");
            }

            if (index == 0 && !sawRange)
            {
                throw new AcceptanceException($"first hold was {stop.ToJson()} before entered range");
            }
        }

        report["enteredRange"] = sawRange;
        report["downed"] = sawDowned;
        if (!sawDowned)
        {
            throw new AcceptanceException($"no downed hold after contact: {stops.ToJson()}");
        }
    }

    // awaitHold waits (beating) until the running clock is held, then returns its clock_hold row: kind, the armed event,
    // and the event's tick, detected and occurrence ticks.
    private static async Task<JsonObject> AwaitHoldAsync(ScenarioClock clock, CancellationToken cancellationToken)
    {
        var status = await clock.AwaitHeldAsync(HoldTimeout, "the combat hold", cancellationToken).ConfigureAwait(false);
        var row = await clock.AwaitHoldRowAsync(status, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["kind"] = ClockRows.Reason(row),
            ["detail"] = ClockRows.Text(row, "msg"),
            ["tick"] = ClockRows.Tick(row),
            ["detectedTick"] = ClockRows.Whole(row, ClockEventTelemetry.DetectedTickKey) ?? 0,
            ["occurrenceTick"] = ClockRows.Whole(row, ClockEventTelemetry.OccurrenceTickKey) ?? 0,
            ["event"] = ClockRows.Text(row, "combat_event"),
            ["thingId"] = ClockRows.Text(row, "thing"),
        };
    }
}
