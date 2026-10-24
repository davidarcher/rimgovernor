using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record HospitalityWork
{
    public QuestId Quest { get; init; }

    public Assign? Assign { get; init; }

    public QuestShuttle? Shuttle { get; init; }

    public QuestSkipReason Reason { get; init; }

    public bool Waiting { get; init; }
}

public static class QuestHospitality
{
    public static bool HospitalityFamily(QuestFamily f) =>
        f is QuestFamily.HospitalityAnimals or QuestFamily.HospitalityJoiners or QuestFamily.HospitalityPrisoners or QuestFamily.HospitalityRefugee or QuestFamily.Laborers;


    /// <summary>
    /// Reuses sleeping upkeep and ordinary guest work, including crash rescue pickup. Native facts end
    /// hosting and prove transporter loading.
    /// </summary>
    public static HospitalityWork SelectHospitalityWork(RoundsFacts f, Tick tick)
    {
        var rows = f.QuestOffers;
        var waiting = new HospitalityWork();
        foreach (var offer in rows.OrderBy(o => o.Id))
        {
            var one = f with { QuestOffers = EquatableArray.From(new[] { offer }) };
            var work = HospitalityWorkOf(one, tick);
            if (work.Assign is not null || work.Shuttle is not null)
            {
                return work;
            }
            if (waiting.Quest.IsEmpty && !work.Quest.IsEmpty)
            {
                waiting = work;
            }
        }
        return waiting;
    }

