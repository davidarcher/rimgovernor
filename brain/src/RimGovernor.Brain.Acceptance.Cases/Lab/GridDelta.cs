using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>lab/grid-delta</c> (Go <c>grid_delta.go</c>).</summary>
internal static class LabGridDelta
{
    // The cell grid arrays one wall on open soil never changes: its delta must not carry them.
    private static readonly string[] Unchanged = ["cell", "zone", "roofed", "polluted", "glow", "roof", "zone_id", "terrain", "base_terrain", "snow_depth"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/grid-delta",
        Scope = "Whole-map cell grid delta (#1551): test/grid_delta reads the native grid, spawns one player wall, "
            + "reads it again and encodes the delta as the stream does; the delta carries only sparse arrays that "
            + "include the wall cell, never the arrays a wall cannot change, and is a small fraction of the keyframe. "
            + "A Go snapshot test cannot see the native encoder read the map.",
        Start = new LabStart { Colonists = 1 },
        RequiredOps = ["test/grid_delta"],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var (cx, cz) = CaseSupport.Center(s);
        var cell = string.Create(CultureInfo.InvariantCulture, $"{cx + 6},{cz + 6}");
        var reply = await s.Harness.CallAsync("grid-delta", "test/grid_delta", new JsonObject { ["cell"] = cell }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"grid_delta: {reply.ToJson()}");
        }

        s.Report["grid_delta"] = reply;
        var arrays = reply["arrays"].AsSlice();
        if (arrays.Count == 0)
        {
            throw new AcceptanceException($"grid_delta: the wall changed no array: {reply.ToJson()}");
        }

        foreach (var a in arrays)
        {
            var row = a.AsMap() ?? [];
            var field = row["field"].AsString();
            if (Dyn.Contains(Unchanged, field))
            {
                throw new AcceptanceException($"grid_delta: delta carries {field}, which one wall does not change: {row.ToJson()}");
            }

            if (row["form"].AsString() != "Sparse")
            {
                throw new AcceptanceException($"grid_delta: {field} is not sparse: {row.ToJson()}");
            }

            if (row["wallCell"].AsBool() != true)
            {
                throw new AcceptanceException($"grid_delta: {field} omits the wall cell: {row.ToJson()}");
            }
        }

        double key = reply["keyframeBytes"].AsNumber();
        double delta = reply["deltaBytes"].AsNumber();
        if (delta * 20 > key)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"grid_delta: delta {delta} bytes against keyframe {key}"));
        }
    }
}
