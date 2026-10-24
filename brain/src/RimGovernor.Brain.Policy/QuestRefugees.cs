using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static class QuestRefugees
{
    public static Dictionary<PawnId, QuestId> QuestRefugeeIds(RoundsFacts f)
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
                if (objective.Kind == Pb.QuestObjectiveKind.RescuePawns)
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

    public static bool RefugeeDeficit(RoundsFacts f) => QuestRefugeeIds(f).Count > 0;
}
