using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
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
            + "row as a native-encoded keyframe; the Go decoder must read each category, flag, faction and state as the "
            + "native side meant it, the tile columns must be known, and the shelf must hold 2 of 3 stacks (#3233). Reports the whole-map read and encode cost. A Go snapshot test cannot see "
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

        s.Report["grid_things"] = new JsonObject
        {
            ["readMs"] = reply["readMs"]?.DeepClone(),
            ["encodeMs"] = reply["encodeMs"]?.DeepClone(),
            ["keyframeBytes"] = reply["keyframeBytes"]?.DeepClone(),
        };
        Mirror.CellGrid wire;
        try
        {
            wire = WireJson.Deserialize<Mirror.CellGrid>(Convert.FromBase64String(reply["grid"].AsString()));
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            throw new AcceptanceException($"grid_things: the native keyframe does not decode: {e.Message}", e);
        }

        if (wire.Things is null)
        {
            throw new AcceptanceException("grid_things: the native keyframe is not complete");
        }

        var grid = MapCellGrid.Apply(wire);
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
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"grid_things: cell {i} holds {cells[i].StorageStacks?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} of {cells[i].StorageCapacity?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} stacks, want {stacks} of {capacity}"));
            }
        }

        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (string.IsNullOrEmpty(c.Terrain))
            {
                throw new AcceptanceException($"grid_things: cell {i} terrain unknown");
            }

            if (c.InHome is null)
            {
                throw new AcceptanceException($"grid_things: cell {i} in_home unknown");
            }

            if (string.IsNullOrEmpty(c.BaseTerrain))
            {
                throw new AcceptanceException($"grid_things: cell {i} base terrain unknown");
            }

            if (c.SnowDepth is null)
            {
                throw new AcceptanceException($"grid_things: cell {i} snow depth unknown");
            }

            if (c.TopLayerRemovable is null)
            {
                throw new AcceptanceException($"grid_things: cell {i} top layer removable unknown");
            }

            if (c.FoundationRemoval is null)
            {
                throw new AcceptanceException($"grid_things: cell {i} foundation removal unknown");
            }
        }

        var things = ThingsByCell(wire, cells.Count);
        Mirror.Thing Find(int i, Mirror.ThingCategory category)
        {
            foreach (var t in things[i])
            {
                if (t.Category == category)
                {
                    return t;
                }
            }

            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"grid_things: cell {i} lists no thing of category {category}: {WireJson.Serialize(things[i])}"));
        }

        string Def(Mirror.Thing t) => t.Def == 0 || t.Def > wire.Strings.Count ? "" : wire.Strings[(int)t.Def - 1];

        var steel = Find(0, Mirror.ThingCategory.Item);
        if (Def(steel) != "Steel" || steel.Count != 25 || !Has(steel, Mirror.ThingFlagBit.Haulable) || Has(steel, Mirror.ThingFlagBit.Edifice))
        {
            throw Fail("steel", steel);
        }

        var wall = Find(1, Mirror.ThingCategory.Building);
        if (Def(wall) != "Wall" || wall.Faction != Mirror.ThingFaction.Player
            || !HasAll(wall, Mirror.ThingFlagBit.Edifice, Mirror.ThingFlagBit.Impassable, Mirror.ThingFlagBit.HoldsRoof, Mirror.ThingFlagBit.Deconstructible)
            || Has(wall, Mirror.ThingFlagBit.Blueprint) || Has(wall, Mirror.ThingFlagBit.Frame)
            || wall.State?.Building is not { } wallState || wallState.HitPoints == 0 || wallState.Burning)
        {
            throw Fail("wall", wall);
        }

        var blueprint = Find(2, Mirror.ThingCategory.Building);
        if (Def(blueprint) != "Wall" || !Has(blueprint, Mirror.ThingFlagBit.Blueprint) || Has(blueprint, Mirror.ThingFlagBit.Frame)
            || blueprint.Faction != Mirror.ThingFaction.Player || blueprint.State?.Building is not { } blueprintState || blueprintState.Needed.Count == 0)
        {
            throw Fail("blueprint", blueprint);
        }

        var frame = Find(3, Mirror.ThingCategory.Building);
        if (Def(frame) != "Wall" || !Has(frame, Mirror.ThingFlagBit.Frame) || Has(frame, Mirror.ThingFlagBit.Blueprint)
            || frame.State?.Building is not { } frameState || frameState.Needed.Count == 0
            || frame.Id != (ulong)reply["frameId"].AsNumber())
        {
            throw Fail("frame", frame);
        }

        var plant = Find(4, Mirror.ThingCategory.Plant);
        if (Def(plant) != "Plant_Potato" || plant.State?.Plant is not { } plantState || plantState.Growth < 0.45 || plantState.Growth > 0.55)
        {
            throw Fail("plant", plant);
        }

        var filth = Find(5, Mirror.ThingCategory.Filth);
        if (!Def(filth).StartsWith("Filth_", StringComparison.Ordinal) || (filth.State?.Filth?.Thickness ?? 0) == 0)
        {
            throw Fail("filth", filth);
        }

        var corpse = Find(6, Mirror.ThingCategory.Corpse);
        if (corpse.State?.Corpse is not { } corpseState || corpseState.Class != Mirror.CorpseClass.Animal || corpseState.Rot > 0.1)
        {
            throw Fail("corpse", corpse);
        }
    }

    private static AcceptanceException Fail(string name, Mirror.Thing thing) => new($"grid_things: {name} = {WireJson.Serialize(thing)}");

    private static bool Has(Mirror.Thing thing, Mirror.ThingFlagBit flag) => (thing.Flags & (uint)flag) == (uint)flag;

    private static bool HasAll(Mirror.Thing thing, params Mirror.ThingFlagBit[] flags) => flags.All(flag => Has(thing, flag));

    /// <summary>The wire thing list as one list per grid cell (Go <c>applyThings</c> over no base).</summary>
    private static List<Mirror.Thing>[] ThingsByCell(Mirror.CellGrid wire, int n)
    {
        var result = new List<Mirror.Thing>[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = [];
        }

        var list = wire.Things!;
        if (list.Offsets.Count != list.Cells.Count + 1)
        {
            throw new AcceptanceException("grid_things: the native keyframe has malformed thing offsets");
        }

        for (var p = 0; p < list.Cells.Count; p++)
        {
            var index = (int)list.Cells[p];
            if (index >= n)
            {
                throw new AcceptanceException("grid_things: the native keyframe lists a thing past the row");
            }

            for (var t = (int)list.Offsets[p]; t < (int)list.Offsets[p + 1]; t++)
            {
                result[index].Add(list.Things[t]);
            }
        }

        return result;
    }
}
