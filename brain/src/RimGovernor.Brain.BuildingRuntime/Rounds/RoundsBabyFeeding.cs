namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Reviews the baby feeding the biotech census and food supply allow; unknown when either is unread or the review refuses.</summary>
    internal void ReviewBabyFeeding(ColonyProjection p)
    {
        p.Facts.BabyFeeding = Unknown<BabyFeeding>();
        var known = p.Biotech.TryGetValue(out var biotech);
        var supplied = p.FoodSupply.TryGetValue(out var supply);
        if (!known || !supplied)
        {
            return;
        }
        var babies = biotech!.Babies.Select(b => b.PawnId).ToList();
        try
        {
            var review = PolicyApi.ReviewBabyFeeding(babies, biotech.Breastfeeders.Count, supply!, Seasonal(p.Facts).FoodTargetDays);
            p.Facts.BabyFeeding = Known(review);
        }
        catch (DomainException)
        {
        }
    }
}
