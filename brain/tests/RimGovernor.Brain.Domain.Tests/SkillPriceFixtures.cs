using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// What a context that prices skill XP (<see cref="ForecastCoupledContext.Investment"/>) also carries since #3912: each priced stock's
/// next level (<see cref="ForecastCoupledContext.SkillRaises"/>) and each of its pawns' upkeep read. The pawns read no work speed from
/// the skill, so only the harm price applies; <see cref="SkillValueTests"/> and <see cref="ClassroomCreditTests"/> price the work part.
/// </summary>
internal static class SkillPriceFixtures
{
    internal static ForecastCoupledContext Priced(this ForecastCoupledContext context)
    {
        var keys = context.Investment.Keys.Where(k => k.StartsWith("skill/", StringComparison.Ordinal)).ToArray();
        var pawns = context.Needs.Pawns.Select(p => p.Pawn).ToHashSet(StringComparer.Ordinal);
        var missing = keys.Select(k => k.Split('/')[1]).Where(p => !pawns.Contains(p)).Distinct(StringComparer.Ordinal)
            .Select(p => NeedsFixtures.Pawn() with { Pawn = p }).ToArray();
        return context with
        {
            SkillRaises = keys.ToDictionary(k => k, k => context.SkillRaises.TryGetValue(k, out var r) ? r : new SkillRaise(1000, 30), StringComparer.Ordinal),
            Needs = context.Needs with { Pawns = [.. context.Needs.Pawns, .. missing] },
        };
    }
}