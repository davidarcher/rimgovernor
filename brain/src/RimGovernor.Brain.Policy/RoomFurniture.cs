namespace RimGovernor.Brain.Policy;

/// <summary>
/// One bed an adult colonist may sleep in, as the catalog rows rank it (DefinitionCatalog.RoomFurniture).
/// Slots is the game's sleeping slot count (the bed's width); Free is a bed that costs nothing to build, a
/// sleeping spot.
/// </summary>
public sealed record FurnitureBed
{
    public string Def { get; init; } = "";

    public int Slots { get; init; }

    public bool Free { get; init; }
}

/// <summary>
/// A building the game links to a bed or bench as a facility (CompProperties_Facility), with the link rules
/// the interior templates lay it out by.
/// </summary>
public sealed record FacilityLink
{
    public string Def { get; init; } = "";

    /// <summary>The furthest centre-to-centre distance at which the facility links.</summary>
    public double MaxDistance { get; init; }

    /// <summary>How many things one facility links.</summary>
    public int MaxSimultaneous { get; init; }

    /// <summary>Set when the facility must stand against the thing it serves (mustBePlacedAdjacent and its bed-head variants).</summary>
    public bool Adjacent { get; init; }

    /// <summary>Set when it must stand cardinally beside a bed's head.</summary>
    public bool CardinalToHead { get; init; }
}

/// <summary>
/// The furniture the room planners place, chosen by rules over the catalog rows and never by name
/// (DefinitionCatalog.RoomFurniture). It is carried in PieceShapes, a threaded value, never a global:
/// Beds are the beds a colonist of any size may sleep in that count for a bedroom or barracks and are not
/// medical by default, by sleeping slots, then the ones that cost something by Comfort per unit of cost at
/// the best stuff, then the free ones by Comfort; Sarcophagus is the Building_Sarcophagus, the cheapest when
/// several; Heater is the cheapest buildable def that heats from a power draw; AnimalSpot is the free bed of
/// the kind animals use and AnimalBed the costed one with the most Comfort per cost; AnimalFlap is the
/// buildable Building_Door roaming animals can open; Bench is, per room role, the cheapest buildable work
/// table of that role worked from the floor in front of it; EndTable and Dresser are the Comfort facilities a
/// bed links; Cabinet is the work speed facility a workshop bench links and Monitor the tend quality facility
/// a medical bed links; AdvancedLab is the laboratory bench, besides the default one, that links a research
/// speed facility, and Analyzer the best such facility it links; both are empty when the catalog has none.
/// </summary>
public sealed partial record RoomFurniture
{
    public EquatableArray<FurnitureBed> Beds { get; init; }

    public string Sarcophagus { get; init; } = "";

    public string Heater { get; init; } = "";

    public string AnimalSpot { get; init; } = "";

    public string AnimalBed { get; init; } = "";

    /// <summary>The door of a pen and barn's shared wall.</summary>
    public string AnimalFlap { get; init; } = "";

    public IReadOnlyDictionary<RoomRole, string> Bench { get; init; } = new Dictionary<RoomRole, string>();

    public FacilityLink EndTable { get; init; } = new();

    public FacilityLink Dresser { get; init; } = new();

    public FacilityLink Cabinet { get; init; } = new();

    public FacilityLink Monitor { get; init; } = new();

    public string AdvancedLab { get; init; } = "";

    public FacilityLink Analyzer { get; init; } = new();

    /// <summary>The beds of one slot count in preference order.</summary>
    private List<FurnitureBed> SlotBeds(int slots) => Beds.Where(b => b.Slots == slots).ToList();

    private static List<string> BedDefs(IEnumerable<FurnitureBed> beds) => beds.Select(b => b.Def).ToList();

    /// <summary>Whether def is one of the catalog's beds.</summary>
    public bool IsBed(string def) => Beds.Any(b => b.Def == def);

    /// <summary>The single-slot beds in preference order: what the sleeping and hospital planners stage, a sleeping spot last.</summary>
    public IReadOnlyList<string> SleepingBeds() => BedDefs(SlotBeds(1));

    /// <summary>
    /// The beds the hospital planner stages, the sleeping beds except a bedroll, which only the sleeping
    /// planner stages (it checks the stuff on hand, the hospital planner does not).
    /// </summary>
    public IReadOnlyList<string> HospitalBeds() => SleepingBeds().Where(def => !PolicyFunctions.SleepingBedrolls.Contains(def)).ToList();

    /// <summary>The single bed the planners stage first (the jail's bed, the bedroom and hospital templates' default bed); empty when the catalog has none.</summary>
    public string PrimaryBed()
    {
        var beds = SlotBeds(1);
        return beds.Count > 0 ? beds[0].Def : "";
    }

    /// <summary>The double bed staged first for a couple; empty when the catalog has none.</summary>
    public string CoupleBed()
    {
        foreach (var b in SlotBeds(2))
        {
            if (!b.Free)
            {
                return b.Def;
            }
        }
        return "";
    }

