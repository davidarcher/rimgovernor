package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"slices"
	"sort"
)

type QuestDeparturePawn struct {
	ID                         PawnID
	HealthyAdult, CanFight     domain.Fact[bool]
	DefensePoints              domain.Fact[float64]
	CarryCapacity, CarriedMass domain.Fact[float64]
}

// DepartureSquad leaves every work type's last primary owner at home and
// prices a fighting squad against the colony's observed defense headroom.
func DepartureSquad(offer JoinerOffer, f RoundsFacts) ([]domain.PawnID, QuestSkipReason) {
	return departureSquad(offer, f, false)
}

func departureSquad(offer JoinerOffer, f RoundsFacts, strongestFirst bool) ([]domain.PawnID, QuestSkipReason) {
	profile, known := offer.Profile.Value()
	if !known {
		return nil, "class_unknown"
	}
	if profile.Family != QuestFamilyPawnLend && profile.Family != QuestFamilyBanditCamp {
		return nil, ""
	}
	calm, ck := f.QuestColonyCalm.Value()
	home, hk := f.QuestColonistsAtHome.Value()
	spare, sk := f.QuestSparePawns.Value()
	rows, rk := f.QuestDeparturePawns.Value()
	work, wk := f.QuestDepartureWork.Value()
	coverage, covk := f.WorkRoster.Value()
	if !ck || !hk || !sk || !rk || !wk || !covk {
		return nil, "capacity_unknown"
	}
	if !calm {
		return nil, "colony_busy"
	}
	need := int64(0)
	for _, objective := range offer.Objectives {
		if objective.Kind != o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_PAWNS {
			continue
		}
		count, known := objective.Count.Value()
		if !known {
			return nil, "pawn_demand_unknown"
		}
		need = max(need, count)
	}
	if need < 1 {
		return nil, "pawn_demand_unknown"
	}
	if int64(home)-need < QuestMinimumColonistsAtHome {
		return nil, "home_capacity"
	}
	owners := map[WorkType]int{}
	for _, row := range coverage {
		owners[row.Work] = row.Owners
	}
	assignments := map[PawnID][]WorkPriority{}
	for _, row := range work {
		assignments[row.Pawn] = row.Priorities
	}
	committed := map[domain.PawnID]bool{}
	if offers, known := f.QuestOffers.Value(); known {
		for _, open := range offers {
			if open.State != "Ongoing" || open.Quest == offer.Quest {
				continue
			}
			for _, pawn := range open.DeparturePawnIDs {
				committed[pawn] = true
			}
			for _, shuttle := range open.Shuttles {
				for _, pawn := range shuttle.PendingPawnIDs {
					committed[pawn] = true
				}
				for _, pawn := range shuttle.LoadedPawnIDs {
					committed[pawn] = true
				}
			}
		}
	}
	candidates := slices.Clone(rows)
	sort.Slice(candidates, func(i, j int) bool {
		if profile.Family == QuestFamilyBanditCamp {
			a, ak := candidates[i].DefensePoints.Value()
			b, bk := candidates[j].DefensePoints.Value()
			if ak != bk {
				return ak
			}
			if ak && bk && a != b {
				if strongestFirst {
					return a > b
				}
				return a < b
			}
		}
		return candidates[i].ID < candidates[j].ID
	})
	capacity, capk := f.DefenseCapacity.Value()
	raid, raidk := f.RaidPoints.Value()
	if profile.Family == QuestFamilyBanditCamp && (!capk || !raidk || !finite(capacity) || !finite(raid) || capacity < 0 || raid < 0) {
		return nil, "defense_unknown"
	}
	for _, pawn := range candidates {
		if !committed[domain.PawnID(pawn.ID)] {
			continue
		}
		home--
		for _, p := range assignments[pawn.ID] {
			if p.Priority == 1 && !p.Disabled {
				owners[p.Work]--
			}
		}
		if profile.Family == QuestFamilyBanditCamp {
			points, known := pawn.DefensePoints.Value()
			if !known || !finite(points) || points < 0 {
				return nil, "defense_unknown"
			}
			capacity -= points
		}
	}
	if int64(home)-need < QuestMinimumColonistsAtHome {
		return nil, "home_capacity"
	}
	selected := []domain.PawnID{}
	for _, pawn := range candidates {
		if committed[domain.PawnID(pawn.ID)] {
			continue
		}
		if !slices.Contains(spare, pawn.ID) {
			continue
		}
		healthy, hk := pawn.HealthyAdult.Value()
		if !hk {
			return nil, "health_unknown"
		}
		if !healthy {
			continue
		}
		priorities, known := assignments[pawn.ID]
		if !known {
			return nil, "work_unknown"
		}
		essential := false
		for _, p := range priorities {
			if p.Priority == 1 && !p.Disabled {
				count, known := owners[p.Work]
				if !known {
					return nil, "work_unknown"
				}
				if count <= 1 {
					essential = true
				}
			}
		}
		if essential {
			continue
		}
		defense, dk := pawn.DefensePoints.Value()
		if profile.Family == QuestFamilyBanditCamp {
			fight, fk := pawn.CanFight.Value()
			if !fk {
				return nil, "defense_unknown"
			}
			if !fight {
				continue
			}
			if !dk || !finite(defense) || defense < 0 {
				return nil, "defense_unknown"
			}
			if capacity-defense < max(raid, wealthBudgetMinRaidPoints) {
				continue
			}
		}
		selected = append(selected, domain.PawnID(pawn.ID))
		if profile.Family == QuestFamilyBanditCamp {
			capacity -= defense
		}
		for _, p := range priorities {
			if p.Priority == 1 && !p.Disabled {
				owners[p.Work]--
			}
		}
		if int64(len(selected)) == need {
			return selected, ""
		}
	}
	if profile.Family == QuestFamilyBanditCamp {
		return nil, "home_defense"
	}
	return nil, "no_spare_pawn"
}

func DepartureAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	_, reason := DepartureSquad(offer, f)
	return reason
}

type QuestDepartureWork struct {
	Quest   domain.QuestID
	Shuttle *domain.QuestShuttle
	Waiting bool
	Reason  QuestSkipReason
}

func DepartureDeficit(f RoundsFacts) bool {
	rows, known := f.QuestOffers.Value()
	if !known {
		return false
	}
	for _, offer := range rows {
		profile, known := offer.Profile.Value()
		if known && offer.State == "Ongoing" && (profile.Family == QuestFamilyPawnLend || profile.Family == QuestFamilyBanditCamp) {
			return true
		}
	}
	return false
}

func SelectQuestDeparture(f RoundsFacts) (QuestDepartureWork, error) {
	rows, known := f.QuestOffers.Value()
	if !known {
		return QuestDepartureWork{}, nil
	}
	rows = slices.Clone(rows)
	sort.Slice(rows, func(i, j int) bool { return rows[i].Quest < rows[j].Quest })
	for _, offer := range rows {
		p, k := offer.Profile.Value()
		if !k || offer.State != "Ongoing" || (p.Family != QuestFamilyPawnLend && p.Family != QuestFamilyBanditCamp) {
			continue
		}
		if _, expedition := questExpeditionSite(offer, f); expedition {
			continue
		}
		result := QuestDepartureWork{Quest: offer.Quest, Waiting: true}
		if len(offer.DeparturePawnIDs) > 0 {
			return result, nil
		}
		if len(offer.Shuttles) == 0 {
			return result, nil
		}
		if len(offer.Shuttles) != 1 {
			result.Reason = "shuttle_ambiguous"
			return result, nil
		}
		shuttle := offer.Shuttles[0]
		loaded, lk := shuttle.AllRequiredLoaded.Value()
		manual, mk := shuttle.ManualLaunchAvailable.Value()
		loading, bk := shuttle.Loading.Value()
		if !lk || !mk || !bk {
			result.Reason = "shuttle_unknown"
			return result, nil
		}
		if loaded {
			if manual {
				intent, err := domain.NewQuestShuttle(offer.Quest, domain.ShuttleLaunchOnly, false, nil, true)
				result.Shuttle = &intent
				result.Waiting = false
				return result, err
			}
			return result, nil
		}
		// Loading is native evidence that boarding orders already exist. Reissuing
		// another roster could send replacements alongside the original squad.
		if loading || len(shuttle.LoadedPawnIDs) > 0 || len(shuttle.PendingPawnIDs) > 0 {
			return result, nil
		}
		squad, reason := DepartureSquad(offer, f)
		if reason != "" {
			result.Reason = reason
			return result, nil
		}
		intent, err := domain.NewQuestShuttle(offer.Quest, domain.ShuttleExplicitPawns, false, squad, false)
		result.Shuttle = &intent
		result.Waiting = false
		return result, err
	}
	return QuestDepartureWork{}, nil
}
