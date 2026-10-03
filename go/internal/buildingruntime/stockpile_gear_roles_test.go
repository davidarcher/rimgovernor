package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A snapshot over a recorded colony (#1774): layout planned an armory over
// (0..4, 0..4). Until its room stands the review owes its shell and plans no
// zone; once the room stands the armory zone fills it, and the old 2x2 weapons
// zone is deleted.
func TestArmoryShellsThenFillsItsRoomAndRetiresTheOldZone(t *testing.T) {
	t.Parallel()
	projection, _ := mealSpotColony(1.6)
	projection.Facts.Items = policy.ItemFacts{Armor: []policy.Resource{"Apparel_FlakVest"}}
	armory := policy.LayoutRoom{Role: policy.ModuleArmory, Interior: policy.Rectangle{X: 0, Z: 0, Width: 5, Height: 5}, Door: domain.Cell{X: 5, Z: 2}}
	projection.LayoutPlan = domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{armory}})
	gear, ok, err := policy.NewGearStore(projection.Facts.Items, nil, 0)
	if err != nil || !ok {
		t.Fatal(ok, err)
	}
	request := withoutOpening(stockpileRequest(projection, nil, nil, domain.Unknown[map[string]bool](), nil, &gear))
	if len(request.Shells) != 1 || request.Shells[0] != policy.ModuleArmory {
		t.Fatalf("shells %v", request.Shells)
	}
	for _, e := range policy.PlanStockpileMaintenance(request).Edits {
		if e.Kind == policy.StockpileCreate {
			t.Fatalf("zone planned before the room stands: %+v", e)
		}
	}

	var room []domain.Cell
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 5; z++ {
			room = append(room, domain.Cell{X: x, Z: z})
		}
	}
	rooms, _ := projection.Rooms.Value()
	rooms.Rooms = append(rooms.Rooms, policy.Room{ID: "Room_5", Role: domain.Known(policy.RoomRoleStoreroom), Enclosed: domain.Known(true), Cells: room})
	projection.Rooms = domain.Known(rooms)
	zoneOn(projection, "Zone_7", false, domain.Cell{X: 20, Z: 20}, domain.Cell{X: 21, Z: 20}, domain.Cell{X: 20, Z: 21}, domain.Cell{X: 21, Z: 21})
	owned := []store.OwnedZone{{ID: "Zone_7", Kind: domain.StockpileZone, Role: domain.WeaponsRole, Filter: domain.GeneralFilter(), Priority: domain.PreferredPriority}}
	request = withoutOpening(stockpileRequest(projection, owned, nil, domain.Unknown[map[string]bool](), nil, &gear))
	request.Colonists = domain.Known(int64(100))
	if len(request.Shells) != 0 {
		t.Fatalf("shells %v with the room standing", request.Shells)
	}
	deleted, created := false, 0
	for _, e := range policy.PlanStockpileMaintenance(request).Edits {
		deleted = deleted || e.Kind == policy.StockpileDelete && e.Zone == "Zone_7"
		if e.Kind == policy.StockpileCreate && e.Role == "armory:Room_5" {
			created = len(e.Cells)
		}
	}
	if !deleted || created != 25 {
		t.Fatalf("deleted %v, armory cells %d", deleted, created)
	}
}
