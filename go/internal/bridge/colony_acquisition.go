package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func validateColonyAcquisition(v *o.ColonyFactsSnapshot) error {
	if len(v.Acquisition) > 256 {
		return contract("acquisition census exceeds bound")
	}
	seen := map[string]bool{}
	for _, row := range v.Acquisition {
		if row == nil {
			return contract("missing acquisition row")
		}
		source := row.Source
		if source == nil || validID(source.GetId()) != nil || seen[source.GetId()] || validID(source.GetDefName()) != nil || source.MapId == nil || source.GetMapId() != v.Context.Identity.GetMapId() || !colonyCell(source.Position, v.MapSize) || source.Snapshot == nil || source.Snapshot.GetEntityId() != source.GetId() || validID(source.Snapshot.GetToken()) != nil || !proto.Equal(source.Snapshot.Context, v.Context) || validID(row.GetResource()) != nil || row.Tree == nil || row.Food == nil || row.Designated == nil || row.Yield == nil || row.GetYield() <= 0 || !combatNumber(row.Yield, true) || row.NutritionYield == nil || !combatNumber(row.NutritionYield, true) || !row.GetFood() && row.GetNutritionYield() != 0 {
			return contract("invalid acquisition source or yield")
		}
		seen[source.GetId()] = true
	}
	for _, issue := range v.Issues {
		if issue.GetField() == "acquisition" && len(v.Acquisition) > 0 || issue.GetField() == "pending_wood_units" && v.PendingWoodUnits != nil || issue.GetField() == "pending_food_nutrition" && v.PendingFoodNutrition != nil {
			return contract("unavailable acquisition contains facts")
		}
	}
	return nil
}
