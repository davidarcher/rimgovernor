namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>Projects a validated Royalty section; an absent or unavailable one is an unknown fact.</summary>
    internal static Fact<RoyaltyColony> ColonyRoyalty(O.RoyaltySection? section)
    {
        var f = section?.Outcome?.Observed;
        return f is null ? default : Known(DecodeRoyaltyColony(f));
    }

    /// <summary>
    /// Go's <c>DecodeRoyaltyColony</c>: validates the Royalty colony section's facts (the neuroformer stock, pending
    /// bestowing ceremonies and standing thrones) and projects them; a malformed row breaks the contract.
    /// </summary>
    internal static RoyaltyColony DecodeRoyaltyColony(O.RoyaltyColonyFacts? v)
    {
        if (v is null)
        {
            throw new BridgeContractException("incomplete royalty colony facts");
        }
        var neuroformers = new Dictionary<string, Neuroformer>(StringComparer.Ordinal);
        foreach (var row in v.Neuroformers)
        {
            var name = (row.DefName ?? "");
            if (!BridgeFunctions.IsValidId(name) || row.Held.GetValueOrDefault() < 0)
            {
                throw new BridgeContractException("invalid royalty neuroformer");
            }
            if (!neuroformers.TryAdd(name, new Neuroformer { Def = name, Held = Optional(row.Held is not null, (int)row.Held.GetValueOrDefault()), Craftable = Optional(row.Craftable is not null, row.Craftable.GetValueOrDefault()) }))
            {
                throw new BridgeContractException($"duplicate royalty neuroformer {name}");
            }
        }
        var ceremonies = new List<BestowingCeremony>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Ceremonies)
        {
            var pawn = row.Pawn?.Id ?? "";
            var bestower = row.Bestower?.Id ?? "";
            var faction = row.FactionDef is not null ? (row.FactionDef ?? "") : "";
            if (!BridgeFunctions.IsValidId(pawn) || (bestower != "" && !BridgeFunctions.IsValidId(bestower)) || !BridgeFunctions.IsValidId((row.Quest ?? ""))
                || (row.FactionDef is not null && !BridgeFunctions.IsValidId((row.FactionDef ?? ""))) || (row.Title is not null && !BridgeFunctions.IsValidId((row.Title ?? ""))) || !seen.Add(pawn + "/" + faction))
            {
                throw new BridgeContractException("invalid royalty ceremony");
            }
            var attendees = new List<PawnId>(row.Attendees.Count);
            foreach (var attendee in row.Attendees)
            {
                if (!BridgeFunctions.IsValidId((attendee.Id ?? "")))
                {
                    throw new BridgeContractException("invalid royalty ceremony attendee");
                }
                attendees.Add(new PawnId((attendee.Id ?? "")));
            }
            ceremonies.Add(new BestowingCeremony
            {
                Quest = new QuestId((row.Quest ?? "")),
                Pawn = new PawnId(pawn),
                Bestower = new PawnId(bestower),
                Faction = faction,
                Title = row.Title is not null ? (row.Title ?? "") : "",
                Accepted = Optional(row.Accepted is not null, row.Accepted.GetValueOrDefault()),
                BestowerWaiting = Optional(row.BestowerWaiting is not null, row.BestowerWaiting.GetValueOrDefault()),
                Started = Optional(row.Started is not null, row.Started.GetValueOrDefault()),
                Spot = row.Spot is null ? default : Known(CellOf(row.Spot)),
                Attendees = EquatableArray.From(attendees),
            });
        }
        var thrones = new List<RoyalThrone>();
        var throneIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Thrones)
        {
            var id = row.Thing?.Id ?? "";
            var owner = row.Owner?.Id ?? "";
            if (!BridgeFunctions.IsValidId(id) || (owner != "" && !BridgeFunctions.IsValidId(owner)) || (row.DefName is not null && !BridgeFunctions.IsValidId((row.DefName ?? ""))) || !throneIds.Add(id))
            {
                throw new BridgeContractException("invalid royalty throne");
            }
            thrones.Add(new RoyalThrone { Id = id, Def = row.DefName is not null ? (row.DefName ?? "") : "", Owner = new PawnId(owner) });
        }
        return new RoyaltyColony { Neuroformers = neuroformers, Ceremonies = EquatableArray.From(ceremonies), Thrones = EquatableArray.From(thrones) };
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
        var facts = PawnRoyaltyFacts(pawns, catalog.Psycast);
        var mirrored = catalog.WithTitleDefs(facts);
        return projection.WithRoyaltyColony(mirrored);
    }
}
