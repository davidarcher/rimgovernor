package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
	"sort"
)

type QuestHackWork struct {
	Quest   domain.QuestID
	Hack    *domain.HackDesignation
	Waiting bool
	Reason  QuestSkipReason
}

// QuestHackComplete follows the native filter predicate: destruction satisfies
// only a filter which explicitly permits it.
func QuestHackComplete(offer JoinerOffer) bool {
	seen := false
	for _, objective := range offer.Objectives {
		for _, target := range objective.HackTargets {
			seen = true
			if satisfied, known := target.Satisfied.Value(); !known || !satisfied {
				return false
			}
		}
	}
	return seen
}

func IdeologyWorkDeficit(f RoundsFacts) bool {
	offers, _ := f.QuestOffers.Value()
	for _, offer := range offers {
		if offer.State != "Ongoing" {
			continue
		}
		profile, known := offer.Profile.Value()
		if !known {
			continue
		}
		if profile.Family == QuestFamilyHack || offer.ScriptDef == "Gravcore_MechanoidRelay" {
			if !QuestHackComplete(offer) {
				return true
			}
		}
		if profile.Family == QuestFamilyBeggars {
			seen := false
			for _, objective := range offer.Objectives {
				if request, known := objective.Gift.Value(); known {
					seen = true
					if remaining, rk := request.Remaining.Value(); !rk || remaining > 0 {
						return true
					}
				}
			}
			if !seen {
				return true
			}
		}
	}
	return false
}

func WorshippedTerminalAdmission(offer JoinerOffer) QuestSkipReason {
	if offer.ScriptDef != "Hack_WorshippedTerminal" {
		return ""
	}
	for _, objective := range offer.Objectives {
		if risk, known := objective.HackRisk.Value(); known {
			hostile, hk := risk.Hostile.Value()
			if !hk || risk.FactionID == "" {
				return "tribe_unknown"
			}
			if !hostile {
				return "worshipped_terminal_trap"
			}
			return ""
		}
	}
	return "tribe_unknown"
}

func HackAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	if reason := WorshippedTerminalAdmission(offer); reason != "" {
		return reason
	}
	if offer.ScriptDef == "Hack_Spacedrone" {
		return JoinerThreatReason(offer, f)
	}
	return ""
}

func SelectQuestHack(f RoundsFacts, currentMap domain.MapID) (QuestHackWork, error) {
	offers, known := f.QuestOffers.Value()
	if !known {
		return QuestHackWork{}, nil
	}
	offers = slices.Clone(offers)
	sort.Slice(offers, func(i, j int) bool { return offers[i].Quest < offers[j].Quest })
	for _, offer := range offers {
		if offer.State != "Ongoing" {
			continue
		}
		profile, known := offer.Profile.Value()
		if !known || profile.Family != QuestFamilyHack && offer.ScriptDef != "Gravcore_MechanoidRelay" {
			continue
		}
		result := QuestHackWork{Quest: offer.Quest, Waiting: true}
		if result.Reason = WorshippedTerminalAdmission(offer); result.Reason != "" {
			return result, nil
		}
		targets := []QuestHackTarget{}
		for _, objective := range offer.Objectives {
			targets = append(targets, objective.HackTargets...)
		}
		if len(targets) == 0 {
			result.Reason = "hack_target_unknown"
			return result, nil
		}
		sort.Slice(targets, func(i, j int) bool { return targets[i].ID < targets[j].ID })
		for _, target := range targets {
			satisfied, hk := target.Satisfied.Value()
			if !hk {
				result.Reason = "hack_state_unknown"
				return result, nil
			}
			if satisfied {
				continue
			}
			spawned, sk := target.Spawned.Value()
			if !sk || !spawned {
				result.Reason = "hack_target_unavailable"
				return result, nil
			}
			if target.Map != currentMap {
				result.Reason = "off_map"
				return result, nil
			}
			locked, lk := target.LockedOut.Value()
			auto, ak := target.Autohack.Value()
			if !lk || !ak {
				result.Reason = "hack_state_unknown"
				return result, nil
			}
			if locked || auto {
				return result, nil
			}
			if len(target.EligiblePawnIDs) == 0 {
				result.Reason = "hacker_unavailable"
				return result, nil
			}
			intent, err := domain.NewHackDesignation(target.ID, true)
			if err != nil {
				return result, err
			}
			result.Hack = &intent
			result.Waiting = false
			return result, nil
		}
	}
	return QuestHackWork{}, nil
}

func RelicAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	if offer.ScriptDef != "RelicHunt" {
		return ""
	}
	calm, ck := f.QuestColonyCalm.Value()
	spare, sk := f.QuestSparePawns.Value()
	workers, wk := f.QuestWorkers.Value()
	if !ck || !sk || !wk {
		return "capacity_unknown"
	}
	if !calm {
		return "colony_busy"
	}
	if len(spare) == 0 {
		return "no_spare_pawn"
	}
	for _, worker := range workers {
		available, ak := worker.Available.Value()
		work, pk := worker.Work.Value()
		if !ak || !pk || !available || !slices.Contains(spare, worker.ID) {
			continue
		}
		for _, assignment := range work {
			if assignment.Work == WorkResearch && !assignment.Disabled && assignment.Priority > 0 {
				return ""
			}
		}
	}
	return "hacker_unavailable"
}
