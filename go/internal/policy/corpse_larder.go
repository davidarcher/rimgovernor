package policy

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

type CorpseHandling struct {
	ID                string
	Cell              domain.Cell
	Hauler            domain.PawnID
	FrozenDestination bool
}

// CookDemandNutrition is one batch per active cook bill, measured using
// its native ingredient filters. Released corpses fund the same window so
// subsequent reviews do not release the entire larder before butchering.
type FoodLarder struct {
	RawMeatNutrition, CookDemandNutrition float64
	Corpses                               []CorpseHandling
	ColdSites                             []domain.Cell
}

type CorpseLarderMethod struct {
	Kind     string
	Stock    FoodStock
	Handling CorpseHandling
	Cell     domain.Cell
}

// SelectCorpseLarder changes at most one corpse per review. A warm corpse is
// never held; frozen dense corpses release in expiry/identity order. A
// quarter-day rot margin leaves time for ordinary butchering and hauling.
func SelectCorpseLarder(v FoodStorageObservation) (CorpseLarderMethod, error) {
	none := CorpseLarderMethod{}
	stocks, sk := v.Stocks.Value()
	larder, lk := v.Larder.Value()
	if !sk || !lk {
		return none, nil
	}
	if !foodNumber(larder.RawMeatNutrition) || !foodNumber(larder.CookDemandNutrition) || len(larder.Corpses) > 4096 || len(larder.ColdSites) > 256 {
		return none, errors.New("invalid corpse larder")
	}
	if err := v.Validate(); err != nil {
		return none, err
	}
	handling := map[string]CorpseHandling{}
	for _, row := range larder.Corpses {
		if !foodID(row.ID) || row.Cell.X < 0 || row.Cell.Z < 0 {
			return none, errors.New("invalid corpse location")
		}
		if _, ok := handling[row.ID]; ok {
			return none, errors.New("duplicate corpse location")
		}
		handling[row.ID] = row
	}
	var corpses []FoodStock
	available := larder.RawMeatNutrition
	for _, row := range stocks {
		s := row.Stock
		if !s.Corpse {
			continue
		}
		forbidden, fk := s.Forbidden.Value()
		meat, mk := s.MeatAmount.Value()
		size, bk := s.BodySize.Value()
		nutrition, nk := s.Nutrition.Value()
		if !fk || !mk || !bk || !nk {
			return none, nil
		}
		if !foodNumber(meat) || !foodNumber(size) || meat <= 0 || size <= 0 {
			return none, errors.New("invalid corpse yield")
		}
		if !forbidden {
			available += nutrition
		}
		corpses = append(corpses, s)
	}
	if !foodNumber(available) {
		return none, errors.New("corpse nutrition overflow")
	}
	sort.Slice(corpses, func(i, j int) bool {
		a, ak := corpses[i].RotTicks.Value()
		b, bk := corpses[j].RotTicks.Value()
		if ak != bk {
			return ak
		}
		if a != b {
			return a < b
		}
		return corpses[i].ID < corpses[j].ID
	})
	for _, s := range corpses {
		row, ok := handling[s.ID]
		if !ok {
			continue
		}
		forbidden, _ := s.Forbidden.Value()
		roof, rk := s.Roofed.Value()
		temp, tk := s.TemperatureC.Value()
		ticks, ek := s.RotTicks.Value()
		meat, _ := s.MeatAmount.Value()
		size, _ := s.BodySize.Value()
		if !rk || !tk || !ek {
			continue
		}
		frozen := roof && temp <= 0
		dense := meat > 225 || size <= 0.75 && meat > 75
		if forbidden && (!frozen || !dense || ticks <= 15000 || available < larder.CookDemandNutrition) {
			return CorpseLarderMethod{Kind: "allow", Stock: s, Handling: row}, nil
		}
	}
	for _, s := range corpses {
		row, ok := handling[s.ID]
		if !ok {
			continue
		}
		forbidden, _ := s.Forbidden.Value()
		if forbidden {
			continue
		}
		roof, rk := s.Roofed.Value()
		temp, tk := s.TemperatureC.Value()
		ticks, ek := s.RotTicks.Value()
		meat, _ := s.MeatAmount.Value()
		size, _ := s.BodySize.Value()
		nutrition, _ := s.Nutrition.Value()
		if !rk || !tk || !ek {
			continue
		}
		if roof && temp <= 0 {
			if (meat > 225 || size <= 0.75 && meat > 75) && ticks > 15000 && available-nutrition >= larder.CookDemandNutrition {
				return CorpseLarderMethod{Kind: "forbid", Stock: s, Handling: row}, nil
			}
		} else if row.FrozenDestination && row.Hauler != "" {
			return CorpseLarderMethod{Kind: "haul", Stock: s, Handling: row}, nil
		} else if len(larder.ColdSites) > 0 {
			return CorpseLarderMethod{Kind: "zone", Cell: larder.ColdSites[0]}, nil
		}
	}
	return none, nil
}
