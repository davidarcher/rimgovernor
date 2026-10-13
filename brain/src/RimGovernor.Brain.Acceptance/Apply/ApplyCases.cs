using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <c>apply/*</c> lab contract cases (Go <c>cases/apply</c>): Actions/Apply behaviours a Go unit test cannot see
/// because they live in the native handlers. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class ApplyCases
{
    /// <summary>Registers every <c>apply/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ApplyActions.Case());
        registry.Register(ApplyAreaIntent.Case());
        registry.Register(ApplyDeferredSnapshot.Case());
        registry.Register(ApplyPolicyPrune.Case());
        registry.Register(ApplyRefusal.Case());
    }

    /// <summary>
    /// The single result of an operations_apply reply that carried one action (a missing or extra result is the case's
    /// failure, with the reply in the message).
    /// </summary>
    internal static JsonObject OneResult(string label, JsonObject reply)
    {
        var results = reply["results"].AsSlice();
        if (results.Count != 1)
        {
            throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}");
        }

        return results[0].AsMap() ?? [];
    }

    /// <summary>
    /// The effect an applied action's receipt observed under <paramref name="effectKey"/>
    /// (<c>result.applied.applied.observed.&lt;effectKey&gt;</c>); null when the result is not an applied one.
    /// </summary>
    internal static JsonObject? AppliedEffect(JsonObject result, string effectKey)
    {
        var applied = result["applied"].AsMap()?["applied"].AsMap();
        return applied?["observed"].AsMap()?[effectKey].AsMap();
    }

    internal static JsonObject Cell(int x, int z) => new() { ["x"] = x, ["z"] = z };
}
