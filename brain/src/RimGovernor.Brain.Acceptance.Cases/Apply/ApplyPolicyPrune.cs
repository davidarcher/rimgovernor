using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apply/policy-prune</c>: PolicyPruneIntent on Actions/Apply (#1298). An extra outfit
/// and an extra allowed area, a colonist assigned to both (test/gear_fixture, which the primitives cannot express), are
/// deleted; the colonist first moves onto its own outfit labelled with its short name and becomes unrestricted; a resent
/// prune applies again with nothing deleted. The colony read's policy facts show each result.
/// </summary>
internal static class ApplyPolicyPrune
{
    private const string Tool = "test/gear_fixture";

    private static readonly FixtureOp<NoArgs, Fx.GearPruneReply> Setup = new(
        Tool,
        _ => new JsonObject([new KeyValuePair<string, JsonNode?>("mode", "prune_setup")]),
        ServedOps.Succeeded<Fx.GearPruneReply>(Tool, r => r is { Success: true, Pawn.Length: > 0, ShortName.Length: > 0, Outfit.Length: > 0, Area.Length: > 0 }));

    internal static TypedCase Case() => new()
    {
        Name = "apply/policy-prune",
        Scope = "PolicyPruneIntent on Actions/Apply (#1298): an extra outfit and an extra allowed area, a colonist assigned "
            + "to both, are deleted; the colonist first moves onto its own outfit labelled with its short name and becomes "
            + "unrestricted; a resent prune applies again with nothing deleted. A unit test cannot see vanilla TryDelete or the "
            + "reassignment.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var before = await s.FixtureAsync("prune-setup", Setup, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("setup", ServedOps.Json(before));
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task PruneAsync(string key, Operations.PolicyDatabase database, string id, bool wantDeleted)
        {
            var outcome = await TypedApply.OneAsync(s, key, new Operations.PolicyPruneIntent { Database = database, DeleteIds = [id] }, cancellationToken).ConfigureAwait(false);
            var effect = ApplyCases.Effect(outcome)?.PolicyPrune ?? throw new AcceptanceException($"{key}: expected an applied policy prune, got {outcome}");
            s.RecordWire(key, effect);
            var ok = wantDeleted
                ? effect.DeletedIds.ToList() is [var deleted] && deleted == id && effect.ReassignedPawnIds.Contains(before.Pawn)
                : effect.DeletedIds.Count == 0 && effect.ReassignedPawnIds.Count == 0;
            if (!ok)
            {
                throw new AcceptanceException($"{key}: expected deleted={wantDeleted} with {before.Pawn} moved, got {effect}");
            }
        }

        await PruneAsync("prune-outfit", Operations.PolicyDatabase.Outfit, before.Outfit, true).ConfigureAwait(false);
        await PruneAsync("prune-area", Operations.PolicyDatabase.AllowedArea, before.Area, true).ConfigureAwait(false);

        var reply = await s.CallAsync("after", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, cancellationToken).ConfigureAwait(false);
        var policies = reply.Policies;
        s.RecordWire("after_outfits", policies.Outfit);
        s.RecordWire("after_areas", policies.AllowedAreas);
        if (policies.Outfit.Any(o => o.Id == before.Outfit) || policies.AllowedAreas.Any(a => a.Id == before.Area))
        {
            throw new AcceptanceException("after: the pruned outfit or allowed area still stands");
        }

        var own = policies.Outfit.SingleOrDefault(o => o.Pawns.Any(p => p.Value == before.Pawn));
        if (own?.Label != before.ShortName || policies.AllowedAreas.Any(a => a.Pawns.Any(p => p.Value == before.Pawn)))
        {
            throw new AcceptanceException($"after: {before.Pawn} is not on its own outfit \"{before.ShortName}\" and unrestricted: outfit {own}");
        }

        await PruneAsync("prune-outfit-again", Operations.PolicyDatabase.Outfit, before.Outfit, false).ConfigureAwait(false);
        await PruneAsync("prune-area-again", Operations.PolicyDatabase.AllowedArea, before.Area, false).ConfigureAwait(false);
    }
}
