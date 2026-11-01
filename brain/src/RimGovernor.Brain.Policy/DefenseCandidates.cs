using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The defense alternatives the generator emits (RG-HABITAT-003/008). No kind is mandatory: Retain keeps what the plan
/// holds, Retreat holds the standing interior doors, TemporaryCover sets sandbag lines with open firing positions,
/// the two perimeter variants wall the core with door or open gates, Killbox adds the funnel and its tiers, and Turrets
/// sets research-gated emplacements. The enum order is the candidates' tie order, never an admission order.
/// </summary>
public enum DefenseCandidateKind
{
    Retain,
    Retreat,
    TemporaryCover,
    PerimeterDoored,
    PerimeterOpen,
    Killbox,
    Turrets,
}

/// <summary>One structure a candidate places: its definition and cell, whether it closes the cell to colonists, and its work and costs when the catalog prices it.</summary>
public sealed record DefenseStructure(string Definition, Cell Cell, string Stuff, bool Impassable)
{
    /// <summary>Its catalog price, or the named state that the catalog does not price the definition.</summary>
    public DefensePiece Piece { get; init; } = new DefensePiece.Unpriced("");
}

/// <summary>What one defense structure costs to build: the catalog's work and costs, or unpriced with the fact named (<c>piece:&lt;def&gt;</c>).</summary>
public abstract record DefensePiece
{
    private DefensePiece() { }

    public sealed record Priced(double Work, IReadOnlyList<Amount> Costs) : DefensePiece;

    public sealed record Unpriced(string Fact) : DefensePiece;
}

/// <summary>The raid exposure a candidate responds to: points and capacity with their RaidExposure bin, the points against zero capacity, or the unread fact named.</summary>
public abstract record DefenseExposure
{
    private DefenseExposure() { }

    public sealed record Binned(double RaidPoints, double Capacity, string Bin) : DefenseExposure;

    public sealed record ZeroCapacity(double RaidPoints) : DefenseExposure;

    public sealed record Unread(string Fact) : DefenseExposure;
}

/// <summary>What a candidate does to one civilian trip of the plan.</summary>
public abstract record DefenseRouteEffect
{
    private DefenseRouteEffect() { }

    /// <summary>The trip stays open over <see cref="AddedCells"/> more cells.</summary>
    public sealed record Longer(int AddedCells) : DefenseRouteEffect;

    /// <summary>The trip has no path left.</summary>
    public sealed record Closed : DefenseRouteEffect;

    /// <summary>No probe covers the trip: the candidate changes ground the plan's walk does not hold.</summary>
    public sealed record Unprobed : DefenseRouteEffect;
}

/// <summary>A trip a candidate changes, named <c>route:&lt;from&gt;→&lt;to&gt;</c>.</summary>
public sealed record DefenseRouteChange(string Route, DefenseRouteEffect Effect);

/// <summary>Whether a candidate stands for comparison: generated, excluded for a named reason, or unassessed for a named missing fact.</summary>
public abstract record DefenseCandidateStanding
{
    private DefenseCandidateStanding() { }

    public sealed record Generated : DefenseCandidateStanding;

    public sealed record Excluded(string Reason) : DefenseCandidateStanding;

    public sealed record Unassessed(string Fact) : DefenseCandidateStanding;
}

/// <summary>The threat facts a candidate responds to, as the threat read gives them, with the facts it does not give named.</summary>
public sealed record DefenseThreat
{
    public DefenseExposure Exposure { get; init; } = new DefenseExposure.Unread("threat:colony census");

    /// <summary>The spawn cells of the ground raids observed this load, in cell order.</summary>
    public IReadOnlyList<Cell> ArrivalEdges { get; init; } = [];

    /// <summary>Each ground raid's observed walk this load, in the read's order: its spawn cell, then its sampled trail.</summary>
    public IReadOnlyList<IReadOnlyList<Cell>> Trails { get; init; } = [];

    /// <summary>The longest raider weapon range the ground raids carried (RaidTrack.Range, 0 for melee), in cells; 0 while none was observed.</summary>
    public double Range { get; init; }

    public IReadOnlyList<string> Missing { get; init; } = [];
}

/// <summary>
/// One defense alternative: a planned site on the shared ground (<see cref="DefenseGround"/>) with its exact cells,
/// its structures and their work, the cells it closes or narrows, the routes it changes and the threat it responds to.
/// </summary>
public sealed record DefenseCandidate
{
    public DefenseCandidateKind Kind { get; init; }

    /// <summary>Deterministic id: the kind and its first cell.</summary>
    public string Key { get; init; } = "";

    /// <summary>The plan site nodes it lays (the retained ones for Retain).</summary>
    public IReadOnlyList<PlannedSite> Sites { get; init; } = [];

    /// <summary>Every cell it holds, in cell order (z, then x).</summary>
    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public IReadOnlyList<DefenseStructure> Structures { get; init; } = [];

    /// <summary>The rock cells it has mined first (the killbox's).</summary>
    public IReadOnlyList<Cell> Dig { get; init; } = [];

    /// <summary>The cells its impassable structures close.</summary>
    public IReadOnlyList<Cell> Closes { get; init; } = [];

