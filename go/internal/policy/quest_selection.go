package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

const QuestNoOffer JoinerPlanReason = "no_quest_offer"

// questDecision is shared by selection, inspection and refusal telemetry.
func questDecision(offer JoinerOffer, f RoundsFacts) (bool, QuestSkipReason) {
	if offer.State != "NotYetAccepted" {
		return false, ""
	}
	profile, known := offer.Profile.Value()
	if !known || profile.Family == QuestFamilyUnknown {
		return false, "class_unknown"
	}
	if profile.Disposition == QuestRefuse {
		return false, QuestSkipReason(profile.SkipReason)
	}
	if profile.NeverAct || profile.Disposition == QuestObserve || profile.Disposition == QuestFollow {
		return false, ""
	}
	if profile.Family == QuestFamilyJoiner {
		return false, JoinerThreatReason(offer, f)
	}
	if profile.Family == QuestFamilyBestowing {
		if parts, known := offer.RewardChoiceParts.Value(); known && parts > 1 {
			return false, "reward_choices"
		}
		return false, "title_unclaimed"
	}
	if !offer.CanAccept {
		return false, "cannot_accept"
	}
	if reward := SelectQuestReward(offer, f); reward.Reason != "" {
		return false, reward.Reason
	}
	_, expedition := questExpeditionSite(offer, f)
	if !expedition && (offer.ScriptDef == "SurveySite" || offer.ScriptDef == "OpportunitySite_PeaceTalks") {
		return false, "site_unknown"
	}
	if profile.Family != QuestFamilyOdysseyGround && profile.Family != QuestFamilyHack && profile.Family != QuestFamilyRelic && !expedition {
		if offer.FactionID == "" {
			return false, "no_faction"
		}
		if hostile, known := offer.FactionHostile.Value(); !known {
			return false, "faction_unknown"
		} else if hostile {
			return false, "hostile"
		}
		if !offer.OnMap {
			return false, "off_map"
		}
	}
	if offer.RequiresAccepter {
		if _, ok := QuestAccepter(offer, f, f.QuestWorkers); !ok {
			return false, "needs_accepter"
		}
	}
	if profile.Family == QuestFamilyOdysseyGround && offer.ChoiceCount > 1 {
		return false, "reward_choices"
	}
	if reason := QuestFeasibility(offer, f); reason != "" {
		return false, reason
	}
	if reason := HackAdmission(offer, f); reason != "" {
		return false, reason
	}
	if reason := RelicAdmission(offer, f); reason != "" {
		return false, reason
	}
	if reason := ShuttleRescueAdmission(offer, f); reason != "" {
		return false, reason
	}
	if reason := HospitalityAdmission(offer, f); reason != "" {
		return false, reason
	}
	if reason := MonumentAdmission(offer, f); reason != "" {
		return false, reason
	}
	if expedition {
		if reason := ExpeditionAdmission(offer, f); reason != "" {
			return false, reason
		}
	} else {
		if reason := DepartureAdmission(offer, f); reason != "" {
			return false, reason
		}
	}
	// These families need a quest driver before their acceptance can be enabled.
	switch profile.Family {
	case QuestFamilyDecreeProduce, QuestFamilyDecreeHarvest, QuestFamilyDecreeHunt:
		return false, "driver_unavailable"
	}
	return true, ""
}

