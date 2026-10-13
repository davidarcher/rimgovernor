using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>The role-driven requirement a room's floor is measured against. (Go <c>FloorTier</c>.)</summary>
public enum FloorTier
{
    /// <summary>The rooms whose cleanliness the game reads for food poisoning and infection: every cell needs a non-negative terrain cleanliness.</summary>
    Clean,

    /// <summary>The rooms colonists rest, eat and relax in: every cell needs a laid floor rather than natural ground.</summary>
    Living,

    /// <summary>The most-travelled home cells: every one needs a laid floor with no path cost.</summary>
    Traffic,

    /// <summary>The animal barns: every cell needs a laid floor rather than natural ground, whatever its beauty (straw matting).</summary>
    Barn,

    /// <summary>The firebreak ring's settled pave cells: the cheapest affordable floor with zero flammability.</summary>
    Firebreak,

    /// <summary>
    /// The standing incinerator's interior: every cell needs ground that neither burns nor grows (zero
    /// flammability and fertility) so the burn leaves nothing to spread on.
    /// </summary>
    Incinerator,

    /// <summary>The throne room: every cell needs a floor whose terrain carries one of the title's required tags.</summary>
    Throne,
}

public static class FloorTierExtensions
{
    /// <summary>Go <c>FloorTier.fireproof</c>: true for the tiers that lay only floors with zero flammability.</summary>
    public static bool Fireproof(this FloorTier t) => t is FloorTier.Firebreak or FloorTier.Incinerator;
}

/// <summary>Go <c>FlooringPolicy</c>.</summary>
public sealed record FlooringPolicy
{
    /// <summary>The floor definitions the planner may choose from; the native planning census reports each one's availability and stats.</summary>
    public IReadOnlyList<string> Floors { get; init; } = [];

    /// <summary>Bounds the cells one admitted plan floors.</summary>
    public int MaxCellsPerPlan { get; init; }

    /// <summary>Weights score a candidate floor for each tier.</summary>
    public FloorWeights Clean { get; init; } = new();

    /// <summary>Weights score a candidate floor for each tier.</summary>
    public FloorWeights Living { get; init; } = new();

    /// <summary>Weights score a candidate floor for each tier.</summary>
    public FloorWeights Traffic { get; init; } = new();

    /// <summary>The longest a traffic floor may take to repay its build and material ticks in walking ticks saved; zero disables the traffic tier.</summary>
    public double PaybackDays { get; init; }

    /// <summary>
    /// Prices one unit of a floor's cost list in colony work ticks (gathering, hauling). The default 30 sits
    /// between chopped wood and cut stone blocks, so a steel or block floor costs more than wood.
    /// </summary>
    public double MaterialTicksPerUnit { get; init; }

    internal bool IsValid
    {
        get
        {
            if (Floors.Count == 0 || MaxCellsPerPlan < 1 || MaxCellsPerPlan > 256 || !Clean.IsValid || !Living.IsValid || !Traffic.IsValid
                || !PolicyFunctions.FloorNumber(PaybackDays) || PaybackDays < 0 || !PolicyFunctions.FloorNumber(MaterialTicksPerUnit) || MaterialTicksPerUnit < 0)
            {
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return Floors.All(f => PolicyFunctions.FoodId(f) && seen.Add(f));
        }
    }
}

/// <summary>
/// Prices a floor's native stats: positive terms reward cleanliness and beauty, negative ones charge path cost,
/// flammability and the summed cost list per cell. (Go <c>FloorWeights</c>.)
/// </summary>
public sealed record FloorWeights
{
    public double Cleanliness { get; init; }

    public double Beauty { get; init; }

    public double PathCost { get; init; }

    public double Flammability { get; init; }

    public double Cost { get; init; }

    internal bool IsValid =>
        new[] { Cleanliness, Beauty, PathCost, Flammability, Cost }.All(v => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0);
}

/// <summary>
/// The native flooring census: every proper indoor home room with the terrain under each cell, and the stats of
/// every terrain named. (Go <c>FlooringObservation</c>.)
/// </summary>
public sealed record FlooringObservation
{
    public IReadOnlyList<FloorRoom> Rooms { get; init; } = [];

    public IReadOnlyDictionary<string, FloorTerrain> Terrains { get; init; } = new Dictionary<string, FloorTerrain>();

    /// <summary>The most-travelled cells the routes census observed, most samples first, out of TrafficSamples samples in all; each names a terrain in Terrains.</summary>
    public IReadOnlyList<TrafficCell> Traffic { get; init; } = [];

    public uint TrafficSamples { get; init; }

