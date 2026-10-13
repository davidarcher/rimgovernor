using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The draft acceptance cases (Go <c>cases/draft</c>): <c>draft/intent</c> and <c>takeover/draft</c>, which Go keeps in
/// the draft package.
/// </summary>
public static class DraftCases
{
    /// <summary>Registers <c>draft/intent</c> and <c>takeover/draft</c>.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(DraftIntent.Case());
        registry.Register(DraftTakeover.Case());
    }

    /// <summary>The census row for <paramref name="id"/>, or the first colonist when <paramref name="id"/> is empty (Go <c>readPawn</c>).</summary>
    internal static async Task<JsonObject> ReadPawnAsync(Harness h, JsonObject identity, string label, string id, CancellationToken cancellationToken)
    {
        var filter = new JsonObject { ["colonist"] = true, ["downed"] = false };
        if (id.Length > 0)
        {
            filter["ids"] = new JsonArray(id);
        }

        var reply = await h.WireAsync(
            label,
            "observations_list_pawns",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["filter"] = filter },
            cancellationToken).ConfigureAwait(false);
        return Draft.PawnRow(reply, identity, id);
    }

    /// <summary>Applies one draft intent and checks issued and the census readback (Go <c>expectDraft</c>).</summary>
    internal static async Task ExpectDraftAsync(Harness h, JsonObject identity, string key, string id, bool drafted, bool issued, CancellationToken cancellationToken)
    {
        var job = await ApplyIntents.ApplyDraftAsync(h, key, identity, key, id, drafted, cancellationToken).ConfigureAwait(false);
        var got = job["issued"].AsBool() == true;
        if (got != issued)
        {
            throw new AcceptanceException($"{key}: issued={Flag(got)}, want {Flag(issued)}: {job.ToJson()}");
        }

        var row = await ReadPawnAsync(h, identity, key + "-readback", id, cancellationToken).ConfigureAwait(false);
        var census = row["drafted"].AsBool() == true;
        if (census != drafted)
        {
            throw new AcceptanceException($"{key}: census drafted={Flag(census)}, want {Flag(drafted)}");
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";
}
