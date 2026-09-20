package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CaravanFoodInsufficient: no combination of crew-eligible food that survives
// the journey covers the crew's demand for it (SelectCaravanFood refused).
const CaravanFoodInsufficient Reason = "caravan_food_insufficient"

// CaravanCargoGroup is one native catalog cargo group with its food facts
// (bridge.CaravanCatalogRead's CargoGroup rows in plain values). Nutrition is
// per unit and unknown for anything that is not food; Eaters lists the
// colonists whose diet admits the group.
type CaravanCargoGroup struct {
	GroupID    string
	Definition string
	Count      int64
	Nutrition  domain.Fact[float64]
	Perishable bool
	RotDays    domain.Fact[float64]
	Reserve    bool
	Eaters     []domain.PawnID
}

// CaravanCargoRequest is what the departure adapter composes a pack from:
// the action's own cargo (trade goods, by definition), the catalog groups,
// each colonist's observed daily nutrition demand (crew and home alike) and
// the journey the food must outlast, in days including the travel margin.
// HomeFoodMinDays is the runway the home colony keeps after the pack leaves
// (RoutinePolicy.FoodMinDays).
type CaravanCargoRequest struct {
	Crew            []domain.PawnID
	Cargo           []domain.CargoItem
	Groups          []CaravanCargoGroup
	Demand          map[domain.PawnID]float64
	JourneyDays     float64
	HomeFoodMinDays float64
}

// CaravanCargoSelection is one FormCaravan cargo line: a catalog group and
// the units loaded from it.
type CaravanCargoSelection struct {
	GroupID    string
	Definition string
	Count      int64
}

// CaravanCargoPlan is the composed pack: the action's cargo resolved to
// groups, then the journey food SelectCaravanFood chose, with the home
// colony's food runway after the pack leaves.
type CaravanCargoPlan struct {
	Cargo          []CaravanCargoSelection
	Food           []CaravanFoodCargo
	HomeRunwayDays float64
}

// PlanCaravanCargo resolves the action's cargo against the catalog, packs the
// crew's journey food from the groups every crew member can eat (reserve
// first, then longest shelf life; SelectCaravanFood) and refuses a pack that
// would leave the home colony's unreserved food runway under the floor. The
// reserve is forbidden stock the home runway never counted, so packing it
// costs the floor nothing; MaintainFoodStorage refills it afterwards.
//
// UnknownFacts: a cargo definition the catalog does not list or offers fewer
// units of, or a crew or home colonist without an observed demand.
// CaravanFoodInsufficient: the surviving crew-eligible food cannot cover the
// journey. CaravanHomeFoodInsufficient: the remaining runway is under the
// floor.
func PlanCaravanCargo(r CaravanCargoRequest) (CaravanCargoPlan, Reason) {
	if len(r.Crew) == 0 || len(r.Groups) > 256 || !fieldPositive(r.JourneyDays) || !foodNumber(r.HomeFoodMinDays) {
		return CaravanCargoPlan{}, UnknownFacts
	}
	// A definition may span several groups (stacks split by quality, stuff,
	// ingredients, rot stage or hit points); each is drawn in catalog order.
	byDef := make(map[string][]int, len(r.Groups))
	seenGroup := make(map[string]bool, len(r.Groups))
	for i, group := range r.Groups {
		if !foodID(group.GroupID) || !foodID(group.Definition) || group.Count < 0 || seenGroup[group.GroupID] {
			return CaravanCargoPlan{}, UnknownFacts
		}
		seenGroup[group.GroupID] = true
		byDef[group.Definition] = append(byDef[group.Definition], i)
	}
	crew := make(map[domain.PawnID]bool, len(r.Crew))
	crewDemand := 0.0
	for _, pawn := range r.Crew {
		demand, known := r.Demand[pawn]
		if !known || !foodNumber(demand) || crew[pawn] {
			return CaravanCargoPlan{}, UnknownFacts
		}
		crew[pawn] = true
		crewDemand += demand
	}
	homeDemand := 0.0
	for pawn, demand := range r.Demand {
		if !foodNumber(demand) {
			return CaravanCargoPlan{}, UnknownFacts
		}
		if !crew[pawn] {
			homeDemand += demand
		}
	}
	plan := CaravanCargoPlan{}
	taken := make(map[string]int64, len(r.Groups))
	for _, item := range r.Cargo {
		indexes, ok := byDef[item.Definition]
		if !ok || item.Count == 0 || item.Count > math.MaxInt32 {
			return CaravanCargoPlan{}, UnknownFacts
		}
		want := int64(item.Count)
		for _, i := range indexes {
			group := r.Groups[i]
			free := group.Count - taken[group.GroupID]
			if free <= 0 || want == 0 {
				continue
			}
			take := min(free, want)
			taken[group.GroupID] += take
			want -= take
			plan.Cargo = append(plan.Cargo, CaravanCargoSelection{GroupID: group.GroupID, Definition: item.Definition, Count: take})
		}
		if want > 0 {
			return CaravanCargoPlan{}, UnknownFacts
		}
	}
	byGroup := make(map[string]CaravanCargoGroup, len(r.Groups))
	var stocks []CaravanFoodStock
	for _, group := range r.Groups {
		byGroup[group.GroupID] = group
		nutrition, known := group.Nutrition.Value()
		if !known {
			continue
		}
		eaters := make(map[domain.PawnID]bool, len(group.Eaters))
		for _, pawn := range group.Eaters {
			eaters[pawn] = true
		}
		eligible := true
		for pawn := range crew {
			if !eaters[pawn] {
				eligible = false
				break
			}
		}
		if !eligible {
			continue
		}
		stocks = append(stocks, CaravanFoodStock{GroupID: group.GroupID, Count: group.Count - taken[group.GroupID], Nutrition: nutrition, Perishable: group.Perishable, RotDays: group.RotDays, Reserve: group.Reserve})
	}
	if !fieldPositive(crewDemand) {
		return CaravanCargoPlan{}, UnknownFacts
	}
	food, ok := SelectCaravanFood(stocks, crewDemand, r.JourneyDays)
	if !ok {
		return CaravanCargoPlan{}, CaravanFoodInsufficient
	}
	plan.Food = food
	for _, line := range food {
		taken[line.GroupID] += line.Count
		plan.Cargo = append(plan.Cargo, CaravanCargoSelection{GroupID: line.GroupID, Definition: byGroup[line.GroupID].Definition, Count: line.Count})
	}
	remaining := 0.0
	for _, group := range r.Groups {
		nutrition, known := group.Nutrition.Value()
		if !known || group.Reserve {
			continue
		}
		homeEats := false
		for _, pawn := range group.Eaters {
			if !crew[pawn] {
				homeEats = true
				break
			}
		}
		if homeEats {
			remaining += float64(group.Count-taken[group.GroupID]) * nutrition
		}
	}
	plan.HomeRunwayDays = math.Inf(1)
	if homeDemand > 0 {
		plan.HomeRunwayDays = remaining / homeDemand
		if plan.HomeRunwayDays < r.HomeFoodMinDays {
			return CaravanCargoPlan{}, CaravanHomeFoodInsufficient
		}
	}
	return plan, ""
}
