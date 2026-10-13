using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>ErrStationFacts: advice that could not be assembled because a fact it reads is unknown; the message names the fact.</summary>
public sealed class StationFactsException(string name) : DomainException("station facts unknown: " + name);

/// <summary>
/// What the advice reads, each part from the model that owns it: the catalog rows (Catalog, Furniture, Dining, Items,
/// Recipes, StackLimit, Perishable), the population censuses, the colonists' food census, the work ledger's orders,
/// the resource demand and the saved plan with the survey it was grown over. Unknown facts are never zero: a fact the
/// colony has not read fails the advice with StationFactsException.
/// </summary>
public sealed record StationFacts
{
    public StationCatalog Catalog { get; init; } = default!;
    public RoomFurniture Furniture { get; init; } = default!;
    public DiningFurniture Dining { get; init; } = default!;
    public ItemFacts Items { get; init; } = default!;
    public IReadOnlyDictionary<string, RecipeFlow> Recipes { get; init; } = new Dictionary<string, RecipeFlow>();

    /// <summary>StackLimit and Perishable cover every resource of Stock and of the recipes' items.</summary>
    public IReadOnlyDictionary<Resource, long> StackLimit { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyDictionary<Resource, bool> Perishable { get; init; } = new Dictionary<Resource, bool>();

    /// <summary>The colony's people.</summary>
    public Fact<long> Colonists { get; init; }

    /// <summary>Patients and Surgical size the hospital ward as SelectHospitalBed does.</summary>
    public Fact<IReadOnlyList<CarePawn>> Patients { get; init; }

    public IReadOnlyList<PawnId> Surgical { get; init; } = [];

    /// <summary>The held prisoners.</summary>
    public Fact<int> Prisoners { get; init; }

    /// <summary>The pen herd.</summary>
    public int Animals { get; init; }

    /// <summary>The pen herd by race, Races the animal race rows and Pasture the worst-quadrum growth per terrain: the pen nodes' products and hunger (#2996). Herds empty asks for no pen.</summary>
    public IReadOnlyList<StationHerd> Herds { get; init; } = [];

    public AnimalRaceCatalog Races { get; init; } = default!;
    public Fact<PastureTable> Pasture { get; init; }

    /// <summary>Says who is a baby.</summary>
    public Fact<IReadOnlyList<WorkPawn>> WorkPawns { get; init; }

    /// <summary>The colonists' food census; its consumers' rates sum to the nutrition a day.</summary>
    public Fact<FoodSupply> Food { get; init; }

    /// <summary>The work ledger's wanted orders.</summary>
    public Fact<IReadOnlyList<OrderSpec>> Orders { get; init; }

    /// <summary>The resource demand's stock levels (DerivedDemand.Needs).</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    /// <summary>The game's roof support distance.</summary>
    public double RoofDistance { get; init; }

    /// <summary>The saved plan the advice reads zones, reservations and built rooms from.</summary>
    public LayoutPlan Plan { get; init; } = new();

    /// <summary>The ids of the plan's rooms that stand as planned (BuiltRooms); null while the construction census is unknown.</summary>
    public IReadOnlySet<int>? Built { get; init; }

    /// <summary>The rooms the replan holds where they stand (TilePinned): a pinned room stands where it is in the embedding, and one not yet built is a rebuild in flight, which holds every built room.</summary>
    public IReadOnlySet<int> Pinned { get; init; } = new HashSet<int>();

    /// <summary>The map the plan was grown over.</summary>
    public MapSurvey Survey { get; init; } = default!;

    /// <summary>The seasonal outdoor temperature curve (twelve means, C); the climate advice needs it and the catalog's full-rot temperature and is left off while either is unknown.</summary>
    public Fact<IReadOnlyList<double>> OutdoorCurve { get; init; }

    public double FullRotRateC { get; init; }

    /// <summary>Whether the vent is researched (#2915); unknown or false leaves vents out of the tiled plan.</summary>
    public Fact<bool> VentAvailable { get; init; }

    /// <summary>The crops the colony grows (the caller's choice, not the chain's): each edible crop with a known capacity target is a field node (#2920).</summary>
    public IReadOnlyList<CropChoice> Crops { get; init; } = [];

    /// <summary>The stored-food buffer the crops' fields budget.</summary>
    public double FoodReserveDays { get; init; }

    /// <summary>The target-sized bedrooms the colony owes (SuiteWishes, #2939): each is a bedroom node of its own in the graph.</summary>
    public IReadOnlyList<SuiteWish> Suites { get; init; } = [];

    /// <summary>The colonists' mean mood level, 0 to 1, the mood price's baseline (#2975, #2878); unknown leaves the comfort tiers and the lost role effects unpriced.</summary>
    public Fact<double> ExpectedMood { get; init; }

    /// <summary>The standing rooms whose native role is not the one PredictRole gives their contents (GuardNativeRoles, #2898): a cost input and a split move of the regrouping.</summary>
    public IReadOnlyList<RoleMismatch> Mismatches { get; init; } = [];

    /// <summary>
    /// Marks the facts of a first plan, read from a survey, the colonists and the tier before the colony is: a station
    /// fact the colony has not read (the census, the orders, the stock) then leaves the graph at the seed rooms
    /// instead of failing the advice, and the rest grows on replan (#2958).
    /// </summary>
    public bool Derive { get; init; }

    /// <summary>The interior cells of the throne room a title asks (the growth's ThroneArea); 0 is none owed, unknown asks for none and says so.</summary>
    public Fact<int> ThroneArea { get; init; }

    /// <summary>The review's ShelterRetirable: the plan's shelter is no longer owed a node and leaves with the next plan.</summary>
    public bool RetireShelter { get; init; }

    /// <summary>The departments' room demand (the training range's stands and the ring's markers, #3003); its default asks for none.</summary>
    public RoomDemand Demand { get; init; } = new();

    /// <summary>The shapes of the child rooms the colony owes (ChildRoomShapes); the playroom, classroom and deathrest chamber among them are room nodes.</summary>
    public IReadOnlyList<ChildRoomShape> Child { get; init; } = [];

    /// <summary>
    /// The special rooms the plan is owed or holds: the day-1 shelter while nothing stands and the plan holds none, or
    /// while the plan's shelter is not retirable (RetireShelter), and the throne room the plan lacks, or the largest
    /// it holds, so a standing one is a node to pin (#3003). The shelter needs the colonists; unknown, the advice
    /// cannot size it.
    /// </summary>
    public SeedRooms SeedRoomsOf()
    {
        var s = new SeedRooms();
        var held = 0;
        foreach (var r in Plan.Rooms)
        {
            if (r.Role == PolicyFunctions.PlannedThrone)
            {
                var b = r.Region.Bounds();
                held = Math.Max(held, b.Width * b.Height);
            }
        }
        s = s with { ThroneArea = held };
        if (ThroneArea.TryGetValue(out var area) && area > held)
        {
            s = s with { ThroneArea = area };
        }
        if (Plan.Rooms.Any(r => r.Role == PolicyFunctions.PlannedShelter))
        {
            if (RetireShelter)
            {
                return s;
            }
        }
        else if (Built is not null && Built.Count > 0)
        {
            return s;
        }
        if (!Colonists.TryGetValue(out var colonists))
        {
            throw PolicyFunctions.UnknownStationFact("colonists");
        }
        return s with { Shelter = true, Colonists = (int)colonists, Cold = Survey.Cold, Hot = Survey.Hot };
    }

    /// <summary>
    /// The range stands and ring markers the graph holds nodes for (#3003): what the demand asks (RoomDemand.Ranges,
    /// Rings) or the plan's standing range and ring already hold, whichever is more, so a standing room is a node to
    /// pin and the demand only widens it. Zero asks for no such room.
    /// </summary>
    public (int Stands, int Markers) TrainingAsk()
    {
        var stands = Math.Min(Demand.Ranges, PolicyFunctions.RangeMaxStands);
        var markers = Math.Min(Demand.Rings, PolicyFunctions.RingMaxMarkers);
        var ranges = Plan.RangeRooms();
        if (ranges.Count > 0)
        {
            stands = Math.Max(stands, PolicyFunctions.RangeStandCount(ranges[0]));
        }
        var rings = Plan.RingRooms();
        if (rings.Count > 0)
        {
            markers = Math.Max(markers, rings[0].Markers);
        }
        return (stands, markers);
    }

    /// <summary>What the regrouping prices conflicts with: a colonist's work from the food census and crops, the mood model and the expected mood.</summary>
    public RegroupPrice RegroupPriceOf() => new() { Work = PolicyFunctions.StationWorkPrice(this), Mood = Catalog.Mood, Expected = ExpectedMood };

    /// <summary>Assembles the graph's input, or throws a StationFactsException naming the unknown fact.</summary>
    public StationGraphInput GraphInput()
    {
        var population = PolicyFunctions.StationPopulationOf(this);
        if (!Food.TryGetValue(out var supply))
        {
            throw PolicyFunctions.UnknownStationFact("food census");
        }
        var (nutrition, nutritionOk) = PolicyFunctions.HumanNutritionPerDay(supply);
        if (!nutritionOk)
        {
            throw PolicyFunctions.UnknownStationFact("nutrition per day");
        }
        if (!Orders.TryGetValue(out var orders))
        {
            throw PolicyFunctions.UnknownStationFact("work ledger orders");
        }
        if (!Stock.TryGetValue(out var stock))
        {
            throw PolicyFunctions.UnknownStationFact("resource demand");
        }
        var seed = SeedRoomsOf();
        var (stands, markers) = TrainingAsk();
        return new StationGraphInput
        {
            Catalog = Catalog,
            Furniture = Furniture,
            Dining = Dining,
            Items = Items,
            Recipes = Recipes,
            Population = population,
            NutritionPerDay = nutrition,
            Orders = orders,
            Stock = stock,
            StackLimit = StackLimit,
            Perishable = Perishable,
            Crops = Crops,
            FoodReserveDays = FoodReserveDays,
            Suites = Suites,
            Seed = seed,
            TrainingStands = stands,
            TrainingMarkers = markers,
            Graveyards = Math.Max(Demand.Graveyards, Plan.RoomsOf(PolicyFunctions.PlannedGraveyard).Count),
            Rooms = [.. PolicyFunctions.GearRoomNodes(Plan, Demand), .. PolicyFunctions.ChildRoomNodes(Plan, Child)],
            Races = Races,
            Pasture = Pasture,
        };
    }

    /// <summary>
    /// Names what the chain varies with: the graph's population, nutrition, orders and stock (or the unknown fact),
    /// the dining furniture and roof distance, and the plan with the rooms standing. Two facts with one key give one
    /// advice over one survey. The definitions (Catalog, Furniture, Items, Recipes and the stack and rot rows of the
    /// stock) are the load's, so the caller keys the load beside it.
    /// </summary>
    public string InputsKey()
    {
        var key = KeyText.Of(Dining, RoofDistance, Built, Pinned, OutdoorCurve, ExpectedMood, Mismatches, Derive, ThroneArea, RetireShelter, Demand, Child, Plan);
        try
        {
            var input = GraphInput();
            return key + KeyText.Of(input.Population, input.NutritionPerDay, input.Orders, input.Stock, input.FoodReserveDays, input.Crops, input.Suites, input.Pasture);
        }
        catch (StationFactsException e)
        {
            return key + e.Message;
        }
    }

    /// <summary>Tiles the embedding of the advice: what the tiler reads of the advice and the facts.</summary>
    internal TileInput TileInputOf(StationAdvice advice)
    {
        var steel = PolicyFunctions.SteelValueOrZero(this); // unknown stays zero: the airlocks and vents go unpriced
        var groups = advice.Clustering.Groups;
        var emb = advice.Embedding;
        var (held, blocks) = PolicyFunctions.HeldBlocks(groups.Count, emb.Held);
        return new TileInput
        {
            Groups = [.. groups, .. held],
            Embedding = emb,
            Blocks = [.. emb.Blocks, .. blocks],
            Catalog = Catalog,
            Zones = Plan.Zones,
            Reservations = Plan.Reserved(),
            Survey = Survey,
            OutdoorCurve = OutdoorCurve,
            RoofDistance = RoofDistance,
            FullRotRateC = FullRotRateC,
            SteelValue = steel,
            VentAvailable = VentAvailable,
            Light = PolicyFunctions.StationLightPrice(this, steel),
            Sites = PolicyFunctions.PowerSitesOf(advice.Graph.Sites, advice.Sites),
            Graph = advice.Graph,
            Furniture = Furniture,
            Dining = Dining,
            Comfort = advice.Comfort,
            NextRoomId = Plan.NextRoomId,
            Conduits = Plan.Conduits,
        };
    }

    /// <summary>
    /// The pinned standing rooms (StationFacts.Pinned) as the embedding holds them (EmbedInput.Standing): every built
    /// room that is pinned, whatever its role or whether a group carries it, bar the attached rooms and a room whose
    /// successor stands (the pair is one room, as in BuiltRegions).
    /// </summary>
    internal List<BuiltRegion> StandingRooms()
    {
        var built = Built ?? new HashSet<int>();
        var replaced = new HashSet<int>(Plan.Rooms.Where(r => built.Contains(r.Id) && r.Replaces != 0).Select(r => r.Replaces));
        var output = new List<BuiltRegion>();
        foreach (var r in Plan.Rooms)
        {
            if (built.Contains(r.Id) && Pinned.Contains(r.Id) && !r.Attached() && !replaced.Contains(r.Id))
            {
                output.Add(new BuiltRegion { Room = r.Id, Planned = r.Role, Region = r.Region, Pinned = true });
            }
        }
        return output;
    }
}

/// <summary>One room group with the built rooms matched to it. Rooms are the matched built rooms' ids and Capacity their summed area. Outgrown: the group needs more floor than its built rooms give. Climate is the cold room's climate answer, beside its cooling station: the watts of each opening to the outdoors (#2940); null for a group that does not require cold or when the curve or the game's numbers are unknown.</summary>
public sealed record GroupAdvice
{
    public RoomGroup Group { get; init; } = default!;
    public IReadOnlyList<int> Rooms { get; init; } = [];
    public int Capacity { get; init; }
    public bool Outgrown { get; init; }
    public GroupClimate? Climate { get; init; }
}

/// <summary>The chain's result over the live colony.</summary>
public sealed record StationAdvice
{
    public StationGraph Graph { get; init; } = default!;
    public Clustering Clustering { get; init; } = default!;
    public Embedding Embedding { get; init; } = default!;

