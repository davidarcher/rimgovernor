using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>A map cell of a wall case (Go <c>wall.cell</c>).</summary>
internal readonly record struct WallCell(int X, int Z);

/// <summary>Room geometry and the roof/floor staging and reads the room cases of the <c>wall/*</c> family share (Go <c>cleared_room.go</c>).</summary>
internal static class WallGeometry
{
    /// <summary>The wall ring of the square room whose interior spans [x0+1,x1-1] x [z0+1,z1-1], and that interior.</summary>
    internal static (List<WallCell> Walls, List<WallCell> Interior) Ring(int x0, int z0, int x1, int z1)
    {
        var walls = new List<WallCell>();
        var interior = new List<WallCell>();
        for (var x = x0; x <= x1; x++)
        {
            for (var z = z0; z <= z1; z++)
            {
                if (x == x0 || x == x1 || z == z0 || z == z1)
                {
                    walls.Add(new WallCell(x, z));
                }
                else
                {
                    interior.Add(new WallCell(x, z));
                }
            }
        }

        return (walls, interior);
    }

    /// <summary>A JSON array of <c>{x, z}</c> cells.</summary>
    internal static JsonArray CellArray(IEnumerable<WallCell> cells)
    {
        var array = new JsonArray();
        foreach (var c in cells)
        {
            array.Add(CaseSupport.Cell(c.X, c.Z));
        }

        return array;
    }

    /// <summary>Stages a constructed roof over the rectangle <paramref name="cells"/> span through test/lab_stage.</summary>
    internal static Task RoofAsync(Harness h, string label, IReadOnlyCollection<WallCell> cells, CancellationToken cancellationToken)
    {
        var (min, max) = Bounds(cells);
        return StageAsync(h, label, new LabFixture { Roof = new LabRoof("RoofConstructed", min.X, min.Z, max.X, max.Z) }.ToJson(), cancellationToken);
    }

    /// <summary>Lays the terrain <paramref name="def"/> over the rectangle <paramref name="cells"/> span through test/lab_stage.</summary>
    internal static Task LayTerrainAsync(Harness h, string label, IReadOnlyCollection<WallCell> cells, string def, CancellationToken cancellationToken)
    {
        var (min, max) = Bounds(cells);
        var spec = new JsonObject { ["terrain"] = new JsonObject { ["def"] = def, ["minX"] = min.X, ["minZ"] = min.Z, ["maxX"] = max.X, ["maxZ"] = max.Z } };
        return StageAsync(h, label, spec, cancellationToken);
    }

    /// <summary>How many of <paramref name="cells"/> the cells read shows roofed.</summary>
    internal static async Task<int> RoofedAsync(ISession s, string label, IReadOnlyCollection<WallCell> cells, CancellationToken cancellationToken) =>
        Count(await ReadAsync(s, label, cells, cancellationToken).ConfigureAwait(false), cells, c => c.Roofed);

    /// <summary>How many of <paramref name="cells"/> the cells read shows carrying a removable constructed floor.</summary>
    internal static async Task<int> FlooredAsync(ISession s, string label, IReadOnlyCollection<WallCell> cells, CancellationToken cancellationToken) =>
        Count(await ReadAsync(s, label, cells, cancellationToken).ConfigureAwait(false), cells, c => c.TopLayerRemovable);

    private static Task<MapCellGrid> ReadAsync(ISession s, string label, IReadOnlyCollection<WallCell> cells, CancellationToken cancellationToken)
    {
        var (min, max) = Bounds(cells);
        return s.Harness.RectCellsAsync(label, s.Identity, min, max, cancellationToken);
    }

    private static int Count(MapCellGrid grid, IEnumerable<WallCell> cells, Func<GridCell, bool> match) =>
        cells.Count(c => grid.At(new Cell(c.X, c.Z)) is { } cell && match(cell));

    private static (Cell Min, Cell Max) Bounds(IReadOnlyCollection<WallCell> cells) =>
        cells.Count == 0
            ? throw new AcceptanceException("no cells")
            : (new Cell(cells.Min(c => c.X), cells.Min(c => c.Z)), new Cell(cells.Max(c => c.X), cells.Max(c => c.Z)));

    private static async Task StageAsync(Harness h, string label, JsonObject spec, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, CombatFixtures.StageTool, new JsonObject { ["spec"] = spec.ToJsonString() }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{label}: {reply.ToJson()}");
        }
    }

    /// <summary>Sends one operations_apply action under <paramref name="key"/> and returns its single ActionResult (applied or refused).</summary>
    internal static async Task<JsonObject> ApplyActionAsync(ISession s, string key, JsonObject action, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.WireAsync(
            key,
            "operations_apply",
            new JsonObject { ["identity"] = s.Identity.DeepClone(), ["actions"] = new JsonArray(action) },
            cancellationToken).ConfigureAwait(false);
        var results = reply["results"].AsSlice();
        return results.Count == 1 ? results[0].AsMap() ?? [] : throw new AcceptanceException($"{key}: expected one result: {reply.ToJson()}");
    }

    /// <summary>The deconstruct target's <c>present</c> flag (test/deconstruct_target inspect).</summary>
    internal static async Task<bool> PresentAsync(Harness h, string label, JsonNode? id, CancellationToken cancellationToken)
    {
        var row = await h.CallAsync(label, "test/deconstruct_target", new JsonObject { ["target"] = id?.DeepClone(), ["action"] = "inspect" }, cancellationToken).ConfigureAwait(false);
        return row["present"].AsBool() == true;
    }

    /// <summary>The ring cell's wall or door spawn: a Door on <paramref name="door"/>, a Wall elsewhere, both of WoodLog; returns the thing id.</summary>
    internal static async Task<string> SpawnRingCellAsync(Harness h, WallCell c, WallCell door, CancellationToken cancellationToken)
    {
        var (id, _) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = c == door ? "Door" : "Wall", Stuff = "WoodLog", X = c.X, Z = c.Z }, cancellationToken).ConfigureAwait(false);
        return id;
    }
}
