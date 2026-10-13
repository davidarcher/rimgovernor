using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record HospitalityWork
{
    public QuestId Quest { get; init; }

    public Assign? Assign { get; init; }

    public QuestShuttle? Shuttle { get; init; }

    public QuestSkipReason Reason { get; init; }

    public bool Waiting { get; init; }
}

public static partial class PolicyFunctions
{
    public static bool HospitalityFamily(QuestFamily f) =>
        f is QuestFamily.HospitalityAnimals or QuestFamily.HospitalityJoiners or QuestFamily.HospitalityPrisoners or QuestFamily.HospitalityRefugee or QuestFamily.Laborers;

    /// <summary>The offer's classification, or the zero profile (unknown family) when it is unread.</summary>
    private static QuestProfile ProfileOf(JoinerOffer offer) => offer.Profile.TryGetValue(out var profile) ? profile : new QuestProfile();

    /// <summary>
    /// Reuses sleeping upkeep and ordinary guest work, including crash rescue pickup. Native facts end
    /// hosting and prove transporter loading.
    /// </summary>
    public static HospitalityWork SelectHospitalityWork(RoundsFacts f, Tick tick)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return new HospitalityWork { Reason = "quest_census_unknown" };
        }
        var waiting = new HospitalityWork();
        foreach (var offer in rows.OrderBy(o => o.Quest))
        {
            var one = f with { QuestOffers = Known(EquatableArray.From(new[] { offer })) };
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
        if (!f.QuestOffers.TryGetValue(out var offers))
        {
            return new HospitalityWork { Reason = "quest_census_unknown" };
        }
        foreach (var offer in offers.OrderBy(o => o.Quest))
        {
            if (!offer.Profile.TryGetValue(out var profile)
                || (!HospitalityFamily(profile.Family) && profile.Family != QuestFamily.ShuttleRescue)
                || (profile.NeverAct && profile.Family != QuestFamily.Laborers)
                || (offer.State != "Ongoing" && offer.State != "NotYetAccepted"))
            {
                continue;
            }
            var work = new HospitalityWork { Quest = offer.Quest };
            if (profile.Family == QuestFamily.ShuttleRescue)
            {
                if (offer.State != "Ongoing")
                {
                    continue;
                }
                if (!f.QuestColonyCalm.TryGetValue(out var calm) || !calm)
                {
                    return work with { Waiting = true };
                }
            }
            if (offer.State == "NotYetAccepted" && offer.CanAccept)
            {
                continue;
            }
            if (profile.Family != QuestFamily.HospitalityAnimals && profile.Family != QuestFamily.HospitalityPrisoners && profile.Family != QuestFamily.ShuttleRescue)
            {
                var review = ReviewSleeping(f.Sleeping, new SleepingHistory(), tick);
                if (review.Targets.TryGetValue(out var targets))
                {
                    f.Sleeping.TryGetValue(out var sleeping);
                    var guests = new HashSet<PawnId>();
                    foreach (var guest in sleeping!.Guests)
                    {
                        guests.Add(guest.Id);
                    }
                    if (offer.State == "NotYetAccepted")
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
                        return work with { Assign = new Assign(target.Pawn, target.Available[0], previous) };
                    }
                }
            }
            if (offer.State != "Ongoing")
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
                var lk = shuttle.AllRequiredLoaded.TryGetValue(out var loaded);
                var mk = shuttle.ManualLaunchAvailable.TryGetValue(out var manual);
                if (!lk || !mk)
                {
                    return work with { Reason = "shuttle_unknown" };
                }
                if (loaded)
                {
                    if (manual)
                    {
                        return work with { Shuttle = new QuestShuttle(offer.Quest, ShuttleLoading.LaunchOnly, false, [], true) };
                    }
                    return work with { Waiting = true };
                }
                var ak = shuttle.AutoloadAvailable.TryGetValue(out var available);
                var ek = shuttle.Autoload.TryGetValue(out var enabled);
                if (!ak || !ek)
                {
                    return work with { Reason = "shuttle_unknown" };
                }
                if (!available)
                {
                    return work with { Reason = "autoload_unavailable" };
                }
                if (!enabled)
                {
                    return work with { Shuttle = new QuestShuttle(offer.Quest, ShuttleLoading.Autoload, true, [], false) };
                }
                return work with { Waiting = true };
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Kind != Pb.QuestObjectiveKind.HostLodgers)
                {
                    continue;
                }
                if (objective.MinimumMood.TryGetValue(out var threshold))
                {
                    foreach (var guest in objective.LodgerMoods)
                    {
                        if (guest.Mood.TryGetValue(out var mood) && mood < threshold)
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
    public static QuestSkipReason HospitalityAdmission(JoinerOffer offer, RoundsFacts f)
    {
        var profile = ProfileOf(offer);
        if (!HospitalityFamily(profile.Family) || profile.Family == QuestFamily.HospitalityAnimals)
        {
            return "";
        }
        long need = 0;
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind == Pb.QuestObjectiveKind.HostLodgers)
            {
                if (!objective.Count.TryGetValue(out var count))
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
            if (bed.Owners.Count == 0 && bed.Users.Count == 0 && Positive(bed.Humanlike) && Positive(bed.Roofed) && Negative(bed.Medical) && !bed.Slaves
                && ((profile.Family == QuestFamily.HospitalityPrisoners && Positive(bed.Prisoners))
                    || (profile.Family != QuestFamily.HospitalityPrisoners && Negative(bed.Prisoners))))
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
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var offer in rows)
        {
            var known = offer.Profile.TryGetValue(out var p);
            if (known && (HospitalityFamily(p!.Family) || p.Family == QuestFamily.ShuttleRescue) && (!p.NeverAct || p.Family == QuestFamily.Laborers) && offer.State == "Ongoing")
            {
                return true;
            }
            if (known && HospitalityFamily(p!.Family) && !p.NeverAct && offer.State == "NotYetAccepted" && !offer.CanAccept)
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

    public static HashSet<PawnId> ProtectedQuestGuestIds<TOffers>(Fact<TOffers> offers)
        where TOffers : IReadOnlyList<JoinerOffer>
    {
        var ids = new HashSet<PawnId>();
        if (!offers.TryGetValue(out var rows))
        {
            return ids;
        }
        foreach (var offer in rows)
        {
            if (!offer.Profile.TryGetValue(out var profile) || !profile.ProtectGuests || offer.State != "Ongoing")
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Gift.TryGetValue(out var gift))
                {
                    foreach (var id in gift.PawnIds)
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
