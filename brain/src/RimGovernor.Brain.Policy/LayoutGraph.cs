using System.Globalization;

namespace RimGovernor.Brain.Policy;

// The station graph is the site planner's input: which stations the colony needs (nodes, with a count each)
// and how strongly each pair wants to stand together (edges). Nothing here is a per-role constant. Counts come
// from the models that already size the colony: the population with its spare place (HousingSparePlaces), the
// nutrition the colonists eat, the production orders of the work ledger and the stock the resource demand
// holds. Which definition serves a need comes from the station catalog and the furniture rules over it. Edges
// are the daily item and pawn flows those same models imply: ingredients from storage to the station that
// works them, food from the stove to the table, the meals a sleeper walks to. Required links come from the
// environment needs the catalog states: a stove working perishable ingredients must be beside the cold storage
// that holds them. The graph is pure data, deterministic and never persisted; clustering it into rooms is the
// next stage.

/// <summary>What a node's stations are for.</summary>
public enum StationPurpose
{
    [WireName("sleep")]
    Sleep,

    [WireName("sleep_medical")]
    SleepMed,

    [WireName("sleep_prison")]
    SleepPrison,

    [WireName("sleep_baby")]
    SleepBaby,

    [WireName("sleep_animal")]
    SleepAnimal,

    [WireName("work")]
    Work,

    [WireName("store")]
    Store,

    [WireName("store_cold")]
    StoreCold,

    [WireName("dine")]
    Dine,

    /// <summary>The pseudo-node of the map outside the base, named OutsideNode in edges. It holds no station: clustering ignores its edges, and the tiler maps it to the plan's entrances.</summary>
    [WireName("outside")]
    Outside,

    /// <summary>The cooling station clustering adds to a group that needs cold (a catalog cooler; no graph node has it).</summary>
    [WireName("cool")]
    Cool,
}

/// <summary>A need the catalog has no station for.</summary>
public sealed class StationGraphException(string message) : DomainException(message);

public static partial class PolicyConstants
{
    // Reasons a node or edge exists; the caller journals them as telemetry decision reasons.
    public const string ReasonBedsPopulationReserve = "beds_population_plus_reserve";
    public const string ReasonBedsSuite = "beds_suite_of_target";
    public const string ReasonBedsPatients = "beds_injured";
    public const string ReasonBedsPrisoners = "beds_prisoners";
    public const string ReasonBedsBabies = "beds_babies";
    public const string ReasonBedsAnimals = "beds_animals";
    public const string ReasonWorkNutrition = "work_nutrition_and_orders";
    public const string ReasonStoreStock = "store_stock_demand";
    public const string ReasonDineSeats = "dine_seats_of_table";
    public const string ReasonFlowIngredient = "flow_ingredient";
    public const string ReasonFlowProduct = "flow_product";
    public const string ReasonFlowMeal = "flow_meal";
    public const string ReasonFlowDay = "flow_day_cycle";
    public const string ReasonNeedCold = "need_cold";

    /// <summary>The spare place MaintainHousing's expansion phase keeps beyond the population: indoor capacity must exceed the colonist count. The station graph provides the same spare bed.</summary>
    public const int HousingSparePlaces = 1;

    /// <summary>
    /// A circulation corridor's width in cells. One: in peace pawns neither block nor slow each other
    /// (PawnUtility.PawnBlockedBy is false unless a hostile is near or the job collides), the pather has no
    /// wall-hugging cost and door costs do not depend on width, so width buys nothing before an attack; the
    /// wider lanes that give bypass under hostile pressure are the defense planner's (#2886). Read from game
    /// code, recorded on #2911.
    /// </summary>
    public const int CirculationWidth = 1;

    /// <summary>Why the graph holds a corridor.</summary>
    public const string ReasonCirculation = "circulation_joins_private_rooms";

    /// <summary>The id of the PurposeOutside pseudo-node in edges.</summary>
    public const string OutsideNode = "outside";

    /// <summary>Why the shelter has a flow: the colonists sleeping in it walk to the table for each meal (sleepEat's bed-to-table trips).</summary>
    public const string ReasonFlowShelterMeal = "flow_shelter_meal";
}

/// <summary>
/// Who needs a place. Colonists are the free people who sleep in a regular bed and eat at a table; Injured are
/// the colonists who need a medical bed now. Prisoners, Babies and Animals sleep in their own kind of bed.
/// Herds are the pen herd by race (the planned ceilings the barns and pens are sized for, #2996); Animals is
/// the misc unit's bed count.
/// </summary>
public sealed record StationPopulation
{
    public int Colonists { get; init; }

    public int Injured { get; init; }

    public int Prisoners { get; init; }

    public int Babies { get; init; }

    public int Animals { get; init; }

    public IReadOnlyList<StationHerd> Herds { get; init; } = [];
}

/// <summary>
/// What one run of a recipe takes in and puts out, by the catalog's rows. Ingredients are one alternative list
/// per slot, the shape OpenBill.Slots has; Food is set when a product is a nutrition-giving food a human eats
/// (the kitchen role worker's own test).
/// </summary>
public sealed record RecipeFlow
{
    public IReadOnlyList<IReadOnlyList<Amount>> Ingredients { get; init; } = [];

