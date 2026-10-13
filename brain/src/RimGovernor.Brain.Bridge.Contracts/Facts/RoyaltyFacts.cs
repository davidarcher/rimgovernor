using RimGovernor.Contracts.Wire.Observations;
using PolicyPawnId = RimGovernor.Brain.Policy.PawnId;
using PolicyPermitCooldown = RimGovernor.Brain.Policy.PermitCooldown;
using PolicyPsycast = RimGovernor.Brain.Policy.Psycast;
using PolicyPsycasterState = RimGovernor.Brain.Policy.PsycasterState;
using PolicyRoyalHolding = RimGovernor.Brain.Policy.RoyalHolding;
using PolicyRoyaltyFacts = RimGovernor.Brain.Policy.RoyaltyFacts;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go royalty.go.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>PawnRoyaltyFacts</c>: reads each colonist's own royalty facts (the pawn row's royalty block; the
    /// ladder and permits are def rows, the neuroformers, ceremonies and thrones the colony section). A colonist
    /// with neither a holding nor a psycast is not listed. A row whose royalty read failed (ReadIssue "royalty")
    /// or whose block is invalid throws: royalty stays unknown rather than read as empty. <paramref name="psycast"/>
    /// is the catalog's psycast row for a def name (<c>DefinitionCatalog.Psycast</c>).
    /// </summary>
    /// <exception cref="BridgeContractException">A row is unread or malformed.</exception>
    public static PolicyRoyaltyFacts PawnRoyaltyFacts(PawnSnapshot? pawns, Func<string, PolicyPsycast> psycast)
    {
        if (pawns is null)
        {
            throw Contract("no pawn rows for the royalty facts");
        }
        var holders = new Dictionary<PolicyPawnId, EquatableArray<PolicyRoyalHolding>>();
        var psycasts = new Dictionary<PolicyPawnId, EquatableArray<PolicyPsycast>>();
        var casters = new Dictionary<PolicyPawnId, PolicyPsycasterState>();
        foreach (var pawn in pawns.Pawns)
        {
            var id = pawn.Pawn?.Id ?? "";
            if (IssueNames(pawn.Issues, "royalty"))
            {
                throw Contract($"royalty of pawn {id} unread");
            }
            var row = pawn.Royalty;
            if (row is null || (row.Holdings.Count == 0 && row.Psycasts.Count == 0))
            {
                continue;
            }
            if (!IsValidId(id))
            {
                throw Contract("invalid royalty pawn");
            }
            var holdings = new List<PolicyRoyalHolding>(row.Holdings.Count);
            foreach (var holding in row.Holdings)
            {
                if (!IsValidId((holding.FactionDef ?? "")) || (holding.Title is not null && !IsValidId((holding.Title ?? ""))))
                {
                    throw Contract("invalid royalty holding");
                }
                if (holding.Permits.Any(permit => !IsValidId(permit)))
                {
                    throw Contract("invalid royalty holding permit");
                }
                var cooldowns = new Dictionary<string, PolicyPermitCooldown>(StringComparer.Ordinal);
                foreach (var cooldown in holding.PermitCooldowns)
                {
                    if (!IsValidId((cooldown.Permit ?? "")) || cooldown.LastUsedTick.GetValueOrDefault() < 0 || cooldown.CooldownRemainingTicks.GetValueOrDefault() < 0)
                    {
                        throw Contract("invalid royalty permit cooldown");
                    }
                    if (!cooldowns.TryAdd(
                        (cooldown.Permit ?? ""),
                        new PolicyPermitCooldown
                        {
                            LastUsedTick = OptionalInt(cooldown.LastUsedTick is not null, cooldown.LastUsedTick.GetValueOrDefault()),
                            RemainingTicks = OptionalInt(cooldown.CooldownRemainingTicks is not null, cooldown.CooldownRemainingTicks.GetValueOrDefault()),
                        }))
                    {
                        throw Contract($"duplicate royalty permit cooldown {(cooldown.Permit ?? "")}");
                    }
                }
                holdings.Add(
                    new PolicyRoyalHolding
                    {
                        FactionDef = (holding.FactionDef ?? ""),
                        Title = holding.Title ?? "",
                        Favor = OptionalInt(holding.Favor is not null, holding.Favor.GetValueOrDefault()),
                        PermitPoints = OptionalInt(holding.PermitPoints is not null, holding.PermitPoints.GetValueOrDefault()),
                        Permits = EquatableArray.From(holding.Permits),
                        Cooldowns = cooldowns,
                    });
            }
            var key = new PolicyPawnId(id);
            holders[key] = EquatableArray.From(holdings);
            var casts = new List<PolicyPsycast>(row.Psycasts.Count);
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cast in row.Psycasts)
            {
                if (!IsValidId((cast.DefName ?? "")) || !known.Add((cast.DefName ?? "")) || cast.CooldownRemainingTicks.GetValueOrDefault() < 0)
                {
                    throw Contract("invalid royalty psycast");
                }
                casts.Add(psycast((cast.DefName ?? "")) with { CooldownRemaining = OptionalInt(cast.CooldownRemainingTicks is not null, cast.CooldownRemainingTicks.GetValueOrDefault()) });
            }
            psycasts[key] = EquatableArray.From(casts);
            if (row.Psyfocus is < 0 or > 1 || row.Entropy.GetValueOrDefault() < 0 || row.EntropyMax.GetValueOrDefault() < 0)
            {
                throw Contract("invalid royalty psycaster state");
            }
            casters[key] = new PolicyPsycasterState
            {
                Psyfocus = OptionalFact(row.Psyfocus is not null, row.Psyfocus.GetValueOrDefault()),
                Entropy = OptionalFact(row.Entropy is not null, row.Entropy.GetValueOrDefault()),
                EntropyMax = OptionalFact(row.EntropyMax is not null, row.EntropyMax.GetValueOrDefault()),
            };
        }
        return new PolicyRoyaltyFacts { Holders = holders, Psycasts = psycasts, Casters = casters };
    }
}
