using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One accepted Beggars gift answered by the colony: either a GiveItem intent, or why it waits.</summary>
public sealed record QuestGiftWork
{
    public QuestId Quest { get; init; }

    public GiveItem? Gift { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static class QuestCapacity
{
    /// <summary>
    /// Chooses only from the native eligibility list. The least skilled eligible pawn is preferred; royal favor
    /// goes to an existing title holder when one is eligible.
    /// </summary>
    public static (PawnId Pawn, bool Ok) QuestAccepter(QuestState offer, RoundsFacts facts) =>
        QuestAccepter(offer, facts, QuestRewards.SelectQuestReward(offer, facts).Choice);

    private static (PawnId Pawn, bool Ok) QuestAccepter(QuestState offer, RoundsFacts facts, int choice)
    {
        if (!offer.RequiresAccepter)
        {
            return (default, true);
        }
        if (offer.EligiblePawns.Count == 0)
        {
            return (default, false);
        }
        var royal = facts.Royalty is RoyaltyState.Active { Facts: var active } ? active : null;
        long favor = offer.Favor + (choice >= 0 && choice < offer.Rewards.Count ? offer.Rewards[choice].Favor : 0);
        var priorities = new Dictionary<PawnId, int>();
        if (facts.QuestPawns is not QuestPawns.Seen { Workers: var pawns })
        {
            return (default, false);
        }
        foreach (var pawn in pawns)
        {
            var skills = pawn.Skills;
            priorities[pawn.Id] = 0;
            foreach (var skill in skills)
            {
                if (!skill.Disabled)
                {
                    priorities[pawn.Id] += skill.Level;
                }
            }
        }
        var best = default(PawnId);
        var bestRoyal = false;
        var bestValue = 0;
        foreach (var id in offer.EligiblePawns)
        {
            if (!priorities.TryGetValue(id, out var value))
            {
                continue;
            }
            var isRoyal = false;
            if (royal is not null && favor > 0 && royal.Holders.TryGetValue(id, out var holdings))
            {
                foreach (var holding in holdings)
                {
                    isRoyal = isRoyal || holding.Title is not null;
                }
            }
            if (best.IsEmpty || (isRoyal && !bestRoyal) || (isRoyal == bestRoyal && (value < bestValue || (value == bestValue && id.CompareTo(best) < 0))))
            {
                best = id;
                bestRoyal = isRoyal;
                bestValue = value;
            }
        }
        return (best, !best.IsEmpty);
    }

    /// <summary>The Beggars gift the colony owes now: one hauler gives the requested item once stock covers it.</summary>
    public static QuestGiftWork SelectQuestGift(RoundsFacts f, MapId currentMap)
    {
        foreach (var offer in f.QuestOffers.OrderBy(o => o.Id))
        {
            if (offer.State != QuestStatus.Ongoing || offer.ScriptDef != "Beggars")
            {
                continue;
            }
            var result = new QuestGiftWork { Quest = offer.Id };
            var seen = false;
            foreach (var objective in offer.Objectives)
            {
                if (objective.GiftRequest is not { } request)
                {
                    continue;
                }
                seen = true;
                if (request.Ask is not Pb.QuestGiftAsk.Asking ask)
                {
                    return result with { Reason = "gift_not_asked" };
                }
                var remaining = ask.Remaining;
                if (remaining <= 0)
                {
                    continue;
                }
                if (new MapId(ask.MapId) != currentMap)
                {
                    return result with { Reason = "off_map" };
                }
                if (!DefTablesRules.QuestGiftDefs.Contains(request.DefName))
                {
                    return result with { Reason = "gift_def_unsupported" };
                }
                var def = new Resource(request.DefName);
                if (!f.Items.Market.TryGetValue(def, out var value) || !TradePolicyRules.Finite(value) || value <= 0)
                {
                    return result with { Reason = "gift_value_unknown" };
                }
                if (remaining * value > 700)
                {
                    return result with { Reason = "gift_value_limit" };
                }
                long have = 0;
                foreach (var amount in f.ObservedStock())
                {
                    if (amount.Resource == def)
                    {
                        have += amount.Count;
                    }
                }
                long needed = 0;
                f.ResourceNeeds.TryGetValue(def, out needed);
                if (have - Math.Max(0L, needed) < remaining)
                {
                    return result with { Reason = "gift_unaffordable" };
                }
                if (ask.Haulers.Count > 0)
                {
                    return result;
                }
                if (ask.EligibleGivers.Count == 0)
                {
                    return result with { Reason = "hauler_unavailable" };
                }
                var intent = new GiveItem(ask.EligibleGivers.Min(), request.Recipient, request.DefName, remaining);
                return result with { Gift = intent };
            }
            if (!seen)
            {
                result = result with { Reason = "gift_request_absent" };
            }
            return result;
        }
        return new QuestGiftWork();
    }

    /// <summary>
    /// Uses the roster's intended assignments, including changes that have not been applied yet. Sole primary
    /// owners of essential work stay home: the home floor counts the sole primary owners of doctor, cooking and
    /// construction, and follows roster coverage, so a colony with redundant owners may send more. Unplanned when
    /// the roster is not planned or any pawn, mood row, essential coverage or assignment is missing.
    /// </summary>
    public static QuestRoster QuestRosterOf(WorkPawnsRead workers, IReadOnlyList<MoodPawn> mood, WorkDecision roster)
    {
        var pawns = workers.Pawns;
        var states = mood;
        if (!roster.Planned)
        {
            return new QuestRoster.Unplanned();
        }
        var status = new Dictionary<PawnId, MoodPawn>();
        foreach (var pawn in states)
        {
            status[pawn.Id] = pawn;
        }
        var essential = new Dictionary<WorkType, int>();
        foreach (var row in roster.Coverage)
        {
            essential[row.Work] = row.Owners;
        }
        foreach (var work in new[] { WorkType.Doctor, WorkType.Cooking, WorkType.Construction })
        {
            if (!essential.ContainsKey(work))
            {
                return new QuestRoster.Unplanned();
            }
        }
        var assignments = new Dictionary<PawnId, IReadOnlyList<WorkPriority>>();
        foreach (var row in roster.Assignments)
        {
            assignments[row.Pawn] = row.Priorities;
        }
        var spare = new List<PawnId>();
        var floor = 0;
        foreach (var pawn in pawns)
        {
            if (!pawn.Available)
            {
                continue;
            }
            if (!status.TryGetValue(pawn.Id, out var state))
            {
                return new QuestRoster.Unplanned();
            }
            if (state.Dead || state.Downed || state.Drafted || state.Mental)
            {
                continue;
            }
            if (!assignments.TryGetValue(pawn.Id, out var priorities))
            {
                return new QuestRoster.Unplanned();
            }
            var needed = false;
            foreach (var setting in priorities)
            {
                if (essential.TryGetValue(setting.Work, out var owners) && owners <= 1 && setting.Priority == 1 && !setting.Disabled)
                {
                    needed = true;
                }
            }
            if (needed)
            {
                floor++;
            }
            else
            {
                spare.Add(pawn.Id);
            }
        }
        spare.Sort();
        return new QuestRoster.Planned { Spare = EquatableArray.From(spare), HomeFloor = floor, Work = EquatableArray.From(roster.Assignments) };
    }

    /// <summary>Whether the colony is calm: unsettled while the emergency decision rests on stale facts or is not clear.</summary>
    public static QuestCalm QuestCalmColony(RoundsFacts f, EmergencyDecision emergency, bool podsPending)
    {
        if (podsPending)
        {
            return new QuestCalm.Busy();
        }
        var stale = false;
        foreach (var hold in emergency.Holds)
        {
            if (hold.Reason is EmergencyReason.StaleFacts)
            {
                stale = true;
            }
            else
            {
                return new QuestCalm.Busy();
            }
        }
        if (stale || !emergency.Clear)
        {
            return new QuestCalm.Unsettled();
        }
        return RoundsRules.CombatSituation(f.Hostiles, HuntSquadRules.HuntRequest(f.FoodPlan).Count) switch
        {
            Situation.Clear => QuestCalm.Settled,
            Situation.Active => new QuestCalm.Busy(),
            Situation.Unclear => new QuestCalm.Unsettled(),
            var unexpected => throw new DomainException($"invalid combat situation: {unexpected}"),
        };
    }
}