    /// <summary>Places the power sites (PowerSiteNodes); a tiled plan stores them.</summary>
    public SiteEmbedding Sites { get; init; } = default!;

    public IReadOnlyList<GroupAdvice> Groups { get; init; } = [];

    /// <summary>The regrouping search's moves: each one applied, or the single reason none was.</summary>
    public IReadOnlyList<RegroupDecision> Regroup { get; init; } = [];

    /// <summary>The embedding tiled into a plan; null unless TileStations ran.</summary>
    public TileResult? Tiled { get; init; }

    /// <summary>The priced comfort tier of the bed and each worked bench (#2975), advisory; empty while the model or a fact is unknown.</summary>
    public IReadOnlyList<ComfortChoice> Comfort { get; init; } = [];

    /// <summary>Names the unknown fact that left a derive-time graph at the seed rooms (StationFacts.Derive); empty when the full graph was built.</summary>
    public string SeedOnly { get; init; } = "";
}

/// <summary>A planned room that stands as planned, with the role the game gives its furniture. Planned is the planned role of a room the native role does not tell apart (MatchedPlanned): the freezer and the butchery; empty otherwise. Pinned: the colony holds the room (TilePinned); the embedding keeps it where it stands. Set by the caller, never by BuiltRegions.</summary>
public sealed record BuiltRegion
{
    public int Room { get; init; }
    public RoomRole Role { get; init; }
    public PlannedRole Planned { get; init; }
    public Region Region { get; init; } = new();
    public bool Pinned { get; init; }

