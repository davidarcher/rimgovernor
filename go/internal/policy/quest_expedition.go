package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ExpeditionSite is a derived native route and target. RoutePawnIDs identifies
// the crew used for the conservative travel estimate; a selected crew must be
// a subset. Layered destinations cannot use the surface caravan action.
type ExpeditionSite struct {
	ID           string
	Quest        domain.QuestID
	Tile         int32
	Layer        domain.Fact[int32]
	ThreatPoints domain.Fact[float64]
	TravelTicks  domain.Fact[int64]
	Reachable    domain.Fact[bool]
	RoutePawnIDs []domain.PawnID
	HoldTicks    domain.Fact[int64]
}

type ExpeditionPlan struct {
	Quest     domain.QuestID
	Site      string
	Departure *domain.CaravanDeparture
	Reason    QuestSkipReason
	Waiting   bool
}

type ExpeditionTrip struct {
	Tile, Destination domain.Fact[int32]
	PawnIDs           []domain.PawnID
	Forming           bool
	HomeRoutes        []SiteHomeRoute
}

func questExpeditionSite(offer JoinerOffer, f RoundsFacts) (ExpeditionSite, bool) {
	sites, known := f.QuestSites.Value()
	if !known {
		return ExpeditionSite{}, false
	}
	var selected *WorldSite
	for i := range sites {
		if !slices.Contains(sites[i].QuestIDs, offer.Quest) {
			continue
		}
		if selected != nil {
			return ExpeditionSite{}, false
		}
		selected = &sites[i]
	}
	if selected == nil {
		return ExpeditionSite{}, false
	}
	if selected.State == o.WorldSiteState_WORLD_SITE_STATE_DESTROYED || selected.State == o.WorldSiteState_WORLD_SITE_STATE_UNKNOWN {
		return ExpeditionSite{}, false
	}
	tile, known := selected.Tile.Value()
	if !known {
		return ExpeditionSite{}, false
	}
	threat := selected.ThreatPoints
	if offer.ScriptDef != "OpportunitySite_PeaceTalks" {
		threat = domain.Unknown[float64]()
		if security, sk := selected.Security.Value(); sk {
			complete, ck := security.Known.Value()
			initial, ik := security.InitialPoints.Value()
			pending, pk := security.PendingRaidPoints.Value()
			if ck && complete && ik && pk && finite(initial) && finite(pending) && initial >= 0 && pending >= 0 {
				threat = domain.Known(initial + pending)
			}
		}
	}
	return ExpeditionSite{ID: selected.ID, Quest: offer.Quest, Tile: tile, Layer: selected.Layer, ThreatPoints: threat, TravelTicks: selected.TravelTicks, Reachable: selected.Reachable, RoutePawnIDs: selected.RoutePawnIDs, HoldTicks: domain.Known(int64(0))}, true
}

func ExpeditionDeficit(f RoundsFacts) bool {
	offers, known := f.QuestOffers.Value()
	if !known {
		return false
	}
	for _, offer := range offers {
		profile, known := offer.Profile.Value()
		if known && !profile.NeverAct && profile.Disposition != QuestRefuse && offer.State == "Ongoing" {
			if _, found := questExpeditionSite(offer, f); found {
				return true
			}
		}
	}
	return false
}

