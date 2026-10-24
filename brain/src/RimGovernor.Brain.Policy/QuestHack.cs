using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record QuestHackWork
{
    public QuestId Quest { get; init; }

    public HackDesignation? Hack { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static class QuestHackPolicy
{
    /// <summary>
    /// Follows the native filter predicate: destruction satisfies only a filter which explicitly permits it.
    /// </summary>
    public static bool QuestHackComplete(QuestState offer)
    {
        var seen = false;
        foreach (var objective in offer.Objectives)
        {
            foreach (var target in objective.HackTargets)
            {
                seen = true;
                if (!target.Satisfied)
                {
                    return false;
                }
            }
        }
        return seen;
    }

    public static bool IdeologyWorkDeficit(RoundsFacts f)
    {
        var offers = f.QuestOffers;
        foreach (var offer in offers)
        {
            if (offer.State != QuestStatus.Ongoing || f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile })
            {
                continue;
            }
            if ((profile.Family == QuestFamily.Hack || offer.ScriptDef == "Gravcore_MechanoidRelay") && !QuestHackComplete(offer))
            {
                return true;
            }
            if (profile.Family == QuestFamily.Beggars)
            {
                var seen = false;
                foreach (var objective in offer.Objectives)
                {
                    if (objective.GiftRequest is { } request)
                    {
                        seen = true;
                        // Until the recipient asks, the gift is still owed.
                        if (request.Ask is not Pb.QuestGiftAsk.Asking { Remaining: <= 0 })
                        {
                            return true;
                        }
                    }
                }
                if (!seen)
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static QuestSkipReason WorshippedTerminalAdmission(QuestState offer)
    {
        if (offer.ScriptDef != "Hack_WorshippedTerminal")
        {
            return "";
        }
        foreach (var objective in offer.Objectives)
        {
            if (objective.HackRisk is { } risk)
            {
                return risk.Hostile ? "" : "worshipped_terminal_trap";
            }
        }
        // No single worshipped-terminal site with a faction holds the terminal.
        return "terminal_tribe_absent";
    }

    public static QuestSkipReason HackAdmission(QuestState offer, RoundsFacts f)
    {
        var reason = WorshippedTerminalAdmission(offer);
        if (!reason.IsNone)
        {
            return reason;
        }
        if (offer.ScriptDef == "Hack_Spacedrone")
        {
            return QuestSelection.JoinerThreatReason(offer, f);
        }
        return "";
    }

    public static QuestHackWork SelectQuestHack(RoundsFacts f, MapId currentMap)
    {
        var all = f.QuestOffers;
        foreach (var offer in all.OrderBy(o => o.Id))
        {
            if (offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile } || (profile.Family != QuestFamily.Hack && offer.ScriptDef != "Gravcore_MechanoidRelay"))
            {
                continue;
            }
            var result = new QuestHackWork { Quest = offer.Id };
            var admission = WorshippedTerminalAdmission(offer);
            if (!admission.IsNone)
            {
                return result with { Reason = admission };
            }
            var targets = new List<Pb.QuestHackTarget>();
            foreach (var objective in offer.Objectives)
            {
                targets.AddRange(objective.HackTargets);
            }
            if (targets.Count == 0)
            {
                return result with { Reason = "hack_target_absent" };
            }
            foreach (var target in targets.OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                if (target.Satisfied)
                {
                    continue;
                }
                if (target.Place is not Pb.QuestHackTargetPlace.OnMap place)
                {
                    return result with { Reason = "hack_target_unavailable" };
                }
                if (new MapId(place.MapId) != currentMap)
                {
                    return result with { Reason = "off_map" };
                }
                if (target.LockedOut || target.Autohack)
                {
                    return result;
                }
                if (place.EligibleHackers.Count == 0)
                {
                    return result with { Reason = "hacker_unavailable" };
                }
                return result with { Hack = new HackDesignation(target.Id, true) };
            }
        }
        return new QuestHackWork();
    }

    public static QuestSkipReason RelicAdmission(QuestState offer, RoundsFacts f)
    {
        if (offer.ScriptDef != "RelicHunt")
        {
            return "";
        }
        if (f.QuestCalm is QuestCalm.Unsettled || f.QuestRoster is not QuestRoster.Planned { Spare: var spare } || f.QuestPawns is not QuestPawns.Seen { Workers: var workers })
        {
            return "capacity_unknown";
        }
        if (f.QuestCalm is QuestCalm.Busy)
        {
            return "colony_busy";
        }
        if (spare.Count == 0)
        {
            return "no_spare_pawn";
        }
        foreach (var worker in workers)
        {
            if (!worker.Available || !spare.Contains(worker.Id))
            {
                continue;
            }
            foreach (var assignment in worker.Work.Priorities())
            {
                if (assignment.Work == WorkType.Research && !assignment.Disabled && assignment.Priority > 0)
                {
                    return "";
                }
            }
        }
        return "hacker_unavailable";
    }
}
