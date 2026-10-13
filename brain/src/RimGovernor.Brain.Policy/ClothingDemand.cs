namespace RimGovernor.Brain.Policy;

// Go: clothing_demand.go

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

    public IReadOnlyDictionary<Resource, EquatableArray<string>> Categories { get; init; } = new Dictionary<Resource, EquatableArray<string>>();

    public Fact<GearObservation> Gear { get; init; }

    public required StockReader Stock { get; init; }
}

/// <summary>
/// The material the worn garments about to wear out take to replace: <see cref="Needs"/> the stock levels
/// MaintainResource must reach, and <see cref="Serves"/> each stuff that may serve a demanded stuff (the
/// loadout stuff and the stuffs sharing a catalog category with it, gearFilter's rule) mapped to the cheapest
/// member the demand names.
/// </summary>
public sealed record ClothingRunway
{
    public IReadOnlyDictionary<Resource, long> Needs { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyDictionary<Resource, Resource> Serves { get; init; } = new Dictionary<Resource, Resource>();
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

public static partial class PolicyFunctions
{
    /// <summary>How far ahead a wearing garment's replacement is wanted: replacement is gradual, so a field that delivers within it (cotton grows in about a week) serves the demand as well as a hunt.</summary>
    public const double ClothingHorizonDays = 15.0;

    /// <summary>PlanClothingRunway of the review's facts.</summary>
    public static ClothingRunway ClothingRunway(this RoundsFacts f) =>
        PlanClothingRunway(
            new ClothingDemandInput
            {
                Garments = f.Garments,
                Categories = f.Items.StuffCategories,
                Gear = f.Gear,
                Stock = new StockReader { Resources = f.Resources, Wood = f.Wood },
            }
        );

    /// <summary>
    /// A worn option at or under the tattered threshold now, or projected to cross it within
    /// <paramref name="days"/>: it loses WearPerDay hit points a day of its MaxHitPoints. An option without a
    /// wear rate or hit points does not wear.
    /// </summary>
    public static bool TatteredWithin(this GearOption o, double days)
    {
        if (o.Condition <= GearTatteredCondition)
        {
            return true;
        }

        if (!(o.MaxHitPoints > 0) || !(o.WearPerDay > 0))
        {
            return false;
        }

        return (o.Condition - GearTatteredCondition) * o.MaxHitPoints / o.WearPerDay <= days;
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
        var needs = new Dictionary<Resource, long>();
        var serves = new Dictionary<Resource, Resource>();
        foreach (var (resource, n) in OpenBillDemand(bills, input.Stock))
        {
            needs[resource] = n;
            serves[resource] = resource;
        }

        foreach (var bill in bills)
        {
            foreach (var pick in bill.Picks())
            {
                if (!needs.ContainsKey(pick.Resource))
                {
                    continue;
                }

                foreach (var m in bill.Filter.Items)
                {
                    var member = new Resource(m);
                    if (!serves.ContainsKey(member) && SharesCategory(input.Categories, member, pick.Resource))
                    {
                        serves[member] = pick.Resource;
                    }
                }
            }
        }

        return new ClothingRunway { Needs = needs, Serves = serves };
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
        if (gear.Stored.TryGetValue(out var stored))
        {
            foreach (var s in stored.Items)
            {
                if (s.Serviceable())
                {
                    spare[s.Definition] = spare.GetValueOrDefault(s.Definition) + s.Count;
                }
            }
        }

        foreach (var pawn in gear.Pawns.Items)
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

                if (!GearFilter(g.Slots, worn.Stuff, input.Categories, out var filter))
                {
                    continue;
                }

                bills.Add(
                    new OpenBill
                    {
                        Count = 1,
                        Slots = Known(g.Slots),
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
            if (spare.GetValueOrDefault(c.Definition) > 0 && g.Groups.Items.Any(group => GearCoreGroups.Contains(group) && c.Groups.Items.Contains(group)))
            {
                spare[c.Definition]--;
                return c.Definition;
            }
        }

        return null;
    }

    /// <summary>Two stuffs that are the same or share a catalog category.</summary>
    private static bool SharesCategory(IReadOnlyDictionary<Resource, EquatableArray<string>> categories, Resource a, Resource b)
    {
        if (a == b)
        {
            return true;
        }

        return categories.TryGetValue(a, out var ca) && categories.TryGetValue(b, out var cb) && ca.Items.Any(c => cb.Items.Contains(c));
    }

    /// <summary>
    /// The census row as the acquisition candidate of <paramref name="resource"/> prices it, null when it does
    /// not serve it. A row serves a resource it names or one serves (ClothingRunway.Serves) maps it to; a hunt
    /// the stuffs its animal's butchery yields (a deer's leather), at the leather's units: the hunt is one
    /// candidate whatever else it yields, its labor charged once.
    /// </summary>
    public static AcquisitionSource? ResourceSourceFor(AcquisitionSource s, Resource resource, IReadOnlyDictionary<Resource, Resource> serves)
    {
        if (new Resource(s.Resource) == resource)
        {
            return s;
        }

        if (s.Hunt)
        {
            var units = 0.0;
            foreach (var p in s.Products.Items)
            {
                if (serves.TryGetValue(p.Def, out var served) && served == resource)
                {
                    units += p.Amount;
                }
            }

            return units > 0 ? s with { Resource = resource.Value, Yield = units } : null;
        }

        if (serves.TryGetValue(new Resource(s.Resource), out var target) && target == resource)
        {
            return s with { Resource = resource.Value };
        }

        return null;
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
            ID = id,
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
}
