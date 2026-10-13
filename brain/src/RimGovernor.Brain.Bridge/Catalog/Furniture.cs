using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_furniture.go: the furniture the room planners place, chosen by rules over the catalog rows
// (P.RoomFurniture states them); the shapes are the rows' plannable shapes (PieceShapes), which name the room role
// family of each bench. A catalog with no bed, sarcophagus, animal bed, bench or facility the rules need is an error.

public sealed partial class DefinitionCatalog
{
    private const string StatWorkTableWorkSpeedFactor = "WorkTableWorkSpeedFactor";
    private const string StatMedicalTendQualityOffset = "MedicalTendQualityOffset";
    private const string StatResearchSpeedFactor = "ResearchSpeedFactor";

    /// <summary>One bed row with the numbers the rules rank it by.</summary>
    private sealed record BedRank(string Def, int Slots, bool Free, double Ratio, double Comfort, bool Humanlike, bool Medical, bool Counts, bool Crib, bool Unrestricted);

    private sealed record BedRanks(IReadOnlyList<BedRank> All, IReadOnlyList<BedRank> Sleeping);

    /// <summary>A buildable def's facility comp with the link rules the templates read.</summary>
    private sealed record FacilityRow(string Def, IReadOnlyDictionary<string, float> Offsets, double MaxDistance, int MaxSimultaneous, bool Adjacent, bool CardinalToHead);

    private Lazy<BedRanks>? _bedRanks;
    private Lazy<IReadOnlyDictionary<string, FacilityRow>>? _facilities;

    private BedRanks Beds() => (_bedRanks ??= new(ComputeBedRanks)).Value;

    private IReadOnlyDictionary<string, FacilityRow> Facilities() => (_facilities ??= new(ComputeFacilityRows)).Value;

    /// <summary>The furniture the room planners place for the shapes (the plannable shape of every buildable def).</summary>
    public P.RoomFurniture RoomFurniture(IReadOnlyDictionary<string, P.InteriorPieceDef> shapes)
    {
        var beds = Beds();
        var furniture = new P.RoomFurniture
        {
            Beds = Domain.EquatableArray.From(beds.Sleeping.Select(b => new P.FurnitureBed { Def = b.Def, Slots = b.Slots, Free = b.Free })),
            Heater = CheapestHeater(),
            AnimalFlap = AnimalFlap(),
            Sarcophagus = CheapestOfClass<Building_Sarcophagus>(),
        };
        var (spot, bed) = AnimalBeds(beds);
        var bench = new Dictionary<P.RoomRole, string>();
        foreach (var role in new[] { P.RoomRole.Kitchen, P.RoomRole.Workshop, P.RoomRole.Laboratory })
        {
            bench[role] = CheapestBench(shapes, role);
        }

        furniture = furniture with { AnimalSpot = spot, AnimalBed = bed, Bench = bench };
        var facilities = Facilities();
        var bedLinks = LinkedFacilities(furniture.PrimaryBed());
        var benchLinks = LinkedFacilities(bench[P.RoomRole.Workshop]);
        var medicalLinks = LinkedFacilities([.. beds.All.Where(b => b.Medical).Select(b => b.Def)]);
        furniture = furniture with
        {
            EndTable = BestFacility("an end table", facilities, bedLinks, StatNames.Comfort, f => f.CardinalToHead),
            Dresser = BestFacility("a dresser", facilities, bedLinks, StatNames.Comfort, f => !f.Adjacent),
            Cabinet = BestFacility("a tool cabinet", facilities, benchLinks, StatWorkTableWorkSpeedFactor, _ => true),
            Monitor = BestFacility("a vitals monitor", facilities, medicalLinks, StatMedicalTendQualityOffset, f => f.Adjacent && !f.CardinalToHead),
        };
        var (lab, analyzer) = AdvancedLab(shapes, facilities, bench[P.RoomRole.Laboratory]);
        return furniture with { AdvancedLab = lab, Analyzer = analyzer };
    }

    // The laboratory bench besides the default one that links a facility offsetting research speed (the cheapest when
    // several, by name when equal) and the best such facility it links. Both are empty when the catalog has no such bench.
    private (string Bench, P.FacilityLink Link) AdvancedLab(IReadOnlyDictionary<string, P.InteriorPieceDef> shapes, IReadOnlyDictionary<string, FacilityRow> facilities, string plain)
    {
        var bench = string.Empty;
        var link = new P.FacilityLink();
        var bestCost = double.PositiveInfinity;
        foreach (var (name, shape) in shapes)
        {
            if (name == plain || shape.Family != P.RoomRole.Laboratory || !P.InteriorPieceShapeExtensions.WorkedFromFront(shape) || ThingRow(name).building?.workTableRoomRole?.defName != Wire.Name(P.RoomRole.Laboratory))
            {
                continue;
            }

            if (FindFacility(facilities, LinkedFacilities(name), StatResearchSpeedFactor, _ => true) is not { } best)
            {
                continue;
            }

            var cost = CheapestCostValue(name);
            if (cost < bestCost || (cost == bestCost && string.CompareOrdinal(name, bench) < 0))
            {
                (bench, link, bestCost) = (name, best, cost);
            }
        }

        return (bench, link);
    }