    public IReadOnlyList<Amount> Products { get; init; } = [];

    public bool Food { get; init; }

    /// <summary>
    /// Set when the recipe's special product is Butchery: the ingredient's meat and leather come out (the def
    /// lists no product, the race decides): meat to the cold store, leather to the plain one.
    /// </summary>
    public bool Butchery { get; init; }

    internal double ProductCount()
    {
        var n = 0.0;
        foreach (var p in Products)
        {
            n += p.Count;
        }
        return n;
    }

    internal double Nutrition(ItemFacts items)
    {
        var n = 0.0;
        foreach (var p in Products)
        {
            n += p.Count * items.Nutrition.GetValueOrDefault(p.Resource);
        }
        return n;
    }
}

/// <summary>Every model the graph reads. Callers build the graph from known facts only: an unknown population or nutrition is not zero.</summary>
public sealed record StationGraphInput
{
    public StationCatalog Catalog { get; init; } = new();

    public RoomFurniture Furniture { get; init; } = new();

    public DiningFurniture Dining { get; init; } = new();

    public ItemFacts Items { get; init; } = new();

    public IReadOnlyDictionary<string, RecipeFlow> Recipes { get; init; } = new Dictionary<string, RecipeFlow>();

    /// <summary>The colony's people.</summary>
    public StationPopulation Population { get; init; } = new();

    /// <summary>What the colonists eat a day (FoodForecast's consumers, summed).</summary>
    public double NutritionPerDay { get; init; }

    /// <summary>The work ledger's wanted production orders.</summary>
    public IReadOnlyList<OrderSpec> Orders { get; init; } = [];

    /// <summary>The stock the colony holds (DerivedDemand.Needs, with the food reserve).</summary>
    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    /// <summary>Each item's largest stack.</summary>
    public IReadOnlyDictionary<Resource, long> StackLimit { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The items that rot.</summary>
    public IReadOnlyDictionary<Resource, bool> Perishable { get; init; } = new Dictionary<Resource, bool>();

    /// <summary>The range stands the colony asks for (RoomDemand.Ranges); zero asks for no such room (#2887).</summary>
    public int TrainingStands { get; init; }

    /// <summary>The ring markers the colony asks for (RoomDemand.Rings); zero asks for no such room (#2887).</summary>
    public int TrainingMarkers { get; init; }

    /// <summary>The graveyards the plan holds or the burial demand asks for (RoomDemand.Graveyards): each beyond the outskirts' first is a node of its own (#3003).</summary>
    public int Graveyards { get; init; }

    /// <summary>The further station-less rooms the plan holds or the colony asks for: the gear rooms and the child rooms (#3003).</summary>
    public IReadOnlyList<RoomNode> Rooms { get; init; } = [];

    /// <summary>The crops chosen to grow (crop choice is the caller's); a crop that is not edible or has an unknown target gets no field node (#2885).</summary>
    public IReadOnlyList<CropChoice> Crops { get; init; } = [];

    /// <summary>The stored-food buffer the crops' fields budget.</summary>
    public double FoodReserveDays { get; init; }

    /// <summary>The target-sized bedrooms colonists are owed (#2939): each takes one colonist's bed out of the ordinary sleepers' node.</summary>
    public IReadOnlyList<SuiteWish> Suites { get; init; } = [];

    /// <summary>The special rooms asked beyond the stations: the shelter and the throne room (#2958).</summary>
    public SeedRooms Seed { get; init; } = new();

    /// <summary>The animal races' catalog rows: the products and hunger of the pen herds (#2996).</summary>
    public AnimalRaceCatalog Races { get; init; } = new();

    /// <summary>What a cell of each terrain grows a day in the worst quadrum. An unread Pasture leaves feeding unsized.</summary>
    public Fact<PastureTable> Pasture { get; init; }
}

/// <summary>A class of station and how many the colony needs.</summary>
public sealed record StationNode
{
    public string Id { get; set; } = string.Empty;

    public string Def { get; set; } = string.Empty;

    public StationPurpose Purpose { get; set; }

    public int Count { get; set; }

    /// <summary>The catalog's environment needs of the station that apply to this use of it.</summary>
    public IReadOnlyList<EnvNeed> Needs { get; set; } = [];

    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// The work ticks a day the colony asks of the node's stations together (recipe runs times the station's
    /// work per run); 0 for a node that is not worked. Conflict costs are priced against it.
    /// </summary>
    public double DailyWork { get; set; }

    /// <summary>
    /// The food items a day the node's stations make (the runs of the food recipes they work times what a run
    /// makes); 0 for a node that makes none. A poisoned meal is the exposure of a cook beside filth (#2955).
    /// </summary>
    public double Meals { get; set; }

    /// <summary>
    /// Set when the colony's orders have the node's stations butcher: a recipe with the Butchery special
    /// product spawns the corpse's blood (or machine bits) at the butcher's cell (Corpse.ButcherProducts calls
    /// FilthMaker.TryMakeFilth; read by decompiling 1.6, the defs only name the special product). Filth lowers
    /// the room's cleanliness, which the cook's room reads as RoomStatDefOf.FoodPoisonChance when a meal is
    /// made (CompFoodPoisonable.Notify_RecipeProduced). It is use, not station: the same bench that dismantles
    /// mechanoids also works other recipes.
    /// </summary>
    public bool Filth { get; set; }

