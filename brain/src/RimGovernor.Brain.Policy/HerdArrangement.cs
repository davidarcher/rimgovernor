namespace RimGovernor.Brain.Policy;

// Herd arrangements (RG-HABITAT, task 6.2 of redefine-habitat-requirements). A pen unit (HerdShelterRules.UnitOf: the
// herd's race, "" for the misc unit) with its planned pens and its penned animals is one arrangement, and it carries
// three deficits, each read off observed service, never off what stands placed:
// - feeding: the nutrition the animals inside the unit's enclosed pens eat over the horizon (the harvest gap, at least a
//   day) less the pasture those pens grow over it (today's when no gap is coming, else the worse of today's and the
//   worst quadrum's) and the feed stockpiled inside them. A marker and a complete fence over bare ground leave it open.
// - shelter: the animals the seasonal outdoor climate would kill (HerdShelterRules.DaysToDeath) that no roofed, walled
//   room the pen's enclosure reaches holds in their comfort band now, with the barn's floor per animal. A barn standing
//   apart from the enclosure (behind a door the roamers cannot pass) is no shelter.
// - containment: the unit's pen-bound animals outside an enclosed pen, plus one while the unit has no planned pen, no
//   observed marker in it, or the marker reads unenclosed.
// Remedies are compared, not chosen by kind. Feeding: retain (the baseline), haul the colony's pen feed lying outside
// every pen into a feed store inside the pen (a ledger in unit order, so no stock feeds two pens), then the remainder
// as the hay field demand the shared field ledger places (FieldPlacementRules.Choose); pen growth is the layout's
// (PenWants) and culling the animals/herd Need's (the herd plan's feed ratio), named here and not re-chosen. Shelter:
// HerdShelterRules.Assess's open/barn/spot alternatives, the barn compared as a layout regroup deviation by the canonical
// ledger (LayoutHerdBarnRules), and unassessed ("animal access:<unit>") while its ring door is none a roamer can open.
// Containment: retain, repairing the planned pen's ring gaps, or a new pen only when repair cannot restore the
// enclosure, each valued by the roaming loss it stops. Every deficit keeps retain as its baseline, which wins ties.
// A fact a deficit needs unread is a named state that keeps the arrangement open.

/// <summary>The three deficits of a herd arrangement.</summary>
public enum HerdDeficitKind
{
    Feeding,
    Shelter,
    Containment,
}

/// <summary>A herd arrangement's remedy kinds; Retain is every deficit's baseline.</summary>
public enum HerdRemedyKind
{
    Retain,
    HaulFeed,
    HayField,
    ExpandPen,
    Cull,
    Barn,
    Spot,
    RepairPen,
    NewPen,
}

/// <summary>A remedy of one deficit and its value (HerdShelterValue: assessed, excluded or unassessed).</summary>
public sealed record HerdRemedy(HerdDeficitKind Deficit, HerdRemedyKind Kind, HerdShelterValue Value);

/// <summary>
/// One pen unit's arrangement: the feeding deficit (nutrition over the horizon), the gross feed it eats over the horizon
/// before the stock inside its pens (FeedNeed, which sizes its feed store), the part of the deficit left for a hay field
/// once the feed ledger hauls outside stock (the haul is the HaulFeed remedy's value), the exposed animals no observed
/// shelter serves, the uncontained animals, whether the pen itself is unserved, the unread facts, every remedy's value and
/// the standing pieces that pen the unit (Pieces).
/// </summary>
public sealed record HerdArrangement
{
    public string Herd { get; init; } = "";

    public double FeedingDeficit { get; init; }

    public double FeedNeed { get; init; }

    public double HayNutrition { get; init; }

    public int ShelterDeficit { get; init; }

    public int Uncontained { get; init; }

    /// <summary>The pen fails its unit: none planned, no marker observed in it, or its marker reads unenclosed.</summary>
    public bool PenUnserved { get; init; }

    public EquatableArray<string> Unread { get; init; }

    public EquatableArray<HerdRemedy> Remedies { get; init; }

