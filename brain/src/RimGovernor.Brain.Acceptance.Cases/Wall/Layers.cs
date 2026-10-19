using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge.Contracts;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/layers</c> (#2314): every frame of a funded three-thick wall ring with a gate starts standable and finishes
/// through native pawn work with no frame stranded and no colonist trapped on any read. A completion that would seal an
/// unfinished neighbour is refused and skipped; a pawn-on-frame completion relocates the pawn, who can still escape.
/// Native: frame standability, the Harmony completion veto and vanilla pawn displacement, which <c>test/wall_layers</c>
/// stages and probes in the lab.
/// </summary>
internal static class WallLayers
{
    private const string Tool = "test/wall_layers";

    private const int RingCells = 69;

    private const int Step = 300;

    private static readonly FixtureOp<string, Fx.WallLayersReply> Ring = new(
        Tool,
        action => WallKit.Args(("action", action)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.WallLayersReply>(reply.ToJsonString()) is { Success: true } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "wall/layers",
        Scope = "All 69 frames of a funded three-thick wall ring start funded, standable and unfinished and finish through native pawn "
            + "work with no colonist ever trapped (#2314); a sealing completion is refused and skipped, and a pawn-on-frame completion "
            + "relocates the pawn, who escapes.",
        Ticks = 24000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var before = await s.FixtureAsync("layers-prepare", Ring, "prepare", cancellationToken).ConfigureAwait(false);
        if (before is not { Expected: RingCells, Frames: RingCells, Funded: RingCells, Standable: RingCells, Standing: 0 })
        {
            throw new AcceptanceException($"every layer must start funded, standable and unfinished: {before}");
        }

        // Authority after staging: prepare sets the builders' work priorities, which native reads as player control and
        // revokes Auto under, so the clock run would refuse a generation granted before it.
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);

        // The completion guard is armed only while the supervisor runs the clock (#3596): a plain unpaused game builds
        // the ring unguarded and seals a builder standing on a frame. So the ring builds under native clock runs with the
        // brain's own watch policy, and the fixture, which reads only a paused game, audits it at each Step hold.
        var run = new Clock.RunRequest
        {
            ExpectedGeneration = generation,
            Speed = Clock.Speed.Ultrafast,
            Policy = ClockExpectation.WithHazardThresholds(new Clock.WatchPolicy
            {
                Mode = Clock.WatchMode.Colony,
                HealthDropFraction = .1f,
                MinHealthFraction = .5f,
                HostileWithin = 20f,
                InjuryStopCooldownMs = 0,
            }),
        };
        var ticks = 0;
        for (var row = before; row.Standing != RingCells || row.Frames != 0; ticks += Step)
        {
            if (s.TicksLeft < Step)
            {
                throw new AcceptanceException($"the three-thick ring did not finish within {ticks} ticks: {row}");
            }

            _ = await s.RunClockUntilHeldAsync("layers-step", Step, run, cancellationToken).ConfigureAwait(false);
            row = await s.FixtureAsync("layers-audit", Ring, "audit", cancellationToken).ConfigureAwait(false);
            if (row.Trapped != 0)
            {
                throw new AcceptanceException($"the wall ring trapped a colonist: {row}");
            }
        }

        s.Record("built_after_ticks", ticks);

        var controls = await s.FixtureAsync("layers-controls", Ring, "controls", cancellationToken).ConfigureAwait(false);
        s.RecordFixture("completion_controls", controls);
        if (controls is not { Refused: true, Skipped: true, Completed: true, Escaped: true })
        {
            throw new AcceptanceException($"completion controls: {controls}");
        }
    }
}
