namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainPermits: take the next royal permit the colonist qualifies for.</summary>
public sealed class RoundsPermitsPlanner
{
    private readonly Rounder reviewer;

    private RoundsPermitsPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static RoundsPermitsPlanner NewRoundsPermitsPlanner(Rounder reviewer)
    {
        if (reviewer is null || !reviewer.MethodEnabled(PolicyConstants.MaintainPermits))
        {
            throw new ControlException("NewRoundsPermitsPlanner: reviewer missing or method disabled");
        }

        return new RoundsPermitsPlanner(reviewer);
    }

    internal async Task<RoundsPermitsResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsPermitsResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsPermitsResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyConstants.MaintainPermits, call);
        if (!workable || goal is null)
        {
            return new RoundsPermitsResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPermitsResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var chosen = PolicyFunctions.NextPermitOf(read.Projection.Facts.Royalty);
        if (chosen is not { } choice)
        {
            return new RoundsPermitsResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "permit") };
        }

        var intent = choice.Intent();
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, PermitMethodPrefix(intent), state.Snapshot, call);
        if (!admitted)
        {
            return new RoundsPermitsResult { Verdict = verdict };
        }

        var plan = PermitPlan(intent);
        if (!arbiter.TryClaim([intent.Holder]))
        {
            return new RoundsPermitsResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "pawn_claim") };
        }

        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, method, $"permits: {intent.Holder} takes {intent.Permit} with {intent.Faction}", plan, call);
        return new RoundsPermitsResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = plan.Id };
    }

    internal static string PermitMethodPrefix(PermitIntent intent) => $"permit-{intent.Holder}-{intent.Permit}-";

    internal static PlanSpec PermitPlan(PermitIntent intent)
    {
        var setting = intent.Setting();
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewPawnSettingsAction(new ActionId($"{id}-0"), setting);
        return new PlanSpec(id, new PlanRevision(1), [action]);
    }
}

public readonly record struct RoundsPermitsResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
