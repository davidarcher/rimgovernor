namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Reviews the baby feeding the biotech census and food supply allow; unknown when either is unread or the review refuses.</summary>
    internal ColonyProjection ReviewBabyFeeding(ColonyProjection p)
    {
        p = p with { Facts = p.Facts with { BabyFeeding = Unknown<BabyFeeding>() } };
        var known = p.Biotech.TryGetValue(out var biotech);
        var supplied = p.FoodSupply.TryGetValue(out var supply);
        if (!known || !supplied)
        {
            return p;
        }
        var babies = biotech!.Babies.Select(b => new PawnId(b.PawnID)).ToList();
        try
        {
            var review = PolicyApi.ReviewBabyFeeding(babies, biotech.Breastfeeders.Count, supply!, Seasonal(p.Facts).FoodTargetDays);
            p = p with { Facts = p.Facts with { BabyFeeding = Known(review) } };
        }
        catch (DomainException)
        {
        }
        return p;
    }
}
