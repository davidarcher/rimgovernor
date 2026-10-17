using RimGovernor.Contracts.Wire.Observations;
using PolicyPermitCooldown = RimGovernor.Brain.Policy.PermitCooldown;
using PolicyPsycast = RimGovernor.Brain.Policy.Psycast;
using PolicyPsycasterState = RimGovernor.Brain.Policy.PsycasterState;
using PolicyRoyalHolding = RimGovernor.Brain.Policy.RoyalHolding;
using PolicyRoyaltyFacts = RimGovernor.Brain.Policy.RoyaltyFacts;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go royalty.go.
public static class RoyaltyFactsRules
{
    /// <summary>
    /// Go's <c>PawnRoyaltyFacts</c>: reads each colonist's own royalty facts (the pawn row's royalty block; the
    /// ladder and permits are def rows, the neuroformers, ceremonies and thrones the colony section). A colonist
    /// with neither a holding nor a psycast is not listed. A row whose royalty read failed (ReadIssue "royalty")
    /// throws: royalty stays unknown rather than read as empty. <paramref name="psycast"/> is the catalog's psycast
    /// row for a def name (<c>DefinitionCatalog.Psycast</c>).
    /// </summary>
    /// <exception cref="BridgeContractException">There are no pawn rows, or a row's royalty is unread.</exception>
    public static PolicyRoyaltyFacts PawnRoyaltyFacts(PawnSnapshot? pawns, Func<string, PolicyPsycast> psycast)
    {
        if (pawns is null)
        {
            throw BridgeContractException.Contract("no pawn rows for the royalty facts");
        }
        var holders = new Dictionary<PawnId, EquatableArray<PolicyRoyalHolding>>();
        var psycasts = new Dictionary<PawnId, EquatableArray<PolicyPsycast>>();
        var casters = new Dictionary<PawnId, PolicyPsycasterState>();
        foreach (var pawn in pawns.Pawns)
        {
            var id = pawn.Pawn.Id ?? "";
            if (FactHelpers.IssueNames(pawn.Issues, "royalty"))
            {
                throw BridgeContractException.Contract($"royalty of pawn {id} unread");
            }
            var row = pawn.Royalty;
            if (row is null || (row.Holdings.Count == 0 && row.Psycasts.Count == 0))
            {
                continue;
            }
            var holdings = new List<PolicyRoyalHolding>(row.Holdings.Count);
            foreach (var holding in row.Holdings)
            {
                var cooldowns = new Dictionary<string, PolicyPermitCooldown>(StringComparer.Ordinal);
                foreach (var cooldown in holding.PermitCooldowns)
                {
                    cooldowns[cooldown.Permit ?? ""] = new PolicyPermitCooldown
                    {
                        RemainingTicks = FactHelpers.OptionalInt(cooldown.CooldownRemainingTicks is not null, cooldown.CooldownRemainingTicks.GetValueOrDefault()),
                    };
                }
                holdings.Add(
                    new PolicyRoyalHolding
                    {
                        FactionDef = (holding.FactionDef ?? ""),
                        Title = holding.Title ?? "",
                        Favor = FactHelpers.OptionalInt(holding.Favor is not null, holding.Favor.GetValueOrDefault()),
                        PermitPoints = FactHelpers.OptionalInt(holding.PermitPoints is not null, holding.PermitPoints.GetValueOrDefault()),
                        Permits = EquatableArray.From(holding.Permits),
                        Cooldowns = cooldowns,
                    });
            }
            var key = new PawnId(id);
            holders[key] = EquatableArray.From(holdings);
            var casts = new List<PolicyPsycast>(row.Psycasts.Count);
            foreach (var cast in row.Psycasts)
            {
                casts.Add(psycast(cast.DefName ?? "") with { CooldownRemaining = FactHelpers.OptionalInt(cast.CooldownRemainingTicks is not null, cast.CooldownRemainingTicks.GetValueOrDefault()) });
            }
            psycasts[key] = EquatableArray.From(casts);
            casters[key] = new PolicyPsycasterState
            {
                Psyfocus = FactHelpers.OptionalFact(row.Psyfocus is not null, row.Psyfocus.GetValueOrDefault()),
                Entropy = FactHelpers.OptionalFact(row.Entropy is not null, row.Entropy.GetValueOrDefault()),
                EntropyMax = FactHelpers.OptionalFact(row.EntropyMax is not null, row.EntropyMax.GetValueOrDefault()),
            };
        }
        return new PolicyRoyaltyFacts { Holders = holders, Psycasts = psycasts, Casters = casters };
    }
}
