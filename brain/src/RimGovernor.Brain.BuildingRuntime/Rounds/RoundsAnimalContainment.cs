namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsAnimalContainmentResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

public sealed partial class RoundsAnimalContainmentPlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsBuildingSource _native;
    private readonly RoundsBuildingPlanner _building;

    public RoundsAnimalContainmentPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsAnimalContainmentPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
        _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = PolicyApi.MaintainAnimalContainment };
    }

    internal RoundsAnimalContainmentResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsAnimalContainmentResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsAnimalContainmentResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.MaintainAnimalContainment, call);
        if (!workable)
        {
            return new RoundsAnimalContainmentResult { Verdict = BuildingReasonNoDeficit };
        }
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = _reviewer.ObserveOwned(call, _reviewer.Native, expected, claims);
        var facts = read.Projection;
        if (!facts.Facts.AnimalUpkeep.Animals.TryGetValue(out var animals))
        {
            return new RoundsAnimalContainmentResult { Verdict = FieldUnavailable("animal_upkeep") };
        }
        if (!facts.WorkPawns.TryGetValue(out var workPawns))
        {
            return new RoundsAnimalContainmentResult { Verdict = FieldUnavailable("work_pawns") };
        }
        var handlerAvailable = AnimalHandlerAvailable(workPawns);
        if (!handlerAvailable.IsKnown)
        {
            return new RoundsAnimalContainmentResult { Verdict = FieldUnavailable("animal_handler") };
        }
        var built = _reviewer.StageHerdRooms(call, epoch, state, review, goal, expected, claims);
        if (built.Verdict != BuildingReasonNoDeficit)
        {
            return built;
        }
        var choice = PolicyApi.SelectAnimalContainmentMethod(animals, handlerAvailable, PensStand(facts));
        if (choice.Reason == AnimalContainmentReason.NoDeficit)
        {
            return new RoundsAnimalContainmentResult { Verdict = BuildingReasonNoDeficit };
        }
        return new RoundsAnimalContainmentResult { Verdict = ContainmentWait(choice.Reason) };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Whether a free colonist can take animal handling work: unknown while any pawn's availability or work
    /// settings are unread.
    /// </summary>
    internal static Fact<bool> AnimalHandlerAvailable(IReadOnlyList<WorkPawn> pawns)
    {
        var known = true;
        var available = false;
        foreach (var p in pawns)
        {
            if (!p.Available.TryGetValue(out var avail))
            {
                known = false;
                continue;
            }
            if (!avail)
            {
                continue;
            }
            if (!p.Work.TryGetValue(out var work))
            {
                known = false;
                continue;
            }
            foreach (var w in work)
            {
                if (w.Work == "Handling" && !w.Disabled && w.Priority > 0)
                {
                    available = true;
                }
            }
        }
        return known ? Known(available) : Unknown<bool>();
    }

    internal static PlanningDefinition? AnimalContainmentDefinition(IReadOnlyList<PlanningDefinition> definitions, string name)
    {
        foreach (var d in definitions)
        {
            if (d.Name == name)
            {
                return d;
            }
        }
        return null;
    }

    internal static string? AnimalContainmentStuff(PlanningDefinition a, PlanningDefinition b) => ObservationApi.SharedStuff(a, b);

    /// <summary>The stuff both shell definitions are made of: the shell style's wall stuff, then the cheapest in stock, then any shared.</summary>
    internal static string? ShellSharedStuff(ColonyProjection facts, PlanningDefinition a, PlanningDefinition b)
    {
        var want = ShellStyle(facts).WallStuff;
        if (want != "" && a.StuffOptions.Count > 0 && a.MakeableFrom(want) && b.MakeableFrom(want))
        {
            return want;
        }
        if (facts.Stock() is { } stock)
        {
            try
            {
                var price = a.StuffChoice(StuffPreference.Cheapest, stock);
                if (b.MakeableFrom(price.Stuff))
                {
                    return price.Stuff;
                }
            }
            catch (DomainException)
            {
            }
        }
        return AnimalContainmentStuff(a, b);
    }

    internal static Verdict ContainmentWait(AnimalContainmentReason reason) => reason switch
    {
        AnimalContainmentReason.WaitingHandler => NoWorker("animal_handler"),
        AnimalContainmentReason.WaitingNativePen => AwaitingPlan("native_pen", "delivery"),
        AnimalContainmentReason.MarkerExhausted => AwaitingPlan("native_pen", "marker_placed"),
        AnimalContainmentReason.NoPen => AwaitingPlan("pen", "planned"),
        _ => AwaitingPlan("pen", Wire.Name(reason)),
    };

    /// <summary>Whether the planned animal pens stand.</summary>
    internal static bool PensStand(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var gk = ColonyGround(facts).TryGetValue(out var ground);
        if (!pk || !ck || !gk)
        {
            return false;
        }
        return PolicyApi.PensStand(plan!, plan!.GroundWithRock(ground!, NaturalRock(facts)), census!.Buildings);
    }
}