    /// <summary>The butchery runs a day the node's stations make (a run is a corpse, one TryMakeFilth); 0 for a node that butchers nothing.</summary>
    public double FilthRuns { get; set; }

    /// <summary>The work ticks of one butchery run (#2988).</summary>
    public double FilthWork { get; set; }

    /// <summary>The sleeper's room-impressiveness target of a target-sized bedroom (a suite, #2939). Zero for an ordinary node.</summary>
    public double Target { get; set; }

    /// <summary>The interior cells that meet the target.</summary>
    public int Floor { get; set; }

    /// <summary>
    /// The area of the suite that stands for it, doors included (0 for a new one): the group's area is never
    /// below it, so a suite as built is not outgrown by the door cells it already holds (#3020).
    /// </summary>
    public int Kept { get; set; }
}

/// <summary>One suite the colony wants: its owner's target and the floor of the suite that stands for it, 0 for a new one (SuiteWishes).</summary>
public readonly record struct SuiteWish(double Target, int Kept)
{
    /// <summary>The interior cells the wish asks for: what meets the target (no size snap), never less than the suite that stands.</summary>
    public int Floor() => Math.Max((int)PolicyFunctions.SuiteCells(Target), Kept);
}

/// <summary>The wish that two nodes stand together. Weight is the movements a day (items or pawn trips) between them. A Required edge is a link the environment needs demand (cold storage beside the stove).</summary>
public sealed record StationEdge
{
    public string A { get; set; } = string.Empty;

    public string B { get; set; } = string.Empty;

    public double Weight { get; set; }

    public bool Required { get; set; }

    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// A room the colony needs that holds no station: a node of the graph with a role and an area demand, nothing
/// to furnish. Circulation is the first (#2911); the morgue and the waste yard (#2882) are the same kind.
/// </summary>
public sealed record RoomNode
{
    public string Id { get; init; } = string.Empty;

    public PlannedRole Role { get; init; }

    /// <summary>The interior cells the room is asked for. 0 for a room that joins others: clustering sizes it from the rooms it joins (Joins).</summary>
    public int Area { get; init; }

    /// <summary>The fixed short side of the room in cells, 0 for the squarest block: a corridor is a strip.</summary>
    public int Width { get; init; }

    /// <summary>Marks a room that exists to connect the rooms that cannot be walked through (AddCirculation).</summary>
    public bool Joins { get; init; }

    /// <summary>Marks a room kept cold: clustering gives it the cooling station a cold group carries (the morgue).</summary>
    public bool Cold { get; init; }

    /// <summary>The room node this one must stand beside (a required link).</summary>
    public string Beside { get; init; } = string.Empty;

    /// <summary>
    /// The trips a day between the room and station nodes, priced in the embedding as any link is (the walk the
    /// trips cost). A room with none is not priced by distance: a flow that is not known is not guessed.
    /// </summary>
    public IReadOnlyList<RoomFlow> Flows { get; init; } = [];

    /// <summary>The marker count of a sparring ring.</summary>
    public int Markers { get; init; }

    public string Reason { get; init; } = string.Empty;
}

/// <summary>The trips a day (one way) between a room node and the station node With; Reason says what travels.</summary>
public sealed record RoomFlow(string With, double Weight, string Reason);

/// <summary>The planner's input.</summary>
public sealed partial class StationGraph
{
    public List<StationNode> Nodes { get; set; } = [];

    /// <summary>The station-less rooms.</summary>
    public List<RoomNode> Rooms { get; set; } = [];

    /// <summary>The nodes with no walls and no door (#2884).</summary>
    public List<SiteNode> Sites { get; set; } = [];

    /// <summary>The outdoor field nodes (layout_field_nodes.go); edges name them by Id, and clustering ignores them until fields are placed.</summary>
    public List<FieldNode> Fields { get; set; } = [];

    /// <summary>The outdoor pen nodes of the herds (layout_pen_nodes.go); their edges count as trips to the outside node.</summary>
    public List<PenNode> Pens { get; set; } = [];

    /// <summary>The joy demand (layout_joy_nodes.go); unknown without a joy model. Its outdoor sessions are edges to the outside node.</summary>
    public Fact<RecreationNode> Recreation { get; set; }

    public List<StationEdge> Edges { get; set; } = [];

    /// <summary>The node with the id.</summary>
    public (StationNode? Node, bool Ok) Node(string id)
    {
        foreach (var n in Nodes)
        {
            if (n.Id == id)
            {
                return (n, true);
            }
        }
        return (null, false);
    }

    /// <summary>The summed edge weight between two nodes, 0 when none.</summary>
    public double Weight(string a, string b)
    {
        (a, b) = PolicyFunctions.OrderedPair(a, b);
        var total = 0.0;
        foreach (var e in Edges)
        {
            if (e.A == a && e.B == b)
            {
                total += e.Weight;
            }
        }
        return total;
    }

    /// <summary>Reports a required link between two nodes.</summary>
    public bool Requires(string a, string b)
    {
        (a, b) = PolicyFunctions.OrderedPair(a, b);
        return Edges.Any(e => e.A == a && e.B == b && e.Required);
    }
}

/// <summary>The runs of one recipe a day at one station.</summary>
internal sealed class RecipeLoad
{
    public string Station { get; init; } = string.Empty;

