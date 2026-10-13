namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native defense-site read (Go's anonymous interface on the building source).</summary>
internal interface IDefenseSiteReader
{
    (DefenseSite Site, BridgeResult Result) ReadDefenseSite(CancellationToken ct, C.Identity identity, CellRect region);
}

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Composes MaintainFlooring's building method: lay a role-appropriate floor on the cells native measures
    /// short of their room's requirement. Completion is the next measured census, not the build receipts: the
    /// review releases a room only once no cell of it reads deficient.
    /// </summary>
    public static RoundsBuildingPlanner NewRoundsFlooringPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(ConcernId.MaintainFlooring))
        {
            throw new ControlException("NewRoundsFlooringPlanner: reviewer, native or method disabled");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsFlooringPlanner: native is not a rounds source");
        }

        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = ConcernId.MaintainFlooring, Definition = "WoodPlankFloor" };
    }

    /// <summary>
    /// Supplies the firebreak tier's cells: pave reports the firebreak ring's pave cells that are still natural
    /// ground with no floor ordered, read at each review.
    /// </summary>
    public void SetFirebreakPave(Func<IReadOnlyList<Cell>> pave) => FirebreakPave = pave;

    /// <summary>
    /// Reads the terrain under the standing incinerator's interior: the native flooring census lists only roofed
    /// rooms, so the nine cells come from one defense-site read. Empty when no incinerator stands, the source
    /// cannot read a site, or a cell is fogged or stands on terrain the catalog does not price.
    /// </summary>
    internal IReadOnlyList<FloorCell> IncineratorFloor(CancellationToken call, GenerationSnapshot current, ColonyProjection facts, IReadOnlyDictionary<string, FloorTerrain> terrains)
    {
        var room = StandingIncinerator(facts);
        if (room is null || Native is not IDefenseSiteReader reader)
        {
            return [];
        }

        var inner = room.Region.Bounds();
        var region = new CellRect(new Cell(inner.X, inner.Z), new Cell(inner.X + inner.Width - 1, inner.Z + inner.Height - 1));
        var (site, _) = reader.ReadDefenseSite(call, Boundary.Identity(current), region);
        try
        {
            Boundary.Context(site.Context, current);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("incinerator floor: defense site context", e);
        }

        if (new Tick(site.Context.Tick) < facts.Identity.Tick)
        {
            throw new ControlException("incinerator floor: defense site context");
        }

        var output = new List<FloorCell>();
        foreach (var cell in site.Cells)
        {
            if (cell.Fogged || !terrains.ContainsKey(cell.Terrain))
            {
                return [];
            }

            output.Add(new FloorCell { Cell = cell.Cell, Terrain = cell.Terrain });
        }

        return output;
    }

    /// <summary>Lists every floor the policy may choose so the census read carries each one's availability, stats and cost list.</summary>
    internal IReadOnlyList<string> FlooringDefinitions() => Reviewer.Policy.Flooring.Floors;

    /// <summary>
    /// Re-reviews the fresh census under the review's latch and maps the policy outcome onto the planner: a
    /// build resolves the floor definition and its cells, everything else is a reason.
    /// </summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectFlooring(CancellationToken call, GenerationSnapshot current, ColonyProjection facts, RoundsLatches latches)
    {
        var p = Reviewer.Policy.Flooring;
        var census = TrafficFlooringFacts(facts, p);
        if (census.TryGetValue(out var v))
        {
            if (FirebreakPave is not null)
            {
                v = v with { Firebreak = FirebreakPave() };
            }

            v = v with { Incinerator = IncineratorFloor(call, current, facts, v.Terrains) };
            census = Known(v);
        }

        LogTrafficFindings(census);
        var review = PolicyFunctions.ReviewFlooring(census, facts.Rooms, latches.Flooring, p);
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }

        if (!review.Known)
        {
            return (null, FieldUnavailable("flooring"));
        }

        var proposal = PolicyFunctions.SelectFlooringMethod(review, FlooringFacts(facts), p);
        switch (proposal.Method)
        {
            case FlooringMethod.Build:
                var resolved = Copy();
                resolved.Flooring = proposal;
                resolved.Definition = proposal.Definition;
                return (resolved, default);
            case FlooringMethod.Unknown:
                return (null, FieldUnavailable("flooring"));
            case FlooringMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            default:
                return (null, AwaitingMethod(proposal.Method.ToString()));
        }
    }

    /// <summary>
    /// Previews the proposal's cells in order and admits every one native reports legal and safe; a floor has
    /// no rotation and a one-cell footprint. Cells native refuses (a wall, an identical floor laid meanwhile)
    /// are skipped; the batch is the cells that remain.
    /// </summary>
    internal (IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict) PreviewFlooring(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (Flooring is null || Flooring.Method != FlooringMethod.Build)
        {
            throw new ControlException("previewFlooring: no build proposal");
        }

        var guarded = new HashSet<Cell>();
        foreach (var c in protectedCells)
        {
            guarded.Add(c);
        }

        // Listed natural rock on a floor cell is the floor already: the rock step leaves it, so no floor is
        // ordered or previewed. A cell the census does not list is the native preview's to judge (the census is
        // a window, not the whole map), so it is not classified here.
        var listed = new HashSet<Cell>();
        foreach (var c in facts.Cells)
        {
            listed.Add(c.Cell);
        }

        var role = new List<RoleCell>();
        foreach (var cell in Flooring.Cells)
        {
            if (listed.Contains(cell))
            {
                role.Add(new RoleCell { Cell = cell, Role = RockRole.Blocks });
            }
        }

        foreach (var cell in PolicyFunctions.RockStep(role, facts.Cells).Left)
        {
            guarded.Add(cell);
        }

        var selected = new List<Preview>();
        var unknown = false;
        foreach (var cell in Flooring.Cells)
        {
            if (guarded.Contains(cell))
            {
                continue;
            }

            var building = Building.NewBuilding(Flooring.Definition, cell, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{selected.Count}"), building, PolicyFunctions.PlannerTier(Concern, Phase));
            var (preview, _) = Native.PreviewBuilding(ct, action, snapshot);
            var p = preview.Preview;
            var fk = p.Footprint.TryGetValue(out var footprint);
            var mk = p.MadeFromStuff.TryGetValue(out var made);
            var lk = p.CanPlace.TryGetValue(out var legal);
            var sk = p.SafeToPlace.TryGetValue(out var safe);
            if (!fk || !mk || !lk || !sk)
            {
                unknown = true;
                continue;
            }

            if (made || footprint!.Count != 1 || footprint[0] != cell || !legal || !safe)
            {
                continue;
            }

            MergeRoundsStock(ref stock, preview.Stock, selected.Count == 0);
            selected.Add(p);
        }

        if (selected.Count > 0)
        {
            return (selected, stock, default);
        }

        if (unknown)
        {
            return ([], stock, FieldUnavailable("flooring_preview"));
        }

        return ([], stock, NoSpace("floor_cells"));
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The last finding set logged, so the OTLP log ring names a flagged room when it changes rather than on every review.</summary>
    private static readonly object TrafficFindingsLoggedLock = new();

    private static string _trafficFindingsLogged = "";

    /// <summary>Flags thoroughfares and animals in clean rooms from the traffic layers in the OTLP log ring; no planner acts on them yet.</summary>
    internal static void LogTrafficFindings(Fact<FlooringObservation> fact)
    {
        if (!fact.TryGetValue(out var v))
        {
            return;
        }

        var lines = PolicyFunctions.TrafficFindings(v).Select(f => f.ToString()).ToList();
        var joined = string.Join("; ", lines);
        bool changed;
        lock (TrafficFindingsLoggedLock)
        {
            changed = !string.Equals(joined, _trafficFindingsLogged, StringComparison.Ordinal);
            _trafficFindingsLogged = joined;
        }

        if (!changed || joined.Length == 0)
        {
            return;
        }

        Tel.Decide(CancellationToken.None, new Decision
        {
            Kind = "routine_skip",
            Component = "routine-flooring",
            Verdict = "skipped",
            Reason = "traffic_finding",
            Target = "flooring",
            Attrs = new Dictionary<string, object?> { ["findings"] = lines },
        });
    }

    /// <summary>
    /// Adds what the traffic tier prices its floor from to the flooring census: every policy floor's planning
    /// row, the accessible stock and the tier style's aisle floor. An unknown census stays unknown.
    /// </summary>
    internal static Fact<FlooringObservation> TrafficFlooringFacts(ColonyProjection facts, FlooringPolicy p)
    {
        if (!facts.Facts.Upkeep.Flooring.TryGetValue(out var v))
        {
            return facts.Facts.Upkeep.Flooring;
        }

        var floors = new Dictionary<string, FloorDefinition>();
        foreach (var d in facts.Definitions)
        {
            if (p.Floors.Contains(d.Name))
            {
                floors[d.Name] = new FloorDefinition
                {
                    Available = d.Available,
                    Terrain = d.Terrain,
                    Cleanliness = d.Cleanliness,
                    Beauty = d.Beauty,
                    Flammability = d.Flammability,
                    PathCost = d.PathCost,
                    Costs = d.Costs,
                    WorkToBuild = d.WorkToBuild,
                    Tags = d.FloorTags,
                };
            }
        }

        v = v with { Floors = floors };
        v = WithThroneFloor(facts, v);
        v = v with { Stock = facts.Resources };
        var style = FloorStyle(facts);
        if (style is not null)
        {
            v = v with { TrafficStyle = style(RoomRole.None).Item1 };
        }

        return Known(v);
    }
}
