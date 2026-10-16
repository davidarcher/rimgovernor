namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Composes MaintainPopulation's anomaly half: lance, entity capture, containment and tend. A humanlike's rescue
/// or capture moved to the prisoners family's prisoners/custody Need (#3262).
/// </summary>
public sealed partial class RoundsPopulationCustodyPlanner
{
    private readonly Rounder reviewer;
    private readonly IRoundsCustodySource native;

    private RoundsPopulationCustodyPlanner(Rounder reviewer, IRoundsCustodySource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsPopulationCustodyPlanner NewRoundsPopulationCustodyPlanner(Rounder reviewer, IRoundsCustodySource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsPopulationCustodyPlanner: reviewer or native is missing");
        }

        return new RoundsPopulationCustodyPlanner(reviewer, native);
    }

    internal async Task<RoundsPopulationCustodyResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyConstants.MaintainPopulation, call);
        if (!workable || goal is null)
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        if (PolicyFunctions.LanceCandidate(read.Projection.Facts) is { } lanceTarget && lanceTarget.Value != "")
        {
            var (lance, handled) = await StepLanceAsync(p, state, started, goal, review.Tick, lanceTarget, arbiter, call, epoch).ConfigureAwait(false);
            if (handled)
            {
                return lance;
            }
        }

        // A downed humanlike's capture or rescue is the prisoners family's (prisoners/custody, #3262); a downed
        // entity's capture stays held here with the rest of the anomaly half.
        if (PolicyFunctions.EntityCaptureTarget(read.Projection.Facts.Containment) is not null)
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_population_capture",
                "Population acquisition requires shared participant and custody outcome alternatives.") };
        }

        return await StepContainmentAsync(p, state, started, goal, read.Projection.Facts, arbiter, call, epoch).ConfigureAwait(false);
    }

    internal RoundsPopulationCustodyResult Commit(Player p, ControlState state, DateTimeOffset started, StandardState goal, MethodId method, PlanId id, DomainAction action, CancellationToken call, CancellationToken epoch)
    {
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("commit: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }
}

public readonly record struct RoundsPopulationCustodyResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
