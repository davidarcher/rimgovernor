using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public static class QuestRefugees
{
    public static Dictionary<PawnId, QuestId> QuestRefugeeIds(ReviewFacts f)
    {
        var output = new Dictionary<PawnId, QuestId>();
        var rows = f.QuestOffers;
        foreach (var offer in rows)
        {
            if (offer.State != QuestStatus.Ongoing || f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile }
                || (profile.Family != QuestFamily.RefugeePod && profile.Family != QuestFamily.Site))
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Kind == RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.RescuePawns)
                {
                    foreach (var id in objective.PawnIds)
                    {
                        output[id] = offer.Id;
                    }
                }
            }
        }
        return output;
    }

    public static bool RefugeeDeficit(ReviewFacts f) => QuestRefugeeIds(f).Count > 0;
}
