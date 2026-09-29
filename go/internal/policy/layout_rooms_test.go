package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestPlannedShellsKeepThePlanRectangleAndDoor(t *testing.T) {
	north := coreRoom(ModuleWorkshop, 20, 30, 7, 5, true)
	south := coreRoom(ModuleWorkshop, 30, 30, 7, 5, false)
	plan := LayoutPlan{Rooms: []LayoutRoom{coreRoom(ModuleKitchen, 10, 30, 6, 5, true), north, south}}
	shells := plan.PlannedShells(RoomRoleWorkshop)
	if len(shells) != 2 {
		t.Fatalf("%d workshop shells", len(shells))
	}
	for i, r := range []LayoutRoom{north, south} {
		s := shells[i]
		b := s.Bounds()
		if s.Door() != r.Door || s.Entrance() != r.DoorRot || b.X != r.Interior.X-1 || b.Z != r.Interior.Z-1 || b.Width != r.Interior.Width+2 || b.Height != r.Interior.Height+2 {
			t.Fatalf("shell %d: door %v %v bounds %+v, room %+v", i, s.Door(), s.Entrance(), b, r)
		}
	}
	if len(plan.PlannedShells(RoomRoleTomb)) != 0 {
		t.Fatal("a role the core plans no room for got shells")
	}
}

func TestStarterLayoutsBuildTheFirstBuildablePlannedRoom(t *testing.T) {
	bounds := Bounds{Width: 60, Height: 60}
	taken := coreRoom(ModuleWorkshop, 20, 30, 7, 5, true)
	next := coreRoom(ModuleWorkshop, 20, 30, 7, 5, false)
	var cells []SiteCell
	for x := int32(0); x < bounds.Width; x++ {
		for z := int32(0); z < bounds.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			occupied := x >= taken.Interior.X && x < taken.Interior.X+taken.Interior.Width && z >= taken.Interior.Z && z < taken.Interior.Z+taken.Interior.Height
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(occupied), Zone: domain.Known(false), SupportsLight: domain.Known(true)})
		}
	}
	// The spine hallway is protected; the door's threshold stands on it.
	var hallway []domain.Cell
	for x := int32(10); x < 40; x++ {
		for z := int32(29); z <= 31; z++ {
			hallway = append(hallway, domain.Cell{X: x, Z: z})
		}
	}
	plan := LayoutPlan{Rooms: []LayoutRoom{taken, next}}
	layouts, err := StarterLayouts(StarterRequest{Bounds: bounds, Anchor: domain.Cell{X: 5, Z: 5}, Cells: cells, Protected: hallway, Planned: plan.PlannedShells(RoomRoleWorkshop)})
	if err != nil {
		t.Fatal(err)
	}
	if len(layouts) != 1 {
		t.Fatalf("%d layouts, want only the planned room", len(layouts))
	}
	want, _ := next.Footprint()
	if !domain.SameRoomFootprint(layouts[0].Shell, want) || layouts[0].Shell.Door() != next.Door {
		t.Fatalf("built %+v door %v, want the planned room door %v", layouts[0].Shell.Bounds(), layouts[0].Shell.Door(), next.Door)
	}
	// Nothing planned is buildable: the search runs as before.
	blocked := StarterRequest{Bounds: bounds, Anchor: domain.Cell{X: 5, Z: 5}, Cells: cells, Protected: hallway, Planned: plan.PlannedShells(RoomRoleWorkshop)[:1]}
	if layouts, err := StarterLayouts(blocked); err != nil || len(layouts) == 0 {
		t.Fatalf("fallback search: %d layouts, %v", len(layouts), err)
	}
}