func SelectExpedition(f RoundsFacts, p RoundsPolicy) ExpeditionPlan {
	offers, known := f.QuestOffers.Value()
	if !known {
		return ExpeditionPlan{}
	}
	offers = slices.Clone(offers)
	sort.Slice(offers, func(i, j int) bool { return offers[i].Quest < offers[j].Quest })
	for _, offer := range offers {
		profile, known := offer.Profile.Value()
		if !known || profile.NeverAct || profile.Disposition == QuestRefuse || offer.State != "Ongoing" {
			continue
		}
		site, found := questExpeditionSite(offer, f)
		if !found {
			continue
		}
		trips, known := f.QuestExpeditionTrips.Value()
		if !known {
			return ExpeditionPlan{Quest: offer.Quest, Site: site.ID, Reason: "journey_unknown", Waiting: true}
		}
		for _, trip := range trips {
			tile, tk := trip.Tile.Value()
			destination, dk := trip.Destination.Value()
			if tk && tile == site.Tile || dk && destination == site.Tile || trip.Forming {
				return ExpeditionPlan{Quest: offer.Quest, Site: site.ID, Waiting: true}
			}
		}
		// A live site map proves arrival; its work belongs to the site driver.
		if sites, known := f.QuestSites.Value(); known {
			for _, row := range sites {
				if row.ID == site.ID && row.State == o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED {
					if offer.ScriptDef == "SurveySite" {
						work := SurveyWork(offer, row, f, f.AnimalUpkeep.Food, p)
						return ExpeditionPlan{Quest: offer.Quest, Site: site.ID, Departure: work.Departure, Waiting: work.Waiting, Reason: work.Reason}
					}
					return ExpeditionPlan{Quest: offer.Quest, Site: site.ID, Waiting: true}
				}
			}
		}
		if offer.ScriptDef == "SurveySite" {
			return PlanSurvey(offer, site, f, f.AnimalUpkeep.Food, p)
		}
		if offer.ScriptDef == "OpportunitySite_PeaceTalks" {
			return PlanPeaceTalks(offer, site, f, f.AnimalUpkeep.Food, p)
		}
		return PlanExpedition(offer, site, f, f.AnimalUpkeep.Food, p)
	}
	return ExpeditionPlan{}
}

func ExpeditionAdmission(offer JoinerOffer, f RoundsFacts) QuestSkipReason {
	if offer.ScriptDef == "SurveySite" {
		return SurveyAdmission(offer, f)
	}
	if offer.ScriptDef == "OpportunitySite_PeaceTalks" {
		return PeaceTalksAdmission(offer, f)
	}
	site, found := questExpeditionSite(offer, f)
	if !found {
		return "site_unknown"
	}
	return PlanExpedition(offer, site, f, f.AnimalUpkeep.Food, DefaultRoundsPolicy()).Reason
}

// PlanExpedition shares departure staffing and native defense accounting with
// shuttle missions. It packs only observed food that every selected pawn eats,
// survives the round trip, and leaves the colony's diet-aware reserve intact.
func PlanExpedition(offer JoinerOffer, site ExpeditionSite, f RoundsFacts, food domain.Fact[FoodSupply], p RoundsPolicy) ExpeditionPlan {
	return planExpedition(offer, site, f, food, p, nil)
}

// PlanExpeditionCrew validates a specialized crew through the common staffing,
// route, mass and food gates. Diplomacy may select noncombatant colonists.
func PlanExpeditionCrew(offer JoinerOffer, site ExpeditionSite, f RoundsFacts, food domain.Fact[FoodSupply], p RoundsPolicy, crew []domain.PawnID) ExpeditionPlan {
	if len(crew) == 0 {
		return ExpeditionPlan{Quest: offer.Quest, Site: site.ID, Reason: "crew_unknown"}
	}
	return planExpedition(offer, site, f, food, p, crew)
}

