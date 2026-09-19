package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

func validateFoodChannels(v *o.ColonyFactsSnapshot) error {
	if v.FoodChannels == nil {
		return nil
	}
	switch s := v.FoodChannels.Outcome.(type) {
	case *o.FoodChannelsSection_Unavailable:
		return validateUnavailable(s.Unavailable)
	case *o.FoodChannelsSection_Observed:
		f := s.Observed
		if f == nil || colonyCounts(f.Completeness, 1, 1) != nil || f.Completeness.GetFiltered() != 0 {
			return contract("incomplete food channels")
		}
		for _, n := range []int{len(f.Gatherable), len(f.EggLayer), len(f.PasteDispenser), len(f.Forage), len(f.Grazing), len(f.Slaughter)} {
			if n > 256 {
				return contract("food channels exceed bound")
			}
		}
		width := min(v.Center.GetX()+22, int32(v.MapSize.GetWidth())-1) - max(v.Center.GetX()-22, 0) + 1
		height := min(v.Center.GetZ()+22, int32(v.MapSize.GetHeight())-1) - max(v.Center.GetZ()-22, 0) + 1
		if f.GetPollutedCells() > uint32(width*height) {
			return contract("pollution exceeds farm window")
		}
		seen := map[[2]string]bool{}
		pens := map[string]bool{}
		for _, row := range f.Grazing {
			if row == nil || validID(row.GetPenId()) != nil || pens[row.GetPenId()] || !combatNumber(row.DemandPerDay, true) || !combatNumber(row.PasturePerDay, true) || !combatNumber(row.StoredNutrition, true) {
				return contract("invalid pen grazing forecast")
			}
			pens[row.GetPenId()] = true
		}
		meat := map[string]bool{}
		for _, row := range f.Slaughter {
			if row == nil || validID(row.GetPawnId()) != nil || validID(row.GetRace()) != nil || meat[row.GetPawnId()] || !combatNumber(row.MeatNutrition, true) || !combatNumber(row.FeedPerDay, true) || !combatNumber(row.ReproductionDays, true) {
				return contract("invalid slaughter food facts")
			}
			meat[row.GetPawnId()] = true
		}
		for _, row := range f.Gatherable {
			if row == nil || validID(row.GetPawnId()) != nil || validID(row.GetRace()) != nil || !combatNumber(row.Fullness, true) || row.GetFullness() > 1 || row.Resource != nil && validID(row.GetResource()) != nil {
				return contract("invalid gatherable animal")
			}
			key := [2]string{row.GetPawnId(), row.GetResource()}
			if seen[key] {
				return contract("duplicate gatherable animal resource")
			}
			seen[key] = true
			if !combatNumber(row.NutritionPerDay, true) || !combatNumber(row.WorkPerDay, true) || !combatNumber(row.LeadDays, true) {
				return contract("invalid gatherable production rate")
			}
		}
		eggs := map[string]bool{}
		for _, row := range f.EggLayer {
			if row == nil || validID(row.GetPawnId()) != nil || validID(row.GetRace()) != nil || eggs[row.GetPawnId()] || !combatNumber(row.Progress, true) || row.GetProgress() > 1 {
				return contract("invalid egg layer")
			}
			eggs[row.GetPawnId()] = true
			if !combatNumber(row.NutritionPerDay, true) || !combatNumber(row.LeadDays, true) {
				return contract("invalid egg production rate")
			}
		}
		dispensers := map[string]bool{}
		for _, row := range f.PasteDispenser {
			if row == nil || validID(row.GetBuildingId()) != nil || dispensers[row.GetBuildingId()] || !combatNumber(row.HopperNutrition, true) || row.AdjacentRoomId != nil && validID(row.GetAdjacentRoomId()) != nil {
				return contract("invalid paste dispenser")
			}
			dispensers[row.GetBuildingId()] = true
		}
		plants := map[string]bool{}
		for _, row := range f.Forage {
			if row == nil || validID(row.GetDefName()) != nil || plants[row.GetDefName()] || len(row.GrowingTwelfths) > 12 {
				return contract("invalid forage plant")
			}
			plants[row.GetDefName()] = true
			twelfths := map[int32]bool{}
			for _, t := range row.GrowingTwelfths {
				if t < 0 || t > 11 || twelfths[t] {
					return contract("invalid forage season")
				}
				twelfths[t] = true
			}
		}
		if water := f.FishableWater; water != nil {
			if len(water.Regions) > 256 {
				return contract("fishable regions exceed bound")
			}
			roots := map[[2]int32]bool{}
			for _, row := range water.Regions {
				if row == nil || !colonyCell(row.Root, v.MapSize) || !combatNumber(row.Population, true) || !combatNumber(row.MaxPopulation, true) || row.Population != nil && row.MaxPopulation != nil && row.GetPopulation() > row.GetMaxPopulation() || row.CellCount != nil && (row.GetCellCount() == 0 || row.GetCellCount() > v.MapSize.GetWidth()*v.MapSize.GetHeight()) {
					return contract("invalid fishable region")
				}
				root := [2]int32{row.Root.GetX(), row.Root.GetZ()}
				if roots[root] {
					return contract("duplicate fishable region")
				}
				roots[root] = true
			}
		}
		return nil
	default:
		return contract("missing food channels outcome")
	}
}
