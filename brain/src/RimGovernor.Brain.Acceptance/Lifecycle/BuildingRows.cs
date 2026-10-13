using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The player building table (Go <c>building_rows.go</c>).</summary>
public static class BuildingTable
{
    /// <summary>
    /// The player building table the bundle carries (every status), keyed by building id: the canonical rows a
    /// section's building references resolve against.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, JsonObject>> BuildingRowsAsync(this Harness harness, string label, JsonNode identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(identity);
        var reply = await harness.WireAsync(
            label,
            "observations_list_buildings",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["statuses"] = new JsonArray("all"),
                ["playerOnly"] = true,
            },
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
        foreach (var raw in listed["buildings"].AsSlice())
        {
            if (raw.AsMap() is { } row)
            {
                table[row["building"].RefId()] = row;
            }
        }

        return table;
    }
}