// QuestFeasibility accounts for open non-automatic quests before admitting new
// work. Unknown observations hold capacity; an autoAccept root reserves none.
func QuestFeasibility(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	profile, known := offer.Profile.Value()
	if !known {
		return "class_unknown"
	}
	if remaining, known := offer.ExpiresInTicks.Value(); known && remaining == 0 {
		return "expired"
	}
	for _, objective := range offer.Objectives {
		if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_ACCEPT_REQUIREMENT_UNMET {
			return "accept_requirement"
		}
	}
	if reason := QuestDeadlineFeasibility(offer, f); reason != "" {
		return reason
	}
	if profile.Cost == QuestCostFree {
		return ""
	}
	if profile.Cost == QuestCostTrap {
		return "automatic_trap"
	}
	calm, ck := f.QuestColonyCalm.Value()
	spare, sk := f.QuestSparePawns.Value()
	home, hk := f.QuestColonistsAtHome.Value()
	floor, fk := f.QuestHomeFloor.Value()
	if !ck || !sk || !hk || !fk {
		return "capacity_unknown"
	}
	if !calm {
		return "colony_busy"
	}
	rows, known := f.QuestOffers.Value()
	if !known {
		return "class_unknown"
	}
	used, away := int64(0), int64(0)
	for _, open := range rows {
		if open.State != "Ongoing" {
			continue
		}
		p, known := open.Profile.Value()
		if !known {
			return "open_demands_unknown"
		}
		if p.NeverAct || p.Disposition == QuestObserve || p.Disposition == QuestFollow || p.Cost == QuestCostFree {
			continue
		}
		need, reason := questPawnDemand(open)
		if reason != "" {
			return reason
		}
		used += need
		if p.Cost == QuestCostPawns {
			away += need
		}
	}
	need, reason := questPawnDemand(offer)
	if reason != "" {
		return reason
	}
	if int64(len(spare)) < used+need {
		return "no_spare_pawn"
	}
	if profile.Cost == QuestCostPawns {
		away += need
	}
	if away > 0 && int64(home)-away < int64(floor) {
		return "home_capacity"
	}
	if profile.Demands&QuestDemandFood != 0 {
		if days, known := f.FoodDays.Value(); !known {
			return "food_unknown"
		} else if days < JoinerFoodFloorDays(int64(home)) {
			return "food_capacity"
		}
	}
	return ""
}

func questPawnDemand(offer JoinerOffer) (int64, QuestSkipReason) {
	need := int64(1)
	// Pickup passengers of a time-cost quest are guests, not colony staff
	// leaving home. Only pawn-cost work reserves the boarding roster.
	if profile, known := offer.Profile.Value(); known && profile.Cost != QuestCostPawns {
		return need, ""
	}
	for _, objective := range offer.Objectives {
		switch objective.Kind {
		case o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_NAMED_PAWNS:
			need = max(need, int64(len(objective.PawnIDs)))
		case o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_PAWNS:
			count, known := objective.Count.Value()
			if !known {
				return 0, "pawn_demand_unknown"
			}
			need = max(need, count)
		}
	}
	return need, ""
}

// SelectQuestMethod keeps allowed title claims first, then the lowest-ID
// affordable family offer. Joiner selection precedes this planner at runtime.
func SelectQuestMethod(f RoundsFacts) JoinerChoice {
	rows, known := f.QuestOffers.Value()
	if !known {
		return JoinerChoice{Reason: JoinerCensusUnknown}
	}
	var best *JoinerOffer
	for i, offer := range rows {
		if claimAnswerable(offer, f.TitleClaimQuests) && SelectQuestReward(offer, f).Reason == "" && (best == nil || offer.Quest < best.Quest) {
			best = &rows[i]
		}
	}
	if best == nil {
		var bestValue QuestRewardValue
		for i, offer := range rows {
			reward := SelectQuestReward(offer, f)
			if accept, _ := questDecision(offer, f); accept && (best == nil || reward.Value.BetterThan(bestValue) || reward.Value == bestValue && offer.Quest < best.Quest) {
				best = &rows[i]
				bestValue = reward.Value
			}
		}
	}
	if best == nil {
		return JoinerChoice{Reason: QuestNoOffer}
	}
	choice := JoinerChoice{Quest: best.Quest, RewardChoice: -1}
	choice.RewardChoice = SelectQuestReward(*best, f).Choice
	if best.RequiresAccepter {
		choice.Accepter, _ = QuestAccepter(*best, f, f.QuestWorkers)
	}
	return choice
}

func QuestDeficit(f RoundsFacts) domain.Fact[bool] {
	if _, known := f.QuestOffers.Value(); !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(SelectQuestMethod(f).Reason == "" || HospitalityDeficit(f) || ExpeditionDeficit(f) || IdeologyWorkDeficit(f))
}
