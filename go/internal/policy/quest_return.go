package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type SiteReturnPlan struct {
	Site      string
	Departure *domain.CaravanDeparture
	Crew      []domain.PawnID
	HomeTile  int32
	Retreat   bool
	Reason    QuestSkipReason
}

// A stopped caravan with a crew from a journaled expedition can return using
// the native home route. A moving outbound or returning caravan keeps its order.
func PlanStoppedExpeditionReturn(trip ExpeditionTrip, departed []domain.PawnID) *domain.CaravanDeparture {
	if trip.Forming || len(trip.PawnIDs) == 0 {
		return nil
	}
	if _, moving := trip.Destination.Value(); moving {
		return nil
	}
	for _, id := range trip.PawnIDs {
		if !slices.Contains(departed, id) {
			return nil
		}
	}
	tile, known := trip.Tile.Value()
	if !known {
		return nil
	}
	routes := slices.Clone(trip.HomeRoutes)
	sort.Slice(routes, func(i, j int) bool {
		a, _ := routes[i].TravelTicks.Value()
		b, _ := routes[j].TravelTicks.Value()
		if a == b {
			return routes[i].Tile < routes[j].Tile
		}
		return a < b
	})
	for _, route := range routes {
		reachable, rk := route.Reachable.Value()
		ticks, tk := route.TravelTicks.Value()
		if route.Tile == tile {
			return nil
		}
		if !rk || !reachable || !tk || ticks < 0 {
			continue
		}
		departure, err := domain.NewCaravanDeparture(trip.PawnIDs, nil, route.Tile)
		if err == nil {
			return &departure
		}
	}
	return nil
}

// PlanSiteReturn retains the desired inventory explicitly because vanilla
// reform empties inventories before packing the selected transferables. A
// failed fight requests a walking retreat; it never reforms through hostiles.
func PlanSiteReturn(site WorldSite, failedFight bool) SiteReturnPlan {
	result := SiteReturnPlan{Site: site.ID}
	fail := func(reason QuestSkipReason) SiteReturnPlan { result.Reason = reason; return result }
	extraction, known := site.Extraction.Value()
	if !known || len(extraction.Crew) == 0 {
		return fail("extraction_unknown")
	}
	result.Crew = slices.Clone(extraction.Crew)
	routes := slices.Clone(extraction.HomeRoutes)
	sort.Slice(routes, func(i, j int) bool {
		a, ak := routes[i].TravelTicks.Value()
		b, bk := routes[j].TravelTicks.Value()
		if ak != bk {
			return ak
		}
		if a != b {
			return a < b
		}
		return routes[i].Map < routes[j].Map
	})
	var route *SiteHomeRoute
	for i := range routes {
		reachable, rk := routes[i].Reachable.Value()
		ticks, tk := routes[i].TravelTicks.Value()
		if rk && reachable && tk && ticks >= 0 {
			route = &routes[i]
			break
		}
	}
	if route == nil {
		return fail("home_route_unknown")
	}
	result.HomeTile = route.Tile
	canReform, known := extraction.CanReform.Value()
	if !known {
		return fail("reform_unknown")
	}
	if !canReform {
		if failedFight {
			result.Retreat = true
			return result
		}
		return fail("site_fight_pending")
	}
	security, known := site.Security.Value()
	if !known {
		return fail("security_unknown")
	}
	complete, ck := security.Known.Value()
	traps, tk := security.TrapCount.Value()
	if !ck || !complete || !tk {
		return fail("security_unknown")
	}
	if traps > 0 {
		return fail("site_hazards_pending")
	}
	active, ak := security.ActiveThreat.Value()
	dormant, dk := security.DormantThreat.Value()
	if !ak || !dk {
		return fail("security_unknown")
	}
	if active || dormant {
		result.Retreat = failedFight
		return fail("site_fight_pending")
	}
	ticks, _ := route.TravelTicks.Value()
	days := float64(ticks)/float64(domain.TicksPerDay) + 1
	cargo, reason := siteReturnCargo(extraction, days, !failedFight)
	if reason != "" {
		return fail(reason)
	}
	departure, err := domain.NewCaravanDeparture(extraction.Crew, cargo, route.Tile)
	if err != nil {
		return fail("cargo_invalid")
	}
	result.Departure = &departure
	return result
}

