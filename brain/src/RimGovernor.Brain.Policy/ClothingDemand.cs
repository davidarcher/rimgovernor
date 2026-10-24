namespace RimGovernor.Brain.Policy;


/// <summary>
/// A garment the apparel policy lets the colonists wear that covers a core body-part group (GearCoreGroups):
/// its groups and the native recipe's ingredient slots, each the stuff alternatives with their counts.
/// </summary>
public sealed record ClothingGarment
{
    public Resource Definition { get; init; }

    public EquatableArray<string> Groups { get; init; }

    public EquatableArray<EquatableArray<Amount>> Slots { get; init; }
}

/// <summary>
/// What the clothing runway is computed from: the allowed garments, each stuff's categories, the gear census
/// (worn garments from the loadout models, the garments in storage) and the stock.
/// </summary>
public sealed record ClothingDemandInput
{
    public EquatableArray<ClothingGarment> Garments { get; init; }

    /// <summary>The catalog's market values the bills' filters read (GearFilter).</summary>
    public ItemFacts Items { get; init; } = new();

    public Fact<GearObservation> Gear { get; init; }

    public required StockReader Stock { get; init; }

    /// <summary>Bills the player's worn-count goals owe (#3496), priced alongside the replacements.</summary>
    public IReadOnlyList<OpenBill> GoalBills { get; init; } = [];

    /// <summary>The stuff tier 2 chose for each pawn's slot (<see cref="RoundsFacts.ClothingStuff"/>); a slot it names is replaced in that stuff.</summary>
    public IReadOnlyDictionary<(PawnId Pawn, GearSlot Slot), Resource> Chosen { get; init; } = new Dictionary<(PawnId Pawn, GearSlot Slot), Resource>();
}

/// <summary>
/// The material the worn garments about to wear out take to replace: <see cref="Needs"/> the stock levels
/// the economy/resource Need must reach. A replacement's filter admits only the stuff tier 2 chose for the
/// slot, else the worn one (GearFilter), so a demanded stuff is served by that stuff alone.
/// </summary>
public sealed record ClothingRunway
{
    public IReadOnlyDictionary<Resource, long> Needs { get; init; } = new Dictionary<Resource, long>();
}

/// <summary>
/// A field of a harvested stuff to sow: the cells it covers, the days to its first harvest, the units a cell
/// yields, the work to create the zone and sow it, and the daily harvest work.
/// </summary>
public sealed record FieldHarvest
{
    public long Cells { get; init; }

    public double GrowDays { get; init; }

    public double UnitsPerCell { get; init; }

    public double SetupTicks { get; init; }

    public double WorkPerDay { get; init; }
}

/// <summary>
/// When a stuff is in hand (#2467), in days from now: the stock covers the units at once; otherwise the supply candidates
/// that yield the stuff (a growing field at its harvest lead, a huntable animal's butchery at the hunt's lead, a stored
/// corpse's butchery at its butchering work) are taken in lead order and the stuff arrives with the candidate whose yield,
/// added to the stock, covers the units. Unknown when no known source covers them: a stuff only a recipe chain makes is
/// valued in tier 2, not here.
/// </summary>
public sealed class StuffArrival(IReadOnlyDictionary<Resource, long> stock, IReadOnlyList<SupplyCandidate> supply)
{
    /// <summary>The day <paramref name="units"/> (at least one) of <paramref name="stuff"/> are in hand, or unknown.</summary>
    public Fact<double> Of(Resource stuff, long units)
    {
        var want = Math.Max(1, units);
        double have = stock.GetValueOrDefault(stuff);
        if (have >= want)
        {
            return Known(0.0);
        }
        var sources = new List<(double Lead, long Units)>();
        foreach (var c in supply)
        {
            var yielded = c.Yields.Where(y => y.Good.Def == stuff && y.StockCap.IsKnown).Sum(y => y.StockCap.Value);
            if (yielded > 0 && c.LeadDays.TryGetValue(out var lead))
            {
                sources.Add((Math.Max(0, lead), yielded));
            }
        }
        foreach (var (lead, yielded) in sources.OrderBy(s => s.Lead))
        {
            have += yielded;
            if (have >= want)
            {
                return Known(lead);
            }
        }
        return Unknown<double>();
    }

    /// <summary>
    /// The arrival of a gear option's stuff: now for a worn, loose or stored item and for a def made from no stuff;
    /// for a bill, the arrival of the units of its stuff its ingredients take.
    /// </summary>
    public Fact<double> Of(GearOption o) =>
        o.Source != GearSource.Bill || o.Stuff.IsEmpty ? Known(0.0) : Of(o.Stuff, o.Ingredients.Where(a => a.Resource == o.Stuff).Sum(a => a.Count));
}

public static class ClothingDemandRules
{
    /// <summary>How far ahead a wearing garment's replacement is wanted: replacement is gradual, so a field that delivers within it (cotton grows in about a week) serves the demand as well as a hunt.</summary>
    public const double ClothingHorizonDays = 15.0;

