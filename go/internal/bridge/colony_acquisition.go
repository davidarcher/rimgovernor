package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// validateColonyAcquisition binds the acquisition census: a hunt row is a
// hunt of one unit of something edible, or of a recognised pest (#247),
// which is inedible with no nutrition.
func validateColonyAcquisition(v *o.ColonyFactsSnapshot) error {
	if v.GetPendingHunts() > 65536 || len(v.Acquisition) > 256 {
		return contract("acquisition census exceeds bound")
	}
	seen := map[string]bool{}
	for _, row := range v.Acquisition {
		if row == nil {
			return contract("missing acquisition row")
		}
		source := row.Source
		if source == nil || validID(source.GetId()) != nil || seen[source.GetId()] || validID(source.GetDefName()) != nil || source.MapId == nil || source.GetMapId() != v.Context.Identity.GetMapId() || !colonyCell(source.Position, v.MapSize) || source.Snapshot == nil || source.Snapshot.GetEntityId() != source.GetId() || validID(source.Snapshot.GetToken()) != nil || !proto.Equal(source.Snapshot.Context, v.Context) || validID(row.GetResource()) != nil || row.Hunt == nil || row.Tree == nil || row.Food == nil || row.Designated == nil || row.Yield == nil || row.GetYield() <= 0 || !combatNumber(row.Yield, true) || row.NutritionYield == nil || !combatNumber(row.NutritionYield, true) || !row.GetFood() && row.GetNutritionYield() != 0 || row.GetHunt() && (row.GetTree() || row.GetYield() != 1 || !row.GetFood() && !policy.PestDefinition(policy.Resource(source.GetDefName()))) {
			return contract("invalid acquisition source or yield")
		}
		seen[source.GetId()] = true
	}
	for _, issue := range v.Issues {
		if issue.GetField() == "pending_hunts" && v.PendingHunts != nil || issue.GetField() == "acquisition" && len(v.Acquisition) > 0 || issue.GetField() == "pending_wood_units" && v.PendingWoodUnits != nil || issue.GetField() == "pending_food_nutrition" && v.PendingFoodNutrition != nil {
			return contract("unavailable acquisition contains facts")
		}
	}
	return nil
}
