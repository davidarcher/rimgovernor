namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Assigns guest beds or loads the pickup shuttle for a hospitality quest; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitHospitality(ControlState state, StandardState goal, Rounds review, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var work = PolicyFunctions.SelectHospitalityWork(read.Projection.Facts, read.Projection.Identity.Tick);
        if (work.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (work.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(work.Quest, "", work.Reason, ""), call);
            if (!work.Waiting)
            {
                return (default, false);
            }
        }

        if (work.Waiting)
        {
            return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "quest_hosting"), NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
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
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(reviewer.Player.Journal, goal, prefix, state.Snapshot, call);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
        }

        if (!arbiter.TryClaim([], $"quest:{work.Quest}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "quest_hosting") }, true);
        }

        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitHospitality: session state changed or the read took too long");
        }

        reviewer.Player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
