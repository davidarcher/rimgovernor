package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type SurveyPlan struct {
	Quest                   domain.QuestID
	Site                    string
	Departure               *domain.CaravanDeparture
	Waiting, Return, Failed bool
	Reason                  QuestSkipReason
}

func surveyScanner(offer JoinerOffer, site string) (QuestSurveyScanner, bool) {
	var selected QuestSurveyScanner
	found := false
	for _, objective := range offer.Objectives {
		if scanner, known := objective.SurveyScanner.Value(); known && scanner.SiteID == site {
			if found {
				return QuestSurveyScanner{}, false
			}
			selected = scanner
			found = true
		}
	}
	return selected, found
}

func surveyBudget(offer JoinerOffer, site ExpeditionSite, f RoundsFacts) (ExpeditionSite, QuestSkipReason) {
	scanner, known := surveyScanner(offer, site.ID)
	if !known {
		return site, "survey_unknown"
	}
	if alive, known := scanner.Alive.Value(); known && !alive {
		return site, "scanner_destroyed"
	}
	duration, known := scanner.DurationTicks.Value()
	if !known || duration <= 0 {
		return site, "survey_duration_unknown"
	}
	if end, known := scanner.EndTick.Value(); known {
		now, nk := f.QuestObservedTick.Value()
		if !nk {
			return site, "survey_duration_unknown"
		}
		duration = max(int64(0), end-int64(now))
	}
	site.HoldTicks = domain.Known(duration)
	forced, fk := offer.ThreatPoints.Value()
	initial, ik := site.ThreatPoints.Value()
	if !fk || !ik || !finite(forced) || !finite(initial) || forced < 0 || initial < 0 {
		return site, "threat_unknown"
	}
	site.ThreatPoints = domain.Known(max(initial, forced))
	return site, ""
}

func SurveyAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	site, found := questExpeditionSite(offer, f)
	if !found {
		return "site_unknown"
	}
	return PlanSurvey(offer, site, f, f.AnimalUpkeep.Food, DefaultRoundsPolicy()).Reason
}

// The original native duration prices the first crew's entire hold. A distinct
// relief crew must remain affordable after that departure; none is sent merely
// because time passed, and the scanner performs its ordinary passive work.
func PlanSurvey(offer JoinerOffer, site ExpeditionSite, f RoundsFacts, food domain.Fact[FoodSupply], p RoundsPolicy) ExpeditionPlan {
	budget, reason := surveyBudget(offer, site, f)
	if reason != "" {
		return ExpeditionPlan{Quest: offer.Quest, Site: site.ID, Reason: reason}
	}
	plan := PlanExpedition(offer, budget, f, food, p)
	if plan.Reason != "" || plan.Departure == nil {
		return plan
	}
	reserve, known := surveyReliefFacts(f, plan.Departure.Crew())
	if !known {
		plan.Departure = nil
		plan.Reason = "relief_capacity_unknown"
		return plan
	}
	reserveFood := surveyReliefFood(food, *plan.Departure)
	if relief := PlanExpedition(offer, budget, reserve, reserveFood, p); relief.Reason != "" || relief.Departure == nil {
		plan.Departure = nil
		plan.Reason = "relief_capacity"
	}
	return plan
}

func surveyReliefFood(food domain.Fact[FoodSupply], primary domain.CaravanDeparture) domain.Fact[FoodSupply] {
	supply, known := food.Value()
	if !known {
		return food
	}
	supply.Consumers = slices.DeleteFunc(slices.Clone(supply.Consumers), func(row FoodConsumer) bool { return slices.Contains(primary.Crew(), domain.PawnID(row.ID)) })
	supply.Stocks = slices.Clone(supply.Stocks)
	packed := map[string]int64{}
	for _, item := range primary.Cargo() {
		packed[item.Definition] = int64(item.Count)
	}
	for i := range supply.Stocks {
		row := &supply.Stocks[i]
		row.Eaters = slices.DeleteFunc(slices.Clone(row.Eaters), func(id PawnID) bool { return slices.Contains(primary.Crew(), domain.PawnID(id)) })
		remaining := packed[string(row.DefName)]
		if remaining == 0 {
			continue
		}
		count, ck := row.Count.Value()
		nutrition, nk := row.Nutrition.Value()
		if !ck || !nk || count <= 0 {
			continue
		}
		take := min(count, remaining)
		row.Count = domain.Known(count - take)
		row.Nutrition = domain.Known(nutrition * (float64(count-take) / float64(count)))
		packed[string(row.DefName)] -= take
	}
	return domain.Known(supply)
}

