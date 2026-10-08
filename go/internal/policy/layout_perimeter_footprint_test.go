package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// footprintBox is the bounding box of the plan's core footprint.
func footprintBox(plan LayoutPlan, w, h int32) Rectangle {
	var box Rectangle
	for i, v := range coreFootprint(plan, w, h) {
		if v {
			box = unionRect(box, Rectangle{X: int32(i) % w, Z: int32(i) / w, Width: 1, Height: 1})
		}
	}
	return box
}

func countCells(v []bool) int {
	n := 0
	for _, b := range v {
		if b {
			n++
		}
	}
	return n
}

func footprintRoom(role PlannedRole, x, z, w, h int32) PlannedRoom {
	return PlannedRoom{Role: role, Interior: Rectangle{X: x, Z: z, Width: w, Height: h}}
}

// A rectangular core encloses its bounding box and yard, as before.
func TestCoreFootprintRectangularCoreMatchesTheBox(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []PlannedRoom{footprintRoom(PlannedWorkshop, 60, 60, 10, 10), footprintRoom(PlannedStorage, 72, 60, 10, 10)}}
	fp := coreFootprint(plan, 150, 150)
	box := footprintBox(plan, 150, 150)
	if got, want := countCells(fp), int(box.Width*box.Height); got != want {
		t.Fatalf("footprint %d cells, box %d", got, want)
	}
	enc := coreEnclosure(plan, 150, 150)
	if enc.bbox != pad(box, perimeterGap) {
		t.Fatalf("enclosure %v, want the box grown by the yard %v", enc.bbox, pad(box, perimeterGap))
	}
}

// An L-shaped core encloses no more ground than its footprint and yard, and a
// wide notch stays a notch.
func TestCoreFootprintLShapeKeepsItsNotch(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []PlannedRoom{
		footprintRoom(PlannedWorkshop, 40, 40, 50, 8),
		footprintRoom(PlannedStorage, 40, 50, 8, 40),
	}}
	const n = 150
	fp := coreFootprint(plan, n, n)
	enc := coreEnclosure(plan, n, n)
	box := pad(footprintBox(plan, n, n), perimeterGap)
	if got := countCells(enc.in); got >= int(box.Width*box.Height) {
		t.Fatalf("enclosure holds %d cells of the %d-cell box", got, box.Width*box.Height)
	}
	// Nothing enclosed lies beyond the yard plus the closing from the footprint.
	for i, d := range chebyshevField(n, n, fp, perimeterGap+perimeterThick) {
		if enc.in[i] && d < 0 {
			t.Fatalf("cell %d is enclosed far from the footprint", i)
		}
	}
	if enc.inside(domain.Cell{X: 80, Z: 80}) {
		t.Fatal("the notch is enclosed")
	}
	if !enc.inside(domain.Cell{X: 44, Z: 85}) || !enc.inside(domain.Cell{X: 85, Z: 44}) {
		t.Fatal("an arm is outside the enclosure")
	}
}

// A narrow notch is closed, as the old box closed it.
func TestCoreFootprintNarrowNotchCloses(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []PlannedRoom{
		footprintRoom(PlannedWorkshop, 40, 40, 10, 10),
		footprintRoom(PlannedStorage, 54, 40, 10, 10),
	}}
	const n = 150
	enc := coreEnclosure(plan, n, n)
	if !enc.inside(domain.Cell{X: 52, Z: 45}) {
		t.Fatal("the gap between two rooms is not enclosed")
	}
}
