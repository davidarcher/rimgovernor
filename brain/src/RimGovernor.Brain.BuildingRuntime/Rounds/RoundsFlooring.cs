namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Composes the rooms/flooring Need's building method: lay a role-appropriate floor on the cells native measures
    /// short of their room's requirement. Completion is the next measured census, not the build receipts: the
    /// review releases a room only once no cell of it reads deficient.
    /// </summary>
    public static RoundsBuildingPlanner NewRoundsFlooringPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsFlooringPlanner: reviewer or native is missing");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsFlooringPlanner: native is not a rounds source");
        }

        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = RoomsFamily.Flooring.FilingKey, Need = RoomsFamily.Flooring, Definition = "WoodPlankFloor" };
    }

    /// <summary>
    /// Supplies the firebreak tier's cells: pave reports the firebreak ring's pave cells that are still natural
    /// ground with no floor ordered, read at each review.
    /// </summary>
    public void SetFirebreakPave(Func<IReadOnlyList<Cell>> pave) => FirebreakPave = pave;

    /// <summary>Lists every floor the policy may choose so the census read carries each one's availability, stats and cost list.</summary>
    internal IReadOnlyList<string> FlooringDefinitions() => Reviewer.Policy.Flooring.Floors;

    /// <summary>The flooring census the traffic tier prices, with the firebreak ring's pave cells when composed: the rooms/flooring Need's evidence.</summary>
    internal Fact<FlooringObservation> FlooringCensus(ColonyProjection facts)
    {
        var census = TrafficFlooringFacts(facts, Reviewer.Policy.Flooring);
        if (census.TryGetValue(out var v) && FirebreakPave is not null)
        {
            census = Known(v with { Firebreak = FirebreakPave() });
        }

        return census;
    }

    /// <summary>
    /// Reviews the fresh census and maps the policy outcome onto the planner: a build resolves the floor
    /// definition and its cells, everything else is a reason.
    /// </summary>
    internal async Task<(RoundsBuildingPlanner? Planner, Verdict Verdict)> SelectFlooringAsync(GenerationSnapshot current, ColonyProjection facts, CancellationToken call)
    {
        var p = Reviewer.Policy.Flooring;
        var census = FlooringCensus(facts);
        LogTrafficFindings(census);
        var review = PolicyFunctions.ReviewFlooring(census, facts.Rooms, p);
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }

        if (!review.Known)
        {
            return (null, FieldUnavailable("flooring"));
        }

        var proposal = PolicyFunctions.SelectFlooringMethod(review, FlooringFactsOf(facts), p);
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
                return (null, AwaitingMethod(proposal.Method));
        }
    }

    /// <summary>
    /// Previews the proposal's cells in order and admits every one native reports legal and safe; a floor has
    /// no rotation and a one-cell footprint. Cells native refuses (a wall, an identical floor laid meanwhile)
    /// are skipped; the batch is the cells that remain.
    /// </summary>
    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewFlooringAsync(GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
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

        foreach (var cell in RockStepRules.RockStep(role, facts.Cells).Left)
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

            var building = new Building(Flooring.Definition, cell, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{selected.Count}"), building, PolicyFunctions.PlannerTier(Concern, Phase));
            var preview = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
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

            MergeRoundsStock(stock, preview.Stock, selected.Count == 0);
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

partial class RoundsBuildingPlanner
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

        var lines = TrafficRules.TrafficFindings(v).Select(f => f.ToString()).ToList();
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

        Tel.Decide(new Decision
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
        v = RoundsSleepingUpkeepPlanner.WithThroneFloor(facts, v);
        v = v with { Stock = facts.Resources };
        var style = RoundsTierStyle.FloorStyle(facts);
        if (style is not null)
        {
            v = v with { TrafficStyle = style(Pol.RoomRole.None).Item1 };
        }

        return Known(v);
    }
}
