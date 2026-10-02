package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A field next to the base takes ring cells without protection and none
// once the ring is protected (#1550).
func TestFieldSitingAvoidsFirebreakRing(t *testing.T) {
	r := firebreakFixture(t, squarePoints(17, 17, 23, 23)...)
	r.Bounds = domain.Known(Bounds{Width: 40, Height: 40})
	ring, err := FirebreakRing(r)
	cells, known := ring.Value()
	if err != nil || !known || len(cells) == 0 {
		t.Fatal(known, err)
	}
	inRing := map[domain.Cell]bool{}
	for _, c := range cells {
		inRing[c] = true
	}
	overlap := func(plan FieldPlan) int {
		n := 0
		for _, patch := range plan.Sites.Patches {
			for _, c := range rectCells(patch) {
				if inRing[c] {
					n++
				}
			}
		}
		return n
	}
	// The base itself (the 17..23 walls) is taken; the ring is the
	// two cells around it.
	var base []domain.Cell
	for x := int32(17); x <= 23; x++ {
		for z := int32(17); z <= 23; z++ {
			base = append(base, domain.Cell{X: x, Z: z})
		}
	}
	field := fieldRequest(1.0)
	field.Site.Protected = base
	plan, ok := PlanField(field)
	if !ok || overlap(plan) == 0 {
		t.Fatal("unprotected field should overlap the ring", ok, plan.Explain())
	}
	field = fieldRequest(1.0)
	field.Site.Protected = append(base, cells...)
	plan, ok = PlanField(field)
	if !ok || overlap(plan) != 0 {
		t.Fatal("protected field took ring cells", ok, plan.Explain())
	}
}

func TestFirebreakRingUnknown(t *testing.T) {
	r := firebreakFixture(t, squarePoints(17, 17, 23, 23)...)
	r.GrowingZones = domain.Unknown[[]domain.Cell]()
	if ring, err := FirebreakRing(r); err != nil || func() bool { _, k := ring.Value(); return k }() {
		t.Fatal(ring, err)
	}
}
