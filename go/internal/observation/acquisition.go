package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ColonyAcquisition decodes the native-approved harvest/hunt source census
// of one colony facts read; unknown when the acquisition section failed.
func ColonyAcquisition(v *o.ColonyFactsSnapshot, tables bridge.Tables) domain.Fact[[]policy.AcquisitionSource] {
	if hasIssue(v.Issues, "acquisition") || !headed(tables, v.Acquisition, (*o.AcquisitionFacts).GetSource) {
		return domain.Unknown[[]policy.AcquisitionSource]()
	}
	races, err := tables.Catalog.AnimalRaces()
	if err != nil {
		return domain.Unknown[[]policy.AcquisitionSource]()
	}
	rows := []policy.AcquisitionSource{}
	for _, row := range v.Acquisition {
		source := tables.Entity(row.Source)
		// An inedible hunt is only ever a pest (#247), a race row's flag.
		race, _ := races.Race(policy.Resource(source.GetDefName()))
		if row.GetHunt() && !row.GetFood() && !race.Pest {
			continue
		}
		rows = append(rows, policy.AcquisitionSource{ID: row.Source.GetId(), Resource: row.GetResource(), Token: row.SourceSnapshot.GetToken(), Definition: source.GetDefName(), Cell: domain.Cell{X: source.GetPosition().GetX(), Z: source.GetPosition().GetZ()}, Hunt: row.GetHunt(), Tree: row.GetTree(), Food: row.GetFood(), Designated: row.GetDesignated(), Yield: row.GetYield(), NutritionYield: row.GetNutritionYield(), RevengeChance: row.GetRevengeChance(), HerdSize: int(row.GetHerdSize()), MeleeOnly: row.GetMeleeOnly(), Downed: row.GetDowned(), BodySize: row.GetBodySize(), Sleeping: row.GetSleeping(), Predator: row.GetPredator(), Pest: race.Pest, WeaponRange: row.GetWeaponRange(), DesignatedTick: domain.Tick(row.GetDesignatedTick()), Taken: row.GetTaken()})
	}
	return domain.Known(rows)
}

func colonyAcquisition(v *o.ColonyFactsSnapshot, tables bridge.Tables, r *ColonyProjection) {
	r.Acquisition = ColonyAcquisition(v, tables)
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
