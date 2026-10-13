namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Hacks a quest target or gifts a quest item; not handled when neither is owed.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitIdeologyQuestWork(ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var f = read.Projection.Facts;
        var hack = PolicyFunctions.SelectQuestHack(f, read.Projection.Identity.Map);
        var gift = PolicyFunctions.SelectQuestGift(f, read.Projection.Identity.Map);
        var id = PlanIds.MintPlanId();
        var actionId = new ActionId(id.Value + "-0");
        DomainAction action;
        QuestId quest;
        string target;
        string kind;
        IReadOnlyList<PawnId> crew = [];
        if (hack.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(hack.Quest, "", hack.Reason, ""), call);
        }

        if (gift.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(gift.Quest, "", gift.Reason, ""), call);
        }

        if (hack.Hack is not null)
        {
            action = DomainAction.NewHackDesignationAction(actionId, hack.Hack);
            (quest, target, kind) = (hack.Quest, hack.Hack.Target, "hack");
        }
        else if (gift.Gift is not null)
        {
            action = DomainAction.NewGiveItemAction(actionId, gift.Gift);
            (quest, target, kind) = (gift.Quest, gift.Gift.Recipient.Value, "gift");
            crew = [(PawnId)gift.Gift.Hauler];
        }
        else
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        if (crew.Count > 0 && !arbiter.TryClaim(crew, "quest:" + quest))
        {
            return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "quest_ideology") }, true);
        }

        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        var prefix = $"quest-{kind}-{quest}-{target}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(reviewer.Player.Journal, goal, prefix, state.Snapshot, call);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
        }

        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitIdeologyQuestWork: session state changed or the read took too long");
        }

        reviewer.Player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
