package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestKitchenSeparation(t *testing.T) {
	rooms := domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{
		{ID: "k", Cells: []domain.Cell{{X: 1, Z: 1}}, Contents: domain.Known([]Amount{{Resource: "ElectricStove", Count: 1}, {Resource: "ButcherSpot", Count: 1}})},
		{ID: "c", Cells: []domain.Cell{{X: 2, Z: 2}}, Contents: domain.Known([]Amount{{Resource: "Campfire", Count: 1}})},
		{ID: "b", Cells: []domain.Cell{{X: 3, Z: 3}}, Contents: domain.Known([]Amount{{Resource: "TableButcher", Count: 1}})},
	}})
	got, known := KitchenSeparation(rooms).Value()
	if !known || !reflect.DeepEqual(got, []SeparationRoom{{ID: "k", Cells: []domain.Cell{{X: 1, Z: 1}}}}) {
		t.Fatalf("separation: %+v", got)
	}
	if cells := SeparationProtectedCells(rooms, true); !reflect.DeepEqual(cells, []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 2}}) {
		t.Fatalf("butcher placement protects cooking rooms: %+v", cells)
	}
	if cells := SeparationProtectedCells(rooms, false); !reflect.DeepEqual(cells, []domain.Cell{{X: 1, Z: 1}, {X: 3, Z: 3}}) {
		t.Fatalf("cooking placement protects butcher rooms: %+v", cells)
	}
	bedrooms := domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{
		{ID: "starter", Cells: []domain.Cell{{X: 126, Z: 135}}, Contents: domain.Known([]Amount{{Resource: "SleepingSpot", Count: 3}})},
		{ID: "b", Cells: []domain.Cell{{X: 5, Z: 5}}, Role: domain.Known(RoomRoleBedroom), Contents: domain.Known([]Amount{})},
		{ID: "open", Cells: []domain.Cell{{X: 9, Z: 9}}, Contents: domain.Known([]Amount{})},
	}})
	if cells := SeparationProtectedCells(bedrooms, true); !reflect.DeepEqual(cells, []domain.Cell{{X: 5, Z: 5}, {X: 126, Z: 135}}) {
		t.Fatalf("butcher placement: sleeping rooms protected: %+v", cells)
	}
	if cells := SeparationProtectedCells(bedrooms, false); len(cells) != 0 {
		t.Fatalf("cooking placement protects no sleeping room: %+v", cells)
	}
	if _, known := KitchenSeparation(domain.Unknown[RoomObservation]()).Value(); known || SeparationProtectedCells(domain.Unknown[RoomObservation](), true) != nil {
		t.Fatalf("unknown census")
	}
}