    /// <summary>PlanClothingRunway of the review's facts.</summary>
    public static ClothingRunway ClothingRunway(this RoundsFacts f) =>
        PlanClothingRunway(
            new ClothingDemandInput
            {
                Garments = f.Garments,
                Items = f.Items,
                Gear = f.Gear,
                Stock = new StockReader { Resources = f.Resources, Wood = f.Wood },
                GoalBills = f.WornBills,
                Chosen = f.ClothingStuff,
            }
        );

    /// <summary>
    /// A worn option at or under the tattered threshold now, or projected to cross it within
    /// <paramref name="days"/>: it loses WearPerDay hit points a day of its MaxHitPoints. An option without a
    /// wear rate or hit points does not wear.
    /// </summary>
    public static bool TatteredWithin(this GearOption o, double days)
    {
        if (o.Condition <= GearRules.GearTatteredCondition)
        {
            return true;
        }

        if (!(o.MaxHitPoints > 0) || !(o.WearPerDay > 0))
        {
            return false;
        }

        return (o.Condition - GearRules.GearTatteredCondition) * o.MaxHitPoints / o.WearPerDay <= days;
    }

    /// <summary>
    /// The replacement demand of every worn garment that is tattered or projected to be within
    /// ClothingHorizonDays: one garment's recipe slots at the cheapest member of its filter (OpenBillDemand's
    /// rule), less the serviceable stored garments covering the same core group. A garment without a recipe, a
    /// pawn without a loadout model or an unread census asks for nothing. The demand is an absolute stock
    /// level, counted only where the stock falls short.
    /// </summary>
    public static ClothingRunway PlanClothingRunway(ClothingDemandInput input)
    {
        var (bills, _) = ClothingReplacements(input);
        bills.AddRange(input.GoalBills);
        var needs = new Dictionary<Resource, long>();
        foreach (var (resource, n) in BillDemandRules.OpenBillDemand(bills, input.Stock))
        {
            needs[resource] = n;
        }

        return new ClothingRunway { Needs = needs };
    }

    /// <summary>
    /// Walks the worn garments about to wear out: the bills for those no serviceable stored garment covers,
    /// and per definition the serviceable stored garments that cover the rest (the spare reserve a sale must
    /// leave in storage).
    /// </summary>
    private static (List<OpenBill> Bills, Dictionary<Resource, int> Reserve) ClothingReplacements(ClothingDemandInput input)
    {
        var bills = new List<OpenBill>();
        var reserve = new Dictionary<Resource, int>();
        if (!input.Gear.TryGetValue(out var gear))
        {
            return (bills, reserve);
        }

        var spare = new Dictionary<Resource, int>();
        foreach (var s in gear.Stored)
        {
            if (s.Serviceable())
            {
                spare[s.Definition] = spare.GetValueOrDefault(s.Definition) + s.Count;
            }
        }

        foreach (var pawn in gear.Pawns)
        {
            if (!pawn.LoadoutModel.TryGetValue(out var model))
            {
                continue;
            }

            foreach (var worn in model.Worn.Items)
            {
                if (!worn.TatteredWithin(ClothingHorizonDays))
                {
                    continue;
                }

                var g = GarmentOf(input.Garments.Items, worn.Definition);
                if (g is null)
                {
                    continue;
                }

                if (SpareGarment(input.Garments.Items, g, spare) is { } taken)
                {
                    reserve[taken] = reserve.GetValueOrDefault(taken) + 1;
                    continue;
                }

                // The stuff tier 2 chose for the slot, while the worn garment's recipe takes it; the worn stuff otherwise.
                var filter = (input.Chosen.TryGetValue((pawn.Pawn, worn.Slot), out var chosen) ? GearRules.GearFilter(g.Slots, chosen, input.Items) : null)
                    ?? GearRules.GearFilter(g.Slots, worn.Stuff, input.Items);
                if (filter is null)
                {
                    continue;
                }

                bills.Add(
                    new OpenBill
                    {
                        Count = 1,
                        Slots = g.Slots,
                        Filter = EquatableArray.From(filter.Select(r => r.Value)),
                    }
                );
            }
        }

        return (bills, reserve);
    }

    /// <summary>
    /// Per definition, the serviceable stored garments PlanClothingRunway counts as the replacement of a worn
    /// garment about to wear out: spare the colony keeps for the runway and never sells.
    /// </summary>
    public static Dictionary<Resource, int> ClothingSpareReserve(ClothingDemandInput input) => ClothingReplacements(input).Reserve;

    /// <summary>The allowed garment of <paramref name="definition"/>.</summary>
    private static ClothingGarment? GarmentOf(IEnumerable<ClothingGarment> garments, Resource definition) =>
        garments.FirstOrDefault(g => g.Definition == definition);

