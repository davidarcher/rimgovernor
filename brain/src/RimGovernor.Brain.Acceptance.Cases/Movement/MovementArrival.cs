using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>movement/arrival</c> (Go <c>movement/movement.go</c>): a move intent through Actions/Apply orders a real native
/// Goto for an owned drafted pawn, and the same order resent under a new key applies again as a no-op.
/// </summary>
internal static class MovementArrival
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "movement/arrival",
        Scope = "A move intent (Actions/Apply) orders a real native Goto for an owned drafted pawn; the same order "
            + "resent under a new key applies as a no-op. A snapshot test cannot see native reachability or the job it issues.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;

        async Task<JsonObject> Read(string label, string pawnId)
        {
            var filter = new JsonObject { ["colonist"] = true, ["downed"] = false };
            if (pawnId.Length > 0)
            {
                filter["ids"] = new JsonArray(pawnId);
            }

            var reply = await h.WireAsync(
                label,
                "observations_list_pawns",
                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["filter"] = filter },
                cancellationToken).ConfigureAwait(false);
            return Draft.PawnRow(reply, identity, pawnId);
        }

        async Task<JsonObject> Apply(string label, string key, string pawnId, JsonObject cell)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["move"] = new JsonObject { ["pawnId"] = pawnId, ["destination"] = cell },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            return results.Count != 1
                ? throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}")
                : results[0].AsMap() ?? [];
        }

        var before = await Read("initial-pawn", string.Empty).ConfigureAwait(false);
        var pawnId = before["pawn"].AsMap()?["id"].AsString() ?? string.Empty;
        var undrafted = await Apply("undrafted", "move-undrafted", pawnId, CaseSupport.Cell(0, 0)).ConfigureAwait(false);
        if (!undrafted.ContainsKey("refused"))
        {
            throw new AcceptanceException($"undrafted: a move for an undrafted pawn was not refused: {undrafted.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "grant", identity, cancellationToken).ConfigureAwait(false);
        _ = await ApplyIntents.ApplyDraftAsync(h, "draft", identity, "move-draft", pawnId, true, cancellationToken).ConfigureAwait(false);
        var owned = await Read("owned", pawnId).ConfigureAwait(false);
        var origin = owned["pawn"].AsMap()?["position"].AsMap();
        var originX = origin?["x"].AsNumber() ?? 0;
        var originZ = origin?["z"].AsNumber() ?? 0;

        // The lab map is far larger than the pawn's reach, so the 11x11 square around it only clips at the map's low edges.
        var cellsReply = await h.WireAsync(
            "candidate-cells",
            "observations_get_cells",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["rectangle"] = new JsonObject
                {
                    ["minimum"] = new JsonObject { ["x"] = Math.Max(originX - 5, 0), ["z"] = Math.Max(originZ - 5, 0) },
                    ["maximum"] = new JsonObject { ["x"] = originX + 5, ["z"] = originZ + 5 },
                },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observedCells) = Harness.Outcome(cellsReply, "observed");
        var candidateCells = Candidates(observedCells, originX, originZ);

        // Native judges reachability when the move applies: the first candidate it applies is the destination.
        JsonObject? destination = null;
        for (var number = 0; number < Math.Min(candidateCells.Count, 24); number++)
        {
            var cell = candidateCells[number];
            var label = "move-" + number.ToString(CultureInfo.InvariantCulture);
            var result = await Apply(label, label, pawnId, cell).ConfigureAwait(false);
            if (result.ContainsKey("applied"))
            {
                destination = cell;
                break;
            }

            if (!result.ContainsKey("refused"))
            {
                throw new AcceptanceException($"{label}: neither applied nor refused: {result.ToJson()}");
            }
        }

        if (destination is null)
        {
            throw new AcceptanceException("no nearby destination applied");
        }

        var moving = await Read("moving", pawnId).ConfigureAwait(false);
        var def = moving["job"].AsMap()?["defName"].AsString() ?? string.Empty;
        if (def != "Goto")
        {
            throw new AcceptanceException($"moving: pawn job is \"{def}\", not Goto");
        }

        var again = await Apply("resend", "move-resend", pawnId, destination).ConfigureAwait(false);
        if (!again.ContainsKey("applied"))
        {
            throw new AcceptanceException($"resend: a matching order was not applied as a no-op: {again.ToJson()}");
        }

        s.Report["pawn_id"] = pawnId;
        s.Report["destination"] = destination.DeepClone();
    }

    /// <summary>Filters and orders an observations_get_cells reply's grid to the exact set of nearby, walkable, unfogged candidates.</summary>
    private static List<JsonObject> Candidates(JsonObject snapshot, double originX, double originZ)
    {
        var (_, grid) = CellReads.DecodeCells(snapshot);
        var result = new List<(int X, int Z, double Distance)>();
        foreach (var cell in grid.Cells())
        {
            double cx = cell.Cell.X;
            double cz = cell.Cell.Z;
            var distance = ((cx - originX) * (cx - originX)) + ((cz - originZ) * (cz - originZ));
            if (distance >= 4 && distance <= 25 && cell.Walkable == true)
            {
                result.Add((cell.Cell.X, cell.Cell.Z, distance));
            }
        }

        return [.. result
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.X)
            .ThenBy(c => c.Z)
            .Select(c => CaseSupport.Cell(c.X, c.Z))];
    }
}
