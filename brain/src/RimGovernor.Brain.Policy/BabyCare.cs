using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A current observation, not a promise of feeding or a second care ledger.</summary>
public sealed record BabyFeedingStatus(string Baby, bool Hungry,
    BabyCareActiveRead? Active, BabyCareOpportunityRead? Opportunity, string Blocker);

public static class BabyCareRules
{
    /// <summary>Assess finite current provision without commanding a carer. A potential nurse is counted at most
    /// once across babies for the provisioning floor; only actual native care occupies worker capacity.
    /// Milk refill alone is not milk available now. Recomputed from every observation.</summary>
    public static EquatableArray<BabyFeedingStatus> AssessBabyCare(NeedsForecastRead read)
    {
        var babies = read.Babies.Babies;
        var held = babies.Where(b => b.Care.Active is not null).Select(b => b.Care.Active!.Carer)
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<BabyFeedingStatus>();
        foreach (var baby in babies.OrderBy(b => b.Care.Food?.Level ?? -1).ThenBy(b => b.Pawn, StringComparer.Ordinal))
        {
            var care = baby.Care;
            var hungry = care.Food is null || care.Food.WantsFeeding;
            var opportunity = care.Active is not null ? null : care.Opportunities
                .Where(o => o.Kind is BabyCareKind.Breastfeed or BabyCareKind.BottleFeed && o.ReadyToFeed && o.FoodAvailable > 0
                    && !held.Contains(o.Carer)
                    && read.Pawns.Any(p => p.Pawn == o.Carer && p.State == "eligible" && p.VoluntaryControlBlockers.Count == 0))
                .OrderBy(o => o.Kind).ThenBy(o => o.Carer, StringComparer.Ordinal).FirstOrDefault();
            if (opportunity is not null) held.Add(opportunity.Carer);
            result.Add(new(baby.Pawn, hungry, care.Active, opportunity,
                care.Food is null ? "baby_food_unobserved" : !hungry ? "" : care.Active is not null ? "native_care_active"
                    : opportunity is not null ? "awaiting_native_feeding" : "no_usable_feeding_opportunity"));
        }
        return EquatableArray.From(result);
    }

    /// <summary>Actual native care occupies capacity through carrying/return. A hungry baby or a possible
    /// feeding opportunity does not reserve a caregiver in advance.</summary>
    public static IReadOnlySet<string> CarePawns(NeedsForecastRead? read) => read is null ? new HashSet<string>()
        : read.Babies.Babies.SelectMany(b => b.Care.Active is { } active ? new[] { b.Pawn, active.Carer } : [])
            .ToHashSet(StringComparer.Ordinal);
}
