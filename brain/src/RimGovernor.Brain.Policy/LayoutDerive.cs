using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Brain.Policy;

// Layout plans grow from a survey. Replanning re-zones the map, drops rooms unsupported by terrain and sites
// missing rooms. Core candidates are derived planning input and are not saved.

/// <summary>A replan whose tiled plan holds no room: the colony keeps the plan it has.</summary>
public sealed class ReplanEmptyException() : DomainException("the tiled plan holds no room");

/// <summary>The rooms the plan is asked to add beyond the core: a throne room of at least ThroneArea cells (0 asks for none), the Child rooms the plan lacks and the gear rooms Gear asks for.</summary>
public sealed record RoomGrowth
{
    public int ThroneArea { get; init; }

    public RoomDemand Demand { get; init; } = new();

    /// <summary>The planned rooms of an ended role still standing or furnished; the replan holds them where they stand.</summary>
    public HashSet<int> InUse { get; init; } = [];

    /// <summary>
    /// The interiors of every planned room with anything of ours on it (FixedRooms), set on every replan once
    /// the census is known (null while it is not: every room is then kept). A replan re-sites only the rooms
    /// outside it.
    /// </summary>
    public HashSet<int>? Fixed { get; init; }

    /// <summary>ShelterRetirable at this review.</summary>
    public bool RetireShelter { get; init; }

    /// <summary>The planned rooms an open journal plan works on, which a retirement leaves in the plan.</summary>
    public HashSet<int> InFlight { get; init; } = [];

    /// <summary>The herds' units (HerdPlan.HerdUnits): each gets a barn of its own beside the misc unit's.</summary>
    public IReadOnlyList<HerdCeiling> HerdUnits { get; init; } = [];

    /// <summary>
    /// The pens the herd asks for (HerdPlan.PenWants): each unit short of its cells gets a pen beside its barn
    /// (PlanPenSites). Empty while the animal census or the pasture table is unread, which asks for none.
    /// </summary>
    public IReadOnlyList<PenWant> Pens { get; init; } = [];

    /// <summary>The survey cells the pens may stand on (GrazingGround); null while the pasture table is unread.</summary>
    public HashSet<Cell>? Grazing { get; init; }

    /// <summary>The superseded bedrooms a pawn still owns a bed in (SleptBedrooms): they stay in the plan after their successor stands.</summary>
    public HashSet<int> Slept { get; init; } = [];

