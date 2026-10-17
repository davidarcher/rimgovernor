using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/door-swap</c> (#1245, epic #1249): the door of a staged walled, roofed room, given DECONSTRUCT Designate with
/// replace_with_wall, ends a built Wall of the door's stuff on the same cell while the room keeps its roof throughout,
/// built by the lab crew under normal game rules. Records which path vanilla took (a wall blueprint over the door named
/// at apply, or deconstruct then place). Native: GenConstruct's placement rule, the jobs and the built wall. The crew
/// builds (Construction first, all hours work): without it nothing is swapped.
/// </summary>
internal static class WallDoorSwap
{
    private const int Width = 11;
    private const int Height = 9;

    private static readonly Fx.StageCell Door = new(3, 4);
    private static readonly Fx.StageRect Interior = new(4, 3, 3, 3);

    internal static TypedCase Case() => new()
    {
        Name = "wall/door-swap",
        Scope = "The door of a staged roofed WoodLog room, given DECONSTRUCT Designate.replace_with_wall, ends a built WoodLog Wall on "
            + "the same cell with no door while the room keeps its roof on every read; records the path vanilla took.",
        Ticks = 12000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = WallKit.Site(Width, Height),
            Clear = [WallKit.Clear(Width, Height)],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, Width, Height), Home = true }],
            Rooms = [new Fx.StageRoom { Name = "room", Rect = new Fx.StageRect(3, 2, 5, 5), Stuff = "WoodLog", Doors = [Door], Roof = true }],
            Things = [new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 50, Cell = new(1, 1) }],
            Pawns = WallKit.Builders,
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var door = staged.Rooms["room"].Doors.Single();
        var at = WallKit.At(site, Door);
        var interior = WallKit.Cells(site, Interior);

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var swap = await TypedApply.AppliedAsync(s, "swap", WallKit.Deconstruct(door, replaceWithWall: true), cancellationToken).ConfigureAwait(false);

        // A replacement named at apply is the in-place path: vanilla accepted the wall blueprint over the standing door.
        s.Record("path", string.IsNullOrEmpty(swap.Effect()?.Deconstruct?.ReplacementId) ? "deconstruct_then_place" : "blueprint_over_door");
        var ticks = await s.RunUntilAsync("swapped", s.TicksLeft, async ct =>
        {
            var roofed = WallKit.Roofed(await TypedApply.CellsAsync(s, "swap-probe", site, ct).ConfigureAwait(false), interior);
            if (roofed != interior.Count)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the room lost roof during the swap: {roofed} of {interior.Count} roofed"));
            }

            var built = await s.BuildingsAsync("swap-probe", new Observations.ListBuildingsRequest { DefNames = ["Wall", "Door"], Statuses = [Observations.BuildingStatus.Built] }, ct).ConfigureAwait(false);
            var here = built.Where(b => b.Building.Position is { X: { } x, Z: { } z } && x == at.X && z == at.Z).ToList();
            var wall = here.Any(b => b.Building.DefName == "Wall" && b.Stuff == "WoodLog");
            var doorStands = here.Any(b => b.Building.DefName == "Door");
            return (Wait.Signature(wall, doorStands), wall && !doorStands);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("swapped_after_ticks", ticks);
    }
}
