package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// The wall's yard grows with the herd the plan is sized for and never drops
// below perimeterGap.
func TestYardGrowsWithTheHerdAndKeepsItsFloor(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []PlannedRoom{footprintRoom(PlannedWorkshop, 60, 60, 10, 10), footprintRoom(PlannedStorage, 72, 60, 10, 10)}}
	const n = 200
	floor := countCells(coreEnclosure(plan, n, n).in)
	last := floor
	for _, animals := range []int{0, 6, 30, 60, 300} {
		sized := plan
		sized.YardCells = YardCells(animals)
		got := countCells(coreEnclosure(sized, n, n).in)
		if got < floor || got < last {
			t.Fatalf("%d animals enclose %d cells, floor %d, fewer animals %d", animals, got, floor, last)
		}
		last = got
	}
	if last <= floor {
		t.Fatalf("a herd of 300 enclosed %d cells, no more than the floor %d", last, floor)
	}
	big := plan
	big.YardCells = YardCells(300)
	if gap := yardGap(coreBaseFootprint(big, n, n), n, n, big.YardCells); gap <= perimeterGap || gap > perimeterGapMax {
		t.Fatalf("yard gap %d outside (%d, %d]", gap, perimeterGap, perimeterGapMax)
	}
}

// A derived plan for a larger herd walls a larger yard, still closes, and
// keeps its barn and vet room.
func TestDeriveSizesTheWallFromTheHerd(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	wallBox := func(animals int) (Rectangle, LayoutPlan) {
		p, ok := DeriveLayoutPlan(s, 3, TechTierCamp, nil, 30, animals).Value()
		if !ok {
			t.Fatal("no plan")
		}
		checkPerimeter(t, p)
		var box Rectangle
		for _, w := range reserved(p, ReservePerimeter) {
			box = unionRect(box, w)
		}
		return box, p
	}
	small, _ := wallBox(6)
	large, p := wallBox(500)
	if large.Width*large.Height <= small.Width*small.Height {
		t.Fatalf("wall for 500 animals %v is no larger than for 6 %v", large, small)
	}
	if len(reserved(p, ReserveBarn)) == 0 || len(reserved(p, ReserveVetRoom)) == 0 {
		t.Fatal("the larger plan lost its barn or vet room")
	}
}