    // BedRanks reads every buildable Building_Bed. A bed is unrestricted when its body size limit is the largest any
    // bed states, the game's "any pawn".
    private BedRanks ComputeBedRanks()
    {
        var limit = _thingDefs.Values.Where(IsBed).Select(row => row.building.bed_maxBodySize).DefaultIfEmpty(float.NegativeInfinity).Max();
        var all = new List<BedRank>();
        var sleeping = new List<BedRank>();
        foreach (var (name, row) in _thingDefs)
        {
            if (!Buildable(row) || !IsBed(row))
            {
                continue;
            }

            var b = row.building;
            var (ratio, comfort, free) = ComfortPerCost(name);
            var rank = new BedRank(name, row.size.x, free, ratio, comfort, b.bed_humanlike, b.bed_defaultMedical, b.bed_countsForBedroomOrBarracks, b.bed_crib, b.bed_maxBodySize >= limit);
            all.Add(rank);
            if (rank.Humanlike && !rank.Medical && rank.Counts && !rank.Crib && rank.Unrestricted && !double.IsNegativeInfinity(rank.Ratio))
            {
                sleeping.Add(rank);
            }
        }

        sleeping.Sort(CompareBeds);
        return new BedRanks(all, sleeping);
    }

    private static bool IsBed(ThingDef row) => typeof(Building_Bed).IsAssignableFrom(row.thingClass);

    // Orders beds by sleeping slots, then the costed ones by comfort per cost (most first) ahead of the free ones by
    // comfort, then by name.
    private static int CompareBeds(BedRank a, BedRank b)
    {
        if (a.Slots != b.Slots)
        {
            return a.Slots.CompareTo(b.Slots);
        }

        if (a.Free != b.Free)
        {
            return a.Free ? 1 : -1;
        }

        var primary = a.Free ? b.Comfort.CompareTo(a.Comfort) : b.Ratio.CompareTo(a.Ratio);
        return primary != 0 ? primary : string.CompareOrdinal(a.Def, b.Def);
    }

    // A bed's Comfort per unit of cost at its best stuff (the market value of its adjusted costs), free when some stuff
    // builds it for nothing, and negative infinity when the game shows no Comfort for a bed that costs something.
    private (double Ratio, double Comfort, bool Free) ComfortPerCost(string name)
    {
        var stuffs = AllowedStuffs(name);
        var options = stuffs.Count == 0 ? [string.Empty] : stuffs;
        var ratio = double.NegativeInfinity;
        double comfort = 0;
        var free = false;
        foreach (var stuff in options)
        {
            var (value, shown) = ShownStatValue(name, stuff, StatNames.Comfort);
            var cost = CostValueOf(name, stuff);
            if (cost <= 0)
            {
                free = true;
                comfort = Math.Max(comfort, value);
                continue;
            }

            if (shown)
            {
                ratio = Math.Max(ratio, value / cost);
            }
        }

        return (free ? double.PositiveInfinity : ratio, comfort, free);
    }

    // The free bed and the best costed bed an animal of any size can use (not humanlike, no body size limit): the
    // sleeping spot and the bed.
    private static (string Spot, string Bed) AnimalBeds(BedRanks ranks)
    {
        var spots = new List<BedRank>();
        var beds = new List<BedRank>();
        foreach (var b in ranks.All)
        {
            if (b.Humanlike || !b.Unrestricted)
            {
                continue;
            }

            if (b.Free)
            {
                spots.Add(b);
            }
            else if (!double.IsNegativeInfinity(b.Ratio))
            {
                beds.Add(b);
            }
        }

        if (spots.Count == 0 || beds.Count == 0)
        {
            throw new CatalogException($"catalog has {spots.Count} free and {beds.Count} costed animal beds, the barn needs one of each");
        }

        spots.Sort((a, b) => string.CompareOrdinal(a.Def, b.Def));
        beds.Sort(CompareBeds);
        return (spots[0].Def, beds[0].Def);
    }

    // The buildable door roaming animals can open (a Building_Door whose building properties say roamerCanOpen), by name
    // when several. A catalog with none is an error: nothing stands in for the flap between a pen and its barn.
    private string AnimalFlap() =>
        _thingDefs.Values
            .Where(row => Buildable(row) && row.building.roamerCanOpen && typeof(Building_Door).IsAssignableFrom(row.thingClass))
            .Select(row => row.defName)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
        ?? throw new CatalogException("catalog has no buildable animal flap (a Building_Door that roamers can open)");

    // The cheapest buildable def of the class (or a subclass), by name when equal.
    private string CheapestOfClass<T>() =>
        Cheapest(_thingDefs.Values.Where(row => Buildable(row) && typeof(T).IsAssignableFrom(row.thingClass)).Select(row => row.defName))
        ?? throw new CatalogException($"catalog has no buildable {typeof(T).Name}");