func planExpedition(offer JoinerOffer, site ExpeditionSite, f RoundsFacts, food domain.Fact[FoodSupply], p RoundsPolicy, explicit []domain.PawnID) ExpeditionPlan {
	result := ExpeditionPlan{Quest: offer.Quest, Site: site.ID}
	fail := func(reason QuestSkipReason) ExpeditionPlan { result.Reason = reason; return result }
	if site.Quest != offer.Quest || site.ID == "" || site.Tile < 0 {
		return fail("site_unknown")
	}
	layer, lk := site.Layer.Value()
	if !lk {
		return fail("route_unknown")
	}
	if layer != 0 {
		return fail("ship_only")
	}
	reachable, rk := site.Reachable.Value()
	ticks, tk := site.TravelTicks.Value()
	hold, hk := site.HoldTicks.Value()
	if !rk || !tk || !hk || ticks < 0 || hold < 0 {
		return fail("route_unknown")
	}
	if !reachable {
		return fail("route_unreachable")
	}
	threat, known := site.ThreatPoints.Value()
	if !known || !finite(threat) || threat < 0 {
		return fail("threat_unknown")
	}
	tripTicks := float64(ticks)*2 + float64(hold) + float64(domain.TicksPerDay)
	// Offer expiry only limits acceptance. Site objective deadlines, when
	// present, limit travel after acceptance.
	if now, known := f.QuestObservedTick.Value(); known {
		for _, objective := range offer.Objectives {
			if deadline, known := objective.DeadlineTicks.Value(); known && float64(deadline)-float64(now) <= float64(ticks)+float64(hold) {
				return fail("deadline_capacity")
			}
		}
	}
	spare, known := f.QuestSparePawns.Value()
	if !known {
		return fail("capacity_unknown")
	}
	rows, known := f.QuestDeparturePawns.Value()
	if !known {
		return fail("capacity_unknown")
	}
	strength := map[domain.PawnID]float64{}
	for _, pawn := range rows {
		if value, known := pawn.DefensePoints.Value(); known && finite(value) && value >= 0 {
			strength[domain.PawnID(pawn.ID)] = value
		}
	}
	var crew []domain.PawnID
	if len(explicit) > 0 {
		filtered := []PawnID{}
		for _, id := range explicit {
			if !slices.Contains(spare, PawnID(id)) || slices.Contains(filtered, PawnID(id)) {
				return fail("no_spare_pawn")
			}
			filtered = append(filtered, PawnID(id))
		}
		mission := offer
		mission.Profile = domain.Known(QuestProfile{Family: QuestFamilyBanditCamp})
		mission.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_PAWNS, Count: domain.Known(int64(len(explicit)))}}
		f.QuestSparePawns = domain.Known(filtered)
		selected, reason := departureSquadWithCrew(mission, f, false, true)
		if reason != "" {
			return fail(reason)
		}
		crew = selected
	}
	for count := 1; len(crew) == 0 && count <= len(spare); count++ {
		// The common departure selector takes an explicit required headcount.
		mission := offer
		mission.Profile = domain.Known(QuestProfile{Family: QuestFamilyBanditCamp})
		mission.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_PAWNS, Count: domain.Known(int64(count))}}
		candidate, reason := departureSquad(mission, f, true)
		if reason != "" {
			return fail(reason)
		}
		points := 0.0
		for _, id := range candidate {
			points += strength[id]
		}
		if points >= threat {
			crew = candidate
			break
		}
	}
	if len(crew) == 0 {
		return fail("expedition_strength")
	}
	points := 0.0
	for _, id := range crew {
		points += strength[id]
	}
	if points < threat {
		return fail("expedition_strength")
	}
	for _, id := range crew {
		if !slices.Contains(site.RoutePawnIDs, id) {
			return fail("route_crew_unknown")
		}
	}
	supply, known := food.Value()
	if !known {
		return fail("food_unknown")
	}
	capacity := 0.0
	for _, id := range crew {
		for _, pawn := range rows {
			if domain.PawnID(pawn.ID) != id {
				continue
			}
			carry, ck := pawn.CarryCapacity.Value()
			mass, mk := pawn.CarriedMass.Value()
			if !ck || !mk || !finite(carry) || !finite(mass) || carry < 0 || mass < 0 {
				return fail("mass_unknown")
			}
			capacity += carry - mass
		}
	}
	cargo, reason := expeditionFood(supply, crew, tripTicks/float64(domain.TicksPerDay), p.FoodMinDays, capacity)
	if reason != "" {
		return fail(reason)
	}
	departure, err := domain.NewCaravanDeparture(crew, cargo, site.Tile)
	if err != nil {
		return fail("cargo_invalid")
	}
	result.Departure = &departure
	return result
}

