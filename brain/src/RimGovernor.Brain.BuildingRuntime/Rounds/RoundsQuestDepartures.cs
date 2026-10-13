namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Domain action payloads carry Domain pawn ids; the arbiter and policy functions take Policy ids.</summary>
internal static class QuestPawnIds
{
    internal static List<PawnId> ToPolicyIds(this IEnumerable<Domain.PawnId> ids) => [.. ids.Select(id => (PawnId)id)];
}

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Loads a quest shuttle with its pawns; the result carries wait ticks even when the step is not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitDeparture(ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var work = PolicyFunctions.SelectQuestDeparture(read.Projection.Facts);
        if (work.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (!work.Reason.IsNone)
        {
            reviewer.LogQuestSkip(new QuestSkip(work.Quest, "", work.Reason, ""), call);
        }

        if (work.Waiting)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        if (work.Shuttle is null)
        {
            return (default, false);
        }

        if (!arbiter.TryClaim(work.Shuttle.Pawns.ToPolicyIds(), $"quest:{work.Quest}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "quest_departure") }, true);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewQuestShuttleAction(new ActionId(id.Value + "-0"), work.Shuttle);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        var prefix = $"quest-departure-{work.Quest}-{Wire.Name(work.Shuttle.Loading)}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(reviewer.Player.Journal, goal, prefix, state.Snapshot, call);
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

        reviewer.Player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
