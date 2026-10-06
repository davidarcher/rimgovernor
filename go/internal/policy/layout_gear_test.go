package policy

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gearTestPlan() LayoutPlan {
	plan := growPlan(LayoutPlan{Zones: coreTestZones()}, 6, 1, BuildTierCamp)
	plan, _, _ = SiteRoom(plan, nil, PlannedPrison, coreRoomSize[PlannedPrison])
	return plan
}

func roomOf(p LayoutPlan, role PlannedRole) (PlannedRoom, bool) {
	for _, r := range p.Rooms {
		if r.Role == role {
			return r, true
		}
	}
	return PlannedRoom{}, false
}

// touches reports two rooms sharing a wall: their interiors a wall apart.
func touches(a, b Rectangle) bool {
	xGap := max(a.X-(b.X+b.Width), b.X-(a.X+a.Width))
	zGap := max(a.Z-(b.Z+b.Height), b.Z-(a.Z+a.Height))
	return xGap == 1 && zGap < 0 || zGap == 1 && xGap < 0
}

func TestGearRoomsNeedDemand(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	plan := gearTestPlan()
	for _, role := range gearRooms {
		if _, ok := roomOf(plan, role); ok {
			t.Fatalf("%s planned with no demand", role)
		}
	}
	if owed := GearRoomsOwed(plan, RoomDemand{}); len(owed) != 0 {
		t.Fatalf("owed %v with no demand", owed)
	}
	if same, added, _ := growGearRooms(plan, RoomDemand{}, nil); added || len(same.Rooms) != len(plan.Rooms) {
		t.Fatal("rooms added with no demand")
	}
}

// gearBesidePlan is a hallway with a storage room and a workshop on it and free
// ground either side of each: a generated base packs its rooms, so whether
// an anchor has a free side depends on its layout, not on the gear siting.
func gearBesidePlan() LayoutPlan {
	spine := []SpineSegment{{From: domain.Cell{X: 30, Z: 59}, To: domain.Cell{X: 88, Z: 59}}}
	return LayoutPlan{
		Zones:     coreTestZones(),
		Spine:     spine,
		Entrances: spineEntrances(spine),
		Rooms: []PlannedRoom{
			hallRoom(PlannedStorage, 40, 59, 7, 5, false),
			hallRoom(PlannedWorkshop, 70, 59, 7, 5, false),
		},
	}
}

func TestGearRoomsSitBesideTheirAnchorAndRoute(t *testing.T) {
	t.Parallel()
	plan := gearBesidePlan()
	grown, added, _ := growGearRooms(plan, RoomDemand{Armory: true, Wardrobe: true}, nil)
	if !added || len(grown.Rooms) != len(plan.Rooms)+2 {
		t.Fatalf("added=%v rooms %d -> %d", added, len(plan.Rooms), len(grown.Rooms))
	}
	for i := range plan.Rooms {
		if !grown.Rooms[i].Same(plan.Rooms[i]) {
			t.Fatalf("room %d moved", i)
		}
	}
	for role, anchor := range map[PlannedRole]PlannedRole{PlannedArmory: PlannedStorage, PlannedWardrobe: PlannedWorkshop} {
		room, ok := roomOf(grown, role)
		host, hok := roomOf(grown, anchor)
		if !ok || !hok {
			t.Fatalf("%s %v %s %v", role, ok, anchor, hok)
		}
		if !touches(room.Interior, host.Interior) {
			t.Errorf("%s %+v not beside %s %+v", role, room.Interior, anchor, host.Interior)
		}
		if room.Link != nil && !inWall(host.Interior, *room.Link) {
			t.Errorf("%s link %v is not in the %s wall", role, *room.Link, anchor)
		}
	}
	if _, err := CheckRoutes(grown); err != nil {
		t.Fatal(err)
	}
	if again, added, _ := growGearRooms(grown, RoomDemand{Armory: true, Wardrobe: true}, nil); added || len(again.Rooms) != len(grown.Rooms) {
		t.Fatal("a standing room answers its demand for good")
	}
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
	if got := (militaryOwner{}).RoomDemand(StorageRequest{}); got != (RoomDemand{}) {
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
	req.Layout.Rooms = append(req.Layout.Rooms, PlannedRoom{Role: PlannedArmory, Interior: Rectangle{X: 30, Z: 10, Width: 3, Height: 3}, Door: domain.Cell{X: 31, Z: 9}})
	if got := PlanStorage(req).RoomDemand; got.Storage != 0 {
		t.Fatalf("armory planned, not standing: %+v", got)
	}
	req.Rooms.Rooms = append(req.Rooms.Rooms, Room{ID: "armory", Enclosed: domain.Known(true), Cells: rectCells(Rectangle{X: 30, Z: 10, Width: 3, Height: 3})})
	if got := PlanStorage(req).RoomDemand; got.Storage != 2 {
		t.Fatalf("armory standing: %+v", got)
	}
}
