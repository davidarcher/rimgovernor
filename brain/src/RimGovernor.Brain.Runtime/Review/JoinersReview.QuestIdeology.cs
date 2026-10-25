namespace RimGovernor.Brain.Runtime;

public sealed partial class JoinersReview
{
    /// <summary>Hacks a quest target or holds a quest gift; not handled when neither is owed.</summary>
    internal (Verdict Verdict, bool Handled) AdmitIdeologyQuestWork(NeedWork work, ReviewReading read, DateTimeOffset started, CancellationToken call)
    {
        var f = read.Reading.Facts;
        var hack = QuestHackPolicy.SelectQuestHack(f, read.Reading.Identity.Map);
        var gift = QuestCapacity.SelectQuestGift(f, read.Reading.Identity.Map);
        if (hack.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(hack.Quest, "", hack.Reason, ""), call);
        }

        if (gift.Reason != "")
        {
            reviewer.LogQuestSkip(new QuestSkip(gift.Quest, "", gift.Reason, ""), call);
        }

        if (hack.Hack is null)
        {
            return gift.Gift is not null
                ? (Verdict.AwaitingPlan("unsupported_forecast_quest_gift", "Quest item delivery requires shared participant, material and destination alternatives."), true)
                : (Verdict.BuildingReasonExistingWork, false);
        }

        var id = PlanIds.MintPlanId();
        var plan = new PlanSpec(id, new PlanRevision(1), [DomainAction.NewHackDesignationAction(new ActionId(id.Value + "-0"), hack.Hack)]);
        var method = new MethodId($"quest-hack-{hack.Quest}-{hack.Hack.Target}");
        return (Commit(work, method, plan, started, "admitIdeologyQuestWork", call), true);
    }
}
