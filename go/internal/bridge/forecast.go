package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

func ValidateForecast(v *o.ForecastFacts, human *o.FoodSupplyFacts) error {
	if v == nil || len(v.AnimalIds) > 256 || len(v.Crops) > 256 || len(v.Patients) > 256 {
		return contract("forecast exceeds bound")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	if err := colonyCounts(v.Completeness, 1+len(v.AnimalIds)+len(v.Crops)+len(v.Patients), 769); err != nil {
		return err
	}
	if v.Completeness.GetFiltered() != 0 {
		return contract("filtered forecast")
	}
	if err := ValidateFoodSupply(v.CombinedFoodSupply); err != nil {
		return err
	}
	consumers := map[string]bool{}
	rates := map[string]*float64{}
	for _, row := range v.CombinedFoodSupply.Consumers {
		consumers[row.GetPawnId()] = true
		rates[row.GetPawnId()] = row.NutritionPerDay
	}
	partition := map[string]bool{}
	for _, id := range v.AnimalIds {
		if !consumers[id] || partition[id] {
			return contract("invalid animal census")
		}
		partition[id] = true
	}
	if human != nil {
		if err := ValidateFoodSupply(human); err != nil {
			return err
		}
		for _, row := range human.Consumers {
			id := row.GetPawnId()
			if !consumers[id] || partition[id] {
				return contract("inconsistent combined consumers")
			}
			partition[id] = true
			if rate := rates[id]; rate != nil && row.NutritionPerDay != nil && *rate != *row.NutritionPerDay {
				return contract("conflicting human food demand")
			}
		}
		if len(partition) != len(consumers) {
			return contract("unclassified combined consumer")
		}
	}
	zones := map[string]bool{}
	for _, row := range v.Crops {
		if row == nil || validID(row.GetZoneId()) != nil || zones[row.GetZoneId()] || row.Crop != nil && validID(row.GetCrop()) != nil || row.Product != nil && validID(row.GetProduct()) != nil {
			return contract("invalid crop forecast")
		}
		zones[row.GetZoneId()] = true
		if err := pawnsIssues(row.Issues, row.ProtoReflect()); err != nil {
			return err
		}
		for _, number := range []*float64{row.SowWork, row.HarvestWork, row.StandingYield} {
			if !combatNumber(number, true) {
				return contract("invalid crop quantity")
			}
		}
	}
	patients := map[string]bool{}
	for _, row := range v.Patients {
		if row == nil || validID(row.GetPawnId()) != nil || patients[row.GetPawnId()] {
			return contract("invalid patient forecast")
		}
		patients[row.GetPawnId()] = true
		if err := pawnsIssues(row.Issues, row.ProtoReflect()); err != nil {
			return err
		}
		for _, number := range []*float64{row.BleedRatePerDay, row.HoursUntilDeathFromBloodLoss} {
			if !combatNumber(number, true) {
				return contract("invalid medical quantity")
			}
		}
		for _, number := range []*float64{row.Mood, row.MoodTarget, row.MinorBreakThreshold, row.MajorBreakThreshold, row.ExtremeBreakThreshold} {
			if !combatNumber(number, false) {
				return contract("invalid mood quantity")
			}
		}
	}
	if human != nil {
		for _, row := range human.Consumers {
			if !patients[row.GetPawnId()] {
				return contract("human patient forecast missing")
			}
		}
	}
	for _, id := range v.AnimalIds {
		if patients[id] {
			return contract("animal in human patient census")
		}
	}
	return nil
}