    /// <summary>
    /// The load ids of the standing pieces that pen the unit, in ordinal order: the marker and the observed boundary
    /// (PenGrazing.Boundary: fences, gates, walls, animal doors) of every pen tied to it, one whose marker stands in the
    /// unit's planned pens or whose enclosure holds one of its animals (PenGrazing.Animals).
    /// </summary>
    public EquatableArray<string> Pieces { get; init; }

    public int ContainmentDeficit => Uncontained + (PenUnserved ? 1 : 0);

    /// <summary>Open while a deficit is above zero or a fact it needs is unread.</summary>
    public bool Open => FeedingDeficit > 0 || ShelterDeficit > 0 || ContainmentDeficit > 0 || Unread.Count > 0;

    /// <summary>
    /// The finding naming the arrangement's open deficits: "herd_arrangement:&lt;unit&gt;:" then each open one with its best
    /// remedy ("feeding=hay_field"), then each unread fact, or "served".
    /// </summary>
    public string State
    {
        get
        {
            var parts = new List<string>();
            foreach (var deficit in (HerdDeficitKind[])[HerdDeficitKind.Feeding, HerdDeficitKind.Shelter, HerdDeficitKind.Containment])
            {
                if (IsOpen(deficit)) parts.Add(Wire.Name(deficit) + "=" + Wire.Name(Best(deficit)));
            }
            parts.AddRange(Unread.Select(u => "unread:" + u));
            return "herd_arrangement:" + Herd + ":" + (parts.Count == 0 ? "served" : string.Join(",", parts));
        }
    }

    /// <summary>Whether <paramref name="deficit"/> is above zero.</summary>
    public bool IsOpen(HerdDeficitKind deficit) =>
        deficit == HerdDeficitKind.Feeding ? FeedingDeficit > 0 : deficit == HerdDeficitKind.Shelter ? ShelterDeficit > 0 : ContainmentDeficit > 0;

    /// <summary>
    /// The remedy of <paramref name="deficit"/>: the assessed remedy with the largest value, the baseline (retain, valued at
    /// the zero of the loss it keeps) on a tie, so a remedy wins only by stopping more priced loss than retaining. An
    /// excluded or unassessed remedy never wins. A best barn names the remedy the layout regroup compares against its
    /// no-barn baseline (LayoutHerdBarnRules); it is not adopted here.
    /// </summary>
    public HerdRemedyKind Best(HerdDeficitKind deficit) => Remedies
        .Where(r => r.Deficit == deficit && r.Value is HerdShelterValue.Assessed)
        .Select(r => (r.Kind, Value: ((HerdShelterValue.Assessed)r.Value).GainPerDay))
        .OrderByDescending(r => r.Value).ThenBy(r => r.Kind).Select(r => r.Kind).DefaultIfEmpty(HerdRemedyKind.Retain).First();
}

/// <summary>
/// What the arrangements read: the herd shelter's facts (animals, units, pen wants, climate, thermal tables, spot), the
/// layout plan's pens, the colony ground (null unread), every marker's grazing row, the pen feed outside the pens, the
/// harvest gap (or the fact unread) and the uncontained animals.
/// </summary>
public sealed record HerdArrangementInput
{
    public HerdShelterInput Shelter { get; init; } = new();

    public LayoutPlan? Plan { get; init; }

    public GroundCensus? Ground { get; init; }

    public EquatableArray<PenGrazing> Grazing { get; init; }

    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.PenFeedStock> Feed { get; init; }

    public HarvestGap HarvestGap { get; init; } = new HarvestGap.Unread("growing season");

    public EquatableArray<PawnId> Uncontained { get; init; }
}

public static class HerdArrangementRules
{
    public const string ReasonRetain = "retain_baseline";
    public const string ReasonHaulFeed = "haul_pen_feed";
    public const string ReasonNoPenFeed = "no_pen_feed_outside_pens";
    public const string ReasonHayField = "hay_field_demand";
    public const string ReasonFeedCovered = "feed_covered";
    public const string ReasonPenGrowthOwed = "layout_pen_growth_owed";
    public const string ReasonPenAtPastureSize = "pen_at_pasture_size";
    public const string ReasonCullHerdFamily = "cull_derived_by_animals_herd";
    public const string ReasonRepairGaps = "repair_ring_gaps";
    public const string ReasonNoRingGap = "no_ring_gap";
    public const string ReasonNoPlannedPen = "no_planned_pen";
    public const string ReasonNewPen = "new_pen_restores_enclosure";
    public const string ReasonRepairRestores = "repair_restores_enclosure";
    public const string ReasonNoPenWant = "no_pen_want";