    /// <summary>
    /// The bed definitions a bed step may stage in preference order; for a couple each double comes first,
    /// ahead of the single of its rank.
    /// </summary>
    public IReadOnlyList<string> SleepingLadder(bool couple)
    {
        var singles = SlotBeds(1);
        if (!couple)
        {
            return BedDefs(singles);
        }
        var doubles = SlotBeds(2).Where(b => !b.Free).ToList();
        var output = new List<string>();
        for (var i = 0; i < Math.Max(singles.Count, doubles.Count); i++)
        {
            if (i < doubles.Count)
            {
                output.Add(doubles[i].Def);
            }
            if (i < singles.Count)
            {
                output.Add(singles[i].Def);
            }
        }
        return output;
    }

    /// <summary>The facility links, in a fixed order.</summary>
    public IReadOnlyList<FacilityLink> Facilities() => [EndTable, Dresser, Cabinet, Monitor];

    /// <summary>The default bench of a room role's template.</summary>
    public string BenchFor(RoomRole role) => Bench.GetValueOrDefault(role) ?? "";

    /// <summary>
    /// Every definition the planners read availability and stuff for, sorted and without repeats: the beds,
    /// the sarcophagus, the animal beds, the default benches and the facilities.
    /// </summary>
    public IReadOnlyList<string> Definitions()
    {
        var output = BedDefs(Beds);
        output.AddRange([Sarcophagus, Heater, AnimalSpot, AnimalBed, AnimalFlap]);
        output.AddRange(Bench.Values);
        foreach (var link in Facilities())
        {
            output.Add(link.Def);
        }
        output.AddRange([AdvancedLab, Analyzer.Def]);
        return output.Where(s => s.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Checks the furniture is a set the interior templates can lay out: a bed, a double bed, a sarcophagus,
    /// both animal beds, a default bench of each room role with a template, and facilities whose link rules
    /// match the way the templates place them (the end table beside the bed's head, the dresser and the
    /// monitor, the cabinet shared by two benches). Throws <see cref="DomainException"/>.
    /// </summary>
    public void Validate()
    {
        if (PrimaryBed().Length == 0)
        {
            throw new DomainException("room furniture has no single bed");
        }
        if (CoupleBed().Length == 0)
        {
            throw new DomainException("room furniture has no costed double bed");
        }
        (string Role, string Def)[] singles =
        [
            ("sarcophagus", Sarcophagus), ("heater", Heater), ("animal sleeping spot", AnimalSpot), ("animal bed", AnimalBed), ("animal flap", AnimalFlap),
        ];
        foreach (var (role, def) in singles)
        {
            if (def.Length == 0)
            {
                throw new DomainException($"room furniture has no {role}");
            }
        }
        foreach (var role in new[] { RoomRole.Kitchen, RoomRole.Workshop, RoomRole.Laboratory })
        {
            if (BenchFor(role).Length == 0)
            {
                throw new DomainException($"room furniture has no default {Wire.Name(role)} bench");
            }
        }
        (string What, FacilityLink Link, Func<FacilityLink, bool> Ok, string Need)[] checks =
        [
            ("end table", EndTable, l => l.CardinalToHead && l.MaxSimultaneous >= 1, "stand cardinally beside a bed's head"),
            ("dresser", Dresser, l => !l.Adjacent && l.MaxDistance > 0 && l.MaxSimultaneous >= 1, "link at a distance"),
            ("tool cabinet", Cabinet, l => l.MaxSimultaneous >= 2 && l.MaxDistance > 0, "link two benches"),
            ("vitals monitor", Monitor, l => l.Adjacent && !l.CardinalToHead && l.MaxSimultaneous >= 1, "stand against the bed it serves"),
        ];
        foreach (var check in checks)
        {
            if (check.Link.Def.Length == 0)
            {
                throw new DomainException($"room furniture has no {check.What} facility");
            }
            if (!check.Ok(check.Link))
            {
                throw new DomainException($"the interior templates place {check.What} {check.Link.Def} as a facility that must {check.Need}, its row says otherwise ({check.Link})");
            }
        }
        // The advanced lab is optional, but one that names a bench needs the analyzer the lab template
        // reserves a slot for.
        if ((AdvancedLab.Length == 0) != (Analyzer.Def.Length == 0))
        {
            throw new DomainException($"room furniture has advanced lab \"{AdvancedLab}\" and analyzer \"{Analyzer.Def}\": both or neither");
        }
        if (Analyzer.Def.Length != 0 && (Analyzer.Adjacent || Analyzer.CardinalToHead || Analyzer.MaxDistance <= 0 || Analyzer.MaxSimultaneous < 1))
        {
            throw new DomainException($"the lab template places analyzer {Analyzer.Def} as a facility that links at a distance, its row says otherwise ({Analyzer})");
        }
    }
}
