using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>
    /// Reviews the babies' food. A colony without an active Biotech section has no babies, so the review is the
    /// empty one that owes nothing; a baby the food supply has no consumer row for breaks the frame's contract.
    /// </summary>
    internal ColonyReading ReviewBabyFeeding(ColonyReading p)
    {
        if (p.Snapshot.Biotech is not RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active { Facts: var biotech })
        {
            return p with { Facts = p.Facts with { BabyFeeding = new BabyFeeding() } };
        }
        var babies = biotech.Babies.Select(b => new PawnId(b)).ToList();
        var review = BabyFeedingRules.ReviewBabyFeeding(babies, biotech.Breastfeeders.Count, p.Facts.FoodSupply, Seasonal(p.Facts).FoodTargetDays);
        return p with { Facts = p.Facts with { BabyFeeding = review } };
    }
}