    /// <summary>Every pen unit's arrangement in unit key order, the pen feed outside the pens allocated across them in that order.</summary>
    public static IReadOnlyList<HerdArrangement> Assess(HerdArrangementInput input)
    {
        var shelters = HerdShelterRules.Assess(input.Shelter).ToDictionary(s => s.Herd, StringComparer.Ordinal);
        var units = input.Shelter.Animals.Where(a => a.RequiresPen).Select(a => HerdShelterRules.UnitOf(a, input.Shelter.Herds))
            .Concat(input.Plan?.RoomsOf(HerdRoomsRules.PlannedPen).Select(r => r.Herd) ?? [])
            .Distinct().Order(StringComparer.Ordinal);
        var stock = input.Feed.Sum(f => f.Nutrition);
        var uncontained = input.Uncontained.ToHashSet();
        var output = new List<HerdArrangement>();
        foreach (var herd in units)
        {
            var animals = HerdShelterRules.UnitAnimals(input.Shelter, herd);
            var pens = input.Plan?.HerdRooms(HerdRoomsRules.PlannedPen, herd) ?? [];
            var rows = input.Grazing.Where(g => pens.Any(p => p.Region.Contains(g.Cell))).OrderBy(g => g.Marker, StringComparer.Ordinal).ToList();
            var ids = animals.Select(a => a.Id.Value).ToHashSet(StringComparer.Ordinal);
            var pieces = input.Grazing.Where(g => rows.Contains(g) || g.Animals.Any(ids.Contains))
                .SelectMany(g => g.Boundary.Prepend(g.Marker)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var enclosed = rows.Where(r => r.Enclosed).ToList();
            var unread = new List<string>();
            var remedies = new List<HerdRemedy>();

            // Containment: the unit's animals outside an enclosed pen, and the pen itself.
            var outside = animals.Count(a => uncontained.Contains(a.Id));
            var penUnserved = animals.Count > 0 && enclosed.Count == 0;
            remedies.AddRange(ContainmentRemedies(input, herd, pens, penUnserved ? animals : [.. animals.Where(a => uncontained.Contains(a.Id))], penUnserved));

            // Feeding, over the enclosed pens' observed rows.
            var (deficit, gross) = (0.0, 0.0);
            if (animals.Count > 0 && enclosed.Count > 0)
            {
                if (input.HarvestGap is not HarvestGap.Read { Days: var gap } || !double.IsFinite(gap) || gap < 0)
                {
                    unread.Add(input.HarvestGap is HarvestGap.Unread { Fact: var fact } ? fact : "growing season");
                }
                else
                {
                    var demand = enclosed.Sum(r => r.DemandPerDay);
                    var today = enclosed.Sum(r => r.PastureTodayPerDay);
                    var worst = enclosed.Sum(r => r.PasturePerDay);
                    var stored = enclosed.Sum(r => r.StoredNutrition);
                    (deficit, gross) = Feeding(demand, today, worst, stored, gap);
                }
            }
            else if (animals.Count > 0 && rows.Count > 0)
            {
                // The marker stands unenclosed: what the animals would graze is unobservable until it encloses.
                unread.Add("pen nutrition:" + herd);
            }
            var haul = Math.Min(deficit, stock);
            stock -= haul;
            var hay = deficit - haul;
            remedies.Add(new(HerdDeficitKind.Feeding, HerdRemedyKind.Retain, new HerdShelterValue.Assessed(0, ReasonRetain)));
            remedies.Add(new(HerdDeficitKind.Feeding, HerdRemedyKind.HaulFeed, haul > 0 ? new HerdShelterValue.Assessed(haul, ReasonHaulFeed)
                : new HerdShelterValue.Excluded(deficit > 0 ? ReasonNoPenFeed : ReasonFeedCovered)));
            remedies.Add(new(HerdDeficitKind.Feeding, HerdRemedyKind.HayField, hay > 0 ? new HerdShelterValue.Assessed(hay, ReasonHayField)
                : new HerdShelterValue.Excluded(ReasonFeedCovered)));
            var want = input.Shelter.Pens.FirstOrDefault(p => p.Race == herd);
            // The layout grows a pen to its want (HerdRoomNodes); the observed enclosure says whether it has.
            var observed = enclosed.Sum(r => r.Cells);
            remedies.Add(new(HerdDeficitKind.Feeding, HerdRemedyKind.ExpandPen, new HerdShelterValue.Excluded(want is not null && want.Cells > observed ? ReasonPenGrowthOwed : ReasonPenAtPastureSize)));
            remedies.Add(new(HerdDeficitKind.Feeding, HerdRemedyKind.Cull, new HerdShelterValue.Excluded(ReasonCullHerdFamily)));

            // Shelter.
            var shelterDeficit = 0;
            if (animals.Count > 0)
            {
                try
                {
                    shelterDeficit = ShelterDeficit(input.Shelter, animals, enclosed);
                }
                catch (RequiredFactException e)
                {
                    unread.Add(e.Fact);
                }
            }
            if (shelters.TryGetValue(herd, out var shelter))
            {
                foreach (var a in shelter.Alternatives)
                {
                    remedies.Add(new(HerdDeficitKind.Shelter, a.Kind == HerdShelterKind.Barn ? HerdRemedyKind.Barn : a.Kind == HerdShelterKind.Bed ? HerdRemedyKind.Spot : HerdRemedyKind.Retain, a.Value));
                }
            }
            output.Add(new HerdArrangement
            {
                Herd = herd,
                FeedingDeficit = deficit,
                FeedNeed = gross,
                HayNutrition = hay,
                ShelterDeficit = shelterDeficit,
                Uncontained = outside,
                PenUnserved = penUnserved,
                Unread = EquatableArray.From(unread),
                Remedies = EquatableArray.From(remedies),
                Pieces = EquatableArray.From(pieces),
            });
        }
        return output;
    }

    /// <summary>
    /// The feeding deficit and the gross feed over the horizon (the harvest gap, at least a day): consumption less the
    /// pasture of the horizon (today's with no gap coming, else the worse of today's and the worst quadrum's), and the
    /// deficit less the feed stored in the pens too.
    /// </summary>
    internal static (double Deficit, double Gross) Feeding(double demand, double today, double worst, double stored, double gap)
    {
        var horizon = Math.Max(gap, 1);
        var pasture = gap > 0 ? Math.Min(today, worst) : today;
        var gross = Math.Max(0, horizon * (demand - pasture));
        return (Math.Max(0, gross - stored), gross);
    }

    /// <summary>
    /// The unit's animals the outdoor climate kills (DaysToDeath over the seasonal curve) that no shelter of an enclosed pen
    /// holds: a room in the animal's comfort band now, its floor shared at the barn's cells per animal. Throws a
    /// RequiredFactException naming the climate, the thermal tables or a species' comfort range when unread.
    /// </summary>
    internal static int ShelterDeficit(HerdShelterInput input, IReadOnlyList<UpkeepAnimal> animals, IReadOnlyList<PenGrazing> enclosed)
    {
        var climate = HerdShelterRules.Climate(input);
        HerdShelterRules.RequireThermalTables(input.Penalties);
        var exposed = new List<(double Min, double Max)>();
        foreach (var animal in animals)
        {
            (double Min, double Max) band;
            try
            {
                band = HerdShelterRules.ComfortOf(animal);
            }
            catch (RequiredFactException)
            {
                throw new RequiredFactException("species comfort:" + animal.Definition.Value);
            }
            if (HerdShelterRules.DaysToDeath(climate, t => climate.Curve[t], band.Min, band.Max, input.Penalties) is not null)
            {
                exposed.Add(band);
            }
        }
        var rooms = enclosed.SelectMany(r => r.Shelters).DistinctBy(s => s.Room).OrderBy(s => s.Room).ToList();
        var spare = rooms.ToDictionary(r => r.Room, r => r.Cells);
        var unserved = 0;
        foreach (var (min, max) in exposed)
        {
            var room = rooms.FirstOrDefault(r => r.TemperatureC >= min && r.TemperatureC <= max && spare[r.Room] >= HerdShelterRules.BarnCellsPerAnimal);
            if (room is null)
            {
                unserved++;
                continue;
            }
            spare[room.Room] -= HerdShelterRules.BarnCellsPerAnimal;
        }
        return unserved;
    }

    /// <summary>
    /// The containment remedies, each valued by the roaming loss it stops: retain (the baseline, which stops none and keeps
    /// the loss); repair the planned pens' ring cells the ground stands no fence, wall or gate on, which restores the
    /// enclosure; or, only when repair cannot (no planned pen, or no ring gap left to close while the pen reads
    /// unenclosed), a new pen at the unit's pen want. The loss is each at-risk animal (every animal of an unserved pen, else
    /// those outside an enclosed one) at its market value over its mean time to leave the map, as the pen credit prices a
    /// roamer (HerdShelterRules.LossPerDay); an animal whose roam row is unpriced leaves the remedy unassessed ("animal
    /// value:&lt;def&gt;"), so the baseline holds.
    /// </summary>
    private static List<HerdRemedy> ContainmentRemedies(HerdArrangementInput input, string herd, IReadOnlyList<PlannedRoom> pens, IReadOnlyList<UpkeepAnimal> atRisk, bool penUnserved)
    {
        HerdShelterValue Stops(string reason) => RoamingLoss(atRisk) is { } loss ? new HerdShelterValue.Assessed(loss, reason) : new HerdShelterValue.Unassessed(UnpricedAnimal(atRisk));
        var output = new List<HerdRemedy> { new(HerdDeficitKind.Containment, HerdRemedyKind.Retain, new HerdShelterValue.Assessed(0, ReasonRetain)) };
        HerdShelterValue repair;
        if (pens.Count == 0)
        {
            repair = new HerdShelterValue.Excluded(ReasonNoPlannedPen);
        }
        else if (input.Ground is not { } ground)
        {
            repair = new HerdShelterValue.Unassessed("construction census");
        }
        else
        {
            repair = pens.Sum(p => RingGaps(input.Plan!, p, ground)) > 0 ? Stops(ReasonRepairGaps) : new HerdShelterValue.Excluded(ReasonNoRingGap);
        }
        output.Add(new(HerdDeficitKind.Containment, HerdRemedyKind.RepairPen, repair));
        output.Add(new(HerdDeficitKind.Containment, HerdRemedyKind.NewPen,
            !input.Shelter.Pens.Any(p => p.Race == herd) ? new HerdShelterValue.Excluded(ReasonNoPenWant)
            : repair is not HerdShelterValue.Excluded || !penUnserved ? new HerdShelterValue.Excluded(ReasonRepairRestores)
            : Stops(ReasonNewPen)));
        return output;
    }

    /// <summary>The roaming loss a day of <paramref name="animals"/>: each one's market value over its mean days to leave; null when one is unpriced.</summary>
    internal static double? RoamingLoss(IReadOnlyList<UpkeepAnimal> animals)
    {
        var loss = 0.0;
        foreach (var a in animals)
        {
            if (a.Colony?.Roping is not RimGovernor.Brain.GameFacts.Observations.AnimalRoping.Roped { Roam: { MarketValue: var value, MtbDays: var days } } || !double.IsFinite(value) || value < 0 || !double.IsFinite(days) || days <= 0)
            {
                return null;
            }
            loss += HerdShelterRules.LossPerDay(value, days);
        }
        return loss;
    }

    private static string UnpricedAnimal(IReadOnlyList<UpkeepAnimal> animals) =>
        "animal value:" + animals.First(a => RoamingLoss([a]) is null).Definition.Value;

    /// <summary>The cells of a planned pen's ring the ground stands no fence, wall or gate on.</summary>
    internal static int RingGaps(LayoutPlan plan, PlannedRoom pen, GroundCensus ground)
    {
        var (walls, doors) = plan.Ring(pen, ground);
        return pen.WallRing().Cells().Count(c => !walls.Contains(c) && !doors.Contains(c));
    }

    /// <summary>The hay field demand of every arrangement: the feeding remainder the pen feed stock does not haul.</summary>
    public static double HayDemand(IReadOnlyList<HerdArrangement> arrangements) => arrangements.Sum(a => a.HayNutrition);

    /// <summary>
    /// The pen feed stores the arrangements ask for, one per unit with a planned pen: a live store while the unit eats more
    /// than its pasture over the horizon and pen feed is known, sized by that gross feed over the smallest feed stack's
    /// nutrition per cell, else a retired one, so the zone goes once the pasture suffices.
    /// </summary>
    public static IReadOnlyList<PenFeedAsk> FeedAsks(IReadOnlyList<HerdArrangement> arrangements, LayoutPlan plan, EquatableArray<RimGovernor.Brain.GameFacts.Observations.PenFeedStock> feed)
    {
        var output = new List<PenFeedAsk>();
        var defs = feed.Select(f => f.Def).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var perCell = feed.Where(f => f.NutritionPerCell > 0).Select(f => f.NutritionPerCell).DefaultIfEmpty(0).Min();
        foreach (var a in arrangements)
        {
            if (plan.HerdRooms(HerdRoomsRules.PlannedPen, a.Herd) is not [var pen, ..])
            {
                continue;
            }
            if (a.FeedNeed <= 0)
            {
                output.Add(new PenFeedAsk(a.Herd, pen.Region, [], 0, true));
            }
            else if (defs.Count > 0 && perCell > 0)
            {
                var cells = (int)Math.Max(1, Math.Ceiling(a.FeedNeed / perCell));
                output.Add(new PenFeedAsk(a.Herd, pen.Region, EquatableArray.From(defs), cells, false));
            }
        }
        return output;
    }
}

/// <summary>The days until the next harvest the herd arrangements read (CalendarRules.HarvestGapDays), or the fact left unread.</summary>
public abstract record HarvestGap
{
    private HarvestGap() { }

