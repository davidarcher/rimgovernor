using System.Text.Json.Nodes;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/replace</c> (#2529): a BuildingIntent with replace_wall over a built steel wall of a roofed, enclosed room
/// swaps it for a wood wall through vanilla's replacement blueprint, and on every single tick from placement to
/// completion the cell is impassable and the room enclosed. A swap that would drop a roof's last holder, a cell with no
/// built wall and a non-wall replacement are refused with typed codes; a plain new-ring wall still gets the Standable
/// Frame. Native: the cloned Impassable Frame def, the Harmony frame choice, vanilla's wipe-and-spawn step and room
/// regions, which <c>test/wall_replacement</c> stages and audits tick by tick in the lab.
/// </summary>
internal static class WallReplace
{
    private const string Tool = "test/wall_replacement";

    private const int Window = 3000;

    private static readonly FixtureOp<(string Action, int X, int Z, int Ticks), Fx.WallReplacementReply> Cell = new(
        Tool,
        r => WallKit.Args(("action", r.Action), ("x", r.X), ("z", r.Z), ("ticks", r.Ticks)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.WallReplacementReply>(reply.ToJsonString()) is { Success: true, Centre: not null } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "wall/replace",
        Scope = "replace_wall over a built steel wall of a roofed enclosed room swaps it for a wood wall with the cell impassable and the "
            + "room enclosed on every tick (#2529); no wall, a non-wall replacement and a roof's last holder are refused with typed codes; "
            + "a plain new-ring wall keeps the Standable Frame.",
        Ticks = 2 * Window,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var prepared = await s.FixtureAsync("replace-prepare", Cell, ("prepare", 0, 0, 0), cancellationToken).ConfigureAwait(false);
        var (cx, cz) = (prepared.Centre!.X, prepared.Centre.Z);
        var target = (X: cx + 3, Z: cz);
        var pillar = (X: cx + 20, Z: cz);
        var empty = (X: cx + 12, Z: cz + 8);

        static Operations.BuildingIntent Wall(string def, (int X, int Z) at, bool replace) => new()
        {
            Placement = new Placement.PlacementCandidate { DefName = def, Stuff = "WoodLog", X = at.X, Z = at.Z, Rotation = Placement.Rotation.North },
            ReplaceWall = replace ? true : null,
        };

        foreach (var (label, def, at, code) in new[]
        {
            ("replace-no-wall", "Wall", empty, Common.FailureCode.NoWallToReplace),
            ("replace-not-a-wall", "Door", target, Common.FailureCode.WallReplacementRefused),
            ("replace-last-roof-holder", "Wall", pillar, Common.FailureCode.WallReplacementStrandsRoof),
        })
        {
            var refusal = await TypedApply.RefusedAsync(s, label, Wall(def, at, true), cancellationToken).ConfigureAwait(false);
            if (refusal.Code != code)
            {
                throw new AcceptanceException($"{label}: want {code}, got {refusal}");
            }
        }

        _ = await TypedApply.AppliedAsync(s, "replace-wall", Wall("Wall", target, true), cancellationToken).ConfigureAwait(false);
        var placed = await s.FixtureAsync("replace-placed", Cell, ("audit", target.X, target.Z, 0), cancellationToken).ConfigureAwait(false);
        if (placed is not { Impassable: true, BuiltStuff: "Steel" })
        {
            throw new AcceptanceException($"the old wall must stand while the blueprint waits: {placed}");
        }

        // The fixture ticks the game itself, one audited tick at a time; it stops at completion.
        var swapped = await s.FixtureAsync("replace-run", Cell, ("run", target.X, target.Z, Window), cancellationToken).ConfigureAwait(false);
        s.RecordWire("replace_completed", swapped);
        if (swapped.Violations != 0 || swapped is not { BuiltDef: "Wall", BuiltStuff: "WoodLog" })
        {
            throw new AcceptanceException($"want a wood wall with the enclosure kept on every tick within {Window} ticks: {swapped}");
        }

        // A plain wall on empty ground (no built wall under it) still gets the Standable Frame (#2314).
        _ = await TypedApply.AppliedAsync(s, "ring-wall", Wall("Wall", empty, false), cancellationToken).ConfigureAwait(false);
        var ring = await s.FixtureAsync("ring-run", Cell, ("run", empty.X, empty.Z, 0), cancellationToken).ConfigureAwait(false);
        for (var spent = 0; ring.FrameDef.Length == 0; spent += 5)
        {
            if (spent >= Window)
            {
                throw new AcceptanceException($"the new-ring wall never became a frame within {Window} ticks: {ring}");
            }

            ring = await s.FixtureAsync("ring-run", Cell, ("run", empty.X, empty.Z, 5), cancellationToken).ConfigureAwait(false);
        }

        s.RecordWire("new_ring", ring);
        if (ring is not { FrameStandable: true, FrameDef: "Frame_Wall" })
        {
            throw new AcceptanceException($"a new-ring wall must keep the Standable Frame: {ring}");
        }
    }
}
