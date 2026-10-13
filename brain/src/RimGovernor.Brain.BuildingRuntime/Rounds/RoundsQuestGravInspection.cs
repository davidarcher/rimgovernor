namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Services a quest's grav inspection; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitGravInspection(ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var work = PolicyFunctions.SelectQuestGravInspection(read.Projection.Facts, read.Projection.Identity.Map);
        if (work.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (work.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(work.Quest, "", work.Reason, ""), call);
        }

        if (work.Waiting)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        var service = work.Service!;
        if (!arbiter.TryClaim([(PawnId)service.Pawn], $"service:{service.Thing}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "grav_inspection") }, true);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRecoveryServiceAction(new ActionId(id.Value + "-0"), service);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        var prefix = $"grav-inspection-{work.Quest}-{service.Thing}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(reviewer.Player.Journal, goal, prefix, state.Snapshot, call);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
        }

        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitGravInspection: session state changed or the read took too long");
        }

        reviewer.Player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
