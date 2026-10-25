using RimGovernor.Brain.Domain;

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
    public static QuestGravInspectionWork SelectQuestGravInspection(ReviewFacts f, MapId home)
    {
        var offers = f.QuestOffers;
        foreach (var offer in offers.OrderBy(o => o.Id))
        {
            if (offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                var engine = objective.GravEngine;
                if (objective.Kind == RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.InspectGravEngine && engine is null)
                {
                    return new QuestGravInspectionWork { Quest = offer.Id, Waiting = true };
                }
                if (engine is null || engine.Inspected)
                {
                    continue;
                }
                var result = new QuestGravInspectionWork { Quest = offer.Id, Waiting = true };
                if (engine.Place is not RimGovernor.Brain.GameFacts.Observations.GravEnginePlace.Spawned spawned)
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

    public static bool GravInspectionDeficit(ReviewFacts f)
    {
        var rows = f.QuestOffers;
        foreach (var offer in rows)
        {
            if (offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                var engine = objective.GravEngine;
                if (objective.Kind == RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.InspectGravEngine && engine is null)
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
