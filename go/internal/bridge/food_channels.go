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
		if f == nil {
			return contract("incomplete food channels")
		}
		if f.GetPollutedCells() > v.MapSize.GetWidth()*v.MapSize.GetHeight() {
			return contract("pollution exceeds the map")
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
			if !combatNumber(water.ResearchBaseCost, true) || !combatNumber(water.ResearchProgress, true) || !combatNumber(water.ResearchCostFactor, true) ||
				!combatNumber(water.ResearchPointsPerWorkTick, true) || !combatNumber(water.ResearchDifficultySpeedFactor, true) || !combatNumber(water.BaseFishingDurationTicks, true) ||
				len(water.ResearcherSpeeds) > 256 || len(water.Fishers) > 256 {
				return contract("invalid fishing research")
			}
			for _, speed := range water.ResearcherSpeeds {
				if !combatNumber(&speed, true) {
					return contract("invalid researcher speed")
				}
			}
			for _, f := range water.Fishers {
				if f == nil || !combatNumber(f.FishingYield, true) || !combatNumber(f.FishingSpeed, true) {
					return contract("invalid fisher")
				}
			}
			roots := map[[2]int32]bool{}
			for _, row := range water.Regions {
				if row == nil || !colonyCell(row.Root, v.MapSize) || !combatNumber(row.Population, true) || !combatNumber(row.MaxPopulation, true) || row.Population != nil && row.MaxPopulation != nil && row.GetPopulation() > row.GetMaxPopulation() || row.CellCount != nil && (row.GetCellCount() == 0 || row.GetCellCount() > v.MapSize.GetWidth()*v.MapSize.GetHeight()) {
					return contract("invalid fishable region")
				}
				root := [2]int32{row.Root.GetX(), row.Root.GetZ()}
				if !combatNumber(row.NutritionPerFish, false) || !combatNumber(row.FishPerBatch, false) || !combatNumber(row.WorkTicksPerBatch, false) || !combatNumber(row.YieldCurveValue, true) || !combatNumber(row.NearestDistanceSquared, true) || len(row.Zones) > 256 {
					return contract("invalid fishing rates or footprint")
				}
				seenCells := map[[2]int32]bool{}
				for _, fc := range row.Cells {
					if fc == nil || !colonyCell(fc.Cell, v.MapSize) || !combatNumber(fc.DistanceSquared, true) {
						return contract("invalid fishing cell")
					}
					key := [2]int32{fc.Cell.GetX(), fc.Cell.GetZ()}
					if seenCells[key] {
						return contract("duplicate fishing cell")
					}
					seenCells[key] = true
				}
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
