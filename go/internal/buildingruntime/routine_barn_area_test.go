package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestBotAreasPlanOnlyAStandingBarn(t *testing.T) {
	plan := policy.LayoutPlan{Reservations: []policy.LayoutReservation{{Kind: policy.ReserveBarn, Area: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 6}}}}
	interior := policy.Rectangle{X: 11, Z: 11, Width: 4, Height: 4}
	barnArea := func(rooms policy.RoomObservation) int {
		var m safeAreaMemory
		projection := observation.ColonyProjection{Rooms: domain.Known(rooms), LayoutPlan: domain.Known(plan)}
		if _, err := m.review("w", projection); err != nil {
			t.Fatal(err)
		}
		for _, edit := range m.take("w") {
			if edit.Operation() == domain.AreaCreate && edit.Key() == policy.BarnAreaKey {
				return len(edit.Cells())
			}
		}
		return 0
	}
	if n := barnArea(policy.RoomObservation{Shapes: testPieceShapes}); n != 0 {
		t.Fatal("a barn that is not a room has no area", n)
	}
	var cells []domain.Cell
	for x := interior.X; x < interior.X+interior.Width; x++ {
		for z := interior.Z; z < interior.Z+interior.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	room := policy.Room{ID: "1", Enclosed: domain.Known(true), Roofed: domain.Known(true), Cells: cells}
	if n := barnArea(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{room}}); n != len(cells) {
		t.Fatal("the Barn area covers the standing barn interior", n)
	}
}
