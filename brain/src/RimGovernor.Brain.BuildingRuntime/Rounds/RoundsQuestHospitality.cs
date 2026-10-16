namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class JoinersRounds
{
    /// <summary>Assigns guest beds or loads the pickup shuttle for a hospitality quest; a wait is not handled.</summary>
    internal (Verdict Verdict, bool Handled) AdmitHospitality(NeedWork work, RoundsReading read, DateTimeOffset started, CancellationToken call)
    {
        var step = PolicyFunctions.SelectHospitalityWork(read.Projection.Facts, read.Projection.Identity.Tick);
        if (step.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (step.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(step.Quest, "", step.Reason, ""), call);
            if (!step.Waiting)
            {
                return (default, false);
            }
        }

        if (step.Waiting)
        {
            return (Verdict.WaitFor(Cause.MethodUsed, "quest_hosting"), false);
        }

        if (step.Shuttle is { Loading: ShuttleLoading.ExplicitPawns } or { Loading: ShuttleLoading.Autoload, Autoload: true })
        {
            return (Verdict.AwaitingPlan("unsupported_forecast_shuttle_loading",
                "Shuttle loading requires shared forecast participation and completion admission."), true);
        }

        var id = PlanIds.MintPlanId();
        DomainAction? action = null;
        var method = default(MethodId);
        if (step.Assign is not null)
        {
            action = DomainAction.NewAssignAction(new ActionId(id.Value + "-0"), step.Assign);
            method = new MethodId($"quest-guest-bed-{step.Quest}-{step.Assign.Pawn}-{step.Assign.Thing}");
        }

        if (step.Shuttle is not null)
        {
            action = DomainAction.NewQuestShuttleAction(new ActionId(id.Value + "-0"), step.Shuttle);
            method = new MethodId($"quest-pickup-{step.Quest}-{step.Shuttle.Loading}-{step.Shuttle.Launch.ToString().ToLowerInvariant()}");
        }

        if (action is null)
        {
            return (default, false);
        }

        if (!Claim(work, [], $"quest:{step.Quest}"))
        {
            return (Verdict.WaitFor(Cause.Claim, "quest_hosting"), true);
        }

        return (Commit(work, method, new PlanSpec(id, new PlanRevision(1), [action]), started, "admitHospitality", call), true);
    }
}
