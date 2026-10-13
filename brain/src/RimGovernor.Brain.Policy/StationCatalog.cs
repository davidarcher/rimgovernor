namespace RimGovernor.Brain.Policy;

/// <summary>
/// Every buildable station the game offers, read from its definitions and room-role code (bridge builds it). A
/// station is a building that adds to a native room role, has a capacity, or needs something of the room it
/// stands in. The planner reasons about stations; rooms emerge from clustering them, and a capability is only a
/// derived grouping of station classes (Hosting).
/// </summary>
public sealed record StationCatalog
{
    public IReadOnlyDictionary<string, Station> Stations { get; init; } = new Dictionary<string, Station>();

    /// <summary>Prices the filth a butchering spawns (FilthModel); the default prices nothing.</summary>
    public FilthModel Filth { get; init; } = new();

    /// <summary>The heat the vent, door, heater and cooler move, from the defs and the game's constants (#2940); the default is unknown.</summary>
    public ClimateModel Climate { get; init; } = new();

    /// <summary>The lamp's draw and build value (#2945); the default is unknown and leaves the lamps unpriced.</summary>
    public LightModel Light { get; init; } = new();

    /// <summary>The generators, the battery, the conduit and the wall a power site is derived and priced from (#2999); the default is unknown.</summary>
    public PowerModel Power { get; init; } = new();

    /// <summary>The break curve and the thought stages the mood price reads (#2954); the default is unknown and leaves mood unpriced.</summary>
    public MoodModel Mood { get; init; } = new();

    /// <summary>The comfort and beauty needs and the furniture that feeds them (#2975); the default is unknown.</summary>
    public ComfortModel Comfort { get; init; } = new();

    /// <summary>The joy givers a colonist uses and the game numbers beside them (#2997); the default is unknown.</summary>
    public JoyModel Joy { get; init; } = new();

    /// <summary>The mined and chopped goods and what they cost in work (#2998); the default is unknown and leaves them unhauled.</summary>
    public HaulModel Haul { get; init; } = new();

    /// <summary>Returns the station for a definition.</summary>
    public bool TryGetStation(string def, out Station station)
    {
        if (Stations.TryGetValue(def, out var found))
        {
            station = found;
            return true;
        }
        station = new Station();
        return false;
    }

    /// <summary>Lists the station definitions, sorted.</summary>
    public List<string> Defs() => [.. Stations.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Lists, sorted, the stations that add to a role.</summary>
    public List<string> Contributors(RoomRole role) =>
        [.. Defs().Where(def => Stations[def].Contributions.Any(c => c.Role == role))];

    /// <summary>
    /// Lists, sorted, the stations of the classes that provide a capability: the capability is the grouping,
    /// the station family its census role (capabilityHomes).
    /// </summary>
    public List<string> Hosting(Capability capability)
    {
        if (!PolicyFunctions.CapabilityHomes.TryGetValue(capability, out var home))
        {
            return [];
        }
        return [.. Defs().Where(def => Stations[def].Shape.Family == home)];
    }

    /// <summary>Lists, sorted, the stations that cool their room.</summary>
    public List<string> Coolers() => [.. Defs().Where(def => Stations[def].Cooling.Cools())];
}

/// <summary>One buildable definition as the planner sees it.</summary>
public sealed record Station
{
    public string Def { get; init; } = "";

    /// <summary>The footprint: size, interaction cell and slot family, the same value PieceShapes holds for the def.</summary>
    public InteriorPieceDef Shape { get; init; } = new();

    /// <summary>Where the footprint may stand.</summary>
    public StationAccess Access { get; init; }

    /// <summary>The native room-role scores the building adds, as RoomRoleWorker.GetScoreDeltaIfBuildingPlaced does.</summary>
    public IReadOnlyList<RoleScore> Contributions { get; init; } = [];

    /// <summary>What one station serves.</summary>
    public StationCapacity Capacity { get; init; } = new();

    /// <summary>A humanlike bed's bed_emptyCountsForBarracks: an unassigned bed counts toward the barracks test.</summary>
    public bool EmptyCountsForBarracks { get; init; }

    /// <summary>What the station wants of its surroundings.</summary>
    public IReadOnlyList<EnvNeed> Needs { get; init; } = [];

    /// <summary>Set when the station cools its room (a temperature control that draws heat out); the default cools nothing.</summary>
    public StationCooling Cooling { get; init; } = new();

    /// <summary>What the station gives off into its room (CompProperties_HeatPusher); the default gives off none.</summary>
    public HeatSource Heat { get; init; } = new();

    /// <summary>
    /// The def's power comp wattage in the sign the power policy reads (PowerBuilding.BaseW): negative for a
    /// consumer, positive for a producer, zero for none or a battery. The conduit router wires planned rooms
    /// holding a station with BaseW &lt; 0 (DrawsPower).
    /// </summary>
    public double BaseW { get; init; }

