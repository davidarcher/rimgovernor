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
	layouts, err := StarterLayouts(StarterRequest{Bounds: bounds, Anchor: domain.Cell{X: 5, Z: 5}, Cells: cells, Protected: hallway, Shelter: ShelterModule, Planned: plan.PlannedShells(RoomRoleWorkshop)})
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
	blocked := StarterRequest{Bounds: bounds, Anchor: domain.Cell{X: 5, Z: 5}, Cells: cells, Protected: hallway, Shelter: ShelterModule, Planned: plan.PlannedShells(RoomRoleWorkshop)[:1]}
	if layouts, err := StarterLayouts(blocked); err != nil || len(layouts) == 0 {
		t.Fatalf("fallback search: %d layouts, %v", len(layouts), err)
	}
}
