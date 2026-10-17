using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/stops</c> (#849): a combat-mode clock run armed with the downed and entered-range events holds the clock on
/// the exact tick of each, once per raider and direction, until a pawn goes down.
/// </summary>
internal static class CombatLabStops
{
    /// <summary>The most holds the raiders may take to close in and be fought: each armed event holds the clock once.</summary>
    private const int MaxHolds = 20;

    /// <summary>The club raiders start 20 cells from the riflemen; contact and a downing come well inside this.</summary>
    private const int FightTicks = 12_000;

    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-open");

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/stops",
        Scope = "Event-triggered combat holds (#849): on lab-open a combat run armed with downed and entered-range holds the clock on the exact tick "
            + "a raider's weapon reaches a colonist or a colonist's reaches a raider (the hold row's tick equals its occurrence and detected "
            + "ticks), once per raider and direction per combat, and a pawn going down holds on its tick.",
        Ticks = FightTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    // Acknowledged, the raiders' approach is not a hostile hold, and the health thresholds are off: only the armed events hold.
    private static Clock.WatchPolicy Policy(IEnumerable<string> hostiles) => new()
    {
        Mode = Clock.WatchMode.Combat,
        HealthDropFraction = 1,
        MinHealthFraction = 0.01f,
        HostileWithin = 40,
        InjuryStopCooldownMs = 0,
        AcknowledgedHostileIds = [.. hostiles],
        CombatStopEvents = [Clock.CombatEvent.Downed, Clock.CombatEvent.EnteredRange],
        SeriousSingleHitDamage = (float)PolicyFunctions.SeriousSingleHitDamage,
        SeriousSummaryHealthFloor = (float)PolicyFunctions.SeriousSummaryHealthFloor,
        SeriousBleedRateFloor = (float)PolicyFunctions.SeriousBleedRateFloor,
        SeriousVitalPartFloor = (float)PolicyFunctions.SeriousVitalPartFloor,
        ExplosiveNearMarginCells = (float)PolicyFunctions.ExplosiveNearMarginCells,
        MeleeReachCells = (float)PolicyFunctions.MeleeReachCells,
        InjurySeverityFloorTicks = (uint)PolicyFunctions.InjurySeverityFloorTicks,
        PredatorMarginCells = (float)PolicyFunctions.HuntPredatorMarginCells,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        var request = new Clock.RunRequest { ExpectedGeneration = generation, Speed = Clock.Speed.Superfast, Policy = Policy(lab.Hostiles()) };

        var entered = new HashSet<(string Thing, bool ReachedBy)>();
        var downed = false;
        for (var index = 0; index < MaxHolds && !downed; index++)
        {
            var label = "hold-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var held = await s.RunClockUntilHeldAsync(label, s.TicksLeft, request, cancellationToken).ConfigureAwait(false);
            if (held.Reason != Clock.HoldReason.CombatEvent)
            {
                throw new AcceptanceException($"{label}: not a combat-event hold: {held}");
            }

            // The clock_hold row carries what the status does not: the thing and the event's detected and occurrence ticks.
            var row = await s.HoldRowAsync(label, held, cancellationToken).ConfigureAwait(false);
            var tick = ClockRows.Tick(row);
            var detected = ClockRows.Whole(row, ColonyEventTelemetry.DetectedTickKey);
            var occurred = ClockRows.Whole(row, ColonyEventTelemetry.OccurrenceTickKey);
            var thing = ClockRows.Text(row, "thing");
            var reason = ClockRows.Text(row, "combat_reason");
            s.Record(label, CombatLabKit.Obj(
                ("event", held.CombatEvent?.ToString()),
                ("thing", thing),
                ("reason", reason),
                ("tick", tick),
                ("detectedTick", detected),
                ("occurrenceTick", occurred)));
            if (held.Context?.Tick != tick || detected != tick || occurred != tick)
            {
                throw new AcceptanceException($"{label}: combat hold off its event's tick: status {held.Context?.Tick}, row {tick}, detected {detected}, occurred {occurred}");
            }

            switch (held.CombatEvent)
            {
                case Clock.CombatEvent.EnteredRange:
                    // First time only: the combat remembers a raider across holds, per direction ("reaches" or "reached by").
                    var key = (thing, reason.Contains(" reached by ", StringComparison.Ordinal));
                    if (!entered.Add(key))
                    {
                        throw new AcceptanceException($"{label}: {key} entered range a second time: {reason}");
                    }

                    break;
                case Clock.CombatEvent.Downed:
                    if (index == 0)
                    {
                        throw new AcceptanceException($"the first hold was a downing before any entered range: {reason}");
                    }

                    downed = true;
                    break;
                default:
                    throw new AcceptanceException($"{label}: an unarmed combat event {held.CombatEvent} held the clock: {reason}");
            }
        }

        if (!downed)
        {
            throw new AcceptanceException($"no downed hold within {MaxHolds} holds: {entered.Count} entered-range holds");
        }
    }
}
