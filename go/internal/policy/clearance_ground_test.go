package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A planned 3x3 room at interior (10,10): ground is (9,9) 5x5.
func groundFixture() (LayoutPlan, RoomObservation) {
	plan := LayoutPlan{Rooms: []PlannedRoom{{Role: PlannedBedroom, Interior: Rectangle{X: 10, Z: 10, Width: 3, Height: 3}, Door: domain.Cell{X: 11, Z: 9}, DoorRot: domain.South}}}
	return plan, RoomObservation{Shapes: testShapes}
}

func playerRow(id, def, class string, min, max domain.Cell, encloses bool) ClearanceTarget {
	return ClearanceTarget{EntityID: id, DefName: def, Class: class, Minimum: min, Maximum: max, Deconstructible: true, InHome: true, Player: true, EnclosesRoom: encloses}
}

func TestPlannedGroundSkipsStandingRooms(t *testing.T) {
	plan, _ := groundFixture()
	if got := PlannedGround(plan, GroundCensus{}); !reflect.DeepEqual(got, []Rectangle{{X: 9, Z: 9, Width: 5, Height: 5}}) {
		t.Fatalf("ground = %v", got)
	}
	if got := PlannedGround(plan, ringWalls(plan, plan.Rooms[0])); len(got) != 0 {
		t.Fatalf("a standing room needs no ground: %v", got)
	}
}

func TestSplitGroundRows(t *testing.T) {
	others, player := SplitGroundRows([]ClearanceTarget{{EntityID: "ruin"}, {EntityID: "mine", Player: true}})
	if len(others) != 1 || others[0].EntityID != "ruin" || len(player) != 1 || player[0].EntityID != "mine" {
		t.Fatalf("split = %v / %v", others, player)
	}
}
