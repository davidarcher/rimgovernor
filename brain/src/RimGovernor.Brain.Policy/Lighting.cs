using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Ports <c>lighting.go</c>. MaintainLighting keeps the cells colonists stand on while working lit. It reasons from
/// the measured native glow at each bench's interaction cell rather than from fixture counts: a roofed work cell
/// whose ground glow is below LitGlow is dark whatever lamps sit nearby. The method first looks for an existing lamp
/// that should cover the cell and defers to power/refuel/repair work when that lamp is merely unserviced; otherwise
/// it places an affordable lamp beside the cell and lets the next measured census release the latch.
/// </summary>
public sealed record LightingPolicy
{
    /// <summary>The ground glow at or above which a cell counts as lit; RimWorld's own darkness penalties use 0.3.</summary>
    public double LitGlow { get; init; }

    /// <summary>Bounds the Chebyshev distance from the dark cell at which a new lamp may stand.</summary>
    public int PlacementRadius { get; init; }

    /// <summary>Ranks the fixture definitions to consider; powered ones are only chosen while the colony has an active power source.</summary>
    public IReadOnlyList<LampDefinition> Lamps { get; init; } = [];

    public static LightingPolicy Default() =>
        new()
        {
            LitGlow = 0.3,
            PlacementRadius = 2,
            Lamps = [new LampDefinition { Name = "StandingLamp", Powered = true }, new LampDefinition { Name = "TorchLamp" }],
        };

    internal bool IsValid()
    {
        if (!(LitGlow > 0 && LitGlow <= 1) || PlacementRadius < 1 || PlacementRadius > 8 || Lamps.Count == 0)
        {
            return false;
        }
        foreach (var l in Lamps)
        {
            if (!PolicyFunctions.FoodId(l.Name))
            {
                return false;
            }
        }
        return true;
    }
}

public sealed record LampDefinition
{
    public string Name { get; init; } = "";
    public bool Powered { get; init; }
}

/// <summary>The native lighting census: every colonist bench's interaction cell with its measured glow, and every glowing fixture.</summary>
public sealed record LightingObservation
{
    public IReadOnlyList<WorkLightCell> WorkCells { get; init; } = [];
    public IReadOnlyList<Lamp> Lamps { get; init; } = [];

    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in WorkCells)
        {
            if (!PolicyFunctions.FoodId(c.Bench) || seen.Contains(c.Bench) || double.IsNaN(c.Glow) || c.Glow < 0 || c.Glow > 1)
            {
                throw new DomainException("invalid work light cell");
            }
            seen.Add(c.Bench);
        }
        var lamps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in Lamps)
        {
            if (!PolicyFunctions.FoodId(l.Id) || lamps.Contains(l.Id) || double.IsNaN(l.Radius) || l.Radius < 0)
            {
                throw new DomainException("invalid lamp");
            }
            lamps.Add(l.Id);
        }
    }
}

public sealed record WorkLightCell
{
    public string Bench { get; init; } = "";
    public string Definition { get; init; } = "";
    public Cell Cell { get; init; }
    public double Glow { get; init; }
    public bool Roofed { get; init; }
    public Fact<string> Room { get; init; }

    /// <summary>
    /// Native's word that the cell's room grows a plant that dies to light (cave fungus); such a room is protected
    /// and the cell is never a lighting deficit however dark it measures.
    /// </summary>
    public bool LightSensitive { get; init; }
}

public sealed record Lamp
{
    public string Id { get; init; } = "";
    public string Definition { get; init; } = "";
    public Cell Cell { get; init; }
    public double Radius { get; init; }

    /// <summary>Native's own "currently emits light" flag, false while the fixture is unpowered, out of fuel, switched off or broken down.</summary>
    public bool Lit { get; init; }

    public Fact<string> Room { get; init; }
    public Fact<bool> Powered { get; init; }
    public Fact<bool> Connected { get; init; }
    public Fact<bool> SwitchedOn { get; init; }
    public Fact<bool> OutOfFuel { get; init; }
    public Fact<bool> BrokenDown { get; init; }
    public IReadOnlyList<string> FuelDefinitions { get; init; } = [];
}

