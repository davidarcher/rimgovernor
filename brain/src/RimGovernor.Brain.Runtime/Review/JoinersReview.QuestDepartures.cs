namespace RimGovernor.Brain.Runtime;

/// <summary>Domain action payloads carry Domain pawn ids; the arbiter and policy functions take Policy ids.</summary>
internal static class QuestPawnIds
{
    internal static List<PawnId> ToPolicyIds(this IEnumerable<Domain.PawnId> ids) => [.. ids.Select(id => (PawnId)id)];
}

public sealed partial class JoinersReview
{
    /// <summary>Loads a quest shuttle with its pawns; a wait for the shuttle is not handled.</summary>
    internal (Verdict Verdict, bool Handled) AdmitDeparture(NeedWork work, ReviewReading read, DateTimeOffset started, CancellationToken call)
    {
        var step = QuestDeparture.SelectQuestDeparture(read.Reading.Facts);
        if (step.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (!step.Reason.IsNone)
        {
            reviewer.LogQuestSkip(new QuestSkip(step.Quest, "", step.Reason, ""), call);
        }

        if (step.Waiting)
        {
            return (Verdict.BuildingReasonExistingWork, false);
        }

        if (step.Shuttle is null)
        {
            return (default, false);
        }

        if (step.Shuttle is { Loading: ShuttleLoading.ExplicitPawns } or { Loading: ShuttleLoading.Autoload, Autoload: true })
        {
            return (Verdict.AwaitingPlan("unsupported_forecast_shuttle_loading",
                "Shuttle loading requires shared forecast participation and completion admission."), true);
        }

        if (!Claim(work, step.Shuttle.Pawns.ToPolicyIds(), $"quest:{step.Quest}"))
        {
            return (Verdict.WaitFor(Cause.Claim, "quest_departure"), true);
        }

        var id = PlanIds.MintPlanId();
        var plan = new PlanSpec(id, new PlanRevision(1), [DomainAction.NewQuestShuttleAction(new ActionId(id.Value + "-0"), step.Shuttle)]);
        var method = new MethodId($"quest-departure-{step.Quest}-{Wire.Name(step.Shuttle.Loading)}");
        return (Commit(work, method, plan, started, "admitDeparture", call), true);
    }
}
