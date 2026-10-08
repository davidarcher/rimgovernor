package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"slices"
	"sort"
)

// TradeMissionCrew selects one healthy negotiator, retaining the existing
// departure policy's last work owners, home staffing and defense headroom.
// Claimed pawns include urgent work and other outstanding missions.
func TradeMissionCrew(f RoundsFacts, claimed []domain.PawnID) ([]domain.PawnID, QuestSkipReason) {
	rows, known := f.QuestDeparturePawns.Value()
	if !known {
		return nil, "capacity_unknown"
	}
	rows = slices.Clone(rows)
	sort.Slice(rows, func(i, j int) bool {
		a, ak := rows[i].NegotiationAbility.Value()
		b, bk := rows[j].NegotiationAbility.Value()
		if ak != bk {
			return ak
		}
		if a != b {
			return a > b
		}
		return rows[i].ID < rows[j].ID
	})
	spare, known := f.QuestSparePawns.Value()
	if !known {
		return nil, "capacity_unknown"
	}
	for _, row := range rows {
		ability, ak := row.NegotiationAbility.Value()
		id := domain.PawnID(row.ID)
		if !ak || !finite(ability) || ability <= 0 || slices.Contains(claimed, id) || !slices.Contains(spare, row.ID) {
			continue
		}
		candidate := f
		candidate.QuestSparePawns = domain.Known([]PawnID{row.ID})
		offer := JoinerOffer{Profile: domain.Known(QuestProfile{Family: QuestFamilyBanditCamp}), Objectives: []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_PAWNS, Count: domain.Known(int64(1))}}}
		crew, reason := departureSquadWithCrew(offer, candidate, false, true)
		if reason == "" {
			return crew, ""
		}
	}
	return nil, "no_safe_negotiator"
}

// TradeMissionFood uses observed dietary access and rot, leaving every home
// consumer's reserve. Capacity excludes the observed silver mass.
func TradeMissionFood(f RoundsFacts, crew []domain.PawnID, roundTripDays, capacity float64, p RoundsPolicy) ([]domain.CargoItem, QuestSkipReason) {
	supply, known := f.AnimalUpkeep.Food.Value()
	if !known || !finite(capacity) || capacity < 0 || !finite(roundTripDays) || roundTripDays < 0 {
		return nil, "food_unknown"
	}
	return expeditionFood(supply, crew, max(1, roundTripDays), p.FoodMinDays, capacity)
}

func TradeMissionCarryCapacity(f RoundsFacts, crew []domain.PawnID) (float64, bool) {
	rows, known := f.QuestDeparturePawns.Value()
	if !known {
		return 0, false
	}
	capacity := 0.0
	for _, id := range crew {
		found := false
		for _, row := range rows {
			if domain.PawnID(row.ID) != id {
				continue
			}
			carry, ck := row.CarryCapacity.Value()
			mass, mk := row.CarriedMass.Value()
			if !ck || !mk || !foodNumber(carry) || !foodNumber(mass) || carry < mass {
				return 0, false
			}
			capacity += carry - mass
			found = true
		}
		if !found {
			return 0, false
		}
	}
	return capacity, true
}

// SafeTradeMissionPack admits only a fresh native calculation for the exact
// final crew and cargo, including both asymmetric routes.
func SafeTradeMissionPack(pack *o.TradePackEstimate, homeTile, settlementTile int32) bool {
	if pack == nil || pack.CanPack == nil || !pack.GetCanPack() || pack.MassUsage == nil || pack.MassCapacity == nil || !foodNumber(pack.GetMassUsage()) || !foodNumber(pack.GetMassCapacity()) || pack.GetMassUsage() > pack.GetMassCapacity() || pack.FoodDays == nil || pack.FoodRotDays == nil || !foodNumber(pack.GetFoodDays()) || !foodNumber(pack.GetFoodRotDays()) {
		return false
	}
	days, known := TradeMissionRouteDays(pack, homeTile, settlementTile)
	return known && pack.GetFoodDays() >= max(1, days) && pack.GetFoodRotDays() >= max(1, days)
}

func TradeMissionRouteDays(pack *o.TradePackEstimate, homeTile, settlementTile int32) (float64, bool) {
	if pack == nil {
		return 0, false
	}
	for i, route := range []*o.WorldRoute{pack.Outbound, pack.Home} {
		tile := settlementTile
		if i == 1 {
			tile = homeTile
		}
		if route == nil || route.Destination == nil || route.GetDestination() != tile || route.Reachable == nil || !route.GetReachable() || route.EstimatedTicks == nil || route.GetEstimatedTicks() < 0 {
			return 0, false
		}
	}
	return (float64(pack.Outbound.GetEstimatedTicks()) + float64(pack.Home.GetEstimatedTicks())) / float64(domain.TicksPerDay), true
}