func expeditionFood(supply FoodSupply, crew []domain.PawnID, days, homeDays, capacity float64) ([]domain.CargoItem, QuestSkipReason) {
	if complete, known := supply.Complete.Value(); !known || !complete || !finite(days) || days < 1 {
		return nil, "food_unknown"
	}
	need := 0.0
	crewSeen := map[domain.PawnID]bool{}
	home := []PawnID{}
	for _, consumer := range supply.Consumers {
		if slices.Contains(crew, domain.PawnID(consumer.ID)) {
			crewSeen[domain.PawnID(consumer.ID)] = true
			rate, known := consumer.NutritionPerDay.Value()
			if !known || !finite(rate) || rate <= 0 {
				return nil, "food_unknown"
			}
			need += rate * days
		} else {
			home = append(home, consumer.ID)
		}
	}
	if need <= 0 || len(home) == 0 || len(crewSeen) != len(crew) {
		return nil, "food_unknown"
	}
	remaining := supply
	remaining.Stocks = slices.Clone(supply.Stocks)
	indices := make([]int, len(remaining.Stocks))
	for i := range indices {
		indices[i] = i
	}
	sort.Slice(indices, func(i, j int) bool { return remaining.Stocks[indices[i]].ID < remaining.Stocks[indices[j]].ID })
	packed := map[string]uint64{}
	for _, index := range indices {
		stock := &remaining.Stocks[index]
		holder, owned := stock.Holder.Value()
		count, ck := stock.Count.Value()
		nutrition, nk := stock.Nutrition.Value()
		perishable, pk := stock.Perishable.Value()
		rot, rotk := stock.RotTicks.Value()
		forbidden, fk := stock.Forbidden.Value()
		mass, mk := stock.UnitMass.Value()
		if !owned || holder != "" || !ck || count <= 0 || !nk || !finite(nutrition) || nutrition <= 0 || !pk || stock.Corpse || stock.IsHumanMeat || stock.RawMeat || !fk || forbidden || stock.Reserve || stock.DefName == "" || !mk || !finite(mass) || mass < 0 {
			continue
		}
		if perishable && (!rotk || float64(rot) <= days*float64(domain.TicksPerDay)) {
			continue
		}
		eligible := true
		for _, id := range crew {
			if !slices.Contains(stock.Eaters, PawnID(id)) {
				eligible = false
				break
			}
		}
		if !eligible {
			continue
		}
		unit := nutrition / float64(count)
		take := min(count, int64(math.Ceil(need/unit)))
		if mass > 0 {
			take = min(take, max(int64(0), int64(math.Floor(capacity/mass))))
		}
		// Find the largest pack from this stack that leaves every home consumer
		// at the minimum runway. ForecastFood accounts for competing diets.
		original := *stock
		lo, hi := int64(0), take
		for lo < hi {
			mid := lo + (hi-lo+1)/2
			stock.Count = domain.Known(count - mid)
			stock.Nutrition = domain.Known(nutrition - float64(mid)*unit)
			forecast, err := ForecastFood(remaining, home)
			runway, known := forecast.RunwayDays.Value()
			if err == nil && known && runway >= homeDays {
				lo = mid
			} else {
				hi = mid - 1
			}
		}
		*stock = original
		stock.Count = domain.Known(count - lo)
		stock.Nutrition = domain.Known(nutrition - float64(lo)*unit)
		packed[string(stock.DefName)] += uint64(lo)
		need -= float64(lo) * unit
		capacity -= float64(lo) * mass
		if need <= 0 {
			break
		}
	}
	if need > 0 {
		return nil, "food_capacity"
	}
	cargo := []domain.CargoItem{}
	for def, count := range packed {
		if count > 0 {
			cargo = append(cargo, domain.CargoItem{Definition: def, Count: count})
		}
	}
	sort.Slice(cargo, func(i, j int) bool { return cargo[i].Definition < cargo[j].Definition })
	return cargo, ""
}
