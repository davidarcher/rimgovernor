using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The map's pawn table and the join of section rows against it (Go <c>pawn_rows.go</c>).</summary>
public static class PawnTable
{
    /// <summary>
    /// The map's pawn table as the list read answers it (every spawned pawn, every detail family), keyed by pawn id:
    /// the canonical rows a section's pawn references resolve against.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, JsonObject>> PawnRowsAsync(this Harness harness, string label, JsonNode identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(identity);
        var reply = await harness.WireAsync(
            label,
            "observations_list_pawns",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        JsonObject listed;
        try
        {
            (_, listed) = Harness.Outcome(reply, "observed");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }

        var table = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var raw in listed["pawns"].AsSlice())
        {
            if (raw.AsMap() is { } row)
            {
                table[row["pawn"].RefId()] = row;
            }
        }

        return table;
    }

    /// <summary>The id a section row's pawn reference names.</summary>
    public static string PawnRef(JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row["pawn"].RefId();
    }

    /// <summary>
    /// Replaces <paramref name="row"/>'s pawn reference with its table row, so a case reads the pawn's facts where
    /// the section once embedded them.
    /// </summary>
    public static async Task JoinPawnAsync(this Harness harness, string label, JsonNode identity, JsonObject row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var pawns = await PawnRowsAsync(harness, label + "-pawns", identity, cancellationToken).ConfigureAwait(false);
        var id = PawnRef(row);
        row["pawn"] = pawns.TryGetValue(id, out var pawn)
            ? pawn.DeepClone()
            : throw new AcceptanceException($"{label}: pawn {id} missing from the pawn table");
    }
}
