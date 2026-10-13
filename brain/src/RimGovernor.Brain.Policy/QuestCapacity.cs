using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>One accepted Beggars gift answered by the colony: either a GiveItem intent, or why it waits.</summary>
public sealed record QuestGiftWork
{
    public QuestId Quest { get; init; }

    public GiveItem? Gift { get; init; }

    public bool Waiting { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Chooses only from the native eligibility list. The least skilled eligible pawn is preferred; royal favor
    /// goes to an existing title holder when one is eligible.
    /// </summary>
    public static (PawnId Pawn, bool Ok) QuestAccepter<TWorkers>(JoinerOffer offer, RoundsFacts facts, Fact<TWorkers> workers)
        where TWorkers : IReadOnlyList<WorkPawn> =>
        QuestAccepter(offer, facts, workers, SelectQuestReward(offer, facts).Choice);

    private static (PawnId Pawn, bool Ok) QuestAccepter<TWorkers>(JoinerOffer offer, RoundsFacts facts, Fact<TWorkers> workers, int choice)
        where TWorkers : IReadOnlyList<WorkPawn>
    {
        if (!offer.RequiresAccepter)
        {
            return (default, true);
        }
        if (offer.EligiblePawnIds.Count == 0)
        {
            return (default, false);
        }
        var royalKnown = facts.Royalty.TryGetValue(out var royal);
        long favor = 0;
        foreach (var reward in offer.Favor)
        {
            if (reward.Choice == choice || reward.Choice == -1)
            {
                favor += reward.Favor;
            }
        }
        var priorities = new Dictionary<PawnId, int>();
        if (!workers.TryGetValue(out var pawns))
        {
            return (default, false);
        }
        foreach (var pawn in pawns)
        {
            if (!pawn.Skills.TryGetValue(out var skills))
            {
                return (default, false);
            }
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
        foreach (var id in offer.EligiblePawnIds)
        {
            if (!priorities.TryGetValue(id, out var value))
            {
                continue;
            }
            var isRoyal = false;
            if (royalKnown && favor > 0 && royal!.Holders.TryGetValue(id, out var holdings))
            {
                foreach (var holding in holdings)
                {
                    isRoyal = isRoyal || holding.Title.Length != 0;
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
        if (!f.QuestOffers.TryGetValue(out var offers))
        {
            return new QuestGiftWork();
        }
        foreach (var offer in offers.OrderBy(o => o.Quest))
        {
            if (offer.State != "Ongoing" || offer.ScriptDef != "Beggars")
            {
                continue;
            }
            var result = new QuestGiftWork { Quest = offer.Quest, Waiting = true };
            var seen = false;
            foreach (var objective in offer.Objectives)
            {
                if (!objective.Gift.TryGetValue(out var request))
                {
                    continue;
                }
                seen = true;
                if (!request.Remaining.TryGetValue(out var remaining))
                {
                    return result with { Reason = "gift_request_unknown" };
                }
                if (remaining <= 0)
                {
                    continue;
                }
                if (request.Map != currentMap)
                {
                    return result with { Reason = "off_map" };
                }
                if (!f.Resources.TryGetValue(out var stock))
                {
                    return result with { Reason = "stock_unknown" };
                }
                if (!QuestGiftDefs.Contains(request.Def))
                {
                    return result with { Reason = "gift_def_unsupported" };
                }
                var def = new Resource(request.Def);
                if (!f.Items.Market.TryGetValue(def, out var value) || !Finite(value) || value <= 0)
                {
                    return result with { Reason = "gift_value_unknown" };
                }
                if (remaining * value > 700)
                {
                    return result with { Reason = "gift_value_limit" };
                }
                long have = 0;
                foreach (var amount in stock)
                {
                    if (amount.Resource == def)
                    {
                        have += amount.Count;
                    }
                }
                long needed = 0;
                f.ResourceNeeds?.TryGetValue(def, out needed);
                if (have - Math.Max(0L, needed) < remaining)
                {
                    return result with { Reason = "gift_unaffordable" };
                }
                if (request.HaulingPawnIds.Count > 0)
                {
                    return result;
                }
                if (request.EligiblePawnIds.Count == 0)
                {
                    return result with { Reason = "hauler_unavailable" };
                }
                var actor = request.EligiblePawnIds.Min();
                var intent = new GiveItem(actor, request.Recipient, request.Def, remaining);
                return result with { Gift = intent, Waiting = false };
            }
            if (!seen)
            {
                result = result with { Reason = "gift_request_unknown" };
            }
            return result;
        }
        return new QuestGiftWork();
    }

    /// <summary>
    /// Uses the roster's intended assignments, including changes that have not been applied yet. Sole primary
    /// owners of essential work stay home.
    /// </summary>
    public static Fact<IReadOnlyList<PawnId>> QuestSpareColonists<TWorkers, TMood>(Fact<TWorkers> workers, Fact<TMood> mood, WorkDecision roster)
        where TWorkers : IReadOnlyList<WorkPawn>
        where TMood : IReadOnlyList<MoodPawn>
    {
        var (spare, _, known) = QuestRosterSplit(workers, mood, roster);
        return known ? Known<IReadOnlyList<PawnId>>(spare) : Unknown<IReadOnlyList<PawnId>>();
    }

    /// <summary>
    /// The number of colonists who must stay home: the sole primary owners of doctor, cooking and
    /// construction. It follows roster coverage, so a colony with redundant owners may send more.
    /// </summary>
    public static Fact<int> QuestHomeFloor<TWorkers, TMood>(Fact<TWorkers> workers, Fact<TMood> mood, WorkDecision roster)
        where TWorkers : IReadOnlyList<WorkPawn>
        where TMood : IReadOnlyList<MoodPawn>
    {
        var (_, floor, known) = QuestRosterSplit(workers, mood, roster);
        return known ? Known(floor) : Unknown<int>();
    }

    private static (List<PawnId> Spare, int Floor, bool Known) QuestRosterSplit<TWorkers, TMood>(Fact<TWorkers> workers, Fact<TMood> mood, WorkDecision roster)
        where TWorkers : IReadOnlyList<WorkPawn>
        where TMood : IReadOnlyList<MoodPawn>
    {
        var wk = workers.TryGetValue(out var pawns);
        var mk = mood.TryGetValue(out var states);
        if (!roster.Capacity.TryGetValue(out _) || !wk || !mk)
        {
            return ([], 0, false);
        }
        var status = new Dictionary<PawnId, MoodPawn>();
        foreach (var pawn in states!)
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
                return ([], 0, false);
            }
        }
        var assignments = new Dictionary<PawnId, IReadOnlyList<WorkPriority>>();
        foreach (var row in roster.Assignments)
        {
            assignments[row.Pawn] = row.Priorities;
        }
        var spare = new List<PawnId>();
        var floor = 0;
        foreach (var pawn in pawns!)
        {
            if (!pawn.Available.TryGetValue(out var available))
            {
                return ([], 0, false);
            }
            if (!available)
            {
                continue;
            }
            if (!status.TryGetValue(pawn.Id, out var state) || !AllKnown(state.Dead, state.Downed, state.Drafted, state.Mental))
            {
                return ([], 0, false);
            }
            if (Positive(state.Dead) || Positive(state.Downed) || Positive(state.Drafted) || Positive(state.Mental))
            {
                continue;
            }
            if (!assignments.TryGetValue(pawn.Id, out var priorities))
            {
                return ([], 0, false);
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
        return (spare, floor, true);
    }

    /// <summary>Retains uncertainty from the existing emergency decision.</summary>
    public static Fact<bool> QuestCalmColony(RoundsFacts f, EmergencyDecision emergency, bool podsPending)
    {
        if (podsPending)
        {
            return Known(false);
        }
        var unknown = false;
        foreach (var hold in emergency.Holds)
        {
            if (hold.Reason is EmergencyReason.UnknownFacts or EmergencyReason.StaleFacts)
            {
                unknown = true;
            }
            else
            {
                return Known(false);
            }
        }
        if (unknown)
        {
            return Unknown<bool>();
        }
        if (!emergency.Clear)
        {
            return Unknown<bool>();
        }
        return CombatCleared(f);
    }
}
