using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static class QuestSelection
{
    /// <summary>Explains refusals with the same family decision used for admission.</summary>
    public static IReadOnlyList<QuestSkip> QuestSkips(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return [];
        }
        var output = new List<QuestSkip>();
        foreach (var offer in rows)
        {
            if (PopulationRules.ClaimAnswerable(offer, f.TitleClaimQuests) && QuestRewards.SelectQuestReward(offer, f).Reason.IsNone)
            {
                continue;
            }
            var (_, reason) = QuestDecision(offer, f);
            if (!reason.IsNone)
            {
                // The detail names why the root is unclassified, or the space layer of a ship-only quest.
                var root = f.RootOf(offer);
                var detail = reason == QuestSkipReason.ClassUnknown && root is QuestRoot.Unclassified unclassified ? unclassified.Error
                    : reason == QuestSkipReason.ShipOnly && root is QuestRoot.Classified classified ? classified.SpaceLayer
                    : "";
                output.Add(new QuestSkip(offer.Id, offer.ScriptDef, reason, detail));
            }
        }
        return output.OrderBy(s => s.Quest).ToList();
    }

    /// <summary>Gates the delayed quest raid using its generated budget.</summary>
    public static QuestSkipReason JoinerThreatReason(QuestState offer, RoundsFacts facts)
    {
        if (!facts.QuestColonyCalm.TryGetValue(out var calm))
        {
            return "capacity_unknown";
        }
        if (!calm)
        {
            return "colony_busy";
        }
        // A quest without threat parts threatens no raid.
        var threat = offer.Threat is Pb.QuestThreat.Points points ? points.Value : 0;
        if (!facts.DefenseCapacity.TryGetValue(out var defense) || !TradePolicyRules.Finite(defense) || defense < 0)
        {
            return "defense_unknown";
        }
        if (defense < threat)
        {
            return "defense_capacity";
        }
        return "";
    }

    /// <summary>
    /// Admits the short crash offer immediately when its observed window is still open and the colony can
    /// defend the generated raid.
    /// </summary>
    public static QuestSkipReason ShuttleRescueAdmission(QuestState offer, RoundsFacts facts)
    {
        if (facts.RootOf(offer) is not QuestRoot.Classified { Profile: var profile } || profile.Family != QuestFamily.ShuttleRescue)
        {
            return "";
        }
        if (offer.Expiry is Pb.QuestExpiry.ExpiresIn { Ticks: <= 0 })
        {
            return "expired";
        }
        return JoinerThreatReason(offer, facts);
    }

    /// <summary>Shared by selection, inspection and refusal telemetry.</summary>
    private static (bool Accept, QuestSkipReason Reason) QuestDecision(QuestState offer, RoundsFacts f)
    {
        if (offer.State != QuestStatus.NotYetAccepted)
        {
            return (false, "");
        }
        if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile } || profile.Family == QuestFamily.None)
        {
            return (false, QuestSkipReason.ClassUnknown);
        }
        if (profile.Disposition == QuestDisposition.Refuse)
        {
            return (false, profile.SkipReason);
        }
        if (profile.NeverAct || profile.Disposition is QuestDisposition.Observe or QuestDisposition.Follow)
        {
            return (false, "");
        }
        if (profile.Family == QuestFamily.Joiner)
        {
            return (false, JoinerThreatReason(offer, f));
        }
        if (profile.Family == QuestFamily.Bestowing)
        {
            if (offer.ChoicePartCount > 1)
            {
                return (false, "reward_choices");
            }
            return (false, "title_unclaimed");
        }
        if (!offer.CanAccept)
        {
            return (false, "cannot_accept");
        }
        var reward = QuestRewards.SelectQuestReward(offer, f);
        if (!reward.Reason.IsNone)
        {
            return (false, reward.Reason);
        }
        var expedition = QuestExpedition.QuestExpeditionSite(offer, f, out _);
        if (!expedition && (offer.ScriptDef == "SurveySite" || offer.ScriptDef == "OpportunitySite_PeaceTalks"))
        {
            return (false, "site_unknown");
        }
        if (profile.Family != QuestFamily.OdysseyGround && profile.Family != QuestFamily.Hack && profile.Family != QuestFamily.Relic && !expedition)
        {
            if (offer.Faction is not Pb.QuestFaction.Involved faction)
            {
                return (false, "no_faction");
            }
            if (faction.Hostile)
            {
                return (false, "hostile");
            }
            if (offer.Place is not Pb.QuestPlace.OnMap place || new MapId(place.MapId) != f.ColonyMap)
            {
                return (false, "off_map");
            }
        }
        if (offer.RequiresAccepter && !QuestCapacity.QuestAccepter(offer, f, f.QuestWorkers).Ok)
        {
            return (false, "needs_accepter");
        }
        if (profile.Family == QuestFamily.OdysseyGround && offer.Rewards.Count > 1)
        {
            return (false, "reward_choices");
        }
        QuestSkipReason r;
        if (!(r = QuestFeasibility(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = QuestHackPolicy.HackAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = QuestHackPolicy.RelicAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = ShuttleRescueAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = QuestHospitality.HospitalityAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = QuestMonumentPolicy.MonumentAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (expedition)
        {
            if (!(r = QuestExpedition.ExpeditionAdmission(offer, f)).IsNone)
            {
                return (false, r);
            }
        }
        else if (!(r = QuestDeparture.DepartureAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        // These families need a quest driver before their acceptance can be enabled.
        if (profile.Family is QuestFamily.DecreeProduce or QuestFamily.DecreeHarvest or QuestFamily.DecreeHunt)
        {
            return (false, "driver_unavailable");
        }
        return (true, "");
    }

    /// <summary>
    /// Accounts for open non-automatic quests before admitting new work. Unknown observations hold capacity;
    /// an autoAccept root reserves none.
    /// </summary>
    public static QuestSkipReason QuestFeasibility(QuestState offer, RoundsFacts f)
    {
        if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile })
        {
            return QuestSkipReason.ClassUnknown;
        }
        if (offer.Expiry is Pb.QuestExpiry.ExpiresIn { Ticks: 0 })
        {
            return "expired";
        }
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind == Pb.QuestObjectiveKind.AcceptRequirementUnmet)
            {
                return "accept_requirement";
            }
        }
        var deadline = QuestCapacityForecast.QuestDeadlineFeasibility(offer, f);
        if (!deadline.IsNone)
        {
            return deadline;
        }
        if (profile.Cost == QuestCost.Free)
        {
            return "";
        }
        if (profile.Cost == QuestCost.Trap)
        {
            return "automatic_trap";
        }
        var ck = f.QuestColonyCalm.TryGetValue(out var calm);
        var sk = f.QuestSparePawns.TryGetValue(out var spare);
        var hk = f.QuestColonistsAtHome.TryGetValue(out var home);
        var fk = f.QuestHomeFloor.TryGetValue(out var floor);
        if (!ck || !sk || !hk || !fk)
        {
            return "capacity_unknown";
        }
        if (!calm)
        {
            return "colony_busy";
        }
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return QuestSkipReason.ClassUnknown;
        }
        long used = 0, away = 0;
        foreach (var open in rows)
        {
            if (open.State != QuestStatus.Ongoing)
            {
                continue;
            }
            if (f.RootOf(open) is not QuestRoot.Classified { Profile: var p })
            {
                return "open_demands_unknown";
            }
            if (p.NeverAct || p.Disposition is QuestDisposition.Observe or QuestDisposition.Follow || p.Cost == QuestCost.Free)
            {
                continue;
            }
            var (openNeed, openReason) = QuestPawnDemand(open, f);
            if (!openReason.IsNone)
            {
                return openReason;
            }
            used += openNeed;
            if (p.Cost == QuestCost.Pawns)
            {
                away += openNeed;
            }
        }
        var (need, reason) = QuestPawnDemand(offer, f);
        if (!reason.IsNone)
        {
            return reason;
        }
        if (spare!.Count < used + need)
        {
            return "no_spare_pawn";
        }
        if (profile.Cost == QuestCost.Pawns)
        {
            away += need;
        }
        if (away > 0 && home - away < floor)
        {
            return "home_capacity";
        }
        if ((profile.Demands & QuestDemand.Food) != 0)
        {
            if (!f.FoodDays.TryGetValue(out var days))
            {
                return "food_unknown";
            }
            if (days < PopulationRules.JoinerFoodFloorDays(home))
            {
                return "food_capacity";
            }
        }
        return "";
    }

    internal static (long Need, QuestSkipReason Reason) QuestPawnDemand(QuestState offer, RoundsFacts f)
    {
        long need = 1;
        // Pickup passengers of a time-cost quest are guests, not colony staff leaving home. Only pawn-cost
        // work reserves the boarding roster.
        if (f.RootOf(offer) is QuestRoot.Classified { Profile: var profile } && profile.Cost != QuestCost.Pawns)
        {
            return (need, "");
        }
        foreach (var objective in offer.Objectives)
        {
            switch (objective.Kind)
            {
                case Pb.QuestObjectiveKind.LoadNamedPawns:
                    need = Math.Max(need, objective.PawnIds.Count);
                    break;
                case Pb.QuestObjectiveKind.LoadPawns:
                    if (objective.Goal is not Pb.QuestObjectiveGoal.Pawns { Count: var count })
                    {
                        return (0, "pawn_demand_unknown");
                    }
                    need = Math.Max(need, count);
                    break;
            }
        }
        return (need, "");
    }

    /// <summary>
    /// Why a quest a player goal names is not accepted now: the failing admission check, empty when it is admitted. A
    /// joiner offer is the joiners/offers step's and answers to its threat check; a quest no check refuses but no step
    /// answers reads <c>not_pursued</c>.
    /// </summary>
    public static QuestSkipReason NamedQuestReason(QuestState offer, RoundsFacts f)
    {
        var (accept, reason) = QuestDecision(offer, f);
        return accept || !reason.IsNone ? reason : "not_pursued";
    }

    /// <summary>
    /// Keeps a quest a player goal names first, then allowed title claims, then the lowest-ID affordable family offer. Joiner selection precedes
    /// this planner at runtime.
    /// </summary>
    public static JoinerChoice SelectQuestMethod(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            throw new RequiredFactException("quest offers");
        }
        // A quest a player goal names goes first, when the same checks every offer passes admit it (#3499).
        QuestState? best = null;
        foreach (var named in f.NamedQuests)
        {
            var offer = rows.FirstOrDefault(o => o.Id == named);
            if (offer is not null && QuestDecision(offer, f).Accept)
            {
                best = offer;
                break;
            }
        }
        if (best is null)
        {
            foreach (var offer in rows)
            {
                if (PopulationRules.ClaimAnswerable(offer, f.TitleClaimQuests) && QuestRewards.SelectQuestReward(offer, f).Reason.IsNone && (best is null || offer.Id.CompareTo(best.Id) < 0))
                {
                    best = offer;
                }
            }
        }
        if (best is null)
        {
            var bestValue = default(QuestRewardValue);
            foreach (var offer in rows)
            {
                var reward = QuestRewards.SelectQuestReward(offer, f);
                if (QuestDecision(offer, f).Accept
                    && (best is null || reward.Value.BetterThan(bestValue) || (reward.Value == bestValue && offer.Id.CompareTo(best.Id) < 0)))
                {
                    best = offer;
                    bestValue = reward.Value;
                }
            }
        }
        if (best is null)
        {
            return new JoinerChoice { Reason = JoinerPlanReason.QuestNoOffer };
        }
        var choice = new JoinerChoice { Quest = best.Id, RewardChoice = QuestRewards.SelectQuestReward(best, f).Choice };
        if (best.RequiresAccepter)
        {
            choice = choice with { Accepter = QuestCapacity.QuestAccepter(best, f, f.QuestWorkers).Pawn };
        }
        return choice;
    }

    /// <summary>
    /// Whether a quest is worth accepting, or an accepted one owes hosting, expedition or ideology work; an unread census
    /// raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static bool QuestDeficit(RoundsFacts f) =>
        SelectQuestMethod(f).Reason == JoinerPlanReason.None || QuestHospitality.HospitalityDeficit(f) || QuestExpedition.ExpeditionDeficit(f) || QuestHackPolicy.IdeologyWorkDeficit(f);
}