    /// <summary>The key a room is matched to groups under.</summary>
    internal string MatchKey() => Planned != PlannedRole.None ? Wire.Name(Planned) : Role.ToString();
}

/// <summary>The built rooms matched to one group: Rooms are the matched room ids, ascending; Capacity their summed floor.</summary>
public sealed record GroupRooms(int Group, IReadOnlyList<int> Rooms, int Capacity);

/// <summary>Ports <c>layout_station_plan.go</c>: the station chain over the live colony. The colony is read into StationFacts, the chain (BuildStationGraph, ClusterStations, Embed) runs over it, and each group is matched to the rooms already built for its predicted role.</summary>
public static partial class PolicyFunctions
{
    /// <summary>Names the unknown fact an advice could not be assembled for.</summary>
    internal static StationFactsException UnknownStationFact(string name) => new(name);

    /// <summary>The market value of the steel a site is priced in, 0 while unknown.</summary>
    internal static double SteelValueOrZero(StationFacts f)
    {
        try
        {
            return f.Items.MarketValue(SiteSteelUnit);
        }
        catch (DomainException)
        {
            return 0;
        }
    }

    /// <summary>The people the graph sizes beds for. Babies sleep in cribs, so they leave the colonists; the injured are colonists who need a medical bed now (IllPatients).</summary>
    public static StationPopulation StationPopulationOf(StationFacts f)
    {
        if (!f.Colonists.TryGetValue(out var colonists))
        {
            throw UnknownStationFact("colonists");
        }
        var (ill, ok) = IllPatients(f.Patients, f.Surgical);
        if (!ok)
        {
            throw UnknownStationFact("patients");
        }
        if (!f.Prisoners.TryGetValue(out var prisoners))
        {
            throw UnknownStationFact("prisoners");
        }
        if (!f.WorkPawns.TryGetValue(out var pawns))
        {
            throw UnknownStationFact("pawn stages");
        }
        var babies = BabyCount(pawns);
        return new StationPopulation { Colonists = Math.Max((int)colonists - babies, 0), Injured = ill.Count, Prisoners = prisoners, Babies = babies, Animals = f.Animals, Herds = f.Herds };
    }

