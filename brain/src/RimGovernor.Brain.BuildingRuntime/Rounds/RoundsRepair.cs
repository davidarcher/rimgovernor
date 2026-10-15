namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsRepairSource : IColonySource
{
    Task<(EmergencyObservation Emergency, BridgeResult Result)> ReadEmergencyAsync(C.Identity identity, CancellationToken ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);
}

/// <summary>Reports observed essential repair demand until its outcome has a shared forecast adapter.</summary>
public sealed class RoundsRepairPlanner
{
    private readonly Rounder reviewer;
    private readonly IRoundsRepairSource native;

    private RoundsRepairPlanner(Rounder reviewer, IRoundsRepairSource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsRepairPlanner NewRoundsRepairPlanner(Rounder reviewer, IRoundsRepairSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsRepairPlanner: reviewer == null || native == null");
        }

        return new RoundsRepairPlanner(reviewer, native);
    }

    internal async Task<RoundsRepairResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native unset");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainEssentialRepairs, call);
        if (goal is not null)
        {
            BuildingRuntimePackage.CancelSettledRepairMethods(p.Journal, goal, call);
        }

        if (!workable || goal is null)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var (identity, _) = await native.IdentityAsync(call).ConfigureAwait(false);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var reading = await reviewer.ObserveColonyAsync(native, expected, null, call).ConfigureAwait(false);
        var upkeepReview = PolicyFunctions.ReviewUpkeep(reading.Projection.Facts.Upkeep, new UpkeepHistory(), null);
        EquatableArray<string> targetIds = [];
        foreach (var need in upkeepReview.Needs)
        {
            if (need.Concern == PolicyFunctions.MaintainEssentialRepairs && need.Targets.TryGetValue(out var targets))
            {
                targetIds = targets;
            }
        }

        if (targetIds.Count == 0)
        {
            return new RoundsRepairResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "essential_repairs") };
        }

        return new RoundsRepairResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_repair",
            "Observed repair demand requires shared forecast worker, timing and outcome alternatives.") };
    }
}

public readonly record struct RoundsRepairResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
