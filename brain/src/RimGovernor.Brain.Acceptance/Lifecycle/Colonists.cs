using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Colonist reads and the PawnState row accessors (Go <c>colonists.go</c>).</summary>
public static class Colonists
{
    /// <summary>
    /// Reads the current map's colonists through <c>observations_list_pawns</c> at the loaded identity:
    /// <paramref name="includeDead"/> adds the inner pawns of spawned corpses, <paramref name="health"/> requests
    /// each row's health section. Every other detail family is off. Returns the PawnState rows;
    /// <see cref="RowID"/> names a row's pawn.
    /// </summary>
    public static async Task<IReadOnlyList<JsonObject>> ListColonistsAsync(Harness harness, string label, bool includeDead, bool health, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var identity = await Session.ReadIdentityAsync(harness, label + "-identity", cancellationToken).ConfigureAwait(false);
        var filter = new JsonObject { ["colonist"] = true };
        if (includeDead)
        {
            filter["includeDead"] = true;
        }

        var reply = await harness.WireAsync(
            label,
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity },
                ["filter"] = filter,
                ["details"] = new JsonObject
                {
                    ["needs"] = false,
                    ["health"] = health,
                    ["equipment"] = false,
                    ["biography"] = false,
                    ["settings"] = false,
                    ["social"] = false,
                    ["animals"] = false,
                    ["work"] = false,
                    ["schedule"] = false,
                },
            },
            cancellationToken).ConfigureAwait(false);
        JsonObject observed;
        try
        {
            (_, observed) = Harness.Outcome(reply, "observed");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }

        var rows = new List<JsonObject>();
        foreach (var raw in observed["pawns"].AsSlice())
        {
            if (raw.AsMap() is { } row)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>A PawnState row's pawn id.</summary>
    public static string RowID(JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row["pawn"].Member("id").AsString();
    }

    /// <summary>A typed Hediff's definition name.</summary>
    public static string HediffDef(JsonObject hediff)
    {
        ArgumentNullException.ThrowIfNull(hediff);
        return hediff["definition"].Member("defName").AsString();
    }
}