    public string Recipe { get; init; } = string.Empty;

    public double Runs { get; set; }

    public double Work { get; init; }
}

internal sealed partial class GraphBuilder(StationGraphInput input)
{
    public StationGraphInput In { get; } = input;

    public Dictionary<string, StationNode> Nodes { get; } = [];

    public Dictionary<(string, string), StationEdge> Edges { get; } = [];

    private RecipeFlow Flow(string recipe) => In.Recipes.GetValueOrDefault(recipe) ?? new RecipeFlow();

    public Station Station(string def)
    {
        var (s, ok) = In.Catalog.Station(def);
        if (!ok)
        {
            throw new StationGraphException($"{PolicyFunctions.Quote(def)} is no station in the catalog");
        }
        return s!;
    }

    public string AddNode(string def, StationPurpose purpose, int count, IReadOnlyList<EnvNeed> needs, string reason)
    {
        var id = PolicyFunctions.NodeId(def, purpose);
        Nodes[id] = new StationNode { Id = id, Def = def, Purpose = purpose, Count = count, Needs = needs, Reason = reason };
        return id;
    }

    public void AddEdge(string a, string c, double weight, bool required, string reason)
    {
        if (a == c || (weight <= 0 && !required))
        {
            return;
        }
        (a, c) = PolicyFunctions.OrderedPair(a, c);
        var key = (a, c);
        if (!Edges.TryGetValue(key, out var e))
        {
            e = new StationEdge { A = a, B = c, Reason = reason };
            Edges[key] = e;
        }
        e.Weight += weight;
        e.Required = e.Required || required;
        if (required)
        {
            e.Reason = reason;
        }
    }

    public StationGraph Graph()
    {
        var g = new StationGraph();
        foreach (var n in Nodes.Values)
        {
            g.Nodes.Add(n with { });
        }
        g.Nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        foreach (var e in Edges.Values)
        {
            g.Edges.Add(e with { });
        }
        g.Edges.Sort((a, b) => a.A != b.A ? string.CompareOrdinal(a.A, b.A) : string.CompareOrdinal(a.B, b.B));
        return g;
    }

    /// <summary>Adds one bed node per kind of sleeper. A bed serves as many sleepers as the catalog gives its sleeping slots.</summary>
    public void Sleepers()
    {
        var p = In.Population;
        var f = In.Furniture;
        var nurseries = In.Catalog.Contributors(RoomRole.Nursery);
        var hospital = f.HospitalBeds();
        (StationPurpose Purpose, int People, string Def, string Reason)[] kinds =
        [
            (StationPurpose.Sleep, p.Colonists + PolicyConstants.HousingSparePlaces, f.PrimaryBed(), PolicyConstants.ReasonBedsPopulationReserve),
            (StationPurpose.SleepMed, p.Injured, PolicyFunctions.First(hospital), PolicyConstants.ReasonBedsPatients),
            (StationPurpose.SleepPrison, p.Prisoners, f.PrimaryBed(), PolicyConstants.ReasonBedsPrisoners),
            (StationPurpose.SleepBaby, p.Babies, PolicyFunctions.First(nurseries), PolicyConstants.ReasonBedsBabies),
            (StationPurpose.SleepAnimal, p.Animals, PolicyFunctions.FirstNonEmpty(f.AnimalBed, f.AnimalSpot), PolicyConstants.ReasonBedsAnimals),
        ];
        foreach (var k in kinds)
        {
            if (k.People <= 0)
            {
                continue;
            }
            if (k.Def == string.Empty)
            {
                throw new StationGraphException($"no bed def for {Wire.Name(k.Purpose)} sleepers");
            }
            var s = Station(k.Def);
            var people = k.People;
            if (k.Purpose == StationPurpose.Sleep)
            {
                // A suite is one colonist's own bedroom, sized to the colonist's target (#2939); the rest sleep in the ordinary node.
                var suites = Suites();
                for (var i = 0; i < suites.Count; i++)
                {
                    var w = suites[i];
                    var id = string.Create(CultureInfo.InvariantCulture, $"{PolicyFunctions.NodeId(k.Def, k.Purpose)}/suite{i}");
                    Nodes[id] = new StationNode { Id = id, Def = k.Def, Purpose = k.Purpose, Count = 1, Reason = PolicyConstants.ReasonBedsSuite, Target = w.Target, Floor = (int)PolicyFunctions.SuiteCells(w.Target), Kept = w.Kept };
                    people -= Math.Max(s.Capacity.Slots, 1);
                }
                if (people <= 0)
                {
                    continue;
                }
            }
            AddNode(k.Def, k.Purpose, PolicyFunctions.CeilDiv(people, Math.Max(s.Capacity.Slots, 1)), [], k.Reason);
        }
    }

    /// <summary>The suite wishes the graph holds, widest first (ties by kept floor), no more than the colonists there are to own one.</summary>
    public List<SuiteWish> Suites()
    {
        var output = In.Suites.SortedBy((a, b) => a.Floor() != b.Floor() ? a.Floor() > b.Floor() : a.Target > b.Target);
        return output.GetRange(0, Math.Min(output.Count, Math.Max(In.Population.Colonists, 0)));
    }