/// <summary>
/// The per-bench latch: Dark lists the roofed work cells measured below LitGlow, sorted by bench ID. Known is false
/// when the census was unknown, in which case Dark repeats the previous latch.
/// </summary>
public sealed record LightingReview
{
    public bool Active { get; init; }
    public IReadOnlyList<string> Dark { get; init; } = [];
    public bool Known { get; init; }

    /// <summary>
    /// The review measured unroofed work cells too: the sky gives no light by day (an eclipse, or a biome whose map
    /// conditions black it out for good), so a dark outdoor bench is a deficit a torch answers for as long as that lasts.
    /// </summary>
    public bool SkyDark { get; init; }

    /// <summary>
    /// The built, powered rooms the layout plan lights whose planned lamps are not all standing (PlanLitRooms), by
    /// room id; set by WithPlanned, recomputed each review (a standing lamp releases it).
    /// </summary>
    public IReadOnlyList<int> Planned { get; init; } = [];

    /// <summary>Folds the plan's lighting deficit into the review: a listed room keeps the project open beside the dark benches. An unknown fact adds none.</summary>
    public LightingReview WithPlanned(Fact<EquatableArray<int>> rooms)
    {
        if (rooms.TryGetValue(out var ids))
        {
            return this with { Planned = ids, Active = Active || ids.Count > 0 };
        }
        return this;
    }
}

public enum LightingMethod
{
    [WireName("")]
    None = 0,
    Unknown,

    [WireName("no_deficit")]
    NoMethod,

    [WireName("lamp_power_needed")]
    PowerNeeded,

    [WireName("lamp_fuel_needed")]
    FuelNeeded,

    [WireName("lamp_repair_needed")]
    RepairNeeded,

    [WireName("lamp_switched_off")]
    SwitchedOff,

    [WireName("lamp_lit_but_cell_dark")]
    Blocked,

    [WireName("lamp_research_needed")]
    ResearchNeeded,

    [WireName("no_lamp_cell")]
    NoSpace,

    [WireName("build_lamp")]
    Build,
}

public sealed record LightingProposal
{
    public LightingMethod Method { get; init; }
    public MethodId Key { get; init; }

    /// <summary>Bench and Target name the dark work cell served.</summary>
    public string Bench { get; init; } = "";

    public Cell Target { get; init; }

    /// <summary>Definition and Cells are the chosen lamp and its candidate cells nearest first, each to be validated by a native placement preview before admission.</summary>
    public string Definition { get; init; } = "";

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>
    /// The layout plan's lamps for the target's room not yet standing, in plan order: tried before Cells, each
    /// previewed as its own definition and facing (a wall lamp hangs on the wall it faces).
    /// </summary>
    public IReadOnlyList<PlannedLamp> Planned { get; init; } = [];
}

/// <summary>What SelectLightingMethod needs beyond the census.</summary>
public sealed record LightingFacts
{
    public IReadOnlyList<Room> Rooms { get; init; } = [];
    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    /// <summary>Maps lamp definition names to their planning availability as read with the census; a lamp absent here is unknown.</summary>
    public IReadOnlyDictionary<string, Fact<bool>> Available { get; init; } = new Dictionary<string, Fact<bool>>();

    /// <summary>Whether any power network currently has an active source; unknown defers powered lamps.</summary>
    public Fact<bool> PoweredSource { get; init; }

    /// <summary>The fixture the tier style names (PlannedLighting): chosen ahead of the policy's lamps when it is known available, empty when the tier styles none.</summary>
    public string Styled { get; init; } = "";

    /// <summary>The colony's layout plan; its planned lamps answer a dark bench before a lamp is improvised beside it (empty when no plan is accepted).</summary>
    public LayoutPlan Plan { get; init; } = new();
}

public static partial class PolicyFunctions
{
    /// <summary>Keeps the cells colonists stand on while working lit.</summary>
    public static readonly ConcernId MaintainLighting = new("MaintainLighting");