    /// <summary>
    /// The planning census row of every policy floor the traffic tier may price, null when the census was not
    /// read: the tier is then unknown.
    /// </summary>
    public IReadOnlyDictionary<string, FloorDefinition>? Floors { get; init; }

    /// <summary>The accessible colony stock.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    /// <summary>The tier style's aisle floor (empty when none), as the selector reads it.</summary>
    public string TrafficStyle { get; init; } = "";

    /// <summary>The firebreak ring's pave cells (PlanFirebreak) that are still natural ground with no floor ordered, as the caller reads them.</summary>
    public IReadOnlyList<Cell> Firebreak { get; init; } = [];

    /// <summary>The standing incinerator's interior with the terrain under each cell, read by the caller (the native census lists only roofed rooms).</summary>
    public IReadOnlyList<FloorCell> Incinerator { get; init; } = [];

    internal FloorTerrain TerrainOf(string name) => Terrains.GetValueOrDefault(name) ?? new FloorTerrain();

    /// <summary>Throws <see cref="DomainException"/> when the census is malformed.</summary>
    public void Validate()
    {
        foreach (var (name, t) in Terrains)
        {
            DomainException.Require(
                PolicyFunctions.FoodId(name) && PolicyFunctions.FloorNumber(t.Cleanliness) && PolicyFunctions.FloorNumber(t.Beauty) && PolicyFunctions.FloorNumber(t.Flammability)
                    && PolicyFunctions.FloorNumber(t.Fertility) && t.PathCost >= 0,
                "invalid floor terrain"
            );
        }
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        var cells = new HashSet<Cell>();
        var total = 0;
        foreach (var room in Rooms)
        {
            DomainException.Require(PolicyFunctions.FoodId(room.Id) && rooms.Add(room.Id), "invalid floor room");
            total += room.Cells.Count;
            DomainException.Require(total <= 4096, "flooring census exceeds bound");
            foreach (var c in room.Cells)
            {
                DomainException.Require(
                    Terrains.ContainsKey(c.Terrain) && cells.Add(c.Cell) && !(c.Pending.Length != 0 && !PolicyFunctions.FoodId(c.Pending)),
                    "invalid floor cell"
                );
            }
        }
        foreach (var c in Incinerator)
        {
            DomainException.Require(
                Terrains.ContainsKey(c.Terrain) && cells.Add(c.Cell) && !(c.Pending.Length != 0 && !PolicyFunctions.FoodId(c.Pending)),
                "invalid incinerator cell"
            );
        }
        var traffic = new HashSet<(TrafficLayer, Cell)>();
        foreach (var t in Traffic)
        {
            DomainException.Require(
                Terrains.ContainsKey(t.Terrain) && t.Layer.Valid() && traffic.Add((t.Layer, t.Cell)) && !(t.Pending.Length != 0 && !PolicyFunctions.FoodId(t.Pending)),
                "invalid traffic cell"
            );
        }
    }
}

public sealed record FloorRoom
{
    public string Id { get; init; } = "";

    public Fact<RoomRole> Role { get; init; }

    public IReadOnlyList<FloorCell> Cells { get; init; } = [];

    /// <summary>
    /// When set, terrain tags every cell's floor must carry one of: the throne room's flooring requirement, read
    /// from the def mirror.
    /// </summary>
    public IReadOnlyList<string> RequiredTags { get; init; } = [];

    /// <summary>The requirement's labelKey.</summary>
    public string RequiredLabel { get; init; } = "";
}

/// <summary>A room's native quality stats as Room.GetStat reports them. (Go <c>RoomQuality</c>.)</summary>
public sealed record RoomQuality(double Space, double Beauty, double Cleanliness, double Wealth, double Impressiveness);

public sealed record FloorCell
{
    public Cell Cell { get; init; }

    public string Terrain { get; init; } = "";

    /// <summary>The floor already ordered on the cell (a blueprint or frame), empty when none.</summary>
    public string Pending { get; init; } = "";
}

public sealed record FloorTerrain
{
    public double Cleanliness { get; init; }

    public double Beauty { get; init; }

    public double Flammability { get; init; }

    public int PathCost { get; init; }

    /// <summary>The terrain's own fertility; above zero it grows plants.</summary>
    public double Fertility { get; init; }

    /// <summary>Native's own flag for unbuilt ground.</summary>
    public bool Natural { get; init; }

    /// <summary>The TerrainDef's tags (the mirror's, never a name list).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>One room measured short of its tier's requirement. (Go <c>FloorDeficit</c>.)</summary>
public sealed record FloorDeficit
{
    public string Key { get; init; } = "";

    public string Room { get; init; } = "";

    public FloorTier Tier { get; init; }

