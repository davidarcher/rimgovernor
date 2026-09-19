package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ColonyAcquisition decodes the native-approved harvest/hunt source census
// of one colony facts read; unknown when the acquisition section failed.
func ColonyAcquisition(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.AcquisitionSource] {
	if hasIssue(v.Issues, "acquisition") {
		return domain.Unknown[[]policy.AcquisitionSource]()
	}
	rows := []policy.AcquisitionSource{}
	for _, row := range v.Acquisition {
		rows = append(rows, policy.AcquisitionSource{ID: row.Source.GetId(), Resource: row.GetResource(), Token: row.Source.Snapshot.GetToken(), Definition: row.Source.GetDefName(), Cell: domain.Cell{X: row.Source.Position.GetX(), Z: row.Source.Position.GetZ()}, Hunt: row.GetHunt(), Tree: row.GetTree(), Food: row.GetFood(), Designated: row.GetDesignated(), Yield: row.GetYield(), NutritionYield: row.GetNutritionYield(), RevengeChance: row.GetRevengeChance(), HerdSize: int(row.GetHerdSize()), MeleeOnly: row.GetMeleeOnly(), Downed: row.GetDowned(), WeaponRange: row.GetWeaponRange()})
	}
	return domain.Known(rows)
}

func colonyAcquisition(v *o.ColonyFactsSnapshot, r *ColonyProjection) {
	r.Acquisition = ColonyAcquisition(v)
	if !hasIssue(v.Issues, "pending_food_nutrition") {
		r.PendingFoodNutrition = optional(v.PendingFoodNutrition)
	}
	if !hasIssue(v.Issues, "pending_hunts") && v.PendingHunts != nil {
		r.PendingHunts = domain.Known(int(v.GetPendingHunts()))
	}
	if !hasIssue(v.Issues, "pending_wood_units") {
		r.PendingWoodUnits = optional(v.PendingWoodUnits)
	}
}
