using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Reviews the baby feeding the biotech census and food supply allow; unknown when either is unread or the review refuses.</summary>
    internal ColonyProjection ReviewBabyFeeding(ColonyProjection p)
    {
        p = p with { Facts = p.Facts with { BabyFeeding = Unknown<BabyFeeding>() } };
        if (p.BiotechSection is not PbObservations.BiotechSection.Active { Facts: var biotech } || !p.FoodSupply.TryGetValue(out var supply))
        {
            return p;
        }
        var babies = biotech.Babies.Select(b => new PawnId(b)).ToList();
        try
        {
            var review = BabyFeedingRules.ReviewBabyFeeding(babies, biotech.Breastfeeders.Count, supply, Seasonal(p.Facts).FoodTargetDays);
            p = p with { Facts = p.Facts with { BabyFeeding = Known(review) } };
        }
        catch (DomainException)
        {
        }
        return p;
    }
}