    /// <summary>
    /// Measures every roofed work cell against LitGlow. There is no hysteresis: a lamp within reach lifts the cell
    /// well above the threshold, and unroofed cells are skipped because sky glow would flap the latch with the day,
    /// except while the sky is dark (an eclipse, or a permanently dark biome), when the day is as dark as the night
    /// and every work cell is measured. A light-sensitive cell (its room grows cave fungus) is never dark: lighting
    /// it would kill the crop. An unknown census keeps the previous dark set. An unknown sky (the biome was not
    /// read) throws once a census is measured, never assumed lit.
    /// </summary>
    public static LightingReview ReviewLighting(Fact<LightingObservation> fact, IReadOnlyList<string> previous, LightingPolicy p, Fact<bool> skyDarkFact)
    {
        if (!p.IsValid())
        {
            throw new DomainException("invalid lighting policy");
        }
        var skyKnown = skyDarkFact.TryGetValue(out var skyDark);
        if (!fact.TryGetValue(out var v))
        {
            var prior = previous.ToList();
            prior.Sort(StringComparer.Ordinal);
            return new LightingReview { Active = prior.Count > 0, Dark = prior, SkyDark = skyDark };
        }
        if (!skyKnown)
        {
            throw new DomainException("outdoors darkness is unknown");
        }
        v.Validate();
        var dark = new List<string>();
        foreach (var c in v.WorkCells)
        {
            if ((c.Roofed || skyDark) && !c.LightSensitive && c.Glow < p.LitGlow)
            {
                dark.Add(c.Bench);
            }
        }
        dark.Sort(StringComparer.Ordinal);
        return new LightingReview { Known = true, SkyDark = skyDark, Dark = dark, Active = dark.Count > 0 };
    }

