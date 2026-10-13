namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Assigns guest beds or loads the pickup shuttle for a hospitality quest; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitHospitality(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Rounds review, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var work = PolicyFunctions.SelectHospitalityWork(read.Projection.Facts, read.Projection.Identity.Tick);
        if (work.Quest == "")
        {
            return (default, false);
        }

        if (work.Reason != "")
        {
            reviewer.LogQuestSkip(call, new QuestSkip { Quest = work.Quest, Reason = work.Reason });
            if (!work.Waiting)
            {
                return (default, false);
            }
        }

        if (work.Waiting)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "quest_hosting"), NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        var id = PlanIds.MintPlanId();
        DomainAction? action = null;
        var methodBase = "";
        if (work.Assign is not null)
        {
            action = DomainAction.NewAssignAction(new ActionId(id.Value + "-0"), work.Assign);
            methodBase = $"quest-guest-bed-{work.Quest}-{work.Assign.Pawn}-{work.Assign.Thing}";
        }

        if (work.Shuttle is not null)
        {
            action = DomainAction.NewQuestShuttleAction(new ActionId(id.Value + "-0"), work.Shuttle);
            methodBase = $"quest-pickup-{work.Quest}-{work.Shuttle.Loading}-{work.Shuttle.Launch.ToString().ToLowerInvariant()}";
        }

        if (action is null)
        {
            return (default, false);
        }

        var prefix = methodBase + "-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
        }

        if (!arbiter.TryClaim([], $"quest:{work.Quest}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "quest_hosting") }, true);
        }

        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitHospitality: session state changed or the read took too long");
        }

        reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
