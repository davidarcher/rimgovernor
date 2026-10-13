namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainHomeCoverage's rounds step: edits the home area toward the planned coverage.</summary>
public sealed partial class RoundsHomeCoveragePlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    public static RoundsHomeCoveragePlanner NewRoundsHomeCoveragePlanner(Rounder reviewer)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("NewRoundsHomeCoveragePlanner: reviewer or its native source is missing");
        }

        return new RoundsHomeCoveragePlanner { Reviewer = reviewer };
    }
}

public readonly record struct RoundsHomeCoverageResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

public sealed partial class RoundsHomeCoveragePlanner
{
    internal RoundsHomeCoverageResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsHomeCoverageResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsHomeCoverageResult { Verdict = BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainHomeCoverage);
        if (!workable)
        {
            return new RoundsHomeCoverageResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var m in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, m.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsHomeCoverageResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var started = Reviewer.Clock.Now();
        var expected = StepScope(call, Reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        var read = Reviewer.ObserveOwned(call, Reviewer.Native, expected, claims);
        var f = read.Projection.Facts;
        var planned = PolicyFunctions.PlanHomeArea(f.MapBounds, f.CurrentConstruction, claims, f.HomeCoverage);
        if (!planned.TryGetValue(out var diff))
        {
            return new RoundsHomeCoverageResult { Verdict = FieldUnavailable("home_coverage") };
        }

        if (diff.Empty())
        {
            return new RoundsHomeCoverageResult { Verdict = WaitFor(Cause.MethodUsed, "home_coverage_diff") };
        }

        var method = diff.MethodId();
        foreach (var seen in goal.Methods)
        {
            if (seen.Method == method)
            {
                return new RoundsHomeCoverageResult { Verdict = WaitFor(Cause.MethodUsed, "home_coverage_method") };
            }
        }

        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        ActionId Next() => new($"{id}-{actions.Count}");
        if (diff.AutoOff)
        {
            actions.Add(DomainAction.NewAutoHomeAreaAction(Next(), false));
        }

        (AreaOperation Op, IReadOnlyList<Cell> Cells)[] edits = [(AreaOperation.SetCells, diff.Set), (AreaOperation.ClearCells, diff.Clear)];
        foreach (var edit in edits)
        {
            if (edit.Cells.Count == 0)
            {
                continue;
            }

            var area = Area.NewArea(edit.Op, "", edit.Cells);
            actions.Add(DomainAction.NewAreaAction(Next(), area));
        }

        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsHomeCoverageResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
