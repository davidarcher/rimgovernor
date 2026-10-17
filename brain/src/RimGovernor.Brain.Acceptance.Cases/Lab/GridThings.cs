using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using RimGovernor.Brain.Policy;
using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>lab/grid-things</c> (Go <c>grid_things.go</c>).</summary>
internal static class LabGridThings
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/grid-things",
        Scope = "Native thing read (#2261): test/grid_things spawns an item, wall, wall blueprint, wall frame, plant, "
            + "filth and animal corpse on seven cells and a shelf holding two stacks on the next two, and replies that "
            + "row as a serialized grid; the harness must read each category, flag, faction and state as the "
            + "native side meant it, the tile columns must be known, and the shelf must hold 2 of 3 stacks (#3233). Reports the whole-map read cost. A Go snapshot test cannot see "
            + "which flags and states the native encoder writes for real game things.",
        Start = new LabStart { Colonists = 1 },
        RequiredOps = ["test/grid_things"],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var (cx, cz) = CaseSupport.Center(s);
        var cell = string.Create(CultureInfo.InvariantCulture, $"{cx + 2},{cz + 9}");
        var reply = await s.Harness.CallAsync("grid-things", "test/grid_things", new JsonObject { ["cell"] = cell }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"grid_things: {reply.ToJson()}");
        }


        s.Report["grid_things"] = new JsonObject { ["readMs"] = reply["readMs"]?.DeepClone() };
        MapCellGrid grid;
        try
        {
            grid = MapCellGrid.Of(WireJson.Deserialize<Mirror.CellGrid>(Convert.FromBase64String(reply["grid"].AsString())));
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
            || frame.Id != (ulong)reply["frameId"].AsNumber())
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