    /// <summary>How many healthy colonists sleep in the node's beds: a suite holds its one owner, the ordinary node the rest.</summary>
    public int HealthySleepers(StationNode n)
    {
        var p = In.Population;
        if (n.Floor > 0)
        {
            return 1;
        }
        return Math.Max(p.Colonists - Math.Min(p.Injured, p.Colonists) - Suites().Count, 0);
    }

    /// <summary>
    /// The station that works a recipe: the preferred def when it hosts the recipe, else the furniture rule's
    /// bench for the role, else the first hosting station by name.
    /// </summary>
    public (string Def, double Work) HostOf(string recipe, params string[] preferred)
    {
        foreach (var def in preferred)
        {
            var (s, ok) = In.Catalog.Station(def);
            if (ok && s!.Capacity.Kind == CapacityKind.Workers)
            {
                var w = PolicyFunctions.HostWork(s, recipe);
                if (w > 0)
                {
                    return (def, w);
                }
            }
        }
        foreach (var def in In.Catalog.Defs())
        {
            var s = In.Catalog.Stations[def];
            if (s.Capacity.Kind != CapacityKind.Workers)
            {
                continue;
            }
            var w = PolicyFunctions.HostWork(s, recipe);
            if (w > 0)
            {
                return (def, w);
            }
        }
        return (string.Empty, 0);
    }

    /// <summary>
    /// Adds a node per work station the colony needs and returns the daily runs of every recipe. The nutrition
    /// the colonists eat is met by the food recipe that takes the least work per nutrition at a kitchen
    /// station; the ledger's orders add their runs (the work owed, cleared within the day). Where the two ask
    /// for the same recipe the larger stands, as BuildResourceDemand merges overlapping targets.
    /// </summary>
    public List<RecipeLoad> WorkLoads()
    {
        var loads = new Dictionary<(string, string), RecipeLoad>();
        void Add(string station, string recipe, double runs, double work)
        {
            var key = (station, recipe);
            if (!loads.TryGetValue(key, out var l))
            {
                l = new RecipeLoad { Station = station, Recipe = recipe, Work = work };
                loads[key] = l;
            }
            l.Runs = Math.Max(l.Runs, runs);
        }
        if (In.NutritionPerDay > 0)
        {
            var (recipe, station, work, perRun) = BestFoodRecipe();
            if (recipe == string.Empty)
            {
                throw new StationGraphException("no kitchen station works a food recipe");
            }
            Add(station, recipe, In.NutritionPerDay / perRun, work);
        }
        var orders = new Dictionary<(string, string), double>();
        var orderWork = new Dictionary<(string, string), double>();
        foreach (var o in In.Orders)
        {
            if (o.Target <= 0)
            {
                continue;
            }
            var (station, work) = HostOf(o.Recipe, o.BenchKind);
            if (station == string.Empty)
            {
                continue;
            }
            double runs = o.Target;
            var per = Flow(o.Recipe).ProductCount();
            if (per > 0)
            {
                runs /= per;
            }
            var key = (station, o.Recipe);
            orders[key] = orders.GetValueOrDefault(key) + runs;
            orderWork[key] = work;
        }
        foreach (var (key, runs) in orders)
        {
            Add(key.Item1, key.Item2, runs, orderWork[key]);
        }
        var perStation = new Dictionary<string, double>();
        var output = new List<RecipeLoad>();
        foreach (var l in loads.Values)
        {
            perStation[l.Station] = perStation.GetValueOrDefault(l.Station) + (l.Runs * l.Work);
            output.Add(l);
        }
        output = output.SortedBy((a, b) => a.Station != b.Station ? string.CompareOrdinal(a.Station, b.Station) < 0 : string.CompareOrdinal(a.Recipe, b.Recipe) < 0);
        var stations = perStation.Keys.ToList();
        stations.Sort(StringComparer.Ordinal);
        foreach (var def in stations)
        {
            var s = Station(def);
            var count = Math.Max(1, (int)Math.Ceiling(perStation[def] / (GameTime.TicksPerDay * Math.Max(s.Capacity.Slots, 1))));
            var node = Nodes[AddNode(def, StationPurpose.Work, count, s.Needs, PolicyConstants.ReasonWorkNutrition)];
            node.DailyWork = perStation[def];
            foreach (var l in output)
            {
                var flow = Flow(l.Recipe);
                if (l.Station == def && flow.Food)
                {
                    node.Meals += l.Runs * flow.ProductCount();
                }
            }
            double butchery = 0, butcherWork = 0;
            foreach (var l in output)
            {
                if (l.Station == def && Flow(l.Recipe).Butchery)
                {
                    node.Filth = true;
                    butchery += l.Runs;
                    butcherWork += l.Runs * l.Work;
                }
            }
            if (butchery > 0)
            {
                node.FilthRuns = butchery;
                node.FilthWork = butcherWork / butchery;
            }
        }
        return output;
    }

