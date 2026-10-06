package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateColonyAcquisition binds the acquisition census: a hunt row is a
// hunt of one unit of something edible, or of a recognised pest (#247),
// which is inedible with no nutrition.
func validateColonyAcquisition(v *o.ColonyFactsSnapshot) error {
	if v.GetPendingHunts() > 65536 {
		return contract("acquisition census exceeds bound")
	}
	seen := map[string]bool{}
	for _, row := range v.Acquisition {
		if row == nil {
			return contract("missing acquisition row")
		}
		source := row.Source
		if !validRef(source) || seen[source.GetId()] || !refSnapshot(row.SourceSnapshot, source, v.Context) || validID(row.GetResource()) != nil || row.Hunt == nil || row.Tree == nil || row.Food == nil || row.Designated == nil || row.Yield == nil || row.GetYield() <= 0 || !combatNumber(row.Yield, true) || row.NutritionYield == nil || !combatNumber(row.NutritionYield, true) || !row.GetFood() && row.GetNutritionYield() != 0 || row.GetHunt() && (row.GetTree() || row.GetYield() != 1) {
			return contract("invalid acquisition source or yield")
		}
		if row.GetHunt() && (row.RevengeChance == nil || !combatNumber(row.RevengeChance, true) || row.GetRevengeChance() > 1 || row.HerdSize == nil || row.GetHerdSize() == 0 || row.GetHerdSize() > 65536 || row.MeleeOnly == nil || row.Downed == nil || row.BodySize != nil && !combatNumber(row.BodySize, true)) {
			return contract("missing or invalid hunt cost facts")
		}
		if row.GetHunt() && (v.HuntCensus == nil || row.Fogged == nil || row.InMentalState == nil) {
			return contract("hunt row without the hunt census or its fogged and mental-state facts")
		}
		// A designated row carries the tick native first saw it; an
		// undesignated row none (#1043). taken is always reported.
		if row.Taken == nil || row.GetDesignated() != (row.DesignatedTick != nil) || row.DesignatedTick != nil && (row.GetDesignatedTick() < 0 || row.GetDesignatedTick() > v.Context.GetTick()) {
			return contract("invalid acquisition designation age or taken")
		}
		seen[source.GetId()] = true
	}
	if err := validateHuntCensus(v.HuntCensus); err != nil {
		return err
	}
	for _, issue := range v.Issues {
		if issue.GetField() == "pending_hunts" && v.PendingHunts != nil || issue.GetField() == "acquisition" && len(v.Acquisition) > 0 || issue.GetField() == "pending_wood_units" && v.PendingWoodUnits != nil || issue.GetField() == "pending_food_nutrition" && v.PendingFoodNutrition != nil {
			return contract("unavailable acquisition contains facts")
		}
	}
	return nil
}

// validateHuntCensus binds the raw hunt census: bounded, every colonist and bench named
// with the facts the hunt gate reads stated.
func validateHuntCensus(c *o.HuntCensus) error {
	if c == nil {
		return nil
	}
	if len(c.Hunters) > 4096 || len(c.Benches) > 4096 {
		return contract("hunt census exceeds bound")
	}
	for _, h := range c.Hunters {
		if h == nil || validID(h.GetPawnId()) != nil || h.Position == nil || h.Downed == nil || h.InMentalState == nil || h.HuntingActive == nil || h.CookingActive == nil || h.HuntingPriority == nil {
			return contract("invalid hunter facts")
		}
	}
	for _, b := range c.Benches {
		if b == nil || validID(b.GetBenchId()) != nil || b.Usable == nil {
			return contract("invalid hunt butcher bench")
		}
	}
	return nil
}