// The starter shell is the plan's storage room (#1177): its rectangle and
// spine door, bunks clear of the door aisle; blocked, the search runs.
func TestStarterShellStandsOnThePlannedStorageRoom(t *testing.T) {
	bounds := Bounds{Width: 60, Height: 60}
	storage := coreRoom(ModuleStorage, 20, 30, 9, 7, true)
	cells := func(blocked bool) []SiteCell {
		var cells []SiteCell
		for x := int32(0); x < bounds.Width; x++ {
			for z := int32(0); z < bounds.Height; z++ {
				in := blocked && x == storage.Interior.X+3 && z == storage.Interior.Z+3
				cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(in), Zone: domain.Known(false), SupportsLight: domain.Known(true)})
			}
		}
		return cells
	}
	var hallway []domain.Cell
	for x := int32(10); x < 40; x++ {
		for z := int32(29); z <= 31; z++ {
			hallway = append(hallway, domain.Cell{X: x, Z: z})
		}
	}
	plan := LayoutPlan{Rooms: []LayoutRoom{coreRoom(ModuleKitchen, 10, 30, 6, 5, true), storage}}
	request := StarterRequest{Bounds: bounds, Anchor: domain.Cell{X: 5, Z: 5}, Cells: cells(false), Protected: hallway, Planned: plan.PlannedShells(RoomRoleStoreroom)}
	layouts, err := StarterLayouts(request)
	if err != nil || len(layouts) != 1 {
		t.Fatalf("%d layouts, %v", len(layouts), err)
	}
	want, _ := storage.Footprint()
	if !domain.SameRoomFootprint(layouts[0].Shell, want) || layouts[0].Shell.Door() != storage.Door {
		t.Fatalf("shell %+v door %v, want storage %+v door %v", layouts[0].Shell.Bounds(), layouts[0].Shell.Door(), want.Bounds(), storage.Door)
	}
	inward := domain.Cell{X: storage.Door.X, Z: storage.Door.Z + 1}
	bunks := PlanShelterBunks(layouts[0], 3, 3, nil)
	if len(bunks.Spots) != 3 {
		t.Fatalf("%d spots fit", len(bunks.Spots))
	}
	for _, anchor := range append(bunks.Beds, bunks.Spots...) {
		for _, c := range BunkFootprint(anchor) {
			if c == inward {
				t.Fatalf("bunk at %v blocks the door aisle %v", anchor, inward)
			}
		}
	}
	request.Cells = cells(true)
	layouts, err = StarterLayouts(request)
	if err != nil || len(layouts) == 0 {
		t.Fatalf("fallback: %d layouts, %v", len(layouts), err)
	}
	if domain.SameRoomFootprint(layouts[0].Shell, want) {
		t.Fatal("a blocked storage room was still sited")
	}
}

func TestShellDoorsPutTheFreezerLinkInBothRings(t *testing.T) {
	kitchen := coreRoom(ModuleKitchen, 10, 30, 6, 5, true)
	freezer := coreRoom(ModuleFreezer, 17, 30, 5, 5, true)
	link := domain.Cell{X: 16, Z: kitchen.Interior.Z + 2}
	freezer.Link = &link
	jail := coreRoom(ModulePrison, 40, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []LayoutRoom{kitchen, freezer, jail}}
	has := func(doors []domain.Cell, c domain.Cell) bool {
		for _, d := range doors {
			if d == c {
				return true
			}
		}
		return false
	}
	if d := plan.ShellDoors(kitchen); len(d) != 2 || !has(d, kitchen.Door) || !has(d, link) {
		t.Fatalf("kitchen doors %v", d)
	}
	if d := plan.ShellDoors(freezer); len(d) != 2 || !has(d, freezer.Door) || !has(d, link) {
		t.Fatalf("freezer doors %v", d)
	}
	if d := plan.ShellDoors(jail); len(d) != 1 || d[0] != jail.Door {
		t.Fatalf("jail doors %v", d)
	}
}

func TestNextPlannedRoomSkipsStandingRooms(t *testing.T) {
	built := coreRoom(ModulePrison, 20, 30, 5, 5, true)
	open := coreRoom(ModulePrison, 30, 30, 5, 5, true)
	plan := LayoutPlan{Rooms: []LayoutRoom{built, open}}
	centre := domain.Cell{X: built.Interior.X + 2, Z: built.Interior.Z + 2}
	rooms := RoomObservation{Rooms: []Room{{ID: "r", Cells: []domain.Cell{centre}, Enclosed: domain.Known(true)}}}
	if r, ok := plan.NextPlannedRoom(ModulePrison, rooms); !ok || r.Interior != open.Interior {
		t.Fatalf("next %+v %v", r, ok)
	}
	if _, ok := plan.NextPlannedRoom(ModuleKitchen, rooms); ok {
		t.Fatal("no kitchen is planned")
	}
}
