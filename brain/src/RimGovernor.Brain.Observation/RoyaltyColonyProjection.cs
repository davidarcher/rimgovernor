namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>Projects a validated Royalty section; an absent or unavailable one is an unknown fact.</summary>
    internal static Fact<RoyaltyColony> ColonyRoyalty(O.RoyaltySection? section)
    {
        var f = section?.Observed;
        return f is null ? default : Known(DecodeRoyaltyColony(f));
    }
}

public sealed partial record ColonyProjection
{
    /// <summary>The royalty read <paramref name="f"/> completed with the colony section's neuroformers, ceremonies and thrones. Without the section the royalty fact is unknown: planning on a stock, ceremony or throne list that was not read would act on an empty one.</summary>
    public Fact<RoyaltyFacts> WithRoyaltyColony(RoyaltyFacts f) =>
        RoyaltyColony.TryGetValue(out var colony) ? Known(f.WithColony(colony)) : Unknown<RoyaltyFacts>();

    /// <summary>
    /// The royalty fact: the colonists' own holdings and psycasts from their pawn rows, the ladder and permits
    /// from the def mirror and the colony section's neuroformers, ceremonies and thrones. The colony section's
    /// presence is the Royalty-applicable gate, as for the other DLC sections: without it the fact is unknown
    /// and nothing is read. A pawn row or def the read cannot use throws and the fact stays unknown.
    /// </summary>
    public Fact<RoyaltyFacts> RoyaltyOf(O.PawnSnapshot? pawns, DefinitionCatalog catalog)
    {
        if (!RoyaltyColony.TryGetValue(out var colony))
        {
            return Unknown<RoyaltyFacts>();
        }
        colony = catalog.WithNeuroformerDefs(colony);
        var projection = this with { RoyaltyColony = Known(colony) };
        var facts = PawnRoyaltyFacts(pawns, catalog);
        var mirrored = catalog.WithTitleDefs(facts);
        return projection.WithRoyaltyColony(mirrored);
    }
}
