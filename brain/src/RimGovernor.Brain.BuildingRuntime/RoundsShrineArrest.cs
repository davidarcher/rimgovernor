namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static PlanSpec ShrineArrestPlan(PlanId id, Domain.PawnId performer, Domain.PawnId target, string bed)
    {
        var draft = new OwnedDraft(performer);
        var draftAction = DomainAction.NewOwnedDraftAction(new ActionId(id.Value + "-draft"), draft);
        var intent = Capture.NewArrest(performer, target, bed);
        var arrest = DomainAction.NewCaptureAction(new ActionId(id.Value + "-arrest"), intent);
        return new PlanSpec(id, new PlanRevision(1), [draftAction, arrest], [new ActionDependency(arrest.Id, draftAction.Id, Coupled: true)]);
    }
}

public sealed partial class RoundsPopulationCustodyPlanner
{
    internal RoundsPopulationCustodyResult CommitArrest(
        Player p,
        ControlState state,
        DateTimeOffset started,
        StandardState goal,
        RoundsFacts facts,
        IReadOnlyList<ShrineDefenderFacts
> squad,
        Domain.PawnId target,
        StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var bed = PolicyFunctions.ShrineArrestBed(facts.Sleeping);
        var performer = PolicyFunctions.ShrineArrester(squad);
        if (bed.Length == 0 || performer.IsEmpty || performer == target)
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "shrine_arrest") };
        }
        var prefix = $"population-arrest-{target.Value}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
        if (!ok)
        {
            return new RoundsPopulationCustodyResult { Verdict = verdict };
        }
        var id = PlanIds.MintPlanId();
        var plan = BuildingRuntimePackage.ShrineArrestPlan(id, performer, target, bed);
        if (!arbiter.TryClaim([(PawnId)performer, (PawnId)target], "bed:" + bed))
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "arrest_claim") };
        }
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("commitArrest: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }
}
