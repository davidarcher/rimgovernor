using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The colony-facts read the tools cases share (Go: each case's inline read plus <c>bridge.ValidateColonyFacts</c>).</summary>
internal static class ToolsFacts
{
    /// <summary>
    /// Reads <c>observations_read_colony_facts</c> against the loaded identity and returns the observed facts. The Go
    /// <c>ValidateColonyFacts</c> checked the context identity against itself; here the context identity must equal the
    /// identity the read was scoped to.
    /// </summary>
    internal static async Task<JsonObject> ReadAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        var identity = await Session.ReadIdentityAsync(h, label + "-identity", cancellationToken).ConfigureAwait(false);
        var raw = await h.WireAsync(label, "observations_read_colony_facts", new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity } }, cancellationToken).ConfigureAwait(false);
        var observed = raw.Member("observed").AsMap()
            ?? throw new AcceptanceException($"colony unavailable: {raw.ToJson()}");
        var got = observed.Member("context").Member("identity").AsMap();
        if (got is null || got["colonyId"].AsString() != identity["colonyId"].AsString() || got["loadToken"].AsString() != identity["loadToken"].AsString())
        {
            throw new AcceptanceException($"colony facts carry identity {got?.ToJson()}, want {identity.ToJson()}");
        }

        return observed;
    }
}