    /// <summary>The cells its passable structures narrow (doors, sandbags, traps, fences).</summary>
    public IReadOnlyList<Cell> Narrows { get; init; } = [];

    /// <summary>The cells its shooters stand on, in cell order: the cover's firing positions, the doors, the killbox's shooter seats.</summary>
    public IReadOnlyList<Cell> Firing { get; init; } = [];

    /// <summary>The cells a raider's walk stops at: what it closes and its doors, in cell order.</summary>
    public IReadOnlyList<Cell> Barriers { get; init; } = [];

    public IReadOnlyList<DefenseRouteChange> Routes { get; init; } = [];

    public DefenseThreat Threat { get; init; } = new();

    public DefenseCandidateStanding Standing { get; init; } = new DefenseCandidateStanding.Generated();

    /// <summary>Every exclusion the constraints found; the standing names the first.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>The facts the candidate lacks that leave it standing (unpriced pieces).</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
}

/// <summary>
/// The ground other plan parts hold, as one ledger: room walls and interiors, the plan's non-defense site nodes, its
/// field zones and its reservations. A defense candidate over any of it is refused, naming the holder.
/// </summary>
public sealed class DefenseGround
{
    private readonly Dictionary<Cell, string> held = [];

    public static DefenseGround Of(LayoutPlan plan)
    {
        var g = new DefenseGround();
        foreach (var r in plan.Rooms)
        {
            var block = r.Unfenced() ? r.Region.Bounds() : PlannedRoom.RoomWalls(r);
            g.Hold(PlannedRules.RectCells(block), string.Create(CultureInfo.InvariantCulture, $"ground:room:{r.Id}:{r.Role}"));
        }
        foreach (var s in plan.Sites)
        {
            if (DefenseCandidateRules.DefenseSite(s.Kind))
            {
                continue;
            }
            g.Hold(s.Footprint.Cells(), "ground:site:" + s.Node);
        }
        var field = 0;
        foreach (var z in plan.Zones)
        {
            if (z.Kind != LayoutZoneKind.Field)
            {
                continue;
            }
            g.Hold(RegionRules.RegionOfRuns(z.Runs).Cells(), string.Create(CultureInfo.InvariantCulture, $"ground:field:{field}"));
            field++;
        }
        foreach (var r in plan.Reservations)
        {
            if (r.Kind == ReservationKind.DefChoice)
            {
                continue; // the bound defense's own ground: an alternative may replace it
            }
            g.Hold(r.Area.Cells(), "ground:reservation:" + r.Kind);
        }
        return g;
    }

    private void Hold(IEnumerable<Cell> cells, string owner)
    {
        foreach (var c in cells)
        {
            held.TryAdd(c, owner);
        }
    }

    /// <summary>The holder of the first held cell in cell order, or null when the ground is free.</summary>
    public string? Refusal(IEnumerable<Cell> cells)
    {
        foreach (var c in cells.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            if (held.TryGetValue(c, out var owner))
            {
                return owner;
            }
        }
        return null;
    }