    /// <summary>
    /// Consumes one serviceable stored garment covering a core group <paramref name="g"/> covers, returning
    /// its definition, or null if none was left.
    /// </summary>
    private static Resource? SpareGarment(IEnumerable<ClothingGarment> garments, ClothingGarment g, Dictionary<Resource, int> spare)
    {
        foreach (var c in garments)
        {
            if (spare.GetValueOrDefault(c.Definition) > 0 && g.Groups.Items.Any(group => GearRules.GearCoreGroups.Contains(group) && c.Groups.Items.Contains(group)))
            {
                spare[c.Definition]--;
                return c.Definition;
            }
        }

        return null;
    }

    /// <summary>
    /// The census row as the acquisition candidate of <paramref name="resource"/> prices it, null when it does
    /// not serve it. A row serves a resource it names; a hunt serves a clothing-demanded stuff its animal's
    /// butchery yields (a deer's leather), at the leather's units: the hunt is one candidate whatever else it
    /// yields, its labor charged once.
    /// </summary>
    public static AcquisitionSource? ResourceSourceFor(AcquisitionSource s, Resource resource, IReadOnlySet<Resource> clothing)
    {
        if (new Resource(s.Resource) == resource)
        {
            return s;
        }

        if (!s.Hunt || !clothing.Contains(resource))
        {
            return null;
        }

        var units = s.Products.Items.Where(p => p.Def == resource).Sum(p => p.Amount);
        return units > 0 ? s with { Resource = resource.Value, Yield = units } : null;
    }

    /// <summary>
    /// A field (cotton) as a harvest candidate: the zone and sowing are its upfront labor, the first harvest
    /// its lead, the cells' yield over the grow time its rate and the standing crop its stock cap.
    /// </summary>
    public static SupplyCandidate? FieldHarvestCandidate(Resource resource, string id, FieldHarvest f)
    {
        if (f.Cells <= 0 || !(f.GrowDays > 0) || !(f.UnitsPerCell > 0))
        {
            return null;
        }

        var units = f.Cells * f.UnitsPerCell;
        return new SupplyCandidate
        {
            Kind = CandidateKind.Harvest,
            Id = id,
            State = Known(CandidateState.Closed),
            Yields = EquatableArray.From(
                [
                    new CandidateYield
                    {
                        Good = new ResourceKey { Def = resource },
                        PerDay = Known(units / f.GrowDays),
                        StockCap = Known((long)units),
                    },
                ]
            ),
            LeadDays = Known(f.GrowDays),
            LaborPerDay = Known(f.WorkPerDay),
            UpfrontCost = new CandidateCost { LaborTicks = Known(f.SetupTicks) },
        };
    }

    /// <summary>
    /// A growing field as the arrival of its harvest (<see cref="StuffArrival"/>): the standing crop's
    /// <paramref name="units"/> of <paramref name="resource"/> at its harvest lead. Null without a known lead or yield.
    /// </summary>
    public static SupplyCandidate? GrowingFieldCandidate(Resource resource, string id, double units, Fact<double> lead)
    {
        if (!(units >= 1) || !lead.IsKnown)
        {
            return null;
        }
        return new SupplyCandidate
        {
            Kind = CandidateKind.Harvest,
            Id = id,
            State = Known(CandidateState.Delivering),
            Yields = [new CandidateYield { Good = new ResourceKey { Def = resource }, StockCap = Known((long)units) }],
            LeadDays = lead,
        };
    }

    /// <summary>
    /// A stored animal corpse as the arrival of what its race's butchery yields besides meat (its leather, #3622), at the
    /// butchering's <paramref name="leadDays"/>. Null for a corpse whose race yields nothing.
    /// </summary>
    public static SupplyCandidate? CorpseYieldCandidate(string id, EquatableArray<SourceProduct> butchery, double leadDays)
    {
        var yields = butchery.Where(p => p.Amount >= 1)
            .Select(p => new CandidateYield { Good = new ResourceKey { Def = p.Def }, StockCap = Known((long)p.Amount) })
            .ToArray();
        return yields.Length > 0 ? CandidateRules.SourceCandidate(CandidateKind.Corpse, id, Unknown<double>(), Unknown<double>(), true, 0, yields) with { LeadDays = Known(leadDays) } : null;
    }

    /// <summary>
    /// A huntable animal as the arrival of what its butchery yields besides meat (its leather), at the hunt channel's lead
    /// (none: <see cref="HuntPlanningRules.HuntRosterChannel"/>). Null for a row that is no hunt or yields nothing.
    /// </summary>
    public static SupplyCandidate? HuntYieldCandidate(AcquisitionSource s)
    {
        var yields = s.Products.Where(p => p.Amount >= 1)
            .Select(p => new CandidateYield { Good = new ResourceKey { Def = p.Def }, StockCap = Known((long)p.Amount) })
            .ToArray();
        return s.Hunt && yields.Length > 0
            ? CandidateRules.SourceCandidate(CandidateKind.Hunt, s.Id, Unknown<double>(), Unknown<double>(), true, 0, yields)
            : null;
    }
}
