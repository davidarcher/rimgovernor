using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
namespace RimGovernor.Brain.Bridge;

public static partial class ColonyPlanningReads
{
    /// <summary>
    /// The royalty read: the colonists' own holdings and psycasts from their pawn rows, the ladder and permits from
    /// the def mirror and the colony <paramref name="section"/>'s neuroformers, ceremonies and thrones. The colony
    /// section is the Royalty gate, as for the other DLC sections: an inactive one is the Inactive state and nothing
    /// is read. A pawn row or def the read cannot use throws.
    /// </summary>
    public static RoyaltyState RoyaltyOf(RimGovernor.Brain.GameFacts.Observations.RoyaltySection section, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? pawns, DefinitionCatalog catalog)
    {
        if (section is not RimGovernor.Brain.GameFacts.Observations.RoyaltySection.Active { Facts: var colony })
        {
            return new RoyaltyState.Inactive();
        }
        var facts = RoyaltyFactsRules.PawnRoyaltyFacts(pawns, catalog.Psycast);
        return new RoyaltyState.Active(catalog.WithTitleDefs(facts).WithColony(catalog.WithNeuroformerDefs(colony), colony));
    }
}
