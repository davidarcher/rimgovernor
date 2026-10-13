using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>apply/policy-prune</c> (Go <c>policy_prune.go</c>): PolicyPruneIntent on Actions/Apply (#1298). An extra outfit
/// and an extra allowed area, a colonist assigned to both, are deleted; the colonist first moves onto its own outfit
/// labelled with its short name and becomes unrestricted; a resent prune applies again with nothing deleted.
/// </summary>
internal static class ApplyPolicyPrune
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "apply/policy-prune",
        Scope = "PolicyPruneIntent on Actions/Apply (#1298): an extra outfit and an extra allowed area, a colonist assigned "
            + "to both, are deleted; the colonist first moves onto its own outfit labelled with its short name and becomes "
            + "unrestricted; a resent prune applies again with nothing deleted. A Go test cannot see vanilla TryDelete or the "
            + "reassignment.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static List<string> List(JsonNode? value) => [.. value.AsSlice().Select(x => x.AsString())];

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;

        Task<JsonObject> Fixture(string mode) =>
            h.CallAsync(mode, "test/gear_fixture", new JsonObject { ["mode"] = mode }, cancellationToken);

        var before = await Fixture("prune_setup").ConfigureAwait(false);
        report["setup"] = before;
        var pawn = before["pawn"].AsString();
        var outfit = before["outfit"].AsString();
        var area = before["area"].AsString();
        if (pawn.Length == 0 || outfit.Length == 0 || area.Length == 0)
        {
            throw new AcceptanceException($"prune fixture assigned nothing: {before.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task Prune(string key, string db, string id, bool wantDeleted)
        {
            var reply = await h.WireAsync(
                key,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["policyPrune"] = new JsonObject { ["database"] = db, ["deleteIds"] = new JsonArray(id) },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var result = ApplyCases.OneResult(key, reply);
            var effect = ApplyCases.AppliedEffect(result, "policyPrune")
                ?? throw new AcceptanceException($"{key}: expected an applied policy prune, got {result.ToJson()}");
            var deleted = List(effect["deletedIds"]);
            var moved = List(effect["reassignedPawnIds"]);
            var ok = wantDeleted
                ? deleted.Count == 1 && deleted[0] == id && moved.Contains(pawn, StringComparer.Ordinal)
                : deleted.Count == 0 && moved.Count == 0;
            if (!ok)
            {
                throw new AcceptanceException($"{key}: expected deleted={(wantDeleted ? "true" : "false")} with {pawn} moved, got {effect.ToJson()}");
            }

            report[key] = effect;
        }

        await Prune("prune-outfit", "POLICY_DATABASE_OUTFIT", outfit, true).ConfigureAwait(false);
        await Prune("prune-area", "POLICY_DATABASE_ALLOWED_AREA", area, true).ConfigureAwait(false);
        var after = await Fixture("prune_read").ConfigureAwait(false);
        report["after"] = after;
        if (List(after["outfits"]).Contains(outfit, StringComparer.Ordinal) || List(after["areas"]).Contains(area, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"pruned policy or area still stands: {after.ToJson()}");
        }

        if (after["outfitLabel"].AsString() != before["shortName"].AsString() || after["outfit"].AsString() == outfit || after["area"].AsString().Length != 0)
        {
            throw new AcceptanceException($"pawn not moved onto its own outfit and off the area: {after.ToJson()}");
        }

        await Prune("prune-outfit-again", "POLICY_DATABASE_OUTFIT", outfit, false).ConfigureAwait(false);
        await Prune("prune-area-again", "POLICY_DATABASE_ALLOWED_AREA", area, false).ConfigureAwait(false);
    }
}
