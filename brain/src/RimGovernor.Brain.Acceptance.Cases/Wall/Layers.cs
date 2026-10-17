using System.Text.Json.Nodes;
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

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var before = await s.FixtureAsync("layers-prepare", Ring, "prepare", cancellationToken).ConfigureAwait(false);
        if (before is not { Expected: RingCells, Frames: RingCells, Funded: RingCells, Standable: RingCells, Standing: 0 })
        {
            throw new AcceptanceException($"every layer must start funded, standable and unfinished: {before}");
        }

        // The fixture reads only a paused game, so the ring is audited between fixed tick steps rather than by a running probe.
        var ticks = 0;
        for (var row = before; row.Standing != RingCells || row.Frames != 0; ticks += Step)
        {
            if (s.TicksLeft < Step)
            {
                throw new AcceptanceException($"the three-thick ring did not finish within {ticks} ticks: {row}");
            }

            await s.RunTicksAsync("layers-step", Step, cancellationToken).ConfigureAwait(false);
            row = await s.FixtureAsync("layers-audit", Ring, "audit", cancellationToken).ConfigureAwait(false);
            if (row.Trapped != 0)
            {
                throw new AcceptanceException($"the wall ring trapped a colonist: {row}");
            }
        }

        s.Record("built_after_ticks", ticks);

        var controls = await s.FixtureAsync("layers-controls", Ring, "controls", cancellationToken).ConfigureAwait(false);
        s.RecordWire("completion_controls", controls);
        if (controls is not { Refused: true, Skipped: true, Completed: true, Escaped: true })
        {
            throw new AcceptanceException($"completion controls: {controls}");
        }
    }
}
