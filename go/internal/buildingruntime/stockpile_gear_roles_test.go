package buildingruntime

import (
	"context"
	"errors"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A snapshot over a recorded colony (#1774): layout planned an armory over
// (2..6, 2..6). Until its room stands the review owes its shell and plans no
// zone; once the room stands the armory zone fills it, and the old 2x2 weapons
// zone is deleted.
func TestArmoryShellsThenFillsItsRoomAndRetiresTheOldZone(t *testing.T) {
	t.Parallel()
	projection, _ := mealSpotColony(1.6)
	projection.Facts.Items = policy.ItemFacts{Armor: []policy.Resource{"Apparel_FlakVest"}}
	armory := policy.PlannedRoom{Role: policy.PlannedArmory, Interior: policy.Rectangle{X: 2, Z: 2, Width: 5, Height: 5}, Door: domain.Cell{X: 7, Z: 4}}
	projection.LayoutPlan = domain.Known(policy.LayoutPlan{Rooms: []policy.PlannedRoom{armory}})
	projection.Facts.CurrentConstruction = ringConstruction(nil)
	gear, err := policy.NewGearStore(projection.Facts.Items, nil, 0)
	if err != nil {
		t.Fatal(err)
	}
	request := withoutOpening(stockpileRequest(projection, nil, nil, domain.Unknown[map[string]bool](), nil, &gear, nil, nil))
	if len(request.Shells) != 1 || request.Shells[0] != policy.PlannedArmory {
		t.Fatalf("shells %v", request.Shells)
	}
	for _, e := range policy.PlanStockpileMaintenance(request).Edits {
		if e.Kind == policy.StockpileCreate {
			t.Fatalf("zone planned before the room stands: %+v", e)
		}
	}

	var room []domain.Cell
	for x := int32(2); x < 7; x++ {
		for z := int32(2); z < 7; z++ {
			room = append(room, domain.Cell{X: x, Z: z})
		}
	}
	rooms, _ := projection.Rooms.Value()
	rooms.Rooms = append(rooms.Rooms, policy.Room{ID: "Room_5", Role: domain.Known(policy.RoomRoleStoreroom), Enclosed: domain.Known(true), Cells: room})
	projection.Rooms = domain.Known(rooms)
	projection.Facts.CurrentConstruction = ringConstruction(&armory)
	zoneOn(projection, "Zone_7", false, domain.Cell{X: 20, Z: 20}, domain.Cell{X: 21, Z: 20}, domain.Cell{X: 20, Z: 21}, domain.Cell{X: 21, Z: 21})
	owned := []store.OwnedZone{{ID: "Zone_7", Kind: domain.StockpileZone, Role: domain.WeaponsRole, Filter: domain.GeneralFilter(), Priority: domain.PreferredPriority}}
	request = withoutOpening(stockpileRequest(projection, owned, nil, domain.Unknown[map[string]bool](), nil, &gear, nil, nil))
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

// A catalog that names no armor fails the stockpile review with the named
// error instead of quietly planning no gear storage (#1804).
func TestGearStoreFailsWithoutCatalogArmor(t *testing.T) {
	t.Parallel()
	projection, _ := mealSpotColony(1.6)
	projection.Facts.Items = policy.ItemFacts{}
	var r Rounder
	if _, err := r.gearStore(context.Background(), domain.GenerationSnapshot{}, projection); !errors.Is(err, policy.ErrNoArmorDefs) {
		t.Fatalf("gearStore error %v", err)
	}
}

// The materials yard is a plan reservation viewed as an Outdoor room (#2215):
// until its fence ring stands the stockpile review owes its shell.
func TestYardShellIsOwedUntilItsRingStands(t *testing.T) {
	t.Parallel()
	projection, _ := mealSpotColony(1.6)
	plan := policy.LayoutPlan{Reservations: []policy.LayoutReservation{{Kind: policy.ReserveYard, Area: policy.Rectangle{X: 30, Z: 30, Width: policy.YardW + 2, Height: policy.YardH + 2}}}}
	projection.LayoutPlan = domain.Known(plan)
	projection.Facts.CurrentConstruction = ringConstruction(nil)
	request := withoutOpening(stockpileRequest(projection, nil, nil, domain.Unknown[map[string]bool](), nil, nil, nil, nil))
	if !slices.Contains(request.Shells, policy.PlannedYard) {
		t.Fatalf("shells %v, want the yard", request.Shells)
	}
}
