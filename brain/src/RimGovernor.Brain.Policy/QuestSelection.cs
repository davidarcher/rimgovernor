using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
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
            if (ClaimAnswerable(offer, f.TitleClaimQuests) && SelectQuestReward(offer, f).Reason.IsNone)
            {
                continue;
            }
            var (_, reason) = QuestDecision(offer, f);
            if (!reason.IsNone)
            {
                var detail = "";
                if (reason == "class_unknown")
                {
                    detail = offer.ClassError;
                }
                if (reason == "ship_only" && offer.Class.TryGetValue(out var cls))
                {
                    detail = cls.SpaceLayer;
                }
                output.Add(new QuestSkip(offer.Quest, offer.ScriptDef, reason, detail));
            }
        }
        return output.OrderBy(s => s.Quest).ToList();
    }

    /// <summary>Gates the delayed quest raid using its generated budget.</summary>
    public static QuestSkipReason JoinerThreatReason(JoinerOffer offer, RoundsFacts facts)
    {
        if (!facts.QuestColonyCalm.TryGetValue(out var calm))
        {
            return "capacity_unknown";
        }
        if (!calm)
        {
            return "colony_busy";
        }
        if (!offer.ThreatPoints.TryGetValue(out var threat) || !Finite(threat) || threat < 0)
        {
            return "threat_unknown";
        }
        if (!facts.DefenseCapacity.TryGetValue(out var defense) || !Finite(defense) || defense < 0)
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
    public static QuestSkipReason ShuttleRescueAdmission(JoinerOffer offer, RoundsFacts facts)
    {
        if (!offer.Profile.TryGetValue(out var profile) || profile.Family != QuestFamily.ShuttleRescue)
        {
            return "";
        }
        if (!offer.ExpiresInTicks.TryGetValue(out var remaining))
        {
            return "expiry_unknown";
        }
        if (remaining <= 0)
        {
            return "expired";
        }
        return JoinerThreatReason(offer, facts);
    }

    /// <summary>Shared by selection, inspection and refusal telemetry.</summary>
    private static (bool Accept, QuestSkipReason Reason) QuestDecision(JoinerOffer offer, RoundsFacts f)
    {
        if (offer.State != "NotYetAccepted")
        {
            return (false, "");
        }
        if (!offer.Profile.TryGetValue(out var profile) || profile.Family == QuestFamily.None)
        {
            return (false, "class_unknown");
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
            if (offer.RewardChoiceParts.TryGetValue(out var parts) && parts > 1)
            {
                return (false, "reward_choices");
            }
            return (false, "title_unclaimed");
        }
        if (!offer.CanAccept)
        {
            return (false, "cannot_accept");
        }
        var reward = SelectQuestReward(offer, f);
        if (!reward.Reason.IsNone)
        {
            return (false, reward.Reason);
        }
        var expedition = QuestExpeditionSite(offer, f, out _);
        if (!expedition && (offer.ScriptDef == "SurveySite" || offer.ScriptDef == "OpportunitySite_PeaceTalks"))
        {
            return (false, "site_unknown");
        }
        if (profile.Family != QuestFamily.OdysseyGround && profile.Family != QuestFamily.Hack && profile.Family != QuestFamily.Relic && !expedition)
        {
            if (offer.FactionId.Length == 0)
            {
                return (false, "no_faction");
            }
            if (!offer.FactionHostile.TryGetValue(out var hostile))
            {
                return (false, "faction_unknown");
            }
            if (hostile)
            {
                return (false, "hostile");
            }
            if (!offer.OnMap)
            {
                return (false, "off_map");
            }
        }
        if (offer.RequiresAccepter && !QuestAccepter(offer, f, f.QuestWorkers).Ok)
        {
            return (false, "needs_accepter");
        }
        if (profile.Family == QuestFamily.OdysseyGround && offer.ChoiceCount > 1)
        {
            return (false, "reward_choices");
        }
        QuestSkipReason r;
        if (!(r = QuestFeasibility(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = HackAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = RelicAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = ShuttleRescueAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = HospitalityAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (!(r = MonumentAdmission(offer, f)).IsNone)
        {
            return (false, r);
        }
        if (expedition)
        {
            if (!(r = ExpeditionAdmission(offer, f)).IsNone)
            {
                return (false, r);
            }
        }
        else if (!(r = DepartureAdmission(offer, f)).IsNone)
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
    public static QuestSkipReason QuestFeasibility(JoinerOffer offer, RoundsFacts f)
    {
        if (!offer.Profile.TryGetValue(out var profile))
        {
            return "class_unknown";
        }
        if (offer.ExpiresInTicks.TryGetValue(out var expires) && expires == 0)
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
        var deadline = QuestDeadlineFeasibility(offer, f);
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
            return "class_unknown";
        }
        long used = 0, away = 0;
        foreach (var open in rows)
        {
            if (open.State != "Ongoing")
            {
                continue;
            }
            if (!open.Profile.TryGetValue(out var p))
            {
                return "open_demands_unknown";
            }
            if (p.NeverAct || p.Disposition is QuestDisposition.Observe or QuestDisposition.Follow || p.Cost == QuestCost.Free)
            {
                continue;
            }
            var (openNeed, openReason) = QuestPawnDemand(open);
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
        var (need, reason) = QuestPawnDemand(offer);
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
            if (days < JoinerFoodFloorDays(home))
            {
                return "food_capacity";
            }
        }
        return "";
    }

    private static (long Need, QuestSkipReason Reason) QuestPawnDemand(JoinerOffer offer)
    {
        long need = 1;
        // Pickup passengers of a time-cost quest are guests, not colony staff leaving home. Only pawn-cost
        // work reserves the boarding roster.
        if (offer.Profile.TryGetValue(out var profile) && profile.Cost != QuestCost.Pawns)
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
                    if (!objective.Count.TryGetValue(out var count))
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
    /// Keeps allowed title claims first, then the lowest-ID affordable family offer. Joiner selection precedes
    /// this planner at runtime.
    /// </summary>
    public static JoinerChoice SelectQuestMethod(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return new JoinerChoice { Reason = JoinerPlanReason.CensusUnknown };
        }
        JoinerOffer? best = null;
        foreach (var offer in rows)
        {
            if (ClaimAnswerable(offer, f.TitleClaimQuests) && SelectQuestReward(offer, f).Reason.IsNone && (best is null || offer.Quest.CompareTo(best.Quest) < 0))
            {
                best = offer;
            }
        }
        if (best is null)
        {
            var bestValue = default(QuestRewardValue);
            foreach (var offer in rows)
            {
                var reward = SelectQuestReward(offer, f);
                if (QuestDecision(offer, f).Accept
                    && (best is null || reward.Value.BetterThan(bestValue) || (reward.Value == bestValue && offer.Quest.CompareTo(best.Quest) < 0)))
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
        var choice = new JoinerChoice { Quest = best.Quest, RewardChoice = SelectQuestReward(best, f).Choice };
        if (best.RequiresAccepter)
        {
            choice = choice with { Accepter = QuestAccepter(best, f, f.QuestWorkers).Pawn };
        }
        return choice;
    }

    public static Fact<bool> QuestDeficit(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out _))
        {
            return Unknown<bool>();
        }
        return Known(SelectQuestMethod(f).Reason == JoinerPlanReason.None || HospitalityDeficit(f) || ExpeditionDeficit(f) || IdeologyWorkDeficit(f));
    }
}
