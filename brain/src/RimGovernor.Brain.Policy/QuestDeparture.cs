using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A colonist on the current map as the departure planners read it: the native quest worker row and its defense points.</summary>
public sealed record QuestDeparturePawn
{
    public required PawnId Id { get; init; }

    public required bool HealthyAdult { get; init; }

    public required bool CanFight { get; init; }

    /// <summary>Melee power plus ranged DPS in raid points; 0 for a pawn that cannot fight. Unknown while the defender roster (#3789) is.</summary>
    public Fact<double> DefensePoints { get; init; }

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
    public static (IReadOnlyList<PawnId> Squad, QuestSkipReason Reason) DepartureSquad(JoinerOffer offer, RoundsFacts f) =>
        DepartureSquadOf(offer, f, false);

    internal static (IReadOnlyList<PawnId> Squad, QuestSkipReason Reason) DepartureSquadOf(JoinerOffer offer, RoundsFacts f, bool strongestFirst) =>
        DepartureSquadWithCrew(offer, f, strongestFirst, false);

    internal static (IReadOnlyList<PawnId> Squad, QuestSkipReason Reason) DepartureSquadWithCrew(JoinerOffer offer, RoundsFacts f, bool strongestFirst, bool allowNonfighters)
    {
        IReadOnlyList<PawnId> none = [];
        if (!offer.Profile.TryGetValue(out var profile))
        {
            return (none, "class_unknown");
        }
        if (profile.Family != QuestFamily.PawnLend && profile.Family != QuestFamily.BanditCamp)
        {
            return (none, "");
        }
        var ck = f.QuestColonyCalm.TryGetValue(out var calm);
        var hk = f.QuestColonistsAtHome.TryGetValue(out var homeCount);
        var fk = f.QuestHomeFloor.TryGetValue(out var floor);
        var sk = f.QuestSparePawns.TryGetValue(out var spare);
        var rk = f.QuestDeparturePawns.TryGetValue(out var rows);
        var wk = f.QuestDepartureWork.TryGetValue(out var work);
        var covk = f.WorkRoster.TryGetValue(out var coverage);
        if (!ck || !hk || !fk || !sk || !rk || !wk || !covk)
        {
            return (none, "capacity_unknown");
        }
        if (!calm)
        {
            return (none, "colony_busy");
        }
        long need = 0;
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind != Pb.QuestObjectiveKind.LoadPawns)
            {
                continue;
            }
            if (!objective.Count.TryGetValue(out var count))
            {
                return (none, "pawn_demand_unknown");
            }
            need = Math.Max(need, count);
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
        foreach (var row in coverage!)
        {
            owners[row.Work] = row.Owners;
        }
        var assignments = new Dictionary<PawnId, IReadOnlyList<WorkPriority>>();
        foreach (var row in work!)
        {
            assignments[row.Pawn] = row.Priorities;
        }
        var committed = new HashSet<PawnId>();
        if (f.QuestOffers.TryGetValue(out var offers))
        {
            foreach (var open in offers)
            {
                if (open.State != "Ongoing" || open.Quest == offer.Quest)
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
        }
        var candidates = new List<QuestDeparturePawn>(rows!);
        candidates.Sort((x, y) =>
        {
            if (profile.Family == QuestFamily.BanditCamp)
            {
                var ak = x.DefensePoints.TryGetValue(out var a);
                var bk = y.DefensePoints.TryGetValue(out var b);
                if (ak != bk)
                {
                    return ak ? -1 : 1;
                }
                if (ak && bk && a != b)
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
        var capk = f.DefenseCapacity.TryGetValue(out var capacity);
        var raidk = f.RaidPoints.TryGetValue(out var raid);
        if (profile.Family == QuestFamily.BanditCamp && (!capk || !raidk || !TradePolicyRules.Finite(capacity) || !TradePolicyRules.Finite(raid) || capacity < 0 || raid < 0))
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
            if (profile.Family == QuestFamily.BanditCamp)
            {
                if (!pawn.DefensePoints.TryGetValue(out var points) || !TradePolicyRules.Finite(points) || points < 0)
                {
                    return (none, "defense_unknown");
                }
                capacity -= points;
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
            if (!spare!.Contains(pawn.Id))
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
            var dk = pawn.DefensePoints.TryGetValue(out var defense);
            if (profile.Family == QuestFamily.BanditCamp)
            {
                if (!allowNonfighters && !pawn.CanFight)
                {
                    continue;
                }
                if (!dk || !TradePolicyRules.Finite(defense) || defense < 0)
                {
                    return (none, "defense_unknown");
                }
                if (capacity - defense < Math.Max(raid, WealthBudgetRules.WealthBudgetMinRaidPoints))
                {
                    continue;
                }
            }
            selected.Add(pawn.Id);
            if (profile.Family == QuestFamily.BanditCamp)
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
        if (profile.Family == QuestFamily.BanditCamp)
        {
            return (none, "home_defense");
        }
        return (none, "no_spare_pawn");
    }

    public static QuestSkipReason DepartureAdmission(JoinerOffer offer, RoundsFacts f) => DepartureSquad(offer, f).Reason;

    public static bool DepartureDeficit(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var offer in rows)
        {
            if (offer.Profile.TryGetValue(out var profile) && offer.State == "Ongoing"
                && (profile.Family == QuestFamily.PawnLend || profile.Family == QuestFamily.BanditCamp))
            {
                return true;
            }
        }
        return false;
    }

    public static QuestDepartureWork SelectQuestDeparture(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return new QuestDepartureWork();
        }
        foreach (var offer in rows.OrderBy(o => o.Quest))
        {
            if (!offer.Profile.TryGetValue(out var p) || offer.State != "Ongoing"
                || (p.Family != QuestFamily.PawnLend && p.Family != QuestFamily.BanditCamp))
            {
                continue;
            }
            if (QuestExpedition.QuestExpeditionSite(offer, f, out _))
            {
                continue;
            }
            var result = new QuestDepartureWork { Quest = offer.Quest, Waiting = true };
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
                    var launch = new QuestShuttle(offer.Quest, ShuttleLoading.LaunchOnly, false, [], true);
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
            var explicitCrew = new QuestShuttle(offer.Quest, ShuttleLoading.ExplicitPawns, false, squad.Select(p => (Domain.PawnId)p), false);
            return result with { Shuttle = explicitCrew, Waiting = false };
        }
        return new QuestDepartureWork();
    }
}
