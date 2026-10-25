using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A colonist on the current map as the departure planners read it: the native quest worker row and its defense points.</summary>
public sealed record QuestDeparturePawn
{
    public required PawnId Id { get; init; }

    public required bool HealthyAdult { get; init; }

    public required bool CanFight { get; init; }

    /// <summary>Melee power plus ranged DPS in raid points; 0 for a pawn that cannot fight.</summary>
    public required double DefensePoints { get; init; }

    public required double CarryCapacity { get; init; }

    public required double CarriedMass { get; init; }

    public required bool FactionLeader { get; init; }

    public required Pb.QuestNegotiation Negotiation { get; init; }
}

public sealed record QuestDepartureWork
{
    public QuestId Quest { get; init; }

    public QuestShuttle? Shuttle { get; init; }

    public bool Waiting { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static class QuestDeparture
{
    /// <summary>
    /// Leaves every work type's last primary owner at home and prices a fighting squad against the colony's
    /// observed defense headroom.
    /// </summary>
    public static (IReadOnlyList<PawnId> Squad, QuestSkipReason Reason) DepartureSquad(QuestState offer, RoundsFacts f) =>
        DepartureSquadOf(offer, f, false);

    internal static (IReadOnlyList<PawnId> Squad, QuestSkipReason Reason) DepartureSquadOf(QuestState offer, RoundsFacts f, bool strongestFirst) =>
        DepartureSquadWithCrew(offer, f, strongestFirst, false);

    internal static (IReadOnlyList<PawnId> Squad, QuestSkipReason Reason) DepartureSquadWithCrew(QuestState offer, RoundsFacts f, bool strongestFirst, bool allowNonfighters)
    {
        if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile })
        {
            return ([], QuestSkipReason.ClassUnknown);
        }
        if (profile.Family != QuestFamily.PawnLend && profile.Family != QuestFamily.BanditCamp)
        {
            return ([], "");
        }
        // The headcount is the largest LoadPawns goal; 0 when one names no count, which the squad reports as unknown.
        long need = 0;
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind != Pb.QuestObjectiveKind.LoadPawns)
            {
                continue;
            }
            if (objective.Goal is not Pb.QuestObjectiveGoal.Pawns { Count: var count })
            {
                need = 0;
                break;
            }
            need = Math.Max(need, count);
        }
        return DepartureSquadFor(offer.Id, profile.Family, need, f, strongestFirst, allowNonfighters);
    }

    /// <summary>
    /// The squad of <paramref name="need"/> spare pawns a lend (<see cref="QuestFamily.PawnLend"/>) or raid
    /// (<see cref="QuestFamily.BanditCamp"/>) departure takes, beside the pawns other open quests already hold. The
    /// <paramref name="self"/> quest's own departures are not counted as held; a need below 1 is an unread headcount.
    /// </summary>
    internal static (IReadOnlyList<PawnId> Squad, QuestSkipReason Reason) DepartureSquadFor(QuestId self, QuestFamily family, long need, RoundsFacts f, bool strongestFirst, bool allowNonfighters)
    {
        IReadOnlyList<PawnId> none = [];
        var covk = f.WorkRoster is WorkRosterRead.Read;
        var coverage = f.WorkRoster is WorkRosterRead.Read { Coverage: var coverageRead } ? coverageRead : default;
        if (f.QuestCalm is QuestCalm.Unsettled || f.QuestPawns is not QuestPawns.Seen { AtHome: var homeCount, Departures: var rows }
            || f.QuestRoster is not QuestRoster.Planned { HomeFloor: var floor, Spare: var spare, Work: var work } || !covk)
        {
            return (none, "capacity_unknown");
        }
        if (f.QuestCalm is QuestCalm.Busy)
        {
            return (none, "colony_busy");
        }
        if (need < 1)
        {
            return (none, "pawn_demand_unknown");
        }
        long home = homeCount;
        if (home - need < floor)
        {
            return (none, "home_capacity");
        }
        var owners = new Dictionary<WorkType, int>();
        foreach (var row in coverage)
        {
            owners[row.Work] = row.Owners;
        }
        var assignments = new Dictionary<PawnId, IReadOnlyList<WorkPriority>>();
        foreach (var row in work)
        {
            assignments[row.Pawn] = row.Priorities;
        }
        var committed = new HashSet<PawnId>();
        foreach (var open in f.QuestOffers)
        {
            if (open.State != QuestStatus.Ongoing || open.Id == self)
            {
                continue;
            }
            foreach (var pawn in open.DeparturePawnIds)
            {
                committed.Add(pawn);
            }
            foreach (var shuttle in open.Shuttles)
            {
                foreach (var pawn in shuttle.PendingPawnIds)
                {
                    committed.Add(pawn);
                }
                foreach (var pawn in shuttle.LoadedPawnIds)
                {
                    committed.Add(pawn);
                }
            }
        }
        var candidates = new List<QuestDeparturePawn>(rows);
        candidates.Sort((x, y) =>
        {
            if (family == QuestFamily.BanditCamp)
            {
                var a = x.DefensePoints;
                var b = y.DefensePoints;
                if (a != b)
                {
                    if (strongestFirst)
                    {
                        return a > b ? -1 : 1;
                    }
                    return a < b ? -1 : 1;
                }
            }
            return x.Id.CompareTo(y.Id);
        });
        var capacity = f.DefenseCapacity;
        var raid = f.RaidPoints;
        if (family == QuestFamily.BanditCamp && (!TradePolicyRules.Finite(capacity) || !TradePolicyRules.Finite(raid) || capacity < 0 || raid < 0))
        {
            return (none, "defense_unknown");
        }
        foreach (var pawn in candidates)
        {
            if (!committed.Contains(pawn.Id))
            {
                continue;
            }
            home--;
            if (assignments.TryGetValue(pawn.Id, out var held))
            {
                foreach (var p in held)
                {
                    if (p.Priority == 1 && !p.Disabled)
                    {
                        owners[p.Work] = owners.GetValueOrDefault(p.Work) - 1;
                    }
                }
            }
            if (family == QuestFamily.BanditCamp)
            {
                capacity -= pawn.DefensePoints;
            }
        }
        if (home - need < floor)
        {
            return (none, "home_capacity");
        }
        var selected = new List<PawnId>();
        foreach (var pawn in candidates)
        {
            if (committed.Contains(pawn.Id))
            {
                continue;
            }
            if (!spare.Contains(pawn.Id))
            {
                continue;
            }
            if (!pawn.HealthyAdult)
            {
                continue;
            }
            if (!assignments.TryGetValue(pawn.Id, out var priorities))
            {
                return (none, "work_unknown");
            }
            var essential = false;
            foreach (var p in priorities)
            {
                if (p.Priority == 1 && !p.Disabled)
                {
                    if (!owners.TryGetValue(p.Work, out var count))
                    {
                        return (none, "work_unknown");
                    }
                    if (count <= 1)
                    {
                        essential = true;
                    }
                }
            }
            if (essential)
            {
                continue;
            }
            var defense = pawn.DefensePoints;
            if (family == QuestFamily.BanditCamp)
            {
                if (!allowNonfighters && !pawn.CanFight)
                {
                    continue;
                }
                if (capacity - defense < Math.Max(raid, WealthBudgetRules.WealthBudgetMinRaidPoints))
                {
                    continue;
                }
            }
            selected.Add(pawn.Id);
            if (family == QuestFamily.BanditCamp)
            {
                capacity -= defense;
            }
            foreach (var p in priorities)
            {
                if (p.Priority == 1 && !p.Disabled)
                {
                    owners[p.Work] = owners.GetValueOrDefault(p.Work) - 1;
                }
            }
            if (selected.Count == need)
            {
                return (selected, "");
            }
        }
        if (family == QuestFamily.BanditCamp)
        {
            return (none, "home_defense");
        }
        return (none, "no_spare_pawn");
    }

    public static QuestSkipReason DepartureAdmission(QuestState offer, RoundsFacts f) => DepartureSquad(offer, f).Reason;

    public static bool DepartureDeficit(RoundsFacts f)
    {
        foreach (var offer in f.QuestOffers)
        {
            if (f.RootOf(offer) is QuestRoot.Classified { Profile: var profile } && offer.State == QuestStatus.Ongoing
                && (profile.Family == QuestFamily.PawnLend || profile.Family == QuestFamily.BanditCamp))
            {
                return true;
            }
        }
        return false;
    }

    public static QuestDepartureWork SelectQuestDeparture(RoundsFacts f)
    {
        foreach (var offer in f.QuestOffers.OrderBy(o => o.Id))
        {
            if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var p } || offer.State != QuestStatus.Ongoing
                || (p.Family != QuestFamily.PawnLend && p.Family != QuestFamily.BanditCamp))
            {
                continue;
            }
            if (QuestExpedition.QuestExpeditionSite(offer, f, out _))
            {
                continue;
            }
            var result = new QuestDepartureWork { Quest = offer.Id, Waiting = true };
            if (offer.DeparturePawnIds.Count > 0)
            {
                return result;
            }
            if (offer.Shuttles.Count == 0)
            {
                return result;
            }
            if (offer.Shuttles.Count != 1)
            {
                return result with { Reason = "shuttle_ambiguous" };
            }
            var shuttle = offer.Shuttles[0];
            if (shuttle.AllRequiredLoaded)
            {
                if (shuttle.ManualLaunchAvailable)
                {
                    var launch = new QuestShuttle(offer.Id, ShuttleLoading.LaunchOnly, false, [], true);
                    return result with { Shuttle = launch, Waiting = false };
                }
                return result;
            }
            // Loading is native evidence that boarding orders already exist. Reissuing another roster could
            // send replacements alongside the original squad.
            if (shuttle.Loading || shuttle.LoadedPawnIds.Count > 0 || shuttle.PendingPawnIds.Count > 0)
            {
                return result;
            }
            var (squad, reason) = DepartureSquad(offer, f);
            if (!reason.IsNone)
            {
                return result with { Reason = reason };
            }
            var explicitCrew = new QuestShuttle(offer.Id, ShuttleLoading.ExplicitPawns, false, squad.Select(p => (Domain.PawnId)p), false);
            return result with { Shuttle = explicitCrew, Waiting = false };
        }
        return new QuestDepartureWork();
    }
}
