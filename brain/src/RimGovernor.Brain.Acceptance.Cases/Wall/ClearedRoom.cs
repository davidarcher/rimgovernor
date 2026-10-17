using System.Globalization;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/cleared-room</c> (#1366, #1245, epic #1249): clearance of a staged walled, roofed player room with a stool on
/// a laid WoodPlankFloor ends with no building, no roof and no constructed floor, in the clearance order: the stool, then
/// the walls and door (designated first, waiting on the roof), then remove_roof, then remove_floor once the cells are
/// clear. No wall comes down while the room is roofed, and a designated wall of a second roofed room is held by the
/// roof-support wait with that room's roof kept. Native: vanilla room geometry, the NoRoof and RemoveFloor jobs and the
/// native job guard. The lab crew builds (Construction first, all hours work): without it nothing is cleared.
/// </summary>
internal static class WallClearedRoom
{
    private const int Width = 17;
    private const int Height = 9;
    private const string Floor = "WoodPlankFloor";

    private static readonly Fx.StageRect Cleared = new(9, 2, 5, 5);
    private static readonly Fx.StageRect Held = new(1, 2, 5, 5);
    private static readonly Fx.StageRect ClearedInterior = new(10, 3, 3, 3);
    private static readonly Fx.StageRect HeldInterior = new(2, 3, 3, 3);

    internal static TypedCase Case() => new()
    {
        Name = "wall/cleared-room",
        Scope = "A staged roofed WoodLog room with a stool on a WoodPlankFloor is cleared in order (stool, walls and door designated "
            + "and waiting on the roof, remove_roof, then remove_floor once clear): it ends with no building, roof or floor, no wall "
            + "came down while it was roofed, and a designated wall of a second roofed room stands, held, with that room's roof kept.",
        Ticks = 20000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = WallKit.Site(Width, Height),
            Clear = [WallKit.Clear(Width, Height)],
            Cells =
            [
                new Fx.StageCells { Rect = new Fx.StageRect(0, 0, Width, Height), Home = true },
                new Fx.StageCells { Rect = ClearedInterior, Terrain = Floor },
            ],
            Rooms =
            [
                new Fx.StageRoom { Name = "cleared", Rect = Cleared, Stuff = "WoodLog", Doors = [new(9, 4)], Roof = true },
                new Fx.StageRoom { Name = "held", Rect = Held, Stuff = "WoodLog", Doors = [new(5, 4)], Roof = true },
            ],
            Buildings = [new Fx.StageBuilding { Name = "stool", Def = "Stool", Stuff = "WoodLog", Cells = [new(11, 4)] }],
            Pawns = WallKit.Builders,
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var room = staged.Rooms["cleared"];
        var held = staged.Rooms["held"];
        var stool = staged.Buildings["stool"].Ids.Single();
        List<string> enclosure = [.. room.Walls, .. room.Doors];
        var footprint = WallKit.Cells(site, Cleared);
        var interior = WallKit.Cells(site, ClearedInterior);
        var heldInterior = WallKit.Cells(site, HeldInterior);

        var start = await TypedApply.CellsAsync(s, "start", site, cancellationToken).ConfigureAwait(false);
        if (WallKit.Roofed(start, interior) != interior.Count || WallKit.WithTerrain(start, interior, Floor) != interior.Count || WallKit.Roofed(start, heldInterior) != heldInterior.Count)
        {
            throw new AcceptanceException("start: want both rooms roofed and the cleared room floored");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "stool", WallKit.Deconstruct(stool), cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < enclosure.Count; i++)
        {
            var key = "wall-" + i.ToString(CultureInfo.InvariantCulture);
            _ = await TypedApply.AppliedAsync(s, key, WallKit.Deconstruct(enclosure[i]), cancellationToken).ConfigureAwait(false);
        }

        var heldWall = held.Walls[held.Walls.Count / 2];
        _ = await TypedApply.AppliedAsync(s, "held-wall", WallKit.Deconstruct(heldWall), cancellationToken).ConfigureAwait(false);

        // Vanilla roofs the walls too (auto-roof), so clearance unroofs the room's whole footprint.
        var unroof = await TypedApply.AppliedAsync(s, "remove-roof", new Operations.RemoveRoofIntent { Cells = [.. footprint.Select(WallKit.Wire)] }, cancellationToken).ConfigureAwait(false);
        if (unroof.Effect()?.RemoveRoof is not { Designated: > 0 })
        {
            throw new AcceptanceException($"remove-roof: want fresh NoRoof cells designated over the roofed footprint, got {unroof.Effect()}");
        }
        List<string> buildings = [stool, .. enclosure];
        var cleared = await s.RunUntilAsync("buildings-cleared", 14000, async ct =>
        {
            var grid = await TypedApply.CellsAsync(s, "cleared-probe", site, ct).ConfigureAwait(false);
            var roofed = WallKit.Roofed(grid, footprint);
            var standing = await WallKit.PresentAsync(s, "cleared-probe", buildings, ct).ConfigureAwait(false);
            if (roofed > 0 && enclosure.Any(id => !standing.Contains(id)))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"a wall came down while {roofed} room cells were still roofed"));
            }

            return (Wait.Signature(roofed, standing.Count), roofed == 0 && standing.Count == 0);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("buildings_cleared_after_ticks", cleared);

        for (var i = 0; i < interior.Count; i++)
        {
            _ = await TypedApply.AppliedAsync(s, "floor-" + i.ToString(CultureInfo.InvariantCulture), new Operations.RemoveFloorIntent { Cell = WallKit.Wire(interior[i]), DefName = Floor }, cancellationToken).ConfigureAwait(false);
        }

        var floors = await s.RunUntilAsync("floors-cleared", s.TicksLeft, async ct =>
        {
            var left = WallKit.WithTerrain(await TypedApply.CellsAsync(s, "floors-probe", site, ct).ConfigureAwait(false), interior, Floor);
            return (Wait.Signature(left), left == 0);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("floors_cleared_after_ticks", floors);

        var end = await TypedApply.CellsAsync(s, "held-kept", site, cancellationToken).ConfigureAwait(false);
        var kept = WallKit.Roofed(end, heldInterior);
        var heldStands = (await WallKit.PresentAsync(s, "held-kept", [heldWall], cancellationToken).ConfigureAwait(false)).Contains(heldWall);
        if (kept != heldInterior.Count || !heldStands)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"held room: {kept} of {heldInterior.Count} cells roofed, designated wall standing={heldStands}; want the roof kept and the wall held"));
        }
    }
}
