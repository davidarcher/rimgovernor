using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The building and failure readers the <c>wall/upgrade</c> and <c>wall/removal</c> cases share (Go <c>upgrade.go</c>, <c>removal.go</c>).</summary>
internal static class WallCensus
{
    /// <summary>The reply's <c>failure.code</c>, null when the reply is not a failure.</summary>
    internal static string? FailureCode(JsonObject reply) => reply["failure"].AsMap() is { } failure ? failure["code"].AsString() : null;

    /// <summary>A request scope pinned to <paramref name="identity"/>.</summary>
    internal static JsonObject Scope(JsonNode? identity) => new() { ["expectedIdentity"] = identity?.DeepClone() };

    /// <summary>The <c>observed</c> body of an observation reply, or an exception naming the label and the outcome found.</summary>
    internal static JsonObject Observed(string label, JsonObject reply)
    {
        try
        {
            return Harness.Outcome(reply, "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }
    }

    /// <summary>The typed building census for every built player Wall id, sorted.</summary>
    internal static async Task<List<string>> ColonistWallsAsync(Harness h, JsonObject scope, string label, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["scope"] = scope.DeepClone(),
            ["defNames"] = new JsonArray("Wall"),
            ["statuses"] = new JsonArray("built"),
            ["playerOnly"] = true,
        };
        var reply = await h.WireAsync(label, "observations_list_buildings", request, cancellationToken).ConfigureAwait(false);
        var observed = Observed(label, reply);
        var ids = observed["buildings"].AsSlice().Select(raw => raw.Member("building").Member("id").AsString()).ToList();
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }
}
