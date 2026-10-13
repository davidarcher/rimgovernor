namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Services a quest's grav inspection; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitGravInspection(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var work = PolicyFunctions.SelectQuestGravInspection(read.Projection.Facts, read.Projection.Identity.Map);
        if (work.Quest == "")
        {
            return (default, false);
        }

        if (work.Reason != "")
        {
            reviewer.LogQuestSkip(call, new QuestSkip { Quest = work.Quest, Reason = work.Reason });
        }

        if (work.Waiting)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        var service = work.Service!;
        if (!arbiter.TryClaim([service.Pawn], $"service:{service.Thing}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "grav_inspection") }, true);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRecoveryServiceAction(new ActionId(id.Value + "-0"), service);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        var prefix = $"grav-inspection-{work.Quest}-{service.Thing}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
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

        reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
