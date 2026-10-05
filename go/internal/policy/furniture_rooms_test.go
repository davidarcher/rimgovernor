package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A bedroom with a standing double bed is planned around that bed.
func TestFurnitureRoomsPlanTheStandingBed(t *testing.T) {
	interior := Rectangle{X: 0, Z: 0, Width: 6, Height: 5}
	door := domain.Cell{X: 1, Z: -1}
	plan, ok := PlanInterior(InteriorRoom{Role: RoomRoleBedroom, Interior: interior, Doors: []domain.Cell{door}, Shapes: testShapes}, testShapes.Defs["DoubleBed"])
	if !ok {
		t.Fatal("no double-bed plan")
	}
	bed := plan.Pieces[0]
	b, err := domain.NewBuilding("DoubleBed", domain.Cell{X: bed.Rect.X, Z: bed.Rect.Z}, bed.Rot, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	census := CurrentConstruction{Colony: true, Buildings: []CurrentBuilding{{ID: "Bed_1", Building: b, Cells: rectCells(bed.Rect)}}}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "Room_1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Cells: rectCells(interior)}}}
	got := FurnitureRooms(rooms, census, []SiteCell{{Cell: door, Doorway: domain.Known(true)}})
	if len(got) != 1 || len(got[0].Room.Standing) != 1 || got[0].Room.Standing[0] != "DoubleBed" {
		t.Fatalf("rooms %+v", got)
	}
}
