using System.Globalization;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/grid-things</c>: a 9x1 site is staged clear, and test/grid_things (a recorder: filth and an animal corpse are
/// beyond the stage primitives) spawns one thing of each category along it and replies the native encoding of that row.
/// </summary>
internal static class LabGridThings
{
    private const string Tool = "test/grid_things";

    private static readonly FixtureOp<string, Fx.GridThingsRead> Grid = new(Tool, cell => ServedOps.Args(("cell", cell)), ServedOps.Reply<Fx.GridThingsRead>(Tool));

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 9,
            Height = 1,
            OffsetX = 2,
            OffsetZ = 9,
            Radius = 20,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged],
        },
    };

    internal static TypedCase Case() => new()
    {
        Name = "lab/grid-things",
        Scope = "Native thing read (#2261): an item, wall, wall blueprint, wall frame, plant, filth and animal corpse on seven "
            + "cells and a shelf holding two stacks on the next two, read as the native cell grid; each category, flag, faction "
            + "and state decodes as the native side meant it, the tile columns are known, and the shelf holds 2 of 3 stacks "
            + "(#3233). A unit test cannot see which flags and states the native encoder writes for real game things.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        Ops = [Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged?.Site ?? throw new AcceptanceException("test/stage replied no site");
        var reply = await s.FixtureAsync("grid-things", Grid, string.Create(CultureInfo.InvariantCulture, $"{site.X},{site.Z}"), cancellationToken).ConfigureAwait(false);
        s.Record("read_ms", reply.ReadMs);
        MapCellGrid grid;
        try
        {
            grid = MapCellGrid.Of(OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Mirror.CellGrid>(Convert.FromBase64String(reply.Grid)));
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            throw new AcceptanceException($"grid_things: the native grid does not decode: {e.Message}", e);
        }

        var cells = grid.Cells();
        if (cells.Count != 9)
        {
            throw new AcceptanceException($"grid_things: {cells.Count} held cells, want 9");
        }

        // Per-cell storage stacks (#3233): the steel cell holds 1 of 1, the wall cell holds none of 0, and the
        // shelf holds 2 of 3 on its first cell and 0 of 3 on its second.
        foreach (var (i, stacks, capacity) in new[] { (0, 1, 1), (1, 0, 0), (7, 2, 3), (8, 0, 3) })
        {
            if (cells[i].StorageStacks != stacks || cells[i].StorageCapacity != capacity)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"grid_things: cell {i} holds {cells[i].StorageStacks} of {cells[i].StorageCapacity} stacks, want {stacks} of {capacity}"));
            }
        }

        for (var i = 0; i < cells.Count; i++)
        {
            if (string.IsNullOrEmpty(cells[i].Terrain) || string.IsNullOrEmpty(cells[i].BaseTerrain))
            {
                throw new AcceptanceException($"grid_things: cell {i} terrain or base terrain unknown");
            }
        }

        var things = grid.Things();
        Thing Find(int i, ThingCategory category)
        {
            foreach (var t in things[i])
            {
                if (t.Category == category)
                {
                    return t;
                }
            }

            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"grid_things: cell {i} lists no thing of category {category}: {Describe(things[i])}"));
        }

        var steel = Find(0, ThingCategory.Item);
        if (steel.Def != "Steel" || steel.Count != 25 || !steel.Has(ThingMarks.Haulable) || steel.Has(ThingMarks.Edifice))
        {
            throw Fail("steel", steel);
        }

        var wall = Find(1, ThingCategory.Building);
        if (wall.Def != "Wall" || wall.Faction != ThingFaction.Player
            || !wall.Has(ThingMarks.Edifice | ThingMarks.Impassable | ThingMarks.HoldsRoof | ThingMarks.Deconstructible)
            || wall.Has(ThingMarks.Blueprint) || wall.Has(ThingMarks.Frame)
            || wall.Building is not { } wallState || wallState.HitPoints == 0 || wallState.Burning)
        {
            throw Fail("wall", wall);
        }

        var blueprint = Find(2, ThingCategory.Building);
        if (blueprint.Def != "Wall" || !blueprint.Has(ThingMarks.Blueprint) || blueprint.Has(ThingMarks.Frame)
            || blueprint.Faction != ThingFaction.Player || blueprint.Building is not { } blueprintState || blueprintState.Needed.Count == 0)
        {
            throw Fail("blueprint", blueprint);
        }

        var frame = Find(3, ThingCategory.Building);
        if (frame.Def != "Wall" || !frame.Has(ThingMarks.Frame) || frame.Has(ThingMarks.Blueprint)
            || frame.Building is not { } frameState || frameState.Needed.Count == 0
            || frame.Id != (ulong)reply.FrameId)
        {
            throw Fail("frame", frame);
        }

        var plant = Find(4, ThingCategory.Plant);
        if (plant.Def != "Plant_Potato" || plant.Plant.Growth < 0.45 || plant.Plant.Growth > 0.55)
        {
            throw Fail("plant", plant);
        }

        var filth = Find(5, ThingCategory.Filth);
        if (!filth.Def.StartsWith("Filth_", StringComparison.Ordinal) || filth.FilthThickness == 0)
        {
            throw Fail("filth", filth);
        }

        var corpse = Find(6, ThingCategory.Corpse);
        if (corpse.Corpse.Class != CorpseClass.Animal || corpse.Corpse.Rot > 0.1)
        {
            throw Fail("corpse", corpse);
        }
    }

    private static AcceptanceException Fail(string name, Thing thing) => new($"grid_things: {name} = {Describe([thing])}");

    private static string Describe(IReadOnlyList<Thing> things) => string.Join("; ", things.Select(t => t.ToString()));
}