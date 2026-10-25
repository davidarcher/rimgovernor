using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the advice reads, each part from the model that owns it: the catalog rows (Catalog, Furniture, Dining, Items,
/// Recipes, StackLimit, Perishable), the population censuses, the colonists' food census, the work ledger's orders,
/// the resource demand and the saved plan with the survey it was grown over. Unknown facts are never zero: a fact the
/// colony has not read fails the advice with RequiredFactException.
/// </summary>
public sealed record StationFacts
{
    public StationCatalog Catalog { get; init; } = default!;
    public RoomFurniture Furniture { get; init; } = default!;

    /// <summary>The catalog's dining furniture, carried by the rooms census; while that is unread it stays empty, an unknown fact that fails the advice.</summary>
    public DiningFurniture Dining { get; init; } = new();

    public ItemFacts Items { get; init; } = default!;
    public IReadOnlyDictionary<string, RecipeFlow> Recipes { get; init; } = new Dictionary<string, RecipeFlow>();

    /// <summary>StackLimit and Perishable cover every resource of Stock and of the recipes' items.</summary>
    public IReadOnlyDictionary<Resource, long> StackLimit { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyDictionary<Resource, bool> Perishable { get; init; } = new Dictionary<Resource, bool>();

    /// <summary>The colony's people.</summary>
    public long Colonists { get; init; }

    /// <summary>Patients and Surgical size the hospital ward as SelectHospitalBed does.</summary>
    public EquatableArray<CarePawn> Patients { get; init; }

    public IReadOnlyList<PawnId> Surgical { get; init; } = [];

    /// <summary>The goals' hospital beds and nursery: floors under the injured and baby counts the graph sizes beds for (#3496).</summary>
    public GoalBuildDemand GoalBuild { get; init; } = GoalBuildDemand.None;

    /// <summary>The held prisoners.</summary>
    public int Prisoners { get; init; }

    /// <summary>The pen herd by race, Races the animal race rows and Pasture the worst-quadrum growth per terrain: the pen nodes' products and hunger (#2996). Herds empty asks for no pen.</summary>
    public IReadOnlyList<StationHerd> Herds { get; init; } = [];

    public AnimalRaceCatalog Races { get; init; } = default!;
    public PastureTable Pasture { get; init; } = new(new Dictionary<string, double>());

    /// <summary>Says who is a baby.</summary>
    public WorkPawnsRead WorkPawns { get; init; } = new WorkPawnsRead([]);

    /// <summary>The colony's rest schedule from its workers' timetables (#3750); null while unread, which fails the disturbed-sleep price that needs it.</summary>
    public RestSchedule? Rest { get; init; }

    /// <summary>The colonists' food census; its consumers' rates sum to the nutrition a day.</summary>
    public FoodSupply Food { get; init; } = new() { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } };

    /// <summary>The work ledger as the latest review filed it: its wanted orders are read once a review reconciled it or found no planner declaring (LedgerOrders).</summary>
    public LedgerView Ledger { get; init; } = WorkRules.NewLedgerView(WorkRules.LedgerViewNone);

    /// <summary>The resource demand's stock levels (DerivedDemand.Needs).</summary>
    public IReadOnlyDictionary<Resource, long> Stock { get => field ?? throw new RequiredFactException("resource demand"); init; }

    /// <summary>The game's roof support distance.</summary>
    public double RoofDistance { get; init; }

    /// <summary>The saved plan the advice reads zones, reservations and built rooms from.</summary>
    public LayoutPlan Plan { get; init; } = new();

    /// <summary>The ids of the plan's rooms that stand as planned (BuiltRooms); null while the construction census is unknown.</summary>
    public IReadOnlySet<int>? Built { get; init; }

    /// <summary>What of each planned room's own shell stands (LayoutPlan.ShellStanding), read with Built: the spend a bed merge charges for a committed room it gives up (#3675).</summary>
    public IReadOnlyDictionary<int, ShellPieces> ShellStanding { get; init; } = new Dictionary<int, ShellPieces>();

    /// <summary>The rooms the replan holds where they stand (TilePinned): a pinned room stands for the embedding and moves only at a built room's switching price, and one not yet built is a rebuild in flight, which holds every built room.</summary>
    public IReadOnlySet<int> Pinned { get; init; } = new HashSet<int>();

    /// <summary>The rooms the colony holds (HeldRooms): a held standing room stays in the embedding as an obstacle whether or not a group carries it; a pinned room no group carries and the colony does not hold leaves the plan.</summary>
    public IReadOnlySet<int> Held { get; init; } = new HashSet<int>();

    /// <summary>The map the plan was grown over.</summary>
    public MapSurvey Survey { get; init; } = default!;

    /// <summary>The seasonal outdoor temperature curve (twelve means, C); required by climate advice alongside the catalog's full-rot temperature.</summary>
    public EquatableArray<double> OutdoorCurve { get => ClimateRules.RequireOutdoorCurve(field); init; }

    public double FullRotRateC { get; init; }

    /// <summary>Whether the vent is researched (#2915); unknown or false leaves vents out of the tiled plan.</summary>
    public bool VentAvailable { get; init; }

    /// <summary>The crops the colony grows (the caller's choice, not the chain's): each edible crop with a known capacity target is a field node (#2920).</summary>
    public IReadOnlyList<CropChoice> Crops { get; init; } = [];

    /// <summary>The stored-food buffer the crops' fields budget.</summary>
    public double FoodReserveDays { get; init; }

    /// <summary>The target-sized bedrooms the colony owes (SuiteWishes, #2939): each is a bedroom node of its own in the graph.</summary>
    public IReadOnlyList<SuiteWish> Suites { get; init; } = [];

    /// <summary>The colonists' mean mood level, 0 to 1, the mood price's baseline (#2975, #2878); unknown leaves the comfort tiers and the lost role effects unpriced.</summary>
    public StationMood ExpectedMood { get; init; } = new StationMood.NoMood();

    /// <summary>What the colony reads for the bed merge's price (#3658); its default reads nothing, which fails a chain with a free-bed pool naming the unread fact (#3747).</summary>
    public BedMergeFacts BedMerge { get; init; } = new BedMergeFacts.Unread("sleeper_mood");

    /// <summary>The capability-days of building each work station the catalog offers (labor plus material, LayoutClusterRules.PiecePrice), by def; a station absent is unread (#3659, GraphBuilder.HostOf).</summary>
    public IReadOnlyDictionary<string, double> HostBuild { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// The bed merge's price (#3658): the colony's facts, the outdoor curve, the work-day (StationWorkDayOf), the colonists able to build (#3748), the bed slots of the plan's largest barracks as the incumbent, and the plan's committed barracks and
    /// bedrooms with what of each shell stands (#3661, #3675): a room standing or under construction (StandingIds), a
    /// superseded one counted once, as its successor. A room on paper alone is not committed.
    /// </summary>
    public BedMergePrice BedMergePriceOf()
    {
        var standing = StandingIds() ?? new HashSet<int>();
        return new()
        {
            Facts = BedMerge,
            OutdoorCurve = OutdoorCurve,
            WorkDay = LayoutLightRules.StationWorkDayOf(this),
            Builders = LayoutClusterRules.BuildersOf(WorkPawns),
            Incumbent = Furniture is null ? 0 : Plan.RoomsOf(PlannedRole.Barracks).Select(r => SleepingRules.BedSlotsOf(Plan, r, Furniture)?.Count ?? 0).DefaultIfEmpty(0).Max(),
            Committed = EquatableArray.From(Plan.RoomsOf(PlannedRole.Barracks).Concat(Plan.RoomsOf(PlannedRole.Bedroom))
                .Where(r => standing.Contains(r.Id) && !Plan.Rooms.Any(o => o.Replaces == r.Id && standing.Contains(o.Id)))
                .Select(r => ShellStanding.GetValueOrDefault(r.Id))),
        };
    }

    /// <summary>The standing rooms whose native role is not the one PredictRole gives their contents (GuardNativeRoles, #2898): a cost input and a split move of the regrouping.</summary>
    public IReadOnlyList<RoleMismatch> Mismatches { get; init; } = [];

    /// <summary>
    /// Marks the facts of a first plan (DeriveLayoutPlan): the power sites its planned demand needs are placed beside
    /// its rooms (PowerSites). A station fact the colony has not read fails the advice as on a replan; the first plan
    /// then waits for it with the clock held (#3652).
    /// </summary>
    public bool Derive { get; init; }

    /// <summary>The interior cells of the throne room a title asks (the growth's ThroneArea); 0 is none owed.</summary>
    public int ThroneArea { get; init; }

    /// <summary>The departments' room demand (the training range's stands and the ring's markers, #3003); its default asks for none.</summary>
    public RoomDemand Demand { get; init; } = new();

    /// <summary>The pens the herd asks for (HerdPlan.PenWants): each unit short of its cells gets a pen node. Empty while the animal census or the pasture table is unread, which asks for none.</summary>
    public IReadOnlyList<PenWant> Pens { get; init; } = [];
    /// <summary>The shapes of the child rooms the colony owes (ChildRoomShapes); the classroom and deathrest chamber among them are room nodes.</summary>
    public IReadOnlyList<ChildRoomShape> Child { get; init; } = [];

    /// <summary>
    /// The special rooms the plan is owed or holds: the throne room the plan lacks, or the largest it holds, so a
    /// standing one is a node to pin (#3003). No shelter is seeded (#3659): a first plan's rooms are the station graph's.
    /// </summary>
    public SeedRooms SeedRoomsOf()
    {
        var held = 0;
        foreach (var r in Plan.Rooms)
        {
            if (r.Role == LayoutThroneRules.PlannedThrone)
            {
                var b = r.Region.Bounds();
                held = Math.Max(held, b.Width * b.Height);
            }
        }
        return new SeedRooms { ThroneArea = Math.Max(ThroneArea, held) };
    }

    /// <summary>
    /// The range stands and ring markers the graph holds nodes for (#3003): what the demand asks (RoomDemand.Ranges,
    /// Rings) or the plan's standing range and ring already hold, whichever is more, so a standing room is a node to
    /// pin and the demand only widens it. Zero asks for no such room.
    /// </summary>
    public (int Stands, int Markers) TrainingAsk()
    {
        var stands = Math.Min(Demand.Ranges, LayoutRangeRules.RangeMaxStands);
        var markers = Math.Min(Demand.Rings, LayoutRingRules.RingMaxMarkers);
        var ranges = Plan.RangeRooms();
        if (ranges.Count > 0)
        {
            stands = Math.Max(stands, PlannedRoom.RangeStandCount(ranges[0]));
        }
        var rings = Plan.RingRooms();
        if (rings.Count > 0)
        {
            markers = Math.Max(markers, rings[0].Markers);
        }
        return (stands, markers);
    }

    /// <summary>What the regrouping prices conflicts with: a colonist's work from the food census and crops, the mood model and the expected mood.</summary>
    public RegroupPrice RegroupPriceOf() => new() { Work = LayoutRegroupRules.StationWorkPrice(this), Mood = Catalog.Mood, Expected = ExpectedMood };

    /// <summary>The room nodes the facts ask beyond the station chain's: the tomb and morgue, the child rooms, the yards, and the pens.</summary>
    internal IReadOnlyList<RoomNode> RoomNodes() =>
        [.. LayoutPlan.OutskirtsRoomNodes(Plan, Demand), .. LayoutPlan.ChildRoomNodes(Plan, Child), .. LayoutPlan.YardRoomNodes(Plan, Demand), .. LayoutPlan.HerdRoomNodes(Plan, Pens)];

    /// <summary>
    /// The work ledger's wanted orders: those of a reconciled ledger, or none for an inactive one (no planner declares
    /// into it). Before any review or with the bench readback unread it throws a RequiredFactException.
    /// </summary>
    public IReadOnlyList<OrderSpec> LedgerOrders() => Ledger.Status is WorkRules.LedgerViewReconciled or WorkRules.LedgerViewInactive
        ? Ledger.Orders
        : throw new RequiredFactException("work ledger orders");

    /// <summary>Assembles the graph's input, or throws a RequiredFactException naming the unknown fact.</summary>
    public StationGraphInput GraphInput()
    {
        var population = LayoutStationPlanRules.StationPopulationOf(this);
        var nutrition = LayoutStationPlanRules.HumanNutritionPerDay(Food);
        var orders = LedgerOrders();
        var stock = Stock;
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
            Rooms = RoomNodes(),
            Races = Races,
            Pasture = Pasture,
            HostBuild = HostBuild,
        };
    }

    /// <summary>
    /// Names the structural facts a replan varies with (#3739): the graph's population, suites and pasture (or the
    /// unknown fact), the dining furniture and roof distance, the demand, and the plan with the rooms standing,
    /// pinned and held. The facts that drift by the hour (stock, orders, expected mood, nutrition, food reserve, crops, the
    /// outdoor curve) feed the chain but are left out: the hourly check's daily floor (LayoutPlanning.ReplanDaily)
    /// carries them to the plan. The definitions (Catalog, Furniture, Items, Recipes) are the load's, so the caller
    /// keys the load beside it.
    /// </summary>
    public string InputsKey()
    {
        var sb = new StringBuilder();
        foreach (var (_, value) in InputsKeyParts())
        {
            sb.Append(value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The named parts of InputsKey, what a replan varies with, each keyed on its own so an hourly check can say which
    /// of them drifted since the last replan (the layout_replan_tiled row's inputs_changed).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> InputsKeyParts()
    {
        var parts = new List<KeyValuePair<string, string>>
        {
            new("dining", KeyText.Of(Dining)),
            new("roof_distance", KeyText.Of(RoofDistance)),
            new("built", KeyText.Of(Built)),
            new("pinned", KeyText.Of(Pinned)),
            new("held", KeyText.Of(Held)),
            new("mismatches", KeyText.Of(Mismatches)),
            new("derive", KeyText.Of(Derive)),
            new("throne_area", KeyText.Of(ThroneArea)),
            new("demand", KeyText.Of(Demand)),
            new("pens", KeyText.Of(Pens)),
            new("child", KeyText.Of(Child)),
            new("plan", KeyText.Of(Plan)),
            new("host_build", KeyText.Of(HostBuild)),
            new("rest", KeyText.Of(Rest)),
        };
        try
        {
            var input = GraphInput();
            parts.Add(new("population", KeyText.Of(input.Population)));
            parts.Add(new("suites", KeyText.Of(input.Suites)));
            parts.Add(new("pasture", KeyText.Of(input.Pasture)));
            parts.Add(new("barracks", BarracksKey(input)));
        }
        catch (RequiredFactException e)
        {
            parts.Add(new("graph", e.Message));
        }
        return parts;
    }

    /// <summary>
    /// The barracks size and room areas the chain's clustering gives the free-bed pool (#3658, #3748,
    /// LayoutClusterRules.BedMergeChoiceOf over the graph's bed nodes alone), so the replan key changes when the priced
    /// choice does and not with every drift of a gain; nil when no pool is priced, a required price fact is unread (#3747)
    /// or the beds cannot be clustered.
    /// </summary>
    private string BarracksKey(StationGraphInput input)
    {
        try
        {
            var choice = LayoutClusterRules.BedMergeChoiceOf(new ClusterInput { Graph = StationGraph.SleepGraph(input), Catalog = Catalog, RoofDistance = RoofDistance, BedMerge = BedMergePriceOf() });
            return choice is null ? KeyText.Of([null]) : KeyText.Of(choice.Size, choice.Area, choice.SingleArea);
        }
        catch (DomainException)
        {
            return KeyText.Of([null]);
        }
    }

    /// <summary>Tiles the embedding of the advice: what the tiler reads of the advice and the facts.</summary>
    internal TileInput TileInputOf(StationAdvice advice)
    {
        var steel = LayoutStationPlanRules.SteelValueOrZero(this); // unknown stays zero: the airlocks and vents go unpriced
        var groups = advice.Clustering.Groups;
        var emb = advice.Embedding;
        var (held, blocks) = LayoutStationPlanRules.HeldBlocks(groups.Count, emb.Held);
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
            FullRotRateC = FullRotRateC,
            SteelValue = steel,
            VentAvailable = VentAvailable,
            Light = LayoutLightRules.StationLightPrice(this, steel),
            Sites = LayoutPowerNodesRules.PowerSitesOf(advice.Graph.Sites, advice.Sites),
            Graph = advice.Graph,
            Furniture = Furniture,
            Dining = Dining,
            Comfort = advice.Comfort,
            NextRoomId = Plan.NextRoomId,
            Conduits = Plan.Conduits,
            StandingDoors = [.. emb.Blocks.Concat(blocks).Where(b => b.Built != 0).SelectMany(b => Plan.Rooms.Where(r => r.Id == b.Built)).SelectMany(Plan.ShellDoors)],
        };
    }

    /// <summary>
    /// The rooms that stand for the embedding: the built rooms and the rooms the colony holds that do not stand yet (a
    /// journal plan is building them: pinned, no successor pair). A room under construction is as much the room its node
    /// asks for as a finished one, and siting another beside it would owe two. Null while the construction census is unknown.
    /// </summary>
    public IReadOnlySet<int>? StandingIds()
    {
        if (Built is null)
        {
            return null;
        }
        var ids = new HashSet<int>(Built);
        foreach (var r in Plan.Rooms)
        {
            if (Pinned.Contains(r.Id) && r.Replaces == 0 && !r.Attached())
            {
                ids.Add(r.Id);
            }
        }
        return ids;
    }

    /// <summary>
    /// The held standing rooms (StationFacts.Held) as the embedding holds them (EmbedInput.Standing): every standing
    /// room the colony holds, whatever its role or whether a group carries it, bar the attached rooms and a room whose
    /// successor stands (the pair is one room, as in BuiltRegions). A room only started (pinned, not held) is no
    /// obstacle: with no group to carry it, it leaves the plan.
    /// </summary>
    internal List<BuiltRegion> StandingRooms()
    {
        var built = StandingIds() ?? new HashSet<int>();
        var replaced = new HashSet<int>(Plan.Rooms.Where(r => built.Contains(r.Id) && r.Replaces != 0).Select(r => r.Replaces));
        var output = new List<BuiltRegion>();
        foreach (var r in Plan.Rooms)
        {
            if (built.Contains(r.Id) && Held.Contains(r.Id) && !r.Attached() && !replaced.Contains(r.Id))
            {
                output.Add(new BuiltRegion { Room = r.Id, Planned = r.Role, Region = r.Region });
            }
        }
        return output;
    }

    /// <summary>
    /// The held plan's unbuilt, unpinned rooms as the embedding keeps them (EmbedInput.Prior, #3740): every room that
    /// does not stand (StandingIds) and the colony does not hold (Pinned), bar the attached rooms and the successors (a
    /// successor is the relocation's, priced by the switching price), keyed as BuiltRegions keys a room.
    /// </summary>
    internal List<BuiltRegion> PriorRooms()
    {
        var standing = StandingIds() ?? new HashSet<int>();
        var unbuilt = new HashSet<int>(Plan.Rooms.Where(r => !standing.Contains(r.Id) && !Pinned.Contains(r.Id) && !r.Attached() && r.Replaces == 0).Select(r => r.Id));
        return LayoutPlan.BuiltRegions(Plan, unbuilt);
    }
}

/// <summary>One room group with the built rooms matched to it. Rooms are the matched built rooms' ids and Capacity their summed area. Outgrown: the group needs more floor than its built rooms give. Climate is the cold room's climate answer, beside its cooling station: the watts of each opening to the outdoors (#2940); null for a group that does not require cold or when the game's numbers cannot be priced; the seasonal curve is required.</summary>
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
}

/// <summary>A planned room that stands as planned, with the role the game gives its furniture. Planned is the planned role of a room the native role does not tell apart (MatchedPlanned): the freezer and the butchery; empty otherwise.</summary>
public sealed record BuiltRegion
{
    public int Room { get; init; }
    public PlanRole Role { get; init; }
    public PlannedRole Planned { get; init; }
    public Region Region { get; init; } = new();

    /// <summary>The herd key of a pen.</summary>
    public string Herd { get; init; } = string.Empty;

    /// <summary>The key a room is matched to groups under.</summary>
    internal string MatchKey() => Planned != PlannedRole.None ? LayoutStationPlanRules.MatchKeyOf(Planned, Herd) : Role.ToString();
}

/// <summary>The built rooms matched to one group: Rooms are the matched room ids, ascending; Capacity their summed floor.</summary>
public sealed record GroupRooms(int Group, IReadOnlyList<int> Rooms, int Capacity);

/// <summary>The station chain over the live colony. The colony is read into StationFacts, the chain (BuildStationGraph, ClusterStations, Embed) runs over it, and each group is matched to the rooms already built for its predicted role.</summary>
public static partial class LayoutStationPlanRules
{
    /// <summary>The market value of the steel a site is priced in, 0 while unknown.</summary>
    internal static double SteelValueOrZero(StationFacts f)
    {
        try
        {
            return f.Items.MarketValue(SiteRules.SiteSteelUnit);
        }
        catch (DomainException)
        {
            return 0;
        }
    }

    /// <summary>The cribs a Nursery goal holds in the graph (#3496): the game's two, only while the catalog has a crib to place (without Biotech the goal asks for nothing).</summary>
    private static int NurseryFloor(StationFacts f) =>
        f.GoalBuild.Rooms.Items.Contains(RoomRole.Nursery) && f.Catalog.Contributors(RoomRole.Nursery).Count > 0 ? ChildRoomsRules.MinNurseryBeds : 0;

    /// <summary>The people the graph sizes beds for. Babies sleep in cribs, so they leave the colonists; the injured are colonists who need a medical bed now (IllPatients).</summary>
    public static StationPopulation StationPopulationOf(StationFacts f)
    {
        var colonists = f.Colonists;
        var ill = HospitalRules.IllPatients(f.Patients, f.Surgical);
        var prisoners = f.Prisoners;
        var pawns = f.WorkPawns.Pawns;
        var babies = BabyCount(pawns);
        return new StationPopulation { Colonists = Math.Max((int)colonists - babies, 0), Injured = Math.Max(ill.Count, f.GoalBuild.HospitalBeds), Prisoners = prisoners, Babies = Math.Max(babies, NurseryFloor(f)), Herds = f.Herds };
    }

    /// <summary>The pawns still in a crib: the newborn and baby stages ChildRoomNeeds gives a nursery bed. A pawn whose stage is unknown owes nothing, as there.</summary>
    public static int BabyCount(IReadOnlyList<WorkPawn> pawns)
    {
        var n = 0;
        foreach (var p in pawns)
        {
            if (p.Biotech is not PawnBiotechStatus.Active { Facts: var bt })
            {
                continue;
            }
            if (bt.DevelopmentalStage is RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Newborn or RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Baby)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>The nutrition the colonists eat a day: the sum of the consumers' rates.</summary>
    public static double HumanNutritionPerDay(FoodSupply supply)
    {
        var total = 0.0;
        foreach (var c in supply.Consumers)
        {
            total += c.NutritionPerDay;
        }
        return total;
    }

    /// <summary>Reports a rebuild in flight: a successor pending in the plan or a pinned room not yet standing (as rebuildInFlight reads the census).</summary>
    internal static bool RebuildPending(StationFacts f)
    {
        if (LayoutPlan.SuccessorPending(f.Plan))
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
    public static StationAdvice TileStations(StationFacts f) => TileStations(f, AdviseStations(f));

    /// <summary>TileStations over advice already made (the replan runs the chain itself, once, before the tiler).</summary>
    public static StationAdvice TileStations(StationFacts f, StationAdvice output)
    {
        try
        {
            var tiled = LayoutTileRules.TileEmbedding(f.TileInputOf(output));
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
            throw new RequiredFactException("construction census");
        }
        // Nothing is guessed for a fact left unread: the advice fails naming it.
        var input = f.GraphInput();
        if (string.IsNullOrEmpty(f.Dining.Table.Def))
        {
            throw new RequiredFactException("dining furniture");
        }
        if (f.Derive && f.RoofDistance <= 0)
        {
            throw new RequiredFactException("roof support distance");
        }
        var graph = StationGraph.BuildStationGraph(input);
        var built = LayoutPlan.BuiltRegions(f.Plan, f.StandingIds()!);
        var regrouped = LayoutRegroupRules.Regroup(new RegroupInput
        {
            Cluster = new ClusterInput { Graph = graph, Catalog = f.Catalog, RoofDistance = f.RoofDistance, BedMerge = f.BedMergePriceOf(), Rest = f.Rest },
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
                Prior = f.PriorRooms(),
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
            Sites = placed,
            Comfort = LayoutComfortChooseRules.AdviseComfort(f, graph, SteelValueOrZero(f)),
        };
        var groups = new List<GroupAdvice>();
        foreach (var m in MatchGroupRooms(clustering.Groups, built))
        {
            var g = clustering.Groups[m.Group];
            groups.Add(new GroupAdvice { Group = g, Rooms = m.Rooms, Capacity = m.Capacity, Outgrown = GroupOutgrown(g, m), Climate = ClimateRules.GroupClimateOf(f, g) });
        }
        return output with { Groups = groups };
    }

    /// <summary>
    /// The planned rooms matched to groups by their planned role, with the native role the game gives them: a freezer
    /// is a storeroom and a butchery reads as a kitchen, but each is matched only to the group that became it
    /// (PlannedRoleOfGroup), not to the plain storage or kitchen groups. A target-sized bedroom (#2939) is matched
    /// only to the suites, so a raised target outgrows its own suite and not an ordinary bedroom.
    /// </summary>
    internal static IReadOnlyDictionary<PlannedRole, PlanRole> MatchedPlanned => new Dictionary<PlannedRole, PlanRole>
    {
        [LayoutSurveyRules.PlannedFreezer] = RoomRole.Storeroom,
        [LayoutSurveyRules.PlannedButchery] = RoomRole.Kitchen,
        [LayoutTripsRules.PlannedSuite] = LayoutTripsRules.RoomRoleSuite,
    };

    private static readonly Lazy<HashSet<PlannedRole>> RoomNodeRolesSet = new(() =>
    {
        var nodes = new List<RoomNode>(LayoutPlan.OutskirtsRoomNodes(new LayoutPlan(), new RoomDemand { Tomb = 1, Morgue = 1 }));
        nodes.AddRange(LayoutTrainingNodesRules.TrainingRoomNodes(1, 1));
        nodes.AddRange(LayoutPlan.YardRoomNodes(new LayoutPlan(), new RoomDemand()));
        nodes.Add(new RoomNode { Role = PlannedRole.Pen });
        nodes.AddRange(LayoutTripsRules.SeedRoomNodes(new SeedRooms { ThroneArea = 1 }));
        nodes.Add(new RoomNode { Role = PlannedRole.Circulation });
        var output = new HashSet<PlannedRole>(nodes.Select(n => n.Role));
        foreach (var role in LayoutChildRoomsRules.ChildNodeRoles)
        {
            output.Add(role);
        }
        return output;
    });

    /// <summary>
    /// The planned roles of the station-less room nodes of the graph (circulation, the outskirts rooms, the training
    /// rooms and the throne room): a standing room of one is matched to the group of its node by planned
    /// role, outdoor or without a native role as it may be (#3003). The set is read off the node constructors, so a
    /// new node kind joins it by being built there.
    /// </summary>
    internal static HashSet<PlannedRole> RoomNodeRoles => RoomNodeRolesSet.Value;

    /// <summary>The key a planned role is matched under: a pen's carries its herd, so a unit's pens are matched to the unit's nodes.</summary>
    internal static string MatchKeyOf(PlannedRole planned, string herd) => planned == PlannedRole.Pen ? $"{Wire.Name(planned)}/{herd}" : Wire.Name(planned);

    /// <summary>
    /// The key a group is matched to rooms under: its predicted role, or the native role of the planned room its
    /// contents decide (RoomGroup.Planned), so a lone crafting spot planned as a workshop keys as the standing
    /// workshop does (BuiltRegion.MatchKey) though the game scores it a generic room (#3659).
    /// </summary>
    internal static string MatchKeyOfGroup(RoomGroup g)
    {
        var (p, ok) = LayoutTileRules.PlannedRoleOfGroup(g);
        if (ok && (MatchedPlanned.ContainsKey(p) || RoomNodeRoles.Contains(p)))
        {
            return MatchKeyOf(p, g.Herd);
        }
        if (ok && g.Planned != PlannedRole.None && InteriorRules.ModuleRoomRoles.TryGetValue(p, out var native))
        {
            return native.ToString();
        }
        return g.Role.ToString();
    }

    /// <summary>
    /// Matches built rooms to the groups of their role. A group that is a standing suite (Kept) first takes a room of
    /// its own floor (largest group first, the lower room id), so a new, larger suite wish does not claim another
    /// suite's smaller standing room (#3022). The rest of a role's groups and rooms are each taken largest first (a
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
            var open = new List<RoomGroup>();
            foreach (var g in gs)
            {
                var own = g.Kept > 0 ? rs.FindIndex(r => r.Region.Area() == g.Kept) : -1;
                if (own < 0)
                {
                    open.Add(g);
                    continue;
                }
                Give(g, rs[own]);
                rs.RemoveAt(own);
            }
            for (var i = 0; i < rs.Count; i++)
            {
                if (i < open.Count)
                {
                    Give(open[i], rs[i]);
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

    /// <summary>
    /// Whether a group needs at least one station's cells (StationCells) more floor than its matched built rooms give
    /// (#3022): a smaller shortfall, such as a suite target raised by a cell, does not replace a standing room. A group
    /// with no built room has nothing to outgrow.
    /// </summary>
    public static bool GroupOutgrown(RoomGroup g, GroupRooms m) => m.Rooms.Count > 0 && g.Area - m.Capacity >= StationCells(g);

    /// <summary>The cells one of the group's stations stands on (Footprint over the station count), at least 1; 1 for a group with no stations.</summary>
    internal static int StationCells(RoomGroup g)
    {
        var stations = g.Stations.Sum(s => s.Count);
        return stations > 0 ? Math.Max(1, g.Footprint / stations) : 1;
    }

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

partial record LayoutPlan
{
    /// <summary>
    /// The plan's standing rooms that carry a native role (ModuleRoomRoles, and the freezer and butchery of
    /// MatchedPlanned) or a room node of the graph (RoomNodeRoles), by room id. Outdoor rooms and rooms with no native
    /// role are left out unless they are a room node's, and so is a room whose successor stands: the pair is one room.
    /// </summary>
    public static List<BuiltRegion> BuiltRegions(LayoutPlan plan, IReadOnlySet<int> built)
    {
        var output = new List<BuiltRegion>();
        var replaced = new HashSet<int>(plan.Rooms.Where(r => built.Contains(r.Id) && r.Replaces != 0).Select(r => r.Replaces));
        var matched = LayoutStationPlanRules.MatchedPlanned;
        var nodeRoles = LayoutStationPlanRules.RoomNodeRoles;
        foreach (var r in plan.Rooms)
        {
            var ok = InteriorRules.ModuleRoomRoles.TryGetValue(r.Role, out var role);
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
            output.Add(new BuiltRegion { Room = r.Id, Role = role, Planned = planned, Region = r.Region, Herd = r.Herd });
        }
        return [.. output.OrderBy(b => b.Room)];
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
    /// <summary>A value copy of the graph with its power sites replaced: the other slices are shared.</summary>
    internal StationGraph WithSites(IEnumerable<SiteNode> sites)
    {
        var copy = (StationGraph)MemberwiseClone();
        copy.Sites = [.. sites];
        return copy;
    }
}
