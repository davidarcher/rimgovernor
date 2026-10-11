package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// lShape is a 4x4 block missing its north-east 2x2 corner.
func lShape() Region {
	return RegionOfRuns([]RowRun{
		{Z: 10, X: 10, Length: 2}, {Z: 11, X: 10, Length: 2},
		{Z: 12, X: 10, Length: 4}, {Z: 13, X: 10, Length: 4},
	})
}

func TestRetiredLShapedGroundDemolishedInFullThenDropped(t *testing.T) {
	l := lShape()
	plan := LayoutPlan{RetiredGround: []Region{l}}
	wall := func(id string, x, z int32) ClearanceTarget {
		c := domain.Cell{X: x, Z: z}
		return ClearanceTarget{EntityID: id, DefName: "Wall", Minimum: c, Maximum: c, Player: true, EnclosesRoom: true}
	}
	notch := wall("notch", 13, 10) // inside the bounds, outside the L
	inL := []ClearanceTarget{wall("a", 10, 10), wall("b", 13, 13)}
	rows := append([]ClearanceTarget{notch}, inL...)

	rg := RetiredGroundOf(plan)
	step, ok := retiredGroundStep(rows, nil, plan.RetiredGround, RoomObservation{}, rg)
	if !ok || step.Phase != GroundWalls || len(step.Targets) != 2 {
		t.Fatalf("both walls on the L and not the notch come down, got %+v ok=%v", step, ok)
	}
	for _, tg := range step.Targets {
		if tg.EntityID == "notch" {
			t.Fatal("a wall in the L's bounding box but outside the L is not retired ground")
		}
	}
	if len(RetiredGroundDone(plan, rows, nil)) != 0 {
		t.Fatal("ground with walls standing is not done")
	}

	floors := []ClearanceFloor{{Cell: domain.Cell{X: 12, Z: 12}}, {Cell: domain.Cell{X: 13, Z: 10}}}
	step, ok = retiredGroundStep(nil, floors, plan.RetiredGround, RoomObservation{}, rg)
	if !ok || step.Phase != GroundFloors || len(step.Floors) != 1 || step.Floors[0].Cell != (domain.Cell{X: 12, Z: 12}) {
		t.Fatalf("only the floor inside the L is cleared, got %+v ok=%v", step, ok)
	}

	// The notch row and floor alone leave the L clear: the entry is dropped.
	done := RetiredGroundDone(plan, []ClearanceTarget{notch}, []ClearanceFloor{{Cell: domain.Cell{X: 13, Z: 10}}})
	if len(done) != 1 || !done[0].Equal(l) {
		t.Fatalf("the cleared L is done, got %v", done)
	}
	if next := plan.WithoutRetiredGround(done); len(next.RetiredGround) != 0 {
		t.Fatalf("the done L is dropped from the plan, got %v", next.RetiredGround)
	}
}

func TestNonRectReservationBlocksOtherSites(t *testing.T) {
	l := lShape()
	plan := LayoutPlan{Reservations: []LayoutReservation{{Kind: ReserveExhaust, Area: l}}}
	if plan.MineTier(domain.Cell{X: 12, Z: 13}) != MineTierCore {
		t.Fatal("a cell of the L reservation is reserved")
	}
	if plan.MineTier(domain.Cell{X: 13, Z: 10}) == MineTierCore {
		t.Fatal("the notch of the L is not reserved")
	}

	u := &utilityGrid{w: 20, h: 20, used: make([]bool, 400)}
	u.markRegion(l)
	if !u.used[13*20+13] || u.used[10*20+13] {
		t.Fatal("the utility grid marks the L's cells and leaves its notch free")
	}
}