    /// <summary>
    /// The package id of the content that defines the station ("ludeon.rimworld" for the base game, an
    /// expansion's id otherwise); the station is plannable only while that content is active.
    /// </summary>
    public string Content { get; init; } = "";

    /// <summary>ThingDef.Minifiable (minifiedDef set): the built building can be uninstalled and reinstalled elsewhere (BuiltPiece.Movable).</summary>
    public bool Minifiable { get; init; }

    /// <summary>
    /// ThingDef.Fillage == Full (fillPercent over 0.99): no attachment, a wall lamp among them, may hang on a
    /// cell it holds (Placeworker_AttachedToWall).
    /// </summary>
    public bool FillsCell { get; init; }

    /// <summary>Reports a station that consumes grid power.</summary>
    public bool DrawsPower() => BaseW < 0;
}

/// <summary>Whether a station stands on open floor or in a wall.</summary>
public enum StationAccess
{
    /// <summary>An ordinary footprint on a floor cell.</summary>
    Floor,

    /// <summary>A building that replaces or attaches to a wall (a door, a vent, a wall lamp): canPlaceOverWall or the attached-to-wall place worker.</summary>
    Wall,
}

/// <summary>Names the instance state a definition cannot decide, which the game's role worker tests on the building itself.</summary>
public enum RoleCondition
{
    /// <summary>The definition alone decides.</summary>
    [WireName("")]
    Always,

    /// <summary>A bed neither medical nor set for prisoners.</summary>
    BedFree,

    /// <summary>A medical bed not set for prisoners.</summary>
    BedMedical,

    /// <summary>A prisoner bed that is not medical.</summary>
    BedPrisoner,

    /// <summary>A medical prisoner bed.</summary>
    BedPrisonerMedical,

    /// <summary>An altar whose precept carries an ideo structure meme.</summary>
    IdeoAltar,

    /// <summary>A deathrest-bindable building that is not the casket.</summary>
    Bindable,
}

/// <summary>
/// The weight the game's role worker gives one building under a condition. Additive scores add per building
/// (kitchen 28 per stove); a Flat score is a gate the role worker applies once (a bedroom is 100000 however
/// many beds). Floor is the least score once any building counts (the worship room's 2000). Go
/// <c>RoleContribution</c> is an alias of this type.
/// </summary>
public sealed record RoleScore
{
    public RoomRole Role { get; init; }

    public RoleCondition When { get; init; }

    public double Weight { get; init; }

    public bool Additive { get; init; }

    public double Floor { get; init; }
}

/// <summary>The unit of a station's capacity.</summary>
public enum CapacityKind
{
    [WireName("")]
    None,

    /// <summary>Slots sleepers (BedUtility.GetSleepingSlotsCount).</summary>
    Sleepers,

    /// <summary>Slots stacks held (cells times maxItemsInCell).</summary>
    Items,

    /// <summary>One worker at a time; Recipes give the work per unit.</summary>
    Workers,
}

/// <summary>
/// A station's throughput. A work table serves one worker (Slots 1); the work one unit of each recipe takes is
/// in Recipes, so throughput is 60000 ticks a day over the amount at the worker's speed.
/// </summary>
public sealed record StationCapacity
{
    public CapacityKind Kind { get; init; }

    public int Slots { get; init; }

    public IReadOnlyList<RecipeWorkAmount> Recipes { get; init; } = [];
}

/// <summary>The work one unit of a recipe takes at the station.</summary>
public sealed record RecipeWorkAmount(string Recipe, double Work);

/// <summary>What a station wants of the room around it.</summary>
public enum EnvNeedKind
{
    [WireName("")]
    None,

    /// <summary>
    /// The station's effect scales with a room stat. Stat is the RoomStatDef a StatPart_RoomStat reads, Input
    /// the RoomStatDef at the end of its inputStat chain (Cleanliness for surgery success), Via the StatDef
    /// carrying the part and Base that stat's value on the station.
    /// </summary>
    RoomStat,

    /// <summary>The work rate is multiplied by Factor outside [Min, Max] degrees (StatPart_WorkTableTemperature).</summary>
    Temperature,

    /// <summary>Factor applies when the room is outdoors (StatPart_WorkTableOutdoors, PlaceWorker_MustBeIndoors: Factor 0 there).</summary>
    Indoors,

    /// <summary>The building cannot stand under a roof (PlaceWorker_NotUnderRoof).</summary>
    OpenSky,

    /// <summary>The work rate is multiplied by Factor when the room's native role is not Role (StatPart_WorkTableRoomRole).</summary>
    Role,

