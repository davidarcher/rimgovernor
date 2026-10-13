namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Hacks a quest target or gifts a quest item; not handled when neither is owed.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitIdeologyQuestWork(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var f = read.Projection.Facts;
        var hack = PolicyFunctions.SelectQuestHack(f, read.Projection.Identity.Map);
        var gift = PolicyFunctions.SelectQuestGift(f, read.Projection.Identity.Map);
        var id = PlanIds.MintPlanId();
        var actionId = new ActionId(id.Value + "-0");
        DomainAction action;
        string quest;
        string target;
        string kind;
        IReadOnlyList<PawnId> crew = [];
        if (hack.Reason != "")
        {
            reviewer.LogQuestSkip(call, new QuestSkip { Quest = hack.Quest, Reason = hack.Reason });
        }

        if (gift.Reason != "")
        {
            reviewer.LogQuestSkip(call, new QuestSkip { Quest = gift.Quest, Reason = gift.Reason });
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
            crew = [gift.Gift.Hauler];
        }
        else
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        if (crew.Count > 0 && !arbiter.TryClaim(crew, "quest:" + quest))
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "quest_ideology") }, true);
        }

        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        var prefix = $"quest-{kind}-{quest}-{target}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
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

        reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
