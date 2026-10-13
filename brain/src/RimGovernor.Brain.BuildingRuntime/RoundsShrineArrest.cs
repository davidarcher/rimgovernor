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
        CancellationToken call,
        CancellationToken epoch,
        Player p,
        ControlState state,
        DateTimeOffset started,
        Store.StandardState goal,
        Pol.RoundsFacts facts,
        IReadOnlyList<Pol.ShrineDefenderFacts> squad,
        Domain.PawnId target,
        StepArbiter arbiter)
    {
        var bed = Pol.Shrine.ShrineArrestBed(facts.Sleeping);
        var performer = Pol.Shrine.ShrineArrester(squad);
        if (bed.Length == 0 || performer.IsEmpty || performer == target)
        {
            return new RoundsPopulationCustodyResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "shrine_arrest") };
        }
        var prefix = $"population-arrest-{target}-";
        var (method, verdict, ok) = AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsPopulationCustodyResult { Verdict = verdict };
        }
        var id = PlanIds.MintPlanId();
        var plan = ShrineArrestPlan(id, performer, target, bed);
        if (!arbiter.TryClaim([performer, target], "bed:" + bed))
        {
            return new RoundsPopulationCustodyResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "arrest_claim") };
        }
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("commitArrest: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsPopulationCustodyResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