    /// <summary>The routine positions (rooms, non-defense sites, fields; not reservations) the cells cross, each once, in holder order.</summary>
    public IReadOnlyList<string> Routine(IEnumerable<Cell> cells) =>
        [.. cells.Select(c => held.GetValueOrDefault(c)).OfType<string>().Where(h => !h.StartsWith("ground:reservation:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>What the generator reads: the plan and survey, the facts for readiness and prices, the threat, and the reads the runtime adds.</summary>
public sealed record DefenseCandidateInputs
{
    public LayoutPlan Plan { get; init; } = new();

    public MapSurvey Survey { get; init; } = new();

    public ReviewFacts Facts { get; init; } = new();

    public DefenseThreat Threat { get; init; } = new();

    public DefenseDefinitions Definitions { get; init; } = new();

    /// <summary>The ids of the rooms standing as planned; null while the construction census is unread.</summary>
    public IReadOnlySet<int>? Built { get; init; }

    /// <summary>The killbox tiers sited on the traced plan's opening; null while their site census is unread (<see cref="TiersMissing"/> names it).</summary>
    public DefenseTiers? Tiers { get; init; }

    public string TiersMissing { get; init; } = DefenseCandidateRules.MissingTiers;

    /// <summary>The rock the killbox opening has mined first.</summary>
    public IReadOnlyList<Cell> Dig { get; init; } = [];

    public string Turret { get; init; } = DefenseTurretsPolicy.TurretMini;

    public string Conduit { get; init; } = "";

    /// <summary>The perimeter's bridge foundation on soft ground.</summary>
    public string Bridge { get; init; } = LayoutDefenseRules.PerimeterBridge;
}

/// <summary>
/// Generates the defense alternatives over a plan (task 7.1). It admits nothing: every candidate carries its ground,
/// structures, closed and narrowed cells, route changes and threat, and stands generated, excluded (ground held,
/// a civilian route closed, ordinary access blocked, research or definition missing) or unassessed (a fact unread).
/// Comparison and binding are later steps.
/// </summary>
public static class DefenseCandidateRules
{
    /// <summary>The killbox tiers' site census the runtime reads; named while it is unread.</summary>
    public const string MissingTiers = "defense_tiers:site_census";

    public const string MissingBuilt = "construction_census";

    /// <summary>The trip from the colony's entrances to the map edge, which the plan's walk does not hold.</summary>
    public const string RouteMapEdge = "route:Entrance→MapEdge";

    public const string RouteField = "route:Entrance→Field";

    internal const int CoverHalf = 2;
    internal const int CoverOffset = 3;

    private static readonly HashSet<NodeKind> KillboxFamily = [NodeKind.Killbox, NodeKind.Approach, NodeKind.CoverClear, NodeKind.Mortar];

    /// <summary>Whether a site node is part of a defense: the perimeter family, a defense tier node or the bound choice.</summary>
    public static bool DefenseSite(NodeKind kind) =>
        LayoutPerimeterSitesRules.PerimeterSiteKinds.Contains(kind) || DefenseTiersPolicy.IsDefenseTierNode(kind) || kind == DefenseBindingRules.NodeDefChoice;

    /// <summary>Whether a defense structure closes its cell to colonists; doors, sandbags, barricades, traps, fences, bait, conduits, floors and bridges do not.</summary>
    public static bool Blocks(DefenseDefinitions defs, string definition, string conduit) =>
        !(definition == defs.Trap || definition == defs.Fence || definition == defs.Sandbag || definition == DefenseCoverStock.DefenseSandbags
          || definition == defs.Door || definition == defs.Bait || definition == defs.Floor || definition == conduit || definition == ""
          || definition == LayoutDefenseRules.PerimeterBridge || definition == LayoutDefenseRules.PerimeterHeavyBridge);

    /// <summary>The threat facts the read gives: raid points and arrival edges from the census, capacity from the facts, and the bin; every gap named.</summary>
    public static DefenseThreat ThreatOf(ReviewFacts f)
    {
        var missing = new List<string> { RaidExposure.LayoutUnassessed };
        DefenseExposure exposure;
        IReadOnlyList<Cell> edges = [];
        IReadOnlyList<IReadOnlyList<Cell>> trails = [];
        var range = 0.0;
        try
        {
            var t = f.Census.Threat.Observed;
            edges = [.. t.Raids.Where(r => r.Ground).Select(r => r.Spawn).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X)];
            trails = [.. t.Raids.Where(r => r.Ground).Select(r => (IReadOnlyList<Cell>)r.Trail.Prepend(r.Spawn).ToList())];
            range = t.Raids.Where(r => r.Ground).Select(r => r.Range).DefaultIfEmpty(0).Max();
            var capacity = f.DefenseCapacity;
            if (capacity > 0 && RaidExposure.BinOf(t.RaidPoints / capacity) is { } bin)
            {
                exposure = new DefenseExposure.Binned(t.RaidPoints, capacity, bin);
            }
            else if (capacity > 0)
            {
                exposure = new DefenseExposure.Unread("exposure_bin:ratio_out_of_range");
                missing.Add("exposure_bin:ratio_out_of_range");
            }
            else
            {
                exposure = new DefenseExposure.ZeroCapacity(t.RaidPoints);
                missing.Add("exposure_bin:zero_capacity");
            }
        }
        catch (RequiredFactException e)
        {
            exposure = new DefenseExposure.Unread("threat:" + e.Fact);
            missing.Add("threat:" + e.Fact);
        }
        if (edges.Count == 0)
        {
            missing.Add("arrival_edges:none_observed_since_load");
        }
        return new DefenseThreat { Exposure = exposure, ArrivalEdges = edges, Trails = trails, Range = range, Missing = missing };
    }

    /// <summary>The plan with a perimeter traced around its rooms and its mountain pockets, from the plan without its defense nodes; the generators read it.</summary>
    public static LayoutPlan Trace(LayoutPlan plan, MapSurvey s) =>
        LayoutPlan.PlanMountainPockets(LayoutPlan.PlanPerimeter(plan with { Sites = [.. plan.Sites.Where(x => !DefenseSite(x.Kind))] }, s), s);

    /// <summary>The priced structures' total work and summed costs (resource, count, in resource order), and how many structures the catalog leaves unpriced.</summary>
    public static (double Work, IReadOnlyList<Amount> Costs, int Unpriced) Price(DefenseCandidate c)
    {
        var work = 0.0;
        var unpriced = 0;
        var costs = new Dictionary<Resource, long>();
        foreach (var s in c.Structures)
        {
            if (s.Piece is not DefensePiece.Priced p)
            {
                unpriced++;
                continue;
            }
            work += p.Work;
            foreach (var a in p.Costs)
            {
                costs[a.Resource] = costs.GetValueOrDefault(a.Resource) + a.Count;
            }
        }
        return (work, [.. costs.OrderBy(kv => kv.Key.Value, StringComparer.Ordinal).Select(kv => new Amount(kv.Key, kv.Value))], unpriced);
    }

    /// <summary>Every candidate over the inputs, constrained and in deterministic order (kind, then first cell, then key).</summary>
    public static IReadOnlyList<DefenseCandidate> Generate(DefenseCandidateInputs inputs, LayoutPlan? traced = null)
    {
        var plan = inputs.Plan;
        var ground = DefenseGround.Of(plan);
        traced ??= Trace(plan, inputs.Survey);
        var drafts = new List<DefenseCandidate> { Retain(plan, inputs), Retreat(plan, inputs.Built) };
        var (cover, firing) = Cover(inputs);
        drafts.Add(cover);
        drafts.AddRange(Perimeter(inputs, traced));
        drafts.Add(Killbox(inputs, traced));
        drafts.Add(Turrets(inputs, firing));
        var output = new List<DefenseCandidate>();
        foreach (var d in drafts)
        {
            var c = d.Kind is DefenseCandidateKind.Retain or DefenseCandidateKind.Retreat ? d : Constrain(Priced(d, inputs.Facts), ground, plan, inputs.Survey);
            var doors = c.Structures.Where(s => s.Definition == inputs.Definitions.Door).Select(s => s.Cell);
            output.Add(c with { Threat = inputs.Threat, Key = KeyOf(c), Barriers = Sorted(c.Closes.Concat(doors)) });
        }
        return Ordered(output);
    }

    /// <summary>The candidates in their one order: kind, then first cell (z, x), then key; never a tier index.</summary>
    public static IReadOnlyList<DefenseCandidate> Ordered(IEnumerable<DefenseCandidate> candidates) =>
        [.. candidates.OrderBy(c => c.Kind)
            .ThenBy(c => c.Cells.Count == 0 ? int.MinValue : c.Cells[0].Z)
            .ThenBy(c => c.Cells.Count == 0 ? int.MinValue : c.Cells[0].X)
            .ThenBy(c => c.Key, StringComparer.Ordinal)];

    private static string KeyOf(DefenseCandidate c)
    {
        var name = Wire.Name(c.Kind);
        return c.Cells.Count == 0 ? name : string.Create(CultureInfo.InvariantCulture, $"{name}@{c.Cells[0].X},{c.Cells[0].Z}");
    }

    private static List<Cell> Sorted(IEnumerable<Cell> cells) => [.. cells.Distinct().OrderBy(c => c.Z).ThenBy(c => c.X)];

    /// <summary>
    /// The plan's defense as it is: its defense sites and, once a choice is bound (DefenseBindingRules), that node's pieces
    /// (priced, so what of them is unbuilt is costed like any alternative's) and firing cells. It takes no ground of its own.
    /// </summary>
    private static DefenseCandidate Retain(LayoutPlan plan, DefenseCandidateInputs inputs)
    {
        var sites = plan.Sites.Where(s => DefenseSite(s.Kind)).ToList();
        var retain = new DefenseCandidate { Kind = DefenseCandidateKind.Retain, Sites = sites, Cells = Sorted(sites.SelectMany(s => s.Footprint.Cells())), Firing = ShooterSeats(sites) };
        if (DefenseBindingRules.Bound(plan) is not { } bound)
        {
            return retain;
        }
        var structures = DefenseBindingRules.Structures(bound, inputs.Definitions, inputs.Conduit);
        retain = Priced(retain with { Structures = structures }, inputs.Facts);
        return retain with
        {
            Firing = Sorted(retain.Firing.Concat(DefenseBindingRules.Firing(bound))),
            Closes = Sorted(structures.Where(s => s.Impassable).Select(s => s.Cell)),
            Narrows = Sorted(structures.Where(s => !s.Impassable).Select(s => s.Cell)),
        };
    }

    /// <summary>The interior door line the standing rooms already hold: their doors and links, no new ground and nothing to build.</summary>
    private static DefenseCandidate Retreat(LayoutPlan plan, IReadOnlySet<int>? built)
    {
        var c = new DefenseCandidate { Kind = DefenseCandidateKind.Retreat };
        if (built is null)
        {
            return c with { Standing = new DefenseCandidateStanding.Unassessed(MissingBuilt) };
        }
        var doors = new List<Cell>();
        foreach (var r in plan.Rooms)
        {
            if (r.Attached() || r.Unfenced() || !built.Contains(r.Id) || r.DoorSide is DoorSide.Doorless)
            {
                continue;
            }
            doors.AddRange(plan.DoorsOf(r));
        }
        if (doors.Count == 0)
        {
            return c with { Standing = new DefenseCandidateStanding.Excluded("retreat:no_standing_interior_door") };
        }
        return c with { Cells = Sorted(doors), Firing = Sorted(doors) };
    }

    /// <summary>The cells of the killbox's shooter seat nodes, in cell order.</summary>
    private static List<Cell> ShooterSeats(IEnumerable<PlannedSite> sites) => Sorted(sites.Where(s => s.Kind == DefenseTiersPolicy.NodeDefShooter).SelectMany(s => s.Footprint.Cells()));

    /// <summary>
    /// A definition's readiness from the catalog's availability: unread names <c>definition:&lt;def&gt;</c>, ready stands,
    /// research-locked excludes naming the first prerequisite the research census does not show finished (every
    /// prerequisite while that census is unread), and absent from the catalog excludes as unavailable.
    /// </summary>
    internal static DefenseCandidateStanding Readiness(ReviewFacts facts, string name)
    {
        var def = facts.Definitions.FirstOrDefault(d => d.Name == name);
        if (def is null)
        {
            return new DefenseCandidateStanding.Unassessed("definition:" + name);
        }
        if (def.Availability == PlanningAvailability.Ready)
        {
            return new DefenseCandidateStanding.Generated();
        }
        if (def.Availability != PlanningAvailability.ResearchLocked || def.Research.Count == 0)
        {
            return new DefenseCandidateStanding.Excluded("definition:" + name + ":unavailable");
        }
        IReadOnlyCollection<ResearchProjectId> finished;
        try
        {
            finished = facts.Research.Finished;
        }
        catch (RequiredFactException)
        {
            finished = [];
        }
        var open = def.Research.FirstOrDefault(p => !finished.Contains(new ResearchProjectId(p))) ?? def.Research[0];
        return new DefenseCandidateStanding.Excluded("research:" + open);
    }

    private static bool Open(Dictionary<Cell, SurveyCell> cells, Cell c) =>
        cells.TryGetValue(c, out var s) && s.Walkable && !s.Rock && !s.Hazard && !s.Built;

    /// <summary>
    /// A sandbag line outside each entrance, parallel to the core side it opens on, with an open firing position behind
    /// every bag. Sandbags need no research. The centre firing position of each line is returned for the turret emplacements.
    /// </summary>
    private static (DefenseCandidate Cover, List<Cell> Firing) Cover(DefenseCandidateInputs inputs)
    {
        var plan = inputs.Plan;
        var c = new DefenseCandidate { Kind = DefenseCandidateKind.TemporaryCover };
        if (plan.Entrances.Count == 0 || plan.CoreBounds() is not { } fp)
        {
            return (c with { Standing = new DefenseCandidateStanding.Excluded("cover:no_entrance") }, []);
        }
        var cells = new Dictionary<Cell, SurveyCell>();
        foreach (var s in inputs.Survey.Cells)
        {
            cells[s.Cell] = s;
        }
        var bags = new List<Cell>();
        var firing = new List<Cell>();
        var centres = new List<Cell>();
        foreach (var e in plan.Entrances)
        {
            var (n, t) = Facing(fp, e);
            var line = new List<Cell>();
            for (var k = -CoverHalf; k <= CoverHalf; k++)
            {
                var bag = new Cell(e.X + (n.X * CoverOffset) + (t.X * k), e.Z + (n.Z * CoverOffset) + (t.Z * k));
                var behind = new Cell(bag.X - n.X, bag.Z - n.Z);
                if (!Open(cells, bag) || !Open(cells, behind))
                {
                    continue;
                }
                bags.Add(bag);
                line.Add(behind);
            }
            if (line.Count > 0)
            {
                firing.AddRange(line);
                centres.Add(line[line.Count / 2]);
            }
        }
        if (bags.Count == 0)
        {
            return (c with { Standing = new DefenseCandidateStanding.Unassessed("survey:open_ground_beyond_entrances") }, []);
        }
        var structures = bags.Select(b => new DefenseStructure(DefenseCoverStock.DefenseSandbags, b, "", false)).ToList();
        c = c with { Structures = structures, Cells = Sorted(bags.Concat(firing)), Firing = Sorted(firing), Standing = Readiness(inputs.Facts, DefenseCoverStock.DefenseSandbags) };
        return (c, Sorted(centres));
    }

    /// <summary>The outward normal and the tangent of the core side nearest the entrance.</summary>
    private static ((int X, int Z) Normal, (int X, int Z) Tangent) Facing(Rectangle fp, Cell e)
    {
        var west = e.X - fp.X;
        var east = fp.X + fp.Width - 1 - e.X;
        var south = e.Z - fp.Z;
        var north = fp.Z + fp.Height - 1 - e.Z;
        var min = Math.Min(Math.Min(west, east), Math.Min(south, north));
        if (min == west)
        {
            return ((-1, 0), (0, 1));
        }
        if (min == east)
        {
            return ((1, 0), (0, 1));
        }
        return min == south ? ((0, -1), (1, 0)) : ((0, 1), (1, 0));
    }

    private static IEnumerable<DefenseCandidate> Perimeter(DefenseCandidateInputs inputs, LayoutPlan traced)
    {
        var defs = inputs.Definitions;
        if (!traced.Ring().Ok)
        {
            var none = new DefenseCandidateStanding.Excluded("perimeter:no_ring_traced");
            yield return new DefenseCandidate { Kind = DefenseCandidateKind.PerimeterDoored, Standing = none };
            yield return new DefenseCandidate { Kind = DefenseCandidateKind.PerimeterOpen, Standing = none };
            yield break;
        }
        var walled = traced with { Sites = [.. traced.Sites.Where(s => !KillboxFamily.Contains(s.Kind))] };
        var buildings = PerimeterBuildings(walled, inputs);
        var sites = walled.Sites.Where(s => DefenseSite(s.Kind)).ToList();
        // Both variants are fought from the gate cells: behind the doors, or in the open gates.
        var gates = Sorted(buildings.Where(b => b.Definition == defs.Door).Select(b => b.Cell));
        yield return Structured(DefenseCandidateKind.PerimeterDoored, sites, buildings, inputs) with { Firing = gates };
        yield return Structured(DefenseCandidateKind.PerimeterOpen, sites, [.. buildings.Where(b => b.Definition != defs.Door)], inputs) with { Firing = gates };
    }

    private static List<Building> PerimeterBuildings(LayoutPlan p, DefenseCandidateInputs inputs)
    {
        var defs = inputs.Definitions;
        var output = new List<Building>();
        output.AddRange(LayoutPlan.PerimeterSections(p, defs.Wall, defs.Door, inputs.Bridge).SelectMany(s => s.Buildings));
        output.AddRange(LayoutPlan.PocketSections(p, defs.Wall).SelectMany(s => s.Buildings));
        output.AddRange(LayoutPlan.FenceSections(p, defs.Fence, defs.FenceStuff).SelectMany(s => s.Buildings));
        return output;
    }

    private static DefenseCandidate Structured(DefenseCandidateKind kind, IReadOnlyList<PlannedSite> sites, IEnumerable<Building> buildings, DefenseCandidateInputs inputs)
    {
        var structures = buildings.Select(b => new DefenseStructure(b.Definition, b.Cell, b.Stuff, Blocks(inputs.Definitions, b.Definition, inputs.Conduit))).ToList();
        return new DefenseCandidate
        {
            Kind = kind,
            Sites = sites,
            Structures = structures,
            Cells = Sorted(structures.Select(s => s.Cell).Concat(sites.SelectMany(s => s.Footprint.Cells()))),
        };
    }

    /// <summary>The killbox: the traced perimeter with its funnel opening, approach and cover band, and the sited tiers but the turrets.</summary>
    private static DefenseCandidate Killbox(DefenseCandidateInputs inputs, LayoutPlan traced)
    {
        var bare = new DefenseCandidate { Kind = DefenseCandidateKind.Killbox };
        var (opening, found) = traced.Killbox();
        if (!found)
        {
            return bare with { Standing = new DefenseCandidateStanding.Excluded("killbox:no_opening_traced") };
        }
        if (inputs.Tiers is not { } tiers)
        {
            return bare with { Sites = [opening], Cells = Sorted(opening.Footprint.Cells()), Standing = new DefenseCandidateStanding.Unassessed(inputs.TiersMissing) };
        }
        if (tiers.Refusal != "")
        {
            return bare with { Sites = [opening], Cells = Sorted(opening.Footprint.Cells()), Standing = new DefenseCandidateStanding.Excluded("killbox:" + tiers.Refusal) };
        }
        var nodes = tiers.Nodes.Where(n => n.Kind != DefenseTiersPolicy.NodeDefTurrets).ToList();
        var buildings = PerimeterBuildings(traced, inputs);
        buildings.AddRange(nodes.SelectMany(n => n.Tier?.Places ?? []).Select(p => p.ToBuilding()));
        var sites = traced.Sites.Where(s => DefenseSite(s.Kind)).Concat(nodes).ToList();
        return Structured(DefenseCandidateKind.Killbox, sites, buildings, inputs) with { Dig = Sorted(inputs.Dig), Firing = ShooterSeats(nodes) };
    }

    /// <summary>Turret emplacements on the killbox's sited turret node, else on the cover's centre firing positions; gated by the turret definition's research.</summary>
    private static DefenseCandidate Turrets(DefenseCandidateInputs inputs, IReadOnlyList<Cell> firing)
    {
        var node = inputs.Tiers?.Nodes.FirstOrDefault(n => n.Kind == DefenseTiersPolicy.NodeDefTurrets && n.Tier is not null);
        var buildings = node is not null
            ? node.Tier!.Places.Select(p => p.ToBuilding()).ToList()
            : firing.Select(c => new Building(inputs.Turret, c, Rotation.North, "")).ToList();
        if (buildings.Count == 0)
        {
            return new DefenseCandidate { Kind = DefenseCandidateKind.Turrets, Standing = new DefenseCandidateStanding.Excluded("turrets:no_firing_position") };
        }
        var c = Structured(DefenseCandidateKind.Turrets, node is null ? [] : [node], buildings, inputs);
        var gate = buildings.Select(b => b.Definition).Where(d => Blocks(inputs.Definitions, d, inputs.Conduit)).Distinct(StringComparer.Ordinal)
            .Select(d => Readiness(inputs.Facts, d)).FirstOrDefault(s => s is not DefenseCandidateStanding.Generated);
        return gate is null ? c : c with { Standing = gate };
    }

    /// <summary>Each structure priced by its catalog piece; an unpriced one is named <c>piece:&lt;def&gt;</c>.</summary>
    private static DefenseCandidate Priced(DefenseCandidate c, ReviewFacts facts)
    {
        var missing = new List<string>(c.Missing);
        var priced = new Dictionary<(string, string), DefensePiece>();
        var structures = new List<DefenseStructure>();
        foreach (var s in c.Structures)
        {
            if (!priced.TryGetValue((s.Definition, s.Stuff), out var piece))
            {
                (double Work, IReadOnlyList<Amount> Costs)? read;
                try
                {
                    // A structure the builder names a material for is priced in it, else in the cheapest common one.
                    read = s.Stuff == "" ? ColonyPlanningRules.HostPiece(facts, s.Definition) : ColonyPlanningRules.ShellPiece(facts, s.Definition, s.Stuff);
                }
                catch (RequiredFactException)
                {
                    read = null;
                }
                piece = read is { } p ? new DefensePiece.Priced(p.Work, p.Costs) : new DefensePiece.Unpriced("piece:" + s.Definition);
                priced[(s.Definition, s.Stuff)] = piece;
                if (piece is DefensePiece.Unpriced u)
                {
                    missing.Add(u.Fact);
                }
            }
            structures.Add(s with { Piece = piece });
        }
        return c with { Structures = structures, Missing = missing };
    }

    /// <summary>
    /// Applies the shared constraints to a drafted candidate: ground another plan part holds refuses it; a civilian
    /// trip it closes excludes it naming the route, one it lengthens is priced by the added cells, one off the plan's
    /// walk is probed over the map survey (RouteChanges); an impassable structure on a door, link or entrance blocks ordinary access.
    /// </summary>
    public static DefenseCandidate Constrain(DefenseCandidate c, DefenseGround ground, LayoutPlan plan, MapSurvey? survey = null)
    {
        var exclusions = new List<string>();
        if (ground.Refusal(c.Cells) is { } held)
        {
            exclusions.Add(held);
        }
        var closes = Sorted(c.Structures.Where(s => s.Impassable).Select(s => s.Cell));
        var narrows = Sorted(c.Structures.Where(s => !s.Impassable).Select(s => s.Cell));
        var routes = RouteChanges(plan, closes, narrows, survey);
        exclusions.AddRange(routes.Where(r => r.Effect is DefenseRouteEffect.Closed).Select(r => r.Route));
        exclusions.AddRange(Access(plan, closes));
        var standing = c.Standing;
        if (standing is DefenseCandidateStanding.Generated && exclusions.Count > 0)
        {
            standing = new DefenseCandidateStanding.Excluded(exclusions[0]);
        }
        return c with { Closes = closes, Narrows = narrows, Routes = routes, Exclusions = exclusions, Standing = standing };
    }

    /// <summary>The ordinary access an impassable cell takes: a plan door, link or entrance, named <c>access:&lt;what&gt;@x,z</c>.</summary>
    internal static List<string> Access(LayoutPlan plan, IReadOnlyList<Cell> closes)
    {
        var doors = new HashSet<Cell>(plan.Rooms.Where(r => !r.Unfenced() && r.DoorSide is not DoorSide.Doorless).SelectMany(plan.DoorsOf));
        var links = new HashSet<Cell>(plan.Links.Select(l => l.Cell));
        var entrances = new HashSet<Cell>(plan.Entrances);
        var output = new List<string>();
        foreach (var c in closes)
        {
            var what = entrances.Contains(c) ? "entrance" : links.Contains(c) ? "link" : doors.Contains(c) ? "door" : null;
            if (what is not null)
            {
                output.Add(string.Create(CultureInfo.InvariantCulture, $"access:{what}@{c.X},{c.Z}"));
            }
        }
        return output;
    }

    private static string RoleName(PlannedRole role) => role == PlannedRole.None ? "Entrance" : role.ToString();

    /// <summary>A plan trip's route name, <c>route:&lt;from&gt;→&lt;to&gt;</c>, the empty role the entrance.</summary>
    public static string RouteName(PlannedRole from, PlannedRole to) => $"route:{RoleName(from)}→{RoleName(to)}";

    /// <summary>
    /// The civilian trips (LayoutRouteRules.RouteTrips) the closed cells change on the plan's walk: Closed when no path
    /// is left, Longer by the added cells when the path grows. A closed or narrowed cell off the walk changes the trips
    /// out of the entrances, probed over the whole-map <paramref name="survey"/> (OutsideChange): to the map edge and, with
    /// field zones, to the fields; without a survey they are named unprobed.
    /// </summary>
    public static List<DefenseRouteChange> RouteChanges(LayoutPlan plan, IReadOnlyList<Cell> closes, IReadOnlyList<Cell> narrows, MapSurvey? survey = null)
    {
        var output = new List<DefenseRouteChange>();
        var (walkMap, rooms) = LayoutPlan.RouteWalk(plan);
        if (closes.Any(walkMap.ContainsKey))
        {
            var before = new RouteGrid(walkMap);
            var cut = new Dictionary<Cell, int>(walkMap);
            foreach (var c in closes)
            {
                cut.Remove(c);
            }
            var after = new RouteGrid(cut);
            var cells = LayoutPlan.TripCells(plan, rooms);
            foreach (var trip in LayoutRouteRules.RouteTrips)
            {
                var to = cells(trip.To).SelectMany(g => g).ToList();
                if (to.Count == 0)
                {
                    continue;
                }
                DefenseRouteEffect? effect = null;
                foreach (var from in cells(trip.From))
                {
                    var was = before.RoutePath(from, to);
                    if (was is null)
                    {
                        continue;
                    }
                    var now = from.Any(cut.ContainsKey) && to.Any(cut.ContainsKey) ? after.RoutePath(from, to) : null;
                    if (now is null)
                    {
                        effect = new DefenseRouteEffect.Closed();
                        break;
                    }
                    if (now.Count > was.Count)
                    {
                        var added = now.Count - was.Count;
                        effect = effect is DefenseRouteEffect.Longer l && l.AddedCells >= added ? effect : new DefenseRouteEffect.Longer(added);
                    }
                }
                if (effect is not null)
                {
                    output.Add(new DefenseRouteChange(RouteName(trip.From, trip.To), effect));
                }
            }
        }
        if (closes.Concat(narrows).Any(c => !walkMap.ContainsKey(c)))
        {
            var fields = plan.Zones.Where(z => z.Kind == LayoutZoneKind.Field).SelectMany(z => z.Runs)
                .SelectMany(r => Enumerable.Range(r.X, r.Length).Select(x => new Cell(x, r.Z))).ToHashSet();
            if (survey is not { Bounds.Width: > 0, Bounds.Height: > 0 } map || map.Cells.Count == 0 || plan.Entrances.Count == 0)
            {
                output.Add(new DefenseRouteChange(RouteMapEdge, new DefenseRouteEffect.Unprobed()));
                if (fields.Count > 0)
                {
                    output.Add(new DefenseRouteChange(RouteField, new DefenseRouteEffect.Unprobed()));
                }
                return output;
            }
            var w = map.Bounds.Width;
            var h = map.Bounds.Height;
            if (OutsideChange(map, plan, closes, c => c.X == 0 || c.Z == 0 || c.X == w - 1 || c.Z == h - 1) is { } edge)
            {
                output.Add(new DefenseRouteChange(RouteMapEdge, edge));
            }
            if (fields.Count > 0 && OutsideChange(map, plan, closes, fields.Contains) is { } field)
            {
                output.Add(new DefenseRouteChange(RouteField, field));
            }
        }
        return output;
    }

    /// <summary>
    /// How the closed cells change the walk from the plan's entrances to the <paramref name="goal"/> cells over the survey's
    /// open ground (walkable, not rock, outside the planned rooms and their walls; a narrowing piece stays passable, as on
    /// the plan's walk): Closed when an entrance that reached a goal reaches none, Longer by the most cells an entrance's
    /// shortest walk grows, null when no walk changes.
    /// </summary>
    internal static DefenseRouteEffect? OutsideChange(MapSurvey survey, LayoutPlan plan, IReadOnlyList<Cell> closes, Func<Cell, bool> goal)
    {
        int w = survey.Bounds.Width, h = survey.Bounds.Height;
        bool In(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < w && c.Z < h;
        var open = new bool[w * h];
        foreach (var s in survey.Cells.Where(s => In(s.Cell)))
        {
            open[(s.Cell.Z * w) + s.Cell.X] = s.Walkable && !s.Rock;
        }
        foreach (var r in plan.Rooms)
        {
            foreach (var c in PlannedRules.RectCells(r.Unfenced() ? r.Region.Bounds() : PlannedRoom.RoomWalls(r)).Where(In))
            {
                open[(c.Z * w) + c.X] = false;
            }
        }
        var entrances = plan.Entrances.Where(In).ToList();
        foreach (var c in entrances)
        {
            open[(c.Z * w) + c.X] = true;
        }
        var before = Distances(open, w, h, goal);
        foreach (var c in closes.Where(In))
        {
            open[(c.Z * w) + c.X] = false;
        }
        var after = Distances(open, w, h, goal);
        DefenseRouteEffect? effect = null;
        foreach (var e in entrances)
        {
            var i = (e.Z * w) + e.X;
            if (before[i] < 0)
            {
                continue;
            }
            if (after[i] < 0)
            {
                return new DefenseRouteEffect.Closed();
            }
            var added = after[i] - before[i];
            if (added > 0 && (effect is not DefenseRouteEffect.Longer l || l.AddedCells < added))
            {
                effect = new DefenseRouteEffect.Longer(added);
            }
        }
        return effect;
    }

    /// <summary>Each open cell's four-way walk in cells to the nearest open goal cell; -1 where none is reached.</summary>
    private static int[] Distances(bool[] open, int w, int h, Func<Cell, bool> goal)
    {
        var dist = new int[w * h];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        for (var i = 0; i < open.Length; i++)
        {
            if (open[i] && goal(new Cell(i % w, i / w)))
            {
                dist[i] = 0;
                queue.Enqueue(i);
            }
        }
        ReadOnlySpan<(int X, int Z)> steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int x = i % w, z = i / w;
            foreach (var (dx, dz) in steps)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                {
                    continue;
                }
                var n = (nz * w) + nx;
                if (open[n] && dist[n] < 0)
                {
                    dist[n] = dist[i] + 1;
                    queue.Enqueue(n);
                }
            }
        }
        return dist;
    }
}
