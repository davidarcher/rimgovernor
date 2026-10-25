using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using KnownPsycast = RimGovernor.Brain.Policy.KnownPsycast;
using PolicyPsycast = RimGovernor.Brain.Policy.Psycast;
using PolicyRoyaltyFacts = RimGovernor.Brain.Policy.RoyaltyFacts;

namespace RimGovernor.Brain.GameAccess;

public static class RoyaltyFactsRules
{
    /// <summary>
    /// Reads each colonist's own royalty facts (the pawn row's royalty block; the
    /// ladder and permits are def rows, the neuroformers, ceremonies and thrones the colony section). A colonist
    /// without royalty, or with neither a holding nor a psycast, is not listed; a colonist without a psylink has no
    /// caster entry. <paramref name="psycast"/> is the catalog's psycast row for a def name (<c>DefinitionCatalog.Psycast</c>).
    /// </summary>
    /// <exception cref="BridgeContractException">There are no pawn rows.</exception>
    public static PolicyRoyaltyFacts PawnRoyaltyFacts(PawnSnapshot? pawns, Func<string, PolicyPsycast> psycast)
    {
        if (pawns is null)
        {
            throw BridgeContractException.Contract("no pawn rows for the royalty facts");
        }
        var holders = new Dictionary<PawnId, EquatableArray<PawnRoyalHolding>>();
        var psycasts = new Dictionary<PawnId, EquatableArray<KnownPsycast>>();
        var casters = new Dictionary<PawnId, PawnPsylink>();
        foreach (var pawn in pawns.Pawns)
        {
            if (pawn.Royalty is not PawnRoyaltyState.Active { Facts: var row } || (row.Holdings.Count == 0 && row.Psycasts.Count == 0))
            {
                continue;
            }
            var key = new PawnId(pawn.Pawn.Id ?? "");
            holders[key] = row.Holdings;
            psycasts[key] = EquatableArray.From(row.Psycasts.Select(cast => new KnownPsycast(psycast(cast.DefName), cast)));
            if (row.Psylink is { } link)
            {
                casters[key] = link;
            }
        }
        return new PolicyRoyaltyFacts { Holders = holders, Psycasts = psycasts, Casters = casters };
    }
}
