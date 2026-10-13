using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>combat_geometry</c> read over JSON (Go <c>bridge/combat_geometry.go</c> and the frame helpers the family
/// uses): requests, the reply validation and the combat event row keys. The Acceptance harness has no typed bridge, so
/// the family builds and checks the ProtoJSON by hand.
/// </summary>
internal static class Geometry
{
    /// <summary>The caps: the native's NativeCombatGeometryTools.MaxCells and MaxHostiles.</summary>
    internal const int MaxCells = 64;

    internal const int MaxHostiles = 16;

    /// <summary>Bounds a firing_cells proposal's search square around its from cell.</summary>
    internal const int MaxRadius = 12;

    /// <summary>Hostile arrived rows from a drop-pod arrival mode carry this raid strategy.</summary>
    internal const string PodsStrategy = "pods";

    internal const string Method = "combat_geometry";

    /// <summary>A request for identity's cells and hostiles.</summary>
    internal static JsonObject Ask(JsonObject identity, IEnumerable<JsonObject> cells, IEnumerable<string> hostiles, string pawnId)
    {
        var request = new JsonObject { ["identity"] = identity.DeepClone() };
        var cellArray = new JsonArray([.. cells.Select(c => (JsonNode?)c.DeepClone())]);
        if (cellArray.Count > 0)
        {
            request["cells"] = cellArray;
        }

        var hostileArray = new JsonArray([.. hostiles.Select(h => (JsonNode?)JsonValue.Create(h))]);
        if (hostileArray.Count > 0)
        {
            request["hostileIds"] = hostileArray;
        }

        if (pawnId.Length > 0)
        {
            request["pawnId"] = pawnId;
        }

        return request;
    }

    /// <summary>A request whose cells are all proposed by the native for <paramref name="propose"/>'s role, scored against hostiles.</summary>
    internal static JsonObject ProposeAsk(JsonObject identity, JsonObject propose, IEnumerable<string> hostiles, string pawnId)
    {
        var request = Ask(identity, [], hostiles, pawnId);
        request["propose"] = propose.DeepClone();
        return request;
    }

    /// <summary>A <c>{role: ...}</c> propose block with the role flattened as ProtoJSON writes a oneof.</summary>
    internal static JsonObject Role(string name, JsonObject body) => new() { [name] = body };

    /// <summary>Calls combat_geometry and returns the whole reply.</summary>
    internal static Task<JsonObject> CallAsync(Harness h, string label, JsonObject request, CancellationToken cancellationToken) =>
        h.WireAsync(label, Method, request, cancellationToken);

    /// <summary>The reply's observed geometry; a missing one is the failure the case reports with the reply.</summary>
    internal static JsonObject? Observed(JsonObject reply) => reply["observed"].AsMap();

    /// <summary>
    /// Checks a reply against its request: the request's world, every cell in order, every hostile's line in order,
    /// cover in 0..1, and proposals only for a propose block: standable, distinct from each other and the named cells,
    /// within the cells cap.
    /// </summary>
    internal static void Validate(JsonObject? g, JsonObject request)
    {
        if (g is null)
        {
            throw Contract("combat geometry missing");
        }

        var context = g["context"].AsMap() ?? throw Contract("context missing");
        if (!Dyn.DeepEqual(context["identity"], request["identity"]))
        {
            throw Contract("combat geometry world mismatch");
        }

        var requested = request["cells"].AsSlice();
        var cells = g["cells"].AsSlice();
        if (cells.Count != requested.Count)
        {
            throw Contract("combat geometry cell count");
        }

        var seen = new HashSet<(long, long)>();
        for (var i = 0; i < cells.Count; i++)
        {
            var row = cells[i].AsMap() ?? [];
            if (!SameCell(row["cell"].AsMap(), requested[i].AsMap()))
            {
                throw Contract($"combat geometry cell {CaseSupport.Tag(i)} off its request");
            }

            _ = seen.Add(Key(row["cell"].AsMap()));
            Lines(row, request, i);
        }

        var propose = request["propose"].AsMap();
        var proposed = g["proposed"].AsSlice();
        if (propose is null)
        {
            if (proposed.Count != 0)
            {
                throw Contract("combat geometry proposals without propose");
            }

            return;
        }

        if (proposed.Count > MaxCells - requested.Count)
        {
            throw Contract("combat geometry proposals over the cells cap");
        }

        var route = propose["rescuePath"].AsMap() is not null;
        for (var i = 0; i < proposed.Count; i++)
        {
            var row = proposed[i].AsMap() ?? [];
            var cell = row["cell"].AsMap();
            var key = Key(cell);
            if (!ValidCell(cell) || (!route && (row["standable"].AsBool() != true || seen.Contains(key))))
            {
                throw Contract($"combat geometry proposal {CaseSupport.Tag(i)} invalid or repeated");
            }

            if (!route)
            {
                _ = seen.Add(key);
            }

            Lines(row, request, requested.Count + i);
        }
    }

    /// <summary>The combat event row's key: its own watermark, <c>"&lt;tick&gt;.&lt;seq&gt;"</c>.</summary>
    internal static string EventId(JsonObject row)
    {
        var at = row["at"].AsMap();
        return string.Create(CultureInfo.InvariantCulture, $"{WatermarkTick(at)}.{WatermarkSeq(at)}");
    }

    /// <summary>A watermark's tick, 0 when the proto3 default omitted it.</summary>
    internal static long WatermarkTick(JsonObject? at) => at is null ? 0 : (long)at["tick"].AsNumberOrZero();

    /// <summary>A watermark's sequence, 0 when the proto3 default omitted it.</summary>
    internal static long WatermarkSeq(JsonObject? at) => at is null ? 0 : (long)at["seq"].AsNumberOrZero();

    /// <summary>Whether the row is a hostile_arrived row from a drop-pod arrival.</summary>
    internal static bool DropPodArrival(JsonObject row) =>
        row["kind"].AsString() == "COMBAT_LOG_KIND_HOSTILE_ARRIVED" && row["raidStrategy"].AsString() == PodsStrategy;

    /// <summary>A number a proto3 default may have omitted: 0 when absent.</summary>
    internal static double AsNumberOrZero(this JsonNode? value) => value is null ? 0 : value.AsNumber();

    private static bool ValidCell(JsonObject? cell) =>
        cell is not null && cell["x"] is not null && cell["z"] is not null && cell["x"].AsNumber() >= 0 && cell["z"].AsNumber() >= 0;

    private static (long, long) Key(JsonObject? cell) =>
        cell is null ? (0, 0) : ((long)cell["x"].AsNumberOrZero(), (long)cell["z"].AsNumberOrZero());

    private static bool SameCell(JsonObject? a, JsonObject? b) => Key(a) == Key(b);

    private static void Lines(JsonObject row, JsonObject request, int i)
    {
        var hostiles = request["hostileIds"].AsSlice();
        var lines = row["lines"].AsSlice();
        if (lines.Count != hostiles.Count)
        {
            throw Contract($"combat geometry cell {CaseSupport.Tag(i)} lines off its request");
        }

        if (row["pathTicks"] is not null && (request["pawnId"] is null || row["pathTicks"].AsNumber() < 0))
        {
            throw Contract("combat geometry path ticks without a pawn");
        }

        for (var j = 0; j < lines.Count; j++)
        {
            var line = lines[j].AsMap() ?? [];
            var cover = line["cover"].AsNumber();
            if (line["hostileId"].AsString() != hostiles[j].AsString() || line["cover"] is null || double.IsNaN(cover) || cover < 0 || cover > 1)
            {
                throw Contract($"combat geometry line {CaseSupport.Tag(i)}.{CaseSupport.Tag(j)} invalid");
            }
        }
    }

    private static AcceptanceException Contract(string message) => new("contract: " + message);
}
