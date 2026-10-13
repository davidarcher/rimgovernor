using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What the trade cases share: reading an applied trade intent's outcome and an identity-scoped observation.</summary>
internal static class TradeKit
{
    /// <summary>
    /// The <c>trade</c> outcome an applied trade intent observed (<c>applied.applied.observed.trade</c>); the result must
    /// be an applied one.
    /// </summary>
    internal static JsonObject AppliedTrade(string label, JsonObject result)
    {
        var receipt = result["applied"].AsMap();
        var applied = receipt?["applied"].AsMap()
            ?? throw new AcceptanceException($"{label}: expected an applied outcome, got {result.ToJson()}");
        return applied["observed"].AsMap()?["trade"].AsMap() ?? [];
    }

    /// <summary>The refusal code of a refused result, empty for any other result.</summary>
    internal static string RefusedCode(JsonObject result) => result["refused"].AsMap()?["code"].AsString() ?? "";

    /// <summary>Sends one <c>observations_*</c> read under the identity and returns its <c>observed</c> outcome.</summary>
    internal static async Task<JsonObject> ObserveAsync(Harness h, string label, string method, JsonObject identity, JsonObject? extra, CancellationToken cancellationToken)
    {
        var request = CaseJson.Scoped(identity, extra ?? []);
        var reply = await h.WireAsync(label, method, request, cancellationToken).ConfigureAwait(false);
        return Harness.Outcome(reply, "observed").Value;
    }
}
