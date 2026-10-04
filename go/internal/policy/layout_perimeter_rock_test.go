package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// A rock face cutting across the ring closes its own cells: the wall covers
// only the open ones, none on rock, and gates and the killbox stay on open
// ground (#1592).
func TestPerimeterSnapsOntoRockFace(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	ring := plainsRing(t)
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	plains := perimeterPlan(t, open)
	// Rock fills the east ring cells beyond its second column.
	edge := ring.X + ring.Width - 2
	p := perimeterPlan(t, func(x, z int32) SurveyCell {
		if x >= edge {
			return SurveyCell{Rock: true}
		}
		return open(x, z)
	})
	if got, base := len(reservedCells(p, ReservePerimeter)), len(reservedCells(plains, ReservePerimeter)); got >= base {
		t.Fatal("rock-backed ring is no cheaper", got, base)
	}
	for c := range reservedCells(p, ReservePerimeter) {
		if c.X >= edge {
			t.Fatal("wall on rock", c)
		}
	}
	for _, kind := range []ReservationKind{ReserveGate, ReserveKillbox} {
		for c := range reservedCells(p, kind) {
			if c.X >= edge {
				t.Fatal("opening on rock", kind, c)
			}
		}
	}
}