    /// <summary>
    /// Resolves the lowest-sorted dark bench. An existing lamp whose radius reaches the cell but which is not lit is
    /// somebody else's job (power, refueling, repair, flicking) and yields a deferring outcome. A lit lamp already
    /// standing within PlacementRadius that still leaves the cell dark is reported as blocked rather than doubled; a
    /// lit lamp further away whose radius only grazes the cell is partial coverage (glow falls off with distance and
    /// stops at walls), and the cell gets its own lamp. Otherwise the first affordable definition is placed on the
    /// nearest free walkable cell of the same room within PlacementRadius.
    /// </summary>
    public static LightingProposal SelectLightingMethod(LightingReview review, Fact<LightingObservation> fact, LightingFacts facts, LightingPolicy p)
    {
        if (!p.IsValid())
        {
            throw new DomainException("invalid lighting policy");
        }
        if (!review.Active)
        {
            return new LightingProposal { Method = LightingMethod.NoMethod };
        }
        if (!fact.TryGetValue(out var v))
        {
            return new LightingProposal { Method = LightingMethod.Unknown };
        }
        v.Validate();
        var cells = new Dictionary<Cell, SiteCell>();
        foreach (var c in facts.Cells)
        {
            cells[c.Cell] = c;
        }
        var cellRoom = new Dictionary<Cell, string>();
        foreach (var room in facts.Rooms)
        {
            foreach (var c in room.Cells)
            {
                cellRoom[c] = room.Id;
            }
        }
        var work = new Dictionary<string, WorkLightCell>(StringComparer.Ordinal);
        var occupied = new HashSet<Cell>();
        foreach (var c in v.WorkCells)
        {
            work[c.Bench] = c;
            occupied.Add(c.Cell);
        }
        foreach (var l in v.Lamps)
        {
            occupied.Add(l.Cell);
        }
        var deferred = LightingMethod.None;
        foreach (var bench in review.Dark)
        {
            if (!work.TryGetValue(bench, out var target) || (!target.Roofed && !review.SkyDark) || target.Glow >= p.LitGlow)
            {
                // The census no longer lists the bench as dark: the latch is stale, and the next review releases it.
                continue;
            }
            var serving = v.Lamps.Where(l => Distance(l.Cell, target.Cell) <= l.Radius).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
            var outcome = ServingOutcome(serving, target.Cell, p.PlacementRadius);
            if (outcome != LightingMethod.None)
            {
                deferred = FirstLightingReason(deferred, outcome);
                continue;
            }
            // The plan's own lamps for the bench's room come first, while the colony has power for them; the
            // improvised lamp beside the cell stays as the fallback when none of them can stand yet.
            List<PlannedLamp> planned = [];
            if (facts.PoweredSource.TryGetValue(out var source) && source)
            {
                planned = UnbuiltPlannedLamps(facts.Plan.PlannedLampsAt(target.Cell), v.Lamps);
            }
            var (definition, method) = SelectLampDefinition(facts, p);
            List<Cell> candidates = [];
            if (method == LightingMethod.None)
            {
                candidates = LampCells(target.Cell, cellRoom, cells, occupied, p.PlacementRadius);
                if (candidates.Count == 0)
                {
                    method = LightingMethod.NoSpace;
                }
            }
            if (method != LightingMethod.None && planned.Count == 0)
            {
                deferred = FirstLightingReason(deferred, method);
                continue;
            }
            if (planned.Count > 0)
            {
                definition = planned[0].Def;
            }
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{bench}/{definition}/{target.Cell.X},{target.Cell.Z}"));
            return new LightingProposal
            {
                Method = LightingMethod.Build,
                Key = new MethodId("lighting-" + Convert.ToHexStringLower(digest.AsSpan(0, 12))),
                Bench = bench,
                Target = target.Cell,
                Definition = definition,
                Cells = candidates,
                Planned = planned,
            };
        }
        // No dark bench to build for: a built, powered room the plan lights with lamps still missing gets them,
        // the first such room in id order.
        if (facts.PoweredSource.TryGetValue(out var sourceNow) && sourceNow)
        {
            foreach (var id in review.Planned)
            {
                facts.Plan.Lamps.TryGetValue(id, out var roomLamps);
                var planned = UnbuiltPlannedLamps(roomLamps ?? [], v.Lamps);
                if (planned.Count == 0)
                {
                    continue;
                }
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"room/{id}/{planned[0].Def}/{planned[0].Cell.X},{planned[0].Cell.Z}"));
                return new LightingProposal
                {
                    Method = LightingMethod.Build,
                    Key = new MethodId("lighting-" + Convert.ToHexStringLower(digest.AsSpan(0, 12))),
                    Target = planned[0].Cell,
                    Definition = planned[0].Def,
                    Planned = planned,
                };
            }
        }
        if (deferred == LightingMethod.None)
        {
            return new LightingProposal { Method = LightingMethod.NoMethod };
        }
        return new LightingProposal { Method = deferred };
    }

    /// <summary>
    /// Classifies the lamps whose radius reaches a dark cell: the lowest-ID unlit one names the service it waits
    /// for; with every lamp lit, one already within the placement radius means the cell is blocked; lit lamps only
    /// reaching from further away leave the cell partially lit and yield None so a lamp of its own is placed.
    /// </summary>
    private static LightingMethod ServingOutcome(IReadOnlyList<Lamp> serving, Cell target, int radius)
    {
        var near = false;
        foreach (var l in serving)
        {
            if (!l.Lit)
            {
                return LampService(l);
            }
            near = near || Chebyshev(l.Cell, target) <= radius;
        }
        return near ? LightingMethod.Blocked : LightingMethod.None;
    }

    private static LightingMethod FirstLightingReason(LightingMethod current, LightingMethod next) =>
        current == LightingMethod.None || current == LightingMethod.Unknown ? next : current;

    /// <summary>Classifies why an in-range lamp is dark, most specific first. Unknown service facts fall through to the generic power outcome.</summary>
    private static LightingMethod LampService(Lamp l)
    {
        if (l.BrokenDown.TryGetValue(out var broken) && broken)
        {
            return LightingMethod.RepairNeeded;
        }
        if (l.OutOfFuel.TryGetValue(out var outOfFuel) && outOfFuel)
        {
            return LightingMethod.FuelNeeded;
        }
        if (l.SwitchedOn.TryGetValue(out var on) && !on)
        {
            return LightingMethod.SwitchedOff;
        }
        if (l.OutOfFuel.IsKnown)
        {
            // A fueled lamp with fuel that still does not glow is refuel scheduling, not construction.
            return LightingMethod.FuelNeeded;
        }
        return LightingMethod.PowerNeeded;
    }

    /// <summary>
    /// The first policy lamp that is available and, when powered, backed by an active power source. Unknown
    /// availability or source facts defer rather than guess.
    /// </summary>
    private static (string Definition, LightingMethod Method) SelectLampDefinition(LightingFacts facts, LightingPolicy p)
    {
        var deferred = LightingMethod.None;
        if (facts.Styled != "" && facts.Available.GetValueOrDefault(facts.Styled).TryGetValue(out var styledOk) && styledOk)
        {
            return (facts.Styled, LightingMethod.None);
        }
        foreach (var lamp in p.Lamps)
        {
            if (!facts.Available.GetValueOrDefault(lamp.Name).TryGetValue(out var ok))
            {
                deferred = FirstLightingReason(deferred, LightingMethod.Unknown);
                continue;
            }
            if (!ok)
            {
                deferred = FirstLightingReason(deferred, LightingMethod.ResearchNeeded);
                continue;
            }
            if (lamp.Powered)
            {
                if (!facts.PoweredSource.TryGetValue(out var source))
                {
                    deferred = FirstLightingReason(deferred, LightingMethod.Unknown);
                    continue;
                }
                if (!source)
                {
                    continue;
                }
            }
            return (lamp.Name, LightingMethod.None);
        }
        if (deferred == LightingMethod.None)
        {
            deferred = LightingMethod.ResearchNeeded;
        }
        return ("", deferred);
    }

    /// <summary>
    /// Lists the free, walkable, unoccupied, unzoned site cells of the target's room within radius of the target
    /// (never the target itself), nearest first and then in cell order, so the planner previews a stable sequence.
    /// </summary>
    private static List<Cell> LampCells(Cell target, Dictionary<Cell, string> cellRoom, Dictionary<Cell, SiteCell> cells, HashSet<Cell> occupied, int radius)
    {
        var inRoom = cellRoom.TryGetValue(target, out var room);
        var found = new List<Cell>();
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                var c = new Cell(target.X + dx, target.Z + dz);
                if (c == target || occupied.Contains(c))
                {
                    continue;
                }
                if (inRoom && (!cellRoom.TryGetValue(c, out var other) || other != room))
                {
                    continue;
                }
                if (!cells.TryGetValue(c, out var site))
                {
                    continue;
                }
                if (!site.Walkable.TryGetValue(out var walkable) || !walkable || site.Occupied())
                {
                    continue;
                }
                if (site.Zone.TryGetValue(out var zone) && zone)
                {
                    continue;
                }
                found.Add(c);
            }
        }
        return [.. found.OrderBy(c => Chebyshev(c, target)).ThenBy(c => c.Z).ThenBy(c => c.X)];
    }

    internal static double Distance(Cell a, Cell b)
    {
        double dx = a.X - b.X;
        double dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    internal static int Chebyshev(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

    /// <summary>The planned lamps no observed lamp stands on, in plan order.</summary>
    private static List<PlannedLamp> UnbuiltPlannedLamps(IReadOnlyList<PlannedLamp> planned, IReadOnlyList<Lamp> standing) =>
        [.. planned.Where(l => !standing.Any(s => s.Cell == l.Cell))];

    /// <summary>
    /// The plan's lighting deficit: the ids of the rooms the plan lights (LayoutPlan.Lamps, placed only where
    /// lighting the dark cost less than leaving it, priced in sustained watts by the tiler) that stand built (an
    /// enclosed census room within the planned region) while a planned lamp is unbuilt and a power network has an
    /// active source, in room order. A room the plan found darkness cheaper for has no lamps and is never listed;
    /// no active source lists nothing (the lamps are electric); an unknown plan, room census, lamp census or source
    /// is unknown.
    /// </summary>
    public static Fact<EquatableArray<int>> PlanLitRooms(Fact<LayoutPlan> plan, Fact<RoomObservation> rooms, Fact<LightingObservation> lamps, Fact<bool> powered)
    {
        if (!plan.TryGetValue(out var p) || !rooms.TryGetValue(out var r) || !lamps.TryGetValue(out var l) || !powered.TryGetValue(out var source))
        {
            return Unknown<EquatableArray<int>>();
        }
        var found = new List<int>();
        if (!source)
        {
            return Known(EquatableArray.From(found));
        }
        foreach (var room in p.Rooms)
        {
            p.Lamps.TryGetValue(room.Id, out var roomLamps);
            if (UnbuiltPlannedLamps(roomLamps ?? [], l.Lamps).Count == 0)
            {
                continue;
            }
            var (_, built) = CensusRoomIn(room, r);
            if (built)
            {
                found.Add(room.Id);
            }
        }
        return Known(EquatableArray.From(found));
    }

    internal static int Abs32(int v) => v < 0 ? -v : v;
}