    // The cheapest buildable def that heats its room from a power draw (a CompProperties_TempControl with a positive
    // energyPerSecond and a consuming power comp), by name when equal. A cooler's energy is negative and a campfire has
    // no power comp, so neither matches.
    private string CheapestHeater() =>
        Cheapest(_thingDefs.Values
            .Where(row => Buildable(row)
                && row.GetCompProperties<CompProperties_TempControl>() is { energyPerSecond: > 0 }
                && row.GetCompProperties<CompProperties_Power>() is { } power && GameMembers.BasePowerConsumption(power) > 0)
            .Select(row => row.defName))
        ?? throw new CatalogException("catalog has no buildable heater (a temperature control that heats from a power draw)");

    // The cheapest buildable work table of a room role that a worker uses from the floor in front of it, by name when equal.
    private string CheapestBench(IReadOnlyDictionary<string, P.InteriorPieceDef> shapes, P.RoomRole role) =>
        Cheapest(shapes
            .Where(pair => pair.Value.Family == role && P.InteriorPieceShapeExtensions.WorkedFromFront(pair.Value) && ThingRow(pair.Key).building?.workTableRoomRole?.defName == Wire.Name(role))
            .Select(pair => pair.Key))
        ?? throw new CatalogException($"catalog has no buildable {Wire.Name(role)} work table worked from the floor in front of it");

    private string? Cheapest(IEnumerable<string> names)
    {
        string? best = null;
        var bestCost = double.PositiveInfinity;
        foreach (var name in names)
        {
            var cost = CheapestCostValue(name);
            if (cost < bestCost || (cost == bestCost && string.CompareOrdinal(name, best) < 0))
            {
                (best, bestCost) = (name, cost);
            }
        }

        return best;
    }

    // The buildable defs carrying a CompProperties_Facility (exactly: a gravship facility is no bed or bench facility).
    private Dictionary<string, FacilityRow> ComputeFacilityRows()
    {
        var rows = new Dictionary<string, FacilityRow>(StringComparer.Ordinal);
        foreach (var (name, row) in _thingDefs)
        {
            if (!Buildable(row) || ExactComp<CompProperties_Facility>(row) is not { } facility)
            {
                continue;
            }

            var offsets = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var offset in facility.statOffsets.Seq())
            {
                offsets[offset.stat.defName] = offsets.GetValueOrDefault(offset.stat.defName) + offset.value;
            }

            var cardinal = facility.mustBePlacedAdjacentCardinalToBedHead || facility.mustBePlacedAdjacentCardinalToAndFacingBedHead;
            rows[name] = new FacilityRow(name, offsets, facility.maxDistance, facility.maxSimultaneous, cardinal || facility.mustBePlacedAdjacent, cardinal);
        }

        return rows;
    }

    // The first comp of row that is exactly T, not a subclass with a purpose of its own.
    private static T? ExactComp<T>(ThingDef row)
        where T : CompProperties =>
        row.comps.Seq().OfType<T>().FirstOrDefault(c => c.GetType() == typeof(T));

    // The facility defs any of the defs' CompAffectedByFacilities lists as linkable.
    private HashSet<string> LinkedFacilities(params string[] defs)
    {
        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in defs)
        {
            if (ExactComp<CompProperties_AffectedByFacilities>(ThingRow(name)) is { } affected)
            {
                linked.UnionWith(affected.linkableFacilities.Seq().Select(f => f.defName));
            }
        }

        return linked;
    }

    // The linkable facility that offsets stat and fits the slot with the most offset per unit of cost (a free one is the
    // best), by name when equal.
    private P.FacilityLink BestFacility(string what, IReadOnlyDictionary<string, FacilityRow> facilities, HashSet<string> linked, string stat, Func<FacilityRow, bool> fits) =>
        FindFacility(facilities, linked, stat, fits)
        ?? throw new CatalogException($"catalog has no facility that is {what}: nothing the interior templates' bed or bench links offsets {stat} in that way");

    private P.FacilityLink? FindFacility(IReadOnlyDictionary<string, FacilityRow> facilities, HashSet<string> linked, string stat, Func<FacilityRow, bool> fits)
    {
        FacilityRow? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var (name, f) in facilities)
        {
            if (!linked.Contains(name) || !f.Offsets.TryGetValue(stat, out var offset) || offset <= 0 || !fits(f))
            {
                continue;
            }

            var cost = CheapestCostValue(name);
            var score = cost > 0 ? offset / cost : double.PositiveInfinity;
            if (score > bestScore || (score == bestScore && string.CompareOrdinal(name, best?.Def) < 0))
            {
                (best, bestScore) = (f, score);
            }
        }

        return best is null
            ? null
            : new P.FacilityLink { Def = best.Def, MaxDistance = best.MaxDistance, MaxSimultaneous = best.MaxSimultaneous, Adjacent = best.Adjacent, CardinalToHead = best.CardinalToHead };
    }
}
