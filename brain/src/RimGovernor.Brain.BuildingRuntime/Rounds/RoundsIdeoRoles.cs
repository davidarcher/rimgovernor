namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainIdeoRoles' rounds step: assigns one owed ideology role per step.</summary>
public sealed partial class RoundsIdeoRolesPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    public static RoundsIdeoRolesPlanner NewRoundsIdeoRolesPlanner(Rounder reviewer)
    {
        if (reviewer is null || !reviewer.MethodEnabled(ConcernId.MaintainIdeoRoles))
        {
            throw new ControlException("NewRoundsIdeoRolesPlanner: reviewer is missing or the method is disabled");
        }

        return new RoundsIdeoRolesPlanner { Reviewer = reviewer };
    }
}

public readonly record struct RoundsIdeoRolesResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

public sealed partial class RoundsIdeoRolesPlanner
{
    internal RoundsIdeoRolesResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsIdeoRolesResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsIdeoRolesResult { Verdict = BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainIdeoRoles);
        if (!workable)
        {
            return new RoundsIdeoRolesResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var m in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, m.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsIdeoRolesResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var expected = StepScope(call, Reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = Reviewer.Clock.Now();
        var read = Reviewer.ObserveOwned(call, Reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        if (!PolicyFunctions.RoleAssignments(read.Projection.Facts.Ideology, read.Projection.WorkPawns).TryGetValue(out var owed) || owed.Count == 0)
        {
            return new RoundsIdeoRolesResult { Verdict = WaitFor(Cause.MethodUsed, "ideology_roles") };
        }

        Verdict held = default;
        foreach (var choice in owed)
        {
            var prefix = $"ideorole-{choice.Pawn}-{choice.Role}-";
            var (method, verdict, ok) = AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
            if (!ok)
            {
                held = verdict;
                continue;
            }

            if (!arbiter.TryClaim([new PawnId(choice.Pawn.Value)]))
            {
                return new RoundsIdeoRolesResult { Verdict = WaitFor(Cause.MethodUsed, "pawn_claim") };
            }

            var assign = Assign.NewAssign(new PawnId(choice.Pawn.Value), choice.Role, Assign.ClearPrevious());
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewAssignAction(new ActionId($"{id}-0"), assign);
            var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = Reviewer.Clock.Now() - started;
            if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
            {
                throw new ControlException("step: session state changed or the read took too long");
            }

            p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, $"ideology: {choice.Pawn} takes role {choice.Def} ({choice.Role})", plan);
            return new RoundsIdeoRolesResult { Verdict = BuildingReasonAdmitted, Plan = id };
        }

        return new RoundsIdeoRolesResult { Verdict = held };
    }
}
