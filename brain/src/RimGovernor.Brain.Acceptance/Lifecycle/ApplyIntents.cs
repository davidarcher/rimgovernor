using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Single-intent <c>operations_apply</c> helpers and their result accessors (Go <c>applyone.go</c>).</summary>
public static class ApplyIntents
{
    /// <summary>The GiveJobIntent of <paramref name="pawn"/> taking the vanilla JobDef <paramref name="job"/> on <paramref name="targets"/>, in job target order.</summary>
    public static JsonObject GiveJob(string pawn, string job, params string[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var refs = new JsonArray();
        foreach (var target in targets)
        {
            refs.Add(new JsonObject { ["id"] = target });
        }

        return new JsonObject
        {
            ["giveJob"] = new JsonObject { ["pawn"] = new JsonObject { ["id"] = pawn }, ["job"] = job, ["targets"] = refs },
        };
    }

    /// <summary>
    /// Sends one Actions/Apply intent under <paramref name="key"/> and returns its result: an object holding
    /// "applied" (a receipt) or "refused" (code, reason).
    /// </summary>
    public static async Task<JsonObject> ApplyOneAsync(Harness harness, string label, JsonNode identity, string key, JsonObject intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(intent);
        var action = new JsonObject { ["key"] = key };
        foreach (var (name, value) in intent)
        {
            action[name] = value?.DeepClone();
        }

        var reply = await harness.WireAsync(
            label,
            "operations_apply",
            new JsonObject { ["identity"] = identity.DeepClone(), ["actions"] = new JsonArray(action) },
            cancellationToken).ConfigureAwait(false);
        var results = reply["results"].AsSlice();
        return results.Count != 1
            ? throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}")
            : results[0].AsMap() ?? [];
    }

    /// <summary>
    /// Drafts or undrafts <paramref name="pawn"/> through a DraftIntent and returns the applied job evidence (issued
    /// is false when the pawn already stood that way), or throws naming a refusal.
    /// </summary>
    public static async Task<JsonObject> ApplyDraftAsync(Harness harness, string label, JsonNode identity, string key, string pawn, bool drafted, CancellationToken cancellationToken)
    {
        var intent = new JsonObject { ["draft"] = new JsonObject { ["pawnId"] = pawn, ["drafted"] = drafted } };
        var result = await ApplyOneAsync(harness, label, identity, key, intent, cancellationToken).ConfigureAwait(false);
        return AppliedJob(label, result);
    }

    /// <summary>
    /// Sends one combat_orders batch as an Actions/Apply intent under <paramref name="key"/> and returns its per-order
    /// results. The intent always applies; each order carries its own applied or refusal.
    /// </summary>
    public static async Task<IReadOnlyList<JsonObject>> ApplyCombatOrdersAsync(Harness harness, string label, JsonNode identity, string key, JsonArray orders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var intent = new JsonObject { ["combatOrders"] = new JsonObject { ["orders"] = orders.DeepClone() } };
        var result = await ApplyOneAsync(harness, label, identity, key, intent, cancellationToken).ConfigureAwait(false);
        var applied = result["applied"].Member("applied").AsMap()
            ?? throw new AcceptanceException($"{label}: combat orders not applied: {result.ToJson()}");
        var rows = applied["observed"].Member("combatOrders").Member("results").AsSlice();
        if (rows.Count != orders.Count)
        {
            throw new AcceptanceException($"{label}: {rows.Count} results for {orders.Count} orders: {result.ToJson()}");
        }

        return [.. rows.Select(row => row.AsMap() ?? [])];
    }

    /// <summary>The job evidence of an applied result, or an exception naming the refusal.</summary>
    public static JsonObject AppliedJob(string label, JsonObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var applied = result["applied"].Member("applied").AsMap()
            ?? throw new AcceptanceException($"{label}: expected applied, got {result.ToJson()}");
        return applied["observed"].Member("job").AsMap() ?? [];
    }

    /// <summary>Checks a result is a refusal whose reason contains <paramref name="want"/>.</summary>
    public static void Refused(string label, JsonObject result, string want)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result["refused"].AsMap() is not { } refusal || !refusal["reason"].AsString().Contains(want, StringComparison.Ordinal))
        {
            throw new AcceptanceException($"{label}: expected a refusal containing \"{want}\", got {result.ToJson()}");
        }
    }
}
