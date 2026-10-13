using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public sealed record QuestHackWork
{
    public QuestId Quest { get; init; }

    public HackDesignation? Hack { get; init; }

    public bool Waiting { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Follows the native filter predicate: destruction satisfies only a filter which explicitly permits it.
    /// </summary>
    public static bool QuestHackComplete(JoinerOffer offer)
    {
        var seen = false;
        foreach (var objective in offer.Objectives)
        {
            foreach (var target in objective.HackTargets)
            {
                seen = true;
                if (!target.Satisfied.TryGetValue(out var satisfied) || !satisfied)
                {
                    return false;
                }
            }
        }
        return seen;
    }

    public static bool IdeologyWorkDeficit(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var offers))
        {
            return false;
        }
        foreach (var offer in offers)
        {
            if (offer.State != "Ongoing" || !offer.Profile.TryGetValue(out var profile))
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
                    if (objective.Gift.TryGetValue(out var request))
                    {
                        seen = true;
                        if (!request.Remaining.TryGetValue(out var remaining) || remaining > 0)
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

    public static QuestSkipReason WorshippedTerminalAdmission(JoinerOffer offer)
    {
        if (offer.ScriptDef != "Hack_WorshippedTerminal")
        {
            return "";
        }
        foreach (var objective in offer.Objectives)
        {
            if (objective.HackRisk.TryGetValue(out var risk))
            {
                var hk = risk.Hostile.TryGetValue(out var hostile);
                if (!hk || risk.FactionId.Length == 0)
                {
                    return "tribe_unknown";
                }
                if (!hostile)
                {
                    return "worshipped_terminal_trap";
                }
                return "";
            }
        }
        return "tribe_unknown";
    }

    public static QuestSkipReason HackAdmission(JoinerOffer offer, RoundsFacts f)
    {
        var reason = WorshippedTerminalAdmission(offer);
        if (!reason.IsNone)
        {
            return reason;
        }
        if (offer.ScriptDef == "Hack_Spacedrone")
        {
            return JoinerThreatReason(offer, f);
        }
        return "";
    }

    public static QuestHackWork SelectQuestHack(RoundsFacts f, MapId currentMap)
    {
        if (!f.QuestOffers.TryGetValue(out var all))
        {
            return new QuestHackWork();
        }
        foreach (var offer in all.OrderBy(o => o.Quest))
        {
            if (offer.State != "Ongoing")
            {
                continue;
            }
            if (!offer.Profile.TryGetValue(out var profile) || (profile.Family != QuestFamily.Hack && offer.ScriptDef != "Gravcore_MechanoidRelay"))
            {
                continue;
            }
            var result = new QuestHackWork { Quest = offer.Quest, Waiting = true };
            var admission = WorshippedTerminalAdmission(offer);
            if (!admission.IsNone)
            {
                return result with { Reason = admission };
            }
            var targets = new List<QuestHackTarget>();
            foreach (var objective in offer.Objectives)
            {
                targets.AddRange(objective.HackTargets);
            }
            if (targets.Count == 0)
            {
                return result with { Reason = "hack_target_unknown" };
            }
            foreach (var target in targets.OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                if (!target.Satisfied.TryGetValue(out var satisfied))
                {
                    return result with { Reason = "hack_state_unknown" };
                }
                if (satisfied)
                {
                    continue;
                }
                if (!target.Spawned.TryGetValue(out var spawned) || !spawned)
                {
                    return result with { Reason = "hack_target_unavailable" };
                }
                if (target.Map != currentMap)
                {
                    return result with { Reason = "off_map" };
                }
                var lk = target.LockedOut.TryGetValue(out var locked);
                var ak = target.Autohack.TryGetValue(out var auto);
                if (!lk || !ak)
                {
                    return result with { Reason = "hack_state_unknown" };
                }
                if (locked || auto)
                {
                    return result;
                }
                if (target.EligiblePawnIds.Count == 0)
                {
                    return result with { Reason = "hacker_unavailable" };
                }
                return result with { Hack = new HackDesignation(target.Id, true), Waiting = false };
            }
        }
        return new QuestHackWork();
    }

    public static QuestSkipReason RelicAdmission(JoinerOffer offer, RoundsFacts f)
    {
        if (offer.ScriptDef != "RelicHunt")
        {
            return "";
        }
        var ck = f.QuestColonyCalm.TryGetValue(out var calm);
        var sk = f.QuestSparePawns.TryGetValue(out var spare);
        var wk = f.QuestWorkers.TryGetValue(out var workers);
        if (!ck || !sk || !wk)
        {
            return "capacity_unknown";
        }
        if (!calm)
        {
            return "colony_busy";
        }
        if (spare!.Count == 0)
        {
            return "no_spare_pawn";
        }
        foreach (var worker in workers!)
        {
            var ak = worker.Available.TryGetValue(out var available);
            var pk = worker.Work.TryGetValue(out var work);
            if (!ak || !pk || !available || !spare.Contains(worker.Id))
            {
                continue;
            }
            foreach (var assignment in work!)
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