func siteReturnCargo(extraction SiteExtraction, days float64, loot bool) ([]domain.CargoItem, QuestSkipReason) {
	capacity, ck := extraction.CarryCapacity.Value()
	carried, mk := extraction.CarriedMass.Value()
	inventoryFood, fk := extraction.InventoryFoodDays.Value()
	rate, rk := extraction.CrewNutritionPerDay.Value()
	if !ck || !mk || !finite(capacity) || !finite(carried) || capacity < 0 || carried < 0 {
		return nil, "mass_unknown"
	}
	if !fk || !rk || !finite(inventoryFood) || !finite(rate) || inventoryFood < 0 || rate <= 0 {
		return nil, "food_unknown"
	}
	rows := slices.Clone(extraction.Cargo)
	sort.Slice(rows, func(i, j int) bool {
		a, _ := rows[i].Held.Value()
		b, _ := rows[j].Held.Value()
		if a != b {
			return a
		}
		av, _ := rows[i].MarketValue.Value()
		bv, _ := rows[j].MarketValue.Value()
		am, _ := rows[i].UnitMass.Value()
		bm, _ := rows[j].UnitMass.Value()
		if am > 0 {
			av /= am
		}
		if bm > 0 {
			bv /= bm
		}
		if av != bv {
			return av > bv
		}
		return rows[i].ID < rows[j].ID
	})
	// Inventory mass is already in CarriedMass, but reform repacks it. Credit
	// that mass once, then account for every retained inventory stack below.
	free := capacity - carried
	for _, row := range rows {
		held, hk := row.Held.Value()
		count, nk := row.Count.Value()
		mass, mk := row.UnitMass.Value()
		if !hk || !nk || !mk || count < 0 || !finite(mass) || mass < 0 {
			return nil, "cargo_unknown"
		}
		if held {
			free += float64(count) * mass
		}
	}
	packed := map[string]uint64{}
	left := map[string]int64{}
	for _, row := range rows {
		count, _ := row.Count.Value()
		mass, _ := row.UnitMass.Value()
		held, _ := row.Held.Value()
		if row.Def == "" || count > math.MaxInt32 {
			return nil, "cargo_invalid"
		}
		left[row.ID] = count
		if !held {
			continue
		}
		if float64(count)*mass > free {
			return nil, "mass_capacity"
		}
		packed[row.Def] += uint64(count)
		left[row.ID] = 0
		free -= float64(count) * mass
	}
	need := max(0.0, days-inventoryFood) * rate
	for _, row := range rows {
		if need <= 0 {
			break
		}
		count := left[row.ID]
		nutrition, nk := row.Nutrition.Value()
		mass, _ := row.UnitMass.Value()
		if count == 0 || !nk || !finite(nutrition) || nutrition <= 0 {
			continue
		}
		take := min(count, int64(math.Ceil(need/nutrition)))
		if mass > 0 {
			take = min(take, max(int64(0), int64(math.Floor(free/mass))))
		}
		packed[row.Def] += uint64(take)
		left[row.ID] -= take
		free -= float64(take) * mass
		need -= float64(take) * nutrition
	}
	if need > 0 {
		return nil, "food_capacity"
	}
	if loot {
		for _, row := range rows {
			count := left[row.ID]
			value, vk := row.MarketValue.Value()
			mass, _ := row.UnitMass.Value()
			if count == 0 || !vk || !finite(value) || value <= 0 {
				continue
			}
			take := count
			if mass > 0 {
				take = min(take, max(int64(0), int64(math.Floor(free/mass))))
			}
			packed[row.Def] += uint64(take)
			free -= float64(take) * mass
		}
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
