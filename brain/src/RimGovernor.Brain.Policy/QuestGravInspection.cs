using RimGovernor.Brain.Domain;
using DomainRecoveryMethod = RimGovernor.Brain.Domain.RecoveryMethod;
using Pb = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record QuestGravInspectionWork
{
    public QuestId Quest { get; init; }

    public RecoveryService? Service { get; init; }

    public bool Waiting { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static partial class PolicyFunctions
{
    public static QuestGravInspectionWork SelectQuestGravInspection(RoundsFacts f, MapId home)
    {
        if (!f.QuestOffers.TryGetValue(out var offers))
        {
            return new QuestGravInspectionWork();
        }
        foreach (var offer in offers.OrderBy(o => o.Quest))
        {
            if (offer.State != "Ongoing")
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                var known = objective.GravEngine.TryGetValue(out var engine);
                if (objective.Kind == Pb.QuestObjectiveKind.InspectGravEngine && !known)
                {
                    return new QuestGravInspectionWork { Quest = offer.Quest, Waiting = true };
                }
                if (!known)
                {
                    continue;
                }
                var result = new QuestGravInspectionWork { Quest = offer.Quest, Waiting = true };
                if (!engine!.Inspected.TryGetValue(out var inspected))
                {
                    return result with { Reason = "inspection_unknown" };
                }
                if (inspected)
                {
                    continue;
                }
                if (!engine.Spawned.TryGetValue(out var spawned) || !spawned)
                {
                    return result;
                }
                if (engine.Map != home)
                {
                    return result with { Reason = "off_map" };
                }
                if (engine.InspectingPawnIds.Count > 0)
                {
                    return result;
                }
                if (engine.EligiblePawns.Count == 0)
                {
                    return result with { Reason = "inspector_unavailable" };
                }
                var service = new RecoveryService((Domain.PawnId)engine.EligiblePawns.Min(), engine.Id, DomainRecoveryMethod.InspectGravEngine);
                return result with { Service = service, Waiting = false };
            }
        }
        return new QuestGravInspectionWork();
    }

    public static bool GravInspectionDeficit(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var offer in rows)
        {
            if (offer.State != "Ongoing")
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                var known = objective.GravEngine.TryGetValue(out var engine);
                if (objective.Kind == Pb.QuestObjectiveKind.InspectGravEngine && !known)
                {
                    return true;
                }
                if (known && (!engine!.Inspected.TryGetValue(out var inspected) || !inspected))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