    /// <summary>The room's role as the flooring census reports it, <see cref="RoomRole.None"/> for the traffic deficit's aisles; the tier style reads it.</summary>
    public RoomRole Role { get; init; }

    /// <summary>The deficient cells not yet ordered, in cell order.</summary>
    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>Counts the deficient cells with a floor already ordered.</summary>
    public int Pending { get; init; }

    /// <summary>The terrain tags a throne deficit's floor must carry one of.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The floor the traffic tier priced its cells on; the selector lays exactly it.</summary>
    public string Floor { get; init; } = "";

    /// <summary>Marks a traffic latch kept while the floor could not be priced.</summary>
    public bool Unknown { get; init; }
}

/// <summary>
/// The per-room latch: Deficits lists every room measured short, clean workspaces first and then by key, and
/// Latched is the key set persisted between reviews. Known is false when the census was unknown, in which case
/// Latched repeats the previous latch and Deficits is empty. (Go <c>FlooringReview</c>.)
/// </summary>
public sealed record FlooringReview
{
    public bool Active { get; init; }

    public bool Known { get; init; }

    public IReadOnlyList<FloorDeficit> Deficits { get; init; } = [];

    public IReadOnlyList<string> Latched { get; init; } = [];
}

/// <summary>Go <c>FlooringMethod</c>. <see cref="None"/> is Go's empty string.</summary>
public enum FlooringMethod
{
    [WireName("")]
    None = 0,

    Unknown,

    [WireName("no_deficit")]
    NoMethod,

    [WireName("floor_pending")]
    Pending,

    [WireName("floor_research_needed")]
    ResearchNeeded,

    [WireName("floor_materials_needed")]
    MaterialsNeeded,

    [WireName("lay_floor")]
    Build,
}

public sealed record FlooringProposal
{
    public FlooringMethod Method { get; init; }

    public MethodId Key { get; init; }

    /// <summary>Names the deficient room served.</summary>
    public string Room { get; init; } = "";

    public FloorTier Tier { get; init; }

    /// <summary>The chosen floor.</summary>
    public string Definition { get; init; } = "";

    /// <summary>The cells to lay it on, each to be validated by a native placement preview before admission.</summary>
    public IReadOnlyList<Cell> Cells { get; init; } = [];
}

/// <summary>What the planning census reports for one candidate floor. (Go <c>FloorDefinition</c>.)</summary>
public sealed record FloorDefinition
{
    public Fact<bool> Available { get; init; }

    public Fact<bool> Terrain { get; init; }

    public Fact<double> Cleanliness { get; init; }

    public Fact<double> Beauty { get; init; }

    public Fact<double> Flammability { get; init; }

    public Fact<int> PathCost { get; init; }

    public Fact<IReadOnlyList<Amount>> Costs { get; init; }

    /// <summary>The native WorkToBuild stat in work ticks.</summary>
    public Fact<double> WorkToBuild { get; init; }

    /// <summary>The TerrainDef's tags, for the throne tier's requirement.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>What <c>SelectFlooringMethod</c> needs beyond the review. (Go <c>FlooringFacts</c>.)</summary>
public sealed record FlooringFacts
{
    /// <summary>Credit floors already laid or ordered inside planned rooms.</summary>
    public Fact<LayoutPlan> Layout { get; init; }

    /// <summary>Credit floors already laid or ordered inside planned rooms.</summary>
    public Fact<FlooringObservation> Observation { get; init; }

    /// <summary>Maps floor definition names to their census row; a floor absent here is unknown.</summary>
    public IReadOnlyDictionary<string, FloorDefinition> Definitions { get; init; } = new Dictionary<string, FloorDefinition>();

    /// <summary>The accessible colony stock by resource; unknown skips the affordability test and leaves it to admission.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    /// <summary>
    /// The tier's floor rule per room role (FloorDef): a styled floor that is known available, meets the tier
    /// and pays for the whole batch is chosen before any scoring. Null styles nothing.
    /// </summary>
    public Func<RoomRole, (string Floor, bool Ok)>? Style { get; init; }

    /// <summary>The terrain the containment cell is laid with when the stock pays for it; the default names none.</summary>
    public ContainmentFloor ContainmentFloor { get; init; } = new();
}

public static partial class PolicyFunctions
{
    // MaintainFlooring lays role-appropriate floors in the rooms the colony lives and works in. It reasons from
    // the measured terrain under each room cell and the native stats of that terrain, never from what the
    // controller last ordered: a clean workspace (kitchen, hospital, laboratory) is deficient while any cell's
    // terrain cleanliness is negative, a living room (bedroom, barracks, dining, recreation) while any cell is
    // still natural ground. Clean workspaces come first. The material is scored per tier from the native
    // cleanliness, path cost, beauty, flammability and cost list of every affordable floor definition, and the
    // latch releases only on a measured census with no deficient cell left. The traffic tier floors the natural
    // home cells colonists are observed to walk where the floor pays for itself within PaybackDays.
    public static readonly ConcernId MaintainFlooring = new("MaintainFlooring");