    /// <summary>
    /// What the defense record holds of the ring's nodes: a ring traced again over a held one is laid as a
    /// replacement beside it, and not at all while a rebuild is in flight (layout_perimeter_replace.go).
    /// </summary>
    public PerimeterHold Perimeter { get; init; } = new();
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Lays a fresh plan over the survey from the station chain: the facts (a first plan's, StationFacts.Derive)
    /// go through the graph, the clustering and the embedding, and the tiler (TileEmbedding) turns the embedding
    /// into the rooms, doors, links, roofs and lamps. What the tiler does not plan is laid over its rooms: a
    /// barn for a herd of animals (HerdPlan.PenAnimals), the yards, the perimeter and its mountain pockets. The
    /// pens are sited by the replan, once the pasture is read. A geothermal enclosure is reserved on each
    /// reported steam geyser and rooms stay off it; the turbines and solar plots the planned demand needs
    /// (derivePowerSites) are placed beside the rooms by the site embedding and stored with their wind lanes,
    /// and the tiler routes the conduit net to them; the outskirts are rooms of the chain itself. The advice is
    /// returned beside the plan for the caller to journal. Unknown when the survey holds no room for a core; the
    /// failure is the chain's (StationFactsException for a fact the colony has not read and the chain needs).
    /// </summary>
    public static (Fact<LayoutPlan> Plan, StationAdvice Advice) DeriveLayoutPlan(MapSurvey s, StationFacts f, IReadOnlyList<PowerGeyser> geysers, int animals)
    {
        var zones = Zone(s);
        var footprints = GeyserFootprints(geysers);
        f = f with
        {
            Derive = true,
            Built = new HashSet<int>(),
            Survey = s,
            Plan = new LayoutPlan
            {
                Zones = CoreWithout(zones, GeothermalCells(footprints)),
                Cold = s.Cold,
                Hot = s.Hot,
                Reservations = GeothermalReservations(zones, footprints),
            },
        };
        var advice = TileStations(f);
        var plan = advice.Tiled.Plan;
        if (plan.Rooms.Count == 0)
        {
            return (Unknown<LayoutPlan>(), advice);
        }
        // The survey's zones less its field patches, then the fields the tiler placed.
        plan = plan with { Zones = [.. WithFieldZones(zones, null), .. plan.Zones.Skip(f.Plan.Zones.Count)], Cold = s.Cold, Hot = s.Hot };
        plan = PlanHerdSites(plan, animals);
        plan = PlanYardSites(plan, YardRoomsWanted(new RoomDemand()));
        plan = PlanMountainPockets(PlanPerimeter(plan, s), s);
        return (Known(WithoutCore(plan).WithRoomIDs()), advice);
    }

    /// <summary>The enclosures of the geysers whose cells all lie on core ground; a site that does not fit is left out.</summary>
    private static List<LayoutReservation> GeothermalReservations(IReadOnlyList<LayoutZone> zones, IReadOnlyList<Rectangle> footprints)
    {
        var core = new HashSet<Cell>();
        foreach (var z in zones)
        {
            if (z.Kind != ZoneKind.Core)
            {
                continue;
            }
            foreach (var r in z.Runs)
            {
                for (var x = r.X; x < r.X + r.Length; x++)
                {
                    core.Add(new Cell(x, r.Z));
                }
            }
        }
        var output = new List<LayoutReservation>();
        foreach (var g in footprints)
        {
            var area = GeothermalArea(g);
            if (!RectangleCells(area).Any(c => !core.Contains(c)))
            {
                output.Add(new LayoutReservation { Kind = ReservationKind.Geothermal, Area = RectRegion(area) });
            }
        }
        return output;
    }

    /// <summary>
    /// The next plan of a review (#3005, J4 of #2965): the tiled plan of the station chain, wholesale, over the
    /// plan prev the colony holds. f are the facts of the review (the plan's Built rooms, the chain's inputs):
    /// the tiler keeps every standing room it has a node for, pins the rooms growth holds (TilePinned) and sites
    /// the rest again. A standing room with no node in the new embedding leaves the plan and its ground is
    /// retired (RetiredGround) for clearance; the embedder's switching price (#2877) decides what moves. The
    /// rooms the tiler does not plan are laid over its plan as DeriveLayoutPlan lays them: the barns, pens and
    /// yards of prev that clear the new rooms stay and the herd's and the yard's shortfall is sited, then the
    /// perimeter is traced around what the defense record holds of it (LayPerimeter). The failure is the
    /// chain's or the tiler's (StationFactsException for a fact the colony has not read, TileException for a
    /// plan it cannot tile, ReplanEmptyException); the caller keeps the plan it has and journals the failure.
    /// </summary>
    public static (LayoutPlan Plan, StationAdvice Advice) ReplanLayout(LayoutPlan prev, StationFacts f, MapSurvey s, IReadOnlyList<PowerGeyser> geysers, RoomGrowth growth, int animals)
    {
        prev = prev.WithRoomIDs();
        f = f with { Plan = prev, Pinned = TilePinned(prev, growth) };
        f = ReplanTileFacts(f, s, geysers);
        var advice = TileStations(f);
        var plan = advice.Tiled.Plan;
        if (plan.Rooms.Count == 0)
        {
            throw new ReplanEmptyException();
        }
        // The survey's zones less its field patches, then the fields the tiler placed.
        plan = plan with { Zones = [.. WithFieldZones(Zone(s), null), .. plan.Zones.Skip(f.Plan.Zones.Count)], Cold = s.Cold, Hot = s.Hot };
        var tiled = plan;
        plan = KeepHeld(prev, plan, growth);
        var (conduits, joined) = RejoinHeld(prev, tiled, plan, s);
        plan = plan with { Conduits = EquatableArray.From(conduits) };
        advice.Tiled.ConduitDecisions = [.. advice.Tiled.ConduitDecisions, .. joined];
        plan = plan with { RetiredGround = RetiredGround(prev, plan, f.Built) };
        plan = plan with { Reservations = [.. plan.Reservations, .. CarriedSites(prev, plan)] };
        plan = PlanHerdSites(plan, animals, growth.HerdUnits);
        plan = PlanPenSites(plan, growth.Grazing, growth.Pens);
        plan = PlanYardSites(plan, YardRoomsWanted(growth.Demand));
        return (LayPerimeter(prev, plan, s, growth.Perimeter).WithRoomIDs(), advice);
    }

    /// <summary>
    /// Puts back the rooms of prev the colony holds (heldRooms) that the tiled plan left out, with their
    /// attached rooms, where they stood: a node-less room an open plan works on, a pawn sleeps in or a role that
    /// ended still uses does not leave the plan under it, and nothing demolishes it. A successor that stands
    /// keeps the Replaces the tiler dropped, so a bedroom a pawn still sleeps in stays the one the migration
    /// moves its sleepers out of.
    /// </summary>
    private static LayoutPlan KeepHeld(LayoutPlan prev, LayoutPlan next, RoomGrowth growth)
    {
        var held = HeldRooms(prev, growth);
        var have = new HashSet<int>(next.Rooms.Select(r => r.Id));
        var before = new Dictionary<int, PlannedRoom>();
        foreach (var r in prev.Rooms)
        {
            before[r.Id] = r;
        }
        var rooms = new List<PlannedRoom>(next.Rooms);
        for (var i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            if (before.TryGetValue(r.Id, out var old) && r.Replaces == 0 && old.Replaces != 0 && held.Contains(old.Replaces))
            {
                rooms[i] = r with { Replaces = old.Replaces };
            }
        }
        foreach (var r in prev.Rooms)
        {
            if (held.Contains(r.Id) && !have.Contains(r.Id) && !r.Attached())
            {
                rooms.Add(r);
                rooms.AddRange(prev.ChildrenOf(r.Id));
            }
        }
        return next with { Rooms = rooms };
    }

    /// <summary>
    /// Joins the rooms KeepHeld put back into the tiled plan's conduit net (#2966). The tiler routes only the
    /// rooms it planned; a held room that drew power through a link in prev (or its predecessor's) keeps that
    /// link where it still holds, else it routes to the net the new plan lays. The conduits a replan moves are
    /// laid before the old ones are given up: nothing ever removes a built conduit (clearance takes down walls
    /// and doors, not conduits), so the old path stays live until the new one carries the load and a link the
    /// plan drops is simply not laid again.
    /// </summary>
    private static (IReadOnlyList<ConduitLink> Links, IReadOnlyList<ConduitDecision> Decisions) RejoinHeld(LayoutPlan prev, LayoutPlan tiled, LayoutPlan plan, MapSurvey s)
    {
        var planned = new HashSet<int>(tiled.Rooms.Select(r => r.Id));
        var held = HeldLinks(prev.Conduits, plan);
        var consumers = new List<ConduitNode>();
        foreach (var r in plan.Rooms)
        {
            if (planned.Contains(r.Id) || r.Attached())
            {
                continue;
            }
            if (held.ContainsKey(RoomRef(r.Id)))
            {
                consumers.Add(new ConduitNode(RoomRef(r.Id), r.Region.Cells().SortedBy(CellLess)));
            }
        }
        if (consumers.Count == 0)
        {
            return (plan.Conduits, []);
        }
        return JoinConduits(plan, consumers, s, prev.Conduits);
    }

    /// <summary>
    /// The retired ground prev holds that clearance has not cleared, and the ground (ring and attached
    /// children) of each standing room of prev the plan next no longer holds: the building comes down through
    /// planned-ground clearance.
    /// </summary>
    private static List<Region> RetiredGround(LayoutPlan prev, LayoutPlan next, IReadOnlySet<int> built)
    {
        var output = new List<Region>(prev.RetiredGround);
        var held = new HashSet<int>(next.Rooms.Select(r => r.Id));
        foreach (var r in prev.Rooms)
        {
            if (r.Attached() || held.Contains(r.Id) || !built.Contains(r.Id))
            {
                continue;
            }
            output.Add(RectRegion(RoomGround(r.Region.Bounds())));
            foreach (var c in prev.ChildrenOf(r.Id))
            {
                output.Add(RectRegion(c.AttachedGround()));
            }
        }
        return output;
    }

    /// <summary>
    /// The barns, pens and yards of prev that stand clear of the rooms and the reserved ground of next: the
    /// tiler does not see them, so a new room may land on one, which then goes and is sited again. A site that
    /// clears stays where it is, so a replan does not move a barn or a yard that was built.
    /// </summary>
    private static List<LayoutReservation> CarriedSites(LayoutPlan prev, LayoutPlan next)
    {
        var taken = new List<Rectangle>();
        foreach (var r in next.Rooms)
        {
            taken.Add(RoomGround(r.Region.Bounds()));
        }
        foreach (var r in next.Reserved())
        {
            taken.Add(r.Area.Bounds());
        }
        var output = new List<LayoutReservation>();
        foreach (var r in prev.Reservations)
        {
            if (r.Kind != ReservationKind.Barn && r.Kind != ReservationKind.Pen && r.Kind != ReservationKind.Yard)
            {
                continue;
            }
            if (!taken.Any(t => RectsOverlap(t, r.Area.Bounds())))
            {
                output.Add(r);
            }
        }
        return output;
    }

    /// <summary>Reports whether a and b are the same plan as it is saved.</summary>
    public static bool SamePlan(LayoutPlan a, LayoutPlan b)
    {
        try
        {
            return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The plan's service-log line: rooms by role, zones by kind, reservations by kind, and the route check (busiest hallway cell's path count, or the rejection).</summary>
    public static string Summary(this LayoutPlan p)
    {
        var rooms = new Dictionary<string, int>();
        var zones = new Dictionary<string, int>();
        var reserved = new Dictionary<string, int>();
        void Count(Dictionary<string, int> m, string key) => m[key] = m.GetValueOrDefault(key) + 1;
        foreach (var r in p.Rooms)
        {
            Count(rooms, Wire.Name(r.Role));
        }
        foreach (var z in p.Zones)
        {
            Count(zones, Wire.Name(z.Kind));
        }
        foreach (var r in p.Reservations)
        {
            Count(reserved, Wire.Name(r.Kind));
        }
        foreach (var s in p.Sites)
        {
            Count(reserved, Wire.Name(s.Kind));
        }
        string route;
        try
        {
            var traffic = CheckRoutes(p);
            var busiest = 0;
            foreach (var n in traffic.Values)
            {
                busiest = Math.Max(busiest, n);
            }
            route = string.Create(CultureInfo.InvariantCulture, $"ok busiest={busiest}");
        }
        catch (DomainException e)
        {
            route = e.Message;
        }
        return $"rooms=[{Counts(rooms)}] zones=[{Counts(zones)}] reservations=[{Counts(reserved)}] routes={route}";
    }

    private static string Counts(Dictionary<string, int> m)
    {
        var keys = m.Keys.ToList();
        keys.Sort(StringComparer.Ordinal);
        return string.Join(" ", keys.Select(k => string.Create(CultureInfo.InvariantCulture, $"{k}:{m[k]}")));
    }

    private static LayoutPlan WithoutCore(LayoutPlan p) =>
        p with { Zones = [.. p.Zones.Where(z => z.Kind != ZoneKind.Core)] };

    private static bool RectHits(Rectangle r, IReadOnlySet<Cell> cells)
    {
        for (var z = r.Z; z < r.Z + r.Height; z++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                if (cells.Contains(new Cell(x, z)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Each reported geyser's bounding rectangle.</summary>
    private static List<Rectangle> GeyserFootprints(IReadOnlyList<PowerGeyser> geysers)
    {
        var output = new List<Rectangle>();
        foreach (var g in geysers)
        {
            if (g.Cells.Count == 0)
            {
                continue;
            }
            var r = default(Rectangle);
            foreach (var c in g.Cells)
            {
                r = UnionRect(r, new Rectangle(c.X, c.Z, 1, 1));
            }
            output.Add(r);
        }
        return output;
    }

    /// <summary>The enclosure the plan reserves over a geyser: the generator centred on it and its shell.</summary>
    private static Rectangle GeothermalArea(Rectangle geyser)
    {
        int cx = geyser.X + (geyser.Width / 2), cz = geyser.Z + (geyser.Height / 2);
        var side = GeothermalSide + (2 * GeothermalShell);
        return new Rectangle(cx - (side / 2), cz - (side / 2), side, side);
    }

    /// <summary>Every cell of the geysers' enclosures: rooms stay off them so each geyser keeps its generator site.</summary>
    private static HashSet<Cell> GeothermalCells(IReadOnlyList<Rectangle> footprints)
    {
        var output = new HashSet<Cell>();
        foreach (var f in footprints)
        {
            foreach (var c in RectangleCells(GeothermalArea(f)))
            {
                output.Add(c);
            }
        }
        return output;
    }

    /// <summary>Drops cells from the core candidates.</summary>
    private static IReadOnlyList<LayoutZone> CoreWithout(IReadOnlyList<LayoutZone> zones, IReadOnlySet<Cell> cells)
    {
        if (cells.Count == 0)
        {
            return zones;
        }
        var output = new List<LayoutZone>();
        foreach (var z in zones)
        {
            if (z.Kind != ZoneKind.Core)
            {
                output.Add(z);
                continue;
            }
            var runs = new List<RowRun>();
            foreach (var r in z.Runs)
            {
                var start = r.X;
                for (var x = r.X; x <= r.X + r.Length; x++)
                {
                    if (x < r.X + r.Length && !cells.Contains(new Cell(x, r.Z)))
                    {
                        continue;
                    }
                    if (x > start)
                    {
                        runs.Add(new RowRun(r.Z, start, x - start));
                    }
                    start = x + 1;
                }
            }
            output.Add(z with { Runs = runs });
        }
        return output;
    }
}