    public sealed record Read(double Days) : HarvestGap;

    public sealed record Unread(string Fact) : HarvestGap;
}

/// <summary>A pen unit's feed store ask: its pen's floor, the feed defs, the cells it covers, or retired.</summary>
public sealed record PenFeedAsk(string Herd, Region Pen, EquatableArray<string> Defs, int Cells, bool Retired);

public static class FactsHerdArrangementExtensions
{
    /// <summary>The herd arrangements from the routine reading.</summary>
    public static IReadOnlyList<HerdArrangement> HerdArrangements(this ReviewFacts f) => HerdArrangementRules.Assess(f.HerdArrangementInput());

    /// <summary>The arrangements' facts from the routine reading; an invalid calendar leaves the harvest gap unread.</summary>
    public static HerdArrangementInput HerdArrangementInput(this ReviewFacts f)
    {
        GroundCensus? ground;
        try
        {
            ground = LayoutGroundRules.GroundOf(f.CurrentConstruction.Buildings);
        }
        catch (RequiredFactException)
        {
            ground = null;
        }
        HarvestGap gap;
        try
        {
            gap = new HarvestGap.Read(CalendarRules.HarvestGapDays(f.Calendar, f.DisasterConditions));
        }
        catch (DomainException)
        {
            gap = new HarvestGap.Unread("growing season");
        }
        return new HerdArrangementInput
        {
            Shelter = f.HerdShelterInput(),
            Plan = f.LayoutPlan,
            Ground = ground,
            Grazing = f.PenGrazing,
            Feed = f.PenFeed,
            HarvestGap = gap,
            Uncontained = AnimalUpkeepRules.ReviewAnimalUpkeep(f.AnimalUpkeep).Containment,
        };
    }
}