    /// <summary>The food recipe at a kitchen station that takes the least work for a unit of nutrition, with the station that works it.</summary>
    public (string Recipe, string Station, double Work, double PerRun) BestFoodRecipe()
    {
        var best = double.PositiveInfinity;
        string recipe = string.Empty, station = string.Empty;
        double work = 0, perRun = 0;
        var kitchens = new List<string>(In.Catalog.Contributors(RoomRole.Kitchen));
        var bench = In.Furniture.Bench.GetValueOrDefault(RoomRole.Kitchen) ?? string.Empty;
        if (bench != string.Empty)
        {
            kitchens.Insert(0, bench);
        }
        foreach (var def in kitchens)
        {
            var (s, ok) = In.Catalog.Station(def);
            if (!ok || s!.Capacity.Kind != CapacityKind.Workers)
            {
                continue;
            }
            foreach (var r in s.Capacity.Recipes)
            {
                var hasFlow = In.Recipes.TryGetValue(r.Recipe, out var flowOrNull);
                var flow = flowOrNull ?? new RecipeFlow();
                var nut = flow.Nutrition(In.Items);
                if (!hasFlow || !flow.Food || nut <= 0 || r.Work <= 0)
                {
                    continue;
                }
                var cost = r.Work / nut;
                if (cost < best || (cost == best && string.CompareOrdinal(r.Recipe, recipe) < 0))
                {
                    best = cost;
                    recipe = r.Recipe;
                    station = def;
                    work = r.Work;
                    perRun = nut;
                }
            }
            if (recipe != string.Empty)
            {
                break;
            }
        }
        return (recipe, station, work, perRun);
    }

    /// <summary>The food items the colony eats a day: the runs of the nutrition recipe times what a run makes.</summary>
    public double MealsPerDay()
    {
        if (In.NutritionPerDay <= 0)
        {
            return 0;
        }
        var (recipe, _, _, perRun) = BestFoodRecipe();
        if (recipe == string.Empty || perRun <= 0)
        {
            return 0;
        }
        return In.NutritionPerDay / perRun * Flow(recipe).ProductCount();
    }

    /// <summary>Adds the tables the colonists eat at. A table seats at its edge cells, as the dining template puts a chair at every side.</summary>
    public void Dining(double meals)
    {
        var eaters = In.Population.Colonists;
        if (eaters <= 0 || meals <= 0)
        {
            return;
        }
        var table = In.Dining.Table;
        if (table.Def == string.Empty)
        {
            throw new StationGraphException("no dining table def");
        }
        var s = Station(table.Def);
        var seats = 2 * (table.Size.X + table.Size.Z);
        AddNode(table.Def, StationPurpose.Dine, PolicyFunctions.CeilDiv(eaters, seats), s.Needs, PolicyConstants.ReasonDineSeats);
    }

    /// <summary>The shelf a stock is stored on: the catalog's item store of the storeroom role holding the most stacks per cell, wanting cold when cold is asked for.</summary>
    public (string Def, Station? Station, bool Ok) StorageStation(bool cold)
    {
        var best = string.Empty;
        var bestDensity = 0.0;
        Station? bestStation = null;
        foreach (var def in In.Catalog.Contributors(RoomRole.Storeroom))
        {
            var s = In.Catalog.Stations[def];
            if (s.Capacity.Kind != CapacityKind.Items || s.Capacity.Slots <= 0)
            {
                continue;
            }
            var wantsCold = s.Needs.Any(n => n.Kind == EnvNeedKind.Cold);
            if (cold && !wantsCold)
            {
                continue;
            }
            var density = (double)s.Capacity.Slots / Math.Max(s.Shape.Size.X * s.Shape.Size.Z, 1);
            if (density > bestDensity)
            {
                best = def;
                bestDensity = density;
                bestStation = s;
            }
        }
        return (best, bestStation, best != string.Empty);
    }

    /// <summary>Adds the shelves the stock needs: perishable stock to a store that wants cold, the rest to a plain one.</summary>
    public void Storage()
    {
        int coldStacks = 0, plainStacks = 0;
        var resources = In.Stock.Keys.ToList();
        resources.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        foreach (var r in resources)
        {
            var n = In.Stock[r];
            if (n <= 0)
            {
                continue;
            }
            var limit = In.StackLimit.GetValueOrDefault(r);
            if (limit <= 0)
            {
                throw new StationGraphException($"no stack limit for {r.Value}");
            }
            var stacks = (int)((n + limit - 1) / limit);
            if (In.Perishable.GetValueOrDefault(r))
            {
                coldStacks += stacks;
            }
            else
            {
                plainStacks += stacks;
            }
        }
        if (coldStacks > 0)
        {
            var (def, s, ok) = StorageStation(true);
            if (ok)
            {
                AddNode(def, StationPurpose.StoreCold, PolicyFunctions.CeilDiv(coldStacks, s!.Capacity.Slots), PolicyFunctions.ColdNeeds(s), PolicyConstants.ReasonStoreStock);
            }
            else
            {
                plainStacks += coldStacks;
            }
        }
        if (plainStacks > 0)
        {
            var (def, s, ok) = StorageStation(false);
            if (!ok)
            {
                throw new StationGraphException("no item store in the catalog");
            }
            AddNode(def, StationPurpose.Store, PolicyFunctions.CeilDiv(plainStacks, s!.Capacity.Slots), PolicyFunctions.WithoutCold(s.Needs), PolicyConstants.ReasonStoreStock);
        }
    }