// surveyReliefFacts accounts for the proposed primary crew in the same home
// staffing facts used by ordinary departure, without persisting a reservation.
func surveyReliefFacts(f RoundsFacts, crew []domain.PawnID) (RoundsFacts, bool) {
	home, hk := f.QuestColonistsAtHome.Value()
	capacity, ck := f.DefenseCapacity.Value()
	rows, rk := f.QuestDeparturePawns.Value()
	work, wk := f.QuestDepartureWork.Value()
	coverage, covk := f.WorkRoster.Value()
	spare, sk := f.QuestSparePawns.Value()
	if !hk || !ck || !rk || !wk || !covk || !sk {
		return f, false
	}
	coverage = slices.Clone(coverage)
	for _, id := range crew {
		found := false
		for _, row := range rows {
			if domain.PawnID(row.ID) == id {
				strength, known := row.DefensePoints.Value()
				if !known {
					return f, false
				}
				capacity -= strength
				found = true
			}
		}
		if !found {
			return f, false
		}
		for _, row := range work {
			if domain.PawnID(row.Pawn) == id {
				for _, priority := range row.Priorities {
					if priority.Priority == 1 && !priority.Disabled {
						for i := range coverage {
							if coverage[i].Work == priority.Work {
								coverage[i].Owners--
							}
						}
					}
				}
			}
		}
	}
	f.QuestColonistsAtHome = domain.Known(home - len(crew))
	f.DefenseCapacity = domain.Known(capacity)
	f.WorkRoster = domain.Known(coverage)
	f.QuestSparePawns = domain.Known(slices.DeleteFunc(slices.Clone(spare), func(id PawnID) bool { return slices.Contains(crew, domain.PawnID(id)) }))
	f.QuestDeparturePawns = domain.Known(slices.DeleteFunc(slices.Clone(rows), func(row QuestDeparturePawn) bool { return slices.Contains(crew, domain.PawnID(row.ID)) }))
	return f, true
}

func SurveyWork(offer JoinerOffer, site WorldSite, f RoundsFacts, food domain.Fact[FoodSupply], p RoundsPolicy) SurveyPlan {
	result := SurveyPlan{Quest: offer.Quest, Site: site.ID, Waiting: true}
	if offer.State == "EndedFailed" {
		result.Waiting, result.Return, result.Failed, result.Reason = false, true, true, "quest_failed"
		return result
	}
	scanner, known := surveyScanner(offer, site.ID)
	if !known {
		result.Reason = "survey_unknown"
		return result
	}
	if alive, known := scanner.Alive.Value(); known && !alive {
		result.Waiting = false
		result.Return = true
		result.Failed = true
		result.Reason = "scanner_destroyed"
		return result
	}
	if complete, known := scanner.Complete.Value(); known && complete {
		result.Waiting = false
		result.Return = true
		return result
	}
	end, ek := scanner.EndTick.Value()
	now, nk := f.QuestObservedTick.Value()
	if !ek || !nk {
		result.Reason = "survey_duration_unknown"
		return result
	}
	// Elapsed time alone never proves native completion.
	if end <= int64(now) {
		result.Reason = "survey_completion_pending"
		return result
	}
	extraction, xk := site.Extraction.Value()
	runway, rk := extraction.InventoryFoodDays.Value()
	if !xk || !rk || !finite(runway) || runway < 0 {
		result.Reason = "survey_supply_unknown"
		return result
	}
	homeTicks := int64(0)
	routeKnown := false
	for _, route := range extraction.HomeRoutes {
		reachable, rk := route.Reachable.Value()
		if ticks, known := route.TravelTicks.Value(); known && rk && reachable && ticks >= 0 {
			homeTicks = max(homeTicks, ticks)
			routeKnown = true
		}
	}
	if !routeKnown {
		result.Reason = "home_route_unknown"
		return result
	}
	remaining := float64(end-int64(now)+homeTicks)/float64(domain.TicksPerDay) + 1
	if len(extraction.Crew) > 0 && runway >= remaining {
		return result
	}
	// Relief is an ordinary affordable expedition carrying food for the whole
	// remaining hold and return. Existing journeys prevent duplicate relief.
	trips, tk := f.QuestExpeditionTrips.Value()
	if !tk {
		result.Reason = "journey_unknown"
		return result
	}
	tile, tileKnown := site.Tile.Value()
	for _, trip := range trips {
		destination, dk := trip.Destination.Value()
		if trip.Forming || tileKnown && dk && destination == tile {
			return result
		}
	}
	target, found := questExpeditionSite(offer, f)
	if !found {
		result.Reason = "site_unknown"
		return result
	}
	budget, reason := surveyBudget(offer, target, f)
	if reason != "" {
		result.Reason = reason
		return result
	}
	plan := PlanExpedition(offer, budget, f, food, p)
	result.Departure = plan.Departure
	result.Reason = plan.Reason
	return result
}
