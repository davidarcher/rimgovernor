namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Loads a quest shuttle with its pawns; the result carries wait ticks even when the step is not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitDeparture(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var work = PolicyFunctions.SelectQuestDeparture(read.Projection.Facts);
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

        if (work.Shuttle is null)
        {
            return (default, false);
        }

        if (!arbiter.TryClaim(work.Shuttle.Pawns(), $"quest:{work.Quest}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "quest_departure") }, true);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewQuestShuttleAction(new ActionId(id.Value + "-0"), work.Shuttle);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        var prefix = $"quest-departure-{work.Quest}-{work.Shuttle.Loading}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
        }

        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitDeparture: session state changed or the read took too long");
        }

        reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
