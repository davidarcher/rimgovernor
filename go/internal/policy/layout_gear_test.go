package policy

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gearTestPlan() LayoutPlan {
	plan := growPlan(LayoutPlan{Zones: coreTestZones()}, 6, 1, TechTierCamp)
	plan, _, _ = SiteRoom(plan, nil, PlannedPrison, coreRoomSize[PlannedPrison])
	return plan
}

func TestGearRoomRolesAreRegistered(t *testing.T) {
	t.Parallel()
	for _, role := range gearRooms {
		if _, ok := coreRoomSize[role]; !ok {
			t.Errorf("%s has no size", role)
		}
		if moduleRoomRoles[role] != RoomRoleStoreroom {
			t.Errorf("%s role %v", role, moduleRoomRoles[role])
		}
		if _, ok := roomOverlay[role]; !ok {
			t.Errorf("%s has no overlay style", role)
		}
	}
}

func TestMilitaryDemandAsksForGearRoomsFromCapacity(t *testing.T) {
	t.Parallel()
	items := ItemFacts{Armor: []Resource{"Apparel_FlakVest"}}
	stock := func(def Resource, quality, hp, count int) GearStock {
		return GearStock{Definition: def, Quality: quality, HPBand: hp, Count: count}
	}
	for _, tc := range []struct {
		name    string
		stored  []GearStock
		weapons int
		want    RoomDemand
	}{
		{"nothing", nil, 0, RoomDemand{Known: true}},
		{"one weapon asks for the armory", nil, 1, RoomDemand{Known: true, Armory: true}},
		{"armor asks for the armory", []GearStock{stock("Apparel_FlakVest", 2, 9, 1)}, 0, RoomDemand{Known: true, Armory: true}},
		{"clothing asks for the wardrobe", []GearStock{stock("Apparel_Parka", 2, 9, 1)}, 0, RoomDemand{Known: true, Wardrobe: true}},
		{"both kinds", []GearStock{stock("Apparel_FlakVest", 2, 9, 2), stock("Apparel_Parka", 2, 9, 3)}, 2, RoomDemand{Known: true, Armory: true, Wardrobe: true}},
		{"poor and worn gear is for the dump", []GearStock{stock("Apparel_Parka", 1, 9, 4), stock("Apparel_FlakVest", 2, 4, 4)}, 0, RoomDemand{Known: true}},
	} {
		gear, err := NewGearStore(items, tc.stored, tc.weapons)
		if err != nil {
			t.Fatalf("%s: store %v", tc.name, err)
		}
		req := storeRequest(1, 1, warehouseZone("a", domain.GeneralRole, Rectangle{X: 10, Z: 10, Width: 3, Height: 3}, 8))
		req.Gear = &gear
		if got := (militaryOwner{}).RoomDemand(req); got != tc.want {
			t.Errorf("%s: %+v, want %+v", tc.name, got, tc.want)
		}
	}
	if _, err := NewGearStore(ItemFacts{}, nil, 9); !errors.Is(err, ErrNoArmorDefs) {
		t.Errorf("a catalog without armor must fail with ErrNoArmorDefs: %v", err)
	}
	if got := (militaryOwner{}).RoomDemand(StoreView{}); got != (RoomDemand{}) {
		t.Errorf("no gear store asks for rooms: %+v", got)
	}
}

// A standing gear room asks for another only once its store is full: while the
// store has room, gear is the store's to hold.
func TestMilitaryDemandAsksForAnotherRoomWhenTheStoreIsFull(t *testing.T) {
	t.Parallel()
	r := gearField(t, nil)
	gear, err := NewGearStore(ItemFacts{Armor: []Resource{"Apparel_FlakVest"}}, []GearStock{{Definition: "Apparel_Parka", Quality: 2, HPBand: 9, Count: 4}}, 3)
	if err != nil {
		t.Fatal(err)
	}
	r.Gear = &gear
	armoryRoom := Rectangle{X: 52, Z: 40, Width: 5, Height: 5}
	zone := func(role string, room Rectangle, used int) StockpileZone {
		cells := rectCells(room)
		return StockpileZone{ID: "Zone_" + role, Role: role + ":x", Cells: cells, Stored: cells[:used], Filter: gear.Armory, Priority: domain.PreferredPriority}
	}
	r.Zones = []StockpileZone{zone("armory", armoryRoom, 10), zone("wardrobe", Rectangle{X: 40, Z: 50, Width: 5, Height: 5}, 10)}
	if got := (militaryOwner{}).RoomDemand(r); got.Armory || got.Wardrobe {
		t.Fatalf("stores with room: %+v", got)
	}
	r.Zones = []StockpileZone{zone("armory", armoryRoom, 25), zone("wardrobe", Rectangle{X: 40, Z: 50, Width: 5, Height: 5}, 25)}
	if got := (militaryOwner{}).RoomDemand(r); !got.Armory || !got.Wardrobe {
		t.Fatalf("full stores: %+v", got)
	}
	// Applied to layout's demand, the declared reading stands.
	if got := DeclareStores(r).Apply(RoomDemand{}); !got.Armory || !got.Wardrobe || !got.Known {
		t.Fatalf("applied: %+v", got)
	}
}

// A planned gear room not yet standing will take gear out of the warehouse:
// the planner holds back the further storage room until it stands.
func TestGearRoomPendingHoldsBackAnotherStorageRoom(t *testing.T) {
	t.Parallel()
	gear, err := NewGearStore(ItemFacts{Armor: []Resource{"Apparel_FlakVest"}}, nil, 3)
	if err != nil {
		t.Fatal(err)
	}
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	req := storeRequest(1, 1, warehouseZone("a", domain.GeneralRole, first, 8))
	req.Gear = &gear
	req.Layout.Rooms = append(req.Layout.Rooms, PlannedRoom{Role: PlannedArmory, Region: RectRegion(Rectangle{X: 30, Z: 10, Width: 3, Height: 3}), Door: domain.Cell{X: 31, Z: 9}})
	if got := storageDemand(req); got.Storage != 0 {
		t.Fatalf("armory planned, not standing: %+v", got)
	}
	req.Rooms.Rooms = append(req.Rooms.Rooms, Room{ID: "armory", Enclosed: domain.Known(true), Cells: rectCells(Rectangle{X: 30, Z: 10, Width: 3, Height: 3})})
	if got := storageDemand(req); got.Storage != 2 {
		t.Fatalf("armory standing: %+v", got)
	}
}
