using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    public static Dictionary<PawnId, QuestId> QuestRefugeeIds<TOffers>(Fact<TOffers> offers)
        where TOffers : IReadOnlyList<JoinerOffer>
    {
        var output = new Dictionary<PawnId, QuestId>();
        if (!offers.TryGetValue(out var rows))
        {
            return output;
        }
        foreach (var offer in rows)
        {
            var known = offer.Profile.TryGetValue(out var profile);
            if (offer.State != "Ongoing" || !known || (profile!.Family != QuestFamily.RefugeePod && profile.Family != QuestFamily.Site))
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Kind == Pb.QuestObjectiveKind.RescuePawns)
                {
                    foreach (var id in objective.PawnIds)
                    {
                        output[id] = offer.Quest;
                    }
                }
            }
        }
        return output;
    }

    public static bool RefugeeDeficit(RoundsFacts f) => QuestRefugeeIds(f.QuestOffers).Count > 0;
}