    /// <summary>The latch key of the single incinerator deficit.</summary>
    private const string IncineratorKey = "incinerator";

    /// <summary>The latch key of the single firebreak deficit.</summary>
    private const string FirebreakKey = "firebreak";

    /// <summary>Scores the firebreak tier on cost alone; <see cref="FloorScore"/> refuses any flammable floor for it.</summary>
    private static readonly FloorWeights FirebreakWeights = new() { Cost = 1 };

    /// <summary>The latch key of the single traffic deficit.</summary>
    private const string TrafficKey = "traffic";

    /// <summary>The fewest samples the whole traffic census must hold before the traffic tier judges any cell.</summary>
    private const int TrafficWarmupSamples = 48;

    /// <summary>The colonist traffic layer's decay half-life: 120000 ticks. Steps per day = samples*ln2/halfLife.</summary>
    private const double ColonistTrafficHalfLifeDays = 2.0;

    /// <summary>Go <c>DefaultFlooringPolicy</c>.</summary>
    public static FlooringPolicy DefaultFlooringPolicy() =>
        new()
        {
            Floors =
            [
                StrawMatting, "SterileTile", "TileSandstone", "TileGranite", "TileLimestone", "TileSlate", "TileMarble",
                "FlagstoneSandstone", "FlagstoneGranite", "FlagstoneLimestone", "FlagstoneSlate", "FlagstoneMarble",
                Carpet, "PavedTile", "Concrete", "WoodPlankFloor",
            ],
            MaxCellsPerPlan = 24,
            Clean = new FloorWeights { Cleanliness = 10, Beauty = 1, PathCost = 1, Flammability = 1, Cost = 0.2 },
            Living = new FloorWeights { Cleanliness = 1, Beauty = 3, PathCost = 1, Flammability = 3, Cost = 0.2 },
            Traffic = new FloorWeights { Cleanliness = 1, Beauty = 1, PathCost = 5, Flammability = 1, Cost = 0.5 },
            PaybackDays = 15,
            MaterialTicksPerUnit = 30,
        };