    /// <summary>The node an item is held in.</summary>
    public (string Id, bool Ok) StoreFor(Resource r) => StoreOf(In.Perishable.GetValueOrDefault(r));

    /// <summary>The store that holds things that rot (the cold one, else the plain one) or things that keep.</summary>
    public (string Id, bool Ok) StoreOf(bool perishable)
    {
        var purpose = perishable ? StationPurpose.StoreCold : StationPurpose.Store;
        foreach (var n in Nodes.Values)
        {
            if (n.Purpose == purpose)
            {
                return (n.Id, true);
            }
        }
        if (purpose == StationPurpose.StoreCold)
        {
            foreach (var n in Nodes.Values)
            {
                if (n.Purpose == StationPurpose.Store)
                {
                    return (n.Id, true);
                }
            }
        }
        return (string.Empty, false);
    }

    /// <summary>
    /// Adds the item edges of every recipe load: ingredients from the store to the station, the product from
    /// the station to the table (food) or the store. A station working a perishable ingredient requires the
    /// cold store.
    /// </summary>
    public void Flows(IReadOnlyList<RecipeLoad> loads)
    {
        var dine = string.Empty;
        foreach (var n in Nodes.Values)
        {
            if (n.Purpose == StationPurpose.Dine)
            {
                dine = n.Id;
            }
        }
        foreach (var l in loads)
        {
            var work = PolicyFunctions.NodeId(l.Station, StationPurpose.Work);
            var flow = Flow(l.Recipe);
            foreach (var pick in PolicyFunctions.RecipePicks(flow))
            {
                var (store, ok) = StoreFor(pick.Resource);
                if (!ok)
                {
                    continue;
                }
                AddEdge(store, work, l.Runs * pick.Count, false, PolicyConstants.ReasonFlowIngredient);
                if (In.Perishable.GetValueOrDefault(pick.Resource))
                {
                    if (Nodes[store].Purpose == StationPurpose.StoreCold)
                    {
                        AddEdge(store, work, 0, true, PolicyConstants.ReasonNeedCold);
                    }
                }
                if (flow.Butchery)
                {
                    // The yield of a butchered ingredient is meat, which rots, and leather, which keeps: one haul of each per ingredient.
                    foreach (var rots in new[] { true, false })
                    {
                        var (output, found) = StoreOf(rots);
                        if (found)
                        {
                            AddEdge(work, output, l.Runs * pick.Count, false, PolicyConstants.ReasonFlowProduct);
                        }
                    }
                }
            }
            foreach (var p in flow.Products)
            {
                var amount = l.Runs * p.Count;
                if (flow.Food && dine != string.Empty)
                {
                    AddEdge(work, dine, amount, false, PolicyConstants.ReasonFlowMeal);
                }
                else
                {
                    var (store, ok) = StoreFor(p.Resource);
                    if (ok)
                    {
                        AddEdge(work, store, amount, false, PolicyConstants.ReasonFlowProduct);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adds the colonist's day at the bed: each colonist who is not in a medical bed leaves it at wake and comes
    /// back to sleep, one trip a day to the outside node (one way, like every edge; the way back is counted
    /// where trips become door passages). Meals are SleepEat's and the pen herds' hauls are PenNodes', the joy
    /// outings are Recreation's and the hauls of mined, wood and hunted goods are Hauls'.
    /// </summary>
    public void DayCycle()
    {
        if (In.Population.Colonists <= 0)
        {
            return;
        }
        foreach (var n in Nodes.Values.ToList())
        {
            if (n.Purpose == StationPurpose.Sleep)
            {
                AddEdge(n.Id, PolicyConstants.OutsideNode, HealthySleepers(n), false, PolicyConstants.ReasonFlowDay);
            }
        }
    }

    /// <summary>Adds the sleep and eat cycle: each meal a sleeper eats is a trip between bed and table, shared by regular and medical beds by the people in them.</summary>
    public void SleepEat(double meals)
    {
        var p = In.Population;
        if (meals <= 0 || p.Colonists <= 0)
        {
            return;
        }
        var dine = string.Empty;
        foreach (var n in Nodes.Values)
        {
            if (n.Purpose == StationPurpose.Dine)
            {
                dine = n.Id;
            }
        }
        if (dine == string.Empty)
        {
            return;
        }
        double injured = Math.Min(p.Injured, p.Colonists);
        double colonists = p.Colonists;
        foreach (var n in Nodes.Values.ToList())
        {
            switch (n.Purpose)
            {
                case StationPurpose.Sleep:
                    AddEdge(n.Id, dine, meals * HealthySleepers(n) / colonists, false, PolicyConstants.ReasonFlowMeal);
                    break;
                case StationPurpose.SleepMed:
                    AddEdge(n.Id, dine, meals * injured / colonists, false, PolicyConstants.ReasonFlowMeal);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Gives the shelter the meal trips of the colonists it houses, as SleepEat gives a bed: meals a day times
    /// the share of the colony sleeping in it, to the dining node. With no table, no meals or no colonists read
    /// there is nothing to price and the shelter carries no flow.
    /// </summary>
    public void ShelterMeals(List<RoomNode> rooms, double meals)
    {
        var colonists = In.Population.Colonists;
        if (meals <= 0 || colonists <= 0)
        {
            return;
        }
        for (var i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].Role != PlannedRole.Shelter)
            {
                continue;
            }
            foreach (var n in Nodes.Values)
            {
                if (n.Purpose == StationPurpose.Dine)
                {
                    var share = (double)Math.Min(In.Seed.Colonists, colonists) / colonists;
                    rooms[i] = rooms[i] with { Flows = [.. rooms[i].Flows, new RoomFlow(n.Id, meals * share, PolicyConstants.ReasonFlowShelterMeal)] };
                }
            }
        }
    }
}

public static partial class PolicyFunctions
{
    internal static (string, string) OrderedPair(string a, string b) => string.CompareOrdinal(b, a) < 0 ? (b, a) : (a, b);

    internal static string NodeId(string def, StationPurpose purpose) => def + "/" + Wire.Name(purpose);

    internal static string Quote(string s) => "\"" + s + "\"";

    /// <summary>The stations that serve n at per each.</summary>
    internal static int CeilDiv(int n, int per)
    {
        if (n <= 0 || per <= 0)
        {
            return 0;
        }
        return (n + per - 1) / per;
    }

    internal static string First(IReadOnlyList<string> list) => list.Count == 0 ? string.Empty : list[0];

    internal static string FirstNonEmpty(params string[] list)
    {
        foreach (var s in list)
        {
            if (s != string.Empty)
            {
                return s;
            }
        }
        return string.Empty;
    }

    /// <summary>The station's work for a recipe (0 when it does not host it).</summary>
    internal static double HostWork(Station s, string recipe)
    {
        foreach (var r in s.Capacity.Recipes)
        {
            if (r.Recipe == recipe)
            {
                return r.Work;
            }
        }
        return 0;
    }

    internal static List<EnvNeed> ColdNeeds(Station s) => [.. s.Needs.Where(n => n.Kind == EnvNeedKind.Cold)];

    internal static List<EnvNeed> WithoutCold(IReadOnlyList<EnvNeed> needs) => [.. needs.Where(n => n.Kind != EnvNeedKind.Cold)];

    /// <summary>The ingredients a run takes: each slot's cheapest member (lowest count, then name), as OpenBill.picks chooses.</summary>
    internal static List<Amount> RecipePicks(RecipeFlow f)
    {
        var output = new List<Amount>();
        foreach (var slot in f.Ingredients)
        {
            var have = false;
            Amount pick = default!;
            foreach (var a in slot)
            {
                if (!have || string.IsNullOrEmpty(pick.Resource.Value) || a.Count < pick.Count || (a.Count == pick.Count && string.CompareOrdinal(a.Resource.Value, pick.Resource.Value) < 0))
                {
                    pick = a;
                    have = true;
                }
            }
            if (have && !string.IsNullOrEmpty(pick.Resource.Value) && pick.Count > 0)
            {
                output.Add(pick);
            }
        }
        return output;
    }

    /// <summary>Derives the graph from the models in input.</summary>
    public static StationGraph BuildStationGraph(StationGraphInput input)
    {
        var b = new GraphBuilder(input);
        b.Sleepers();
        var runs = b.WorkLoads();
        var meals = b.MealsPerDay();
        b.Dining(meals);
        b.Storage();
        b.Flows(runs);
        b.SleepEat(meals);
        b.DayCycle();
        var fields = b.FieldNodes(runs);
        var pens = b.PenNodes();
        var recreation = b.Recreation();
        b.Hauls();
        var g = b.Graph();
        g.Fields = fields;
        g.Pens = pens;
        g.Recreation = recreation;
        g.Rooms = StationRoomNodes(input);
        b.ShelterMeals(g.Rooms, meals);
        foreach (var n in g.Nodes)
        {
            switch (n.Purpose)
            {
                case StationPurpose.Sleep:
                case StationPurpose.SleepMed:
                case StationPurpose.SleepBaby:
                    // Somebody sleeps in a room nobody may walk through (RoomCrossing.Beds): the colony may need a way to reach it that crosses no other room.
                    g.Rooms.Add(new RoomNode { Id = Wire.Name(PlannedRole.Circulation), Role = PlannedRole.Circulation, Width = CirculationWidth, Joins = true, Reason = ReasonCirculation });
                    return g;
                default:
                    break;
            }
        }
        return g;
    }

    /// <summary>The station-less rooms the graph holds: the outskirts, the training rooms and the seed rooms.</summary>
    private static List<RoomNode> StationRoomNodes(StationGraphInput input)
    {
        var nodes = new List<RoomNode>(OutskirtsRoomNodes());
        nodes.AddRange(GraveyardRoomNodes(input.Graveyards));
        nodes.AddRange(TrainingRoomNodes(input.TrainingStands, input.TrainingMarkers));
        nodes.AddRange(input.Rooms);
        nodes.AddRange(SeedRoomNodes(input.Seed));
        return nodes;
    }

    /// <summary>
    /// The graph of the rooms that need no live fact: no station and no edge, only the station-less rooms. A
    /// first plan derived before the colony is read grows from it, and the rest grows on replan.
    /// </summary>
    public static StationGraph SeedGraph(StationGraphInput input) => new() { Rooms = StationRoomNodes(input) };
}