    /// <summary>
    /// The station stores things that rot, so it wants a room kept cold. The rot curve is game code
    /// (GenTemperature.RotRateAtTemperature), so no numbers are stated.
    /// </summary>
    Cold,
}

/// <summary>One environment need. Which fields are set depends on Kind.</summary>
public sealed record EnvNeed
{
    public EnvNeedKind Kind { get; init; }

    public string Stat { get; init; } = "";

    public string Input { get; init; } = "";

    public string Via { get; init; } = "";

    public RoomRole Role { get; init; }

    public double Base { get; init; }

    public double Min { get; init; }

    public double Max { get; init; }

    public double Factor { get; init; }
}

/// <summary>
/// What a cooling station does, from its CompProperties_TempControl and power comp: the heat it moves per second
/// (the def's negative energyPerSecond, as a positive number) and the power it draws.
/// </summary>
public sealed record StationCooling
{
    public double HeatPerSecond { get; init; }

    public double PowerDraw { get; init; }

    /// <summary>Reports whether the station cools its room.</summary>
    public bool Cools() => HeatPerSecond > 0;
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The weights of the game's RoomRoleWorker.GetScore (and GetScoreDeltaIfBuildingPlaced) per building. They
    /// are literals in the game's code, not in any def, so the catalog cannot read them; they were read by
    /// decompiling 1.6 and are a DefTables entry keyed by RoomRoleDef so a renamed role fails the
    /// dangling-reference gate. Which buildings earn a score is read from the defs (bridge).
    /// </summary>
    private static readonly IReadOnlyList<RoleScore> RoomRoleScores =
    [
        new() { Role = RoomRole.Kitchen, Weight = 28, Additive = true },
        new() { Role = RoomRole.Workshop, Weight = 27, Additive = true },
        new() { Role = RoomRole.Laboratory, Weight = 60, Additive = true },
        new() { Role = RoomRole.DiningRoom, Weight = 12, Additive = true },
        new() { Role = RoomRole.RecRoom, Weight = 7, Additive = true },
        new() { Role = RoomRole.Storeroom, Weight = 1, Additive = true },
        new() { Role = RoomRole.Barn, Weight = 7.6, Additive = true },
        new() { Role = RoomRole.Tomb, Weight = 50, Additive = true },
        new() { Role = RoomRole.Bedroom, When = RoleCondition.BedFree, Weight = 100000 },
        new() { Role = RoomRole.Barracks, When = RoleCondition.BedFree, Weight = 100100, Additive = true },
        new() { Role = RoomRole.Nursery, When = RoleCondition.BedFree, Weight = 100200, Additive = true },
        new() { Role = RoomRole.Hospital, When = RoleCondition.BedMedical, Weight = 100000 },
        new() { Role = RoomRole.PrisonCell, When = RoleCondition.BedPrisoner, Weight = 170000 },
        new() { Role = RoomRole.PrisonCell, When = RoleCondition.BedPrisonerMedical, Weight = 100000 },
        new() { Role = RoomRole.PrisonBarracks, When = RoleCondition.BedPrisoner, Weight = 100100, Additive = true },
        new() { Role = RoomRole.PrisonBarracks, When = RoleCondition.BedPrisonerMedical, Weight = 50001, Additive = true },
        new() { Role = RoomRole.ThroneRoom, Weight = 10000 },
        new() { Role = RoomRole.WorshipRoom, When = RoleCondition.IdeoAltar, Weight = 75, Additive = true, Floor = 2000 },
        new() { Role = RoomRole.Classroom, Weight = 8, Additive = true },
        new() { Role = RoomRole.Playroom, Weight = 8, Additive = true },
        new() { Role = RoomRole.DeathrestChamber, Weight = 100, Additive = true },
        new() { Role = RoomRole.DeathrestChamber, When = RoleCondition.Bindable, Weight = 50, Additive = true },
        new() { Role = RoomRole.CeremonialChamber, Weight = 200, Additive = true },
    ];

    /// <summary>The game's weight for a role under a condition.</summary>
    public static bool TryRoomRoleScore(RoomRole role, RoleCondition when, out RoleScore score)
    {
        foreach (var s in RoomRoleScores)
        {
            if (s.Role == role && s.When == when)
            {
                score = s;
                return true;
            }
        }
        score = new RoleScore();
        return false;
    }

    internal static List<string> RoomRoleScoreRoles() =>
        [.. RoomRoleScores.Select(s => Wire.Name(s.Role)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The buildings the game's role workers name through ThingDefOf rather than by a trait of the def: the
    /// deathrest casket (the bindable test counts every other bindable building half) and the psychic ritual
    /// spot. The game offers no def signal for either.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RoomRole> RoomRoleNamedDefs = new Dictionary<string, RoomRole>(StringComparer.Ordinal)
    {
        ["DeathrestCasket"] = RoomRole.DeathrestChamber,
        ["PsychicRitualSpot"] = RoomRole.CeremonialChamber,
    };
}
