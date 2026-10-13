using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>A map cell of a wall case (Go <c>wall.cell</c>).</summary>
internal readonly record struct WallCell(int X, int Z);

/// <summary>Room geometry and the roof/floor fixture calls the room cases of the <c>wall/*</c> family share (Go <c>cleared_room.go</c>).</summary>
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

    /// <summary>The fixture's <c>x,z;x,z</c> cell list text.</summary>
    internal static string CellList(IEnumerable<WallCell> cells) =>
        string.Join(';', cells.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.X},{c.Z}")));

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

    /// <summary>Roofs (<paramref name="set"/>) or reads (not set) the roofed count of <paramref name="cells"/> through test/roof_cells.</summary>
    internal static async Task<int> RoofedAsync(Harness h, string label, IEnumerable<WallCell> cells, bool set, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, "test/roof_cells", new JsonObject { ["cells"] = CellList(cells), ["set"] = set }, cancellationToken).ConfigureAwait(false);
        return reply["success"].AsBool() == true ? (int)reply["roofed"].AsNumber() : throw new AcceptanceException($"{label}: {reply.ToJson()}");
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
