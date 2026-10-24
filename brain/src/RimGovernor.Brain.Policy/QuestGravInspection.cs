using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record QuestGravInspectionWork
{
    public QuestId Quest { get; init; }

    public RecoveryService? Service { get; init; }

    public bool Waiting { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static class QuestGravInspection
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
                var engine = objective.GravEngine;
                if (objective.Kind == Pb.QuestObjectiveKind.InspectGravEngine && engine is null)
                {
                    return new QuestGravInspectionWork { Quest = offer.Quest, Waiting = true };
                }
                if (engine is null || engine.Inspected)
                {
                    continue;
                }
                var result = new QuestGravInspectionWork { Quest = offer.Quest, Waiting = true };
                if (engine.Place is not Pb.GravEnginePlace.Spawned spawned)
                {
                    return result;
                }
                if (new MapId(spawned.MapId) != home)
                {
                    return result with { Reason = "off_map" };
                }
                if (spawned.InspectingPawnIds.Count > 0)
                {
                    return result;
                }
                if (spawned.EligiblePawnIds.Count == 0)
                {
                    return result with { Reason = "inspector_unavailable" };
                }
                var service = new RecoveryService(spawned.EligiblePawnIds.Min(), engine.EngineId, RecoveryMethod.InspectGravEngine);
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
                var engine = objective.GravEngine;
                if (objective.Kind == Pb.QuestObjectiveKind.InspectGravEngine && engine is null)
                {
                    return true;
                }
                if (engine is { Inspected: false })
                {
                    return true;
                }
            }
        }
        return false;
    }
}
