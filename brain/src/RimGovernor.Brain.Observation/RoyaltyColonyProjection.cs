namespace RimGovernor.Brain.Observation;

public sealed partial record ColonyProjection
{
    /// <summary>
    /// The royalty fact: the colonists' own holdings and psycasts from their pawn rows, the ladder and permits
    /// from the def mirror and the colony <paramref name="section"/>'s neuroformers, ceremonies and thrones. The colony
    /// section is the Royalty-applicable gate, as for the other DLC sections: an inactive one is no royalty fact and
    /// nothing is read. A pawn row or def the read cannot use throws.
    /// </summary>
    public static Fact<RoyaltyFacts> RoyaltyOf(O.RoyaltySection section, O.PawnSnapshot? pawns, DefinitionCatalog catalog)
    {
        if (section is not O.RoyaltySection.Active { Facts: var colony })
        {
            return Unknown<RoyaltyFacts>();
        }
        var facts = RoyaltyFactsRules.PawnRoyaltyFacts(pawns, catalog.Psycast);
        return Known(catalog.WithTitleDefs(facts).WithColony(catalog.WithNeuroformerDefs(colony), colony));
    }
}