    internal static bool FloorNumber(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>Go <c>FloorRoomKey</c>: identifies a flooring room across native renumbering the way RoomLatchKey does: by its lowest-sorted cell.</summary>
    public static string FloorRoomKey(FloorRoom room)
    {
        if (room.Cells.Count == 0)
        {
            return room.Id;
        }
        var low = room.Cells[0].Cell;
        foreach (var c in room.Cells.Skip(1))
        {
            if (CellLess(c.Cell, low))
            {
                low = c.Cell;
            }
        }
        return $"{low.X},{low.Z}";
    }

    /// <summary>
    /// Classifies a room by the same role facts the cleanliness slice uses: the room census row supplies role and
    /// contents (a cooking bench makes a "Room" a clean workspace), the flooring row's own role stands in when
    /// the room census does not list the room. Inherently dirty rooms and every role outside the two tiers have
    /// no requirement.
    /// </summary>
    private static FloorTier? FloorTierOf(FloorRoom room, IReadOnlyDictionary<string, Room> census)
    {
        if (room.RequiredTags.Count > 0)
        {
            return FloorTier.Throne;
        }
        if (census.TryGetValue(room.Id, out var r))
        {
            if (InherentlyDirty(r))
            {
                return null;
            }
            if (CleanWorkspace(r))
            {
                return FloorTier.Clean;
            }
        }
        return room.Role.TryGetValue(out var role) ? RoleFloorTier(role) : null;
    }

    /// <summary>The tier a room role alone requires.</summary>
    private static FloorTier? RoleFloorTier(RoomRole role) =>
        role switch
        {
            RoomRole.Kitchen or RoomRole.Hospital or RoomRole.Laboratory => FloorTier.Clean,
            RoomRole.Bedroom or RoomRole.Barracks or RoomRole.DiningRoom or RoomRole.RecRoom => FloorTier.Living,
            RoomRole.Barn => FloorTier.Barn,
            _ => null,
        };

    /// <summary>Reports whether tags holds any of want.</summary>
    private static bool HasAnyTag(IReadOnlyList<string> tags, IReadOnlyList<string> want) => tags.Any(want.Contains);

    private static bool FloorDeficient(FloorTier tier, FloorTerrain t) =>
        tier switch
        {
            FloorTier.Clean => t.Cleanliness < 0,
            FloorTier.Living or FloorTier.Traffic or FloorTier.Barn => t.Natural,
            _ => false,
        };

    /// <summary>
    /// Gathers the natural home cells colonists are observed to walk where a laid floor pays for itself. The
    /// floor is chosen before any cell: the tier style's aisle floor when it can be laid, else no floor. Each
    /// cell's payback is the floor's one-time ticks over the walking ticks it saves a day; cells are kept
    /// cheapest payback first up to the first beyond PaybackDays. Cells inside a tiered room are that room's
    /// business. A floor that cannot be priced leaves the tier unknown: a traffic latch held before is kept,
    /// none is raised.
    /// </summary>
    private static (FloorDeficit Deficit, bool Ok) TrafficDeficit(FlooringObservation v, HashSet<Cell> roomed, bool latched, FlooringPolicy p)
    {
        var d = new FloorDeficit { Key = TrafficKey, Tier = FloorTier.Traffic };
        if (p.PaybackDays == 0 || v.TrafficSamples < TrafficWarmupSamples)
        {
            return (d, false);
        }
        var cells = new List<TrafficCell>();
        var pending = 0;
        foreach (var t in v.Traffic)
        {
            if (!t.Layer.Colonist() || !t.Home || roomed.Contains(t.Cell) || !FloorDeficient(FloorTier.Traffic, v.TerrainOf(t.Terrain)))
            {
                continue;
            }
            if (t.Pending.Length != 0)
            {
                pending++;
                continue;
            }
            cells.Add(t);
        }
        d = d with { Pending = pending };
        if (cells.Count == 0)
        {
            return (d, pending > 0);
        }
        var (name, known) = TrafficFloor(v, Math.Min(cells.Count, p.MaxCellsPerPlan), p);
        if (!known)
        {
            return (d with { Unknown = latched }, latched || pending > 0);
        }
        if (name.Length == 0)
        {
            return (d, pending > 0);
        }
        var floor = v.Floors![name];
        var floorPath = floor.PathCost.ValueOrDefault;
        var cost = FloorTicks(floor, p);
        var payback = new Dictionary<Cell, double>();
        foreach (var t in cells)
        {
            var saving = t.Samples * Math.Log(2) / ColonistTrafficHalfLifeDays * (v.TerrainOf(t.Terrain).PathCost - floorPath);
            payback[t.Cell] = saving > 0 ? cost / saving : double.PositiveInfinity;
        }
        var kept = new List<Cell>();
        foreach (var t in cells.OrderBy(t => payback[t.Cell]))
        {
            if (payback[t.Cell] > p.PaybackDays)
            {
                break;
            }
            kept.Add(t.Cell);
        }
        d = d with { Cells = kept, Floor = kept.Count > 0 ? name : "" };
        return (d, kept.Count > 0 || pending > 0);
    }

    /// <summary>
    /// The floor the traffic tier prices: the tier style's aisle floor when <see cref="StyledFloor"/> would lay
    /// it for the batch, never a substitute. Known is false when the floor, or an unknown fact, leaves it
    /// unpriced; an empty name with known true means no floor is laid (no style, or it must wait).
    /// </summary>
    private static (string Name, bool Known) TrafficFloor(FlooringObservation v, int batch, FlooringPolicy p)
    {
        if (v.Floors is null)
        {
            return ("", false);
        }
        if (v.TrafficStyle.Length == 0)
        {
            return ("", true);
        }
        var facts = new FlooringFacts { Definitions = v.Floors, Stock = v.Stock, Style = _ => (v.TrafficStyle, true) };
        var (name, why) = StyledFloor(new FloorDeficit { Tier = FloorTier.Traffic }, facts, batch, p.Traffic);
        if (name.Length != 0)
        {
            return (name, FloorPriced(v.Floors.GetValueOrDefault(name) ?? new FloorDefinition()));
        }
        return why == FlooringMethod.Unknown ? ("", false) : ("", true);
    }

    internal static bool FloorPriced(FloorDefinition d) => d.WorkToBuild.IsKnown && d.Costs.IsKnown && d.PathCost.IsKnown;

    /// <summary>One cell's one-time cost in colony work ticks: the native WorkToBuild plus MaterialTicksPerUnit for every unit of its cost list.</summary>
    private static double FloorTicks(FloorDefinition d, FlooringPolicy p)
    {
        var work = d.WorkToBuild.ValueOrDefault;
        _ = d.Costs.TryGetValue(out var costs);
        double units = 0;
        foreach (var a in costs ?? [])
        {
            units += a.Count;
        }
        return work + (p.MaterialTicksPerUnit * units);
    }

    /// <summary>
    /// Go <c>ReviewFlooring</c>: measures every tiered room against its requirement. There is no hysteresis:
    /// terrain does not flap. A cell whose floor is already ordered still counts as deficient, so the latch
    /// holds until the floor is actually laid, but the planner never orders it twice. An unknown census keeps the
    /// previous latch. Throws <see cref="DomainException"/> where Go returned an error.
    /// </summary>
    public static FlooringReview ReviewFlooring(Fact<FlooringObservation> fact, Fact<RoomObservation> rooms, IReadOnlyList<string> previous, FlooringPolicy p)
    {
        DomainException.Require(p.IsValid, "invalid flooring policy");
        if (!fact.TryGetValue(out var v))
        {
            var held = previous.Order(StringComparer.Ordinal).ToList();
            return new FlooringReview { Active = held.Count > 0, Latched = held };
        }
        v.Validate();
        var census = new Dictionary<string, Room>(StringComparer.Ordinal);
        if (rooms.TryGetValue(out var rc))
        {
            foreach (var room in rc.Rooms)
            {
                census[room.Id] = room;
            }
        }
        var deficits = new List<FloorDeficit>();
        var latched = new List<string>();
        var roomed = new HashSet<Cell>();
        foreach (var room in v.Rooms)
        {
            var tier = FloorTierOf(room, census);
            if (tier is null)
            {
                continue;
            }
            foreach (var c in room.Cells)
            {
                roomed.Add(c.Cell);
            }
            var role = room.Role.ValueOrDefault;
            var cells = new List<Cell>();
            var pending = 0;
            foreach (var c in room.Cells)
            {
                if (tier == FloorTier.Throne && HasAnyTag(v.TerrainOf(c.Terrain).Tags, room.RequiredTags))
                {
                    continue;
                }
                if (tier != FloorTier.Throne && !FloorDeficient(tier.Value, v.TerrainOf(c.Terrain)))
                {
                    continue;
                }
                if (c.Pending.Length != 0)
                {
                    pending++;
                    continue;
                }
                cells.Add(c.Cell);
            }
            if (cells.Count == 0 && pending == 0)
            {
                continue;
            }
            var key = FloorRoomKey(room);
            deficits.Add(
                new FloorDeficit
                {
                    Key = key,
                    Room = room.Id,
                    Tier = tier.Value,
                    Role = role,
                    Tags = tier == FloorTier.Throne ? room.RequiredTags : [],
                    Cells = SortCells(cells),
                    Pending = pending,
                }
            );
            latched.Add(key);
        }
        var (traffic, trafficOk) = TrafficDeficit(v, roomed, previous.Contains(TrafficKey), p);
        if (trafficOk)
        {
            deficits.Add(traffic);
            latched.Add(traffic.Key);
        }
        if (v.Firebreak.Count > 0)
        {
            var cells = new List<Cell>();
            foreach (var c in v.Firebreak)
            {
                if (!roomed.Contains(c) && !cells.Contains(c))
                {
                    cells.Add(c);
                }
            }
            if (cells.Count > 0)
            {
                deficits.Add(new FloorDeficit { Key = FirebreakKey, Tier = FloorTier.Firebreak, Cells = SortCells(cells) });
                latched.Add(FirebreakKey);
            }
        }
        if (v.Incinerator.Count > 0)
        {
            var cells = new List<Cell>();
            var pending = 0;
            foreach (var c in v.Incinerator)
            {
                var t = v.TerrainOf(c.Terrain);
                if (t.Flammability <= 0 && t.Fertility <= 0)
                {
                    continue;
                }
                if (c.Pending.Length != 0)
                {
                    pending++;
                    continue;
                }
                cells.Add(c.Cell);
            }
            if (cells.Count > 0 || pending > 0)
            {
                deficits.Add(new FloorDeficit { Key = IncineratorKey, Tier = FloorTier.Incinerator, Cells = SortCells(cells), Pending = pending });
                latched.Add(IncineratorKey);
            }
        }
        var ordered = deficits.OrderBy(d => FloorTierRank(d.Tier)).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
        latched.Sort(StringComparer.Ordinal);
        return new FlooringReview { Known = true, Active = ordered.Count > 0, Deficits = ordered, Latched = latched };
    }

    private static List<Cell> SortCells(List<Cell> cells)
    {
        cells.Sort((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0);
        return cells;
    }

    private static int FloorTierRank(FloorTier t) =>
        t switch
        {
            FloorTier.Clean => 0,
            FloorTier.Barn => 1,
            FloorTier.Living or FloorTier.Throne => 2,
            FloorTier.Incinerator => 3,
            FloorTier.Firebreak => 4,
            _ => 5,
        };

    /// <summary>Prices one candidate for a tier; Ok is false when the floor fails the tier's requirement outright.</summary>
    private static (double Score, bool Ok) FloorScore(FloorTier tier, FloorDefinition d, FloorWeights w)
    {
        var cleanliness = d.Cleanliness.ValueOrDefault;
        var beauty = d.Beauty.ValueOrDefault;
        var fk = d.Flammability.TryGetValue(out var flammability);
        var pathCost = d.PathCost.ValueOrDefault;
        if (tier.Fireproof() && (!fk || flammability > 0))
        {
            return (0, false);
        }
        if ((tier == FloorTier.Clean && cleanliness < 0) || (tier == FloorTier.Living && beauty < 0) || (tier == FloorTier.Traffic && pathCost > 0))
        {
            return (0, false);
        }
        double cost = 0;
        if (d.Costs.TryGetValue(out var costs))
        {
            foreach (var a in costs)
            {
                cost += a.Count;
            }
        }
        return ((w.Cleanliness * cleanliness) + (w.Beauty * beauty) - (w.PathCost * pathCost) - (w.Flammability * flammability) - (w.Cost * cost), true);
    }

    /// <summary>How many cells of the floor the stock pays for; -1 when the stock is unknown.</summary>
    internal static int AffordableCells(FloorDefinition d, Fact<IReadOnlyDictionary<Resource, long>> stock, int want)
    {
        var known = stock.TryGetValue(out var values);
        var ck = d.Costs.TryGetValue(out var costs);
        if (!known || !ck)
        {
            return -1;
        }
        var n = want;
        foreach (var a in costs!)
        {
            if (a.Count <= 0)
            {
                continue;
            }
            n = (int)Math.Min(n, values!.GetValueOrDefault(a.Resource) / a.Count);
        }
        return n;
    }

    /// <summary>
    /// Go <c>SelectFlooringMethod</c>: resolves the first deficit: clean workspaces before living rooms.
    /// Candidates must be known available, be terrain, meet the tier's requirement and be affordable for at
    /// least one cell; a floor that pays for the whole batch beats one that pays for part of it, then the tier's
    /// score decides, except the tiers that lay the tier style's floor or none. A room whose deficient cells are
    /// all ordered already waits for them. The traffic deficit lays the floor its cells were priced on. A
    /// traffic latch the review could not price is unknown. Throws where Go returned an error.
    /// </summary>
    public static FlooringProposal SelectFlooringMethod(FlooringReview review, FlooringFacts facts, FlooringPolicy p)
    {
        DomainException.Require(p.IsValid, "invalid flooring policy");
        if (!review.Active)
        {
            return new FlooringProposal { Method = FlooringMethod.NoMethod };
        }
        if (!review.Known)
        {
            return new FlooringProposal { Method = FlooringMethod.Unknown };
        }
        var deferred = FlooringMethod.None;
        foreach (var d in review.Deficits)
        {
            if (d.Unknown)
            {
                deferred = FirstFlooringReason(deferred, FlooringMethod.Unknown);
                continue;
            }
            if (d.Cells.Count == 0)
            {
                deferred = FirstFlooringReason(deferred, FlooringMethod.Pending);
                continue;
            }
            var batch = Math.Min(d.Cells.Count, p.MaxCellsPerPlan);
            var (name, count, reason) = ChooseFloor(d, facts, batch, p);
            if (name.Length == 0)
            {
                deferred = FirstFlooringReason(deferred, reason);
                continue;
            }
            var cells = d.Cells.Take(count).ToList();
            var text = $"{d.Key}/{Wire.Name(d.Tier)}/{name}/{cells[0].X},{cells[0].Z}/{cells.Count}";
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return new FlooringProposal
            {
                Method = FlooringMethod.Build,
                Key = new MethodId("flooring-" + Convert.ToHexStringLower(digest.AsSpan(0, 12))),
                Room = d.Room,
                Tier = d.Tier,
                Definition = name,
                Cells = cells,
            };
        }
        return new FlooringProposal { Method = deferred == FlooringMethod.None ? FlooringMethod.NoMethod : deferred };
    }

    /// <summary>
    /// The one floor decision: the floor, and how many of the batch's cells it pays for, that a deficit's tier
    /// lays; no name and the reason when none can be (research, materials or an unknown fact).
    /// <see cref="SelectFlooringMethod"/> lays a batch of it; <c>WantedFloors</c> reads it for a planned room's
    /// every cell.
    /// </summary>
    internal static (string Name, int Cells, FlooringMethod Reason) ChooseFloor(FloorDeficit d, FlooringFacts facts, int batch, FlooringPolicy p)
    {
        var weights = d.Tier switch
        {
            FloorTier.Living or FloorTier.Throne => p.Living,
            FloorTier.Traffic => p.Traffic,
            FloorTier.Firebreak or FloorTier.Incinerator => FirebreakWeights,
            _ => p.Clean,
        };
        string? bestName = null;
        var bestCells = 0;
        var bestScore = 0.0;
        var reason = FlooringMethod.ResearchNeeded;
        var unknown = false;
        // Only the throne and a priced traffic aisle score a list; every other tier lays the tier style's floor or none.
        List<string> scored;
        if (d.Tier == FloorTier.Throne)
        {
            // Any floor the mirror tags as the title requires, however the policy lists floors; none known is a fact gap.
            scored = [.. facts.Definitions.Where(kv => HasAnyTag(kv.Value.Tags, d.Tags)).Select(kv => kv.Key).Order(StringComparer.Ordinal)];
            if (scored.Count == 0)
            {
                unknown = true;
            }
        }
        else if (d.Floor.Length != 0)
        {
            // The traffic tier lays the floor its payback was priced on.
            scored = [d.Floor];
        }
        else
        {
            // Every other tier lays the tier style's floor or none: a style that names nothing, or whose floor
            // cannot be laid, is a reason to wait, never a cheaper substitute from the policy list.
            if (d.Tier.Fireproof())
            {
                d = d with { Role = RoomRole.None };
            }
            var (styled, why) = StyledFloor(d, facts, batch, weights);
            return styled.Length == 0 ? ("", 0, why) : (styled, batch, FlooringMethod.None);
        }
        foreach (var name in scored)
        {
            if (!facts.Definitions.TryGetValue(name, out var def))
            {
                unknown = true;
                continue;
            }
            var ak = def.Available.TryGetValue(out var available);
            var tk = def.Terrain.TryGetValue(out var terrain);
            if (!ak || !tk)
            {
                unknown = true;
                continue;
            }
            if (!available || !terrain)
            {
                continue;
            }
            var (score, meets) = FloorScore(d.Tier, def, weights);
            if (!meets)
            {
                continue;
            }
            var cells = AffordableCells(def, facts.Stock, batch);
            if (cells < 0)
            {
                cells = batch;
            }
            if (cells == 0)
            {
                reason = FlooringMethod.MaterialsNeeded;
                continue;
            }
            if (bestName is null || cells > bestCells || (cells == bestCells && score > bestScore))
            {
                bestName = name;
                bestCells = cells;
                bestScore = score;
            }
        }
        if (bestName is null)
        {
            return ("", 0, unknown ? FlooringMethod.Unknown : reason);
        }
        return (bestName, bestCells, FlooringMethod.None);
    }

    private static FlooringMethod FirstFlooringReason(FlooringMethod current, FlooringMethod next) =>
        current is FlooringMethod.None or FlooringMethod.Unknown ? next : current;

    /// <summary>
    /// The style's floor for the deficit when it can be laid now: known available terrain that meets the tier's
    /// requirement and pays for the whole batch. Otherwise no name and the reason it cannot be laid: none while
    /// the style names no floor (Camp), research, materials or an unknown fact.
    /// </summary>
    private static (string Name, FlooringMethod Reason) StyledFloor(FloorDeficit d, FlooringFacts facts, int batch, FloorWeights weights)
    {
        if (facts.Style is null)
        {
            return ("", FlooringMethod.None);
        }
        var (name, ok) = facts.Style(d.Role);
        if (!ok)
        {
            return ("", FlooringMethod.MaterialsNeeded);
        }
        if (!facts.Definitions.TryGetValue(name, out var def))
        {
            return ("", FlooringMethod.Unknown);
        }
        var ak = def.Available.TryGetValue(out var available);
        var tk = def.Terrain.TryGetValue(out var terrain);
        if (!ak || !tk)
        {
            return ("", FlooringMethod.Unknown);
        }
        if (!available || !terrain)
        {
            return ("", FlooringMethod.ResearchNeeded);
        }
        if (!FloorScore(d.Tier, def, weights).Ok)
        {
            return ("", FlooringMethod.Unknown);
        }
        var cells = AffordableCells(def, facts.Stock, batch);
        if (cells >= 0 && cells < batch)
        {
            return ("", FlooringMethod.MaterialsNeeded);
        }
        return (name, FlooringMethod.None);
    }
}