    /// <summary>The pawns still in a crib: the newborn and baby stages ChildRoomNeeds gives a nursery bed. A pawn whose stage is unknown owes nothing, as there.</summary>
    public static int BabyCount(IReadOnlyList<WorkPawn> pawns)
    {
        var n = 0;
        foreach (var p in pawns)
        {
            if (!p.Biotech.TryGetValue(out var bt))
            {
                continue;
            }
            if (bt.DevelopmentalStage.TryGetValue(out var stage) && (stage == "Newborn" || stage == "Baby"))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>The nutrition the colonists eat a day: the sum of the consumers' rates. Ok is false when the census is incomplete or any rate is unknown.</summary>
    public static (double Total, bool Ok) HumanNutritionPerDay(FoodSupply supply)
    {
        if (supply.Complete.TryGetValue(out var complete) && !complete)
        {
            return (0, false);
        }
        var total = 0.0;
        foreach (var c in supply.Consumers)
        {
            if (!c.NutritionPerDay.TryGetValue(out var rate))
            {
                return (0, false);
            }
            total += rate;
        }
        return (total, true);
    }

    /// <summary>Reports a rebuild in flight: a successor pending in the plan or a pinned room not yet standing (as rebuildInFlight reads the census).</summary>
    internal static bool RebuildPending(StationFacts f)
    {
        if (SuccessorPending(f.Plan))
        {
            return true;
        }
        return f.Pinned.Any(id => f.Built is null || !f.Built.Contains(id));
    }

    /// <summary>
    /// TileStations is AdviseStations with the embedding tiled into a plan (TileEmbedding): the planner of the derive
    /// (DeriveLayoutPlan) and the replan (ReplanLayout). The review that only records advice calls AdviseStations. A
    /// tiling that fails throws its TileException with the advice (TileException.Advice) it stood on.
    /// </summary>
    public static StationAdvice TileStations(StationFacts f)
    {
        var output = AdviseStations(f);
        try
        {
            var tiled = TileEmbedding(f.TileInputOf(output));
            return output with { Tiled = tiled };
        }
        catch (TileException e)
        {
            e.Advice = output;
            throw;
        }
    }

    /// <summary>Runs the station chain over the live colony. It reads the plan and survey and changes neither.</summary>
    public static StationAdvice AdviseStations(StationFacts f)
    {
        if (f.Built is null)
        {
            throw UnknownStationFact("construction census");
        }
        StationGraph graph;
        var seedOnly = "";
        StationGraphInput? input = null;
        StationFactsException? err = null;
        try
        {
            input = f.GraphInput();
        }
        catch (StationFactsException e)
        {
            err = e;
        }
        if (string.IsNullOrEmpty(f.Dining.Table.Def))
        {
            err = UnknownStationFact("dining furniture");
        }
        if (err is null)
        {
            graph = BuildStationGraph(input!);
        }
        else if (f.Derive)
        {
            // A first plan has no census, orders or stock: the graph keeps the rooms that need none and the rest grows
            // on replan. Nothing is guessed for the facts left unread.
            seedOnly = err.Message;
            var seed = f.SeedRoomsOf();
            if (f.RoofDistance <= 0)
            {
                throw UnknownStationFact("roof support distance");
            }
            graph = SeedGraph(new StationGraphInput { Catalog = f.Catalog, Seed = seed });
            graph.Fields = [.. f.SeedFields()];
        }
        else
        {
            throw err;
        }
        var built = BuiltRegions(f.Plan, f.Built).Select(b => b with { Pinned = f.Pinned.Contains(b.Room) }).ToList();
        var regrouped = Regroup(new RegroupInput
        {
            Cluster = new ClusterInput { Graph = graph, Catalog = f.Catalog, RoofDistance = f.RoofDistance },
            Embed = new EmbedInput
            {
                Zones = f.Plan.Zones,
                Reservations = f.Plan.Reserved(),
                Survey = f.Survey,
                RoofDistance = f.RoofDistance,
                Built = built,
                Standing = f.StandingRooms(),
                InFlight = RebuildPending(f),
                RetiredGround = f.Plan.RetiredGround,
            },
            Price = f.RegroupPriceOf(),
            Mismatches = f.Mismatches,
        });
        var clustering = regrouped.Clustering;
        var embedIn = regrouped.Embed;
        var embedding = regrouped.Embedding;
        var (nodes, placed) = f.PowerSites(clustering.Groups, embedIn, embedding);
        var output = new StationAdvice
        {
            Graph = graph.WithSites(nodes),
            Clustering = clustering,
            Embedding = embedding,
            Regroup = regrouped.Decisions,
            SeedOnly = seedOnly,
            Sites = placed,
            Comfort = AdviseComfort(f, graph, SteelValueOrZero(f)),
        };
        var groups = new List<GroupAdvice>();
        foreach (var m in MatchGroupRooms(clustering.Groups, built))
        {
            var g = clustering.Groups[m.Group];
            groups.Add(new GroupAdvice { Group = g, Rooms = m.Rooms, Capacity = m.Capacity, Outgrown = GroupOutgrown(g, m), Climate = GroupClimateOf(f, g) });
        }
        return output with { Groups = groups };
    }

    /// <summary>
    /// The planned rooms matched to groups by their planned role, with the native role the game gives them: a freezer
    /// is a storeroom and a butchery reads as a kitchen, but each is matched only to the group that became it
    /// (PlannedRoleOfGroup), not to the plain storage or kitchen groups. A target-sized bedroom (#2939) is matched
    /// only to the suites, so a raised target outgrows its own suite and not an ordinary bedroom.
    /// </summary>
    internal static IReadOnlyDictionary<PlannedRole, RoomRole> MatchedPlanned => new Dictionary<PlannedRole, RoomRole>
    {
        [PlannedFreezer] = RoomRole.Storeroom,
        [PlannedButchery] = RoomRole.Kitchen,
        [PlannedSuite] = RoomRoleSuite,
    };

    private static readonly Lazy<HashSet<PlannedRole>> RoomNodeRolesSet = new(() =>
    {
        var nodes = new List<RoomNode>(OutskirtsRoomNodes());
        nodes.AddRange(TrainingRoomNodes(1, 1));
        nodes.AddRange(SeedRoomNodes(new SeedRooms { Shelter = true, Colonists = 1, ThroneArea = 1 }));
        nodes.Add(new RoomNode { Role = PlannedRole.Circulation });
        var output = new HashSet<PlannedRole>(nodes.Select(n => n.Role));
        foreach (var role in GearRooms.Concat(ChildNodeRoles))
        {
            output.Add(role);
        }
        return output;
    });

    /// <summary>
    /// The planned roles of the station-less room nodes of the graph (circulation, the outskirts rooms, the training
    /// rooms, the shelter and the throne room): a standing room of one is matched to the group of its node by planned
    /// role, outdoor or without a native role as it may be (#3003). The set is read off the node constructors, so a
    /// new node kind joins it by being built there.
    /// </summary>
    internal static HashSet<PlannedRole> RoomNodeRoles => RoomNodeRolesSet.Value;

    /// <summary>The key a group is matched to rooms under.</summary>
    internal static string MatchKeyOfGroup(RoomGroup g)
    {
        var (p, ok) = PlannedRoleOfGroup(g);
        if (ok && (MatchedPlanned.ContainsKey(p) || RoomNodeRoles.Contains(p)))
        {
            return Wire.Name(p);
        }
        return g.Role.ToString();
    }

    /// <summary>
    /// The plan's standing rooms that carry a native role (ModuleRoomRoles, and the freezer and butchery of
    /// MatchedPlanned) or a room node of the graph (RoomNodeRoles), by room id. Outdoor rooms and rooms with no native
    /// role are left out unless they are a room node's, and so is a room whose successor stands: the pair is one room.
    /// </summary>
    public static List<BuiltRegion> BuiltRegions(LayoutPlan plan, IReadOnlySet<int> built)
    {
        var output = new List<BuiltRegion>();
        var replaced = new HashSet<int>(plan.Rooms.Where(r => built.Contains(r.Id) && r.Replaces != 0).Select(r => r.Replaces));
        var matched = MatchedPlanned;
        var nodeRoles = RoomNodeRoles;
        foreach (var r in plan.Rooms)
        {
            var ok = ModuleRoomRoles.TryGetValue(r.Role, out var role);
            var planned = default(PlannedRole);
            if (matched.TryGetValue(r.Role, out var native))
            {
                role = native;
                ok = true;
                planned = r.Role;
            }
            if (nodeRoles.Contains(r.Role))
            {
                ok = true;
                planned = r.Role; // a room node's room: matched by planned role, outdoor or with no native role
            }
            else if (r.Outdoor)
            {
                continue;
            }
            if (!ok || !built.Contains(r.Id) || replaced.Contains(r.Id))
            {
                continue;
            }
            output.Add(new BuiltRegion { Room = r.Id, Role = role, Planned = planned, Region = r.Region });
        }
        return [.. output.OrderBy(b => b.Room)];
    }

    /// <summary>
    /// Matches built rooms to the groups of their role. Groups and rooms of a role are each taken largest first (a
    /// group by Area, a room by floor, ties by lower id); the n-th largest group gets the n-th largest room. A room
    /// left over when the role has more rooms than groups goes to the group of that role with the most floor still
    /// short (Area less the capacity it has, ties to the lower group id), so a role's rooms are always all assigned. A
    /// group of a role with no built room has no rooms. The result has one entry per group, in group order.
    /// </summary>
    public static List<GroupRooms> MatchGroupRooms(IReadOnlyList<RoomGroup> groups, IReadOnlyList<BuiltRegion> built)
    {
        var rooms = groups.Select(_ => new List<int>()).ToArray();
        var capacity = new int[groups.Count];
        var index = new Dictionary<int, int>();
        for (var i = 0; i < groups.Count; i++)
        {
            index[groups[i].Id] = i;
        }
        var byRole = new Dictionary<string, List<RoomGroup>>(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var k = MatchKeyOfGroup(g);
            if (!byRole.TryGetValue(k, out var list))
            {
                list = [];
                byRole[k] = list;
            }
            list.Add(g);
        }
        var roomsByRole = new Dictionary<string, List<BuiltRegion>>(StringComparer.Ordinal);
        foreach (var b in built)
        {
            var k = b.MatchKey();
            if (!roomsByRole.TryGetValue(k, out var list))
            {
                list = [];
                roomsByRole[k] = list;
            }
            list.Add(b);
        }
        foreach (var role in byRole.Keys.Order(StringComparer.Ordinal))
        {
            var gs = byRole[role].OrderByDescending(g => g.Area).ThenBy(g => g.Id).ToList();
            var rs = roomsByRole.GetValueOrDefault(role)?.OrderByDescending(r => r.Region.Area()).ThenBy(r => r.Room).ToList() ?? [];
            void Give(RoomGroup g, BuiltRegion r)
            {
                var at = index[g.Id];
                rooms[at].Add(r.Room);
                capacity[at] += r.Region.Area();
            }
            for (var i = 0; i < rs.Count; i++)
            {
                if (i < gs.Count)
                {
                    Give(gs[i], rs[i]);
                    continue;
                }
                var best = gs[0];
                foreach (var g in gs.Skip(1))
                {
                    var shortage = g.Area - capacity[index[g.Id]];
                    var bestShort = best.Area - capacity[index[best.Id]];
                    if (shortage > bestShort || (shortage == bestShort && g.Id < best.Id))
                    {
                        best = g;
                    }
                }
                Give(best, rs[i]);
            }
        }
        var output = new List<GroupRooms>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            rooms[i].Sort();
            output.Add(new GroupRooms(groups[i].Id, rooms[i], capacity[i]));
        }
        return output;
    }

    /// <summary>Whether a group needs more floor than its matched built rooms give. A group with no built room has nothing to outgrow.</summary>
    public static bool GroupOutgrown(RoomGroup g, GroupRooms m) => m.Rooms.Count > 0 && g.Area > m.Capacity;

    /// <summary>The groups and blocks of the embedding's held rooms, held room k being group groups+k: a group of its planned role with no stations, so the tiler tiles the standing room where it stands.</summary>
    internal static (List<RoomGroup> Groups, List<EmbeddedBlock> Blocks) HeldBlocks(int groups, IReadOnlyList<HeldRoom> held)
    {
        var gs = new List<RoomGroup>();
        var blocks = new List<EmbeddedBlock>();
        for (var k = 0; k < held.Count; k++)
        {
            var h = held[k];
            var id = groups + k;
            gs.Add(new RoomGroup { Id = id, Planned = h.Role, Area = h.Interior.Width * h.Interior.Height });
            blocks.Add(new EmbeddedBlock { Group = id, Interior = h.Interior, Built = h.Room });
        }
        return (gs, blocks);
    }
}

/// <summary>Builds the canonical text a cache key is made of: every public member of a value in name order, collections in element order (sets and maps sorted), so equal values give equal text and no pointer or iteration order enters.</summary>
internal static class KeyText
{
    internal static string Of(params object?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            Write(sb, p, 0);
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v, int depth)
    {
        if (depth > 24)
        {
            sb.Append("...");
            return;
        }
        switch (v)
        {
            case null:
                sb.Append("nil");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case bool or Enum:
                sb.Append(v);
                return;
            case IFormattable f:
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
                return;
        }
        var type = v.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Fact<>))
        {
            var known = (bool)type.GetProperty("IsKnown")!.GetValue(v)!;
            sb.Append(known ? "known(" : "unknown(");
            if (known)
            {
                Write(sb, type.GetProperty("ValueOrDefault")!.GetValue(v), depth + 1);
            }
            sb.Append(')');
            return;
        }
        if (v is IEnumerable items)
        {
            var parts = new List<string>();
            var ordered = v is ISet<object> || IsSet(type) || IsMap(type);
            foreach (var item in items)
            {
                var inner = new StringBuilder();
                Write(inner, item, depth + 1);
                parts.Add(inner.ToString());
            }
            if (ordered)
            {
                parts.Sort(StringComparer.Ordinal);
            }
            sb.Append('[').Append(string.Join(',', parts)).Append(']');
            return;
        }
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null && p.Name != "EqualityContract")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        if (props.Count == 0)
        {
            sb.Append(v);
            return;
        }
        sb.Append('{');
        foreach (var p in props)
        {
            sb.Append(p.Name).Append('=');
            Write(sb, p.GetValue(v), depth + 1);
            sb.Append(',');
        }
        sb.Append('}');
    }

    private static bool IsSet(Type t) => t.GetInterfaces().Append(t).Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ISet<>) || i.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)));

    private static bool IsMap(Type t) => t.GetInterfaces().Append(t).Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
}

public sealed partial class StationGraph
{
    /// <summary>Go's value copy of the graph with its power sites replaced: the other slices are shared, as a Go struct copy shares them.</summary>
    internal StationGraph WithSites(IEnumerable<SiteNode> sites)
    {
        var copy = (StationGraph)MemberwiseClone();
        copy.Sites = [.. sites];
        return copy;
    }
}
