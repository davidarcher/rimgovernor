using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class BuildingPlanner
{
    /// <summary>
    /// Composes the rooms/flooring Need's building method: lay a role-appropriate floor on the cells native measures
    /// short of their room's requirement. Completion is the next measured census, not the build receipts: the
    /// review releases a room only once no cell of it reads deficient.
    /// </summary>
    public static BuildingPlanner NewReviewFlooringPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewFlooringPlanner: reviewer or native is missing");
        }

        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewFlooringPlanner: native is not a rounds source");
        }

        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = RoomsFamily.Flooring.FilingKey, Need = RoomsFamily.Flooring, Definition = "WoodPlankFloor" };
    }

    /// <summary>
    /// Supplies the firebreak tier's cells: pave reports the firebreak ring's pave cells that are still natural
    /// ground with no floor ordered, read at each review.
    /// </summary>
    public void SetFirebreakPave(Func<IReadOnlyList<Cell>> pave) => FirebreakPave = pave;

    /// <summary>Lists every floor the policy may choose so the census read carries each one's availability, stats and cost list.</summary>
    internal IReadOnlyList<string> FlooringDefinitions() => Reviewer.Policy.Flooring.Floors;

    /// <summary>The flooring census the traffic tier prices, with the firebreak ring's pave cells when composed: the rooms/flooring Need's evidence.</summary>
    internal FlooringObservation FlooringCensus(ColonyReading facts)
    {
        var census = TrafficFlooringFacts(facts, Reviewer.Policy.Flooring);
        return FirebreakPave is not null ? census with { Firebreak = FirebreakPave() } : census;
    }

    /// <summary>
    /// Reviews the fresh census and maps the policy outcome onto the planner: a build resolves the floor
    /// definition and its cells, everything else is a reason.
    /// </summary>
    internal async Task<(BuildingPlanner? Planner, Verdict Verdict)> SelectFlooringAsync(GenerationSnapshot current, ColonyReading facts, CancellationToken call)
    {
        var p = Reviewer.Policy.Flooring;
        var census = FlooringCensus(facts);
        LogTrafficFindings(census);
        var review = FlooringRules.ReviewFlooring(census, facts.Facts.Upkeep.Rooms, p);
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }

        var proposal = FlooringRules.SelectFlooringMethod(review, FlooringFactsOf(facts), p);
        switch (proposal.Method)
        {
            case FlooringMethod.Build:
                var resolved = Copy();
                resolved.Flooring = proposal;
                resolved.Definition = proposal.Definition;
                return (resolved, default);
            case FlooringMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case FlooringMethod.None or FlooringMethod.Pending or FlooringMethod.ResearchNeeded or FlooringMethod.MaterialsNeeded:
                return (null, AwaitingMethod(proposal.Method));
        }
        throw new DomainException($"undefined flooring method {(int)proposal.Method}");
    }

    /// <summary>
    /// Previews the proposal's cells in order and admits every one native reports legal and safe; a floor has
    /// no rotation and a one-cell footprint. Cells native refuses (a wall, an identical floor laid meanwhile)
    /// are skipped; the batch is the cells that remain.
    /// </summary>
    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewFlooringAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
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
        foreach (var c in facts.PlanningWindow.Cells)
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

        foreach (var cell in RockStepRules.RockStep(role, facts.PlanningWindow.Cells).Left)
        {
            guarded.Add(cell);
        }

        var selected = new List<Preview>();
        foreach (var cell in Flooring.Cells)
        {
            if (guarded.Contains(cell))
            {
                continue;
            }

            var building = new Building(Flooring.Definition, cell, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{selected.Count}"), building, ConstructionTierRules.PlannerTier(Concern, Phase));
            var preview = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
            var p = preview.Preview;
            if (p.MadeFromStuff || p.Footprint.Count != 1 || p.Footprint[0] != cell || !p.CanPlace || !p.SafeToPlace)
            {
                continue;
            }

            MergeReviewStock(stock, preview.Stock);
            selected.Add(p);
        }

        if (selected.Count > 0)
        {
            return (selected, stock, default);
        }

        return ([], stock, NoSpace("floor_cells"));
    }
}

partial class BuildingPlanner
{
    /// <summary>The last finding set logged, so the OTLP log ring names a flagged room when it changes rather than on every review.</summary>
    private static readonly object TrafficFindingsLoggedLock = new();

    private static string _trafficFindingsLogged = "";

    /// <summary>Flags thoroughfares and animals in clean rooms from the traffic layers in the OTLP log ring; no planner acts on them yet.</summary>
    internal static void LogTrafficFindings(FlooringObservation v)
    {
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
    /// row, the accessible stock and the tier style's aisle floor.
    /// </summary>
    internal static FlooringObservation TrafficFlooringFacts(ColonyReading facts, FlooringPolicy p)
    {
        var v = facts.Facts.Upkeep.Flooring;

        var floors = new Dictionary<string, FloorDefinition>();
        foreach (var d in facts.Facts.Definitions)
        {
            if (p.Floors.Contains(d.Name) && d.Floor is not null)
            {
                floors[d.Name] = new FloorDefinition
                {
                    Available = d.Available,
                    Terrain = d.Terrain,
                    Cleanliness = (d.Floor ?? throw new RequiredFactException("planning Floor")).Cleanliness,
                    Beauty = (d.Floor ?? throw new RequiredFactException("planning Floor")).Beauty,
                    Flammability = (d.Floor ?? throw new RequiredFactException("planning Floor")).Flammability,
                    PathCost = d.PathCost,
                    Costs = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).Costs,
                    WorkToBuild = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).WorkToBuild,
                    Tags = (d.Floor ?? throw new RequiredFactException("planning Floor")).FloorTags,
                };
            }
        }

        v = v with { Floors = floors };
        v = SleepingUpkeepPlanner.WithThroneFloor(facts, v);
        v = v with { Stock = ResourceStockRules.Count(facts.Snapshot.Resources) };
        var style = TierStyle.FloorStyle(facts);
        if (style is not null)
        {
            v = v with { TrafficStyle = style(RoomRole.None).Item1 };
        }

        return v;
    }
}