    private static HospitalityWork HospitalityWorkOf(RoundsFacts f, Tick tick)
    {
        var offers = f.QuestOffers;
        foreach (var offer in offers.OrderBy(o => o.Id))
        {
            if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile }
                || (!HospitalityFamily(profile.Family) && profile.Family != QuestFamily.ShuttleRescue)
                || (profile.NeverAct && profile.Family != QuestFamily.Laborers)
                || (offer.State != QuestStatus.Ongoing && offer.State != QuestStatus.NotYetAccepted))
            {
                continue;
            }
            var work = new HospitalityWork { Quest = offer.Id };
            if (profile.Family == QuestFamily.ShuttleRescue)
            {
                if (offer.State != QuestStatus.Ongoing)
                {
                    continue;
                }
                if (f.QuestCalm is not QuestCalm.Calm)
                {
                    return work with { Waiting = true };
                }
            }
            if (offer.State == QuestStatus.NotYetAccepted && offer.CanAccept)
            {
                continue;
            }
            if (profile.Family != QuestFamily.HospitalityAnimals && profile.Family != QuestFamily.HospitalityPrisoners && profile.Family != QuestFamily.ShuttleRescue)
            {
                var review = SleepingRules.ReviewSleeping(f.Sleeping, new SleepingHistory(), tick);
                if (review.Targets.TryGetValue(out var targets))
                {
                    f.Sleeping.TryGetValue(out var sleeping);
                    var guests = new HashSet<PawnId>();
                    foreach (var guest in sleeping!.Guests)
                    {
                        guests.Add(guest.Id);
                    }
                    if (offer.State == QuestStatus.NotYetAccepted)
                    {
                        foreach (var person in sleeping.People)
                        {
                            if (person.Title is not null)
                            {
                                guests.Add(person.Id);
                            }
                        }
                    }
                    foreach (var target in targets)
                    {
                        if (!guests.Contains(target.Pawn) || target.Available.Count == 0)
                        {
                            continue;
                        }
                        var previous = PreviousAssignment.ClearPrevious();
                        if (target.PreviousBed.Length != 0)
                        {
                            previous = PreviousAssignment.KnownPrevious(target.PreviousBed);
                        }
                        return work with { Assign = new Assign((Domain.PawnId)target.Pawn, target.Available[0], previous) };
                    }
                }
            }
            if (offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            if (offer.Shuttles.Count > 1)
            {
                return work with { Reason = "shuttle_ambiguous" };
            }
            if (offer.Shuttles.Count == 1)
            {
                var shuttle = offer.Shuttles[0];
                if (shuttle.AllRequiredLoaded)
                {
                    if (shuttle.ManualLaunchAvailable)
                    {
                        return work with { Shuttle = new QuestShuttle(offer.Id, ShuttleLoading.LaunchOnly, false, [], true) };
                    }
                    return work with { Waiting = true };
                }
                if (!shuttle.AutoloadAvailable)
                {
                    return work with { Reason = "autoload_unavailable" };
                }
                if (!shuttle.Autoload)
                {
                    return work with { Shuttle = new QuestShuttle(offer.Id, ShuttleLoading.Autoload, true, [], false) };
                }
                return work with { Waiting = true };
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Kind != Pb.QuestObjectiveKind.HostLodgers)
                {
                    continue;
                }
                if (objective.MinimumMood is Pb.QuestMoodFloor.Floor { Mood: var threshold })
                {
                    foreach (var guest in objective.LodgerMoods)
                    {
                        if (guest.Mood < threshold)
                        {
                            return work with { Reason = "lodger_mood_upkeep", Waiting = true };
                        }
                    }
                }
            }
            return work with { Waiting = true };
        }
        return new HospitalityWork();
    }

    /// <summary>Requires vacant appropriate beds before adding guests.</summary>
    public static QuestSkipReason HospitalityAdmission(QuestState offer, RoundsFacts f)
    {
        if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile } || !HospitalityFamily(profile.Family) || profile.Family == QuestFamily.HospitalityAnimals)
        {
            return "";
        }
        long need = 0;
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind == Pb.QuestObjectiveKind.HostLodgers)
            {
                if (objective.Goal is not Pb.QuestObjectiveGoal.Pawns { Count: var count })
                {
                    return "guest_count_unknown";
                }
                need = Math.Max(need, count);
            }
        }
        if (need <= 0)
        {
            if (profile.ProtectGuests)
            {
                return "";
            }
            return "guest_count_unknown";
        }
        if (!f.Sleeping.TryGetValue(out var sleeping))
        {
            return "guest_beds_unknown";
        }
        long spare = 0;
        foreach (var bed in sleeping.Beds)
        {
            if (bed.Owners.Count == 0 && bed.Users.Count == 0 && bed.Humanlike && bed.Roofed && !bed.Medical && !bed.Slaves
                && ((profile.Family == QuestFamily.HospitalityPrisoners && bed.Prisoners)
                    || (profile.Family != QuestFamily.HospitalityPrisoners && !bed.Prisoners)))
            {
                spare++;
            }
        }
        if (spare < need)
        {
            return "guest_bed_capacity";
        }
        return "";
    }

    public static bool HospitalityDeficit(RoundsFacts f)
    {
        var rows = f.QuestOffers;
        foreach (var offer in rows)
        {
            if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var p })
            {
                continue;
            }
            if ((HospitalityFamily(p.Family) || p.Family == QuestFamily.ShuttleRescue) && (!p.NeverAct || p.Family == QuestFamily.Laborers) && offer.State == QuestStatus.Ongoing)
            {
                return true;
            }
            if (HospitalityFamily(p.Family) && !p.NeverAct && offer.State == QuestStatus.NotYetAccepted && !offer.CanAccept)
            {
                foreach (var objective in offer.Objectives)
                {
                    if (objective.Kind == Pb.QuestObjectiveKind.AcceptRequirementUnmet)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public static HashSet<PawnId> ProtectedQuestGuestIds(RoundsFacts f)
    {
        var ids = new HashSet<PawnId>();
        var rows = f.QuestOffers;
        foreach (var offer in rows)
        {
            if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile } || !profile.ProtectGuests || offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.GiftRequest is { } gift)
                {
                    foreach (var id in gift.Pawns)
                    {
                        ids.Add(id);
                    }
                }
                if (objective.Kind == Pb.QuestObjectiveKind.HostLodgers)
                {
                    foreach (var id in objective.PawnIds)
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        return ids;
    }
}
