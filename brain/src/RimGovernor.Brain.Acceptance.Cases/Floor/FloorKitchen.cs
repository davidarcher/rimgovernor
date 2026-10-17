using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>floor/kitchen</c>: the upkeep flooring census the rooms/flooring Need reads. A staged roofed home kitchen (a
/// stove) on bare soil reads as a Kitchen row whose every interior cell stands on Soil with nothing pending; a floor
/// blueprint placed on one cell then reads as that cell's pending floor. The room role, the terrain under each cell and
/// the open blueprint are the game's; the planner's floor choice is covered by the flooring policy tests.
/// </summary>
internal static class FloorKitchen
{
    private const string Floor = "WoodPlankFloor";

    internal static TypedCase Case() => new()
    {
        Name = "floor/kitchen",
        Scope = "The upkeep flooring census over a staged roofed home kitchen with a stove on Soil: one Kitchen row lists every "
            + "interior cell on Soil with nothing pending, and after a WoodPlankFloor blueprint is placed on one cell that "
            + "cell, and only it, reads the floor as pending.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 9,
                Height = 9,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Unzoned],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 9, 9), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Roofs, Fx.StageClearKind.Zones] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 9, 9), Terrain = "Soil", Home = true }],
            Rooms = [new Fx.StageRoom { Name = "kitchen", Rect = new Fx.StageRect(1, 1, 7, 7), Stuff = "WoodLog", Doors = [new(4, 1)], Roof = true }],
            Buildings = [new Fx.StageBuilding { Name = "stove", Def = "FueledStove", Cells = [new(4, 5)], Fuel = 50 }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var interior = s.Staged!.Rooms["kitchen"].Interior.ToHashSet();
        var target = new Fx.StageCell(interior.Min(c => c.X), interior.Min(c => c.Z));

        async Task<List<Observations.FloorCell>> ReadAsync(string label)
        {
            var upkeep = await TypedApply.UpkeepAsync(s, label, cancellationToken).ConfigureAwait(false);
            var flooring = upkeep.Flooring?.Outcome ?? throw new AcceptanceException($"{label}: flooring section unavailable: {upkeep.Flooring?.Outcome}");
            var room = flooring.Rooms.FirstOrDefault(r => r.Cells.Any(c => interior.Contains(Of(c))))
                ?? throw new AcceptanceException($"{label}: no flooring row holds the staged kitchen");
            s.RecordWire(label, room);
            var cells = room.Cells.Where(c => interior.Contains(Of(c))).ToList();
            return room.Role == "Kitchen" && cells.Count == interior.Count
                ? cells
                : throw new AcceptanceException($"{label}: want a Kitchen row over the {interior.Count} interior cells, got {room.Role} over {cells.Count}");
        }

        var before = await ReadAsync("flooring-before").ConfigureAwait(false);
        if (before.FirstOrDefault(c => c.Terrain != "Soil" || !string.IsNullOrEmpty(c.Pending)) is { } bare)
        {
            throw new AcceptanceException($"flooring-before: want every cell on Soil with nothing pending, got {bare}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "floor-blueprint", new Operations.BuildingIntent
        {
            Placement = new Placement.PlacementCandidate { DefName = Floor, X = target.X, Z = target.Z, Rotation = Placement.Rotation.North },
        }, cancellationToken).ConfigureAwait(false);

        var after = await ReadAsync("flooring-after").ConfigureAwait(false);
        var pending = after.Where(c => !string.IsNullOrEmpty(c.Pending)).ToList();
        if (pending is not [{ Pending: Floor } cell] || Of(cell) != target)
        {
            throw new AcceptanceException($"flooring-after: want only {target} pending {Floor}, got {string.Join("; ", pending)}");
        }
    }

    private static Fx.StageCell Of(Observations.FloorCell cell) => cell.Cell is { } c ? new(c.X, c.Z) : new(-1, -1);
}
